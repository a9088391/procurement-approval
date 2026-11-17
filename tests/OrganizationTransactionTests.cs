using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Procurement;
using Xunit;
using static OrganizationTests;

// 使用真實 PostgreSQL 鎖及故障注入驗證管理設定的併發、即時撤權及回滾。
public sealed class OrganizationTransactionTests
{
    // 兩位管理員可各自通過登入驗證並實際等鎖，同版本只能有一份更新成功。
    [Fact]
    public async Task Overlapping_account_updates_commit_one_version_and_reject_the_other()
    {
        // 不同管理帳號避免先在同一個操作者的帳號列鎖排隊。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        await using var other = new Harness();
        await other.Seed("admin", "Administrator");
        using var first = await app.SignedIn();
        using var second = await other.SignedIn();
        var department = await CreateDepartment(first);
        var account = await CreateAccount(first, department.Id, TemporaryPassword());
        var path = $"{AccountsPath}/{account.Id}";

        // 隔離連線先持有與正式服務相同的交易鎖，再送出兩筆 HTTP 設定。
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Database");
        await using var blocker = new NpgsqlConnection(connection);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(80101)", blocker, tx);
        await hold.ExecuteNonQueryAsync();
        var a = first.PostAsJsonAsync(path, account.Edit() with { IsManager = true });
        var b = second.PostAsJsonAsync(path, account.Edit() with { IsFinance = true });

        // 從 PostgreSQL 確認兩筆請求同時等待管理交易鎖，無論結果都釋放屏障。
        try
        {
            await WaitForOrganizationLockWaiters(connection);
        }
        finally
        {
            await tx.CommitAsync();
        }

        // 只允許一筆成功；另一筆辨識為過期版本，重查只能看到勝出者的完整資料。
        var responses = await Task.WhenAll(a, b);
        var firstWon = responses[0].StatusCode == HttpStatusCode.NoContent;
        await Expect(Assert.Single(responses, r => r.StatusCode == HttpStatusCode.NoContent), HttpStatusCode.NoContent);
        await Expect(Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, "stale_version");
        var saved = (await Read(first)).Accounts.Single(u => u.Id == account.Id);
        Assert.NotEqual(account.Version, saved.Version);
        Assert.Equal(firstWon, saved.IsManager);
        Assert.Equal(!firstWon, saved.IsFinance);
        Assert.Equal(account.DepartmentId, saved.DepartmentId);
    }

