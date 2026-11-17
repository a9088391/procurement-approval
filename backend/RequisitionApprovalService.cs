using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Procurement;

// 請購查詢與簽核共用目前可見範圍；決定另受目前有效指派、當前關卡及資格約束。
internal sealed class RequisitionApprovalService(AuthDb db, AuthService auth, TimeProvider clock, OrganizationService organization)
{
    private const string ManagerRole = "Manager";
    private const string FinanceRole = "Finance";
    private static readonly string[] DecisionKinds = [Requisition.Approved, Requisition.Rejected];

    // 一般清單保留本人草稿及撤回單；送審清單、統計及匯出共用同一份可組合的送審查詢。
    internal Task<IResult> ReadCollection(IQueryCollection query, ClaimsPrincipal principal, RequisitionReadKind kind, CancellationToken ct) =>
        RunAsBusiness(principal, async (actor, roles) =>
        {
            // 使用鎖內取得的業務身分，無權查詢者不會取得參數錯誤或探知組織資料。
            if (RequisitionReportFilter.Read(query) is not { } filter)
                return AuthService.Error(400, "invalid_input", "請確認送審範圍、日期、部門及狀態篩選條件。");

            // 所有可見條件及報表篩選都在 PostgreSQL 投影及載入欄位之前執行。
            var visible = VisibleWorkflows(actor, roles);
            if (kind != RequisitionReadKind.List || filter.SubmittedOnly)
                visible = SubmittedWorkflows(visible, filter);

            // 依用途投影所需欄位，不在統計或匯出中載入內容快照及完整歷程。
            return kind switch
            {
                RequisitionReadKind.Statistics => await Statistics(visible, ct),
                RequisitionReadKind.Csv => await Download(visible, ct),
                _ => Results.Ok(await Summaries(visible, actor, roles).ToArrayAsync(ct))
            };
        }, ct);

    // 只在目前已送審狀態使用最新輪次條件；不以已送審過或曾經到過的狀態納入舊輪。
    private static IQueryable<CurrentWorkflow> SubmittedWorkflows(IQueryable<CurrentWorkflow> visible, RequisitionReportFilter filter)
    {
        // 確認最新輪存在，再依篩選條件限制同一輪的部門、狀態及送審時間。
        visible = visible.Where(w => w.Submitted && w.Round != null);
        if (filter.StartUtc is { } start)
            visible = visible.Where(w => w.Round!.SubmittedAt >= start);
        if (filter.EndUtcExclusive is { } end)
            visible = visible.Where(w => w.Round!.SubmittedAt < end);
        if (filter.DepartmentId is { } department)
            visible = visible.Where(w => w.Round!.DepartmentId == department);
        if (filter.Status is { } status)
            visible = visible.Where(w => w.Draft.Status == status);
        return visible;
    }

    // 統計先在資料庫計算各送審部門名稱的明細小計，再依穩定識別合併更名前後資料。
    private static async Task<IResult> Statistics(IQueryable<CurrentWorkflow> submitted, CancellationToken ct)
    {
        // 只接受已確認具有最新輪的查詢；主檔金額在已送審狀態不可修改，與當輪快照一致。
        var subtotals = await submitted
            .GroupBy(w => new { w.Round!.DepartmentId, w.Round.DepartmentName, w.Draft.Status })
            .Select(group => new
            {
                group.Key,
                Count = group.Count(),
                EstimatedAmount = group.Sum(w => w.Draft.EstimatedAmount),
                Latest = group.Max(w => w.Round!.SubmittedAt)
            })
            .ToArrayAsync(ct);

        // 名稱取自篩選後本來就可見的最新快照；同時送審時按名稱決定順序，避免查到未授權的快照。
        var groups = subtotals
            .GroupBy(row => row.Key.DepartmentId)
            .OrderBy(department => department.Key, StringComparer.Ordinal)
            .SelectMany(department =>
            {
                // 同部門各狀態都使用同一個顯示名稱，件數及金額不因部門更名而分開。
                var name = department
                    .OrderByDescending(row => row.Latest)
                    .ThenBy(row => row.Key.DepartmentName, StringComparer.Ordinal)
                    .First()
                    .Key.DepartmentName;
                var label = new RequisitionDepartment(department.Key, name);
                return department
                    .GroupBy(row => row.Key.Status)
                    .OrderBy(status => status.Key, StringComparer.Ordinal)
                    .Select(status => new RequisitionStatisticsGroup(label, status.Key,
                        status.Sum(row => row.Count), status.Sum(row => row.EstimatedAmount ?? 0m)));
            })
            .ToArray();
        return Results.Ok(new RequisitionStatistics(groups));
    }

