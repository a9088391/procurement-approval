using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Procurement;

// 請購查詢與簽核共用目前可見範圍；決定另受原指派、當前關卡及資格約束。
internal sealed class RequisitionApprovalService(AuthDb db, AuthService auth, TimeProvider clock)
{
    private const string ManagerRole = "Manager";
    private const string FinanceRole = "Finance";
    private static readonly string[] DecisionKinds = [Requisition.Approved, Requisition.Rejected];

    // 列出本人及目前業務角色可見的請購，清單不載入逐筆明細。
    internal Task<IResult> List(ClaimsPrincipal principal, CancellationToken ct) => RunAsBusiness(principal, async (actor, roles) =>
        Results.Ok(await Visible(actor, roles).ToArrayAsync(ct)), ct);

    // 明細與歷程一起回傳，兩者使用和清單相同的授權範圍。
    internal Task<IResult> Read(string id, ClaimsPrincipal principal, CancellationToken ct) =>
        RunAsBusiness(principal, async (actor, roles) =>
        {
            var summary = await FindVisible(actor, roles, id, ct);
            return summary is null ? NotFound() : Results.Ok(await Detail(summary, actor, ct));
        }, ct);

    // 以本人已保存的內容和目前組織設定建立新一輪，不沿用舊快照或人選。
    internal Task<IResult> Submit(string id, ApprovalRequest request, ClaimsPrincipal principal, CancellationToken ct) =>
        RunAsBusiness(principal, async (actor, roles) =>
        {
            // 先套用可見範圍，只有本人能以目前版本送審草稿或撤回單。
            var summary = await FindVisible(actor, roles, id, ct);
            if (summary is null)
                return NotFound();
            if (summary.Applicant.Id != actor.Id)
                return Forbidden();
            if (CheckVersion(summary, request.Version) is { } versionError)
                return versionError;
            if (summary.Status is not (Requisition.DraftStatus or Requisition.Withdrawn))
                return InvalidState();

            // 由帳號列鎖保護可編輯內容，內容合格才查詢送審部門與預設審核人。
            var draft = await LoadEditable(summary.Id, ct);
            if (!RequisitionDraftInput.CanSubmit(draft))
                return AuthService.Error(400, "invalid_input", "請填妥主旨、用途及 1–100 筆有效明細。");
            if (actor.DepartmentId is null)
                return AuthService.Error(409, "reviewers_unavailable", "請管理員設定申請人的部門後再送審。");
            if (await DefaultReviewers(actor, ct) is not { } reviewers)
                return ReviewersUnavailable();

            // 歷史部門讀取當下識別與名稱；已存在的停用部門關聯仍可保留。
            var department = await db.Departments.SingleAsync(d => d.Id == actor.DepartmentId, ct);
            var now = Requisition.CurrentTimestamp(clock);
            var round = new RequisitionRound
            {
                RequisitionId = draft.Id,
                Number = (summary.Round?.Number ?? 0) + 1,
                DepartmentId = department.Id,
                DepartmentName = department.Name,
                ManagerId = reviewers.Manager,
                FinanceId = reviewers.Finance,
                SubmittedAt = now,
                Content = JsonSerializer.Serialize(Snapshot(draft), JsonSerializerOptions.Web)
            };
            round.History.Add(new RequisitionApprovalEvent
            {
                Kind = RequisitionApprovalEvent.Submitted,
                ActorId = actor.Id,
                OccurredAt = now
            });
            db.RequisitionRounds.Add(round);
            Advance(draft, Requisition.ManagerPending, now);
            return await SaveAndRead(actor, roles, draft.Id, ct);
        }, ct, write: true);

