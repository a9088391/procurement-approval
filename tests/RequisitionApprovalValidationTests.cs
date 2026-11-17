using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static OrganizationTests;
using static RequisitionApprovalTests;
using static RequisitionDraftTests;

// 在既有草稿與新送審入口驗證資料、資格及暫停的界線。
public sealed class RequisitionApprovalValidationTests
{
    // 明細 1／100 筆及合法數量、單價、總額邊界能保存並送審。
    [Theory]
    [InlineData(1, 10_000, 1_000, 10_000_000)]
    [InlineData(1, 10, 1_000_000, 10_000_000)]
    [InlineData(100, 1, 0.01, 1)]
    public async Task Submit_accepts_the_inclusive_limits(int count, int quantity, double unitPrice, double amount)
    {
        // 先透過草稿 API 保存，再送出保存版本並從快照讀回。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var draft = await scenario.Draft(count: count, quantity: quantity, unitPrice: (decimal)unitPrice);
        var submitted = await Act(owner, draft, "submit");
        AssertStatus(submitted, "manager_pending", false, false);
        Assert.Equal((decimal)amount, submitted.GetProperty("estimatedAmount").GetDecimal());
        Assert.Equal(count, submitted.GetProperty("lines").GetArrayLength());
        Assert.True(JsonElement.DeepEquals(draft.GetProperty("lines"), submitted.GetProperty("lines")));
        Assert.True(JsonElement.DeepEquals(submitted, await Detail(owner, draft)));
    }

    // 草稿可保存未填齊的資料，但缺任一必填欄位或超出筆數不得建立輪次。
    [Fact]
    public async Task Submit_rejects_each_missing_required_field_and_zero_or_101_lines()
    {
        // 各案例只移除一項合格條件，確保每個必填欄位及兩個不合法筆數都被檢查。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var cases = new[]
        {
            await scenario.Draft("  "),
            await scenario.Draft(count: 0),
            await scenario.Draft(count: 101),
            await CreateDraft(owner, new
            {
                subject = "缺用途",
                lines = new[] { new { itemName = "紙", unit = "箱", quantity = 1, unitPrice = 2m } }
            }),
            await CreateDraft(owner, new
            {
                subject = "缺品名", purpose = "用途", lines = new[] { new { unit = "箱", quantity = 1, unitPrice = 2m } }
            }),
            await CreateDraft(owner, new
            {
                subject = "缺單位", purpose = "用途", lines = new[] { new { itemName = "紙", quantity = 1, unitPrice = 2m } }
            }),
            await CreateDraft(owner, new
            {
                subject = "缺數量", purpose = "用途", lines = new[] { new { itemName = "紙", unit = "箱", unitPrice = 2m } }
            }),
            await CreateDraft(owner, new
            {
                subject = "缺單價", purpose = "用途", lines = new[] { new { itemName = "紙", unit = "箱", quantity = 1 } }
            })
        };
        foreach (var draft in cases)
        {
            // 先查無法送審，再直接 POST 確認不能繞過畫面限制，並核對原稿內容。
            var saved = await Detail(owner, draft);
            Assert.False(saved.GetProperty("isComplete").GetBoolean());
            Assert.False(saved.GetProperty("canSubmit").GetBoolean());
            await Expect(await Post(owner, draft, "submit"), HttpStatusCode.BadRequest, "invalid_input");
            Assert.True(JsonElement.DeepEquals(saved, await Detail(owner, draft)));
        }
    }

