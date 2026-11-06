using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using Procurement;
using Xunit;
using static OrganizationTests;
using static RequisitionApprovalTests;
using static RequisitionDraftTests;

// 用正式 HTTP 與 PostgreSQL 核對三個查詢入口，斷言本案的已知單號、狀態與金額。
public sealed class RequisitionReportingTests
{
    private const string StatisticsPath = RequisitionsPath + "/statistics";
    private const string CsvPath = RequisitionsPath + "/csv";
    private static readonly string[] ReportPaths = [RequisitionsPath, StatisticsPath, CsvPath];

    // 一般清單保留本人草稿；送審清單與統計只計入實際送審的資料。
    [Fact]
    public async Task Submitted_reports_exclude_owner_drafts_and_withdrawn_requisitions()
    {
        // 從正式入口保存草稿與送審單，用固定小數核對資料來源。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var draft = await scenario.Draft("不納入統計");
        scenario.App.Clock.Advance(TimeSpan.FromSeconds(1));
        var withdrawn = await Act(owner, await Act(owner, await scenario.Draft("撤回修改"), "submit"), "withdraw");
        scenario.App.Clock.Advance(TimeSpan.FromSeconds(1));
        var submitted = await Act(owner, await scenario.Draft("納入統計"), "submit");

        // 一般清單維持 JSON 陣列、狀態及原排序；報表仍排除曾送審但已撤回的單。
        var original = await owner.GetFromJsonAsync<JsonElement>(RequisitionsPath);
        Assert.Collection(original.EnumerateArray(),
            row => Assert.Equal(ApprovalPath(submitted), ApprovalPath(row)),
            row => Assert.Equal(ApprovalPath(withdrawn), ApprovalPath(row)),
            row => Assert.Equal(ApprovalPath(draft), ApprovalPath(row)));
        var list = await AssertReport(owner, "", new ExpectedRow(submitted, scenario.Department, "manager_pending", 0.87m));
        Assert.True(JsonElement.DeepEquals(original[0], Assert.Single(list)));
    }

    // 以臺灣日界線前後一微秒的已知送審資料，驗證含起點、含整個迄日及排除翌日。
    [Fact]
    public async Task Taiwan_date_filters_use_latest_submission_and_inclusive_days()
    {
        // 正式時鐘從 11/05 00:00 UTC 起，推到 11/06 臺灣零時前一微秒後重新登入。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var microsecond = TimeSpan.FromTicks(10);
        scenario.App.Clock.Advance(TimeSpan.FromHours(16) - microsecond);
        using var firstSession = await scenario.App.SignedIn(scenario.Owner.Name, scenario.Owner.Password);
        var before = await Submit(firstSession, "起點前", 0.05m);
        scenario.App.Clock.Advance(microsecond);
        var start = await Submit(firstSession, "起日零時", 0.11m);

        // 模擬隔一天的合法新登入，避免靠回撥時鐘或失效 Cookie 建立測試資料。
        scenario.App.Clock.Advance(TimeSpan.FromDays(1) - microsecond);
        using var owner = await scenario.App.SignedIn(scenario.Owner.Name, scenario.Owner.Password);
        var last = await Submit(owner, "迄日最後一微秒", 0.22m);
        scenario.App.Clock.Advance(microsecond);
        var next = await Submit(owner, "翌日零時", 0.44m);

        // 不加 scope 時任一已知篩選仍採送審範圍，起迄分開與合併使用都有明確結果。
        var department = scenario.Department;
        var beforeRow = new ExpectedRow(before, department, "manager_pending", 0.05m);
        var startRow = new ExpectedRow(start, department, "manager_pending", 0.11m);
        var lastRow = new ExpectedRow(last, department, "manager_pending", 0.22m);
        var nextRow = new ExpectedRow(next, department, "manager_pending", 0.44m);
        const string day = "?startDate=2026-11-06&endDate=2026-11-06";
        await AssertReport(owner, day, startRow, lastRow);
        await AssertReport(owner, "?startDate=2026-11-06", startRow, lastRow, nextRow);
        await AssertReport(owner, "?endDate=2026-11-06", beforeRow, startRow, lastRow);

        // 直接核對 CSV 的送審瞬間、時差與小數格式，並核對固定資料的精確合計。
        var csv = await ReadCsv(owner, day);
        Assert.Collection(csv,
            row => Assert.Equal("2026-11-06T23:59:59.9999990+08:00", row[6]),
            row => Assert.Equal("2026-11-06T00:00:00.0000000+08:00", row[6]));
        var statistics = await owner.GetFromJsonAsync<JsonElement>(StatisticsPath + day);
        var group = Assert.Single(statistics.GetProperty("groups").EnumerateArray());
        Assert.Equal(0.33m, group.GetProperty("estimatedAmount").GetDecimal());

        // 迄日之後才作出的決定仍顯示目前狀態，日期不會將報表還原成當時畫面。
        using var manager = await scenario.App.SignedIn(scenario.Manager.Name, scenario.Manager.Password);
        var advanced = await Act(manager, start, "approve");
        await AssertReport(owner, day, startRow with { Saved = advanced, Status = "finance_pending" }, lastRow);
    }

