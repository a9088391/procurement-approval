using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Procurement;

// 同源入口只負責 HTTP 與組態；規則、交易與本機維護各有明確邊界。
var builder = WebApplication.CreateBuilder(args);
var localDirectory = builder.Configuration["LocalDirectory"] ?? "/local";

// 準備憑證不需啟動 HTTP 或連資料庫，供 Compose 在資料庫啟動前執行。
if (args.FirstOrDefault() == "prepare-local")
{
    LocalSetup.Prepare(localDirectory);
    return;
}

// 優先採用環境組態的連線字串。展示容器缺少此組態時才讀本機受保護的密碼檔。
var connection = builder.Configuration.GetConnectionString("Database");
if (connection is null)
{
    var cs = new NpgsqlConnectionStringBuilder
    {
        Host = "db",
        Database = "procurement",
        Username = "procurement",
        Password = File.ReadAllText(Path.Combine(localDirectory, "database-password"))
    };
    connection = cs.ConnectionString;
}

// 一般紀錄不包含 SQL 參數或 Data Protection 金鑰內容。
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);

// 在同一資料庫設定 EF 與 Identity store，讓帳號、角色與登入紀錄可以一起管理。
builder.Services.AddDbContext<AuthDb>(options => options.UseNpgsql(connection));
builder
    .Services
    .AddIdentityCore<AppUser>()
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AuthDb>();

// 時鐘和來源節流全程序共用。登入、管理服務及 Cookie 事件跟隨請求並共用 DbContext。
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<SessionEvents>();
builder.Services.AddScoped<OrganizationService>();

// 讓格式錯誤走共用的 JSON 錯誤處理，而非由路由直接產生空回應。
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

// 金鑰與資料庫持久保存，容器重啟後既有 Cookie 才能繼續解密。
LocalSetup.ProtectDirectory(Path.Combine(localDirectory, "keys"));
builder
    .Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(localDirectory, "keys")))
    .SetApplicationName("Procurement");

// 註冊同源登入 Cookie，固定期限並掛上資料庫驗證事件，以便即時撤銷登入。
builder
    .Services
    .AddAuthentication(AuthService.Scheme)
    .AddCookie(AuthService.Scheme, options =>
    {
        // 限制 Cookie 的讀取與跨站傳送，並配合本機 HTTP 或 HTTPS 的請求方式。
        options.Cookie.Name = "procurement.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

        // 保持八小時絕對期限，且每次驗證都由 SessionEvents 判斷登入紀錄。
        options.SlidingExpiration = false;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.EventsType = typeof(SessionEvents);
    });

// Cookie 與登入服務共用 DI 時鐘，讓兩邊的期限使用同一時間來源。
builder
    .Services
    .AddOptions<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(AuthService.Scheme)
    .Configure<TimeProvider>((options, clock) => options.TimeProvider = clock);

// 將 CSRF 權杖綁定至 Cookie，並要求前端明確提供權杖標頭來驗證變更請求。
builder.Services.AddAntiforgery(options =>
{
    // 固定標頭名稱並限制防偽 Cookie 的讀取與跨站傳送。
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "procurement.csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
});

// CLI 與網站共用 migration／初始化；維護命令完成後離開，不開放 HTTP 維護端點。
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    // 先套用 migration，讓初始化和 HTTP 端點取得所需的資料表。
    var db = scope.ServiceProvider.GetRequiredService<AuthDb>();
    await db.Database.MigrateAsync();

    // 只有明確命令或組態要求時才補帳號，避免一般啟動自行建立示範帳號。
    if (args.FirstOrDefault() is "initialize-demo"
        or "create-admin"
        || builder.Configuration.GetValue<bool>("InitializeDemo"))
        await LocalSetup.Initialize(
            db,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>(),
            localDirectory,
            args.FirstOrDefault() == "create-admin");
}

// 維護命令完成後直接結束程序，讓 CLI 呼叫端可接續下一步。
if (args.FirstOrDefault() is "initialize-demo" or "create-admin")
    return;

