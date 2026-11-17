using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static OrganizationTests;
using static RequisitionApprovalTests;
using static RequisitionDraftTests;

// 從正式 HTTP 操作驗證撤回後只能由本人修改，歷程及舊快照持續保留。
public sealed class RequisitionWithdrawalTests
{
    // 只有本人能在首關決定前撤回；後續保存不得改寫本輪歷史，也不能把單刪掉。
    [Fact]
    public async Task Applicant_can_withdraw_edit_and_keep_the_first_snapshot()
    {
        // 以兩位分工的審核人送審，再用實際 HTTP 操作撤回。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var draft = await scenario.Draft();
        await Expect(await Post(owner, draft, "withdraw"), HttpStatusCode.Conflict, "invalid_state");
        var submitted = await Act(owner, draft, "submit");

        // 先用仍可見的原主管及偽造的本文測試授權與輸入，兩者都不得撤回。
        await Expect(await Post(scenario.Manager.Client, submitted, "withdraw"), HttpStatusCode.Forbidden, "forbidden");
        var withdraw = $"{ApprovalPath(draft)}/withdraw";
        await Expect(await owner.PostAsJsonAsync(withdraw, new { }), HttpStatusCode.BadRequest, "invalid_input");
        await Expect(await owner.PostAsJsonAsync(withdraw, new { version = VersionOf(submitted), state = "withdrawn" }),
            HttpStatusCode.BadRequest, "invalid_input");
        scenario.App.Clock.Advance(TimeSpan.FromMinutes(1));
        var withdrawn = await Act(owner, submitted, "withdraw");

        // 核對狀態、版本與撤回事件，拒絕重播、重複撤回和刪除。
        AssertStatus(withdrawn, "withdrawn", false, false);
        Assert.True(submitted.GetProperty("canWithdraw").GetBoolean());
        Assert.False(withdrawn.GetProperty("canWithdraw").GetBoolean());
        Assert.True(withdrawn.GetProperty("canSubmit").GetBoolean());
        Assert.NotEqual(VersionOf(submitted), VersionOf(withdrawn));
        var history = withdrawn.GetProperty("history");
        Assert.Equal(2, history.GetArrayLength());
        Assert.True(JsonElement.DeepEquals(submitted.GetProperty("history")[0], history[0]));
        AssertEvent(history[1], "withdrawn", null, scenario.Owner, scenario.App.Clock.GetUtcNow());
        await Expect(await Post(owner, submitted, "withdraw"), HttpStatusCode.Conflict, "stale_version");
        await Expect(await Post(owner, withdrawn, "withdraw"), HttpStatusCode.Conflict, "invalid_state");
        await Expect(await Post(owner, submitted, "submit"), HttpStatusCode.Conflict, "stale_version");
        await Expect(await owner.PostAsJsonAsync($"{PathOf(draft)}/delete", new { version = VersionOf(withdrawn) }),
            HttpStatusCode.NotFound, "not_found");

        // 本人可在編輯清單找回原單；送審時拿到的舊版不能保存到撤回單。
        var editable = await ReadDraft(owner, draft);
        AssertStatus(await Detail(owner, draft), "withdrawn", false, false);
        Assert.Equal("withdrawn", editable.GetProperty("status").GetString());
        Assert.Equal(VersionOf(withdrawn), VersionOf(editable));
        var drafts = await owner.GetFromJsonAsync<JsonElement>(DraftsPath);
        var listedDraft = Assert.Single(drafts.EnumerateArray());
        Assert.Equal(draft.GetProperty("id").GetGuid(), listedDraft.GetProperty("id").GetGuid());
        await Expect(await Save(owner, submitted), HttpStatusCode.Conflict, "stale_version");

        // 保存新的內容，再從一般明細比較目前內容及完整的舊輪快照。
        await Expect(await Save(owner, withdrawn), HttpStatusCode.OK);
        var saved = await Detail(owner, draft);
        Assert.Equal("改購紙盒", saved.GetProperty("subject").GetString());
        Assert.Equal("新用途", saved.GetProperty("purpose").GetString());
        Assert.Equal(25.5m, saved.GetProperty("estimatedAmount").GetDecimal());
        AssertSameFields(submitted, saved, "round");
        AssertSameFields(submitted, Assert.Single(saved.GetProperty("previousRounds").EnumerateArray()),
            "round", "subject", "purpose", "lines", "estimatedAmount");
        Assert.True(JsonElement.DeepEquals(history, saved.GetProperty("history")));

        // 連續撤回再送審時，所有舊輪仍依原順序回傳，且第二輪也只多一筆撤回。
        var second = await Act(owner, saved, "submit");
        var withdrawnAgain = await Act(owner, second, "withdraw");
        var third = await Act(owner, withdrawnAgain, "submit");
        var thirdRound = third.GetProperty("round");
        Assert.Equal(3, thirdRound.GetProperty("number").GetInt32());
        var previous = third.GetProperty("previousRounds");
        Assert.Equal(2, previous.GetArrayLength());
        AssertSameFields(submitted, previous[0], "round", "subject", "purpose", "lines", "estimatedAmount");
        AssertSameFields(second, previous[1], "round", "subject", "purpose", "lines", "estimatedAmount");
        var allEvents = third.GetProperty("history");
        Assert.Equal(5, allEvents.GetArrayLength());
        AssertEvent(allEvents[3], "withdrawn", null, scenario.Owner, scenario.App.Clock.GetUtcNow(), roundNumber: 2);
        AssertEvent(allEvents[4], "submitted", null, scenario.Owner, scenario.App.Clock.GetUtcNow(), roundNumber: 3);
    }

