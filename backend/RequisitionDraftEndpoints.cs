namespace Procurement;

// 草稿端點只繫結 HTTP 輸入，登入、所有權及版本由服務在交易內重查。
internal static class RequisitionDraftEndpoints
{
    // 集中掛載本人草稿入口，讓清單和所有操作沿用同一個登入與 CSRF 前綴。
    internal static void MapRequisitionDraftEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // 清單不讀明細，個別查詢使用系統識別重新取得目前內容。
        var drafts = endpoints.MapGroup("/api/requisitions/drafts");
        drafts.MapGet("", (HttpContext http, RequisitionDraftService service, CancellationToken ct) =>
            service.List(http.User, ct));
        drafts.MapGet("/{id}", (string id, HttpContext http, RequisitionDraftService service, CancellationToken ct) =>
            service.Read(id, http.User, ct));

        // 保存及刪除以獨立的 DTO 表示，所有異動皆走共用 POST 防偽檢查。
        drafts.MapPost("", (
            CreateDraftRequest request, HttpContext http, RequisitionDraftService service, CancellationToken ct) =>
            service.Create(request, http.User, ct));
        drafts.MapPost("/{id}", (
            string id, SaveDraftRequest request, HttpContext http, RequisitionDraftService service, CancellationToken ct) =>
            service.Save(id, request, http.User, ct));
        drafts.MapPost("/{id}/delete", (
            string id, DeleteDraftRequest request, HttpContext http, RequisitionDraftService service, CancellationToken ct) =>
            service.Delete(id, request, http.User, ct));
    }
}
