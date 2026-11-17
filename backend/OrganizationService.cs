using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Procurement;

// 管理組織設定，將資格、版本與資料異動集中在同一個 PostgreSQL 交易。
internal sealed class OrganizationService(AuthDb db, AuthService auth, IPasswordHasher<AppUser> hasher)
{
    private const int UserNameLimit = 64;
    private const string Manager = "Manager";
    private const string Finance = "Finance";
    private const string Applicant = "Applicant";

    // 讀取組織快照時也使用管理交易，部門和帳號才能一起反映最近一次設定。
    public Task<IResult> Read(ClaimsPrincipal principal, CancellationToken ct) => RunAsAdmin(principal, async _ =>
    {
        // 保留停用部門及原有識別，讓畫面可顯示歷史歸屬。
        var departments = await db
            .Departments
            .OrderBy(d => d.Name)
            .ThenBy(d => d.Id)
            .Select(d => new DepartmentResponse(d.Id, d.Name, d.Active, d.Version))
            .ToArrayAsync(ct);

        // 只投影業務帳號與管理所需欄位；角色從當下的 Identity 對應查詢。
        var managers = UsersWithRole(Manager);
        var finance = UsersWithRole(Finance);
        var accounts = await db
            .Users
            .Where(u => u.AccountType == "business")
            .OrderBy(u => u.UserName)
            .ThenBy(u => u.Id)
            .Select(u => new BusinessAccountResponse(
                u.Id, u.UserName ?? "", u.Active, u.ConcurrencyStamp ?? "", u.DepartmentId,
                managers.Contains(u.Id), finance.Contains(u.Id),
                u.ManagerReviewerId, u.FinanceReviewerId, u.MustChangePassword))
            .ToArrayAsync(ct);

        // 回傳同一次讀取的設定和版本，讓呼叫端保存後以資料庫值為準。
        return Results.Ok(new OrganizationResponse(departments, accounts));
    }, ct, recordActivity: false);

    // 新增部門由伺服器產生識別及版本，預設啟用供分配帳號。
    public Task<IResult> CreateDepartment(CreateDepartmentRequest request, ClaimsPrincipal principal, CancellationToken ct) =>
        RunAsAdmin(principal, _ =>
        {
            // 先檢查原始輸入，避免修剪名稱時靜默移除換行。
            if (AuthService.ContainsCrOrLf(request.Name))
                return Task.FromResult(Invalid("部門名稱不可包含換行。"));

            // 統一修剪名稱，避免僅有空白或資料庫不能儲存的字元。
            var name = request.Name?.Trim();
            if (!ValidText(name, Department.NameLimit))
                return Task.FromResult(Invalid("部門名稱須為 1–100 字元。"));

            // 新識別和啟用狀態由服務決定，請求只能設定名稱。
            db.Departments.Add(new Department { Name = name });
            return Task.FromResult(Results.NoContent());
        }, ct);

    // 修改部門時比較讀取版本，停用只修改狀態，不移除既有歸屬。
    public Task<IResult> UpdateDepartment(
        string id, UpdateDepartmentRequest request, ClaimsPrincipal principal, CancellationToken ct) =>
        RunAsAdmin(principal, async _ =>
        {
            // 和新增採同一單行規則，名稱首尾的換行也須拒絕。
            if (AuthService.ContainsCrOrLf(request.Name))
                return Invalid("部門名稱不可包含換行。");

            // 只接受可儲存的名稱、明確狀態及不透明版本。
            var name = request.Name?.Trim();
            if (!ValidText(name, Department.NameLimit)
                || request.Active is not { } active
                || string.IsNullOrEmpty(request.Version)
                || !ValidId(id))
                return Invalid("請提供部門名稱、啟用狀態及目前版本。");

            // 在組織交易鎖內比較版本，過期請求不覆蓋前次保存。
            var department = await db.Departments.SingleOrDefaultAsync(d => d.Id == id, ct);
            if (department is null)
                return NotFound();
            if (department.Version != request.Version)
                return Stale();

            // 更新可編輯欄位和版本，原始部門識別繼續供帳號參照。
            department.Name = name;
            department.Active = active;
            department.Version = Guid.NewGuid().ToString("N");
            return Results.NoContent();
        }, ct);

