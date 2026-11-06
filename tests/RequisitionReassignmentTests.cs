using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static OrganizationTests;
using static RequisitionApprovalTests;
using static RequisitionDraftTests;

public sealed class RequisitionReassignmentTests
{
    internal const string Path = "/api/admin/reassignments";

    // 分別修復兩個未完成關卡，確認資格完整才恢復，且原決定不受後續撤權影響。
    [Fact]
    public async Task Admin_can_reassign_a_paused_manager_stage()
    {
        // 兩個原人選先失格，單據留在主管關。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var submitted = await Act(owner, await scenario.Draft(), "submit");
        var manager = await scenario.AddBusiness(scenario.OtherDepartment.Id, manager: true, finance: true);
        var finance = await scenario.AddBusiness(scenario.OtherDepartment.Id, finance: true);
        await scenario.Update(scenario.Manager, account => account with { IsManager = false });
        await scenario.Update(scenario.Finance, account => account with { Active = false });
        AssertStatus(await Detail(owner, submitted), "manager_pending", true, false);

        // 只補主管後仍然暫停，管理讀回使用新版本，接手者看得到單但不能先決定。
        var item = await Item(scenario.Admin, submitted);
        Assert.Equal("manager", item.GetProperty("stage").GetString());
        Assert.True(item.GetProperty("paused").GetBoolean());
        Assert.Equal(VersionOf(submitted), VersionOf(item));
        await Expect(await Replace(scenario.Admin, item, "manager", manager.Id, "  交接主管待辦  "), HttpStatusCode.NoContent);
        var partial = await Detail(manager.Client, submitted);
        AssertStatus(partial, "manager_pending", true, false);
        await Expect(await Post(manager.Client, partial, "approve"), HttpStatusCode.Conflict, "reviewers_unavailable");

        // 在主管尚未決定時修復未來的財務關，仍按主管、財務原順序完成。
        scenario.App.Clock.Advance(TimeSpan.FromMinutes(1));
        var interim = await Item(scenario.Admin, submitted);
        Assert.Equal(VersionOf(partial), VersionOf(interim));
        Assert.True(interim.GetProperty("paused").GetBoolean());
        await AssertLimitedError(await Replace(scenario.Admin, interim, "finance", manager.Id),
            HttpStatusCode.Conflict, "reviewers_unavailable");
        await Expect(await Replace(scenario.Admin, interim, "finance", finance.Id), HttpStatusCode.NoContent);
        var ready = await Detail(manager.Client, submitted);
        AssertStatus(ready, "manager_pending", false, true);
        Assert.NotEqual(VersionOf(partial), VersionOf(ready));
        await Expect(await finance.Client.GetAsync(ApprovalPath(ready)), HttpStatusCode.NotFound, "not_found");

        // 核對有效人選、內容、部門與事件；已完成主管的資格不再造成暫停。
        var history = ready.GetProperty("history");
        Assert.Equal(3, history.GetArrayLength());
        Assert.True(JsonElement.DeepEquals(submitted.GetProperty("history")[0], history[0]));
        AssertReassignment(history[1], scenario, "manager", scenario.Manager, manager, "交接主管待辦",
            scenario.App.Clock.GetUtcNow().AddMinutes(-1));
        AssertReassignment(history[2], scenario, "finance", scenario.Finance, finance, "交接待辦", scenario.App.Clock.GetUtcNow());
        AssertContent(submitted, ready);
        var forwarded = await Act(manager.Client, ready, "approve");
        await scenario.Update(manager, account => account with { IsManager = false });
        AssertStatus(await Detail(finance.Client, forwarded), "finance_pending", false, true);
        var completed = await Act(finance.Client, forwarded, "approve");
        AssertStatus(completed, "approved", false, false);
        Assert.True(JsonElement.DeepEquals(forwarded.GetProperty("history")[3], completed.GetProperty("history")[3]));
        AssertContent(submitted, completed);
        Assert.DoesNotContain((await List(scenario.Admin)).GetProperty("items").EnumerateArray(), item => Id(item) == Id(completed));
    }

