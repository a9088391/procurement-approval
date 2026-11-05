using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Procurement;
using Xunit;
using static OrganizationTests;
using static RequisitionApprovalTests;
using static RequisitionDraftTests;

// 由多部門及多種登入直接呼叫 HTTP，區分目前可見範圍與決定資格。
public sealed class RequisitionApprovalAuthorizationTests
{
    // 主管依目前部門或原指派，財務依實際進關，兼任者再合併本人草稿。
    [Fact]
    public async Task List_detail_and_history_follow_the_same_current_role_union()
    {
        // 配置同部門非指派主管、另一部門申請人及具雙重角色的業務帳號。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var manager = scenario.Manager.Client;
        var finance = scenario.Finance.Client;
        var localManager = await scenario.AddBusiness(scenario.Department.Id, manager: true);
        var dual = await scenario.AddBusiness(scenario.OtherDepartment.Id, manager: true, finance: true);
        var otherApplicant = await scenario.AddBusiness(scenario.OtherDepartment.Id);
        await scenario.Update(otherApplicant, edit => edit with
        {
            ManagerReviewerId = scenario.Manager.Id,
            FinanceReviewerId = scenario.Finance.Id
        });

        // 隔離庫跨案例保留資料；純財務帳號會合法取得先前案例已進入財務的單。
        var earlier = await finance.GetFromJsonAsync<JsonElement>(RequisitionsPath);
        var financeBaseline = earlier
            .EnumerateArray()
            .Select(ApprovalPath)
            .ToArray();

        // 同時準備本人草稿、主管待審、主管駁回、財務進關和另一部門待審。
        var ownDraft = await scenario.Draft("只給本人");
        var managerPending = await Act(owner, await scenario.Draft("主管待審"), "submit");
        var managerRejected = await Act(manager, await Act(owner, await scenario.Draft("主管駁回"), "submit"), "reject");
        var financePending = await Act(manager, await Act(owner, await scenario.Draft("財務待審"), "submit"), "approve");
        var otherDraft = await CreateDraft(otherApplicant.Client, new
        {
            subject = "另一部門",
            purpose = "補貨",
            lines = new[] { new { itemName = "紙", unit = "箱", quantity = 1, unitPrice = 2.15m } }
        });
        var otherPending = await Act(otherApplicant.Client, otherDraft, "submit");
        var dualDraft = await CreateDraft(dual.Client, new { subject = "兼任者自己編輯" });

        // 逐一核對整份清單及每張可見單的明細歷程，財務看不到主管駁回。
        await AssertVisible(owner, [ownDraft, managerPending, managerRejected, financePending]);
        await AssertVisible(localManager.Client, [managerPending, managerRejected, financePending]);
        await AssertVisible(manager, [managerPending, managerRejected, financePending, otherPending]);
        await AssertVisible(finance, [financePending], financeBaseline);
        await AssertVisible(dual.Client, [dualDraft, otherPending, financePending], financeBaseline);
        await AssertInvisible(localManager.Client, ownDraft);
        await AssertInvisible(localManager.Client, otherPending);
        await AssertInvisible(finance, managerRejected);
        await AssertInvisible(dual.Client, managerPending);
        await AssertInvisible(manager, dualDraft);

        // 看得到的非指派主管不能代簽或代送；正被指派的主管仍可跨部門執行當關。
        AssertStatus(await Detail(localManager.Client, managerPending), "manager_pending", false, false);
        AssertStatus(await Detail(manager, managerPending), "manager_pending", false, true);
        await Expect(await Post(localManager.Client, managerPending, "approve"), HttpStatusCode.Forbidden, "forbidden");
        await Expect(await Post(localManager.Client, managerPending, "submit"), HttpStatusCode.Forbidden, "forbidden");
        await Expect(await Post(dual.Client, otherPending, "reject"), HttpStatusCode.Forbidden, "forbidden");
        await Expect(await Post(dual.Client, financePending, "approve"), HttpStatusCode.Forbidden, "forbidden");

        // 變更部門及撤掉角色後沿用原 Cookie，權限立刻跟隨現況。
        await scenario.Update(localManager, edit => edit with { DepartmentId = scenario.OtherDepartment.Id });
        await AssertVisible(localManager.Client, [otherPending]);
        await AssertInvisible(localManager.Client, managerPending);
        await scenario.Update(dual, edit => edit with { IsManager = false });
        await AssertVisible(dual.Client, [dualDraft, financePending], financeBaseline);
        await AssertInvisible(dual.Client, otherPending);
        Assert.True(JsonElement.DeepEquals(managerPending, await Detail(owner, managerPending)));
    }