    // 一次停用一個或多個部門，保留既有帳號歸屬及送審快照。
    public Task<IResult> DeactivateDepartments(
        DeactivateOrganizationRequest? request, ClaimsPrincipal principal, CancellationToken ct) =>
        RunAsAdmin(principal, async _ =>
        {
            // 先驗證所有識別及版本欄位，再以同一查詢取得整批部門。
            if (!TryReadDeactivation(request, out var versions))
                return Invalid("請提供不重複的停用目標及各筆目前版本。");
            var ids = versions.Keys.ToArray();
            var departments = await db
                .Departments
                .Where(d => ids.Contains(d.Id))
                .ToArrayAsync(ct);

            // 組織鎖保護版本檢查；任一目標不存在或過期都不更動任何部門。
            if (departments.Length != ids.Length)
                return NotFound();
            if (departments.Any(d => d.Version != versions[d.Id]))
                return Stale();

            // 只更動仍啟用的資料列，維持原有名稱與穩定識別。
            foreach (var department in departments.Where(d => d.Active))
            {
                department.Active = false;
                department.Version = Guid.NewGuid().ToString("N");
            }
            return Results.NoContent();
        }, ct);

    // 建立業務帳號及初始角色，並要求使用者登入後更換臨時密碼。
    public Task<IResult> CreateAccount(
        CreateBusinessAccountRequest request, ClaimsPrincipal principal, CancellationToken ct) =>
        RunAsAdmin(principal, async _ =>
        {
            // 先拒絕單行欄位不能保留的換行，避免帳號修剪掩蓋無效輸入。
            if (AuthService.ContainsCrOrLf(request.UserName) || AuthService.ContainsCrOrLf(request.Password))
                return Invalid("帳號名稱與臨時密碼不可包含換行。");

            // 只修剪帳號名稱；密碼保留原字元，角色需由呼叫端明確選擇。
            var userName = request.UserName?.Trim();
            if (!ValidText(userName, UserNameLimit)
                || request.Password is not { } password
                || !AuthService.ValidPassword(password)
                || request.IsManager is not { } isManager
                || request.IsFinance is not { } isFinance)
                return Invalid("請提供 1–64 字元帳號、12–128 字元密碼及角色選項。");

            // 新分配的部門必須仍啟用，且帳號名稱比對沿用登入的不分大小寫規則。
            if (!await ActiveDepartment(request.DepartmentId, ct))
                return Invalid("請選擇啟用中的部門。");
            var normalizedName = userName.ToUpperInvariant();
            if (await db.Users.AnyAsync(u => u.NormalizedUserName == normalizedName, ct))
                return AuthService.Error(409, "duplicate_user_name", "此帳號名稱已被使用。");

            // 帳號類別、啟用及強制改密碼由服務設定；只保存 Identity 雜湊。
            var user = new AppUser
            {
                UserName = userName,
                NormalizedUserName = normalizedName,
                DepartmentId = request.DepartmentId,
                MustChangePassword = true,
                SecurityStamp = Guid.NewGuid().ToString(),
                LockoutEnabled = true
            };
            user.PasswordHash = hasher.HashPassword(user, password);
            db.Users.Add(user);

            // 新帳號一律具有申請人角色，主管及財務可兼任，和帳號一起保存。
            await SetRoles(user.Id, isManager, isFinance, newAccount: true, ct);
            return Results.NoContent();
        }, ct);

