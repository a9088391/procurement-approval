namespace Procurement;

// 簽核與一般查詢共用請購 API 前綴，HTTP 路由只負責繫結請求及傳遞身分。
internal static class RequisitionApprovalEndpoints
{
    // 以獨立動作表示合法操作，讓客戶端無法指定關卡或任意目標狀態。
    internal static void MapRequisitionApprovalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // 清單、報表、明細及歷程交給同一個服務；查詢字串由服務完成授權後再驗證。
        var requisitions = endpoints.MapGroup("/api/requisitions");
        requisitions.MapGet("", (HttpContext http, RequisitionApprovalService service, CancellationToken ct) =>
            service.ReadCollection(http.Request.Query, http.User, RequisitionReadKind.List, ct));
        requisitions.MapGet("/statistics", (HttpContext http, RequisitionApprovalService service, CancellationToken ct) =>
            service.ReadCollection(http.Request.Query, http.User, RequisitionReadKind.Statistics, ct));
        requisitions.MapGet("/csv", (HttpContext http, RequisitionApprovalService service, CancellationToken ct) =>
            service.ReadCollection(http.Request.Query, http.User, RequisitionReadKind.Csv, ct));
        requisitions.MapGet("/{id}", (string id, HttpContext http, RequisitionApprovalService service, CancellationToken ct) =>
            service.Read(id, http.User, ct));

        // 每個 POST 只接收目前版本；資格、指派及狀態由交易內的資料決定。
        requisitions.MapPost("/{id}/submit", (
            string id, ApprovalRequest request, HttpContext http, RequisitionApprovalService service, CancellationToken ct) =>
            service.Submit(id, request, http.User, ct));
        requisitions.MapPost("/{id}/withdraw", (
            string id, ApprovalRequest request, HttpContext http, RequisitionApprovalService service, CancellationToken ct) =>
            service.Withdraw(id, request, http.User, ct));
        requisitions.MapPost("/{id}/approve", (
            string id, ApprovalRequest request, HttpContext http, RequisitionApprovalService service, CancellationToken ct) =>
            service.Decide(id, request, http.User, approve: true, ct));
        requisitions.MapPost("/{id}/reject", (
            string id, ApprovalRequest request, HttpContext http, RequisitionApprovalService service, CancellationToken ct) =>
            service.Decide(id, request, http.User, approve: false, ct));

        // 改派路由獨立於業務查詢，僅能讀取有限待辦與修改指定人選。
        var reassignments = endpoints.MapGroup("/api/admin/reassignments");
        reassignments.MapGet("", (HttpContext http, RequisitionApprovalService service, CancellationToken ct) =>
            service.ListReassignments(http.User, ct));
        reassignments.MapPost("/{id}", (
            string id, ReassignmentRequest request, HttpContext http, RequisitionApprovalService service, CancellationToken ct) =>
            service.Reassign(id, request, http.User, ct));
    }
}
