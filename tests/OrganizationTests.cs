using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Procurement;
using Xunit;

// 透過正式 HTTP 端點檢查管理授權、輸入驗證與組織設定的持久化結果。
public sealed class OrganizationTests
{
    internal const string OrganizationPath = "/api/admin/organization";
    internal const string DepartmentsPath = "/api/admin/departments";
    internal const string AccountsPath = "/api/admin/accounts";

    // 未登入時先回報身分失效，避免舊權杖把管理權限問題誤報成 CSRF 錯誤。
    [Fact]
    public async Task Anonymous_organization_operations_require_authentication_before_csrf()
    {
        // 不取得防偽權杖，讓管理 POST 同時面對兩個未滿足的條件。
        await using var app = new Harness();
        using var client = app.Client();
        using var read = await client.GetAsync(OrganizationPath);
        using var write = await client.PostAsJsonAsync(DepartmentsPath, new { name = "部門" });

        // 兩個方法都須提供明確的登入失效回應，且 POST 不先回防偽錯誤。
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal("unauthenticated", (await read.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
        Assert.Equal("unauthenticated", (await write.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    // 單靠帳號類別或管理角色都不足以管理設定，保留業務與管理的界線。
    [Theory]
    [InlineData("business", null)]
    [InlineData("admin", null)]
    [InlineData("business", "Administrator")]
    public async Task Organization_requires_both_admin_account_type_and_current_role(string type, string? role)
    {
        // 取得已登入並通過 CSRF 的 client，讓請求真正抵達授權判斷。
        await using var app = new Harness();
        await app.Seed(type, role);
        using var client = await app.SignedIn();

        // 即使帶有合法 Cookie 與權杖，也不能讀取設定或保存變更。
        await Expect(await client.GetAsync(OrganizationPath), HttpStatusCode.Forbidden, "forbidden");
        await Expect(await client.PostAsJsonAsync(DepartmentsPath, new { name = UniqueName() }),
            HttpStatusCode.Forbidden, "forbidden");
    }

    // 管理寫入應延長本次登入的閒置期限；全程只用 POST 驗證此效果。
    [Fact]
    public async Task Successful_admin_post_extends_only_the_acting_session_idle_period()
    {
        // 所有登入和設定讀取都在同一時間完成，後續不讀取資料以免更新管理員的活動時間。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        using var admin = await app.SignedIn();
        using var other = await app.SignedIn();
        var department = await CreateDepartment(admin);
        var temporary = TemporaryPassword();
        var account = await CreateAccount(admin, department.Id, temporary);
        using var target = await app.SignedIn(account.UserName, temporary);

        // 第 20 分鐘修改業務帳號，此路徑會再鎖定目標，因而需要保留操作者原登入的識別。
        app.Clock.Advance(TimeSpan.FromMinutes(20));
        await Expect(await admin.PostAsJsonAsync($"{AccountsPath}/{account.Id}", account.Edit() with { IsManager = true }),
            HttpStatusCode.NoContent);

        // 第 31 分鐘只有實際執行過異動的管理登入可保存，其他登入依原閒置期限失效。
        app.Clock.Advance(TimeSpan.FromMinutes(11));
        await Expect(await admin.PostAsJsonAsync(DepartmentsPath, new { name = UniqueName() }), HttpStatusCode.NoContent);
        await Expect(await other.PostAsJsonAsync(DepartmentsPath, new { name = UniqueName() }),
            HttpStatusCode.Unauthorized, "unauthenticated");
        await Expect(await target.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthenticated");

        // 最後一次成功後滿三十分鐘即失效，持續未使用的 Cookie 不能再保存。
        app.Clock.Advance(TimeSpan.FromMinutes(30));
        await Expect(await admin.PostAsJsonAsync(DepartmentsPath, new { name = UniqueName() }),
            HttpStatusCode.Unauthorized, "unauthenticated");
    }

    // 防偽或輸入檢查未通過的管理寫入，不延長登入的閒置期限。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rejected_admin_post_does_not_extend_idle_period(bool invalidCsrf)
    {
        // 在原登入閒置到期前送出會被拒絕的寫入，讓錯誤的續期可被觀察。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        using var admin = await app.SignedIn();
        app.Clock.Advance(TimeSpan.FromMinutes(20));

        // 用空白名稱測服務拒絕，另一次移除權杖以測中介層提前拒絕。
        if (invalidCsrf)
            admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await Expect(await admin.PostAsJsonAsync(DepartmentsPath, new { name = " " }),
            HttpStatusCode.BadRequest, invalidCsrf ? "invalid_csrf" : "invalid_input");

        // 原期限整點仍須先回登入失效，即使請求未帶有效防偽權杖。
        app.Clock.Advance(TimeSpan.FromMinutes(10));
        await Expect(await admin.PostAsJsonAsync(DepartmentsPath, new { name = UniqueName() }),
            HttpStatusCode.Unauthorized, "unauthenticated");
    }

    // 拒絕無效 CSRF、非 DTO 欄位與管理目標，並讀回資料確認保護條件沒有副作用。
    [Fact]
    public async Task Admin_mutations_reject_invalid_csrf_overposting_and_admin_targets()
    {
        // 用正式管理權限建立有效部門，再控制每個拒絕案例的單一條件。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        using var admin = await app.SignedIn();
        var department = await CreateDepartment(admin);
        var identity = await Harness.Me(admin);
        var rejectedName = UniqueName();

        // 拔除已登入權杖仍須拒絕寫入，後續案例換回合法權杖才測輸入限制。
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await Expect(await admin.PostAsJsonAsync(DepartmentsPath, new { name = rejectedName }),
            HttpStatusCode.BadRequest, "invalid_csrf");
        await Harness.Token(admin);
        await Expect(await admin.PostAsJsonAsync(AccountsPath, new
        {
            userName = rejectedName,
            password = app.Password,
            departmentId = department.Id,
            isManager = false,
            isFinance = false,
            accountType = "admin",
            roles = new[] { "Administrator" }
        }), HttpStatusCode.BadRequest, "invalid_input");

        // 偽造管理目標或傳入不存在的識別都不得異動；即使知道管理員自身版本也一樣。
        var edit = new AccountEdit(identity.Version, false, department.Id, true, true, null, null);
        await Expect(await admin.PostAsJsonAsync($"{AccountsPath}/{app.UserId}", edit),
            HttpStatusCode.NotFound, "not_found");
        await Expect(await admin.PostAsJsonAsync($"{AccountsPath}/{app.UserId}/reset-password",
            new { version = identity.Version, newPassword = TemporaryPassword() }), HttpStatusCode.NotFound, "not_found");
        await Expect(await admin.PostAsJsonAsync($"{AccountsPath}/{Guid.NewGuid()}", edit),
            HttpStatusCode.NotFound, "not_found");

        // 管理回應不含管理員資料或拒絕的輸入，且管理員可用原登入讀到未變更版本。
        var saved = await Read(admin);
        Assert.DoesNotContain(saved.Departments, d => d.Name == rejectedName);
        Assert.DoesNotContain(saved.Accounts, a => a.Id == app.UserId || a.UserName == rejectedName);
        var current = await Harness.Me(admin);
        Assert.Equal(identity.Version, current.Version);
        Assert.Equal("admin", current.AccountType);
        Assert.Equal(["Administrator"], current.Roles);
    }

    // 部門新增與更新都須拒絕換行，即使換行在原本可被 Trim 移除的位置。
    [Theory]
    [InlineData(false, '\r')]
    [InlineData(false, '\n')]
    [InlineData(true, '\r')]
    [InlineData(true, '\n')]
    public async Task Department_name_rejects_line_break_before_trimming(bool update, char lineBreak)
    {
        // 建立原有部門，供拒絕更新時確認內容與版本皆保留。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        using var admin = await app.SignedIn();
        var department = await CreateDepartment(admin);
        var proposed = UniqueName();

        // 新增測字首，更新測字尾；兩種換行都不能由 Trim 靜默移除。
        var response = update
            ? await PostJsonWithLineBreak(admin, $"{DepartmentsPath}/{department.Id}",
                new { name = $"{proposed}{lineBreak}", active = false, version = department.Version }, "name", lineBreak.ToString())
            : await PostJsonWithLineBreak(admin, DepartmentsPath,
                new { name = $"{lineBreak}{proposed}" }, "name", lineBreak.ToString());
        await Expect(response, HttpStatusCode.BadRequest, "invalid_input");

        // 重新查詢既有狀態，確保未新增修剪後的名稱，也未變更原部門。
        var saved = (await Read(admin)).Departments;
        Assert.DoesNotContain(saved, d => d.Name == proposed);
        Assert.Equal(department, saved.Single(d => d.Id == department.Id));
    }

    // 帳號名稱與臨時密碼各自拒絕兩種換行，不能用其中一個無效欄位遮蔽另一個。
    [Theory]
    [InlineData(true, '\r')]
    [InlineData(true, '\n')]
    [InlineData(false, '\r')]
    [InlineData(false, '\n')]
    public async Task Account_creation_rejects_line_break_in_name_or_password(bool invalidName, char lineBreak)
    {
        // 使用有效的啟用部門及原本合規的帳號和臨時密碼，排除其他拒絕條件。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        using var admin = await app.SignedIn();
        var department = await CreateDepartment(admin);
        var name = UniqueName();
        var password = TemporaryPassword();

        // 帳號測字尾，密碼測中間，每次只改變一個欄位。
        var body = invalidName
            ? NewAccount($"{name}{lineBreak}", password, department.Id)
            : NewAccount(name, $"{password}{lineBreak}尾", department.Id);
        await Expect(await PostJsonWithLineBreak(admin, AccountsPath, body,
            invalidName ? "userName" : "password", lineBreak.ToString()), HttpStatusCode.BadRequest, "invalid_input");

        // 重新查詢確認拒絕沒有建立帳號，也沒有靜默採用修剪後的名稱。
        Assert.DoesNotContain((await Read(admin)).Accounts, a => a.UserName.Trim() == name);
    }

    // 驗證 DTO 必填、名稱與密碼長度、部門資格及帳號唯一性在 API 邊界生效。
    [Fact]
    public async Task Organization_inputs_enforce_boundaries_without_partial_updates()
    {
        // 先建立合法部門，讓後續各次拒絕可單獨驗證一項無效欄位。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        using var admin = await app.SignedIn();
        var department = await CreateDepartment(admin);
        var departmentPath = $"{DepartmentsPath}/{department.Id}";
        var accountName = UniqueName().PadRight(64, 'x');

        // 新增名稱不可只有空白，既有設定也不能保存超限或省略狀態的 DTO。
        await Expect(await admin.PostAsJsonAsync(DepartmentsPath, new { name = "  " }),
            HttpStatusCode.BadRequest, "invalid_input");
        await Expect(await admin.PostAsJsonAsync(DepartmentsPath, new { name = new string('部', 101) }),
            HttpStatusCode.BadRequest, "invalid_input");
        await Expect(await admin.PostAsJsonAsync(departmentPath, new { name = "缺狀態", version = department.Version }),
            HttpStatusCode.BadRequest, "invalid_input");
        await Expect(await admin.PostAsJsonAsync(departmentPath,
            new { name = new string('部', 101), active = true, version = department.Version }),
            HttpStatusCode.BadRequest, "invalid_input");
        Assert.Equal(department, (await Read(admin)).Departments.Single(d => d.Id == department.Id));

        // 驗證名稱兩端邊界皆可保存，且修剪後的名稱可直接讀回。
        await Expect(await admin.PostAsJsonAsync(departmentPath,
            new { name = "  部  ", active = true, version = department.Version }), HttpStatusCode.NoContent);
        var shortName = (await Read(admin)).Departments.Single(d => d.Id == department.Id);
        Assert.Equal("部", shortName.Name);
        var longName = new string('部', 100);
        await Expect(await admin.PostAsJsonAsync(departmentPath,
            new { name = longName, active = true, version = shortName.Version }), HttpStatusCode.NoContent);
        await Expect(await admin.PostAsJsonAsync(departmentPath,
            new { name = "過期保存", active = false, version = department.Version }), HttpStatusCode.Conflict, "stale_version");
        var renamed = (await Read(admin)).Departments.Single(d => d.Id == department.Id);
        Assert.Equal(longName, renamed.Name);

        // 無效名稱、長度超限、找不到部門及缺少部門不得建立任何帳號。
        await Expect(await admin.PostAsJsonAsync(AccountsPath, NewAccount("  ", app.Password, department.Id)),
            HttpStatusCode.BadRequest, "invalid_input");
        await Expect(await admin.PostAsJsonAsync(AccountsPath, NewAccount(accountName + "x", app.Password, department.Id)),
            HttpStatusCode.BadRequest, "invalid_input");
        await Expect(await admin.PostAsJsonAsync(AccountsPath, NewAccount(accountName, app.Password, null)),
            HttpStatusCode.BadRequest, "invalid_input");
        await Expect(await admin.PostAsJsonAsync(AccountsPath, NewAccount(accountName, app.Password, Guid.NewGuid().ToString())),
            HttpStatusCode.BadRequest, "invalid_input");
        foreach (var length in new[] { 11, 129 })
        {
            // 密碼長度上下界各驗一次，帳號應可在修正後成功建立。
            await Expect(await admin.PostAsJsonAsync(AccountsPath, NewAccount(accountName, new string('x', length), department.Id)),
                HttpStatusCode.BadRequest, "invalid_input");
        }

        // 12 字元密碼保留首尾空白，64 字元帳號先修剪再保存及比對同名。
        var minimumPassword = $" {TemporaryPassword()[..10]} ";
        var created = await CreateAccount(admin, department.Id, minimumPassword, $" {accountName} ");
        using var trimmedNameLogin = await app.SignedIn(accountName, minimumPassword);
        Assert.Equal(accountName, (await Harness.Me(trimmedNameLogin)).UserName);
        await Expect(await admin.PostAsJsonAsync(AccountsPath,
            NewAccount(accountName.ToUpperInvariant(), app.Password, department.Id)), HttpStatusCode.Conflict, "duplicate_user_name");
        await Expect(await admin.PostAsJsonAsync(AccountsPath,
            NewAccount(app.UserName.ToUpperInvariant(), app.Password, department.Id)), HttpStatusCode.Conflict, "duplicate_user_name");

        // 在累積測試資料中挑未用的單字帳號，避免重跑時受上次有效資料干擾。
        var existing = (await Read(admin))
            .Accounts
            .Select(a => a.UserName)
            .ToHashSet();
        var oneCharacter = Enumerable
            .Range(0xe000, 0x1900)
            .Select(i => ((char)i).ToString())
            .First(name => !existing.Contains(name));
        var maximumPassword = TemporaryPassword().PadRight(128, 'x');
        await CreateAccount(admin, department.Id, maximumPassword, oneCharacter);
        using var maxPasswordLogin = await app.SignedIn(oneCharacter, maximumPassword);
        Assert.True((await Harness.Me(maxPasswordLogin)).MustChangePassword);

        // 省略 nullable 欄位不能誤當成清除，且格式錯誤前後讀到的資料一致。
        await Expect(await admin.PostAsJsonAsync($"{AccountsPath}/{created.Id}", new
        {
            version = created.Version,
            active = false,
            departmentId = department.Id,
            isManager = false,
            isFinance = false,
            financeReviewerId = (string?)null
        }), HttpStatusCode.BadRequest, "invalid_input");
        Assert.Equal(created, (await Read(admin)).Accounts.Single(a => a.Id == created.Id));
    }

    // 驗證目前角色與停用效果，並保留停用部門及失去資格的人選既有關聯。
    [Fact]
    public async Task Organization_changes_preserve_historical_references_and_update_live_sessions()
    {
        // 建立跨部門人選與申請人，並讓申請人在原 Cookie 下讀到後續角色變更。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        using var admin = await app.SignedIn();
        var department = await CreateDepartment(admin);
        var newDepartment = await CreateDepartment(admin);
        var manager = await CreateAccount(admin, department.Id, TemporaryPassword(), isManager: true, isFinance: true);
        var finance = await CreateAccount(admin, department.Id, TemporaryPassword(), isFinance: true);
        var temporary = TemporaryPassword();
        var applicant = await CreateAccount(admin, department.Id, temporary);
        using var initial = await app.SignedIn(applicant.UserName, temporary);
        var password = TemporaryPassword();
        await Expect(await initial.PostAsJsonAsync("/api/auth/change-password",
            new PasswordRequest(temporary, password, (await Harness.Me(initial)).Version)), HttpStatusCode.NoContent);
        using var applicantClient = await app.SignedIn(applicant.UserName, password);
        Assert.Equal(["Applicant"], (await Harness.Me(applicantClient)).Roles);
        applicant = (await Read(admin)).Accounts.Single(a => a.Id == applicant.Id);
        var path = $"{AccountsPath}/{applicant.Id}";

        // 排除本人、兩關同人、對應角色不符及不存在的人選，再核對先前資料尚未改寫。
        var change = applicant.Edit() with { DepartmentId = newDepartment.Id, IsManager = true, IsFinance = true };
        foreach (var bad in new[]
        {
            change with { ManagerReviewerId = applicant.Id },
            change with { ManagerReviewerId = manager.Id, FinanceReviewerId = manager.Id },
            change with { ManagerReviewerId = finance.Id },
            change with { FinanceReviewerId = Guid.NewGuid().ToString() },
            change with { ManagerReviewerId = app.UserId }
        })
        {
            // 每個候選錯誤都應由服務驗證阻止，不能靠 UI 下拉選單限制。
            await Expect(await admin.PostAsJsonAsync(path, bad), HttpStatusCode.BadRequest, "invalid_input");
        }
        Assert.Equal(applicant, (await Read(admin)).Accounts.Single(a => a.Id == applicant.Id));

        // 一次保存調部門、兼任及跨部門審核人；同一登入讀到角色聯集。
        await Expect(await admin.PostAsJsonAsync(path,
            change with { ManagerReviewerId = manager.Id, FinanceReviewerId = finance.Id }), HttpStatusCode.NoContent);
        var updated = (await Read(admin)).Accounts.Single(a => a.Id == applicant.Id);
        Assert.NotEqual(applicant.Version, updated.Version);
        Assert.Equal(newDepartment.Id, updated.DepartmentId);
        Assert.Equal(manager.Id, updated.ManagerReviewerId);
        Assert.Equal(finance.Id, updated.FinanceReviewerId);
        Assert.Equal(["Applicant", "Finance", "Manager"], (await Harness.Me(applicantClient)).Roles);

        // 停用後新帳號與調部門皆不能分配到停用部門，已分配的歸屬保留原識別。
        await Expect(await admin.PostAsJsonAsync($"{DepartmentsPath}/{newDepartment.Id}",
            new { name = "停用部門", active = false, version = newDepartment.Version }), HttpStatusCode.NoContent);
        await Expect(await admin.PostAsJsonAsync(AccountsPath, NewAccount(UniqueName(), temporary, newDepartment.Id)),
            HttpStatusCode.BadRequest, "invalid_input");
        await Expect(await admin.PostAsJsonAsync($"{AccountsPath}/{finance.Id}",
            finance.Edit() with { DepartmentId = newDepartment.Id }), HttpStatusCode.BadRequest, "invalid_input");
        await Expect(await admin.PostAsJsonAsync($"{AccountsPath}/{finance.Id}",
            finance.Edit() with { DepartmentId = null }), HttpStatusCode.BadRequest, "invalid_input");
        var disabledDepartment = (await Read(admin)).Departments.Single(d => d.Id == newDepartment.Id);
        Assert.False(disabledDepartment.Active);
        Assert.Equal("停用部門", disabledDepartment.Name);
        Assert.NotEqual(newDepartment.Version, disabledDepartment.Version);

        // 停用既有主管不清除其參照，新指派則不能選停用人選。
        var managerPath = $"{AccountsPath}/{manager.Id}";
        await Expect(await admin.PostAsJsonAsync(managerPath, manager.Edit() with { Active = false }), HttpStatusCode.NoContent);
        await Expect(await admin.PostAsJsonAsync($"{AccountsPath}/{finance.Id}", finance.Edit() with { ManagerReviewerId = manager.Id }),
            HttpStatusCode.BadRequest, "invalid_input");
        var inactiveManager = (await Read(admin)).Accounts.Single(a => a.Id == manager.Id);
        Assert.False(inactiveManager.Active);
        await Expect(await admin.PostAsJsonAsync(managerPath, inactiveManager.Edit() with { Active = true, IsManager = false }),
            HttpStatusCode.NoContent);

        // 撤除申請人的主管角色；即使部門或預設人選已不可再指派，原值仍可保存。
        await Expect(await admin.PostAsJsonAsync(path, updated.Edit() with { IsManager = false }), HttpStatusCode.NoContent);
        Assert.Equal(["Applicant", "Finance"], (await Harness.Me(applicantClient)).Roles);
        var preserved = (await Read(admin)).Accounts.Single(a => a.Id == applicant.Id);
        Assert.Equal(newDepartment.Id, preserved.DepartmentId);
        Assert.Equal(manager.Id, preserved.ManagerReviewerId);
        Assert.Equal(finance.Id, preserved.FinanceReviewerId);

        // 清除一關不會改動另一關或部門，空值只在明確提交時才採用。
        await Expect(await admin.PostAsJsonAsync(path, preserved.Edit() with { ManagerReviewerId = null }), HttpStatusCode.NoContent);
        var cleared = (await Read(admin)).Accounts.Single(a => a.Id == applicant.Id);
        Assert.Null(cleared.ManagerReviewerId);
        Assert.Equal(finance.Id, cleared.FinanceReviewerId);
        Assert.Equal(newDepartment.Id, cleared.DepartmentId);
    }

    // 透過 HTTP 檢查穩定狀態碼和選定的錯誤代碼，避免把錯誤回應正文寫入實測結果。
    internal static async Task Expect(HttpResponseMessage response, HttpStatusCode status, string? code = null)
    {
        // 使用回應後立即釋放，錯誤斷言只顯示公開代碼。
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            if (code is not null)
                Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        }
    }

    // 送出原始 JSON 內容前確認指定欄位解碼後仍含真正的控制字元，避免測成反斜線文字。
    internal static async Task<HttpResponseMessage> PostJsonWithLineBreak(
        HttpClient client, string path, object body, string field, string lineBreak)
    {
        // 先讀取同一份內容做確認，失敗診斷只顯示欄位名，避免顯示測試密碼。
        using var content = JsonContent.Create(body);
        using var json = JsonDocument.Parse(await content.ReadAsStringAsync());
        var decoded = json
            .RootElement
            .GetProperty(field)
            .GetString();
        Assert.True(decoded?.Contains(lineBreak, StringComparison.Ordinal) == true, $"欄位 {field} 應含實際的換行字元。");

        // 使用已核對的內容送出 HTTP，讓正式 JSON 繫結與驗證都能受測。
        return await client.PostAsync(path, content);
    }

    // 以正式讀取 API 重新取得保存值，測試不從 EF 追蹤狀態推測異動成功。
    internal static async Task<Snapshot> Read(HttpClient admin) =>
        await admin.GetFromJsonAsync<Snapshot>(OrganizationPath) ?? throw new InvalidOperationException();

    // 建立唯一部門名稱，讓可持續重跑的隔離測試庫仍能查出本案資料。
    internal static async Task<DepartmentState> CreateDepartment(HttpClient admin)
    {
        // 建立後重新查詢，取得系統產生的識別及版本。
        var name = UniqueName();
        await Expect(await admin.PostAsJsonAsync(DepartmentsPath, new { name }), HttpStatusCode.NoContent);
        var saved = (await Read(admin)).Departments.Single(d => d.Name == name);
        Assert.True(saved.Active);
        Assert.False(string.IsNullOrEmpty(saved.Id));
        Assert.False(string.IsNullOrEmpty(saved.Version));
        return saved;
    }

    // 以正式寫入入口建立業務帳號，再取回保存資料供其他場景使用。
    internal static async Task<AccountState> CreateAccount(
        HttpClient admin, string departmentId, string password, string? userName = null,
        bool isManager = false, bool isFinance = false)
    {
        // 呼叫端可傳入需驗證的帳號值；其他測試使用唯一名稱隔離資料。
        var name = userName ?? UniqueName();
        await Expect(await admin.PostAsJsonAsync(AccountsPath,
            NewAccount(name, password, departmentId, isManager, isFinance)), HttpStatusCode.NoContent);
        return (await Read(admin)).Accounts.Single(a => a.UserName == name.Trim());
    }

    // 共用建立輸入形狀，讓錯誤案例能只更換需要驗證的欄位。
    private static object NewAccount(string userName, string password, string? departmentId,
        bool isManager = false, bool isFinance = false) => new { userName, password, departmentId, isManager, isFinance };

    // 產生測試專用唯一名稱，避免多輪測試混用先前資料。
    private static string UniqueName() => "org-" + Guid.NewGuid().ToString("N");

    // 每次產生不同測試密碼；任何驗證只記錄結果或代碼。
    internal static string TemporaryPassword() => Guid.NewGuid().ToString("N");

    // 用公開 JSON 形狀讀取快照，避免依賴服務內部 DTO 型別。
    internal sealed record Snapshot(DepartmentState[] Departments, AccountState[] Accounts);

    // 讀取測試要核對的部門資料及版本。
    internal sealed record DepartmentState(string Id, string Name, bool Active, string Version);

    // 讀取設定欄位，讓測試可核對 HTTP 異動前後的狀態。
    internal sealed record AccountState(
        string Id, string UserName, bool Active, string Version, string? DepartmentId, bool IsManager, bool IsFinance,
        string? ManagerReviewerId, string? FinanceReviewerId, bool MustChangePassword)
    {
        // 以目前讀取結果填滿更新輸入，案例只覆寫需要變更的欄位。
        internal AccountEdit Edit() => new(Version, Active, DepartmentId, IsManager, IsFinance, ManagerReviewerId, FinanceReviewerId);
    }

    // 測試送出完整更新形狀，避免省略欄位時掩蓋資格或版本情境。
    internal sealed record AccountEdit(
        string Version, bool Active, string? DepartmentId, bool IsManager, bool IsFinance,
        string? ManagerReviewerId, string? FinanceReviewerId);
}