// 將下游例外轉成一致的 API 回應，讓呼叫端辨識失敗並保護例外內容與請求資料。
app.Use(async (http, next) =>
{
    // 包住後續 middleware 和端點，集中處理各種錯誤回應。
    try
    {
        await next(http);
    }
    catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
    {
        // 用戶端已取消請求，略過錯誤回應。
    }
    catch (BadHttpRequestException)
    {
        // 格式錯誤回傳明確代碼，讓呼叫端可以修正輸入。
        await AuthService.Error(400, "invalid_input", "請求格式不正確。").ExecuteAsync(http);
    }
    catch (Exception)
    {
        // 500 可能發生在交易提交後；此處不宣稱操作已回滾，也不記錄敏感例外本文。
        app.Logger.LogError("請求失敗；追蹤識別碼 {TraceId}", http.TraceIdentifier);
        if (!http.Response.HasStarted)
        {
            // 只在尚未送出回應時改寫狀態碼與內容。
            http.Response.Clear();
            await AuthService.Error(500, "server_error", "系統暫時無法完成操作，請重試。").ExecuteAsync(http);
        }
    }
});

// 順序不可交換：先驗證 Cookie，再檢查登入資格及 CSRF，最後才計入登入來源節流。
app.UseAuthentication();

// 在同一處處理驗證後的請求防護，讓端點只接收通過檢查的呼叫。
app.Use(async (http, next) =>
{
    // 身分與權杖內容不應由瀏覽器或中介快取重用。
    http.Response.Headers.CacheControl = "no-store";

    // 路由忽略大小寫與尾斜線；安全判斷必須採同樣語意，避免端點別名繞過節流。
    var requestPath = http.Request.Path;
    var path = requestPath.Value?.TrimEnd('/');
    var login = string.Equals(path, "/api/auth/login", StringComparison.OrdinalIgnoreCase);
    var csrf = string.Equals(path, "/api/auth/csrf", StringComparison.OrdinalIgnoreCase);
    var api = requestPath.StartsWithSegments("/api");
    var anonymousAuth = login || csrf;
    var protectedAuth = requestPath.StartsWithSegments("/api/auth") && !anonymousAuth;
    var protectedPath = requestPath.StartsWithSegments("/api/admin") || protectedAuth;
    var post = HttpMethods.IsPost(http.Request.Method);

    // 先辨識失效登入，避免舊身分 token 因撤銷時序被誤報為 CSRF 錯誤。
    if (protectedPath
        && http
            .User
            .Identity
            ?.IsAuthenticated != true)
    {
        await AuthService.Unauthorized().ExecuteAsync(http);
        return;
    }

    // 強制改密碼期間只允許四項身分操作；新掛載的 API 預設也受此限制。
    var readRecovery = csrf || string.Equals(path, "/api/auth/me", StringComparison.OrdinalIgnoreCase);
    var writeRecovery = string.Equals(path, "/api/auth/change-password", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "/api/auth/logout", StringComparison.OrdinalIgnoreCase);
    var passwordRecovery = (HttpMethods.IsGet(http.Request.Method) && readRecovery)
        || (post && writeRecovery);
    if (api
        && !passwordRecovery
        && http.Items.ContainsKey(SessionEvents.PasswordChangeRequired))
    {
        await AuthService.Error(403, "forbidden", "請先變更臨時密碼。").ExecuteAsync(http);
        return;
    }

    // POST 可能異動狀態，先驗證防偽權杖再允許執行。
    if (api && post)
    {
        try
        {
            // 由框架比對要求中的標頭與瀏覽器送來的防偽 Cookie。
            await http
                .RequestServices
                .GetRequiredService<IAntiforgery>()
                .ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            // 無效權杖回傳可辨識的錯誤，並在進入端點前中止請求。
            await AuthService.Error(400, "invalid_csrf", "安全權杖已失效，請重新整理後再試。").ExecuteAsync(http);
            return;
        }
    }

    // 防偽驗證通過後才計入登入額度，從直連 IP 取來源以免信任外部標頭。
    if (login
        && post
        && !http
            .RequestServices
            .GetRequiredService<LoginThrottle>()
            .Allow(http
                .Connection
                .RemoteIpAddress
                ?.ToString() ?? "local"))
    {
        await AuthService.Error(429, "rate_limited", "登入嘗試過於頻繁，請稍後重試。").ExecuteAsync(http);
        return;
    }

    // 通過所有檢查後，將請求交給對應端點或靜態資源處理。
    await next(http);
});

// 各模組集中註冊自己的 API，啟動入口只負責掛載。
app.MapAuthEndpoints();
app.MapOrganizationEndpoints();

// 讓編譯後的前端與 API 使用同一來源，瀏覽器可直接帶上登入 Cookie。
app.UseDefaultFiles();
app.UseStaticFiles();
app.Run();

// 讓 WebApplicationFactory 透過正式入口測試 middleware 與 HTTP 契約。
public partial class Program;