    // 未登入、管理與臨時密碼登入均不能讀寫；這些拒絕不會改變已保存的單。
    [Fact]
    public async Task Anonymous_admin_and_password_setup_sessions_cannot_access_approval()
    {
        // 準備合格草稿與確實具有主管角色但尚未改密碼的帳號。
        await using var scenario = await RequisitionApprovalScenario.Start();
        using var anonymous = scenario.App.Client();
        var initial = await scenario.AddBusiness(scenario.Department.Id, manager: true, ready: false);
        var owner = scenario.Owner.Client;
        var draft = await scenario.Draft();
        var saved = await Detail(owner, draft);

        // 直接讀取清單、明細及呼叫所有寫入，確保各角色在同一授權邊界被擋住。
        await AssertDenied(anonymous, draft, HttpStatusCode.Unauthorized, "unauthenticated");
        await AssertDenied(scenario.Admin, draft, HttpStatusCode.Forbidden, "forbidden");
        await AssertDenied(initial.Client, draft, HttpStatusCode.Forbidden, "forbidden");
        await Expect(await scenario.Admin.PostAsJsonAsync(DraftsPath, new { subject = "管理員代提" }),
            HttpStatusCode.Forbidden, "forbidden");
        Assert.True(JsonElement.DeepEquals(saved, await Detail(owner, draft)));

        // 指派者仍具啟用及角色資格，所以可送審；該帳號仍需先改密碼才能作出決定。
        await scenario.Update(scenario.Owner, edit => edit with { ManagerReviewerId = initial.Id });
        var submitted = await Act(owner, draft, "submit");
        AssertStatus(submitted, "manager_pending", false, false);
        await Expect(await Post(initial.Client, submitted, "approve"), HttpStatusCode.Forbidden, "forbidden");
        Assert.True(JsonElement.DeepEquals(submitted, await Detail(owner, draft)));
    }

    // 同一 Cookie 容器切換帳號後，舊頁不能藉一般清單或明細讀取新身分資料。
    [Fact]
    public async Task General_reads_check_supplied_cross_tab_token_and_allow_cookie_only()
    {
        // 保留 A 權杖，登出後以同一個 HTTP client 登入 B。
        const string header = "X-CSRF-TOKEN";
        await using var scenario = await RequisitionApprovalScenario.Start();
        using var browser = await scenario.App.SignedIn(scenario.Manager.Name, scenario.Manager.Password);
        var stale = browser.DefaultRequestHeaders.GetValues(header).Single();
        await Expect(await browser.PostAsync("/api/auth/logout", null), HttpStatusCode.NoContent);
        await Expect(await Harness.Login(browser, scenario.Owner.Name, scenario.Owner.Password), HttpStatusCode.NoContent);
        await Harness.Token(browser);
        var draft = await scenario.Draft();
        var paths = new[] { RequisitionsPath, ApprovalPath(draft) };
        var matched = new List<JsonElement>();
        foreach (var path in paths)
            matched.Add(await browser.GetFromJsonAsync<JsonElement>(path));

        // 只換成 A 標頭，B 的有效 Cookie 對兩個讀取入口及寫入都得到防偽錯誤。
        browser.DefaultRequestHeaders.Remove(header);
        browser.DefaultRequestHeaders.Add(header, stale);
        foreach (var path in paths)
            await Expect(await browser.GetAsync(path), HttpStatusCode.BadRequest, "invalid_csrf");
        await Expect(await Post(browser, draft, "submit"), HttpStatusCode.BadRequest, "invalid_csrf");

        // 只用 Cookie 時讀回相同內容，但沒有權杖仍不可送審。
        browser.DefaultRequestHeaders.Remove(header);
        for (var i = 0; i < paths.Length; i++)
            Assert.True(JsonElement.DeepEquals(matched[i], await browser.GetFromJsonAsync<JsonElement>(paths[i])));
        await Expect(await Post(browser, draft, "submit"), HttpStatusCode.BadRequest, "invalid_csrf");
        AssertStatus(await Detail(scenario.Owner.Client, draft), "draft", false, false);
    }

