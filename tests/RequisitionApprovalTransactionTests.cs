using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using static OrganizationTests;
using static RequisitionApprovalTests;
using static RequisitionDraftTests;

// 用真實 PostgreSQL 等鎖、提交屏障與故障驗證簽核狀態和歷程原子性。
public sealed class RequisitionApprovalTransactionTests
{
    // 同版本同時送審、重複決定或相反決定時，只有一筆可提交。
    [Theory]
    [InlineData("submit", "submit")]
    [InlineData("approve", "approve")]
    [InlineData("approve", "reject")]
    public async Task Overlapping_actions_commit_one_round_or_one_stage_result(string firstAction, string secondAction)
    {
        // 同帳號另開一個登入，讓兩個 HTTP 請求各有可用的 Cookie 與權杖。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var draft = await scenario.Draft();
        var submit = firstAction == "submit";
        var before = submit ? draft : await Act(owner, draft, "submit");
        var actor = submit ? scenario.Owner : scenario.Manager;
        using var other = await scenario.App.SignedIn(actor.Name, actor.Password);

        // 真正持有流程使用的交易鎖，兩個 Cookie 驗證仍可完成並進入端點交易。
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Database");
        await using var blocker = new NpgsqlConnection(connection);
        await blocker.OpenAsync();
        await using var observer = new NpgsqlConnection(connection);
        await observer.OpenAsync();
        Task<HttpResponseMessage> first;
        Task<HttpResponseMessage> second;
        await using (var tx = await blocker.BeginTransactionAsync())
        {
            // 確認兩筆請求都在 PostgreSQL 真的等鎖後才放行。
            await using var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(80101)", blocker, tx);
            await hold.ExecuteNonQueryAsync();
            first = Post(actor.Client, before, firstAction);
            second = Post(other, before, secondAction);
            await WaitForApprovalLockWaiters(observer);
            await tx.CommitAsync();
        }

        // 放行順序不保證；成功狀態及歷程必須反映實際勝出操作。
        var responses = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        using var a = responses[0];
        using var b = responses[1];
        var committed = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        await Expect(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict),
            HttpStatusCode.Conflict, "stale_version");
        var winner = committed == a ? firstAction : secondAction;
        var saved = await committed.Content.ReadFromJsonAsync<JsonElement>();
        var history = saved.GetProperty("history");
        var expectedStatus = submit ? "manager_pending" : winner == "approve" ? "finance_pending" : "rejected";
        AssertStatus(saved, expectedStatus, false, false);
        var round = saved.GetProperty("round");
        Assert.Equal(1, round.GetProperty("number").GetInt32());
        Assert.Equal(submit ? 1 : 2, history.GetArrayLength());
        AssertEvent(history[0], "submitted", null, scenario.Owner, scenario.App.Clock.GetUtcNow());
        if (!submit)
        {
            AssertEvent(history[1], winner == "approve" ? "approved" : "rejected", "manager", actor,
                scenario.App.Clock.GetUtcNow());
        }