    // 保存業務帳號的設定，舊歸屬或審核人可原樣保留，變更時才檢查當下資格。
    public Task<IResult> UpdateAccount(
        string id, UpdateBusinessAccountRequest request, ClaimsPrincipal principal, CancellationToken ct) =>
        RunAsAdmin(principal, async _ =>
        {
            // 更新須包含版本與所有狀態選項，避免遺漏欄位即意外撤權。
            if (string.IsNullOrEmpty(request.Version)
                || request.Active is not { } active
                || request.IsManager is not { } isManager
                || request.IsFinance is not { } isFinance
                || !ValidId(id))
                return Invalid("請提供帳號狀態、角色及目前版本。");

            // 與登入和改密碼使用相同列鎖；管理帳號與不存在的目標均不可修改。
            var user = await auth.LockUser(id, ct);
            if (user is null || user.AccountType != "business")
                return NotFound();
            if (user.ConcurrencyStamp != request.Version)
                return Stale();

            // 歷史未分配或停用部門可保留，任何新的部門歸屬都須指向啟用中的部門。
            if (request.DepartmentId != user.DepartmentId && !await ActiveDepartment(request.DepartmentId, ct))
                return Invalid("請選擇啟用中的部門。");

            // 兩關需為不同人且不可選本人，變更的人選另須有目前的對應資格。
            var manager = request.ManagerReviewerId;
            var finance = request.FinanceReviewerId;
            if (manager == user.Id
                || finance == user.Id
                || (manager is not null
                    && manager == finance))
                return Invalid("兩關審核人必須不同，且不可選擇本人。");
            if (!await EligibleReviewer(manager, user.ManagerReviewerId, Manager, ct)
                || !await EligibleReviewer(finance, user.FinanceReviewerId, Finance, ct))
                return Invalid("請選擇啟用中且具有對應角色的業務帳號。");

            // 調整角色與設定時保留穩定識別；停用須撤銷所有登入，避免重新啟用後舊 Cookie 恢復。
            user.Active = active;
            user.DepartmentId = request.DepartmentId;
            user.ManagerReviewerId = manager;
            user.FinanceReviewerId = finance;
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
            await SetRoles(user.Id, isManager, isFinance, newAccount: false, ct);
            if (!active)
                await auth.RevokeSessions(user.Id, ct);
            return Results.NoContent();
        }, ct);

    // 批次停用只改業務帳號狀態，與登入及憑證異動共用同一組帳號列鎖。
    public Task<IResult> DeactivateAccounts(
        DeactivateOrganizationRequest? request, ClaimsPrincipal principal, CancellationToken ct) =>
        RunAsAdmin(principal, async _ =>
        {
            // 先檢查整批輸入，再依識別排序取鎖，避免由傳入順序決定持鎖次序。
            if (!TryReadDeactivation(request, out var versions))
                return Invalid("請提供不重複的停用目標及各筆目前版本。");
            var users = new List<AppUser>(versions.Count);
            foreach (var id in versions.Keys.Order(StringComparer.Ordinal))
            {
                // 與單筆編輯一致，找不到目標與管理帳號使用相同回應。
                var user = await auth.LockUser(id, ct);
                if (user is null || user.AccountType != "business")
                    return NotFound();
                users.Add(user);
            }

            // 在鎖內確認全部版本，包含原本已停用的目標。
            if (users.Any(u => u.ConcurrencyStamp != versions[u.Id]))
                return Stale();
            var activeUsers = users.Where(u => u.Active).ToArray();
            if (activeUsers.Length == 0)
                return Results.NoContent();

            // 逐筆取鎖會清除舊追蹤。先以讀取時的版本附加，再異動狀態及版本。
            db.Users.AttachRange(activeUsers);
            foreach (var user in activeUsers)
            {
                user.Active = false;
                user.ConcurrencyStamp = Guid.NewGuid().ToString();
            }

            // 同一交易撤銷所有剛停用帳號的登入，重新啟用後舊 Cookie 仍無法使用。
            var activeIds = activeUsers.Select(u => u.Id).ToArray();
            await auth.RevokeSessions(activeIds, ct);
            return Results.NoContent();
        }, ct);

