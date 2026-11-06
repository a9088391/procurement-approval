using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using static OrganizationTests;
using static RequisitionApprovalTests;
using static RequisitionApprovalTransactionTests;
using static RequisitionDraftTests;
using static RequisitionReassignmentTests;

public sealed class RequisitionReassignmentTransactionTests
{
    // 兩次帶同版的改派確實重疊等鎖，只有先提交的人選及歷程會保留。
    [Fact]
    public async Task Concurrent_reassignments_commit_exactly_one_replacement()
    {
        // 使用同一管理帳號的兩次登入，讓各請求獨立通過 Cookie 與防偽驗證。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        using var secondAdmin = await scenario.App.SignedIn();
        var submitted = await Act(owner, await scenario.Draft(), "submit");
        var next = await scenario.AddBusiness(scenario.OtherDepartment.Id, manager: true);
        var later = await scenario.AddBusiness(scenario.OtherDepartment.Id, manager: true);
        var (first, second) = await QueueInOrder(
            () => Replace(scenario.Admin, submitted, "manager", next.Id, "先改派"),
            () => Replace(secondAdmin, submitted, "manager", later.Id, "後改派"));
        await Expect(first, HttpStatusCode.NoContent);
        await Expect(second, HttpStatusCode.Conflict, "stale_version");

        // 正式查詢應回傳勝出版本及唯一改派，落後人選無跨部門查閱權。
        var item = await Item(secondAdmin, submitted);
        Assert.NotEqual(VersionOf(submitted), VersionOf(item));
        AssertPerson(item.GetProperty("manager"), next);
        var read = await Detail(next.Client, submitted);
        Assert.Equal(VersionOf(item), VersionOf(read));
        Assert.Equal(2, read.GetProperty("history").GetArrayLength());
        AssertReassignment(read.GetProperty("history")[1], scenario, "manager", scenario.Manager, next, "先改派",
            scenario.App.Clock.GetUtcNow());
        await Expect(await later.Client.GetAsync(ApprovalPath(submitted)), HttpStatusCode.NotFound, "not_found");
    }

    // 主管決定與改派都用相同資料庫鎖，依實際等鎖先後觀察兩種合法終態。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Manager_approval_and_reassignment_accept_only_the_ordered_winner(bool reassignFirst)
    {
        // 使用同一版單據，且原主管沒有其他可見授權。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var submitted = await Act(owner, await scenario.Draft(), "submit");
        var replacement = await scenario.AddBusiness(scenario.OtherDepartment.Id, manager: true);

        // 兩種操作只在送出順序不同，其他版本及角色條件保持相同。
        Task<HttpResponseMessage> Reassign() => Replace(scenario.Admin, submitted, "manager", replacement.Id);

        // 原主管沿用已讀取的版本，測試其決定在改派後不能成功。
        Task<HttpResponseMessage> Approve() => Post(scenario.Manager.Client, submitted, "approve");

        var (first, second) = await QueueInOrder(reassignFirst ? Reassign : Approve, reassignFirst ? Approve : Reassign);
        await Expect(first, reassignFirst ? HttpStatusCode.NoContent : HttpStatusCode.OK);
        await Expect(second, reassignFirst ? HttpStatusCode.NotFound : HttpStatusCode.Conflict,
            reassignFirst ? "not_found" : "stale_version");

        // 從新的本人登入讀回，兩種順序都只能有一筆對應的歷程。
        using var fresh = await scenario.App.SignedIn(scenario.Owner.Name, scenario.Owner.Password);
        var saved = await Detail(fresh, submitted);
        var history = saved.GetProperty("history");
        Assert.Equal(2, history.GetArrayLength());
        Assert.NotEqual(VersionOf(submitted), VersionOf(saved));
        AssertStatus(saved, reassignFirst ? "manager_pending" : "finance_pending", false, false);
        if (reassignFirst)
        {
            // 改派勝出時，由新人取得主管關簽核權。
            AssertStatus(await Detail(replacement.Client, submitted), "manager_pending", false, true);
            AssertReassignment(history[1], scenario, "manager", scenario.Manager, replacement, "交接待辦",
                scenario.App.Clock.GetUtcNow());
        }
        else
        {
            // 主管勝出時，原指派及決定保留，重讀版本也不能改派已完成的主管關。
            AssertPerson(saved.GetProperty("round").GetProperty("manager"), scenario.Manager);
            AssertEvent(history[1], "approved", "manager", scenario.Manager, scenario.App.Clock.GetUtcNow());
            await Expect(await Replace(scenario.Admin, saved, "manager", replacement.Id), HttpStatusCode.Conflict, "invalid_state");
        }
    }