        // 以獨立登入查回勝出結果，舊版重送仍不能產生重複輪次或決定。
        await Expect(await Post(other, before, secondAction), HttpStatusCode.Conflict, "stale_version");
        using var fresh = await scenario.App.SignedIn(scenario.Owner.Name, scenario.Owner.Password);
        Assert.True(JsonElement.DeepEquals(saved, await Detail(fresh, draft)));
    }

    // 申請人和原主管帶著同版競爭，依資料庫組織鎖先後驗證兩種可提交結果。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Withdrawal_and_first_decision_accept_only_the_ordered_winner(bool withdrawFirst)
    {
        // 不同帳號各自完成登入，兩筆請求共用目前請購版本。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var manager = scenario.Manager.Client;
        var submitted = await Act(owner, await scenario.Draft(), "submit");
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Database");
        await using var blocker = new NpgsqlConnection(connection);
        await blocker.OpenAsync();
        await using var observer = new NpgsqlConnection(connection);
        await observer.OpenAsync();
        Task<HttpResponseMessage> first;
        Task<HttpResponseMessage> second;
        await using (var tx = await blocker.BeginTransactionAsync())
        {
            // 控制兩筆 PostgreSQL 等鎖入列順序，確認確實重疊才一起放行。
            await using var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(80101)", blocker, tx);
            await hold.ExecuteNonQueryAsync();
            first = withdrawFirst ? Post(owner, submitted, "withdraw") : Post(manager, submitted, "approve");
            await WaitForApprovalLockWaiters(observer, count: 1);
            second = withdrawFirst ? Post(manager, submitted, "approve") : Post(owner, submitted, "withdraw");
            await WaitForApprovalLockWaiters(observer);
            await tx.CommitAsync();
        }

        // 撤回先成功會隱藏原單；主管先成功則本人可見，但版本已舊。
        using var winner = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
        await Expect(await second.WaitAsync(TimeSpan.FromSeconds(10)),
            withdrawFirst ? HttpStatusCode.NotFound : HttpStatusCode.Conflict,
            withdrawFirst ? "not_found" : "stale_version");
        var committed = await winner.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(VersionOf(submitted), VersionOf(committed));
        AssertStatus(committed, withdrawFirst ? "withdrawn" : "finance_pending", false, false);
        var history = committed.GetProperty("history");
        Assert.Equal(2, history.GetArrayLength());
        Assert.True(JsonElement.DeepEquals(submitted.GetProperty("history")[0], history[0]));
        AssertEvent(history[1], withdrawFirst ? "withdrawn" : "approved", withdrawFirst ? null : "manager",
            withdrawFirst ? scenario.Owner : scenario.Manager, scenario.App.Clock.GetUtcNow());

        // 同版本重播仍失敗，再由本人獨立讀回，確保只有一筆合法效果。
        await Expect(await Post(owner, submitted, "withdraw"), HttpStatusCode.Conflict, "stale_version");
        using var fresh = await scenario.App.SignedIn(scenario.Owner.Name, scenario.Owner.Password);
        Assert.True(JsonElement.DeepEquals(committed, await Detail(fresh, submitted)));
    }

    // 已通過 Cookie 驗證的送審、決定與撤回，仍以管理異動先提交後的資格為準。
    [Theory]
    [InlineData("submit", false)]
    [InlineData("approve", false)]
    [InlineData("approve", true)]
    [InlineData("withdraw", true)]
    public async Task Management_revocation_committed_after_cookie_validation_wins(string action, bool deactivate)
    {
        // 管理和簽核使用不同 HTTP 主機，屏障只停住待測的業務請求。
        await using var scenario = await RequisitionApprovalScenario.Start();
        await using var requestApp = new Harness();
        var draft = await scenario.Draft();
        var owner = scenario.Owner.Client;
        var submit = action == "submit";
        var before = submit ? draft : await Act(owner, draft, "submit");
        var actor = action == "approve" ? scenario.Manager : scenario.Owner;
        var target = action == "withdraw" ? scenario.Owner : scenario.Manager;
        var managerView = action == "withdraw" ? await Detail(scenario.Manager.Client, before) : default;
        using var client = await requestApp.SignedIn(actor.Name, actor.Password);

        // 讓管理撤權透過正式端點先提交，再讓業務請求進入自己的交易。
        requestApp.Gate.Arm();
        var waiting = Post(client, before, action);
        try
        {
            await requestApp
                .Gate
                .Arrived
                .Task
                .WaitAsync(TimeSpan.FromSeconds(10));
            await scenario.Update(target, edit => deactivate
                ? edit with { Active = false }
                : edit with { IsManager = false });
        }
        finally
        {
            // 不論管理操作的斷言是否成功，都釋放暫停請求。
            requestApp.Gate.Release.TrySetResult();
        }

        // 撤回請求的申請人已停用，由仍可查看的原主管核對版本、原關與歷程完全不變。
        var status = submit ? HttpStatusCode.Conflict : deactivate ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound;
        var code = submit ? "reviewers_unavailable" : deactivate ? "unauthenticated" : "not_found";
        await Expect(await waiting.WaitAsync(TimeSpan.FromSeconds(10)), status, code);
        if (action == "withdraw")
        {
            Assert.True(JsonElement.DeepEquals(managerView, await Detail(scenario.Manager.Client, draft)));
            return;
        }

        // 送審保持草稿；失去審核人時保留舊指派與歷程並暫停原關。
        var saved = await Detail(owner, draft);
        Assert.Equal(VersionOf(before), VersionOf(saved));
        AssertStatus(saved, submit ? "draft" : "manager_pending", !submit, false);
        if (submit)
        {
            Assert.Empty(saved.GetProperty("history").EnumerateArray());
            Assert.True(JsonElement.DeepEquals(draft, await ReadDraft(owner, draft)));
        }
        else
        {
            Assert.True(JsonElement.DeepEquals(before.GetProperty("round"), saved.GetProperty("round")));
            Assert.True(JsonElement.DeepEquals(before.GetProperty("history"), saved.GetProperty("history")));
        }
    }

    // 在提交時讓歷程觸發器失敗，首送、決定、撤回及重送的寫入都必須回滾。
    [Theory]
    [InlineData("submit")]
    [InlineData("approve")]
    [InlineData("withdraw")]
    [InlineData("resubmit")]
    public async Task Deferred_database_failure_rolls_back_and_allows_an_explicit_retry(string operation)
    {
        // 先在無故障時準備操作來源，保留申請人視角的完整版本做交易前後比較。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var draft = await scenario.Draft();
        var before = operation == "submit" ? await Detail(owner, draft) : await Act(owner, draft, "submit");
        if (operation == "resubmit")
            before = await Act(owner, before, "withdraw");
        var client = operation == "approve" ? scenario.Manager.Client : owner;
        var action = operation == "resubmit" ? "submit" : operation;

        // 只於隔離測試庫設置延後的觸發器，讓錯誤發生在所有相關寫入已送出之後。
        await scenario.App.WithDb(db => db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION test_requisition_approval_commit_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'isolated requisition approval commit failure';
            END $$;
            CREATE CONSTRAINT TRIGGER test_requisition_approval_commit_failure
                AFTER INSERT ON "RequisitionApprovalEvents" DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW WHEN (NEW."Kind" IN ('submitted', 'approved', 'withdrawn'))
                EXECUTE FUNCTION test_requisition_approval_commit_failure();
            """));
        try
        {
            // 正式 HTTP 回報失敗，再確認整份資料仍與舊版本相同。
            await Expect(await Post(client, before, action), HttpStatusCode.InternalServerError, "server_error");
            Assert.True(JsonElement.DeepEquals(before, await Detail(owner, draft)));
            using var fresh = await scenario.App.SignedIn(scenario.Owner.Name, scenario.Owner.Password);
            Assert.True(JsonElement.DeepEquals(before, await Detail(fresh, draft)));
        }
        finally
        {
            // 總是移除本案觸發器，避免其他共用隔離庫的案例受到影響。
            await scenario.App.WithDb(db => db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER test_requisition_approval_commit_failure ON "RequisitionApprovalEvents";
                DROP FUNCTION test_requisition_approval_commit_failure();
                """));
        }

        // 讀回已確認原交易未生效後，同一原版本才可由呼叫端明確重試並成功。
        var recovered = await Act(client, before, action);
        var status = operation switch
        {
            "approve" => "finance_pending",
            "withdraw" => "withdrawn",
            _ => "manager_pending"
        };
        AssertStatus(recovered, status, false, false);
        Assert.Equal(before.GetProperty("history").GetArrayLength() + 1, recovered.GetProperty("history").GetArrayLength());
        Assert.True(JsonElement.DeepEquals(recovered, await Detail(owner, draft)));
    }

    // 觀察 PostgreSQL 交易實際等候組織鎖，可在第二筆入列前先確認第一筆。
    private static async Task WaitForApprovalLockWaiters(NpgsqlConnection observer, int count = 2)
    {
        // 只計目前測試庫內正在等待同一個 advisory lock 的請求。
        await using var query = new NpgsqlCommand("""
            SELECT count(*) FROM pg_stat_activity
            WHERE datname = current_database() AND wait_event_type = 'Lock' AND wait_event = 'advisory'
              AND query = 'SELECT pg_advisory_xact_lock(80101)'
            """, observer);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            // 必須觀察到指定筆數，呼叫端才可開始下一個請求或解除鎖。
            if (Convert.ToInt64(await query.ExecuteScalarAsync()) == count)
                return;
            await Task.Delay(10);
        }
        Assert.Fail($"必須有 {count} 筆簽核交易實際等待 PostgreSQL 組織鎖。");
    }
}