    // 批次停用與單筆編輯競爭同一版本，兩種提交順序都須保留勝出者的完整結果。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Overlapping_batch_deactivation_and_account_edit_commit_only_the_winner(bool batchFirst)
    {
        // 兩位管理員各自登入，避免在中介層等待同一操作者列鎖。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        await using var other = new Harness();
        await other.Seed("admin", "Administrator");
        using var batchAdmin = await app.SignedIn();
        using var editAdmin = await other.SignedIn();
        var department = await CreateDepartment(batchAdmin);
        var password = TemporaryPassword();
        var shared = await CreateAccount(batchAdmin, department.Id, password);
        var batchOnly = await CreateAccount(batchAdmin, department.Id, password);
        using var sharedSession = await app.SignedIn(shared.UserName, password);
        using var batchOnlySession = await app.SignedIn(batchOnly.UserName, password);

        // 批次包含共用目標和另一筆帳號，以驗證失敗時未留下部分停用。
        Task<HttpResponseMessage> Batch() => batchAdmin.PostAsJsonAsync($"{AccountsPath}/deactivate",
            new { items = new[] { new { shared.Id, shared.Version }, new { batchOnly.Id, batchOnly.Version } } });

        // 單筆編輯沿用同一個原始版本，僅新增角色供讀回區分勝出者。
        Task<HttpResponseMessage> Edit() => editAdmin.PostAsJsonAsync($"{AccountsPath}/{shared.Id}",
            shared.Edit() with { IsManager = true });

        // 由 PostgreSQL 觀察第一筆已排隊，再送第二筆，確認兩筆實際重疊後才放行。
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Database");
        await using var blocker = new NpgsqlConnection(connection);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(80101)", blocker, tx);
        await hold.ExecuteNonQueryAsync();
        var winner = batchFirst ? Batch() : Edit();
        Task<HttpResponseMessage> loser;
        try
        {
            await WaitForOrganizationLockWaiters(connection, expectedWaiters: 1);
            loser = batchFirst ? Edit() : Batch();
            await WaitForOrganizationLockWaiters(connection);
        }
        finally
        {
            // 即使等待狀態查核失敗也釋放屏障。
            await tx.CommitAsync();
        }

        // 勝出者更新共用帳號的版本，另一筆必須因版本過期被拒絕。
        await Expect(await winner, HttpStatusCode.NoContent);
        await Expect(await loser, HttpStatusCode.Conflict, "stale_version");
        var saved = (await Read(batchAdmin)).Accounts;
        var savedShared = saved.Single(a => a.Id == shared.Id);
        var savedBatchOnly = saved.Single(a => a.Id == batchOnly.Id);
        Assert.NotEqual(shared.Version, savedShared.Version);
        Assert.Equal(shared with { Active = !batchFirst, Version = savedShared.Version, IsManager = !batchFirst }, savedShared);

        // 批次成功須停用兩筆並撤銷兩個登入；批次失敗則保留未重疊帳號及兩個登入。
        if (batchFirst)
        {
            Assert.NotEqual(batchOnly.Version, savedBatchOnly.Version);
            Assert.Equal(batchOnly with { Active = false, Version = savedBatchOnly.Version }, savedBatchOnly);
            await Expect(await sharedSession.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthenticated");
            await Expect(await batchOnlySession.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        }
        else
        {
            Assert.Equal(batchOnly, savedBatchOnly);
            Assert.Equal(savedShared.Version, (await Harness.Me(sharedSession)).Version);
            Assert.Equal(batchOnly.Version, (await Harness.Me(batchOnlySession)).Version);
        }
    }

    // 已過 Cookie middleware 的管理請求必須在寫入交易內確認登入及 Administrator 角色。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Revocation_after_authentication_prevents_admin_write(bool revokeSession)
    {
        // 以另一位管理員讀回資料，避免使用即將撤權的 Cookie 驗證保存結果。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        await using var observer = new Harness();
        await observer.Seed("admin", "Administrator");
        using var actor = await app.SignedIn();
        using var admin = await observer.SignedIn();
        var department = await CreateDepartment(admin);
        var path = $"{DepartmentsPath}/{department.Id}";

        // 在 Cookie 驗證交易提交之後停止請求，讓撤銷可先取得列鎖並提交。
        app.Gate.Arm();
        var save = actor.PostAsJsonAsync(path, new { name = "拒絕寫入", active = false, version = department.Version });
        await app
            .Gate
            .Arrived
            .Task
            .WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // 使用和正式異動相同的鎖定順序，安排登入或權限已撤銷的資料庫狀態。
            await app.WithDb(async db =>
            {
                // 先取得管理交易鎖及操作者列鎖，固定撤銷與後續寫入的先後。
                await using var tx = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(80101)");
                await db
                    .Users
                    .FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {app.UserId} FOR UPDATE")
                    .SingleAsync();

                // 分別撤銷登入或唯一的管理角色，證明兩項資格都會在交易內重新判斷。
                if (revokeSession)
                    await db
                        .Sessions
                        .Where(s => s.UserId == app.UserId)
                        .ExecuteDeleteAsync();
                else
                    await db
                        .UserRoles
                        .Where(r => r.UserId == app.UserId)
                        .ExecuteDeleteAsync();
                await tx.CommitAsync();
            });
        }
        finally
        {
            // 釋放屏障後由正式端點重新驗證，fixture 失敗也不能留下等待中的請求。
            app
                .Gate
                .Release
                .TrySetResult();
        }

        // 登入失效和角色被撤銷的回應須可區分，部門仍保留原值和版本。
        var status = revokeSession ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
        var code = revokeSession ? "unauthenticated" : "forbidden";
        await Expect(await save, status, code);
        await Expect(await actor.PostAsJsonAsync(path, new { name = "仍拒絕寫入", active = false, version = department.Version }),
            status, code);
        Assert.Equal(department, (await Read(admin)).Departments.Single(d => d.Id == department.Id));
    }

    // 在撤銷 SQL 已發出後讓資料庫更新失敗，從 HTTP 證明兩種管理操作都完整回滾。
    [Fact]
    public async Task Failed_admin_writes_restore_settings_credentials_and_sessions()
    {
        // 建立可被修改的部門、角色、人選及現有工作階段，供故障後逐一比對。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        using var admin = await app.SignedIn();
        var department = await CreateDepartment(admin);
        var destination = await CreateDepartment(admin);
        var manager = await CreateAccount(admin, destination.Id, TemporaryPassword(), isManager: true);
        var finance = await CreateAccount(admin, destination.Id, TemporaryPassword(), isFinance: true);
        var temporary = TemporaryPassword();
        var account = await CreateAccount(admin, department.Id, temporary);
        var password = TemporaryPassword();
        using var setup = await app.SignedIn(account.UserName, temporary);
        await Expect(await setup.PostAsJsonAsync("/api/auth/change-password",
            new PasswordRequest(temporary, password, (await Harness.Me(setup)).Version)), HttpStatusCode.NoContent);
        var original = (await Read(admin)).Accounts.Single(a => a.Id == account.Id);
        using var first = await app.SignedIn(account.UserName, password);
        using var second = await app.SignedIn(account.UserName, password);

        // 停用或密碼修改在保存帳號列前已送出撤銷 SQL；觸發器在此阻斷後續保存。
        await app.WithDb(db => db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION test_organization_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."Active" IS DISTINCT FROM OLD."Active" OR NEW."PasswordHash" IS DISTINCT FROM OLD."PasswordHash" THEN
                    RAISE EXCEPTION 'isolated organization failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER test_organization_failure BEFORE UPDATE ON "AspNetUsers"
                FOR EACH ROW EXECUTE FUNCTION test_organization_failure();
            """));
        try
        {
            // 送出有效的停用與組織異動；讀回同一筆原設定和同一份有效登入。
            var path = $"{AccountsPath}/{account.Id}";
            var edit = original.Edit() with
            {
                Active = false,
                DepartmentId = destination.Id,
                IsManager = true,
                IsFinance = true,
                ManagerReviewerId = manager.Id,
                FinanceReviewerId = finance.Id
            };
            await Expect(await admin.PostAsJsonAsync(path, edit), HttpStatusCode.InternalServerError, "server_error");
            Assert.Equal(original, (await Read(admin)).Accounts.Single(a => a.Id == account.Id));
            Assert.Equal(["Applicant"], (await Harness.Me(first)).Roles);

            // 以原版本再重設密碼並觸發失敗，原登入、密碼、版本及強制狀態仍須完整保留。
            var reset = TemporaryPassword();
            await Expect(await admin.PostAsJsonAsync($"{path}/reset-password", new { version = original.Version, newPassword = reset }),
                HttpStatusCode.InternalServerError, "server_error");
            Assert.Equal(original, (await Read(admin)).Accounts.Single(a => a.Id == account.Id));
            Assert.Equal(original.Version, (await Harness.Me(first)).Version);
            Assert.Equal(original.Version, (await Harness.Me(second)).Version);
            using var wrong = app.Client();
            await Expect(await Harness.Login(wrong, account.UserName, reset), HttpStatusCode.Unauthorized, "invalid_credentials");
            using var fresh = await app.SignedIn(account.UserName, password);
            Assert.False((await Harness.Me(fresh)).MustChangePassword);
        }
        finally
        {
            // 測試庫共用資料表；無論結果如何都移除本案例的故障注入。
            await app.WithDb(db => db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER test_organization_failure ON \"AspNetUsers\"; DROP FUNCTION test_organization_failure();"));
        }
    }

    // 在批次撤銷兩個帳號的登入後阻擋帳號保存，驗證原版本、狀態與兩個登入都回滾。
    [Fact]
    public async Task Failed_batch_deactivation_rolls_back_all_account_and_session_changes()
    {
        // 兩個目標是唯一歸屬此新部門的帳號，讓觸發器能辨識整批登入是否已撤銷。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        using var admin = await app.SignedIn();
        var department = await CreateDepartment(admin);
        var password = TemporaryPassword();
        var first = await CreateAccount(admin, department.Id, password);
        var second = await CreateAccount(admin, department.Id, password);
        using var firstSession = await app.SignedIn(first.UserName, password);
        using var secondSession = await app.SignedIn(second.UserName, password);

        // 觸發器只在兩個帳號的登入都已於同一交易撤銷時中斷狀態寫入。
        await app.WithDb(db => db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION test_batch_deactivation_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD."Active" AND NOT NEW."Active" AND NOT EXISTS (
                    SELECT 1 FROM "Sessions" s
                    JOIN "AspNetUsers" u ON u."Id" = s."UserId"
                    WHERE u."DepartmentId" = NEW."DepartmentId"
                ) THEN
                    RAISE EXCEPTION 'isolated batch deactivation failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER test_batch_deactivation_failure BEFORE UPDATE OF "Active" ON "AspNetUsers"
                FOR EACH ROW EXECUTE FUNCTION test_batch_deactivation_failure();
            """));
        try
        {
            // 從正常 HTTP 批次路徑送出有效版本，確認後續保存失敗回傳公開錯誤。
            await Expect(await admin.PostAsJsonAsync($"{AccountsPath}/deactivate",
                new { items = new[] { new { first.Id, first.Version }, new { second.Id, second.Version } } }),
                HttpStatusCode.InternalServerError, "server_error");
        }
        finally
        {
            // 撤除可影響共用資料表的故障注入，再驗證實際持久化結果。
            await app.WithDb(db => db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER test_batch_deactivation_failure ON \"AspNetUsers\"; DROP FUNCTION test_batch_deactivation_failure();"));
        }

