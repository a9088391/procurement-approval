using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using static OrganizationTests;
using static RequisitionApprovalTests;

// 透過正式 HTTP 端點核對停用的交易、歷史保留與重新啟用時的登入行為。
public sealed class OrganizationDeactivationTests
{
    private const string DepartmentsDeactivatePath = DepartmentsPath + "/deactivate";
    private const string AccountsDeactivatePath = AccountsPath + "/deactivate";

    // 同一個部門端點支援單筆及批次，停用後仍可讀到關聯及原送審內容。
    [Fact]
    public async Task Departments_deactivate_singly_and_in_bulk_and_keep_references_and_history()
    {
        // 由正式送審入口產生部門快照及歷程，另建第三個部門以驗證批次包含兩個啟用目標。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var admin = scenario.Admin;
        var first = scenario.Department;
        var second = scenario.OtherDepartment;
        var third = await CreateDepartment(admin);
        var owner = scenario.Owner.Client;
        var submitted = await Act(owner, await scenario.Draft(), "submit");
        var originalOwner = (await Read(admin)).Accounts.Single(a => a.Id == scenario.Owner.Id);

        // 單筆停用只改狀態與版本，其他部門及原歸屬仍維持。
        await Expect(await admin.PostAsJsonAsync(DepartmentsDeactivatePath, new { items = new[] { new { first.Id, first.Version } } }),
            HttpStatusCode.NoContent);
        var saved = await Read(admin);
        var inactive = saved.Departments.Single(d => d.Id == first.Id);
        Assert.NotEqual(first.Version, inactive.Version);
        Assert.Equal(first with { Active = false, Version = inactive.Version }, inactive);
        Assert.Equal(second, saved.Departments.Single(d => d.Id == second.Id));
        Assert.Equal(originalOwner, saved.Accounts.Single(a => a.Id == originalOwner.Id));

        // 混合已停用與啟用資料時只更新仍啟用的部門，送審快照與歷程維持原值。
        await Expect(await admin.PostAsJsonAsync(DepartmentsDeactivatePath,
            new { items = new[] { new { inactive.Id, inactive.Version }, new { second.Id, second.Version }, new { third.Id, third.Version } } }),
            HttpStatusCode.NoContent);
        var disabled = (await Read(admin)).Departments;
        Assert.Equal(inactive, disabled.Single(d => d.Id == first.Id));
        foreach (var original in new[] { second, third })
        {
            // 每一個原本啟用的目標都取得新版本及停用狀態。
            var current = disabled.Single(d => d.Id == original.Id);
            Assert.NotEqual(original.Version, current.Version);
            Assert.Equal(original with { Active = false, Version = current.Version }, current);
        }
        var detail = await Detail(owner, submitted);
        Assert.True(JsonElement.DeepEquals(submitted.GetProperty("round"), detail.GetProperty("round")));
        Assert.True(JsonElement.DeepEquals(submitted.GetProperty("history"), detail.GetProperty("history")));

        // 原本的編輯端點可重新啟用同一個部門，且不需重新分配帳號。
        await Expect(await admin.PostAsJsonAsync($"{DepartmentsPath}/{first.Id}",
            new { first.Name, active = true, inactive.Version }), HttpStatusCode.NoContent);
        saved = await Read(admin);
        Assert.True(saved.Departments.Single(d => d.Id == first.Id).Active);
        Assert.Equal(originalOwner, saved.Accounts.Single(a => a.Id == originalOwner.Id));
    }

