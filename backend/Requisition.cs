using Microsoft.EntityFrameworkCore;

namespace Procurement;

// 請購主檔保留系統識別、所有人與狀態；草稿階段的時間只代表建立及保存。
internal sealed class Requisition
{
    internal const string DraftStatus = "draft";
    internal const int SubjectLimit = 200;
    internal const int PurposeLimit = 4000;

    public Guid Id { get; set; }
    public string Number { get; set; } = "";
    public string ApplicantId { get; set; } = "";
    public string Status { get; set; } = DraftStatus;
    public string? Subject { get; set; }
    public string? Purpose { get; set; }
    public List<RequisitionLine> Lines { get; set; } = [];
    public decimal? EstimatedAmount { get; set; }
    public string Version { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // 將主檔與明細關聯、精度及清單索引集中，供同一個 DbContext 與 migration 使用。
    internal static void Configure(ModelBuilder builder)
    {
        // 單號獨立唯一；清單依所有人和狀態篩選，再以更新時間及識別穩定排序。
        var headers = builder.Entity<Requisition>();
        headers.ToTable("Requisitions");
        headers.Property(d => d.Number).HasMaxLength(35);
        headers.Property(d => d.Status).HasMaxLength(20);
        headers.Property(d => d.Subject).HasMaxLength(SubjectLimit);
        headers.Property(d => d.Purpose).HasMaxLength(PurposeLimit);
        headers.Property(d => d.EstimatedAmount).HasPrecision(10, 2);
        headers.Property(d => d.Version).HasMaxLength(32);
        headers.HasIndex(d => d.Number).IsUnique();
        headers
            .HasIndex(d => new { d.ApplicantId, d.Status, d.UpdatedAt, d.Id })
            .IsDescending(false, false, true, false);

        // 帳號停用時保留請購；刪除草稿才由外鍵一起清除明細。
        headers
            .HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(d => d.ApplicantId)
            .OnDelete(DeleteBehavior.Restrict);
        headers
            .HasMany(d => d.Lines)
            .WithOne()
            .HasForeignKey(l => l.RequisitionId)
            .OnDelete(DeleteBehavior.Cascade);

        // 同一張請購的明細以位置識別，避免因資料庫預設排序而重開出不同順序。
        var lines = builder.Entity<RequisitionLine>();
        lines.ToTable("RequisitionLines");
        lines.HasKey(l => new { l.RequisitionId, l.Position });
        lines.Property(l => l.ItemName).HasMaxLength(RequisitionLine.ItemNameLimit);
        lines.Property(l => l.Unit).HasMaxLength(RequisitionLine.UnitLimit);
        lines.Property(l => l.UnitPrice).HasPrecision(9, 2);
    }
}

// 草稿明細依輸入位置保存，欄位可空白，小計始終由數量及單價推導。
internal sealed class RequisitionLine
{
    internal const int ItemNameLimit = 200;
    internal const int UnitLimit = 40;

    public Guid RequisitionId { get; set; }
    public int Position { get; set; }
    public string? ItemName { get; set; }
    public string? Unit { get; set; }
    public int? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? EstimatedAmount => Quantity * UnitPrice;
}