    // 調部門不移動歷史歸屬；更名時穩定識別不拆分件數，重送只依新輪次計入一次。
    [Fact]
    public async Task Department_rename_move_and_resubmission_keep_snapshot_semantics()
    {
        // 在同一部門名稱變更前後分別送審；另準備一張可撤回重送的單。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var department = scenario.Department;
        var first = await Act(scenario.Finance.Client,
            await Act(scenario.Manager.Client, await Submit(owner, "更名前送審", 0.10m), "approve"), "approve");
        var moved = await Submit(owner, "將調整申請人的部門", 0.27m);
        var renamed = department with { Name = "更名 " + department.Name };
        await Expect(await scenario.Admin.PostAsJsonAsync($"{DepartmentsPath}/{department.Id}",
            new { name = renamed.Name, active = true, version = department.Version }), HttpStatusCode.NoContent);
        scenario.App.Clock.Advance(TimeSpan.FromSeconds(1));
        var latest = await Submit(owner, "更名後送審", 0.20m);

        // 即使申請人已改部門，舊部門仍看見原輪；兩個快照名稱仍是同一統計群組。
        await scenario.Update(scenario.Owner, edit => edit with { DepartmentId = scenario.OtherDepartment.Id });
        var firstRow = new ExpectedRow(first, department, "approved", 0.10m);
        var movedRow = new ExpectedRow(moved, department, "manager_pending", 0.27m);
        var latestRow = new ExpectedRow(latest, renamed, "manager_pending", 0.20m);
        await AssertReport(owner, $"?departmentId={department.Id}", firstRow, movedRow, latestRow);
        await AssertReport(owner, $"?departmentId={scenario.OtherDepartment.Id}");
        var statistics = await owner.GetFromJsonAsync<JsonElement>(StatisticsPath + $"?departmentId={department.Id}");
        var group = Assert.Single(statistics.GetProperty("groups").EnumerateArray(), g => g.GetProperty("status").GetString() == "manager_pending");
        Assert.Equal(renamed.Name, RequiredText(group.GetProperty("department"), "name"));
        Assert.Equal(0.47m, group.GetProperty("estimatedAmount").GetDecimal());
        await AssertReport(owner, $"?departmentId={department.Id}&status=approved", firstRow);

        // 撤回期間不列入；重送跨臺灣日期後，只出現在新部門和新日期，保留原識別與舊輪內容。
        var withdrawn = await Act(owner, moved, "withdraw");
        await AssertReport(owner, "?startDate=2026-11-05&endDate=2026-11-05", firstRow, latestRow);
        using var save = await owner.PostAsJsonAsync(PathOf(withdrawn), new
        {
            version = VersionOf(withdrawn),
            subject = "新部門重送",
            purpose = "修改後重送",
            lines = new[] { new { itemName = "紙", unit = "包", quantity = 2, unitPrice = 0.17m } }
        });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var edited = await save.Content.ReadFromJsonAsync<JsonElement>();
        scenario.App.Clock.Advance(TimeSpan.FromHours(16));
        using var renewed = await scenario.App.SignedIn(scenario.Owner.Name, scenario.Owner.Password);
        var resubmitted = await Act(renewed, edited, "submit");
        Assert.Equal(ApprovalPath(moved), ApprovalPath(resubmitted));
        var round = resubmitted.GetProperty("round");
        Assert.Equal(2, round.GetProperty("number").GetInt32());
        var oldRound = Assert.Single(resubmitted.GetProperty("previousRounds").EnumerateArray());
        Assert.Equal(0.27m, oldRound.GetProperty("estimatedAmount").GetDecimal());
        await AssertReport(renewed, "?endDate=2026-11-05", firstRow, latestRow);
        await AssertReport(renewed, $"?startDate=2026-11-06&departmentId={scenario.OtherDepartment.Id}",
            new ExpectedRow(resubmitted, scenario.OtherDepartment, "manager_pending", 0.34m));
    }