    // 驗證非法數值、假造欄位及過期版本無法改寫合格草稿或送出其他內容。
    [Fact]
    public async Task Submit_uses_only_the_saved_version_and_rejects_forged_payloads()
    {
        // 讀取合格草稿後使用原始 JSON 測試保存端點，避免測試序列化替輸入修正格式。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var draft = await scenario.Draft();
        var before = await Detail(owner, draft);
        foreach (var values in new[]
        {
            """ "quantity":1.0,"unitPrice":1 """, """ "quantity":10001,"unitPrice":1 """,
            """ "quantity":0,"unitPrice":1 """, """ "quantity":"1","unitPrice":1 """,
            """ "quantity":1,"unitPrice":0 """, """ "quantity":1,"unitPrice":0.001 """,
            """ "quantity":1,"unitPrice":1e1 """, """ "quantity":1,"unitPrice":1000000.01 """,
            """ "quantity":11,"unitPrice":1000000 """
        })
        {
            var body = $$"""{"subject":"替換","purpose":"用途","version":"{{VersionOf(draft)}}","lines":[{"itemName":"紙","unit":"箱",{{values}}}]}""";
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            await Expect(await owner.PostAsync(PathOf(draft), content), HttpStatusCode.BadRequest, "invalid_input");
        }

        // 操作本文僅可有版本；代填流程、內容、金額及人選都須直接拒絕。
        foreach (var input in new object[]
        {
            new { }, new { version = " " }, new { version = 17 },
            new { version = VersionOf(draft), managerReviewerId = scenario.Owner.Id },
            new { version = VersionOf(draft), financeReviewerId = scenario.Manager.Id },
            new { version = VersionOf(draft), status = "approved" },
            new { version = VersionOf(draft), lines = new[] { new { quantity = 0, unitPrice = -1m } } },
            new { version = VersionOf(draft), estimatedAmount = 1m },
            new { version = VersionOf(draft), departmentId = scenario.OtherDepartment.Id }
        })
            await Expect(await owner.PostAsJsonAsync($"{ApprovalPath(draft)}/submit", input), HttpStatusCode.BadRequest, "invalid_input");
        Assert.True(JsonElement.DeepEquals(before, await Detail(owner, draft)));

        // 存出新版本後，舊版不能送審；合法請求只使用新版已保存的主旨及金額。
        await Expect(await owner.PostAsJsonAsync(PathOf(draft), new
        {
            subject = "合法新版",
            purpose = "用途",
            version = VersionOf(draft),
            lines = new[] { new { itemName = "紙", unit = "箱", quantity = 2, unitPrice = 1.23m } }
        }), HttpStatusCode.OK);
        var edited = await Detail(owner, draft);
        await Expect(await Post(owner, draft, "submit"), HttpStatusCode.Conflict, "stale_version");
        Assert.True(JsonElement.DeepEquals(edited, await Detail(owner, draft)));
        var submitted = await Act(owner, edited, "submit");
        Assert.Equal("合法新版", submitted.GetProperty("subject").GetString());
        Assert.Equal(2.46m, submitted.GetProperty("estimatedAmount").GetDecimal());

        // 審核也不得代填結果或關卡，請求失敗後原待審版本及歷程仍一致。
        await Expect(await scenario.Manager.Client.PostAsJsonAsync($"{ApprovalPath(draft)}/approve",
            new { version = VersionOf(submitted), stage = "finance", status = "approved" }), HttpStatusCode.BadRequest, "invalid_input");
        Assert.True(JsonElement.DeepEquals(submitted, await Detail(owner, draft)));
    }

    // 目前設定缺人、停用或缺角時，已完成的草稿仍保留且提示需補正設定。
    [Theory]
    [InlineData(true, "missing")]
    [InlineData(false, "missing")]
    [InlineData(true, "inactive")]
    [InlineData(false, "inactive")]
    [InlineData(true, "role")]
    [InlineData(false, "role")]
    public async Task Submit_rechecks_both_default_reviewer_qualifications(bool manager, string change)
    {
        // 草稿先在有效設定下完成，失格由正式管理端點生效。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var draft = await scenario.Draft();
        var reviewer = manager ? scenario.Manager : scenario.Finance;
        if (change == "missing")
        {
            await scenario.Update(scenario.Owner, edit => manager
                ? edit with { ManagerReviewerId = null }
                : edit with { FinanceReviewerId = null });
        }
        else
        {
            await scenario.Update(reviewer, edit => change == "inactive"
                ? edit with { Active = false }
                : edit with { IsManager = false, IsFinance = false });
        }

        // 合格內容與資格失敗要分開，並在公開草稿 API 讀回原始內容及版本。
        await AssertSettingsBlock(scenario, draft);
    }