    // 撤回與首關決定共用交易鎖，暫停不影響尚未決定時的撤回資格。
    internal Task<IResult> Withdraw(string id, ApprovalRequest request, ClaimsPrincipal principal, CancellationToken ct) =>
        RunAsBusiness(principal, async (actor, roles) =>
        {
            // 與送審相同，先確認可見及所有權，再辨識過期版本與非法狀態。
            var summary = await FindVisible(actor, roles, id, ct);
            if (summary is null)
                return NotFound();
            if (summary.Applicant.Id != actor.Id)
                return Forbidden();
            if (CheckVersion(summary, request.Version) is { } versionError)
                return versionError;
            if (WithdrawalCandidate(summary) is not { } round)
                return InvalidState();

            // 持鎖查當輪是否已有決定，撤回資格以實際歷程為準。
            var decided = await db
                .RequisitionApprovalEvents
                .AnyAsync(e => e.RequisitionId == summary.Id
                    && e.RoundNumber == round.Number
                    && DecisionKinds.Contains(e.Kind), ct);
            if (decided)
                return InvalidState();

            // 不改寫送審快照；狀態與撤回事件一起提交，同時對其他帳號隱藏。
            var requisition = await db.Requisitions.SingleAsync(d => d.Id == summary.Id, ct);
            var now = Requisition.CurrentTimestamp(clock);
            db.RequisitionApprovalEvents.Add(new RequisitionApprovalEvent
            {
                RequisitionId = requisition.Id,
                RoundNumber = round.Number,
                Kind = RequisitionApprovalEvent.Withdrawn,
                ActorId = actor.Id,
                OccurredAt = now
            });
            Advance(requisition, Requisition.Withdrawn, now);
            return await SaveAndRead(actor, roles, requisition.Id, ct);
        }, ct, write: true);

    // 只有最新的主管待審輪次可以撤回；暫停時仍以有無審核決定判斷。
    private static RequisitionRoundSummary? WithdrawalCandidate(RequisitionSummary summary) =>
        summary.Status == Requisition.ManagerPending ? summary.Round : null;

    // 指定審核者只能決定輪到本人的關卡；任何尚未完成的指派失格即暫停。
    internal Task<IResult> Decide(
        string id, ApprovalRequest request, ClaimsPrincipal principal, bool approve, CancellationToken ct) =>
        RunAsBusiness(principal, async (actor, roles) =>
        {
            // 版本檢查早於狀態檢查，讓同版本的競爭請求能辨識已提交的變更。
            var summary = await FindVisible(actor, roles, id, ct);
            if (summary is null)
                return NotFound();
            if (CheckVersion(summary, request.Version) is { } versionError)
                return versionError;
            if (summary.Round is not { } round || summary.Status is not (Requisition.ManagerPending or Requisition.FinancePending))
                return InvalidState();

            // 先確認本人是當關仍具角色的指派者，只有這個操作者會收到暫停的補正提示。
            var managerStage = summary.Status == Requisition.ManagerPending;
            if (actor.Id != (managerStage ? round.Manager.Id : round.Finance.Id)
                || !roles.Contains(managerStage ? ManagerRole : FinanceRole))
                return Forbidden();
            if (summary.Paused)
                return ReviewersUnavailable();

            // 狀態與歷程同時寫入；主管核准才會留下財務可見的進關依據。
            var draft = await db.Requisitions.SingleAsync(d => d.Id == summary.Id, ct);
            var now = Requisition.CurrentTimestamp(clock);
            var kind = approve ? Requisition.Approved : Requisition.Rejected;
            db.RequisitionApprovalEvents.Add(new RequisitionApprovalEvent
            {
                RequisitionId = draft.Id,
                RoundNumber = round.Number,
                Kind = kind,
                Stage = managerStage ? RequisitionApprovalEvent.ManagerStage : RequisitionApprovalEvent.FinanceStage,
                ActorId = actor.Id,
                OccurredAt = now
            });
            Advance(draft, approve && managerStage ? Requisition.FinancePending : kind, now);
            return await SaveAndRead(actor, roles, draft.Id, ct);
        }, ct, write: true);

    // 與管理端採相同鎖序，讓資格變更、送審、撤回、決定及讀回反映同一筆已提交結果。
    private async Task<IResult> RunAsBusiness(
        ClaimsPrincipal principal, Func<AppUser, string[], Task<IResult>> operation, CancellationToken ct, bool write = false)
    {
        // 組織鎖須早於帳號列鎖；Cookie 中介層已釋放其列鎖，這裡重新查登入及角色。
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockOrganization(ct);
        var actor = await auth.CurrentLockedUser(principal, ct);
        if (actor is null)
            return AuthService.Unauthorized();
        if (actor.AccountType != "business" || actor.MustChangePassword)
            return AuthService.Error(403, "forbidden", "此操作僅限已完成密碼設定的業務帳號。");
        var roles = await auth.RoleNames(actor.Id).ToArrayAsync(ct);

        // 明細會另讀快照和歷程，保留鎖到查詢完成；只有成功寫入才提交本次活動。
        var result = await operation(actor, roles);
        if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
        {
            if (write)
                await auth.RecordSessionActivity(actor.Id, principal, ct);
            await tx.CommitAsync(ct);
        }
        return result;
    }

