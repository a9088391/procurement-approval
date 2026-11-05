using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Procurement;
using Xunit;
using static OrganizationTests;
using static RequisitionDraftTests;

// 以實際 PostgreSQL 的交易先後和故障驗證草稿不會被過期或失效的請求改寫。
public sealed class RequisitionDraftTransactionTests
{
    // 兩次登入同時以相同版本保存，先進入草稿交易者成功，落後者必須讀到新版本。
    [Fact]
    public async Task Overlapping_saves_accept_only_the_first_committed_version()
    {
        // 同一帳號從兩個測試主機登入，讓兩筆請求各自有 Cookie 驗證後的屏障。
        await using var app = new Harness();
        await app.Seed();
        await using var otherApp = new Harness();
        using var first = await app.SignedIn();
        using var second = await otherApp.SignedIn(app.UserName, app.Password);
        var draft = await CreateDraft(first, new { subject = "初始主旨" });
        var path = PathOf(draft);

        // 先準備獨立連線，再暫停兩筆同版本請求。兩筆的 Cookie 交易都提交後才能鎖定該帳號。
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Database");
        await using var blocker = new NpgsqlConnection(connection);
        await blocker.OpenAsync();
        await using var observer = new NpgsqlConnection(connection);
        await observer.OpenAsync();
        Task<HttpResponseMessage> a;
        Task<HttpResponseMessage> b;
        await using (var tx = await blocker.BeginTransactionAsync())
        {
            app.Gate.Arm();
            otherApp.Gate.Arm();
            a = first.PostAsJsonAsync(path, Edit(draft, "第一個請求"));
            b = second.PostAsJsonAsync(path, Edit(draft, "第二個請求"));
            try
            {
                // 此刻兩筆都已通過 Cookie 驗證，稍後觀察到的等鎖必定發生於草稿寫入。
                await Task
                    .WhenAll(app.Gate.Arrived.Task, otherApp.Gate.Arrived.Task)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                await using var hold = new NpgsqlCommand(
                    "SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = @id FOR UPDATE", blocker, tx);
                hold.Parameters.AddWithValue("id", app.UserId);
                await hold.ExecuteScalarAsync();
            }
            finally
            {
                // 放行到服務交易；即使測試的鎖定失敗也不能留下暫停請求。
                app.Gate.Release.TrySetResult();
                otherApp.Gate.Release.TrySetResult();
            }

            // 確認兩筆草稿交易在真實資料庫同時等待。失敗時釋放交易也會解除列鎖。
            await WaitForDraftLockWaiters(observer);
            await tx.CommitAsync();
        }

        // 不指定哪筆先取得列鎖，兩筆合計必須只有一個成功及一個明確的版本衝突。
        var responses = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(10));
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];
        var committed = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        await Expect(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict),
            HttpStatusCode.Conflict, "stale_version");
        var winner = await committed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(committed == firstResponse ? "第一個請求" : "第二個請求", winner.GetProperty("subject").GetString());
        Assert.NotEqual(VersionOf(draft), VersionOf(winner));

        // 用新的登入重新讀取，驗證只保留勝出者的完整版本。
        using var fresh = await app.SignedIn();
        Assert.True(JsonElement.DeepEquals(winner, await ReadDraft(fresh, draft)));
    }

    // 只在隔離資料庫觀察兩筆帳號鎖等待，確認不是由測試屏障或 HTTP 排程模擬重疊。
    private static async Task WaitForDraftLockWaiters(NpgsqlConnection observer)
    {
        // 兩個 Cookie 交易皆已完成，剩下的同類 FOR UPDATE 查詢來自本案例的草稿交易。
        await using var query = new NpgsqlCommand("""
            SELECT count(*) FROM pg_stat_activity
            WHERE datname = current_database() AND wait_event_type = 'Lock'
              AND query LIKE '%"AspNetUsers"%FOR UPDATE%'
            """, observer);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            // 計數到二才可放行，未到達時短暫等候下一個資料庫活動快照。
            if (Convert.ToInt64(await query.ExecuteScalarAsync()) == 2)
                return;
            await Task.Delay(10);
        }

        // 逾時保留診斷，呼叫端會在離開交易範圍時釋放帳號鎖。
        Assert.Fail("兩個草稿交易都必須實際等待 PostgreSQL 帳號列鎖。");
    }

    // 管理停用已完成時，通過中介層的舊登入也必須在草稿交易內失效。
    [Fact]
    public async Task Committed_account_disable_rejects_a_write_that_already_passed_cookie_validation()
    {
        // 透過管理 API 建立業務帳號並完成改密碼，讓待測的兩種身分均使用正常登入。
        await using var adminApp = new Harness();
        await adminApp.Seed("admin", "Administrator");
        using var admin = await adminApp.SignedIn();
        var department = await CreateDepartment(admin);
        var temporary = TemporaryPassword();
        var password = TemporaryPassword();
        var account = await CreateAccount(admin, department.Id, temporary);
        await using var ownerApp = new Harness();
        using var setup = await ownerApp.SignedIn(account.UserName, temporary);
        await Expect(await setup.PostAsJsonAsync("/api/auth/change-password",
            new PasswordRequest(temporary, password, (await Harness.Me(setup)).Version)), HttpStatusCode.NoContent);
        using var owner = await ownerApp.SignedIn(account.UserName, password);
        var draft = await CreateDraft(owner, new { subject = "停用前", lines = new[] { new { quantity = 3, unitPrice = 0.29m } } });

        // 只暫停本人請求，讓管理端在不同主機以正式 HTTP 先鎖住目標帳號並提交停用。
        var target = (await Read(admin)).Accounts.Single(a => a.Id == account.Id);
        var accountPath = $"{AccountsPath}/{target.Id}";
        ownerApp.Gate.Arm();
        var paused = owner.PostAsJsonAsync(PathOf(draft), Edit(draft, "已停用仍寫入"));
        try
        {
            await ownerApp
                .Gate
                .Arrived
                .Task
                .WaitAsync(TimeSpan.FromSeconds(10));
            await Expect(await admin.PostAsJsonAsync(accountPath, target.Edit() with { Active = false }), HttpStatusCode.NoContent);
        }
        finally
        {
            // 釋放暫停請求，使服務在自己的交易內重查帳號及登入狀態。
            ownerApp.Gate.Release.TrySetResult();
        }
        await Expect(await paused, HttpStatusCode.Unauthorized, "unauthenticated");

        // 恢復帳號不會恢復舊 Cookie，只有新登入可重開，並取得停用前同一版資料。
        var disabled = (await Read(admin)).Accounts.Single(a => a.Id == target.Id);
        Assert.False(disabled.Active);
        await Expect(await admin.PostAsJsonAsync(accountPath, disabled.Edit() with { Active = true }), HttpStatusCode.NoContent);
        await Expect(await owner.GetAsync(PathOf(draft)), HttpStatusCode.Unauthorized, "unauthenticated");
        using var fresh = await ownerApp.SignedIn(account.UserName, password);
        Assert.True(JsonElement.DeepEquals(draft, await ReadDraft(fresh, draft)));
    }

    // 在已移除舊明細後讓第二列插入失敗，確認主檔及明細全部回到前一版本。
    [Fact]
    public async Task Failed_detail_insert_restores_the_entire_draft_and_allows_a_later_save()
    {
        // 建立兩筆舊明細，並推進時間讓部分更新主檔時也可從讀回結果辨識。
        await using var app = new Harness();
        await app.Seed();
        using var owner = await app.SignedIn();
        var draft = await CreateDraft(owner, new
        {
            subject = "原有主旨",
            purpose = "原有用途",
            lines = new[]
            {
                new { itemName = "標籤", unit = "張", quantity = 3, unitPrice = 0.29m },
                new { itemName = "影印紙", unit = "包", quantity = 2, unitPrice = 0.31m }
            }
        });
        app.Clock.Advance(TimeSpan.FromSeconds(1));

        // 隔離庫的觸發器只拒絕本案標記的第二列，正式程式沒有新增故障入口。
        await app.WithDb(db => db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION test_requisition_draft_detail_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'isolated requisition draft detail failure';
            END $$;
            CREATE TRIGGER test_requisition_draft_detail_failure BEFORE INSERT ON "RequisitionLines"
                FOR EACH ROW WHEN (NEW."Position" = 1 AND NEW."ItemName" = '隔離草稿明細故障注入')
                EXECUTE FUNCTION test_requisition_draft_detail_failure();
            """));
        try
        {
            // 替換輸入本身合法，只因真實資料庫故障失敗，再由 HTTP 讀回整份舊內容。
            var failed = Replacement(draft, "隔離草稿明細故障注入");
            await Expect(await owner.PostAsJsonAsync(PathOf(draft), failed), HttpStatusCode.InternalServerError, "server_error");
            Assert.True(JsonElement.DeepEquals(draft, await ReadDraft(owner, draft)));
        }
        finally
        {
            // 總是移除本案 DDL，避免共用隔離庫的其他測試遇到殘留故障。
            await app.WithDb(db => db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER test_requisition_draft_detail_failure ON "RequisitionLines";
                DROP FUNCTION test_requisition_draft_detail_failure();
                """));
        }

        // 同一原版本接著可保存，並查回兩筆新明細、正確金額、時間及新版本。
        await Expect(await owner.PostAsJsonAsync(PathOf(draft), Replacement(draft, "第二筆新明細")), HttpStatusCode.OK);
        var recovered = await ReadDraft(owner, draft);
        Assert.Equal("新主旨", recovered.GetProperty("subject").GetString());
        Assert.Equal("新用途", recovered.GetProperty("purpose").GetString());
        Assert.Equal(new[] { "第一筆新明細", "第二筆新明細" }, recovered
            .GetProperty("lines")
            .EnumerateArray()
            .Select(line => line.GetProperty("itemName").GetString()));
        Assert.Equal(5.24m, recovered.GetProperty("estimatedAmount").GetDecimal());
        Assert.Equal(app.Clock.GetUtcNow(), recovered.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.NotEqual(VersionOf(draft), VersionOf(recovered));
    }

    // 用相同合法內容隔離資料庫故障，僅更換第二列品名來觸發或避開測試規則。
    private static object Replacement(JsonElement draft, string secondItemName) => new
    {
        subject = "新主旨",
        purpose = "新用途",
        lines = new[]
        {
            new { itemName = "第一筆新明細", unit = "件", quantity = 2, unitPrice = 1.23m },
            new { itemName = secondItemName, unit = "件", quantity = 1, unitPrice = 2.78m }
        },
        version = VersionOf(draft)
    };
}
