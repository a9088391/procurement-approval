using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Procurement;

// 帳號沿用 Identity；啟用、部門、預設審核人與強制改密碼狀態均由資料庫保存。
public sealed class AppUser : IdentityUser
{
    public string AccountType { get; set; } = "business";
    public bool Active { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public string? DepartmentId { get; set; }
    public string? ManagerReviewerId { get; set; }
    public string? FinanceReviewerId { get; set; }
}

// 每次登入獨立保存，供單次撤銷、全部撤銷及閒置／絕對期限判斷。
public sealed class LoginSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

// 組織、Identity 與登入紀錄共用 DbContext，讓設定更新和必要撤銷能在同一交易提交。
public sealed class AuthDb(DbContextOptions<AuthDb> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<LoginSession> Sessions => Set<LoginSession>();
    public DbSet<Department> Departments => Set<Department>();

    // 在呼叫端交易內串行組織設定與本機初始化，避免跨帳號或部門的資格檢查互相穿插。
    internal Task LockOrganization(CancellationToken ct) =>
        Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(80101)", ct);

    // 保留 Identity 原有對應，並補上組織和登入的資料庫約束，讓資料庫也能維護一致性。
    protected override void OnModelCreating(ModelBuilder builder)
    {
        // 先建立 Identity 的標準模型，再限制應用程式可保存的帳號類別。
        base.OnModelCreating(builder);
        var users = builder.Entity<AppUser>();
        users
            .Property(u => u.AccountType)
            .HasMaxLength(20);
        users.ToTable(t => t.HasCheckConstraint("CK_AccountType", "\"AccountType\" IN ('admin', 'business')"));

        // 既有帳號可保留未分部門與未設定審核人的狀態，升級時不要求改密碼。
        users
            .Property(u => u.MustChangePassword)
            .HasDefaultValue(false);
        builder
            .Entity<Department>()
            .Property(d => d.Name)
            .HasMaxLength(Department.NameLimit);

        // 停用保留原識別與關聯；資料庫拒絕刪除仍被帳號引用的部門或審核人。
        users
            .HasOne<Department>()
            .WithMany()
            .HasForeignKey(u => u.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);
        users
            .HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(u => u.ManagerReviewerId)
            .OnDelete(DeleteBehavior.Restrict);
        users
            .HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(u => u.FinanceReviewerId)
            .OnDelete(DeleteBehavior.Restrict);

        // 每次登入有自己的主鍵。帳號刪除時一併刪除登入，並以帳號索引支援整批撤銷。
        builder.Entity<LoginSession>().HasKey(s => s.Id);
        builder
            .Entity<LoginSession>()
            .HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<LoginSession>().HasIndex(s => s.UserId);
    }
}

// 接收帳號與密碼，將 HTTP 輸入限制在登入所需欄位，交由服務統一驗證。
public sealed record LoginRequest(string? UserName, string? Password);

// 接收目前密碼、新密碼與讀取版本，讓服務能拒絕缺少憑證或版本衝突的變更。
public sealed record PasswordRequest(string? CurrentPassword, string? NewPassword, string? Version);

// Version 是改密碼時原樣傳回的不透明版本；角色及改密碼要求由資料庫取得。
public sealed record IdentityResponse(
    string UserName,
    string AccountType,
    string[] Roles,
    string Version,
    bool MustChangePassword = false);

// 將錯誤代碼與顯示訊息分開，讓前端能用穩定代碼判斷恢復方式。
public sealed record ErrorResponse(string Code, string Message);

// 將請求權杖交給前端，供後續變更請求透過標頭傳回。
public sealed record CsrfResponse(string Token);