    // 重送同一張單，授權與審核者回應只能依最新輪；本人仍能查閱全部歷程。
    [Fact]
    public async Task Resubmission_uses_current_content_department_and_people_without_exposing_past_rounds()
    {
        // 舊部門主管和原指派主管原本可見；撤回後兩種範圍及財務都不能查詢或異動。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var oldLocalManager = await scenario.AddBusiness(scenario.Department.Id, manager: true);
        var draft = await scenario.Draft();
        var first = await Act(owner, draft, "submit");
        AssertStatus(await Detail(oldLocalManager.Client, first), "manager_pending", false, false);
        var withdrawn = await Act(owner, first, "withdraw");
        foreach (var actor in new[] { scenario.Manager, oldLocalManager, scenario.Finance })
        {
            // 已知識別與版本也不能透過任何查詢、保存或狀態操作取得撤回單。
            await AssertHidden(actor.Client, withdrawn);
            await Expect(await actor.Client.GetAsync(PathOf(draft)), HttpStatusCode.NotFound, "not_found");
            var editable = await actor.Client.GetFromJsonAsync<JsonElement>(DraftsPath);
            Assert.Empty(editable.EnumerateArray());
            await Expect(await Save(actor.Client, withdrawn), HttpStatusCode.NotFound, "not_found");
            await Expect(await actor.Client.PostAsJsonAsync($"{PathOf(draft)}/delete", new { version = VersionOf(withdrawn) }),
                HttpStatusCode.NotFound, "not_found");
            foreach (var action in new[] { "submit", "withdraw", "approve", "reject" })
                await Expect(await Post(actor.Client, withdrawn, action), HttpStatusCode.NotFound, "not_found");
        }

        // 修改所有快照內容，再由管理 API 指定不同部門及兩位新審核人。
        await Expect(await Save(owner, withdrawn), HttpStatusCode.OK);
        var revised = await ReadDraft(owner, draft);
        var department = await CreateDepartment(scenario.Admin);
        var manager = await scenario.AddBusiness(scenario.Department.Id, manager: true);
        var finance = await scenario.AddBusiness(department.Id, finance: true);
        await scenario.Update(scenario.Owner, edit => edit with
        {
            DepartmentId = department.Id,
            ManagerReviewerId = manager.Id,
            FinanceReviewerId = finance.Id
        });
        await Expect(await scenario.Admin.PostAsJsonAsync($"{DepartmentsPath}/{scenario.Department.Id}",
            new { name = "舊名已改-" + scenario.Department.Name, active = false, version = scenario.Department.Version }),
            HttpStatusCode.NoContent);

        // 重送取得同一識別與單號的新輪，原部門名稱、人選、內容及事件都維持原值。
        scenario.App.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await Act(owner, revised, "submit");
        AssertStatus(second, "manager_pending", false, false);
        AssertSameFields(draft, second, "id", "number", "createdAt");
        AssertSameFields(revised, second, "subject", "purpose", "lines", "estimatedAmount");
        Assert.NotEqual(VersionOf(revised), VersionOf(second));
        var round = second.GetProperty("round");
        Assert.Equal(2, round.GetProperty("number").GetInt32());
        Assert.Equal(scenario.App.Clock.GetUtcNow(), round.GetProperty("submittedAt").GetDateTimeOffset());
        var savedDepartment = round.GetProperty("department");
        Assert.Equal(department.Id, savedDepartment.GetProperty("id").GetString());
        Assert.Equal(department.Name, savedDepartment.GetProperty("name").GetString());
        Assert.Equal(
            manager.Id,
            round
                .GetProperty("manager")
                .GetProperty("id")
                .GetString());
        Assert.Equal(
            finance.Id,
            round
                .GetProperty("finance")
                .GetProperty("id")
                .GetString());
        AssertSameFields(first, Assert.Single(second.GetProperty("previousRounds").EnumerateArray()),
            "round", "subject", "purpose", "lines", "estimatedAmount");
        var history = second.GetProperty("history");
        Assert.Equal(3, history.GetArrayLength());
        Assert.True(JsonElement.DeepEquals(withdrawn.GetProperty("history")[0], history[0]));
        Assert.True(JsonElement.DeepEquals(withdrawn.GetProperty("history")[1], history[1]));
        AssertEvent(history[2], "submitted", null, scenario.Owner, scenario.App.Clock.GetUtcNow(), roundNumber: 2);
        await Expect(await Post(owner, revised, "submit"), HttpStatusCode.Conflict, "stale_version");
        await Expect(await Save(owner, second), HttpStatusCode.NotFound, "not_found");

        // 舊主管不再有範圍，財務要等本輪主管核准，新的指派主管只看到新輪。
        await AssertHidden(scenario.Manager.Client, second);
        await AssertHidden(oldLocalManager.Client, second);
        await AssertHidden(scenario.Finance.Client, second);
        await AssertHidden(finance.Client, second);
        var managerView = await Detail(manager.Client, second);
        AssertStatus(managerView, "manager_pending", false, true);
        AssertLatestOnly(second, managerView);

        // 驗證決定的 HTTP 回應也不附舊輪；目前財務及原財務皆只能查已進關的新輪。
        var forwarded = await Act(manager.Client, second, "approve");
        var applicantView = await Detail(owner, second);
        Assert.Equal(4, applicantView.GetProperty("history").GetArrayLength());
        AssertEvent(applicantView.GetProperty("history")[3], "approved", "manager", manager,
            scenario.App.Clock.GetUtcNow(), roundNumber: 2);
        AssertSameFields(second, applicantView, "previousRounds");
        AssertLatestOnly(applicantView, forwarded);
        AssertLatestOnly(applicantView, await Detail(finance.Client, second));
        AssertLatestOnly(applicantView, await Detail(scenario.Finance.Client, second));
    }

