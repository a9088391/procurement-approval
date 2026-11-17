namespace Procurement;

// 管理路由只繫結請求並轉交服務，由服務依即時身分及組織設定判斷可執行的操作。
internal static class OrganizationEndpoints
{
    // 集中掛載組織設定路由，沿用入口的登入及 CSRF 防護。
    internal static void MapOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // 所有讀寫使用同一管理前綴，保持直接 HTTP 呼叫與畫面的授權一致。
        var admin = endpoints.MapGroup("/api/admin");

        // 查詢只回傳管理設定及最新版本，供畫面讀回實際保存結果。
        admin.MapGet("/organization", (HttpContext http, OrganizationService organization, CancellationToken ct) =>
            organization.Read(http.User, ct));

        // 新增部門時由服務產生識別，避免呼叫端覆寫既有設定。
        admin.MapPost("/departments", (
            CreateDepartmentRequest request, HttpContext http, OrganizationService organization, CancellationToken ct) =>
            organization.CreateDepartment(request, http.User, ct));

        // 部門更新保留穩定識別及關聯，讓既有帳號可維持停用部門歸屬。
        admin.MapPost("/departments/{id}", (
            string id, UpdateDepartmentRequest request, HttpContext http, OrganizationService organization, CancellationToken ct) =>
            organization.UpdateDepartment(id, request, http.User, ct));

        // 單筆與批次停用共用同一入口，讓兩者適用相同的版本及交易檢查。
        admin.MapPost("/departments/deactivate", (
            DeactivateOrganizationRequest? request, HttpContext http, OrganizationService organization, CancellationToken ct) =>
            organization.DeactivateDepartments(request, http.User, ct));

        // HTTP 只開放新增業務帳號，避免從管理畫面新增管理員。
        admin.MapPost("/accounts", (
            CreateBusinessAccountRequest request, HttpContext http, OrganizationService organization, CancellationToken ct) =>
            organization.CreateAccount(request, http.User, ct));

        // 業務設定和角色交由同一操作保存，免得只更新其中一部分。
        admin.MapPost("/accounts/{id}", (
            string id, UpdateBusinessAccountRequest request, HttpContext http, OrganizationService organization, CancellationToken ct) =>
            organization.UpdateAccount(id, request, http.User, ct));

        // 帳號停用獨立接受目標版本，管理員可在保留原設定的情況下撤銷登入。
        admin.MapPost("/accounts/deactivate", (
            DeactivateOrganizationRequest? request, HttpContext http, OrganizationService organization, CancellationToken ct) =>
            organization.DeactivateAccounts(request, http.User, ct));

        // 密碼重設使用獨立請求欄位，避免一般設定表單能變更憑證。
        admin.MapPost("/accounts/{id}/reset-password", (
            string id, ResetBusinessPasswordRequest request, HttpContext http, OrganizationService organization, CancellationToken ct) =>
            organization.ResetPassword(id, request, http.User, ct));
    }
}
