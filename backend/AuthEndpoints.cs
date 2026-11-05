using Microsoft.AspNetCore.Antiforgery;

namespace Procurement;

internal static class AuthEndpoints
{
    // 集中註冊身分 API，讓啟動入口只需掛載一次，並由 AuthService 處理登入與交易規則。
    public static void MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // 共用身分 API 前綴，讓各端點只需宣告自己的子路徑。
        var group = endpoints.MapGroup("/api/auth");

        // 發出防偽權杖，讓前端可用同一瀏覽器的 Cookie 送出後續 POST。
        group.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) =>
            Results.Ok(new CsrfResponse(
                antiforgery.GetAndStoreTokens(http).RequestToken ?? throw new InvalidOperationException())));

        // 把登入請求交給 AuthService，讓 HTTP 與交易驗證各自保有明確責任。
        group.MapPost(
            "/login",
            (LoginRequest request, HttpContext http, AuthService auth, CancellationToken ct) =>
                auth.Login(request, http, ct));

        // 查詢目前身分，沿用 AuthService 從資料庫取得的授權資訊。
        group.MapGet("/me", (HttpContext http, AuthService auth, CancellationToken ct) =>
            auth.Me(http.User, ct));

        // 將密碼變更交給 AuthService，讓雜湊更新和撤銷維持同一交易。
        group.MapPost(
            "/change-password",
            (PasswordRequest request, HttpContext http, AuthService auth, CancellationToken ct) =>
                auth.ChangePassword(request, http, ct));

        // 將登出交給 AuthService，統一處理伺服器端撤銷與 Cookie 清除。
        group.MapPost("/logout", (HttpContext http, AuthService auth, CancellationToken ct) =>
            auth.Logout(http, ct));
    }
}