    // 目前業務角色及有效指派控制三個報表入口，暫停仍屬原待審狀態。
    [Fact]
    public async Task Reports_follow_current_role_union_assignment_and_paused_status()
    {
        // 分離部門主管、跨部門指派主管、候選接手人、財務與兼任者的授權來源。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var owner = scenario.Owner.Client;
        var manager = scenario.Manager.Client;
        var finance = scenario.Finance.Client;
        var department = scenario.Department;
        var other = scenario.OtherDepartment;
        var local = await scenario.AddBusiness(department.Id, manager: true);
        var replacement = await scenario.AddBusiness(other.Id, manager: true);
        var dual = await scenario.AddBusiness(department.Id, manager: true, finance: true);
        await scenario.Update(dual, edit => edit with { ManagerReviewerId = scenario.Manager.Id, FinanceReviewerId = scenario.Finance.Id });
        var dualOwn = await Submit(dual.Client, "兼任人本人單", 0.31m);
        await scenario.Update(dual, edit => edit with { DepartmentId = other.Id });
        var otherApplicant = await scenario.AddBusiness(other.Id);
        await scenario.Update(otherApplicant, edit => edit with { ManagerReviewerId = replacement.Id, FinanceReviewerId = scenario.Finance.Id });
        var otherPending = await Submit(otherApplicant.Client, "另一部門待審", 0.51m);

        // 準備四種目前狀態；主管駁回並未進財務，不能被純財務或跨部門兼任者取得。
        var pending = await Submit(owner, "主管待審", 0.12m);
        var rejected = await Act(manager, await Submit(owner, "主管駁回", 0.23m), "reject");
        var financePending = await Act(manager, await Submit(owner, "財務待審", 0.34m), "approve");
        var approved = await Act(finance, await Act(manager, await Submit(owner, "財務核准", 0.45m), "approve"), "approve");
        var pendingRow = new ExpectedRow(pending, department, "manager_pending", 0.12m);
        var dualRow = new ExpectedRow(dualOwn, department, "manager_pending", 0.31m);
        var rejectedRow = new ExpectedRow(rejected, department, "rejected", 0.23m);
        var financeRow = new ExpectedRow(financePending, department, "finance_pending", 0.34m);
        var approvedRow = new ExpectedRow(approved, department, "approved", 0.45m);
        var filter = $"?departmentId={department.Id}";
        await AssertReport(owner, filter, pendingRow, rejectedRow, financeRow, approvedRow);
        await AssertReport(local.Client, filter, pendingRow, dualRow, rejectedRow, financeRow, approvedRow);
        await AssertReport(manager, filter, pendingRow, dualRow, rejectedRow, financeRow, approvedRow);
        await AssertReport(finance, filter, financeRow, approvedRow);
        await AssertReport(dual.Client, filter, dualRow, financeRow, approvedRow);
        await AssertReport(dual.Client, $"?departmentId={other.Id}", new ExpectedRow(otherPending, other, "manager_pending", 0.51m));
        await AssertReport(replacement.Client, filter);
        await AssertReport(owner, filter + "&status=approved", approvedRow);

        // 撤權立即隱藏舊主管的跨部門資料；仍可見的暫停單保持原主管待審分類。
        await scenario.Update(scenario.Manager, edit => edit with { IsManager = false });
        var waiting = filter + "&status=manager_pending";
        await AssertReport(manager, waiting);
        await AssertReport(local.Client, waiting, pendingRow with { Paused = true }, dualRow with { Paused = true });
        await Expect(await RequisitionReassignmentTests.Replace(scenario.Admin,
            await RequisitionReassignmentTests.Item(scenario.Admin, pending), "manager", replacement.Id), HttpStatusCode.NoContent);

        // 接手者取得原待審單；舊人即使恢復資格，也只能依仍有效的指派或部門讀取。
        await AssertReport(replacement.Client, waiting, pendingRow);
        await scenario.Update(scenario.Manager, edit => edit with { IsManager = true });
        await AssertReport(manager, waiting, dualRow);
        await scenario.Update(dual, edit => edit with { IsManager = false, IsFinance = false });
        await AssertReport(dual.Client, filter, dualRow);
        await AssertReport(dual.Client, $"?departmentId={other.Id}");

        // 停用財務立刻撤銷登入；既有財務待審仍留在財務狀態，不被算成結案或新關卡。
        await scenario.Update(scenario.Finance, edit => edit with { Active = false });
        await AssertError(finance, filter, HttpStatusCode.Unauthorized, "unauthenticated");
        await AssertReport(owner, filter + "&status=finance_pending", financeRow with { Paused = true });
        await AssertReport(owner, filter + "&status=approved", approvedRow);
    }