    // 集中定義可見範圍與待辦提示，讓單筆查詢和清單使用同一個 PostgreSQL 查詢。
    private IQueryable<RequisitionSummary> Visible(AppUser actor, string[] roles, Guid? id = null)
    {
        // 其他帳號僅能查已送審且未撤回的單；目前部門、指派及財務進關依最新一輪判定。
        var manager = roles.Contains(ManagerRole);
        var finance = roles.Contains(FinanceRole);
        var eligibleManagers = EligibleReviewerIds(ManagerRole);
        var eligibleFinance = EligibleReviewerIds(FinanceRole);
        return
            from draft in db.Requisitions
            join applicant in db.Users on draft.ApplicantId equals applicant.Id
            from round in draft.Rounds
                .OrderByDescending(r => r.Number)
                .Take(1)
                .DefaultIfEmpty()
            let waitingManager = draft.Status == Requisition.ManagerPending
            let waitingFinance = draft.Status == Requisition.FinancePending
            let submitted = waitingManager
                || waitingFinance
                || draft.Status == Requisition.Approved
                || draft.Status == Requisition.Rejected
            let managerVisible = manager
                && round != null
                && (round.DepartmentId == actor.DepartmentId
                    || round.ManagerId == actor.Id)
            let financeVisible = finance
                && round != null
                && round.History.Any(e =>
                    e.Stage == RequisitionApprovalEvent.ManagerStage
                    && e.Kind == Requisition.Approved)
            where (id == null
                    || draft.Id == id)
                && (draft.ApplicantId == actor.Id
                    || (submitted
                        && (managerVisible
                            || financeVisible)))
            // 當主管已完成，只重查財務；主管待審時兩個未完成關卡都必須合格。
            let paused = round != null
                && ((waitingManager
                        && !eligibleManagers.Contains(round.ManagerId))
                    || ((waitingManager
                            || waitingFinance)
                        && !eligibleFinance.Contains(round.FinanceId)))
            let assigned = round != null
                && ((waitingManager
                        && manager
                        && round.ManagerId == actor.Id)
                    || (waitingFinance
                        && finance
                        && round.FinanceId == actor.Id))
            let managerUser = round == null ? null : round.Manager
            let financeUser = round == null ? null : round.Finance
            orderby draft.UpdatedAt descending, draft.Id
            select new RequisitionSummary(draft.Id, draft.Number, draft.Subject,
                new RequisitionPerson(applicant.Id, applicant.UserName ?? ""), draft.Status,
                draft.EstimatedAmount, draft.Version, draft.CreatedAt, draft.UpdatedAt, paused, assigned && !paused,
                round == null ? null : new RequisitionRoundSummary(round.Number,
                    new RequisitionDepartment(round.DepartmentId, round.DepartmentName), round.SubmittedAt,
                    new RequisitionPerson(round.ManagerId, managerUser != null ? managerUser.UserName ?? "" : ""),
                    new RequisitionPerson(round.FinanceId, financeUser != null ? financeUser.UserName ?? "" : "")));
    }

    // 單號以外的公開識別須能解析，缺少或無權讀取時共用找不到的結果。
    private Task<RequisitionSummary?> FindVisible(AppUser actor, string[] roles, string id, CancellationToken ct) =>
        Guid.TryParse(id, out var key) ? Visible(actor, roles, key).SingleOrDefaultAsync(ct) : Task.FromResult<RequisitionSummary?>(null);

    // 以即時 Identity 角色和啟用狀態認定人選；強制改密碼限制的是該人登入後的操作。
    private IQueryable<string> EligibleReviewerIds(string roleName) =>
        from user in db.Users
        join assignment in db.UserRoles on user.Id equals assignment.UserId
        join role in db.Roles on assignment.RoleId equals role.Id
        where user.Active
            && user.AccountType == "business"
            && role.Name == roleName
        select user.Id;