    // 重送重新驗證可編輯內容及目前設定；每次拒絕都保留同一版本、內容和第一輪。
    [Fact]
    public async Task Invalid_resubmission_preserves_withdrawn_content_version_and_history()
    {
        // 撤回後能保存未完成的內容，但不能建立新輪次。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var submitted = await Act(owner, await scenario.Draft(), "submit");
        var withdrawn = await Act(owner, submitted, "withdraw");
        await Expect(await owner.PostAsJsonAsync(PathOf(withdrawn),
            new { subject = "未完成", purpose = "", lines = Array.Empty<object>(), version = VersionOf(withdrawn) }),
            HttpStatusCode.OK);
        await AssertResubmissionBlocked(owner, withdrawn, HttpStatusCode.BadRequest, "invalid_input");

        // 透過真實管理端點撤權與停用，保留下來的人選不得被當作仍具資格。
        await Expect(await Save(owner, await ReadDraft(owner, withdrawn)), HttpStatusCode.OK);
        await scenario.Update(scenario.Manager, edit => edit with { IsManager = false });
        await AssertResubmissionBlocked(owner, withdrawn, HttpStatusCode.Conflict, "reviewers_unavailable");
        await scenario.Update(scenario.Manager, edit => edit with { IsManager = true });
        await scenario.Update(scenario.Finance, edit => edit with { Active = false });
        await AssertResubmissionBlocked(owner, withdrawn, HttpStatusCode.Conflict, "reviewers_unavailable");
        await scenario.Update(scenario.Finance, edit => edit with { Active = true });
        await scenario.Update(scenario.Owner, edit => edit with { IsManager = true });
        await scenario.Update(scenario.Manager, edit => edit with { IsFinance = true });

        // 管理 API 不允許寫入這些舊資料組合；直接安排既有狀態，仍透過 HTTP 驗證重送。
        var invalidSettings = new (string? Department, string Manager, string Finance)[]
        {
            (null, scenario.Manager.Id, scenario.Finance.Id),
            (scenario.Department.Id, scenario.Owner.Id, scenario.Finance.Id),
            (scenario.Department.Id, scenario.Manager.Id, scenario.Manager.Id)
        };
        foreach (var (department, manager, finance) in invalidSettings)
        {
            // 只更動本案帳號的設定；讀回後以目前請購版本重送。
            await scenario.App.WithDb(db => db
                .Users
                .Where(u => u.Id == scenario.Owner.Id)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(u => u.DepartmentId, department)
                    .SetProperty(u => u.ManagerReviewerId, manager)
                    .SetProperty(u => u.FinanceReviewerId, finance)));
            await AssertResubmissionBlocked(owner, withdrawn, HttpStatusCode.Conflict, "reviewers_unavailable");
        }