    // 三入口使用同一驗證，非法輸入及未授權回應不帶受保護資料或反射請求值。
    [Fact]
    public async Task Reports_reject_invalid_filters_and_apply_authorization_before_filter_errors()
    {
        // 準備本案可見資料、另一個無角色帳號及未完成密碼設定的合法登入。
        await using var scenario = await RequisitionApprovalScenario.Start();
        using var anonymous = scenario.App.Client();
        var owner = scenario.Owner.Client;
        var other = await scenario.AddBusiness(scenario.OtherDepartment.Id);
        var initial = await scenario.AddBusiness(scenario.Department.Id, manager: true, ready: false);
        var submitted = await Submit(owner, "不能經錯誤回應取得", 0.17m);
        var known = $"?scope=submitted&departmentId={scenario.Department.Id.ToUpperInvariant()}";
        await AssertReport(owner, known, new ExpectedRow(submitted, scenario.Department, "manager_pending", 0.17m));
        await AssertReport(other.Client, known);
        await AssertReport(owner, $"?departmentId={Guid.NewGuid():N}");

        // 可表示的極端界線與閏日仍可查詢，錯誤的邊界、空值、重複及非標準格式一律拒絕。
        await AssertReport(owner, "?startDate=0001-01-02&endDate=9999-12-30", new ExpectedRow(submitted, scenario.Department, "manager_pending", 0.17m));
        await AssertReport(owner, "?startDate=2024-02-29&endDate=2024-02-29");
        foreach (var invalid in new[]
        {
            "scope=", "scope=all", "scope=submitted&scope=submitted",
            "startDate=", "startDate=2026-11-5", "startDate=2026-11-05%20", "startDate=2026-02-29",
            "startDate=2026-11-05T00%3A00", "startDate=2026-11-05&STARTDATE=2026-11-05", "startDate=0001-01-01",
            "endDate=", "endDate=2026%2F11%2F05", "endDate=2026-11-05&endDate=2026-11-05", "endDate=9999-12-31",
            "startDate=2026-11-06&endDate=2026-11-05", "departmentId=", "departmentId=private%00value",
            $"departmentId={Guid.NewGuid():D}", $"departmentId={scenario.Department.Id}&departmentId={scenario.Department.Id}",
            "status=", "status=draft", "status=withdrawn", "status=manager_pending%2Capproved", "status=approved&status=approved"
        })
            await AssertError(owner, "?" + invalid, HttpStatusCode.BadRequest, "invalid_input");

        // 沒有資格時有效或非法查詢均給同一身分錯誤；過期頁面也不能用另一人的權杖下載資料。
        foreach (var query in new[] { known, "?status=private" })
        {
            // 不以格式錯誤取代身分驗證，涵蓋三種無法讀報表的帳號狀態。
            await AssertError(anonymous, query, HttpStatusCode.Unauthorized, "unauthenticated");
            await AssertError(scenario.Admin, query, HttpStatusCode.Forbidden, "forbidden");
            await AssertError(initial.Client, query, HttpStatusCode.Forbidden, "forbidden");
        }
        const string header = "X-CSRF-TOKEN";
        using var browser = await scenario.App.SignedIn(other.Name, other.Password);
        var stale = browser.DefaultRequestHeaders.GetValues(header).Single();
        await Expect(await browser.PostAsync("/api/auth/logout", null), HttpStatusCode.NoContent);
        await Expect(await Harness.Login(browser, scenario.Owner.Name, scenario.Owner.Password), HttpStatusCode.NoContent);
        await Harness.Token(browser);
        browser.DefaultRequestHeaders.Remove(header);
        browser.DefaultRequestHeaders.Add(header, stale);
        await AssertError(browser, known, HttpStatusCode.BadRequest, "invalid_csrf");
        browser.DefaultRequestHeaders.Remove(header);
        await AssertReport(browser, known, new ExpectedRow(submitted, scenario.Department, "manager_pending", 0.17m));
    }