    // 重設業務密碼須帶回版本，更新憑證與撤銷所有登入共用同一交易。
    public Task<IResult> ResetPassword(
        string id, ResetBusinessPasswordRequest request, ClaimsPrincipal principal, CancellationToken ct) =>
        RunAsAdmin(principal, async _ =>
        {
            // 重設不接受空版本或不符合共用長度規則的臨時密碼。
            if (!ValidId(id)
                || string.IsNullOrEmpty(request.Version)
                || request.NewPassword is not { } password
                || !AuthService.ValidPassword(password))
                return Invalid("請提供 12–128 字元臨時密碼及目前版本。");

            // 臨時密碼須能在單行登入欄位輸入，原字元不做轉換。
            if (AuthService.ContainsCrOrLf(password))
                return Invalid("臨時密碼不可包含換行。");

            // 與停用、登入及改密碼的列鎖順序一致，且只允許重設業務帳號。
            var user = await auth.LockUser(id, ct);
            if (user is null || user.AccountType != "business")
                return NotFound();
            if (user.ConcurrencyStamp != request.Version)
                return Stale();

            // 拒絕重用目前密碼，讓舊憑證在重設後無法建立任何登入。
            if (auth.PasswordMatches(user, password))
                return Invalid("臨時密碼必須與原密碼不同。");
            await auth.ReplaceCredentials(user, password, true, ct);
            return Results.NoContent();
        }, ct);

    // 在管理交易中重查操作者，成功寫入與該次登入的活動紀錄一起提交。
    internal async Task<IResult> RunAsAdmin(
        ClaimsPrincipal principal, Func<AppUser, Task<IResult>> operation, CancellationToken ct, bool recordActivity = true)
    {
        // 先取得組織交易鎖，再依序鎖定操作者及異動帳號，避免不同操作以反序持鎖。
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockOrganization(ct);
        var actor = await auth.CurrentLockedUser(principal, ct);
        if (actor is null)
            return AuthService.Unauthorized();

        // 管理類別與即時 Administrator 角色缺一不可，不沿用 middleware 的驗證結果。
        if (actor.AccountType != "admin"
            || actor.MustChangePassword
            || !await auth.RoleNames(actor.Id).ContainsAsync("Administrator", ct))
            return AuthService.Error(403, "forbidden", "此操作僅限管理帳號。");

        // 驗證拒絕或儲存例外會讓交易釋放時回滾，包含已發出的撤銷 SQL。
        var result = await operation(actor);
        if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
        {
            await db.SaveChangesAsync(ct);

            // GET 已由 Cookie 驗證記錄活動；管理寫入只在成功時延長操作者的閒置期限。
            if (recordActivity)
                await auth.RecordSessionActivity(actor.Id, principal, ct);
            await tx.CommitAsync(ct);
        }
        return result;
    }

    // 以原始識別對應版本，拒絕空清單、缺少欄位及同批重複目標。
    private static bool TryReadDeactivation(DeactivateOrganizationRequest? request, out Dictionary<string, string> versions)
    {
        // 使用與資料庫識別相同的精確比對，避免將不同的輸入目標視為同一筆。
        versions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request?.Items is not { Length: > 0 } items)
            return false;