    // 新接手主管與角色撤銷競爭，驗證撤權先提交會暫停、決定先提交則保留歷史。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Revocation_and_new_reviewer_approval_serialize_in_both_orders(bool revokeFirst)
    {
        // 先完成實際改派；管理角色異動的版本事前讀取，等鎖的是正式 POST 而非前置 GET。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var submitted = await Act(owner, await scenario.Draft(), "submit");
        var replacement = await scenario.AddBusiness(scenario.OtherDepartment.Id, manager: true);
        await Expect(await Replace(scenario.Admin, submitted, "manager", replacement.Id), HttpStatusCode.NoContent);
        var assigned = await Detail(replacement.Client, submitted);
        AssertStatus(assigned, "manager_pending", false, true);
        var roleChange = (await Read(scenario.Admin))
            .Accounts
            .Single(a => a.Id == replacement.Id)
            .Edit() with
        {
            IsManager = false
        };

        // 使用正式組織入口撤銷新人的角色，與決定共用 PostgreSQL 組織鎖。
        Task<HttpResponseMessage> Revoke() => scenario.Admin.PostAsJsonAsync($"{AccountsPath}/{replacement.Id}", roleChange);

        // 兩種順序都讓新審核者送出同一份改派後的版本。
        Task<HttpResponseMessage> Approve() => Post(replacement.Client, assigned, "approve");

        var (first, second) = await QueueInOrder(revokeFirst ? Revoke : Approve, revokeFirst ? Approve : Revoke);
        await Expect(first, revokeFirst ? HttpStatusCode.NoContent : HttpStatusCode.OK);
        await Expect(second, revokeFirst ? HttpStatusCode.NotFound : HttpStatusCode.NoContent, revokeFirst ? "not_found" : null);

        // 已提交的改派事件保持相同；撤權依勝出順序影響暫停與合法決定。
        var saved = await Detail(owner, submitted);
        var history = saved.GetProperty("history");
        AssertPerson(saved.GetProperty("round").GetProperty("manager"), replacement);
        Assert.True(JsonElement.DeepEquals(assigned.GetProperty("history")[1], history[1]));
        Assert.Equal(revokeFirst ? 2 : 3, history.GetArrayLength());
        AssertStatus(saved, revokeFirst ? "manager_pending" : "finance_pending", revokeFirst, false);
        if (revokeFirst)
            Assert.Equal(VersionOf(assigned), VersionOf(saved));
        else
            AssertEvent(history[2], "approved", "manager", replacement, scenario.App.Clock.GetUtcNow());
    }

    // 管理 POST 在 Cookie 已通過驗證後仍重查角色，撤權先提交就拒絕改派。
    [Fact]
    public async Task Administrator_role_removed_after_cookie_validation_cannot_reassign()
    {
        // 用獨立主機暫停單一已驗證請求，正式管理端點沒有移除管理角色的入口。
        await using var scenario = await RequisitionApprovalScenario.Start();
        await using var requestApp = new Harness();
        var owner = scenario.Owner.Client;
        var submitted = await Act(owner, await scenario.Draft(), "submit");
        var replacement = await scenario.AddBusiness(scenario.OtherDepartment.Id, manager: true);
        using var admin = await requestApp.SignedIn(scenario.App.UserName, scenario.App.Password);
        requestApp.Gate.Arm();
        var pending = Replace(admin, submitted, "manager", replacement.Id);
        try
        {
            // 以隔離測試資料庫安排管理角色撤權，與正式組織操作使用相同交易鎖。
            await requestApp
                .Gate
                .Arrived
                .Task
                .WaitAsync(TimeSpan.FromSeconds(10));
            await scenario.App.WithDb(async db =>
            {
                // 只刪除本案管理身分的角色對應，保留 Cookie 及登入紀錄以區分授權失敗。
                await using var tx = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(80101)");
                var affected = await db
                    .UserRoles
                    .Where(assignment => assignment.UserId == scenario.App.UserId)
                    .ExecuteDeleteAsync();
                Assert.Equal(1, affected);
                await tx.CommitAsync();
            });
        }
        finally
        {
            // 總是讓請求進入端點，避免案例失敗後仍有未完成的 HTTP 工作。
            requestApp.Gate.Release.TrySetResult();
        }

        // 帳號仍可登入但已失去即時角色；其他人讀回的版本、指派與歷程完全不變。
        await AssertLimitedError(await pending.WaitAsync(TimeSpan.FromSeconds(10)), HttpStatusCode.Forbidden, "forbidden");
        Assert.True(JsonElement.DeepEquals(submitted, await Detail(owner, submitted)));
    }

