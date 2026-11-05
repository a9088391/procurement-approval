using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Procurement;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

// 時鐘與交易屏障只從 WAF 的 DI 注入；正式程式沒有測試專用 API。
public sealed class TestClock : TimeProvider
{
    private long ticks = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero).Ticks;

    // 以原子讀取取得測試時鐘的 UTC 值，供並行 HTTP 請求安全共用。
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

    // 原子推進測試時間，讓期限測試無須等待真實時間經過。
    public void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
}

// 暫停已提交交易的請求，讓測試固定插入「驗證完成、端點尚未執行」的撤權時序。
public sealed class CommitGate : DbTransactionInterceptor
{
    private int armed;

    public TaskCompletionSource Arrived { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 只啟用下一次提交的屏障，避免同一個測試的後續交易也被暫停。
    public void Arm() => Interlocked.Exchange(ref armed, 1);

    // 在指定交易提交後暫停請求，讓測試可在列鎖釋放後插入另一筆交易。
    public override async Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        // 原子消耗一次性旗標，避免後續交易進入同一屏障。
        if (Interlocked.Exchange(ref armed, 0) == 0)
            return;

        // 通知測試已到指定時序，等待放行並限制等待時間以免測試卡住。
        Arrived.TrySetResult();
        await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }
}

// 真實 HTTP、Identity 與 PostgreSQL 共用的隔離入口；每個案例自行建立隨機帳號。
public sealed class Harness : WebApplicationFactory<Program>
{
    // 在啟動測試主機前驗證環境連線設定，避免 migration 或測試寫入誤接其他資料庫。
    public Harness() => VerifyTarget(Environment.GetEnvironmentVariable("ConnectionStrings__Database"));

    // 驗證指定的隔離資料庫設定，讓故障注入與清理只能在可丟棄的環境執行。
    public static void VerifyTarget(string? connection)
    {
        // 要求明確提供連線字串，避免缺少設定時使用應用程式的展示預設值。
        if (connection is null)
            throw new InvalidOperationException("必須透過隔離測試 Compose 執行。");

        // 解析後比對主機、資料庫和帳號，避免只檢查名稱而接到其他服務。
        var target = new NpgsqlConnectionStringBuilder(connection);
        if (target.Host != "db"
            || target.Database != "procurement_test"
            || target.Username != "procurement_test")
            throw new InvalidOperationException("拒絕對非指定測試資料庫執行整合測試。");
    }

    public TestClock Clock { get; } = new();
    public CommitGate Gate { get; } = new();
    public string UserName { get; } = "test_" + Guid.NewGuid().ToString("N");
    public string Password { get; } =
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
    public string UserId { get; private set; } = "";

    // 設定測試主機的時鐘與交易屏障，精準安排時序並沿用正式端點與資料庫。
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // 關閉主機的測試輸出，避免把含憑證的測試請求寫進紀錄。
        builder.ConfigureLogging(logging => logging.ClearProviders());

        // 只替換時鐘並加入攔截器，透過同一組正式服務測到登入與交易的行為。
        builder.ConfigureServices(services =>
        {
            // 主機內所有驗證共用測試時鐘，期限才能一起推進。
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            // 讓屏障攔住實際 EF 提交，以資料庫事件固定競爭時序。
            services.AddDbContext<AuthDb>(options => options.AddInterceptors(Gate));
        });
    }

    // 建立測試帳號及正式格式的雜湊，讓案例可直接從公開登入端點開始驗證。
    public async Task Seed()
    {
        // 在獨立的資料庫 scope 建立隨機帳號，避免不同案例共用身分。
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDb>();
        var user = new AppUser
        {
            UserName = UserName,
            NormalizedUserName = UserName.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString()
        };

        // 使用正式 hasher 寫入測試密碼，再保存 ID 供案例精準操作該帳號。
        user.PasswordHash = scope
            .ServiceProvider
            .GetRequiredService<IPasswordHasher<AppUser>>()
            .HashPassword(user, Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        UserId = user.Id;
    }

    // 建立保留原始狀態碼的 HTTP client，可關閉 Cookie 容器以手動重播舊 Cookie。
    public HttpClient Client(bool cookies = true) => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = cookies
    });

    // 取得並替換防偽權杖，讓後續請求帶上與當前登入身分相符的值。
    public static async Task Token(HttpClient client)
    {
        // 走正式端點，讓測試 client 收到框架產生的權杖和配對 Cookie。
        var response = await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf");

        // 先移除舊標頭，避免同一個請求帶上兩個身分的權杖。
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", response?.Token ?? throw new InvalidOperationException());
    }

    // 以測試帳號送出真實登入，可替換密碼以驗證失敗路徑與鎖定規則。
    public async Task<HttpResponseMessage> Login(HttpClient client, string? password = null)
    {
        // 先取得防偽權杖，讓登入測到帳密規則並通過正式防偽檢查。
        await Token(client);

        // 未指定密碼時使用測試帳號的原憑證，保留呼叫者覆寫的能力。
        return await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(UserName, password ?? Password));
    }

    // 回傳已登入且可異動的 client，避免案例沿用匿名權杖而提前被 CSRF 拒絕。
    public async Task<HttpClient> SignedIn()
    {
        // 先確認登入成功，讓後續失敗能歸因於案例要驗證的條件。
        var client = Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client)).StatusCode);

        // 重新取得已登入身分的權杖，完成異動請求的準備。
        await Token(client);
        return client;
    }

    // 透過身分端點取得 DTO，缺少有效回應立即失敗，避免案例以空資料繼續。
    public static async Task<IdentityResponse> Me(HttpClient client) =>
        await client.GetFromJsonAsync<IdentityResponse>("/api/auth/me") ?? throw new InvalidOperationException();

    // 在獨立的 DI scope 執行測試資料異動，讓請求內外各自取得自己的 EF 追蹤狀態。
    public async Task WithDb(Func<AuthDb, Task> action)
    {
        // 每次由 scope 解析 DbContext，並等待操作完成才釋放連線資源。
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AuthDb>());
    }
}
