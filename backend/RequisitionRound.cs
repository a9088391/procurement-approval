using Microsoft.EntityFrameworkCore;

namespace Procurement;

// 每輪獨立保留當時的內容、送審部門及原始人選；之後的設定異動不改寫快照。
internal sealed class RequisitionRound
{
    public Guid RequisitionId { get; set; }
    public int Number { get; set; }
    public string DepartmentId { get; set; } = "";
    public string DepartmentName { get; set; } = "";
    public DateTimeOffset SubmittedAt { get; set; }
    public string ManagerId { get; set; } = "";
    public AppUser? Manager { get; set; }
    public string FinanceId { get; set; } = "";
    public AppUser? Finance { get; set; }
    public string? ManagerReplacementId { get; set; }
    public string? FinanceReplacementId { get; set; }
    public string Content { get; set; } = "";
    public List<RequisitionApprovalEvent> History { get; set; } = [];

    // 將輪次、原始指派及歷程完整性集中於資料庫模型，防止誤刪關聯或重複決定。
    internal static void Configure(ModelBuilder builder)
    {
        // 同單同輪只能建立一次，送審後的請購不能透過刪除主檔連帶移除輪次。
        var rounds = builder.Entity<RequisitionRound>();
        rounds.ToTable("RequisitionRounds", table =>
        {
            // 原始人選與改派後的兩關人選各自維持分工。
            table.HasCheckConstraint("CK_RequisitionRounds_DistinctReviewers", """ "ManagerId" <> "FinanceId" """);
            table.HasCheckConstraint("CK_RequisitionRounds_DistinctEffectiveReviewers",
                """ COALESCE("ManagerReplacementId", "ManagerId") <> COALESCE("FinanceReplacementId", "FinanceId") """);
        });
        rounds.HasKey(r => new { r.RequisitionId, r.Number });
        rounds.Property(r => r.DepartmentName).HasMaxLength(Department.NameLimit);
        rounds.Property(r => r.Content).HasColumnType("jsonb");
        rounds
            .HasOne<Requisition>()
            .WithMany(d => d.Rounds)
            .HasForeignKey(r => r.RequisitionId)
            .OnDelete(DeleteBehavior.Restrict);

        // 部門與帳號可停用；外鍵保留當時關聯，部門名稱仍讀取輪次上的值。
        rounds
            .HasOne<Department>()
            .WithMany()
            .HasForeignKey(r => r.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);
        rounds
            .HasOne(r => r.Manager)
            .WithMany()
            .HasForeignKey(r => r.ManagerId)
            .OnDelete(DeleteBehavior.Restrict);
        rounds
            .HasOne(r => r.Finance)
            .WithMany()
            .HasForeignKey(r => r.FinanceId)
            .OnDelete(DeleteBehavior.Restrict);

        // 改派只新增有效指派關聯，原始快照的兩個外鍵保持原值。
        rounds
            .HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(r => r.ManagerReplacementId)
            .OnDelete(DeleteBehavior.Restrict);
        rounds
            .HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(r => r.FinanceReplacementId)
            .OnDelete(DeleteBehavior.Restrict);

        // 改派與決定都要記關卡，只有改派具備前後人選與原因。
        var history = builder.Entity<RequisitionApprovalEvent>();
        history.ToTable("RequisitionApprovalEvents", table =>
        {
            // 舊事件保留原形狀；新事件須留下不同的前後人選及原因。
            table.HasCheckConstraint("CK_RequisitionApprovalEvents_KindStage",
                """
                ("Kind" IN ('submitted', 'withdrawn') AND "Stage" IS NULL) OR
                ("Kind" IN ('approved', 'rejected', 'reassigned') AND "Stage" IS NOT NULL AND "Stage" IN ('manager', 'finance'))
                """);
            table.HasCheckConstraint("CK_RequisitionApprovalEvents_Reassignment",
                """
                ("Kind" = 'reassigned'
                    AND "PreviousReviewerId" IS NOT NULL AND "NewReviewerId" IS NOT NULL
                    AND "PreviousReviewerId" <> "NewReviewerId" AND "Reason" IS NOT NULL AND length("Reason") > 0) OR
                ("Kind" <> 'reassigned' AND "PreviousReviewerId" IS NULL AND "NewReviewerId" IS NULL AND "Reason" IS NULL)
                """);
        });
        history.HasKey(e => e.Id);
        history.Property(e => e.Kind).HasMaxLength(20);
        history.Property(e => e.Stage).HasMaxLength(10);
        history.Property(e => e.Reason).HasMaxLength(RequisitionApprovalEvent.ReasonLimit);

        // 各關核准或駁回互斥；完整輪次索引涵蓋不在部分索引中的改派事件。
        history.HasIndex(e => new { e.RequisitionId, e.RoundNumber });
        history
            .HasIndex(e => new { e.RequisitionId, e.RoundNumber, e.Stage })
            .IsUnique()
            .HasFilter(""" "Kind" IN ('approved', 'rejected') """);
        history
            .HasIndex(e => new { e.RequisitionId, e.RoundNumber, e.Kind })
            .IsUnique()
            .HasFilter(""" "Stage" IS NULL """);
        history
            .HasOne<RequisitionRound>()
            .WithMany(r => r.History)
            .HasForeignKey(e => new { e.RequisitionId, Number = e.RoundNumber })
            .OnDelete(DeleteBehavior.Restrict);
        // 人員僅停用，保留每次改派與其他歷程的帳號關聯。
        history
            .HasOne(e => e.PreviousReviewer)
            .WithMany()
            .HasForeignKey(e => e.PreviousReviewerId)
            .OnDelete(DeleteBehavior.Restrict);
        history
            .HasOne(e => e.NewReviewer)
            .WithMany()
            .HasForeignKey(e => e.NewReviewerId)
            .OnDelete(DeleteBehavior.Restrict);
        history
            .HasOne(e => e.Actor)
            .WithMany()
            .HasForeignKey(e => e.ActorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

// 歷程保存原始操作者與關卡結果；透過輪次外鍵避免孤立紀錄。
internal sealed class RequisitionApprovalEvent
{
    internal const string Submitted = "submitted";
    internal const string Withdrawn = "withdrawn";
    internal const string Reassigned = "reassigned";
    internal const int ReasonLimit = 500;
    internal const string ManagerStage = "manager";
    internal const string FinanceStage = "finance";

    public long Id { get; set; }
    public Guid RequisitionId { get; set; }
    public int RoundNumber { get; set; }
    public string Kind { get; set; } = "";
    public string? Stage { get; set; }
    public string ActorId { get; set; } = "";
    public AppUser? Actor { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? PreviousReviewerId { get; set; }
    public AppUser? PreviousReviewer { get; set; }
    public string? NewReviewerId { get; set; }
    public AppUser? NewReviewer { get; set; }
    public string? Reason { get; set; }
}

// 以固定的快照形狀保存送審當下的資料，明細順序由陣列保留。
internal sealed record RequisitionSnapshot(string? Subject, string? Purpose, decimal? EstimatedAmount, RequisitionSnapshotLine[] Lines);
internal sealed record RequisitionSnapshotLine(string? ItemName, string? Unit, int? Quantity, decimal? UnitPrice);
