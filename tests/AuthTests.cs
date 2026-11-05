using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Procurement;
using Xunit;

// 從公開 HTTP 邊界驗證登入、安全限制與撤銷；時效案例使用可控時鐘，不等待真實時間。
public sealed class AuthTests
{
    // 確認匿名身分查詢回傳 JSON 401，讓 API 呼叫端能直接辨識登入失效。
    [Fact]
    public async Task Unauthenticated_identity_returns_json_401()
    {
        // 保留原始 HTTP 回應，避免自動轉址隱藏匿名狀態碼。
        await using var app = new Harness();
        using var client = app.Client();
        var response = await client.GetAsync("/api/auth/me");

        // API 客戶端應取得可辨識的 JSON 401，而不是 HTML 登入頁的轉址。
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    // 驗證登入 Cookie 的防護和資料庫身分更新，確保 Cookie 既可使用也不快取授權。
    [Fact]
    public async Task Login_cookie_and_current_database_identity()
    {
        // 建立可登入的帳號，從真實回應取得 Cookie。
        await using var app = new Harness();
        await app.Seed();
        using var client = app.Client();
        var response = await app.Login(client);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // 檢查瀏覽器端的讀取、跨站與保存限制，並確認 Cookie 對應正確帳號。
        var cookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("procurement.session="));
        Assert.True(cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase), "Cookie 必須為 HttpOnly。");
        Assert.True(
            cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase),
            "Cookie 必須為 SameSite Strict。");
        Assert.False(cookie.Contains("expires=", StringComparison.OrdinalIgnoreCase), "不得建立長期記住登入 Cookie。");
        var identity = await Harness.Me(client);
        Assert.Equal(app.UserName, identity.UserName);
        Assert.Equal("business", identity.AccountType);
        Assert.False(identity.MustChangePassword);

        // 沿用同一個 Cookie 修改資料庫角色，確認授權資訊沒有被 Cookie 快取。
        await app.WithDb(async db =>
        {
            // 若測試資料庫已有主管角色就重用，避免固定角色名稱互相衝突。
            var role = new Microsoft.AspNetCore.Identity.IdentityRole("Manager")
            {
                NormalizedName = "MANAGER"
            };
            var existing = await db.Roles.SingleOrDefaultAsync(r => r.NormalizedName == "MANAGER");
            if (existing is null)
                db.Roles.Add(role);
            else
                role = existing;

            // 直接新增測試帳號的角色對應，讓原 Cookie 不必更新即可驗證讀取結果。
            db.UserRoles.Add(new() { UserId = app.UserId, RoleId = role.Id });
            await db.SaveChangesAsync();
        });
        // 在原登入下讀取新增的角色，再移除並確認下一次查詢立即反映。
        Assert.Equal(["Manager"], (await Harness.Me(client)).Roles);
        await app.WithDb(db => db
            .UserRoles
            .Where(r => r.UserId == app.UserId)
            .ExecuteDeleteAsync());
        Assert.Empty((await Harness.Me(client)).Roles);

        // 停用立即生效，不需要等待 Cookie 或登入期限到期。
        await app.WithDb(db => db
            .Users
            .Where(u => u.Id == app.UserId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Active, false)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    // 對照改密碼和登入的上下限，避免同一個密碼被一個入口接受、另一個入口拒絕。
    [Theory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task Password_length_applies_to_change_and_login(int length, bool valid)
    {
        // 使用已登入的帳號取得版本，讓密碼長度成為主要變因。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();
        var before = await Harness.Me(client);
        var password = new string('x', length);

        // 同一組長度邊界同時驗證改密碼與登入，避免兩個入口採不同規則。
        var response = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new PasswordRequest(app.Password, password, before.Version));
        Assert.Equal(valid ? HttpStatusCode.NoContent : HttpStatusCode.BadRequest, response.StatusCode);
        // 以另一個 client 嘗試新密碼，獨立確認登入入口的長度驗證。
        using var login = app.Client();
        var attempt = await app.Login(login, password);
        Assert.Equal(valid ? HttpStatusCode.NoContent : HttpStatusCode.BadRequest, attempt.StatusCode);
        if (!valid)
        {
            // 不合法的新／舊密碼都不得影響原憑證。
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await client.PostAsJsonAsync(
                    "/api/auth/change-password",
                    new PasswordRequest(password, app.Password, before.Version))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await app.Login(login)).StatusCode);
        }
        else
        {
            // 合法邊界完成真改密碼後，透過新登入再改回原憑證。
            await Harness.Token(login);
            var changed = await Harness.Me(login);
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await login.PostAsJsonAsync(
                    "/api/auth/change-password",
                    new PasswordRequest(password, app.Password, changed.Version))).StatusCode);
        }
    }

    // 透過真實 POST 驗證 CSRF 拒絕的效果，確保無效權杖不能建立或改變登入。
    [Fact]
    public async Task Csrf_rejections_do_not_login_change_password_or_logout()
    {
        // 先準備可登入的帳號和匿名 client，排除帳密錯誤造成的拒絕。
        await using var app = new Harness();
        await app.Seed();
        using var client = app.Client();

        // 匿名登入先驗證缺少與偽造 token 均不能建立登入。
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginRequest(app.UserName, app.Password))).StatusCode);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginRequest(app.UserName, app.Password))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        // 使用合法權杖登入後，才能單獨驗證受保護異動的防偽拒絕。
        Assert.Equal(HttpStatusCode.NoContent, (await app.Login(client)).StatusCode);
        var me = await Harness.Me(client);

        // 有效登入下重試兩種無效 token，改密碼與登出都不能寫入。
        foreach (var token in new[] { "", "invalid" })
        {
            // 先清空標頭，視本輪案例決定是否帶偽造值。
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            if (token.Length > 0)
                client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);

            // 兩種異動都須回 400，且當前登入仍可讀取原密碼版本。
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await client.PostAsJsonAsync(
                    "/api/auth/change-password",
                    new PasswordRequest(app.Password, "replacement-password", me.Version))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
            Assert.Equal(me.Version, (await Harness.Me(client)).Version);
        }

        // 以新 client 重新登入，確認舊密碼仍有效。
        using var other = app.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await app.Login(other)).StatusCode);
    }

    // 驗證成功登入重置失敗次數，並用可控時鐘精準確認十五分鐘的解除界線。
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Lockout_boundary_and_success_clears_failures(int seconds)
    {
        // 每個邊界案例使用新帳號，避免共用鎖定計數或期限。
        await using var app = new Harness();
        await app.Seed();
        using var client = app.Client();

        // 先以成功登入清除四次失敗，再累積五次，排除舊計數造成提早鎖定。
        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.Login(client, "wrong-password-123")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Login(client)).StatusCode);

        // 重置後仍允許四次一般失敗，第五次才進入鎖定。
        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.Login(client, "wrong-password-123")).StatusCode);
        Assert.Equal((HttpStatusCode)423, (await app.Login(client, "wrong-password-123")).StatusCode);

        // -1／0／1 秒涵蓋期限前、等於及超過；等於期限時必須已能登入。
        app.Clock.Advance(TimeSpan.FromMinutes(15).Add(TimeSpan.FromSeconds(seconds)));
        Assert.Equal(
            seconds < 0 ? (HttpStatusCode)423 : HttpStatusCode.NoContent,
            (await app.Login(client)).StatusCode);
        if (seconds < 0)
        {
            // 前一秒被拒絕的嘗試不能延長鎖定，推到原期限即能登入。
            app.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(HttpStatusCode.NoContent, (await app.Login(client)).StatusCode);
        }
    }

    // 驗證不存在的帳號仍共用來源額度，並用測試時鐘確認一分鐘到期即重置。
    [Fact]
    public async Task Source_limit_includes_unknown_accounts_and_resets_at_one_minute()
    {
        // 維持同一來源且帶合法防偽權杖，讓每次嘗試確實進入節流判斷。
        await using var app = new Harness();
        using var client = app.Client();
        await Harness.Token(client);

        // 不存在的帳號也消耗同來源額度，避免可藉帳號不存在繞過節流。
        for (var i = 0; i < 30; i++)
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync(
                    "/api/auth/login",
                    new LoginRequest("unknown", "unknown-password"))).StatusCode);
        // 額度耗盡時，回應應為節流錯誤而非帳密錯誤。
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginRequest("unknown", "unknown-password"))).StatusCode);
        // 推到視窗邊界後再送一次，確認嘗試重新進入帳密驗證。
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginRequest("unknown", "unknown-password"))).StatusCode);
    }

    // 在三十分鐘前後與整點驗證閒置到期，避免以實際等待造成不穩定結果。
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Idle_expiry_boundary(int seconds)
    {
        // 建立新登入後直接推進時鐘，期間不送會更新閒置時間的請求。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();

        // 閒置期限採嚴格小於；等於三十分鐘即失效。
        app.Clock.Advance(TimeSpan.FromMinutes(30).Add(TimeSpan.FromSeconds(seconds)));
        Assert.Equal(
            seconds < 0 ? HttpStatusCode.OK : HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    // 持續使用同一次登入以驗證絕對期限，避免誤把閒置到期當成八小時上限。
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Absolute_expiry_is_not_extended_by_activity(int seconds)
    {
        // 從新的有效登入開始，讓八小時上限有固定的起算點。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();

        // 持續活動避免先撞到閒置期限，單獨驗證八小時上限無法延長。
        for (var i = 0; i < 23; i++)
        {
            app.Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        }

        // 最後一次推到八小時前後與整點，確認活動不延長絕對期限。
        app.Clock.Advance(TimeSpan.FromMinutes(20).Add(TimeSpan.FromSeconds(seconds)));
        Assert.Equal(
            seconds < 0 ? HttpStatusCode.OK : HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    // 比較登出與改密碼的撤銷範圍，直接重播舊 Cookie 以驗證伺服器端結果。
    [Fact]
    public async Task Logout_revokes_only_that_cookie_and_password_change_revokes_all()
    {
        // 為同一帳號建立兩次登入並保存原始 Cookie，才能在清除後手動重播。
        await using var app = new Harness();
        await app.Seed();
        using var first = app.Client();
        var login = await app.Login(first);
        var cookie = Assert.Single(
            login.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("procurement.session=")).Split(';')[0];
        using var second = app.Client();
        var secondLogin = await app.Login(second);
        var secondCookie = Assert.Single(
            secondLogin.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("procurement.session=")).Split(';')[0];
        await Harness.Token(second);
        await Harness.Token(first);

        // 登出後手動重播原 Cookie，確認伺服器已撤銷，且第二個登入仍可使用。
        Assert.Equal(HttpStatusCode.NoContent, (await first.PostAsync("/api/auth/logout", null)).StatusCode);
        using var replay = app.Client(false);
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/auth/me")).StatusCode);

        // 補一份仍有效的登入，讓改密碼時可以同時驗證本人與其他登入都被撤銷。
        using var third = app.Client();
        var thirdLogin = await app.Login(third);
        var thirdCookie = Assert.Single(
            thirdLogin.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("procurement.session=")).Split(';')[0];
        var me = await Harness.Me(second);

        // 從第二次登入變更密碼，讓同一操作同時觸發密碼更新和登入撤銷。
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await second.PostAsJsonAsync(
                "/api/auth/change-password",
                new PasswordRequest(app.Password, "new-password-123", me.Version))).StatusCode);

        // 逐一重播舊 Cookie，確認伺服器拒絕兩筆登入，而非只在原 client 清除。
        replay.DefaultRequestHeaders.Remove("Cookie");
        replay.DefaultRequestHeaders.Add("Cookie", thirdCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/auth/me")).StatusCode);
        replay.DefaultRequestHeaders.Remove("Cookie");
        replay.DefaultRequestHeaders.Add("Cookie", secondCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/auth/me")).StatusCode);

        // 以另一個 client 驗證舊密碼失效且新密碼能登入。
        using var fresh = app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Login(fresh)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Login(fresh, "new-password-123")).StatusCode);
    }

    // 驗證無效 CSRF 的 POST 不延長登入，透過原期限當下的請求檢查實際結果。
    [Fact]
    public async Task Invalid_csrf_does_not_extend_idle_session()
    {
        // 將有效登入推到到期前一分鐘，保留可觀察活動更新與否的時間差。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();
        app.Clock.Advance(TimeSpan.FromMinutes(29));

        // 移除權杖並送出登出，確認請求在防偽檢查被拒絕。
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/auth/logout", null)).StatusCode);

        // 登入在原期限當下應已逾期，身分查詢回傳 401 表示被拒絕的請求沒有延長閒置期限。
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    // 驗證大小寫與尾斜線別名沿用同一登入額度，避免換 URL 繞過節流。
    [Fact]
    public async Task Framework_route_aliases_share_the_login_source_limit()
    {
        // 先使用正式路徑成功登入，消耗同來源視窗中的第一個額度。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();

        // 從別名耗盡剩餘額度，確認別名呼叫實際進入同一條登入防護路徑。
        for (var i = 0; i < 29; i++)
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync(
                    "/API/AUTH/LOGIN/",
                    new LoginRequest("unknown", "unknown-password"))).StatusCode);
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await client.PostAsJsonAsync(
                "/API/AUTH/LOGIN/",
                new LoginRequest("unknown", "unknown-password"))).StatusCode);
    }
}
