using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace Procurement;

// 集中限制可讀寫的本人草稿；保存、刪除與登入活動在同一筆 PostgreSQL 交易完成。
internal sealed class RequisitionDraftService(AuthDb db, AuthService auth, TimeProvider clock)
{
    // 清單按更新時間及識別排序，投影最少欄位且只涵蓋目前帳號的草稿。
    internal Task<IResult> List(ClaimsPrincipal principal, CancellationToken ct) => RunAsBusiness(principal, async applicantId =>
    {
        var drafts = await OwnDrafts(applicantId)
            .OrderByDescending(d => d.UpdatedAt)
            .ThenBy(d => d.Id)
            .Select(d => new DraftSummary(d.Id, d.Number, d.Subject, d.UpdatedAt))
            .ToArrayAsync(ct);
        return Results.Ok(drafts);
    }, ct, write: false);

    // 重開時取得目前版本與全部明細；不可存取的資料沿用相同的 404。
    internal Task<IResult> Read(string id, ClaimsPrincipal principal, CancellationToken ct) => RunAsBusiness(principal, async applicantId =>
    {
        var draft = await FindDraft(applicantId, id, ct, includeLines: true);
        return draft is null ? NotFound() : Results.Ok(Response(draft));
    }, ct, write: false);

    // 新草稿由系統產生單號、申請人與時間，不要求先有送審設定。
    internal Task<IResult> Create(CreateDraftRequest request, ClaimsPrincipal principal, CancellationToken ct) =>
        RunAsBusiness(principal, applicantId =>
        {
            // 在配置主檔前驗證完整輸入，避免錯誤資料進入追蹤及寫入流程。
            var input = RequisitionDraftInput.Read(request.Subject, request.Purpose, request.Lines);
            if (input is null)
                return Task.FromResult(InvalidContent());

            // 由同一個識別推導穩定的唯一單號，首次更新時間與建立時間相同。
            var id = Guid.NewGuid();
            var draft = new Requisition
            {
                Id = id,
                Number = $"PR-{id:N}",
                ApplicantId = applicantId,
                CreatedAt = CurrentTimestamp()
            };
            ApplyContent(draft, input, draft.CreatedAt);
            db.Requisitions.Add(draft);
            return Task.FromResult<IResult>(Results.Created($"/api/requisitions/drafts/{id}", Response(draft)));
        }, ct);

    // 持有帳號列鎖時比較版本，再把主檔與有順序的明細整批取代。
    internal Task<IResult> Save(string id, SaveDraftRequest request, ClaimsPrincipal principal, CancellationToken ct) =>
        RunAsBusiness(principal, async applicantId =>
        {
            // 先限制本人及從未送審狀態，再辨識版本衝突，避免從錯誤推測他人的資料。
            var draft = await FindDraft(applicantId, id, ct);
            if (draft is null)
                return NotFound();
            if (CheckVersion(draft, request.Version) is { } conflict)
                return conflict;

            // 所有欄位驗證成功才移除舊明細；之後若新增失敗，舊列也隨交易回滾復原。
            var input = RequisitionDraftInput.Read(request.Subject, request.Purpose, request.Lines);
            if (input is null)
                return InvalidContent();
            await db
                .RequisitionLines
                .Where(line => line.RequisitionId == draft.Id)
                .ExecuteDeleteAsync(ct);
            ApplyContent(draft, input, CurrentTimestamp());
            return Results.Ok(Response(draft));
        }, ct);

    // 只刪除本人目前版本的從未送審草稿，資料庫在同一交易清除其明細。
    internal Task<IResult> Delete(string id, DeleteDraftRequest request, ClaimsPrincipal principal, CancellationToken ct) =>
        RunAsBusiness(principal, async applicantId =>
        {
            // 和保存共用所有權及版本判斷，讓兩種操作按同一列鎖順序生效。
            var draft = await FindDraft(applicantId, id, ct);
            if (draft is null)
                return NotFound();
            if (CheckVersion(draft, request.Version) is { } conflict)
                return conflict;

            // 刪除主檔即可，由外鍵同步移除同張草稿的明細。
            db.Requisitions.Remove(draft);
            return Results.NoContent();
        }, ct);