    // 驗證異常設定阻擋送審，並由正式管理入口補上缺少的部門後以同一版草稿送審。
    [Fact]
    public async Task Submit_rejects_self_assignment_shared_reviewer_and_missing_department()
    {
        // 三個異常預設均能保持有效外鍵；同人和自審者有兩角色，避免被其他資格誤攔。
        await using var scenario = await RequisitionApprovalScenario.Start();
        await scenario.Update(scenario.Owner, edit => edit with { IsManager = true, IsFinance = true });
        await scenario.Update(scenario.Manager, edit => edit with { IsFinance = true });
        var draft = await scenario.Draft();
        foreach (var (manager, finance) in new[]
        {
            (scenario.Owner.Id, scenario.Finance.Id), (scenario.Manager.Id, scenario.Owner.Id),
            (scenario.Manager.Id, scenario.Manager.Id)
        })
        {
            // 隔離測試庫模擬已存在的無效設定；所有行為及結果仍經正式 HTTP 入口。
            await scenario.App.WithDb(db => db.Users
                .Where(u => u.Id == scenario.Owner.Id)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(u => u.ManagerReviewerId, manager)
                    .SetProperty(u => u.FinanceReviewerId, finance)));
            await AssertSettingsBlock(scenario, draft);
        }

        // 有兩位合格人選但沒有部門時，須提供部門補正訊息且保留草稿。
        await scenario.App.WithDb(db => db.Users
            .Where(u => u.Id == scenario.Owner.Id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(u => u.ManagerReviewerId, scenario.Manager.Id)
                .SetProperty(u => u.FinanceReviewerId, scenario.Finance.Id)
                .SetProperty(u => u.DepartmentId, (string?)null)));
        var error = await AssertSettingsBlock(scenario, draft);
        Assert.Contains("部門", error.Message);

        // 管理員補上啟用中的部門後，原草稿仍是同一版本，且可送審但尚無輪次或歷程。
        var department = scenario.OtherDepartment;
        await scenario.Update(scenario.Owner, edit => edit with { DepartmentId = department.Id });
        var owner = scenario.Owner.Client;
        var ready = await Detail(owner, draft);
        AssertStatus(ready, "draft", false, false);
        Assert.Equal(VersionOf(draft), VersionOf(ready));
        Assert.True(ready.GetProperty("canSubmit").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ready.GetProperty("round").ValueKind);
        Assert.Empty(ready.GetProperty("history").EnumerateArray());

