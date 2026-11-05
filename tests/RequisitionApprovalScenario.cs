using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Procurement;
using Xunit;
using static OrganizationTests;
using static RequisitionDraftTests;

// 經正式管理與身分端點準備跨部門三人分工；每個案例獨立擁有登入與時鐘。
internal sealed class RequisitionApprovalScenario : IAsyncDisposable
{
    private readonly List<HttpClient> clients = [];
    internal Harness App { get; } = new();
    internal HttpClient Admin => clients[0];
    internal DepartmentState Department { get; private set; } = new("", "", false, "");
    internal DepartmentState OtherDepartment { get; private set; } = new("", "", false, "");
    private Business? owner;
    private Business? manager;
    private Business? finance;
    internal Business Owner => owner ?? throw new InvalidOperationException();
    internal Business Manager => manager ?? throw new InvalidOperationException();
    internal Business Finance => finance ?? throw new InvalidOperationException();

    // 主管原本分在別的部門，使測試能分別驗證部門可見範圍與跨部門指派。
    internal static async Task<RequisitionApprovalScenario> Start()
    {
        // 只有測試所需的三個業務帳號，額外角色由需要的案例自行建立。
        var scenario = new RequisitionApprovalScenario();
        try
        {
            // 建立管理員及兩個部門，再設定有效的預設人選。
            await scenario.App.Seed("admin", "Administrator");
            scenario.clients.Add(await scenario.App.SignedIn());
            scenario.Department = await CreateDepartment(scenario.Admin);
            scenario.OtherDepartment = await CreateDepartment(scenario.Admin);
            scenario.owner = await scenario.AddBusiness(scenario.Department.Id);
            scenario.manager = await scenario.AddBusiness(scenario.OtherDepartment.Id, manager: true);
            scenario.finance = await scenario.AddBusiness(scenario.OtherDepartment.Id, finance: true);
            await scenario.Update(scenario.Owner, edit => edit with
            {
                ManagerReviewerId = scenario.Manager.Id,
                FinanceReviewerId = scenario.Finance.Id
            });
            return scenario;
        }
        catch
        {
            // 安排途中失敗也釋放已開啟的登入及 HTTP 主機。
            await scenario.DisposeAsync();
            throw;
        }
    }

    // 以正常登入和改密碼入口準備業務 client，亦可保留初次登入狀態以驗證限制。
    internal async Task<Business> AddBusiness(string departmentId, bool manager = false, bool finance = false, bool ready = true)
    {
        // 每人使用不同的隨機憑證，強制改密碼的案例保留尚未完成設定的登入。
        var temporary = TemporaryPassword();
        var account = await CreateAccount(Admin, departmentId, temporary, isManager: manager, isFinance: finance);
        var password = temporary;
        if (ready)
        {
            // 走正式改密碼，等伺服器撤銷舊登入後重新取得新的 Cookie 和權杖。
            using var setup = await App.SignedIn(account.UserName, temporary);
            password = TemporaryPassword();
            await Expect(await setup.PostAsJsonAsync("/api/auth/change-password",
                new PasswordRequest(temporary, password, (await Harness.Me(setup)).Version)), HttpStatusCode.NoContent);
        }
        var client = await App.SignedIn(account.UserName, password);
        clients.Add(client);
        return new Business(account.Id, account.UserName, password, client);
    }

    // 由正式管理讀取取得目前版本，再套用本案所需的資格或部門變更。
    internal async Task Update(Business business, Func<AccountEdit, AccountEdit> edit)
    {
        var account = (await Read(Admin)).Accounts.Single(a => a.Id == business.Id);
        await Expect(await Admin.PostAsJsonAsync($"{AccountsPath}/{business.Id}", edit(account.Edit())), HttpStatusCode.NoContent);
    }

    // 建立已填齊草稿；預設金額可用小數精確核對，不依賴正式計算函式。
    internal Task<JsonElement> Draft(string subject = "請購標籤", int count = 1, int quantity = 3, decimal unitPrice = 0.29m) =>
        CreateDraft(Owner.Client, new
        {
            subject,
            purpose = "整理設備\n補充庫存",
            lines = Enumerable
                .Range(1, count)
                .Select(i => new { itemName = $"標籤 {i}", unit = "張", quantity, unitPrice })
                .ToArray()
        });

    // 每次案例結束釋放登入及主機，保留測試庫供下個獨立案例使用。
    public async ValueTask DisposeAsync()
    {
        // 先釋放所有 HTTP client，再釋放處理請求的主機。
        foreach (var client in clients)
            client.Dispose();
        await App.DisposeAsync();
    }

    // 保留測試身分與已登入 client，競爭案例可用相同帳號另開一筆登入。
    internal sealed record Business(string Id, string Name, string Password, HttpClient Client);
}