        // 兩個帳號的原設定和版本仍在，原有 Cookie 也都可以查到身分。
        var saved = (await Read(admin)).Accounts;
        Assert.Equal(first, saved.Single(a => a.Id == first.Id));
        Assert.Equal(second, saved.Single(a => a.Id == second.Id));
        Assert.Equal(first.Version, (await Harness.Me(firstSession)).Version);
        Assert.Equal(second.Version, (await Harness.Me(secondSession)).Version);
    }

    // 活動紀錄與管理設定必須一起提交；在設定已保存但交易未提交時注入故障。
    [Fact]
    public async Task Failed_activity_recording_rolls_back_admin_write_without_renewal()
    {
        // 在初始時間建立管理登入及部門，以其資料和到期時間作為回滾基準。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        using var admin = await app.SignedIn();
        var department = await CreateDepartment(admin);
        var path = $"{DepartmentsPath}/{department.Id}";
        app.Clock.Advance(TimeSpan.FromMinutes(20));

        // 讓資料庫只拒絕活動時間的更新，確保管理設定的保存先執行。
        await app.WithDb(db => db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION test_session_activity_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'isolated session activity failure';
            END $$;
            CREATE TRIGGER test_session_activity_failure BEFORE UPDATE OF "LastSeenAt" ON "Sessions"
                FOR EACH ROW EXECUTE FUNCTION test_session_activity_failure();
            """));
        try
        {
            // 活動紀錄失敗須回報交易失敗，即使部門更新 SQL 已執行。
            await Expect(await admin.PostAsJsonAsync(path,
                new { name = "應回滾", active = false, version = department.Version }), HttpStatusCode.InternalServerError, "server_error");
        }
        finally
        {
            // 在後續確認前撤除故障，避免查詢失敗被誤認為登入已到期。
            await app.WithDb(db => db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER test_session_activity_failure ON \"Sessions\"; DROP FUNCTION test_session_activity_failure();"));
        }

        // 原登入仍按初始時間到期；用新登入讀回原部門名稱、狀態和版本。
        app.Clock.Advance(TimeSpan.FromMinutes(10));
        await Expect(await admin.PostAsJsonAsync(path,
            new { name = "拒絕寫入", active = false, version = department.Version }), HttpStatusCode.Unauthorized, "unauthenticated");
        using var fresh = await app.SignedIn();
        Assert.Equal(department, (await Read(fresh)).Departments.Single(d => d.Id == department.Id));
    }

    // 等待 PostgreSQL 觀察到預期的交易鎖等待者，避免只憑送出 HTTP 推斷順序及競爭。
    private static async Task WaitForOrganizationLockWaiters(string? connection, int expectedWaiters = 2)
    {
        // 觀察連線不持鎖，只查詢隔離資料庫中的實際等待狀態。
        await using var observer = new NpgsqlConnection(connection);
        await observer.OpenAsync();
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            // 先確認第一筆已排隊；兩筆都抵達相同 PostgreSQL advisory lock 時才算建立重疊時序。
            await using var query = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND classid = 0 AND objid = 80101 AND NOT granted",
                observer);
            if (Convert.ToInt64(await query.ExecuteScalarAsync()) == expectedWaiters)
                return;
            await Task.Delay(10);
        }

        // 限時內未達指定時序即報告 fixture 失敗，避免把循序測試當成併發證據。
        Assert.Fail($"須確認 {expectedWaiters} 筆 HTTP 請求實際等待組織交易鎖。");
    }
}