    // 匯出只取得七個欄位，依相同清單排序，並在交易釋鎖後才格式化及寫入回應。
    private async Task<IResult> Download(IQueryable<CurrentWorkflow> submitted, CancellationToken ct)
    {
        // 申請人顯示名稱沿用清單的帳號欄位；不把額外個資、操作資格及歷程載入記憶體。
        var rows = await (
            from workflow in submitted
            let draft = workflow.Draft
            let round = workflow.Round!
            join applicant in db.Users on draft.ApplicantId equals applicant.Id
            orderby draft.UpdatedAt descending, draft.Id
            select new RequisitionCsvRow(draft.Number, draft.Subject, applicant.UserName ?? "", round.DepartmentName,
                draft.Status, draft.EstimatedAmount, round.SubmittedAt))
            .ToArrayAsync(ct);
        return new RequisitionCsvDownload(rows);
    }

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
            if (CheckVersion(summary.Version, request.Version) is { } versionError)
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
            // 新輪次、送審事件與待審狀態／版本須在同一交易提交，避免留下不完整送審歷程。
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
            if (CheckVersion(summary.Version, request.Version) is { } versionError)
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
            if (CheckVersion(summary.Version, request.Version) is { } versionError)
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

    // 管理查詢只取當輪待辦及合格人選，鎖住組織異動直到有限資料讀取完成。
    internal Task<IResult> ListReassignments(ClaimsPrincipal principal, CancellationToken ct) =>
        organization.RunAsAdmin(principal, async _ =>
        {
            // 查詢與業務待辦共用有效指派及暫停判斷；只投影改派所需欄位。
            var items = await PendingWorkflows()
                .OrderBy(w => w.Draft.Number)
                .Select(w => new ReassignmentItem(
                    w.Draft.Id, w.Draft.Number, new RequisitionDepartment(w.Round!.DepartmentId, w.Round.DepartmentName),
                    w.WaitingManager ? RequisitionApprovalEvent.ManagerStage : RequisitionApprovalEvent.FinanceStage,
                    w.Paused, w.Draft.Version, new RequisitionPerson(w.ManagerId, w.ManagerName),
                    new RequisitionPerson(w.FinanceId, w.FinanceName)))
                .ToArrayAsync(ct);

            // 候選人只傳穩定識別與名稱，三人分工仍以提交改派時的該單人選檢查。
            return Results.Ok(new ReassignmentList(items, await ReviewerPeople(ManagerRole, ct),
                await ReviewerPeople(FinanceRole, ct)));
        }, ct, recordActivity: false);