        // 合格設定恢復後以原保存版本重試，只能產生第二輪。
        await scenario.Update(scenario.Owner, edit => edit with
        {
            ManagerReviewerId = scenario.Manager.Id,
            FinanceReviewerId = scenario.Finance.Id
        });
        var finalDraft = await ReadDraft(owner, withdrawn);
        var resubmitted = await Act(owner, finalDraft, "submit");
        var round = resubmitted.GetProperty("round");
        Assert.Equal(2, round.GetProperty("number").GetInt32());
        AssertSameFields(finalDraft, resubmitted, "id", "number", "subject", "purpose", "lines", "estimatedAmount");
    }

    // 首次決定是撤回分界，即使未完成關卡失格使兩張請購都處於暫停。
    [Fact]
    public async Task Paused_approval_can_only_be_withdrawn_before_the_first_decision()
    {
        // 同一財務設定準備兩張單，只有其中一張先由主管核准。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var pending = await Act(owner, await scenario.Draft(), "submit");
        var forwarded = await Act(scenario.Manager.Client, await Act(owner, await scenario.Draft(), "submit"), "approve");
        await scenario.Update(scenario.Finance, edit => edit with { IsFinance = false });

        // 兩張都會暫停，但只有尚無任何決定的單仍能撤回。
        var beforeDecision = await Detail(owner, pending);
        AssertStatus(beforeDecision, "manager_pending", true, false);
        Assert.True(beforeDecision.GetProperty("canWithdraw").GetBoolean());
        var afterDecision = await Detail(owner, forwarded);
        AssertStatus(afterDecision, "finance_pending", true, false);
        Assert.False(afterDecision.GetProperty("canWithdraw").GetBoolean());
        await Expect(await Post(owner, afterDecision, "withdraw"), HttpStatusCode.Conflict, "invalid_state");
        var withdrawn = await Act(owner, beforeDecision, "withdraw");
        AssertStatus(withdrawn, "withdrawn", false, false);
        Assert.False(withdrawn.GetProperty("canSubmit").GetBoolean());
        Assert.True(JsonElement.DeepEquals(afterDecision, await Detail(owner, forwarded)));
    }

    // 清單與明細都不能由識別辨識不可見的單，適用於撤回期間及重新分派後。
    private static async Task AssertHidden(HttpClient client, JsonElement requisition)
    {
        var list = await client.GetFromJsonAsync<JsonElement>(RequisitionsPath);
        Assert.DoesNotContain(list.EnumerateArray(), entry => ApprovalPath(entry) == ApprovalPath(requisition));
        await Expect(await client.GetAsync(ApprovalPath(requisition)), HttpStatusCode.NotFound, "not_found");
    }

    // 非本人不能看到舊快照或舊事件；歷程必須和本人所見的新輪一致。
    private static void AssertLatestOnly(JsonElement ownerView, JsonElement reviewerView)
    {
        Assert.False(reviewerView.GetProperty("canWithdraw").GetBoolean());
        Assert.Empty(reviewerView.GetProperty("previousRounds").EnumerateArray());
        AssertSameFields(ownerView, reviewerView, "round", "subject", "purpose", "lines", "estimatedAmount");
        var latest = reviewerView.GetProperty("history");
        var ownerHistory = ownerView.GetProperty("history");
        Assert.Equal(ownerHistory.GetArrayLength() - 2, latest.GetArrayLength());
        for (var i = 0; i < latest.GetArrayLength(); i++)
            Assert.True(JsonElement.DeepEquals(ownerHistory[i + 2], latest[i]));
    }

    // 失敗的送審應讓一般明細與可編輯讀取維持原狀，兩者都包含版本。
    private static async Task AssertResubmissionBlocked(HttpClient owner, JsonElement requisition, HttpStatusCode status, string code)
    {
        var edit = await ReadDraft(owner, requisition);
        var before = await Detail(owner, requisition);
        Assert.False(before.GetProperty("canSubmit").GetBoolean());
        await Expect(await Post(owner, edit, "submit"), status, code);
        Assert.True(JsonElement.DeepEquals(before, await Detail(owner, requisition)));
        Assert.True(JsonElement.DeepEquals(edit, await ReadDraft(owner, requisition)));
    }

    // 跨明細及快照比對指定欄位，失敗時標出未保留的契約欄位。
    private static void AssertSameFields(JsonElement expected, JsonElement actual, params string[] fields)
    {
        foreach (var field in fields)
            Assert.True(JsonElement.DeepEquals(expected.GetProperty(field), actual.GetProperty(field)), $"應保留 {field}。");
    }

    // 用指定版本完整取代可編輯欄位，讓舊版及本人保存走同一個公開入口。
    private static Task<HttpResponseMessage> Save(HttpClient client, JsonElement source) =>
        client.PostAsJsonAsync(PathOf(source), new
        {
            subject = "改購紙盒",
            purpose = "新用途",
            lines = new[] { new { itemName = "紙盒", unit = "箱", quantity = 2, unitPrice = 12.75m } },
            version = VersionOf(source)
        });
}