    // PostgreSQL 延後約束在提交時失敗，有效指派、版本與歷程都須回滾。
    [Fact]
    public async Task Deferred_commit_failure_rolls_back_reassignment_and_allows_explicit_retry()
    {
        // 讓待改派的財務關前已有主管決定，故障後也要驗證既有事件完全不變。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var submitted = await Act(owner, await scenario.Draft(), "submit");
        var forwarded = await Act(scenario.Manager.Client, submitted, "approve");
        var replacement = await scenario.AddBusiness(scenario.OtherDepartment.Id, finance: true);
        var before = await Item(scenario.Admin, forwarded);

        // 只在隔離測試庫注入提交故障，確認所有 SQL 已送出後仍能一起回滾。
        await scenario.App.WithDb(db => db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION test_reassignment_commit_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'isolated reassignment commit failure';
            END $$;
            CREATE CONSTRAINT TRIGGER test_reassignment_commit_failure
                AFTER INSERT ON "RequisitionApprovalEvents" DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW WHEN (NEW."Kind" = 'reassigned')
                EXECUTE FUNCTION test_reassignment_commit_failure();
            """));
        try
        {
            // 以新登入分別讀回管理欄位與業務明細，確認失敗交易沒有任何部分結果。
            await AssertLimitedError(await Replace(scenario.Admin, before, "finance", replacement.Id),
                HttpStatusCode.InternalServerError, "server_error");
            using var fresh = await scenario.App.SignedIn(scenario.Owner.Name, scenario.Owner.Password);
            using var freshAdmin = await scenario.App.SignedIn();
            Assert.True(JsonElement.DeepEquals(forwarded, await Detail(fresh, submitted)));
            Assert.True(JsonElement.DeepEquals(before, await Item(freshAdmin, submitted)));
        }
        finally
        {
            // 總是移除專屬故障觸發器，避免影響後續的隔離測試。
            await scenario.App.WithDb(db => db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER test_reassignment_commit_failure ON "RequisitionApprovalEvents";
                DROP FUNCTION test_reassignment_commit_failure();
                """));
        }

        // 已確認未提交後才明確重試；成功時只追加一次，後續才交給新財務決定。
        await Expect(await Replace(scenario.Admin, before, "finance", replacement.Id), HttpStatusCode.NoContent);
        var committed = await Detail(replacement.Client, submitted);
        Assert.NotEqual(VersionOf(before), VersionOf(committed));
        AssertStatus(committed, "finance_pending", false, true);
        var history = committed.GetProperty("history");
        Assert.Equal(3, history.GetArrayLength());
        Assert.True(JsonElement.DeepEquals(forwarded.GetProperty("history")[1], history[1]));
        AssertReassignment(history[2], scenario, "finance", scenario.Finance, replacement, "交接待辦",
            scenario.App.Clock.GetUtcNow());
    }

    // 同一組織交易鎖安排兩筆真實請求依序入列，避免以送出或收到回應時間猜測提交次序。
    private static async Task<(HttpResponseMessage First, HttpResponseMessage Second)> QueueInOrder(
        Func<Task<HttpResponseMessage>> startFirst, Func<Task<HttpResponseMessage>> startSecond)
    {
        // 只在 Harness 核准的隔離資料庫用兩條連線控制及觀察組織鎖。
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Database");
        Harness.VerifyTarget(connection);
        await using var blocker = new NpgsqlConnection(connection);
        await blocker.OpenAsync();
        await using var observer = new NpgsqlConnection(connection);
        await observer.OpenAsync();
        Task<HttpResponseMessage> first;
        Task<HttpResponseMessage> second;
        await using (var tx = await blocker.BeginTransactionAsync())
        {
            // 在持鎖期間先觀察一筆，再觀察兩筆實際等鎖，最後同時放行。
            await using var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(80101)", blocker, tx);
            await hold.ExecuteNonQueryAsync();
            first = startFirst();
            await WaitForApprovalLockWaiters(observer, count: 1);
            second = startSecond();
            await WaitForApprovalLockWaiters(observer);
            await tx.CommitAsync();
        }

        // 只回傳實際完成的兩筆請求，讓呼叫端核對個別結果並釋放回應。
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        return (results[0], results[1]);
    }
}