    // 已完成主管關不可改派；原財務可依獨立角色查閱，但任何版本均不能代替新人決定。
    [Fact]
    public async Task Finance_reassignment_preserves_manager_decision_and_removes_old_signing_right()
    {
        // 由原主管核准，再嘗試改派兩關以區分已完成和未完成狀態。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var submitted = await Act(owner, await scenario.Draft(), "submit");
        var forwarded = await Act(scenario.Manager.Client, submitted, "approve");
        var replacement = await scenario.AddBusiness(scenario.OtherDepartment.Id, manager: true, finance: true);
        await Expect(await Replace(scenario.Admin, forwarded, "manager", replacement.Id),
            HttpStatusCode.Conflict, "invalid_state");
        Assert.True(JsonElement.DeepEquals(forwarded, await Detail(owner, submitted)));
        await Expect(await Replace(scenario.Admin, forwarded, "finance", replacement.Id), HttpStatusCode.NoContent);

        // 舊財務仍有一般查閱權；舊版回報衝突，新版明確拒絕簽核。
        var visible = await Detail(scenario.Finance.Client, forwarded);
        AssertStatus(visible, "finance_pending", false, false);
        AssertPerson(visible.GetProperty("round").GetProperty("finance"), replacement);
        await Expect(await Post(scenario.Finance.Client, forwarded, "approve"), HttpStatusCode.Conflict, "stale_version");
        await Expect(await Post(scenario.Finance.Client, visible, "approve"), HttpStatusCode.Forbidden, "forbidden");
        var history = visible.GetProperty("history");
        Assert.True(JsonElement.DeepEquals(forwarded.GetProperty("history")[1], history[1]));
        AssertReassignment(history[2], scenario, "finance", scenario.Finance, replacement, "交接待辦", scenario.App.Clock.GetUtcNow());
        AssertContent(forwarded, visible);
        AssertStatus(await Act(replacement.Client, visible, "approve"), "approved", false, false);
    }

    // 反覆改派只追加歷程，原跨部門主管隨指派失去查閱，撤回重送仍保留原始快照。
    [Fact]
    public async Task Withdrawal_and_resubmission_preserve_original_assignment_and_isolate_previous_round()
    {
        // 兩位新人都不負責送審部門，只有該輪目前被指派者有跨部門查閱權。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var draft = await scenario.Draft("首輪內容");
        var submitted = await Act(owner, draft, "submit");
        var first = await scenario.AddBusiness(scenario.OtherDepartment.Id, manager: true);
        var next = await scenario.AddBusiness(scenario.OtherDepartment.Id, manager: true);
        await Expect(await Replace(scenario.Admin, submitted, "manager", first.Id), HttpStatusCode.NoContent);
        await Expect(await scenario.Manager.Client.GetAsync(ApprovalPath(draft)), HttpStatusCode.NotFound, "not_found");
        await Expect(await Post(scenario.Manager.Client, submitted, "approve"), HttpStatusCode.NotFound, "not_found");
        var reassigned = await Detail(first.Client, draft);
        await Expect(await Replace(scenario.Admin, reassigned, "manager", next.Id), HttpStatusCode.NoContent);
        await Expect(await first.Client.GetAsync(ApprovalPath(draft)), HttpStatusCode.NotFound, "not_found");
        var read = await Detail(next.Client, draft);
        AssertReassignment(read.GetProperty("history")[2], scenario, "manager", first, next, "交接待辦",
            scenario.App.Clock.GetUtcNow());

        // 改派不算決定；本人仍能撤回，撤回後即使當輪受指派也看不到。
        var owned = await Detail(owner, draft);
        Assert.True(owned.GetProperty("canWithdraw").GetBoolean());
        var withdrawn = await Act(owner, owned, "withdraw");
        await Expect(await next.Client.GetAsync(ApprovalPath(draft)), HttpStatusCode.NotFound, "not_found");
        await Expect(await Replace(scenario.Admin, withdrawn, "manager", first.Id), HttpStatusCode.NotFound, "not_found");
        var previous = Assert.Single(withdrawn.GetProperty("previousRounds").EnumerateArray());
        AssertContent(submitted, previous);
        AssertPerson(previous.GetProperty("round").GetProperty("manager"), scenario.Manager);

        // 重送採目前部門與預設人選；原主管僅能看到新輪，本人仍能讀回完整舊歷程。
        await scenario.Update(scenario.Owner, edit => edit with { DepartmentId = scenario.OtherDepartment.Id });
        var resubmitted = await Act(owner, withdrawn, "submit");
        Assert.Equal(2, resubmitted.GetProperty("round").GetProperty("number").GetInt32());
        Assert.Equal(scenario.OtherDepartment.Id, resubmitted.GetProperty("round").GetProperty("department").GetProperty("id").GetString());
        AssertPerson(resubmitted.GetProperty("round").GetProperty("manager"), scenario.Manager);
        Assert.True(JsonElement.DeepEquals(previous, Assert.Single(resubmitted.GetProperty("previousRounds").EnumerateArray())));
        Assert.Equal(5, resubmitted.GetProperty("history").GetArrayLength());
        var reviewer = await Detail(scenario.Manager.Client, draft);
        Assert.Empty(reviewer.GetProperty("previousRounds").EnumerateArray());
        AssertEvent(Assert.Single(reviewer.GetProperty("history").EnumerateArray()), "submitted", null, scenario.Owner,
            scenario.App.Clock.GetUtcNow(), roundNumber: 2);
    }