    // 重設密碼後舊 Cookie 已失效，舊權杖不可蓋過登入失效或影響歷程。
    [Fact]
    public async Task Password_reset_revokes_existing_approval_sessions()
    {
        // 確認當關的人原本有決定資格，再從正式管理入口重設密碼。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var manager = scenario.Manager.Client;
        var submitted = await Act(scenario.Owner.Client, await scenario.Draft(), "submit");
        AssertStatus(await Detail(manager, submitted), "manager_pending", false, true);
        var target = (await Read(scenario.Admin)).Accounts.Single(a => a.Id == scenario.Manager.Id);
        var temporary = TemporaryPassword();
        await Expect(await scenario.Admin.PostAsJsonAsync($"{AccountsPath}/{target.Id}/reset-password",
            new { version = target.Version, newPassword = temporary }), HttpStatusCode.NoContent);

        // 舊 Cookie 不能重播，新臨時登入先完成改密碼，才可用原先的指派繼續。
        await AssertDenied(manager, submitted, HttpStatusCode.Unauthorized, "unauthenticated");
        using var setup = await scenario.App.SignedIn(target.UserName, temporary);
        await AssertDenied(setup, submitted, HttpStatusCode.Forbidden, "forbidden");
        Assert.True(JsonElement.DeepEquals(submitted, await Detail(scenario.Owner.Client, submitted)));
        var password = TemporaryPassword();
        await Expect(await setup.PostAsJsonAsync("/api/auth/change-password",
            new PasswordRequest(temporary, password, (await Harness.Me(setup)).Version)), HttpStatusCode.NoContent);
        using var fresh = await scenario.App.SignedIn(target.UserName, password);
        AssertStatus(await Act(fresh, submitted, "approve"), "finance_pending", false, false);
    }

    // 閒置登入到期時，舊 Cookie 和權杖不再能讀取或提交已保存草稿。
    [Fact]
    public async Task Idle_expiry_prevents_reads_and_writes_until_a_new_login()
    {
        // 保存草稿後不再活動，使用測試時鐘走到已核准的三十分鐘界線。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var draft = await scenario.Draft();
        scenario.App.Clock.Advance(TimeSpan.FromMinutes(30));
        await AssertDenied(scenario.Owner.Client, draft, HttpStatusCode.Unauthorized, "unauthenticated");

        // 同一帳號重新登入後可讀回原版草稿，並由使用者明確送審。
        using var fresh = await scenario.App.SignedIn(scenario.Owner.Name, scenario.Owner.Password);
        Assert.True(JsonElement.DeepEquals(draft, await ReadDraft(fresh, draft)));
        AssertStatus(await Act(fresh, draft, "submit"), "manager_pending", false, false);
    }

    // 比對清單完整集合，並從單筆讀取核對授權及歷程未被省略。
    private static async Task AssertVisible(HttpClient client, JsonElement[] expected, string[]? baseline = null)
    {
        // 清單與明細共用公開識別，不靠單號或傳回順序推測可見結果。
        var list = await client.GetFromJsonAsync<JsonElement>(RequisitionsPath);
        var expectedPaths = expected
            .Select(ApprovalPath)
            .Concat(baseline ?? [])
            .Order();
        var visiblePaths = list
            .EnumerateArray()
            .Select(ApprovalPath)
            .Order();
        Assert.Equal(expectedPaths, visiblePaths);
        foreach (var item in expected)
        {
            // 讀回與狀態及歷程相符的資料，檢查列表和明細在同一個授權範圍。
            var detail = await Detail(client, item);
            Assert.Equal(item.GetProperty("status").GetString(), detail.GetProperty("status").GetString());
            var count = item.TryGetProperty("history", out var history) ? history.GetArrayLength() : 0;
            Assert.Equal(count, detail.GetProperty("history").GetArrayLength());
        }
    }

    // 已存在但越權和不存在的單須有相同的完整錯誤，避免知道識別就能枚舉。
    private static async Task AssertInvisible(HttpClient client, JsonElement hidden)
    {
        // 讀取及三個寫入都帶可通過本文驗證的格式，核對相同狀態、代碼及訊息。
        var version = new { version = VersionOf(hidden) };
        foreach (var action in new[] { "", "/submit", "/approve", "/reject" })
        {
            var target = ApprovalPath(hidden) + action;
            var missing = $"{RequisitionsPath}/{Guid.NewGuid()}{action}";
            using var actual = action == "" ? await client.GetAsync(target) : await client.PostAsJsonAsync(target, version);
            using var absent = action == "" ? await client.GetAsync(missing) : await client.PostAsJsonAsync(missing, version);
            Assert.Equal(HttpStatusCode.NotFound, actual.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
            var error = await actual.Content.ReadFromJsonAsync<ErrorResponse>();
            Assert.Equal("not_found", error?.Code);
            Assert.Equal(error, await absent.Content.ReadFromJsonAsync<ErrorResponse>());
        }
    }

    // 驗證相同身分限制涵蓋清單、含歷程的明細及所有寫入。
    private static async Task AssertDenied(HttpClient client, JsonElement draft, HttpStatusCode status, string code)
    {
        await Expect(await client.GetAsync(RequisitionsPath), status, code);
        await Expect(await client.GetAsync(ApprovalPath(draft)), status, code);
        foreach (var action in new[] { "submit", "approve", "reject" })
            await Expect(await Post(client, draft, action), status, code);
    }
}
