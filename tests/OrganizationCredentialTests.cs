using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Procurement;
using Xunit;
using static OrganizationTests;

// 透過正式管理與登入 API 驗證臨時密碼、即時撤銷及恢復登入。
public sealed class OrganizationCredentialTests
{
    // 新帳號須先更換臨時密碼；以實際請求驗證強制狀態允許的端點及完成後的登入。
    [Fact]
    public async Task New_account_requires_a_distinct_password_and_revokes_every_initial_session()
    {
        // 由管理員新增帳號；在進入身分流程前核對預設欄位和設定輸出範圍。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        using var admin = await app.SignedIn();
        var department = await CreateDepartment(admin);
        var temporary = $" {TemporaryPassword()} ";
        var created = await CreateAccount(admin, department.Id, temporary);
        Assert.True(created.Active);
        Assert.True(created.MustChangePassword);
        Assert.Equal(department.Id, created.DepartmentId);
        Assert.Null(created.ManagerReviewerId);
        Assert.Null(created.FinanceReviewerId);
        Assert.False(created.IsManager);
        Assert.False(created.IsFinance);
        await ExpectSafeProjection(admin, created.Id, temporary);

        // 三份有效登入分別用來登出、改密碼與檢查同帳號的其餘登入是否一起撤銷。
        using var forced = await app.SignedIn(created.UserName, temporary);
        using var other = await app.SignedIn(created.UserName, temporary);
        using var logout = await app.SignedIn(created.UserName, temporary);
        var identity = await forced.GetFromJsonAsync<IdentityResponse>("/API/AUTH/ME/");
        Assert.NotNull(identity);
        Assert.True(identity.MustChangePassword);
        Assert.Equal("business", identity.AccountType);
        Assert.Equal(["Applicant"], identity.Roles);

        // 強制狀態拒絕管理讀寫與任何其他 API；正式的身分端點仍可用。
        await Expect(await forced.GetAsync(OrganizationPath), HttpStatusCode.Forbidden, "forbidden");
        await Expect(await forced.PostAsJsonAsync(DepartmentsPath, new { name = "拒絕寫入" }),
            HttpStatusCode.Forbidden, "forbidden");
        await Expect(await forced.GetAsync("/api/future-business-operation"), HttpStatusCode.Forbidden, "forbidden");
        await Expect(await forced.PostAsJsonAsync("/api/auth/login", new LoginRequest(created.UserName, temporary)),
            HttpStatusCode.Forbidden, "forbidden");
        await Expect(await logout.PostAsync("/API/AUTH/LOGOUT/", null), HttpStatusCode.NoContent);
        await Expect(await logout.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        Assert.True((await Harness.Me(other)).MustChangePassword);

        // 舊密碼錯誤或新密碼與臨時密碼相同都不解除狀態，也不改變帳號版本。
        const string changePath = "/API/AUTH/CHANGE-PASSWORD/";
        var next = $" {TemporaryPassword()} ";
        await Expect(await forced.PostAsJsonAsync(changePath, new PasswordRequest(TemporaryPassword(), next, identity.Version)),
            HttpStatusCode.BadRequest, "wrong_password");
        await Expect(await forced.PostAsJsonAsync(changePath, new PasswordRequest(temporary, temporary, identity.Version)),
            HttpStatusCode.BadRequest, "invalid_input");
        var stillForced = await Harness.Me(forced);
        Assert.True(stillForced.MustChangePassword);
        Assert.Equal(identity.Version, stillForced.Version);
        Assert.Equal(created, (await Read(admin)).Accounts.Single(a => a.Id == created.Id));

        // 成功後兩份登入都須失效；舊臨時密碼無法重登，只有新憑證可解除強制狀態。
        await Expect(await forced.PostAsJsonAsync(changePath, new PasswordRequest(temporary, next, identity.Version)),
            HttpStatusCode.NoContent);
        await Expect(await forced.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        await Expect(await other.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        using var wrong = app.Client();
        await Expect(await Harness.Login(wrong, created.UserName, temporary), HttpStatusCode.Unauthorized, "invalid_credentials");
        using var ready = await app.SignedIn(created.UserName, next);
        Assert.False((await Harness.Me(ready)).MustChangePassword);
        var changed = (await Read(admin)).Accounts.Single(a => a.Id == created.Id);
        Assert.False(changed.MustChangePassword);
        await Expect(await ready.GetAsync("/api/future-business-operation"), HttpStatusCode.NotFound);
    }

    // 強制改密碼拒絕含換行的新密碼，原登入、臨時密碼與強制狀態仍有效。
    [Theory]
    [InlineData('\r')]
    [InlineData('\n')]
    public async Task Forced_password_change_rejects_line_break_without_replacing_temporary_password(char lineBreak)
    {
        // 透過管理 API 建立新帳號，並用包含首尾空白的合法臨時密碼登入。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        using var admin = await app.SignedIn();
        var department = await CreateDepartment(admin);
        var temporary = $" {TemporaryPassword()} ";
        var account = await CreateAccount(admin, department.Id, temporary);
        using var forced = await app.SignedIn(account.UserName, temporary);
        var original = await Harness.Me(forced);

        // 新密碼長度及目前密碼皆正確，拒絕原因應為新密碼中的換行。
        var newPassword = $"{TemporaryPassword()}{lineBreak}";
        await Expect(await PostJsonWithLineBreak(forced, "/api/auth/change-password",
            new PasswordRequest(temporary, newPassword, original.Version), "newPassword", lineBreak.ToString()),
            HttpStatusCode.BadRequest, "invalid_input");

        // 後續讀取仍要求改密碼，原臨時密碼也可建立新工作階段。
        var current = await Harness.Me(forced);
        Assert.True(current.MustChangePassword);
        Assert.Equal(original.Version, current.Version);
        using var fresh = await app.SignedIn(account.UserName, temporary);
        Assert.True((await Harness.Me(fresh)).MustChangePassword);
    }

    // 重設拒絕含換行的臨時密碼，帳號版本、既有登入及原憑證皆保留。
    [Theory]
    [InlineData('\r')]
    [InlineData('\n')]
    public async Task Admin_password_reset_rejects_line_break_without_changing_credentials(char lineBreak)
    {
        // 使用無鎖定且已登入的舊業務帳號，獨立驗證拒絕重設的資料影響。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        await using var business = new Harness();
        await business.Seed();
        using var admin = await app.SignedIn();
        using var first = await business.SignedIn();
        using var second = await business.SignedIn();
        var original = (await Read(admin)).Accounts.Single(a => a.Id == business.UserId);

        // 帶回正確版本，使用合法長度但含換行的新臨時密碼測試重設端點。
        var newPassword = $"{lineBreak}{TemporaryPassword()}";
        await Expect(await PostJsonWithLineBreak(admin, $"{AccountsPath}/{original.Id}/reset-password",
            new { version = original.Version, newPassword }, "newPassword", lineBreak.ToString()),
            HttpStatusCode.BadRequest, "invalid_input");

        // 查詢未更動的設定與兩份舊登入，並以新舊密碼實際比對登入結果。
        Assert.Equal(original, (await Read(admin)).Accounts.Single(a => a.Id == original.Id));
        Assert.Equal(original.Version, (await Harness.Me(first)).Version);
        Assert.Equal(original.Version, (await Harness.Me(second)).Version);
        using var wrong = business.Client();
        await Expect(await business.Login(wrong, newPassword), HttpStatusCode.Unauthorized, "invalid_credentials");
        using var fresh = await business.SignedIn();
        Assert.False((await Harness.Me(fresh)).MustChangePassword);
    }

    // 一般改密碼沿用原字元語意，既有含換行的密碼仍可登入並再次變更。
    [Fact]
    public async Task Ordinary_password_change_accepts_line_break_in_new_and_current_password()
    {
        // 使用非強制改密碼的既有業務帳號，保留原本登入與變更入口。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();
        var original = await Harness.Me(client);
        Assert.False(original.MustChangePassword);

        // 先接受含 CRLF 的新密碼，再以完全相同的原字元登入。
        const string path = "/api/auth/change-password";
        const string lineBreak = "\r\n";
        var withLineBreak = $" {TemporaryPassword()}{lineBreak} ";
        await Expect(await PostJsonWithLineBreak(client, path,
            new PasswordRequest(app.Password, withLineBreak, original.Version), "newPassword", lineBreak), HttpStatusCode.NoContent);
        using var current = await app.SignedIn(password: withLineBreak);
        var identity = await Harness.Me(current);
        Assert.False(identity.MustChangePassword);

        // 同一密碼當作目前憑證也必須驗證成功，之後可用含空白的新密碼登入。
        var next = $" {TemporaryPassword()} ";
        await Expect(await PostJsonWithLineBreak(current, path,
            new PasswordRequest(withLineBreak, next, identity.Version), "currentPassword", lineBreak), HttpStatusCode.NoContent);
        using var fresh = await app.SignedIn(password: next);
        Assert.False((await Harness.Me(fresh)).MustChangePassword);
    }

    // 重設只允許有效版本和不同的臨時密碼，並使所有原登入與憑證失效。
    [Fact]
    public async Task Admin_password_reset_requires_a_fresh_forced_login()
    {
        // 使用既有業務帳號，驗證相容舊帳號及其兩個工作階段。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        await using var business = new Harness();
        await business.Seed();
        using var admin = await app.SignedIn();
        using var first = await business.SignedIn();
        using var second = await business.SignedIn();
        var original = (await Read(admin)).Accounts.Single(a => a.Id == business.UserId);
        Assert.False(original.MustChangePassword);
        var path = $"{AccountsPath}/{original.Id}/reset-password";
        var temporary = $" {TemporaryPassword()} ";

        // 建立舊憑證的登入鎖定，重設後立即登入即可證明鎖定計數已清除。
        using var locked = business.Client();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            // 最後一次錯誤須進入鎖定，既有工作階段仍供後續測試驗證撤銷。
            var status = attempt == 5 ? HttpStatusCode.Locked : HttpStatusCode.Unauthorized;
            var code = attempt == 5 ? "account_locked" : "invalid_credentials";
            await Expect(await business.Login(locked, TemporaryPassword()), status, code);
        }

        // 錯誤長度、原密碼及舊版本都不撤銷既有登入或更改已保存資料。
        foreach (var length in new[] { 11, 129 })
        {
            // 驗證重設端點本身採共用密碼規則。
            await Expect(await admin.PostAsJsonAsync(path, new { version = original.Version, newPassword = new string('x', length) }),
                HttpStatusCode.BadRequest, "invalid_input");
        }
        await Expect(await admin.PostAsJsonAsync(path, new { version = original.Version, newPassword = business.Password }),
            HttpStatusCode.BadRequest, "invalid_input");
        await Expect(await admin.PostAsJsonAsync(path, new { version = "舊版本", newPassword = temporary }),
            HttpStatusCode.Conflict, "stale_version");
        Assert.Equal(original, (await Read(admin)).Accounts.Single(a => a.Id == original.Id));
        Assert.Equal(original.Version, (await Harness.Me(first)).Version);
        Assert.Equal(original.Version, (await Harness.Me(second)).Version);

        // 成功回應沒有本文，後續查詢不公開憑證，並能讀到強制狀態及新版本。
        using var reset = await admin.PostAsJsonAsync(path, new { version = original.Version, newPassword = temporary });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.True((await reset.Content.ReadAsByteArrayAsync()).Length == 0, "重設回應不可包含內容。");
        var saved = (await Read(admin)).Accounts.Single(a => a.Id == original.Id);
        Assert.True(saved.MustChangePassword);
        Assert.NotEqual(original.Version, saved.Version);
        await ExpectSafeProjection(admin, original.Id, temporary);

        // 舊 Cookie 與舊 CSRF 組合仍回 401，且原密碼已無法取得新 Cookie。
        await Expect(await first.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        await Expect(await second.PostAsJsonAsync(DepartmentsPath, new { name = "拒絕寫入" }),
            HttpStatusCode.Unauthorized, "unauthenticated");
        using var oldCredentials = business.Client();
        await Expect(await business.Login(oldCredentials), HttpStatusCode.Unauthorized, "invalid_credentials");

        // 新臨時密碼仍受強制狀態保護，變更成功後以新密碼登入確認恢復。
        using var forced = await business.SignedIn(password: temporary);
        var forcedIdentity = await Harness.Me(forced);
        Assert.True(forcedIdentity.MustChangePassword);
        await Expect(await forced.GetAsync("/api/future-business-operation"), HttpStatusCode.Forbidden, "forbidden");
        var next = TemporaryPassword();
        await Expect(await forced.PostAsJsonAsync("/api/auth/change-password",
            new PasswordRequest(temporary, next, forcedIdentity.Version)), HttpStatusCode.NoContent);
        await Expect(await forced.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        using var ready = await business.SignedIn(password: next);
        Assert.False((await Harness.Me(ready)).MustChangePassword);
        var changed = (await Read(admin)).Accounts.Single(a => a.Id == original.Id);
        Assert.False(changed.MustChangePassword);
    }

    // 停用立刻撤銷全部登入，重新啟用後只能建立新工作階段，保留既有無部門狀態。
    [Fact]
    public async Task Reactivation_never_revives_disabled_account_sessions()
    {
        // 以既有帳號和兩份登入證明舊 Cookie 在重啟用後仍不可用。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        await using var business = new Harness();
        await business.Seed("business", "Applicant");
        using var admin = await app.SignedIn();
        using var first = await business.SignedIn();
        using var second = await business.SignedIn();
        var original = (await Read(admin)).Accounts.Single(a => a.Id == business.UserId);
        Assert.Null(original.DepartmentId);
        var path = $"{AccountsPath}/{original.Id}";

        // 停用期間原工作階段及以原密碼新登入都須被拒絕。
        await Expect(await admin.PostAsJsonAsync(path, original.Edit() with { Active = false }), HttpStatusCode.NoContent);
        await Expect(await first.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        using var attempted = business.Client();
        await Expect(await business.Login(attempted), HttpStatusCode.Unauthorized, "invalid_credentials");
        var disabled = (await Read(admin)).Accounts.Single(a => a.Id == original.Id);
        Assert.False(disabled.Active);
        Assert.NotEqual(original.Version, disabled.Version);

        // 恢復啟用仍保留舊部門與 Applicant 權限，只允許新工作階段。
        await Expect(await admin.PostAsJsonAsync(path, disabled.Edit() with { Active = true, IsManager = true }),
            HttpStatusCode.NoContent);
        await Expect(await first.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        await Expect(await second.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        using var fresh = await business.SignedIn();
        Assert.Equal(["Applicant", "Manager"], (await Harness.Me(fresh)).Roles);
        var reactivated = (await Read(admin)).Accounts.Single(a => a.Id == original.Id);
        Assert.Null(reactivated.DepartmentId);
    }

    // 檢查真實 JSON 只含核准欄位，失敗時不將回應內可能的憑證寫到測試輸出。
    private static async Task ExpectSafeProjection(HttpClient admin, string accountId, string password)
    {
        // 先用不包含本文的斷言檢查明文，避免錯誤診斷意外輸出密碼。
        var body = await admin.GetStringAsync(OrganizationPath);
        Assert.False(body.Contains(password, StringComparison.Ordinal), "組織設定回應不可含密碼。");

        // 僅比對欄位名稱，涵蓋雜湊、登入資料或其他未核准欄位的意外新增。
        using var json = JsonDocument.Parse(body);
        var account = json
            .RootElement
            .GetProperty("accounts")
            .EnumerateArray()
            .Single(a => a.GetProperty("id").GetString() == accountId);
        var fields = account
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);
        Assert.Equal(
            ["active", "departmentId", "financeReviewerId", "id", "isFinance", "isManager", "managerReviewerId", "mustChangePassword", "userName", "version"],
            fields);
    }
}