    // 沿用停用、登出及改密碼會使用的帳號列鎖，在交易內重新取得目前資格。
    private async Task<IResult> RunAsBusiness(
        ClaimsPrincipal principal, Func<string, Task<IResult>> operation, CancellationToken ct, bool write = true)
    {
        // 即使 Cookie 曾通過中介層，仍須在取得同一帳號列鎖後重查登入。
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actor = await auth.CurrentLockedUser(principal, ct);
        if (actor is null)
            return AuthService.Unauthorized();

        // 任何啟用的業務帳號都能寫本人草稿；管理帳號及須改密碼的業務帳號不得操作。
        if (actor.AccountType != "business")
            return AuthService.Error(403, "forbidden", "此操作僅限業務帳號。");
        if (actor.MustChangePassword)
            return AuthService.Error(403, "forbidden", "請先變更臨時密碼。");

        // 失敗時不提交。成功寫入才更新這次登入的活動時間，兩者一起提交或回滾。
        var result = await operation(actor.Id);
        if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
        {
            if (write)
            {
                await db.SaveChangesAsync(ct);
                await auth.RecordSessionActivity(actor.Id, principal, ct);
            }
            await tx.CommitAsync(ct);
        }
        return result;
    }

    // 本人且仍屬從未送審狀態的查詢範圍，供清單、單筆與異動一致使用。
    private IQueryable<Requisition> OwnDrafts(string applicantId) =>
        db.Requisitions.Where(d => d.ApplicantId == applicantId && d.Status == Requisition.DraftStatus);

    // 將無效或不可存取的識別視為找不到，只有重開時讀取現有明細。
    private Task<Requisition?> FindDraft(string applicantId, string id, CancellationToken ct, bool includeLines = false)
    {
        // 無法解析的識別不查資料庫，與有效但不在存取範圍的識別共用回應。
        if (!Guid.TryParse(id, out var draftId))
            return Task.FromResult<Requisition?>(null);

        // 異動不需先讀舊明細，避免不必要的追蹤及逐列刪除。
        var query = OwnDrafts(applicantId);
        if (includeLines)
            query = query.Include(draft => draft.Lines);
        return query.SingleOrDefaultAsync(d => d.Id == draftId, ct);
    }

    // 使用資料庫的微秒精度，讓保存回應與重開讀到的 UTC 時間一致。
    private DateTimeOffset CurrentTimestamp()
    {
        var now = clock.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    // 只套用可編輯欄位及其衍生值，保存時一併發出新版本。
    private static void ApplyContent(Requisition draft, RequisitionDraftInput input, DateTimeOffset timestamp)
    {
        draft.Subject = input.Subject;
        draft.Purpose = input.Purpose;
        draft.Lines = input.Lines;
        draft.EstimatedAmount = input.EstimatedAmount;
        draft.Version = Guid.NewGuid().ToString("N");
        draft.UpdatedAt = timestamp;
    }

    // 明細順序由儲存位置決定，金額與完整性皆從已驗證的內容取得。
    private static DraftResponse Response(Requisition draft)
    {
        // 建立及查詢共用投影，缺少數值的明細小計保持 null。
        var lines = draft
            .Lines
            .OrderBy(line => line.Position)
            .Select(line => new DraftLineResponse(line.ItemName, line.Unit, line.Quantity, line.UnitPrice, line.EstimatedAmount))
            .ToArray();
        return new DraftResponse(draft.Id, draft.Number, draft.ApplicantId, draft.Status, draft.Subject, draft.Purpose,
            lines, draft.EstimatedAmount, RequisitionDraftInput.IsComplete(draft), draft.Version, draft.CreatedAt, draft.UpdatedAt);
    }

    // 版本缺漏是輸入錯誤，完整但過期的版本則回報需要重新載入。
    private static IResult? CheckVersion(Requisition draft, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return AuthService.Error(400, "invalid_input", "請提供草稿版本。");
        return version == draft.Version
            ? null
            : AuthService.Error(409, "stale_version", "草稿已變更，請重新載入。");
    }

    // 找不到及非本人草稿共用回應，避免洩露其他帳號的草稿是否存在。
    private static IResult NotFound() => AuthService.Error(404, "not_found", "找不到指定的草稿。");

    // 格式、精度或金額上限不符時沿用共用輸入錯誤，且保留原內容。
    private static IResult InvalidContent() =>
        AuthService.Error(400, "invalid_input", "草稿欄位格式、長度或金額不正確。");
}