        // 用原保存版本送審並獨立讀回，核對原內容、補上的部門與原人選都保存於唯一送審輪次。
        var submitted = await Act(owner, draft, "submit");
        var saved = await Detail(owner, draft);
        Assert.True(JsonElement.DeepEquals(submitted, saved));
        AssertStatus(saved, "manager_pending", false, false);
        foreach (var field in new[] { "subject", "purpose", "lines", "estimatedAmount" })
            Assert.True(JsonElement.DeepEquals(draft.GetProperty(field), saved.GetProperty(field)), $"送審須保留 {field}。");
        var round = saved.GetProperty("round");
        var savedDepartment = round.GetProperty("department");
        var assignedManager = round.GetProperty("manager");
        var assignedFinance = round.GetProperty("finance");
        Assert.Equal(department.Id, savedDepartment.GetProperty("id").GetString());
        Assert.Equal(department.Name, savedDepartment.GetProperty("name").GetString());
        Assert.Equal(scenario.Manager.Id, assignedManager.GetProperty("id").GetString());
        Assert.Equal(scenario.Finance.Id, assignedFinance.GetProperty("id").GetString());
        AssertEvent(Assert.Single(saved.GetProperty("history").EnumerateArray()), "submitted", null, scenario.Owner,
            scenario.App.Clock.GetUtcNow());
    }

    // 既有部門歸屬停用後仍可保存快照，送審資格以合法歸屬及人選為準。
    [Fact]
    public async Task Existing_inactive_department_is_retained_in_the_snapshot()
    {
        // 從正式端點停用現有部門，不更改申請人既有歸屬。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var department = scenario.Department;
        await Expect(await scenario.Admin.PostAsJsonAsync($"{DepartmentsPath}/{department.Id}",
            new { name = department.Name, active = false, version = department.Version }), HttpStatusCode.NoContent);

        // 資格仍可送審，歷史資料保留相同部門識別。
        var submitted = await Act(scenario.Owner.Client, await scenario.Draft(), "submit");
        var savedDepartment = submitted
            .GetProperty("round")
            .GetProperty("department")
            .GetProperty("id")
            .GetString();
        Assert.Equal(department.Id, savedDepartment);
    }

    // 尚未完成的任一關失格就停在原關卡，角色撤銷及停用都由正式管理操作完成。
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    public async Task Losing_any_unfinished_reviewer_pauses_the_original_stage(bool financeStage, bool managerLost, bool deactivate)
    {
        // 保留現有版本及指派，讓失格後的請求帶著同一個舊登入及狀態重試。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var manager = scenario.Manager.Client;
        var submitted = await Act(owner, await scenario.Draft(), "submit");
        var prior = financeStage ? await Act(manager, submitted, "approve") : submitted;
        var reviewer = managerLost ? scenario.Manager : scenario.Finance;
        await scenario.Update(reviewer, edit => deactivate
            ? edit with { Active = false }
            : edit with { IsManager = false, IsFinance = false });

        // 完全失去登入回 401；只有失去角色則不能再經舊指派取得跨部門資料或審核。
        var code = deactivate ? "unauthenticated" : "not_found";
        var failure = deactivate ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound;
        await Expect(await Post(reviewer.Client, prior, "approve"), failure, code);
        await Expect(await reviewer.Client.GetAsync(ApprovalPath(prior)), failure, code);
        if (!financeStage && !managerLost)
        {
            // 後一關先失格時，即使當前主管仍有角色，核准或駁回都不得推進。
            AssertStatus(await Detail(manager, prior), "manager_pending", true, false);
            await Expect(await Post(manager, prior, "approve"), HttpStatusCode.Conflict, "reviewers_unavailable");
            await Expect(await Post(manager, prior, "reject"), HttpStatusCode.Conflict, "reviewers_unavailable");
        }

        // 以本人再次查詢，原關、指派、版本和既有決定不變，且清單也顯示暫停。
        var result = await Detail(owner, prior);
        AssertStatus(result, financeStage ? "finance_pending" : "manager_pending", true, false);
        Assert.Equal(VersionOf(prior), VersionOf(result));
        Assert.True(JsonElement.DeepEquals(prior.GetProperty("history"), result.GetProperty("history")));
        Assert.True(JsonElement.DeepEquals(prior.GetProperty("round"), result.GetProperty("round")));
        var list = await owner.GetFromJsonAsync<JsonElement>(RequisitionsPath);
        var listedRequisition = Assert.Single(list.EnumerateArray());
        Assert.True(listedRequisition.GetProperty("paused").GetBoolean());
    }

    // 主管已決定後才失格，既有決定保留，財務關不受已完成資格影響。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Losing_a_completed_reviewer_does_not_pause_finance(bool deactivate)
    {
        // 先取得已生效的主管決定，再由管理員修改該主管的資格。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var submitted = await Act(scenario.Owner.Client, await scenario.Draft(), "submit");
        var forwarded = await Act(scenario.Manager.Client, submitted, "approve");
        await scenario.Update(scenario.Manager, edit => deactivate
            ? edit with { Active = false }
            : edit with { IsManager = false });

        // 財務資格、原主管歷程及結案結果仍依同一輪次處理。
        AssertStatus(await Detail(scenario.Finance.Client, forwarded), "finance_pending", false, true);
        var completed = await Act(scenario.Finance.Client, forwarded, "approve");
        AssertStatus(completed, "approved", false, false);
        Assert.True(JsonElement.DeepEquals(forwarded.GetProperty("history")[1], completed.GetProperty("history")[1]));
    }

    // 檢查設定失敗、完整性提示及資料回讀，並讓呼叫端核對個別補正訊息。
    private static async Task<Procurement.ErrorResponse> AssertSettingsBlock(RequisitionApprovalScenario scenario, JsonElement draft)
    {
        // 內容完整但不能送審；錯誤不能建立輪次、歷程或修改既有版本。
        var owner = scenario.Owner.Client;
        var pending = await Detail(owner, draft);
        Assert.True(pending.GetProperty("isComplete").GetBoolean());
        Assert.False(pending.GetProperty("canSubmit").GetBoolean());
        using var blocked = await Post(owner, draft, "submit");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var error = Assert.IsType<Procurement.ErrorResponse>(await blocked.Content.ReadFromJsonAsync<Procurement.ErrorResponse>());
        Assert.Equal("reviewers_unavailable", error.Code);
        Assert.True(JsonElement.DeepEquals(pending, await Detail(owner, draft)));
        Assert.True(JsonElement.DeepEquals(draft, await ReadDraft(owner, draft)));
        return error;
    }
}