    // 驗證預設人選確為三人分工，再批次確認兩個人選目前都具資格。
    private async Task<(string Manager, string Finance)?> DefaultReviewers(AppUser actor, CancellationToken ct)
    {
        // 設定已保留的人選可能之後失格；不可只驗證設定是否非空。
        var manager = actor.ManagerReviewerId;
        var finance = actor.FinanceReviewerId;
        if (manager is null
            || finance is null
            || manager == finance
            || actor.Id == manager
            || actor.Id == finance)
            return null;

        // 兩個人選在同一查詢檢查，並與暫停判斷共用資格條件。
        var eligible = await EligibleReviewerIds(ManagerRole)
            .Where(id => id == manager)
            .Concat(EligibleReviewerIds(FinanceRole).Where(id => id == finance))
            .CountAsync(ct);
        return eligible == 2 ? (manager, finance) : null;
    }

    // 讀取已授權本人的可編輯內容；同一帳號列鎖避免讀到一半被保存取代。
    private Task<Requisition> LoadEditable(Guid id, CancellationToken ct) => db
        .Requisitions
        .Include(d => d.Lines)
        .SingleAsync(d => d.Id == id, ct);

    // 快照只複製當次內容，保留原始明細位置且不追蹤可編輯的實體。
    private static RequisitionSnapshot Snapshot(Requisition requisition) => new(
        requisition.Subject, requisition.Purpose, requisition.EstimatedAmount, requisition
            .Lines
            .OrderBy(l => l.Position)
            .Select(l => new RequisitionSnapshotLine(l.ItemName, l.Unit, l.Quantity, l.UnitPrice))
            .ToArray());

    // 已授權的單依狀態讀目前內容；只有本人取得舊輪快照及跨輪歷程。
    private async Task<RequisitionDetail> Detail(RequisitionSummary summary, AppUser actor, CancellationToken ct)
    {
        // 本人可讀所有事件；其他可見者只能讀最新一輪，查詢及操作回應共用此限制。
        var owner = summary.Applicant.Id == actor.Id;
        var round = summary.Round;
        var history = round is null ? [] : await db
            .RequisitionApprovalEvents
            .Where(e => e.RequisitionId == summary.Id && (owner || e.RoundNumber == round.Number))
            .OrderBy(e => e.RoundNumber)
            .ThenBy(e => e.Id)
            .Select(e => new RequisitionHistoryEntry(e.RoundNumber, e.Kind, e.Stage,
                new RequisitionPerson(e.ActorId, e.Actor != null ? e.Actor.UserName ?? "" : ""), e.OccurredAt))
            .ToArrayAsync(ct);

        // 撤回後最後一次送審內容也是歷史；其餘已送審狀態以最新輪作為目前內容。
        var editable = summary.Status is Requisition.DraftStatus or Requisition.Withdrawn;
        var previous = owner
            && round is not null
            && (editable || round.Number > 1)
                ? await PreviousRounds(summary.Id, editable ? round.Number : round.Number - 1, ct)
                : [];
        if (editable)
        {
            // 只有本人可見可編輯單；重送和首送都以目前內容、部門關聯及預設人選判斷。
            var draft = await LoadEditable(summary.Id, ct);
            var canSubmit = RequisitionDraftInput.CanSubmit(draft)
                && actor.DepartmentId is not null
                && await DefaultReviewers(actor, ct) is not null;
            return new RequisitionDetail(summary, draft.Purpose, DetailLines(Snapshot(draft).Lines),
                RequisitionDraftInput.IsComplete(draft), canSubmit, canWithdraw: false, history, previous);
        }

        // 送審後的內容從獨立快照讀取，部門、人選與決定不由目前的申請人設定推算。
        if (round is null)
            throw new InvalidOperationException("已送審的請購必須有送審輪次。");
        var content = await db
            .RequisitionRounds
            .Where(r => r.RequisitionId == summary.Id && r.Number == round.Number)
            .Select(r => r.Content)
            .SingleAsync(ct);
        var snapshot = ReadSnapshot(content);

        // 與撤回操作共用輪次和決定種類，利用已讀取的本人歷程判斷按鈕資格。
        var canWithdraw = owner
            && WithdrawalCandidate(summary) is { } candidate
            && !history.Any(e => e.RoundNumber == candidate.Number && DecisionKinds.Contains(e.Kind));
        return new RequisitionDetail(summary with { Subject = snapshot.Subject, EstimatedAmount = snapshot.EstimatedAmount },
            snapshot.Purpose, DetailLines(snapshot.Lines), isComplete: true, canSubmit: false, canWithdraw, history, previous);
    }