    // 在管理交易中以最新版本更換一個未完成關卡，並原子保存改派歷程。
    internal Task<IResult> Reassign(string id, ReassignmentRequest request, ClaimsPrincipal principal, CancellationToken ct) =>
        organization.RunAsAdmin(principal, async actor =>
        {
            // 管理端僅可操作待辦清單中的單，其他識別與狀態共用找不到的結果。
            if (!Guid.TryParse(id, out var key))
                return NotFound();
            var current = await PendingWorkflows()
                .Where(w => w.Draft.Id == key)
                .Select(w => new { w.Draft, w.Round, w.WaitingManager, w.ManagerId, w.FinanceId })
                .SingleOrDefaultAsync(ct);
            if (current?.Round is not { } round)
                return NotFound();

            // 拒絕無效欄位及超過 UTF-16 長度的原因；NUL 無法保存到 PostgreSQL 文字欄位。
            var reason = request.Reason?.Trim();
            if (request.Stage is not (RequisitionApprovalEvent.ManagerStage or RequisitionApprovalEvent.FinanceStage)
                || string.IsNullOrWhiteSpace(request.ReviewerId)
                || request.ReviewerId.Contains('\0')
                || reason is null or { Length: 0 or > RequisitionApprovalEvent.ReasonLimit }
                || reason.Contains('\0'))
                return AuthService.Error(400, "invalid_input", "請提供有效的改派關卡、接手人及 1–500 字的原因。");
            if (CheckVersion(current.Draft.Version, request.Version) is { } versionError)
                return versionError;

            // 主管待審可改派兩個未完成關卡；已進財務時不得改寫主管決定或指派。
            var managerStage = request.Stage == RequisitionApprovalEvent.ManagerStage;
            if (managerStage && !current.WaitingManager)
                return InvalidState();

            // 資格必須對應目標關卡，與另一關的目前有效人選及申請人維持三人分工。
            var previous = managerStage ? current.ManagerId : current.FinanceId;
            var other = managerStage ? current.FinanceId : current.ManagerId;
            if (request.ReviewerId == previous
                || request.ReviewerId == other
                || request.ReviewerId == current.Draft.ApplicantId
                || !await EligibleReviewerIds(managerStage ? ManagerRole : FinanceRole).ContainsAsync(request.ReviewerId, ct))
                return ReviewersUnavailable();

            // 不更動原送審快照及待審關卡；交易同時提交有效指派、版本、歷程與登入活動。
            if (managerStage)
                round.ManagerReplacementId = request.ReviewerId;
            else
                round.FinanceReplacementId = request.ReviewerId;
            var now = Requisition.CurrentTimestamp(clock);
            Advance(current.Draft, current.Draft.Status, now);
            db.RequisitionApprovalEvents.Add(new RequisitionApprovalEvent
            {
                RequisitionId = current.Draft.Id,
                RoundNumber = round.Number,
                Kind = RequisitionApprovalEvent.Reassigned,
                Stage = request.Stage,
                ActorId = actor.Id,
                OccurredAt = now,
                PreviousReviewerId = previous,
                NewReviewerId = request.ReviewerId,
                Reason = reason
            });
            return Results.NoContent();
        }, ct);

    // 待辦清單和寫入採相同範圍，包含待審中的暫停案件。
    private IQueryable<CurrentWorkflow> PendingWorkflows() =>
        CurrentWorkflows()
            .Where(w => w.Round != null
                && (w.WaitingManager
                    || w.WaitingFinance));

    // 只投影具該關角色的啟用業務帳號，避免在管理改派讀取不相干的帳號資料。
    private Task<RequisitionPerson[]> ReviewerPeople(string role, CancellationToken ct)
    {
        var eligible = EligibleReviewerIds(role);
        return db
            .Users
            .Where(u => eligible.Contains(u.Id))
            .OrderBy(u => u.UserName)
            .ThenBy(u => u.Id)
            .Select(u => new RequisitionPerson(u.Id, u.UserName ?? ""))
            .ToArrayAsync(ct);
    }

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

    // 依最新輪次推導目前有效指派；原始指派留給送審歷史使用。
    private IQueryable<CurrentWorkflow> CurrentWorkflows()
    {
        // 主管待審時兩個未完成關卡皆需合格；主管完成後，只重查仍待審的財務關。
        var eligibleManagers = EligibleReviewerIds(ManagerRole);
        var eligibleFinance = EligibleReviewerIds(FinanceRole);
        return
            from draft in db.Requisitions
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
            let managerId = round.ManagerReplacementId ?? round.ManagerId
            let financeId = round.FinanceReplacementId ?? round.FinanceId
            join manager in db.Users on managerId equals manager.Id into managers
            from manager in managers.DefaultIfEmpty()
            join finance in db.Users on financeId equals finance.Id into finances
            from finance in finances.DefaultIfEmpty()
            let paused = round != null
                && ((waitingManager && !eligibleManagers.Contains(managerId))
                    || ((waitingManager || waitingFinance) && !eligibleFinance.Contains(financeId)))
            select new CurrentWorkflow
            {
                Draft = draft,
                Round = round,
                WaitingManager = waitingManager,
                WaitingFinance = waitingFinance,
                Submitted = submitted,
                Paused = paused,
                ManagerId = managerId,
                FinanceId = financeId,
                ManagerName = manager == null ? "" : manager.UserName ?? "",
                FinanceName = finance == null ? "" : finance.UserName ?? ""
            };
    }

    // 查詢內部使用同一組有效指派與待審條件，依各端點需求投影公開欄位。
    private sealed class CurrentWorkflow
    {
        public required Requisition Draft { get; init; }
        public RequisitionRound? Round { get; init; }
        public bool WaitingManager { get; init; }
        public bool WaitingFinance { get; init; }
        public bool Submitted { get; init; }
        public bool Paused { get; init; }
        public required string ManagerId { get; init; }
        public required string FinanceId { get; init; }
        public required string ManagerName { get; init; }
        public required string FinanceName { get; init; }
    }