    // 讓標準 CSV 剖析器讀取實際下載，驗證危險字首、逗號、引號及換行不破壞欄位或原始值。
    [Fact]
    public async Task Csv_escapes_spreadsheet_formulas_and_preserves_source_text()
    {
        // 帳號、部門與主旨都來自既有寫入端點，三個欄位分別包含可成公式的文字。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var department = scenario.Department;
        const string departmentName = "＋部門,\"中文\"";
        var applicantName = $"@申請人,\"中文\"-{Guid.NewGuid():N}";
        await Expect(await scenario.Admin.PostAsJsonAsync($"{DepartmentsPath}/{department.Id}",
            new { name = departmentName, active = true, version = department.Version }), HttpStatusCode.NoContent);
        var temporary = TemporaryPassword();
        var applicant = await CreateAccount(scenario.Admin, department.Id, temporary, applicantName);
        using var setup = await scenario.App.SignedIn(applicant.UserName, temporary);
        var password = TemporaryPassword();
        await Expect(await setup.PostAsJsonAsync("/api/auth/change-password",
            new PasswordRequest(temporary, password, (await Harness.Me(setup)).Version)), HttpStatusCode.NoContent);
        var business = new RequisitionApprovalScenario.Business(applicant.Id, applicantName, password, setup);
        await scenario.Update(business, edit => edit with { ManagerReviewerId = scenario.Manager.Id, FinanceReviewerId = scenario.Finance.Id });
        using var owner = await scenario.App.SignedIn(applicantName, password);

        // 逐一留下 ASCII／全形、Tab、空白及控制字元字首；普通含逗號與引號的文字保持原樣。
        var rows = new List<(JsonElement Saved, string Subject, string Exported)>();
        const string normal = "請購,\"中文\"";
        rows.Add((await Submit(owner, normal, 0.10m), normal, normal));
        foreach (var formula in new[]
        {
            "=1+1", "+1", "-1", "@SUM(1,2)", "＝1+1", "＋1", "－1", "＠SUM(1,2)",
            "\t普通文字", " \u3000=1+1", "\u0001\u200B＠SUM(1,2)"
        })
            rows.Add((await Submit(owner, formula, 0.10m), formula, "'" + formula));

        // 正式 API 拒絕單行欄位的 CR/LF；只在隔離測試庫模擬既有資料，保留主檔與輪次一致。
        const string legacy = "\r\n=\"甲,乙\"\r\n第二行";
        var original = await Submit(owner, "待模擬舊資料的單", 0.10m);
        var legacyId = original.GetProperty("id").GetGuid();
        await scenario.App.WithDb(async db =>
        {
            // 只在隔離庫更新本案例經正式 HTTP 建立的單與該輪快照，兩處保持一致。
            Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH updated AS (
                    UPDATE "Requisitions" SET "Subject" = {legacy} WHERE "Id" = {legacyId} RETURNING "Id"
                )
                UPDATE "RequisitionRounds" SET "Content" = jsonb_set("Content", ARRAY['subject'], to_jsonb({legacy}::text))
                WHERE "RequisitionId" IN (SELECT "Id" FROM updated) AND "Number" = 1
                """));
        });
        rows.Add((original, legacy, "'" + legacy));

        // 解析出的每列精確核對單號與七個欄位；匯出後用正式明細核對原文未被改寫。
        var csv = await ReadCsv(owner, "");
        Assert.Equal(rows.Count, csv.Length);
        foreach (var (saved, subject, exported) in rows)
        {
            // 單號為正式入口產生的穩定識別，每筆只出現一次。
            var number = saved.GetProperty("number").GetString();
            var cells = Assert.Single(csv, cell => cell[0] == number);
            Assert.Equal(new[] { number, exported, "'" + applicantName, "'" + departmentName, "主管待審", "0.10",
                "2026-11-05T08:00:00.0000000+08:00" }, cells);
            var current = await Detail(owner, saved);
            var snapshot = current.GetProperty("round").GetProperty("department");
            Assert.Equal(subject, current.GetProperty("subject").GetString());
            Assert.Equal(applicantName, RequiredText(current.GetProperty("applicant"), "userName"));
            Assert.Equal(departmentName, RequiredText(snapshot, "name"));
        }
    }

    // 透過正式草稿及送審入口準備已知金額；傳送前核對原始 JSON 真的包含測試所需文字。
    private static async Task<JsonElement> Submit(HttpClient owner, string subject, decimal amount)
    {
        // 同一份 JSON 先驗證字串往返，再由 HTTP 寫入及送審。
        using var body = JsonContent.Create(new
        {
            subject,
            purpose = "不應出現在匯出欄位\n用途",
            lines = new[] { new { itemName = "紙", unit = "包", quantity = 1, unitPrice = amount } }
        });
        using var json = JsonDocument.Parse(await body.ReadAsStringAsync());
        Assert.Equal(subject, json.RootElement.GetProperty("subject").GetString());
        using var saved = await owner.PostAsync(DraftsPath, body);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        return await Act(owner, await saved.Content.ReadFromJsonAsync<JsonElement>(), "submit");
    }

    // 三個輸出均與已知資料核對，再比對清單與 CSV 的順序，避免只驗證同一個錯誤結果相等。
    private static async Task<JsonElement[]> AssertReport(HttpClient client, string query, params ExpectedRow[] expected)
    {
        // 比對送審清單的識別、分類、金額及暫停；無報表篩選時只對清單明確指定範圍。
        var listQuery = query == "" ? "?scope=submitted" : query;
        var result = await client.GetFromJsonAsync<JsonElement>(RequisitionsPath + listQuery);
        var list = result.EnumerateArray().ToArray();
        Assert.Equal(expected.Select(row => ApprovalPath(row.Saved)).Order(), list.Select(ApprovalPath).Order());
        foreach (var item in expected)
        {
            // 從同一份已完成的 HTTP 回應找回本案例預期的唯一一張單。
            var row = Assert.Single(list, entry => ApprovalPath(entry) == ApprovalPath(item.Saved));
            Assert.Equal(item.Status, row.GetProperty("status").GetString());
            Assert.Equal(item.Amount, row.GetProperty("estimatedAmount").GetDecimal());
            Assert.Equal(item.Paused, row.GetProperty("paused").GetBoolean());
            var snapshot = row.GetProperty("round").GetProperty("department");
            Assert.Equal(item.Department.Id, snapshot.GetProperty("id").GetString());
            Assert.Equal(item.Department.Name, snapshot.GetProperty("name").GetString());
        }

        // 用已知資料建立預期群組，逐筆比較部門、狀態、件數與金額，不從列表反推統計。
        var stats = await client.GetFromJsonAsync<JsonElement>(StatisticsPath + query);
        var groups = stats
            .GetProperty("groups")
            .EnumerateArray()
            .Select(group => (
                Department: RequiredText(group.GetProperty("department"), "id"),
                Name: RequiredText(group.GetProperty("department"), "name"),
                Status: RequiredText(group, "status"),
                Count: group.GetProperty("count").GetInt32(),
                Amount: group.GetProperty("estimatedAmount").GetDecimal()))
            .Order();
        var expectedGroups = expected
            .GroupBy(row => row.Department.Id)
            .SelectMany(department =>
            {
                // 最新匹配快照名稱用於同一部門的所有群組，名稱改動不拆分統計。
                var label = department.MaxBy(row => SubmittedAt(row.Saved)) ?? throw new InvalidOperationException();
                return department
                    .GroupBy(row => row.Status)
                    .Select(status => (department.Key, label.Department.Name, status.Key, status.Count(), status.Sum(row => row.Amount)));
            })
            .Order();
        Assert.Equal(expectedGroups, groups);

        // 清單順序固定，CSV 不多列也不重複，並保留同一張單的送審部門、金額和狀態。
        var csv = await ReadCsv(client, query);
        Assert.Equal(list.Select(row => row.GetProperty("number").GetString()), csv.Select(cells => cells[0]));
        foreach (var item in expected)
        {
            // CSV 只公開單號，所以用已知單號獨立比對，並檢查標籤而非內部狀態碼。
            var number = item.Saved.GetProperty("number").GetString();
            var cells = Assert.Single(csv, cell => cell[0] == number);
            Assert.Equal(item.Saved.GetProperty("subject").GetString(), cells[1]);
            Assert.Equal(RequiredText(item.Saved.GetProperty("applicant"), "userName"), cells[2]);
            Assert.Equal(item.Department.Name, cells[3]);
            Assert.Equal(StatusLabel(item.Status), cells[4]);
            Assert.Equal(item.Amount.ToString("F2", CultureInfo.InvariantCulture), cells[5]);
            Assert.Equal(SubmittedAt(item.Saved), DateTimeOffset.ParseExact(cells[6], "O", CultureInfo.InvariantCulture));
        }
        return list;
    }

    // 僅對測試中須存在的公開欄位取字串，避免缺值被視為合法結果。
    private static string RequiredText(JsonElement element, string property) =>
        element.GetProperty(property).GetString() ?? throw new InvalidOperationException($"缺少欄位 {property}");

    // 統計名稱與匯出日期都以正式送審回應中保存的時間為基準。
    private static DateTimeOffset SubmittedAt(JsonElement saved) => saved
        .GetProperty("round")
        .GetProperty("submittedAt")
        .GetDateTimeOffset();

    // 用正式 HTTP 下載檢查附件、BOM 和欄名，再交由通用解析器處理帶引號的多行欄位。
    private static async Task<string[][]> ReadCsv(HttpClient client, string query)
    {
        // 匯出不得使用 JSON 或被當作可快取的網頁。
        using var response = await client.GetAsync(CsvPath + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("requisitions.csv", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.True(response.Headers.CacheControl?.NoStore);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));

        // 不自行切割字串，這裡使用實際支援引號、逗號及換行的標準程式庫。
        using var input = new MemoryStream(bytes);
        using var parser = new TextFieldParser(input, Encoding.UTF8) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        Assert.Equal(new[] { "單號", "主旨", "申請人顯示名稱", "送審部門", "目前狀態", "TWD 預估金額", "最近送審時間" }, parser.ReadFields());
        var rows = new List<string[]>();
        while (!parser.EndOfData)
        {
            // 任何錯誤跳脫都應讓解析失敗或無法得到完整的七欄。
            var cells = parser.ReadFields() ?? throw new InvalidOperationException();
            Assert.Equal(7, cells.Length);
            rows.Add(cells);
        }
        return [.. rows];
    }

    // 各入口都回傳既有錯誤形狀，沒有可下載的內容或洩漏其他查詢資料的欄位。
    private static async Task AssertError(HttpClient client, string query, HttpStatusCode status, string code)
    {
        foreach (var path in ReportPaths)
        {
            // 同時核對回應類型、穩定錯誤代碼與無附件，避免錯誤被當作檔案下載。
            using var response = await client.GetAsync(path + query);
            Assert.Equal(status, response.StatusCode);
            Assert.Null(response.Content.Headers.ContentDisposition);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            var fields = error
                .EnumerateObject()
                .Select(field => field.Name)
                .Order();
            Assert.Equal(new[] { "code", "message" }, fields);
            Assert.Equal(code, error.GetProperty("code").GetString());
            var message = error.GetProperty("message").GetString();
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.DoesNotContain("不能經錯誤回應取得", message);
            Assert.DoesNotContain("private", message);
        }
    }

    // 以公開的狀態名稱斷言 CSV；暫停案也維持原待審文字。
    private static string StatusLabel(string status) => status switch
    {
        "manager_pending" => "主管待審",
        "finance_pending" => "財務待審",
        "approved" => "核准結案",
        "rejected" => "駁回結案",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    // 以真實寫入回傳的識別搭配本案指定的已知數值，作為三份輸出的獨立期望值。
    private sealed record ExpectedRow(JsonElement Saved, DepartmentState Department, string Status, decimal Amount, bool Paused = false);
}