    // 僅供本人明細一次取回所有符合範圍的舊輪，依原輪次排列，不逐輪查詢。
    private async Task<RequisitionPreviousRound[]> PreviousRounds(Guid id, int throughRound, CancellationToken ct)
    {
        // SQL 只讀到需要的輪次，並合併當時的部門及指派關聯。
        var previous = await db
            .RequisitionRounds
            .Where(r => r.RequisitionId == id && r.Number <= throughRound)
            .OrderBy(r => r.Number)
            .Select(r => new
            {
                r.Content,
                Round = new RequisitionRoundSummary(r.Number, new RequisitionDepartment(r.DepartmentId, r.DepartmentName),
                    r.SubmittedAt, new RequisitionPerson(r.ManagerId, r.Manager != null ? r.Manager.UserName ?? "" : ""),
                    new RequisitionPerson(r.FinanceId, r.Finance != null ? r.Finance.UserName ?? "" : ""))
            })
            .ToArrayAsync(ct);

        // 目前及歷史輪次以相同 DTO 表示快照內容，不直接回傳持久化 JSON。
        return previous.Select(p =>
        {
            var snapshot = ReadSnapshot(p.Content);
            return new RequisitionPreviousRound(p.Round, snapshot.Subject, snapshot.Purpose,
                snapshot.EstimatedAmount, DetailLines(snapshot.Lines));
        }).ToArray();
    }

    // 已送審輪次必有快照，讀取時保持固定格式並將損毀視為錯誤。
    private static RequisitionSnapshot ReadSnapshot(string content) =>
        JsonSerializer.Deserialize<RequisitionSnapshot>(content, JsonSerializerOptions.Web)
        ?? throw new InvalidOperationException("請購送審快照不得為空。");

    // 共用草稿明細的公開欄位與精確小計，避免將持久化快照直接傳給客戶端。
    private static DraftLineResponse[] DetailLines(RequisitionSnapshotLine[] lines) => lines
        .Select(l => new DraftLineResponse(l.ItemName, l.Unit, l.Quantity, l.UnitPrice, l.Quantity * l.UnitPrice))
        .ToArray();

    // 狀態、修改時間與新版本同步，供下一次操作辨識過期資料。
    private static void Advance(Requisition requisition, string status, DateTimeOffset now)
    {
        requisition.Status = status;
        requisition.UpdatedAt = now;
        requisition.Version = Guid.NewGuid().ToString("N");
    }

    // 在原交易寫入後重查結果；若任一步失敗，尚未提交的快照、狀態與歷程一起回滾。
    private async Task<IResult> SaveAndRead(AppUser actor, string[] roles, Guid id, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        var summary = await Visible(actor, roles, id).SingleAsync(ct);
        return Results.Ok(await Detail(summary, actor, ct));
    }

    // 只有可見的單才辨識版本，空值與過期值分開回報。
    private static IResult? CheckVersion(RequisitionSummary summary, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return AuthService.Error(400, "invalid_input", "請提供請購版本。");
        return version == summary.Version
            ? null
            : AuthService.Error(409, "stale_version", "請購單已變更，請重新載入。");
    }

    // 缺少與不可見共用回應，避免依照識別或版本枚舉他人的請購。
    private static IResult NotFound() => AuthService.Error(404, "not_found", "找不到指定的請購單。");

    // 可查看內容不代表是申請人或當關的合法指派者。
    private static IResult Forbidden() => AuthService.Error(403, "forbidden", "此身分不能執行這項操作。");

    // 操作已不適用於目前狀態，保留既有流程及決定。
    private static IResult InvalidState() => AuthService.Error(409, "invalid_state", "目前狀態不允許此操作。");

    // 人選失格時由管理員補正，保留草稿或原關卡。
    private static IResult ReviewersUnavailable() =>
        AuthService.Error(409, "reviewers_unavailable", "目前的審核人設定或資格不完整，請聯絡管理員。");
}