    // 批次停用只改帳號狀態與版本，並從伺服器撤銷每個目標的既有登入。
    [Fact]
    public async Task Business_accounts_keep_settings_and_history_but_old_cookies_never_revive()
    {
        // 保存既有預設人選並完成第一關決定，供停用後核對目前設定和歷史。
        await using var scenario = await RequisitionApprovalScenario.Start();
        var admin = scenario.Admin;
        await scenario.Update(scenario.Manager, edit => edit with { FinanceReviewerId = scenario.Finance.Id });
        var before = await Read(admin);
        var manager = before.Accounts.Single(a => a.Id == scenario.Manager.Id);
        var finance = before.Accounts.Single(a => a.Id == scenario.Finance.Id);
        var owner = before.Accounts.Single(a => a.Id == scenario.Owner.Id);
        var submitted = await Act(scenario.Owner.Client, await scenario.Draft(), "submit");
        var approved = await Act(scenario.Manager.Client, submitted, "approve");

        // 保存兩個目標的原始 Cookie，重新啟用後仍能以實際請求重播同一值。
        using var oldManager = await OldSession(scenario.App, scenario.Manager);
        using var oldFinance = await OldSession(scenario.App, scenario.Finance);
        await Expect(await admin.PostAsJsonAsync(AccountsDeactivatePath,
            new { items = new[] { new { manager.Id, manager.Version }, new { finance.Id, finance.Version } } }),
            HttpStatusCode.NoContent);
        var disabled = await Read(admin);
        Assert.Equal(owner, disabled.Accounts.Single(a => a.Id == owner.Id));
        var detail = await Detail(scenario.Owner.Client, approved);
        Assert.True(JsonElement.DeepEquals(approved.GetProperty("round"), detail.GetProperty("round")));
        Assert.True(JsonElement.DeepEquals(approved.GetProperty("history"), detail.GetProperty("history")));
        AssertStatus(detail, "finance_pending", true, false);

        // 停用中的帳號拒絕新登入，其他裝置的既有登入也立即失效。
        await Expect(await scenario.Manager.Client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        await Expect(await scenario.Finance.Client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        using var freshClient = scenario.App.Client();
        await Expect(await Harness.Login(freshClient, scenario.Finance.Name, scenario.Finance.Password),
            HttpStatusCode.Unauthorized, "invalid_credentials");

        // 保留所有公開設定，用讀回的版本透過既有的編輯端點重新啟用。
        foreach (var original in new[] { manager, finance })
        {
            // 再次單筆停用已停用帳號應成功，並保留版本以供接續重新啟用。
            var current = disabled.Accounts.Single(a => a.Id == original.Id);
            Assert.NotEqual(original.Version, current.Version);
            Assert.Equal(original with { Active = false, Version = current.Version }, current);
            await Expect(await admin.PostAsJsonAsync(AccountsDeactivatePath,
                new { items = new[] { new { current.Id, current.Version } } }), HttpStatusCode.NoContent);
            await Expect(await admin.PostAsJsonAsync($"{AccountsPath}/{current.Id}", current.Edit() with { Active = true }),
                HttpStatusCode.NoContent);
        }

        // 舊登入不會因恢復啟用而復活，新登入則保有原本的業務角色。
        await Expect(await oldManager.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        await Expect(await oldFinance.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthenticated");
        using var managerRestored = await scenario.App.SignedIn(scenario.Manager.Name, scenario.Manager.Password);
        using var financeRestored = await scenario.App.SignedIn(scenario.Finance.Name, scenario.Finance.Password);
        Assert.Equal(["Applicant", "Manager"], (await Harness.Me(managerRestored)).Roles);
        Assert.Equal(["Applicant", "Finance"], (await Harness.Me(financeRestored)).Roles);
        AssertStatus(await Detail(financeRestored, approved), "finance_pending", false, true);
    }

    // 部門與帳號停用沿用身分及防偽防護，任何輸入或批次目標錯誤都不留下部分結果。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deactivation_validates_the_whole_batch_and_leaves_members_unchanged_on_error(bool accounts)
    {
        // 建立兩筆可變更的目標，並保留啟用中的業務登入供錯誤後核對。
        await using var app = new Harness();
        await app.Seed("admin", "Administrator");
        using var admin = await app.SignedIn();
        var department = await CreateDepartment(admin);
        var secondDepartment = await CreateDepartment(admin);
        var password = TemporaryPassword();
        var account = await CreateAccount(admin, department.Id, password);
        var secondAccount = await CreateAccount(admin, secondDepartment.Id, password);
        using var signedInTarget = await app.SignedIn(account.UserName, password);
        var path = accounts ? AccountsDeactivatePath : DepartmentsDeactivatePath;
        var target = accounts ? new { account.Id, account.Version } : new { department.Id, department.Version };
        var second = accounts ? new { secondAccount.Id, secondAccount.Version } : new { secondDepartment.Id, secondDepartment.Version };
        var valid = new { items = new[] { target, second } };

        // 匿名、帶有效權杖的業務帳號及未帶權杖的管理員都不能進行停用。
        using var anonymous = app.Client();
        await Expect(await anonymous.PostAsJsonAsync(path, valid), HttpStatusCode.Unauthorized, "unauthenticated");
        await using var businessApp = new Harness();
        await businessApp.Seed("business", "Administrator");
        using var business = await businessApp.SignedIn();
        await Expect(await business.PostAsJsonAsync(path, valid), HttpStatusCode.Forbidden, "forbidden");
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await Expect(await admin.PostAsJsonAsync(path, valid), HttpStatusCode.BadRequest, "invalid_csrf");
        await Harness.Token(admin);

        // 缺少本文、空值、無效欄位、重複目標與夾帶欄位都應回報輸入錯誤。
        await Expect(await admin.PostAsync(path, content: null), HttpStatusCode.BadRequest, "invalid_input");
        foreach (var invalid in new object?[]
        {
            null,
            new { },
            new { items = Array.Empty<object>() },
            new { items = new object?[] { target, null } },
            new { items = new object[] { target, new { second.Id } } },
            new { items = new object[] { target, new { id = " ", second.Version } } },
            new { items = new object[] { target, new { second.Id, version = 1 } } },
            new { items = new[] { target, target } },
            new { valid.items, active = false },
            new { items = new object[] { target, new { second.Id, second.Version, active = false } } }
        })
        {
            // 混合有效與無效目標的請求也須拒絕整批。
            await Expect(await admin.PostAsJsonAsync(path, invalid), HttpStatusCode.BadRequest, "invalid_input");
        }

        // 任一筆不存在或版本過期時，同批其他目標不得異動。
        await Expect(await admin.PostAsJsonAsync(path,
            new { items = new[] { target, new { Id = "missing-" + Guid.NewGuid().ToString("N"), second.Version } } }),
            HttpStatusCode.NotFound, "not_found");
        await Expect(await admin.PostAsJsonAsync(path,
            new { items = new[] { target, new { second.Id, Version = "stale-version" } } }),
            HttpStatusCode.Conflict, "stale_version");
        if (accounts)
        {
            // 管理帳號是不可用的目標，即使呼叫端知道該帳號的當前版本。
            var adminVersion = (await Harness.Me(admin)).Version;
            await Expect(await admin.PostAsJsonAsync(path,
                new { items = new[] { target, new { Id = app.UserId, Version = adminVersion } } }),
                HttpStatusCode.NotFound, "not_found");
            Assert.Equal(["Administrator"], (await Harness.Me(admin)).Roles);
        }

        // 重新讀取兩筆設定及既有業務登入，確認狀態、版本及登入全數保留。
        var saved = await Read(admin);
        Assert.Equal(department, saved.Departments.Single(d => d.Id == department.Id));
        Assert.Equal(secondDepartment, saved.Departments.Single(d => d.Id == secondDepartment.Id));
        Assert.Equal(account, saved.Accounts.Single(a => a.Id == account.Id));
        Assert.Equal(secondAccount, saved.Accounts.Single(a => a.Id == secondAccount.Id));
        Assert.Equal(account.UserName, (await Harness.Me(signedInTarget)).UserName);
    }

    // 直接重播登入時簽發的 Cookie，排除用戶端清理造成的假陽性。
    private static async Task<HttpClient> OldSession(Harness app, RequisitionApprovalScenario.Business business)
    {
        // 由真實登入回應取得 Cookie，再放入不使用 Cookie 容器的 client。
        using var signIn = app.Client();
        using var response = await Harness.Login(signIn, business.Name, business.Password);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("procurement.session=")).Split(';')[0];
        var replay = app.Client(cookies: false);
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        return replay;
    }
}