        // 不修剪不透明值，並拒絕空白或含資料庫無法儲存的 NUL。
        foreach (var item in items)
        {
            if (item is not { Id: { } id, Version: { } version }
                || string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(version)
                || !ValidId(id)
                || !ValidId(version)
                || !versions.TryAdd(id, version))
                return false;
        }
        return true;
    }

    // 新歸屬只能分配到現有啟用部門；呼叫端持組織鎖，檢查後不會被同時停用。
    private async Task<bool> ActiveDepartment(string? id, CancellationToken ct) =>
        ValidId(id) && await db.Departments.AnyAsync(d => d.Id == id && d.Active, ct);

    // 從 Identity 關聯投影具指定角色的帳號，供設定回應及審核人資格查詢使用。
    private IQueryable<string> UsersWithRole(string roleName) =>
        from ur in db.UserRoles
        join role in db.Roles on ur.RoleId equals role.Id
        where role.Name == roleName
        select ur.UserId;

    // 保留未改的舊人選或允許清除；新指派須是啟用業務帳號且具有該關角色。
    private async Task<bool> EligibleReviewer(string? candidate, string? current, string role, CancellationToken ct)
    {
        // 原人選保留作為設定紀錄，清除也不需要再驗證舊資格。
        if (candidate is null || candidate == current)
            return true;
        if (!ValidId(candidate))
            return false;

        // 查詢目前資格；組織鎖避免同時撤權或停用讓新指派採用過期資料。
        var eligibleIds = UsersWithRole(role);
        return await db.Users.AnyAsync(u => u.Id == candidate
            && u.AccountType == "business"
            && u.Active
            && eligibleIds.Contains(u.Id), ct);
    }

    // 只異動管理範圍內的角色；初始角色缺少時才建立，既有其他權限保持原設定。
    private async Task SetRoles(string userId, bool isManager, bool isFinance, bool newAccount, CancellationToken ct)
    {
        // 批次載入角色與對應，避免每個角色逐次往返資料庫。
        var roles = await db
            .Roles
            .Where(r => r.Name == Manager
                || r.Name == Finance
                || r.Name == Applicant)
            .ToListAsync(ct);
        var memberships = newAccount
            ? []
            : await db
                .UserRoles
                .Where(ur => ur.UserId == userId)
                .ToListAsync(ct);

        // 原有帳號只更新主管與財務；新帳號額外建立申請人角色對應。
        var desired = new List<(string Name, bool Grant)> { (Manager, isManager), (Finance, isFinance) };
        if (newAccount)
            desired.Add((Applicant, true));
        foreach (var (name, grant) in desired)
        {
            // 只在授權時補齊缺少的角色，避免撤權新增空角色。
            var role = roles.SingleOrDefault(r => r.Name == name);
            if (role is null && grant)
            {
                role = new IdentityRole(name) { NormalizedName = name.ToUpperInvariant() };
                db.Roles.Add(role);
            }
            if (role is null)
                continue;

            // 只新增缺少的或移除取消的對應，其他角色對應保持原樣。
            var membership = memberships.SingleOrDefault(ur => ur.RoleId == role.Id);
            if (grant && membership is null)
                db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = role.Id });
            else if (!grant && membership is not null)
                db.UserRoles.Remove(membership);
        }
    }

    // 資料庫識別參數保留原字元，拒絕空值或資料庫不能儲存的 NUL。
    private static bool ValidId(string? value) => !string.IsNullOrEmpty(value) && !value.Contains('\0');

    // 以 .NET UTF-16 長度檢查名稱，並拒絕 PostgreSQL 文字欄位無法保存的 NUL。
    private static bool ValidText([NotNullWhen(true)] string? value, int limit) =>
        value is { Length: > 0 }
        && value.Length <= limit
        && !value.Contains('\0');

    // 所有設定輸入錯誤共用穩定代碼，保留對應欄位或規則的顯示訊息。
    private static IResult Invalid(string message) => AuthService.Error(400, "invalid_input", message);

    // 統一回報不可用目標，避免從業務端點辨識管理帳號是否存在。
    private static IResult NotFound() => AuthService.Error(404, "not_found", "找不到指定的設定資料。");

    // 版本過期時要求重新查詢，讓呼叫端不會默默覆寫已保存的變更。
    private static IResult Stale() => AuthService.Error(409, "stale_version", "資料已變更，請重新載入設定。");
}
