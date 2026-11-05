using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Procurement;

// 集中登入、改密碼與登出的規則，並以帳號列鎖協調同一帳號的登入異動。
public sealed class AuthService(AuthDb db, IPasswordHasher<AppUser> hasher, TimeProvider clock)
{
    public const string Scheme = "local";

    // 檢查密碼長度。依 .NET 的 UTF-16 單位計算，保留使用者輸入的空白。
    public static bool ValidPassword(string? value) => value is { Length: >= 12 and <= 128 };

    // 單行欄位只拒絕 CR 與 LF，其他字元仍由各欄位沿用原有規則處理。
    internal static bool ContainsCrOrLf(string? value) => value.AsSpan().ContainsAny('\r', '\n');

    // 將狀態碼和錯誤內容組成 JSON，讓呼叫端以穩定代碼決定恢復方式。
    public static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorResponse(code, message), statusCode: status);

    // 以共用的 JSON 401 表示登入無效，讓所有受保護端點都能引導重新登入。
    public static IResult Unauthorized() => Error(401, "unauthenticated", "請重新登入。");

    // 沿用 Identity 雜湊驗證，讓登入、改密碼及重設共用對目前憑證的判定。
    internal bool PasswordMatches(AppUser user, string password) => user.PasswordHash is { } hash
        && hasher.VerifyHashedPassword(user, hash, password) != PasswordVerificationResult.Failed;

    // 在呼叫端的交易內鎖定並讀取帳號，讓同一帳號的驗證與異動按交易順序處理。
    public Task<AppUser?> LockUser(string id, CancellationToken ct)
    {
        // 同一請求的 Cookie 驗證可能已追蹤舊帳號；列鎖之後仍必須重新載入目前資料。
        db.ChangeTracker.Clear();

        // 將帳號 ID 當作 SQL 參數傳入，並持有該列鎖到呼叫端的交易結束。
        return db
            .Users
            .FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
    }

    // 回傳仍有效的登入紀錄。呼叫端必須先鎖帳號，才能以資料庫當下的狀態判斷撤銷、停用與逾期。
    public async Task<LoginSession?> CurrentSession(AppUser user, ClaimsPrincipal principal, CancellationToken ct)
    {
        // 同時比對 Cookie 的登入識別與帳號 ID，確保讀到的是該帳號的這一次登入。
        var id = principal.FindFirstValue("session");
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == id && s.UserId == user.Id, ct);

        // 只取一次 UTC 時間，讓絕對期限與閒置期限以同一個時間點判斷。
        var now = clock.GetUtcNow();

        // 帳號須啟用且登入紀錄存在，並同時未達八小時絕對期限與三十分鐘閒置期限。
        return user.Active
            && session is not null
            && now < session.CreatedAt.AddHours(8)
            && now < session.LastSeenAt.AddMinutes(30)
            ? session
            : null;
    }

    // 在呼叫端的交易內取得帳號及有效登入，供身分讀取與受保護異動重新確認授權。
    internal async Task<AppUser?> CurrentLockedUser(ClaimsPrincipal principal, CancellationToken ct)
    {
        // 帳號列鎖與登入檢查採固定順序，避免撤銷在兩個步驟之間生效。
        var user = await LockUser(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", ct);
        return user is not null && await CurrentSession(user, principal, ct) is not null ? user : null;
    }

    // 呼叫端持有已驗證操作者的帳號列鎖；直接更新該次登入，以免異動目標時清除追蹤而遺漏紀錄。
    internal Task RecordSessionActivity(string userId, ClaimsPrincipal principal, CancellationToken ct)
    {
        // 只更新這次登入的最後活動時間，保留建立時間及同帳號的其他登入。
        var id = principal.FindFirstValue("session");
        var now = clock.GetUtcNow();
        return db
            .Sessions
            .Where(s => s.Id == id && s.UserId == userId)
            .ExecuteUpdateAsync(update => update.SetProperty(s => s.LastSeenAt, now), ct);
    }

    // 從 Identity 的角色對應查詢目前角色，讓身分端點與管理授權採相同來源。
    internal IQueryable<string> RoleNames(string userId) =>
        from ur in db.UserRoles
        join role in db.Roles on ur.RoleId equals role.Id
        where ur.UserId == userId
        select role.Name ?? "";

    // 驗證帳密並建立登入。HTTP 入口先檢查防偽與節流，此處用列鎖協調失敗計數和登入寫入。
    public async Task<IResult> Login(LoginRequest request, HttpContext http, CancellationToken ct)
    {
        // 先拒絕無法處理的帳密格式，避免開啟不必要的交易。
        if (request.Password is not { } password
            || !ValidPassword(password)
            || string.IsNullOrWhiteSpace(request.UserName)
            || request.UserName.Length > 256)
            return Error(400, "invalid_input", "帳號必填，密碼須為 12–128 字元。");

        // 以正規化帳號查詢並鎖列，清除追蹤避免沿用同一請求中先前讀到的帳號。
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ChangeTracker.Clear();
        var normalized = request.UserName.ToUpperInvariant();
        var user = await db
            .Users
            .FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"NormalizedUserName\" = {normalized} FOR UPDATE")
            .SingleOrDefaultAsync(ct);

        // 在取得列鎖後取時。找不到帳號或帳號停用時，沿用同一個帳密錯誤。
        var now = clock.GetUtcNow();
        if (user is null || !user.Active)
            return Error(401, "invalid_credentials", "帳號或密碼不正確。");

        // 鎖定未到期時拒絕驗密，保留原本的解除時間。
        if (user.LockoutEnd > now)
            return Error(423, "account_locked", "連續登入失敗，請於 15 分鐘後重試。");

        // 到期當下即可重試，舊失敗次數不帶入新的鎖定週期。
        if (user.LockoutEnd is not null)
        {
            user.LockoutEnd = null;
            user.AccessFailedCount = 0;
        }

        // 用 Identity 驗證保存的雜湊，並在同一交易內累計失敗。
        if (!PasswordMatches(user, password))
        {
            // 達失敗門檻才開始新一輪鎖定。
            if (++user.AccessFailedCount >= 5)
                user.LockoutEnd = now.AddMinutes(15);

            // 先提交失敗計數，再依實際鎖定結果回應。
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return user.LockoutEnd is null
                ? Error(401, "invalid_credentials", "帳號或密碼不正確。")
                : Error(423, "account_locked", "連續登入失敗，請於 15 分鐘後重試。");
        }

        // 驗密成功後重置失敗計數，建立可獨立撤銷的登入紀錄並一起提交。
        user.AccessFailedCount = 0;
        var session = new LoginSession
        {
            UserId = user.Id,
            CreatedAt = now,
            LastSeenAt = now
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Cookie 只帶識別，不快取角色；交易已提交，後續回應失敗不會撤回登入紀錄。
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.UserName ?? ""),
                new Claim("session", session.Id)
            ],
            Scheme));

        // 發出工作階段 Cookie，將伺服器端的驗證期限固定為八小時。
        await http.SignInAsync(Scheme, principal, new AuthenticationProperties
        {
            IsPersistent = false,
            IssuedUtc = now,
            ExpiresUtc = now.AddHours(8)
        });
        return Results.NoContent();
    }

    // 在同一帳號列鎖下讀取有效登入、角色與改密碼版本，讓 Cookie 發出後的變更也能反映。
    public async Task<IResult> Me(ClaimsPrincipal principal, CancellationToken ct)
    {
        // 即使 middleware 已驗證 Cookie，仍在列鎖下確認登入尚未被停用或撤銷。
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await CurrentLockedUser(principal, ct);
        if (user is null)
            return Unauthorized();

        // 角色異動也使用同一列鎖，因此排序後的角色與帳號版本屬於同一次讀取。
        var roles = await RoleNames(user.Id)
            .OrderBy(r => r)
            .ToArrayAsync(ct);
        await tx.CommitAsync(ct);

        // 只傳回前端需要的身分資料，並讓改密碼可帶回當次讀取的版本。
        return Results.Ok(new IdentityResponse(
            user.UserName ?? "",
            user.AccountType,
            roles,
            user.ConcurrencyStamp ?? "",
            user.MustChangePassword));
    }

    // 更換密碼並撤銷所有登入。重查登入與版本，並共用交易以確保密碼與撤銷一起生效。
    public async Task<IResult> ChangePassword(PasswordRequest request, HttpContext http, CancellationToken ct)
    {
        // 在開啟交易前確認兩個密碼和版本都有可用的輸入。
        if (request.CurrentPassword is not { } currentPassword
            || request.NewPassword is not { } newPassword
            || !ValidPassword(currentPassword)
            || !ValidPassword(newPassword)
            || string.IsNullOrEmpty(request.Version))
            return Error(400, "invalid_input", "密碼須為 12–128 字元，並提供目前版本。");

        // 取得帳號列鎖後再驗證登入，避免使用 middleware 先前已通過的結果。
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await CurrentLockedUser(http.User, ct);
        if (user is null)
            return Unauthorized();

        // 只接受與目前帳號相符的版本，避免覆寫較新的資料。
        if (user.ConcurrencyStamp != request.Version)
            return Error(409, "stale_version", "資料已變更，請重新取得目前身分。");

        // 有效登入仍須提供目前密碼，避免僅憑 Cookie 便能變更憑證。
        if (!PasswordMatches(user, currentPassword))
            return Error(400, "wrong_password", "目前密碼不正確。");

        // 強制改密碼的新值須能在單行欄位輸入，並與臨時密碼不同。
        if (user.MustChangePassword && ContainsCrOrLf(newPassword))
            return Error(400, "invalid_input", "新密碼不可包含換行。");
        if (user.MustChangePassword && newPassword == currentPassword)
            return Error(400, "invalid_input", "新密碼必須與臨時密碼不同。");

        // 解除強制狀態、保存新憑證及撤銷登入同一交易提交，失敗時一起回滾。
        await ReplaceCredentials(user, newPassword, false, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // 此後若 Cookie／HTTP 傳送失敗，結果仍可能已生效，呼叫端不可自動重送改密碼。
        await http.SignOutAsync(Scheme);
        return Results.NoContent();
    }

    // 呼叫端持有帳號列鎖並提交交易；自助變更與管理重設共用憑證寫入及撤銷。
    internal async Task ReplaceCredentials(AppUser user, string password, bool mustChangePassword, CancellationToken ct)
    {
        // 換新雜湊與版本，並清除舊憑證累積的登入失敗與鎖定狀態。
        user.PasswordHash = hasher.HashPassword(user, password);
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        user.MustChangePassword = mustChangePassword;
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;

        // 在同一交易撤銷所有舊登入，即使持有舊 Cookie 也必須重登。
        await RevokeSessions(user.Id, ct);
    }

    // 在呼叫端持有帳號列鎖的交易內整批撤銷，供停用與密碼變更共用。
    internal Task RevokeSessions(string userId, CancellationToken ct) => db
        .Sessions
        .Where(s => s.UserId == userId)
        .ExecuteDeleteAsync(ct);

    // 撤銷目前登入並清除 Cookie。以單筆登入紀錄為範圍，保留其他裝置的登入。
    public async Task<IResult> Logout(HttpContext http, CancellationToken ct)
    {
        // 與改密碼共用帳號列鎖，並在鎖內確認這次登入仍存在。
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockUser(http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", ct);
        if (user is null || await CurrentSession(user, http.User, ct) is not { } session)
            return Unauthorized();

        // 先提交伺服器端撤銷，讓原 Cookie 即使未成功清除也不能重播。
        db.Sessions.Remove(session);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // 撤銷已提交，再通知用戶端清除目前的 Cookie。
        await http.SignOutAsync(Scheme);
        return Results.NoContent();
    }
}

// 每次 Cookie 驗證回查登入紀錄；資料庫的撤銷與期限不等待 Cookie 自己到期。
public sealed class SessionEvents(AuthDb db, AuthService auth, TimeProvider clock) : CookieAuthenticationEvents
{
    internal static readonly object PasswordChangeRequired = new();

    // 驗證 Cookie 對應的登入，並按請求方法更新閒置時間，讓伺服器端撤銷立即生效。
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        // 讓列鎖與讀取共用交易，並沿用 HTTP 的取消訊號。
        var ct = context.HttpContext.RequestAborted;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await auth.LockUser(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", ct);

        // 帳號與 Cookie 身分俱在時才查登入紀錄，找不到有效登入則拒絕該身分。
        var session = user is null || context.Principal is null
            ? null
            : await auth.CurrentSession(user, context.Principal, ct);
        if (session is null)
            context.RejectPrincipal();
        else
        {
            // 強制改密碼只標記在本次請求，避免把會變更的狀態寫進 Cookie。
            if (user?.MustChangePassword == true)
                context.HttpContext.Items[PasswordChangeRequired] = true;

            // POST 仍待防偽與輸入驗證；管理寫入成功後才由其交易記錄活動。
            if (!HttpMethods.IsPost(context.HttpContext.Request.Method))
            {
                session.LastSeenAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
        }

        // 提交活動更新並釋放列鎖，讓後續異動可取得當下的狀態。
        await tx.CommitAsync(ct);
    }
}

// 單體內依直連來源計數；不信任外部可偽造的 Forwarded 標頭。
public sealed class LoginThrottle(TimeProvider clock)
{
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> windows = [];

    // 判斷來源是否尚有登入額度。包含不存在的帳號，並鎖住計數避免並行請求共用同一額度。
    public bool Allow(string source)
    {
        // 過期清理與計數都在同一把鎖內完成，避免讀到中途的視窗狀態。
        lock (windows)
        {
            var now = clock.GetUtcNow();
            // 先取得過期來源快照，再逐筆移除，避免邊列舉字典邊改動它。
            foreach (var expired in windows
                .Where(p => now >= p
                    .Value
                    .Start
                    .AddMinutes(1))
                .Select(p => p.Key)
                .ToArray())
                windows.Remove(expired);

            // 無視窗便以本次時間起算，以累加前的次數判斷這次是否仍在三十次額度內。
            var window = windows.GetValueOrDefault(source, (now, 0));
            windows[source] = (window.Item1, window.Item2 + 1);
            return window.Item2 < 30;
        }
    }
}