    // 透過直接 HTTP 驗證管理契約欄位、即時身分、配對防偽及可辨識的拒絕回應。
    [Fact]
    public async Task Admin_contract_exposes_only_limited_fields_and_rejects_unauthorized_requests()
    {
        // 讓清單同時遇到草稿、待辦及不合格候選；仍待改密碼的候選具有指派資格。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var draft = await scenario.Draft("不能出現在管理回應的主旨");
        var submitted = await Act(owner, await scenario.Draft("只有業務可見的請購內容"), "submit");
        var disabled = await scenario.AddBusiness(scenario.OtherDepartment.Id, manager: true, finance: true);
        await scenario.Update(disabled, account => account with { Active = false });
        var candidate = await scenario.AddBusiness(scenario.OtherDepartment.Id, manager: true, ready: false);

        // 逐層核對封閉欄位，證明回應不含申請人、內容、金額、時間或帳號設定。
        var list = await List(scenario.Admin);
        AssertKeys(list, "items", "managers", "finances");
        var item = Assert.Single(list.GetProperty("items").EnumerateArray(), item => Id(item) == Id(submitted));
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), row => Id(row) == Id(draft));
        AssertKeys(item, "id", "number", "department", "stage", "paused", "version", "manager", "finance");
        AssertKeys(item.GetProperty("department"), "id", "name");
        Assert.True(JsonElement.DeepEquals(submitted.GetProperty("round").GetProperty("department"), item.GetProperty("department")));
        Assert.True(JsonElement.DeepEquals(submitted.GetProperty("number"), item.GetProperty("number")));
        AssertPerson(item.GetProperty("manager"), scenario.Manager);
        AssertPerson(item.GetProperty("finance"), scenario.Finance);
        // 不論人選是哪一關，都以相同有限欄位提供，角色資格以兩份清單區分。
        var managers = list.GetProperty("managers").EnumerateArray();
        var finances = list.GetProperty("finances").EnumerateArray();
        foreach (var person in managers.Concat(finances))
            AssertKeys(person, "id", "userName");
        var managerIds = managers.Select(person => person.GetProperty("id").GetString());
        var financeIds = finances.Select(person => person.GetProperty("id").GetString());
        Assert.Contains(candidate.Id, managerIds);
        Assert.DoesNotContain(candidate.Id, financeIds);
        Assert.DoesNotContain(disabled.Id, managerIds);
        Assert.DoesNotContain(disabled.Id, financeIds);
        Assert.DoesNotContain(scenario.Owner.Id, managerIds);

        // 匿名、業務帳號、強制改密碼及缺少管理角色的管理帳號都不能讀取或改派。
        using var anonymous = scenario.App.Client();
        await using var wrongRole = new Harness();
        await wrongRole.Seed("admin");
        using var noAdministrator = await wrongRole.SignedIn();
        foreach (var (client, status, code) in new[]
        {
            (anonymous, HttpStatusCode.Unauthorized, "unauthenticated"),
            (owner, HttpStatusCode.Forbidden, "forbidden"),
            (candidate.Client, HttpStatusCode.Forbidden, "forbidden"),
            (noAdministrator, HttpStatusCode.Forbidden, "forbidden")
        })
        {
            // 錯誤仍為相同有限結構，不將請購或輸入資料放進回應。
            await AssertLimitedError(await client.GetAsync(Path), status, code);
            await AssertLimitedError(await Replace(client, item, "manager", candidate.Id), status, code);
        }
        await AssertLimitedError(await scenario.Admin.GetAsync(ApprovalPath(submitted)), HttpStatusCode.Forbidden, "forbidden");
        await AssertLimitedError(await Replace(scenario.Admin, draft, "manager", candidate.Id), HttpStatusCode.NotFound, "not_found");
        await AssertLimitedError(await Replace(scenario.Admin, item, "manager", candidate.Id, id: "unrelated"),
            HttpStatusCode.NotFound, "not_found");
        await AssertLimitedError(await Replace(scenario.Admin, item, "manager", candidate.Id, id: Guid.NewGuid().ToString()),
            HttpStatusCode.NotFound, "not_found");

        // 混用另一人的標頭和管理 Cookie 時讀寫皆拒絕；GET 省略標頭仍保留直接讀取契約。
        const string header = "X-CSRF-TOKEN";
        scenario.Admin.DefaultRequestHeaders.Remove(header);
        scenario.Admin.DefaultRequestHeaders.Add(header, owner.DefaultRequestHeaders.GetValues(header).Single());
        await AssertLimitedError(await scenario.Admin.GetAsync(Path), HttpStatusCode.BadRequest, "invalid_csrf");
        await AssertLimitedError(await Replace(scenario.Admin, item, "manager", candidate.Id), HttpStatusCode.BadRequest, "invalid_csrf");
        scenario.Admin.DefaultRequestHeaders.Remove(header);
        Assert.True(JsonElement.DeepEquals(list, await List(scenario.Admin)));
        await AssertLimitedError(await Replace(scenario.Admin, item, "manager", candidate.Id), HttpStatusCode.BadRequest, "invalid_csrf");

        // 所有拒絕皆無效果；恢復有效權杖後，待改密碼者可被指派但仍不能行使審核權。
        Assert.True(JsonElement.DeepEquals(submitted, await Detail(owner, submitted)));
        await Harness.Token(scenario.Admin);
        using var success = await Replace(scenario.Admin, item, "manager", candidate.Id);
        Assert.Equal(HttpStatusCode.NoContent, success.StatusCode);
        Assert.Empty(await success.Content.ReadAsByteArrayAsync());
        AssertPerson((await Item(scenario.Admin, submitted)).GetProperty("manager"), candidate);
        AssertStatus(await Detail(owner, submitted), "manager_pending", false, false);
        await AssertLimitedError(await Post(candidate.Client, submitted, "approve"), HttpStatusCode.Forbidden, "forbidden");

        // 即使類別與角色都正確，進入強制改密碼狀態的原管理者也失去改派讀寫權。
        await scenario.App.WithDb(db => db
            .Users
            .Where(u => u.Id == scenario.App.UserId)
            .ExecuteUpdateAsync(update => update.SetProperty(u => u.MustChangePassword, true)));
        await AssertLimitedError(await scenario.Admin.GetAsync(Path), HttpStatusCode.Forbidden, "forbidden");
        await AssertLimitedError(await Replace(scenario.Admin, item, "manager", candidate.Id), HttpStatusCode.Forbidden, "forbidden");
    }

    // 拒絕無效輸入、版本及同單角色衝突，且只有成功提交才會改變任何公開狀態。
    [Fact]
    public async Task Invalid_reassignment_never_changes_the_round_or_audit()
    {
        // 為申請人及另一關人選加上目標角色，使拒絕確實來自三人分工。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var submitted = await Act(owner, await scenario.Draft(), "submit");
        var replacement = await scenario.AddBusiness(scenario.OtherDepartment.Id, manager: true);
        var disabled = await scenario.AddBusiness(scenario.OtherDepartment.Id, manager: true);
        var missingRole = await scenario.AddBusiness(scenario.OtherDepartment.Id, finance: true);
        await scenario.Update(disabled, edit => edit with { Active = false });
        await scenario.Update(scenario.Owner, edit => edit with { IsManager = true });
        await scenario.Update(scenario.Finance, edit => edit with { IsManager = true });
        var current = await Item(scenario.Admin, submitted);

        // 每種不可用人選均只收到通用錯誤，且沒有寫入。
        foreach (var reviewerId in new[] { disabled.Id, missingRole.Id, scenario.Owner.Id, scenario.Finance.Id, scenario.Manager.Id, Guid.NewGuid().ToString() })
            await AssertLimitedError(await Replace(scenario.Admin, current, "manager", reviewerId),
                HttpStatusCode.Conflict, "reviewers_unavailable");

        // 驗證原始 JSON 欄位限制、格式與理由界線，錯誤不回顯提交內容。
        foreach (var payload in new object[]
        {
            new { version = VersionOf(current), stage = "unknown", reviewerId = replacement.Id, reason = "交接" },
            new { version = VersionOf(current), stage = "manager", reviewerId = "", reason = "交接" },
            new { version = "", stage = "manager", reviewerId = replacement.Id, reason = "交接" },
            new { version = VersionOf(current), stage = "manager", reviewerId = replacement.Id },
            new { version = VersionOf(current), stage = "manager", reviewerId = replacement.Id, reason = " \t\u3000 " },
            new { version = VersionOf(current), stage = "manager", reviewerId = replacement.Id, reason = "不得回顯的內容\0" },
            new { version = VersionOf(current), stage = "manager", reviewerId = replacement.Id, reason = 2 },
            new { version = VersionOf(current), stage = "manager", reviewerId = replacement.Id, reason = new string('測', 501) },
            new { version = VersionOf(current), stage = "manager", reviewerId = replacement.Id, reason = "交接", applicantId = scenario.Owner.Id },
            new { version = VersionOf(current), stage = "manager", reviewerId = replacement.Id, reason = "交接", status = "approved" }
        })
            await AssertLimitedError(await scenario.Admin.PostAsJsonAsync($"{Path}/{Id(current)}", payload),
                HttpStatusCode.BadRequest, "invalid_input");
        Assert.True(JsonElement.DeepEquals(submitted, await Detail(owner, submitted)));
        Assert.True(JsonElement.DeepEquals(current, await Item(scenario.Admin, submitted)));

        // 250 個代理對恰好為 500 個 UTF-16 碼元；多一碼拒絕，同一版本在成功後也失效。
        var boundary = string.Concat(Enumerable.Repeat("🧾", 250));
        Assert.Equal(500, boundary.Length);
        await AssertLimitedError(await Replace(scenario.Admin, current, "manager", replacement.Id, boundary + "字"),
            HttpStatusCode.BadRequest, "invalid_input");
        await Expect(await Replace(scenario.Admin, current, "manager", replacement.Id, " " + boundary + " "), HttpStatusCode.NoContent);
        await AssertLimitedError(await Replace(scenario.Admin, current, "manager", replacement.Id), HttpStatusCode.Conflict, "stale_version");
        AssertReassignment((await Detail(owner, submitted)).GetProperty("history")[1], scenario, "manager",
            scenario.Manager, replacement, boundary, scenario.App.Clock.GetUtcNow());
    }

    // 從正式管理查詢取得目前有效資料，避免用寫入要求推斷已保存狀態。
    internal static Task<JsonElement> List(HttpClient admin) => admin.GetFromJsonAsync<JsonElement>(Path);

    // 清單會含其他測試建立的待辦，只以該案公開識別精確比對。
    internal static async Task<JsonElement> Item(HttpClient admin, JsonElement requisition) =>
        Assert.Single((await List(admin)).GetProperty("items").EnumerateArray(), item => Id(item) == Id(requisition));

    // 使用固定的正式請求形狀，允許測試重送相同版本或指定不存在的識別。
    internal static Task<HttpResponseMessage> Replace(HttpClient admin, JsonElement item, string stage, string reviewerId,
        string reason = "交接待辦", string? id = null) =>
        admin.PostAsJsonAsync($"{Path}/{id ?? Id(item).ToString()}", new { version = VersionOf(item), stage, reviewerId, reason });

    // 使用正式回應的穩定 UUID 比對清單及明細。
    private static Guid Id(JsonElement row) => row.GetProperty("id").GetGuid();

    // 錯誤只能包含公開欄位，訊息不可反射本案內容或惡意輸入。
    internal static async Task AssertLimitedError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        // 讀取有限本文後即釋放回應，避免並行測試保留無用資源。
        using (response)
        {
            // 比對公開代碼及有限的本文結構，不記錄回應中的請求資料。
            Assert.Equal(status, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            AssertKeys(body, "code", "message");
            Assert.Equal(code, body.GetProperty("code").GetString());
            var message = body.GetProperty("message").GetString();
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.DoesNotContain("只有業務可見", message);
            Assert.DoesNotContain("不能出現在管理回應", message);
            Assert.DoesNotContain("不得回顯", message);
        }
    }

    // 封閉 DTO 的實際 JSON 欄位，欄位順序不屬於契約。
    private static void AssertKeys(JsonElement entry, params string[] expected) =>
        Assert.Equal(expected.Order(), entry.EnumerateObject().Select(property => property.Name).Order());

    // 人員在管理與歷程皆只包含相同的穩定識別與名稱。
    internal static void AssertPerson(JsonElement person, RequisitionApprovalScenario.Business expected)
    {
        AssertKeys(person, "id", "userName");
        Assert.Equal(expected.Id, person.GetProperty("id").GetString());
        Assert.Equal(expected.Name, person.GetProperty("userName").GetString());
    }

    // 比對內容和送審部門，避免改派造成快照、金額或送審時間異動。
    private static void AssertContent(JsonElement before, JsonElement current)
    {
        // 固定請購內容與精確金額，另比對原送審部門、時間及輪次。
        foreach (var key in new[] { "subject", "purpose", "estimatedAmount", "lines" })
            Assert.True(JsonElement.DeepEquals(before.GetProperty(key), current.GetProperty(key)));
        foreach (var key in new[] { "department", "submittedAt", "number" })
            Assert.True(JsonElement.DeepEquals(before.GetProperty("round").GetProperty(key), current.GetProperty("round").GetProperty(key)));
    }

    // 精確核對操作人、時間、前後人選與原因，區分改派與一般決定的歷程。
    internal static void AssertReassignment(JsonElement entry, RequisitionApprovalScenario scenario, string stage,
        RequisitionApprovalScenario.Business previous, RequisitionApprovalScenario.Business next, string reason, DateTimeOffset time)
    {
        // 管理操作者須對應正式登入的帳號，而不是請求提供的其他識別。
        AssertKeys(entry, "roundNumber", "kind", "stage", "actor", "occurredAt", "reassignment");
        Assert.Equal("reassigned", entry.GetProperty("kind").GetString());
        Assert.Equal(stage, entry.GetProperty("stage").GetString());
        Assert.Equal(1, entry.GetProperty("roundNumber").GetInt32());
        var actor = entry.GetProperty("actor");
        AssertKeys(actor, "id", "userName");
        Assert.Equal(scenario.App.UserId, actor.GetProperty("id").GetString());
        Assert.Equal(scenario.App.UserName, actor.GetProperty("userName").GetString());
        Assert.Equal(time, entry.GetProperty("occurredAt").GetDateTimeOffset());

        // 每筆事件保留交易當時有效的原人選及新人選。
        var reassignment = entry.GetProperty("reassignment");
        AssertKeys(reassignment, "previousReviewer", "newReviewer", "reason");
        AssertPerson(reassignment.GetProperty("previousReviewer"), previous);
        AssertPerson(reassignment.GetProperty("newReviewer"), next);
        Assert.Equal(reason, reassignment.GetProperty("reason").GetString());
    }
}
