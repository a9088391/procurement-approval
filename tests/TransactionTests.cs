using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Procurement;
using Xunit;

// 在真實 PostgreSQL 上安排重疊、撤銷與失敗，並以後續 HTTP 操作確認最終狀態。
public sealed class TransactionTests
{
    // 驗證已撤銷登入回傳 401，避免身分綁定的舊權杖先被誤判成 CSRF 失敗。
    [Theory]
    [InlineData("/api/auth/change-password")]
    [InlineData("/api/auth/logout")]
    public async Task Revoked_session_with_old_csrf_returns_unauthenticated(string path)
    {
        // 同時建立兩份登入，由其中一份改密碼，讓另一份保留舊 Cookie 與權杖。
        await using var app = new Harness();
        await app.Seed();
        using var winner = await app.SignedIn();
        using var revoked = await app.SignedIn();
        var me = await Harness.Me(winner);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await winner.PostAsJsonAsync(
                "/api/auth/change-password",
                new PasswordRequest(app.Password, "winner-password-123", me.Version))).StatusCode);

        // 撤銷完成後保留原 Cookie 與身分綁定 token，確定走 Cookie 驗證先拒絕的時序。
        var response = path == "/api/auth/logout"
            ? await revoked.PostAsync(path, null)
            : await revoked.PostAsJsonAsync(
                path,
                new PasswordRequest(app.Password, "loser-password-123", me.Version));

        // 同時檢查狀態碼與錯誤代碼，確定呼叫端能正確辨識撤銷。
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized,
            $"預期 401 unauthenticated，實際 {(int)response.StatusCode} {error?.Code}。");
        Assert.Equal("unauthenticated", error?.Code);

        // 從新 client 驗證只剩先前成功更新的密碼可登入。
        using var fresh = app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Login(fresh)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Login(fresh, "winner-password-123")).StatusCode);
    }

    // 以錯誤密碼與舊版本各送一次請求，確認保護條件各自生效且原憑證仍可使用。
    [Fact]
    public async Task Stale_version_and_wrong_password_do_not_change_credentials()
    {
        // 建立有效登入並讀取版本，讓接下來可以獨立控制密碼與版本兩個變因。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();
        var me = await Harness.Me(client);

        // 在版本仍有效時送錯誤密碼，單獨驗證目前密碼檢查。
        var wrong = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new PasswordRequest("wrong-password-123", "new-password-123", me.Version));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(
            "wrong_password",
            (await wrong.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);

        // 從另一個 scope 變更版本，再帶正確密碼與舊版本請求，單獨驗證衝突。
        await app.WithDb(db => db
            .Users
            .Where(u => u.Id == app.UserId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.ConcurrencyStamp, "changed")));
        var stale = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new PasswordRequest(app.Password, "new-password-123", me.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            "stale_version",
            (await stale.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);

        // 用原憑證走一次真實登入，確認兩個拒絕分支都沒有更新密碼。
        using var fresh = app.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await app.Login(fresh)).StatusCode);
    }

    // 在 Cookie 驗證後插入撤銷，驗證異動交易會重查登入，不採用早先的授權結果。
    [Fact]
    public async Task Revocation_after_request_authentication_prevents_password_write()
    {
        // 先完成登入與身分查詢，讓屏障只攔住改密碼請求中的驗證交易。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();
        var me = await Harness.Me(client);

        // 屏障停在 Cookie 驗證交易提交後；撤銷先完成，再放行端點的第二次檢查。
        app.Gate.Arm();
        var change = client.PostAsJsonAsync(
            "/api/auth/change-password",
            new PasswordRequest(app.Password, "new-password-123", me.Version));
        await app
            .Gate
            .Arrived
            .Task
            .WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // 用獨立交易模擬撤銷，確保它先於受阻的改密碼端點完成。
            await app.WithDb(async db =>
            {
                // 使用與正式異動相同的帳號列鎖，避免測到不可能發生的時序。
                await using var tx = await db.Database.BeginTransactionAsync();
                await db
                    .Users
                    .FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {app.UserId} FOR UPDATE")
                    .SingleAsync();

                // 撤銷帳號的登入紀錄並提交，讓原請求恢復後看見新狀態。
                await db
                    .Sessions
                    .Where(s => s.UserId == app.UserId)
                    .ExecuteDeleteAsync();
                await tx.CommitAsync();
            });
        }
        finally
        {
            // 即使 fixture 失敗也放行請求，避免將測試故障誤判成伺服器永久等待。
            app
                .Gate
                .Release
                .TrySetResult();
        }

        // 放行後必須被拒絕，並以原密碼登入確認沒有寫入新密碼。
        Assert.Equal(HttpStatusCode.Unauthorized, (await change).StatusCode);

        using var fresh = app.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await app.Login(fresh)).StatusCode);
    }

    // 在 Cookie 驗證後停用帳號，確認端點會讀取列鎖內的新狀態，避免使用舊追蹤實體。
    [Fact]
    public async Task Deactivation_after_authentication_prevents_password_write()
    {
        // 建立可改密碼的登入，排除 CSRF 和版本錯誤干擾停用情境。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();
        var me = await Harness.Me(client);

        // 讓 middleware 已看過啟用帳號，再停用，驗證端點不沿用 EF 追蹤的舊實體。
        app.Gate.Arm();
        var change = client.PostAsJsonAsync(
            "/api/auth/change-password",
            new PasswordRequest(app.Password, "new-password-123", me.Version));
        await app
            .Gate
            .Arrived
            .Task
            .WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // 從獨立 scope 寫入停用，避免直接更動原請求已追蹤的帳號物件。
            await app.WithDb(db => db
                .Users
                .Where(u => u.Id == app.UserId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Active, false)));
        }
        finally
        {
            // 資料庫異動即使失敗也要放行，讓測試能結束受阻的請求。
            app
                .Gate
                .Release
                .TrySetResult();
        }

        // 端點在重新取得列鎖時應讀到停用，拒絕原本合法的請求。
        Assert.Equal(HttpStatusCode.Unauthorized, (await change).StatusCode);

        // 重新啟用後原密碼仍可登入，證明被拒絕的請求沒有改寫憑證。
        await app.WithDb(db => db
            .Users
            .Where(u => u.Id == app.UserId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Active, true)));
        using var fresh = app.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await app.Login(fresh)).StatusCode);
    }

    // 驗證兩筆改密碼只有一筆生效。在隔離 PostgreSQL 觀察至少兩筆含 AspNetUsers 的等鎖查詢，據此推定兩筆請求重疊。
    [Fact]
    public async Task Overlapping_password_changes_have_exactly_one_winner()
    {
        // 建立兩個有效登入，讓兩筆操作競爭同一帳號及讀取版本。
        await using var app = new Harness();
        await app.Seed();
        using var first = await app.SignedIn();
        using var second = await app.SignedIn();
        var me = await Harness.Me(first);

        // 由測試連線先持有帳號列鎖，安排兩筆 HTTP 請求等鎖的時序。
        await using var blocker = new NpgsqlConnection(
            Environment.GetEnvironmentVariable("ConnectionStrings__Database"));
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand(
            "SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = @id FOR UPDATE",
            blocker,
            tx))
        {
            // 固定只鎖定本案例的帳號，避免其他資料列影響等待結果。
            hold.Parameters.AddWithValue("id", app.UserId);
            await hold.ExecuteScalarAsync();
        }

        // 在釋放帳號鎖之前送出兩筆 HTTP 請求，提供兩筆等待重疊的機會。
        var a = first.PostAsJsonAsync(
            "/api/auth/change-password",
            new PasswordRequest(app.Password, "first-password-123", me.Version));
        var b = second.PostAsJsonAsync(
            "/api/auth/change-password",
            new PasswordRequest(app.Password, "second-password-123", me.Version));

        // 使用另一連線觀察等待狀態。依賴測試庫隔離，將至少兩筆相符活動視為本案請求重疊的跡象。
        await using var observer = new NpgsqlConnection(
            Environment.GetEnvironmentVariable("ConnectionStrings__Database"));
        await observer.OpenAsync();
        var elapsed = Stopwatch.StartNew();
        var waiting = 0L;
        while (waiting < 2 && elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            // 輪詢等待類型為 Lock 且 SQL 含 AspNetUsers 的活動，直到計數至少為二或超時。
            await using var query = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%AspNetUsers%'",
                observer);
            waiting = Convert.ToInt64(await query.ExecuteScalarAsync());

            // 觀察數尚未達門檻時短暫等待，避免連續查詢資料庫狀態。
            if (waiting < 2)
                await Task.Delay(10);
        }
        Assert.True(waiting >= 2, "兩個請求都必須實際等待 PostgreSQL 帳號列鎖。");

        // 放行後由交易順序決定勝者；另一請求必須觀察到登入已撤銷。
        await tx.CommitAsync();
        var responses = await Task.WhenAll(a, b);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Unauthorized);

        // 新登入讀回最終效果，不能只憑兩個 HTTP 狀態碼推定實際保存的密碼。
        using var fresh = app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Login(fresh)).StatusCode);
        var winner = responses[0].StatusCode == HttpStatusCode.NoContent ? "first-password-123" : "second-password-123";
        Assert.Equal(HttpStatusCode.NoContent, (await app.Login(fresh, winner)).StatusCode);
    }

    // 模擬登入刪除後的密碼更新失敗，透過 HTTP 查驗交易是否保留原版本、密碼與登入。
    [Fact]
    public async Task Database_failure_rolls_back_password_and_deleted_sessions()
    {
        // 建立兩個登入並保存原版本，便於在故障後比對撤銷與寫入結果。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();
        using var other = await app.SignedIn();
        var me = await Harness.Me(client);

        // 隔離測試庫不平行執行；故障觸發於刪除登入後的密碼更新，驗證交易回滾。
        await app.WithDb(db => db.Database.ExecuteSqlRawAsync(
            "CREATE OR REPLACE FUNCTION test_password_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.\"PasswordHash\" IS DISTINCT FROM OLD.\"PasswordHash\" THEN RAISE EXCEPTION 'isolated test failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER test_password_failure BEFORE UPDATE ON \"AspNetUsers\" FOR EACH ROW EXECUTE FUNCTION test_password_failure();"));
        try
        {
            // 觸發密碼更新，確認資料庫故障透過 HTTP 回報失敗。
            Assert.Equal(
                HttpStatusCode.InternalServerError,
                (await client.PostAsJsonAsync(
                    "/api/auth/change-password",
                    new PasswordRequest(app.Password, "new-password-123", me.Version))).StatusCode);

            // 讀回原版本與兩個既有登入，再用原密碼建立新登入以驗證回滾。
            Assert.Equal(me.Version, (await Harness.Me(client)).Version);
            Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/auth/me")).StatusCode);
            using var fresh = app.Client();
            Assert.Equal(HttpStatusCode.NoContent, (await app.Login(fresh)).StatusCode);
        }
        finally
        {
            // trigger 影響整張帳號表；無論斷言結果如何，都必須移除本案例的故障注入。
            await app.WithDb(db => db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER test_password_failure ON \"AspNetUsers\"; DROP FUNCTION test_password_failure();"));
        }
    }
}
