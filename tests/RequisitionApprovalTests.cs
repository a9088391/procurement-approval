using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using static OrganizationTests;
using static RequisitionDraftTests;

// 透過 HTTP 查閱與簽核請購，從公開回應驗證授權與資料保留。
public sealed class RequisitionApprovalTests
{
    internal const string RequisitionsPath = "/api/requisitions";

    // 一般清單須包含本人尚未送審的草稿，明細及歷程使用同一份授權範圍。
    [Fact]
    public async Task Applicant_can_read_own_draft_in_the_general_list_and_detail()
    {
        // 使用既有草稿入口保存未填齊資料，驗證新入口直接讀到同張單。
        await using var app = new Harness();
        await app.Seed("business", "Applicant");
        using var owner = await app.SignedIn();
        var draft = await CreateDraft(owner, new { subject = "仍在編輯" });
        var id = draft.GetProperty("id").GetGuid();

        // 一般清單提供自己的草稿；詳細結果還沒有送審輪次或操作歷程。
        var list = await owner.GetFromJsonAsync<JsonElement>(RequisitionsPath);
        Assert.Equal(id, Assert.Single(list.EnumerateArray()).GetProperty("id").GetGuid());
        using var response = await owner.GetAsync($"{RequisitionsPath}/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("draft", detail.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("round").ValueKind);
        Assert.Empty(detail.GetProperty("history").EnumerateArray());
        Assert.Empty(detail.GetProperty("previousRounds").EnumerateArray());
        Assert.False(detail.GetProperty("canWithdraw").GetBoolean());
    }

    // 主管及財務依序作出決定；後續設定、部門與草稿寫入均不可改寫首輪內容。
    [Fact]
    public async Task Ordered_approvals_keep_original_content_department_reviewers_and_history()
    {
        // 先從本人草稿讀到可送審資格，並用可區分的時鐘驗證送審與兩次決定。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var manager = scenario.Manager.Client;
        var finance = scenario.Finance.Client;
        var draft = await scenario.Draft();
        var ready = await Detail(owner, draft);
        Assert.True(ready.GetProperty("isComplete").GetBoolean());
        Assert.True(ready.GetProperty("canSubmit").GetBoolean());
        scenario.App.Clock.Advance(TimeSpan.FromMinutes(1));
        var submittedAt = scenario.App.Clock.GetUtcNow();
        var submitted = await Act(owner, draft, "submit");
        AssertStatus(submitted, "manager_pending", false, false);
        Assert.False(submitted.GetProperty("canSubmit").GetBoolean());
        Assert.NotEqual(VersionOf(draft), VersionOf(submitted));
        Assert.Equal(submittedAt, submitted.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(draft.GetProperty("createdAt").GetDateTimeOffset(), submitted.GetProperty("createdAt").GetDateTimeOffset());
        Assert.True(JsonElement.DeepEquals(draft.GetProperty("lines"), submitted.GetProperty("lines")));
        Assert.Equal("請購標籤", submitted.GetProperty("subject").GetString());
        Assert.Equal("整理設備\n補充庫存", submitted.GetProperty("purpose").GetString());
        Assert.Equal(0.87m, submitted.GetProperty("estimatedAmount").GetDecimal());
        var round = submitted.GetProperty("round");
        Assert.Equal(1, round.GetProperty("number").GetInt32());
        Assert.Equal(submittedAt, round.GetProperty("submittedAt").GetDateTimeOffset());
        var submittedDepartment = round.GetProperty("department");
        var submittedManager = round.GetProperty("manager");
        var submittedFinance = round.GetProperty("finance");
        Assert.Equal(scenario.Department.Id, submittedDepartment.GetProperty("id").GetString());
        Assert.Equal(scenario.Department.Name, submittedDepartment.GetProperty("name").GetString());
        Assert.Equal(scenario.Manager.Name, submittedManager.GetProperty("userName").GetString());
        Assert.Equal(scenario.Finance.Name, submittedFinance.GetProperty("userName").GetString());
        AssertEvent(Assert.Single(submitted.GetProperty("history").EnumerateArray()), "submitted", null, scenario.Owner, submittedAt);

        // 調整部門、改名與換掉兩位預設審核人，仍由原指派決定且保留當時部門。
        var nextManager = await scenario.AddBusiness(scenario.Department.Id, manager: true);
        var nextFinance = await scenario.AddBusiness(scenario.OtherDepartment.Id, finance: true);
        await scenario.Update(scenario.Owner, edit => edit with
        {
            DepartmentId = scenario.OtherDepartment.Id,
            ManagerReviewerId = nextManager.Id,
            FinanceReviewerId = nextFinance.Id
        });
        await Expect(await scenario.Admin.PostAsJsonAsync($"{DepartmentsPath}/{scenario.Department.Id}",
            new { name = "改名-" + scenario.Department.Name, active = false, version = scenario.Department.Version }),
            HttpStatusCode.NoContent);
        Assert.True(JsonElement.DeepEquals(submitted, await Detail(owner, draft)));
        await Expect(await Post(nextManager.Client, submitted, "approve"), HttpStatusCode.Forbidden, "forbidden");
        await Expect(await Post(finance, submitted, "approve"), HttpStatusCode.NotFound, "not_found");
        await Expect(await Post(owner, submitted, "approve"), HttpStatusCode.Forbidden, "forbidden");
        await Expect(await Post(owner, submitted, "submit"), HttpStatusCode.Conflict, "invalid_state");
        await AssertDraftClosed(owner, submitted);

        // 只有原主管核准後，財務範圍才可看到此單，且僅原財務可作出第二次決定。
        AssertStatus(await Detail(manager, draft), "manager_pending", false, true);
        scenario.App.Clock.Advance(TimeSpan.FromMinutes(1));
        var managerAt = scenario.App.Clock.GetUtcNow();
        var forwarded = await Act(manager, submitted, "approve");
        AssertStatus(forwarded, "finance_pending", false, false);
        AssertStatus(await Detail(finance, draft), "finance_pending", false, true);
        await Expect(await Post(nextFinance.Client, forwarded, "approve"), HttpStatusCode.Forbidden, "forbidden");
        await Expect(await Post(manager, submitted, "reject"), HttpStatusCode.Conflict, "stale_version");
        scenario.App.Clock.Advance(TimeSpan.FromMinutes(1));
        var financeAt = scenario.App.Clock.GetUtcNow();
        var completed = await Act(finance, forwarded, "approve");
        AssertStatus(completed, "approved", false, false);
        Assert.True(JsonElement.DeepEquals(round, completed.GetProperty("round")));
        Assert.True(JsonElement.DeepEquals(draft.GetProperty("lines"), completed.GetProperty("lines")));
        Assert.Equal(0.87m, completed.GetProperty("estimatedAmount").GetDecimal());
        var history = completed.GetProperty("history");
        Assert.Equal(3, history.GetArrayLength());
        AssertEvent(history[0], "submitted", null, scenario.Owner, submittedAt);
        AssertEvent(history[1], "approved", "manager", scenario.Manager, managerAt);
        AssertEvent(history[2], "approved", "finance", scenario.Finance, financeAt);
        await Expect(await Post(owner, completed, "withdraw"), HttpStatusCode.Conflict, "invalid_state");
        await Expect(await Post(owner, completed, "submit"), HttpStatusCode.Conflict, "invalid_state");
        await Expect(await Post(finance, completed, "reject"), HttpStatusCode.Conflict, "invalid_state");
        await AssertDraftClosed(owner, completed);
        Assert.True(JsonElement.DeepEquals(completed, await Detail(owner, draft)));

        // 新單才取用新部門與新預設審核人，舊輪次不受第二張單影響。
        var later = await Act(owner, await scenario.Draft("設定變更後"), "submit");
        var laterRound = later.GetProperty("round");
        var laterDepartment = laterRound.GetProperty("department");
        var laterManager = laterRound.GetProperty("manager");
        var laterFinance = laterRound.GetProperty("finance");
        Assert.Equal(scenario.OtherDepartment.Id, laterDepartment.GetProperty("id").GetString());
        Assert.Equal(nextManager.Id, laterManager.GetProperty("id").GetString());
        Assert.Equal(nextFinance.Id, laterFinance.GetProperty("id").GetString());
        Assert.True(JsonElement.DeepEquals(completed, await Detail(owner, draft)));
    }

    // 任一關駁回即終止，財務只在該輪已進入財務時繼續保有可見性。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejection_closes_the_original_and_preserves_the_exact_stage(bool financeStage)
    {
        // 使用真正的申請人送審；財務案例才先由原主管推進。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var draft = await scenario.Draft();
        var submitted = await Act(owner, draft, "submit");
        var prior = financeStage ? await Act(scenario.Manager.Client, submitted, "approve") : submitted;
        var reviewer = financeStage ? scenario.Finance : scenario.Manager;
        var rejected = await Act(reviewer.Client, prior, "reject");

        // 驗證指定關卡只多一筆駁回，原單拒絕撤回、重送、修改、刪除及再次審核。
        AssertStatus(rejected, "rejected", false, false);
        var history = rejected.GetProperty("history");
        Assert.Equal(financeStage ? 3 : 2, history.GetArrayLength());
        AssertEvent(history[history.GetArrayLength() - 1], "rejected", financeStage ? "finance" : "manager", reviewer,
            scenario.App.Clock.GetUtcNow());
        await Expect(await Post(owner, rejected, "submit"), HttpStatusCode.Conflict, "invalid_state");
        await Expect(await Post(owner, rejected, "withdraw"), HttpStatusCode.Conflict, "invalid_state");
        await Expect(await Post(reviewer.Client, rejected, "reject"), HttpStatusCode.Conflict, "invalid_state");
        await AssertDraftClosed(owner, rejected);
        if (financeStage)
            Assert.True(JsonElement.DeepEquals(rejected, await Detail(scenario.Finance.Client, draft)));
        else
            await Expect(await scenario.Finance.Client.GetAsync(ApprovalPath(draft)), HttpStatusCode.NotFound, "not_found");
        Assert.True(JsonElement.DeepEquals(rejected, await Detail(owner, draft)));
    }

    // 以單一公開識別推導一般明細與操作路徑，和既有草稿路徑區分。
    internal static string ApprovalPath(JsonElement requisition) => $"{RequisitionsPath}/{requisition.GetProperty("id").GetGuid()}";

    // 每次從 HTTP 讀取目前資料，避免以追蹤狀態或先前結果推測交易是否成功。
    internal static Task<JsonElement> Detail(HttpClient client, JsonElement requisition) =>
        client.GetFromJsonAsync<JsonElement>(ApprovalPath(requisition));

    // 操作一律回傳版本，讓競爭測試能重送相同版本而不改變請求資料。
    internal static Task<HttpResponseMessage> Post(HttpClient client, JsonElement requisition, string action) =>
        client.PostAsJsonAsync($"{ApprovalPath(requisition)}/{action}", new { version = VersionOf(requisition) });

    // 讀出成功操作的完整回應，後續可獨立比對實際保存結果。
    internal static async Task<JsonElement> Act(HttpClient client, JsonElement requisition, string action)
    {
        using var response = await Post(client, requisition, action);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // 驗證狀態及兩個提示一起一致，區分原關卡暫停與合法可決定。
    internal static void AssertStatus(JsonElement requisition, string status, bool paused, bool canDecide)
    {
        Assert.Equal(status, requisition.GetProperty("status").GetString());
        Assert.Equal(paused, requisition.GetProperty("paused").GetBoolean());
        Assert.Equal(canDecide, requisition.GetProperty("canDecide").GetBoolean());
    }

    // 核對公開歷程的輪次、結果、關卡、操作者與可控時間。
    internal static void AssertEvent(
        JsonElement entry, string kind, string? stage, RequisitionApprovalScenario.Business actor, DateTimeOffset time,
        int roundNumber = 1)
    {
        Assert.Equal(roundNumber, entry.GetProperty("roundNumber").GetInt32());
        Assert.Equal(kind, entry.GetProperty("kind").GetString());
        Assert.Equal(stage, entry.GetProperty("stage").GetString());
        var person = entry.GetProperty("actor");
        Assert.Equal(actor.Id, person.GetProperty("id").GetString());
        Assert.Equal(actor.Name, person.GetProperty("userName").GetString());
        Assert.Equal(time, entry.GetProperty("occurredAt").GetDateTimeOffset());
    }

    // 仍在審核或已結案的單不能從草稿入口讀取、保存或刪除。
    private static async Task AssertDraftClosed(HttpClient owner, JsonElement requisition)
    {
        // 使用目前有效版本和完整寫入形狀，確保拒絕來自既有草稿範圍。
        var path = PathOf(requisition);
        var version = VersionOf(requisition);
        await Expect(await owner.GetAsync(path), HttpStatusCode.NotFound, "not_found");
        await Expect(await owner.PostAsJsonAsync(path, new { subject = "不應保存", purpose = "", lines = new object[0], version }),
            HttpStatusCode.NotFound, "not_found");
        await Expect(await owner.PostAsJsonAsync($"{path}/delete", new { version }), HttpStatusCode.NotFound, "not_found");
        var drafts = await owner.GetFromJsonAsync<JsonElement>(DraftsPath);
        Assert.Empty(drafts.EnumerateArray());
    }
}