    // 所有業務查詢使用同一個 PostgreSQL 可見條件；篩選及欄位投影不參與授權。
    private IQueryable<CurrentWorkflow> VisibleWorkflows(AppUser actor, string[] roles, Guid? id = null)
    {
        // 他人草稿與撤回單不可見；主管部門、最新有效指派及財務進關各自保留授權來源。
        var manager = roles.Contains(ManagerRole);
        var finance = roles.Contains(FinanceRole);
        return
            from workflow in CurrentWorkflows()
            let draft = workflow.Draft
            let round = workflow.Round
            let managerVisible = manager
                && round != null
                && (round.DepartmentId == actor.DepartmentId
                    || workflow.ManagerId == actor.Id)
            let financeVisible = finance
                && round != null
                && round.History.Any(e =>
                    e.Stage == RequisitionApprovalEvent.ManagerStage
                    && e.Kind == Requisition.Approved)
            where (id == null
                    || draft.Id == id)
                && (draft.ApplicantId == actor.Id
                    || (workflow.Submitted
                        && (managerVisible
                            || financeVisible)))
            select workflow;
    }

    // 既有清單與明細仍使用相同摘要；只有目前被指派到當關且具角色的人可決定。
    private IQueryable<RequisitionSummary> Summaries(IQueryable<CurrentWorkflow> visible, AppUser actor, string[] roles)
    {
        // 保留原有輸出、排序與操作資格，避免報表模式改變一般請購流程。
        var manager = roles.Contains(ManagerRole);
        var finance = roles.Contains(FinanceRole);
        return
            from workflow in visible
            let draft = workflow.Draft
            let round = workflow.Round
            join applicant in db.Users on draft.ApplicantId equals applicant.Id
            let assigned = round != null
                && ((workflow.WaitingManager
                        && manager
                        && workflow.ManagerId == actor.Id)
                    || (workflow.WaitingFinance
                        && finance
                        && workflow.FinanceId == actor.Id))
            orderby draft.UpdatedAt descending, draft.Id
            select new RequisitionSummary(draft.Id, draft.Number, draft.Subject,
                new RequisitionPerson(applicant.Id, applicant.UserName ?? ""), draft.Status,
                draft.EstimatedAmount, draft.Version, draft.CreatedAt, draft.UpdatedAt, workflow.Paused,
                assigned && !workflow.Paused, round == null ? null : new RequisitionRoundSummary(round.Number,
                    new RequisitionDepartment(round.DepartmentId, round.DepartmentName), round.SubmittedAt,
                    new RequisitionPerson(workflow.ManagerId, workflow.ManagerName),
                    new RequisitionPerson(workflow.FinanceId, workflow.FinanceName)));
    }

    // 單筆讀取及寫入後讀回共用摘要投影，維持原有的資料與操作提示。
    private IQueryable<RequisitionSummary> Visible(AppUser actor, string[] roles, Guid id) =>
        Summaries(VisibleWorkflows(actor, roles, id), actor, roles);

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
                new RequisitionPerson(e.ActorId, e.Actor != null ? e.Actor.UserName ?? "" : ""), e.OccurredAt,
                e.PreviousReviewerId != null
                    && e.NewReviewerId != null
                    && e.Reason != null
                    ? new RequisitionHistoryReassignment(
                        new RequisitionPerson(e.PreviousReviewerId, e.PreviousReviewer != null ? e.PreviousReviewer.UserName ?? "" : ""),
                        new RequisitionPerson(e.NewReviewerId, e.NewReviewer != null ? e.NewReviewer.UserName ?? "" : ""), e.Reason)
                    : null))
            .ToArrayAsync(ct);

        // 撤回後最後一次送審內容也是歷史；其餘已送審狀態以最新輪作為目前內容。
        var editable = summary.Status is Requisition.DraftStatus or Requisition.Withdrawn;
        var previous = owner
            && round is not null
            && (editable
                || round.Number > 1)
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

        // 送審後的內容從獨立快照讀取，部門與內容不因改派或申請人的組織設定改變。
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
        // SQL 只讀到需要的輪次；合併當時的部門及原始指派，改派另由歷程保留。
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
    private static IResult? CheckVersion(string currentVersion, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return AuthService.Error(400, "invalid_input", "請提供請購版本。");
        return version == currentVersion
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
