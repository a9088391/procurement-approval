using System.Text.Json.Serialization;

namespace Procurement;

// 只接受讀取時的版本，拒絕客戶端代填身分、指派、內容或結果。
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ApprovalRequest(string? Version);

// 限定人員、部門與歷程的公開欄位，避免回傳帳號設定或憑證。
internal sealed record RequisitionPerson(string Id, string UserName);
internal sealed record RequisitionDepartment(string Id, string Name);
internal sealed record RequisitionRoundSummary(
    int Number, RequisitionDepartment Department, DateTimeOffset SubmittedAt, RequisitionPerson Manager, RequisitionPerson Finance);
internal sealed record RequisitionHistoryEntry(
    int RoundNumber, string Kind, string? Stage, RequisitionPerson Actor, DateTimeOffset OccurredAt);

// 先前輪次僅供申請人查閱，固定內容與該輪部門、指派放在一起。
internal sealed record RequisitionPreviousRound(
    RequisitionRoundSummary Round, string? Subject, string? Purpose, decimal? EstimatedAmount, DraftLineResponse[] Lines);

// 清單不載入逐筆明細與歷程；操作提示仍按目前身分及未完成關卡判斷。
internal record RequisitionSummary(
    Guid Id,
    string Number,
    string? Subject,
    RequisitionPerson Applicant,
    string Status,
    decimal? EstimatedAmount,
    string Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Paused,
    bool CanDecide,
    RequisitionRoundSummary? Round);

// 明細沿用清單欄位，另公開內容、可用操作及符合目前身分的歷程。
internal sealed record RequisitionDetail : RequisitionSummary
{
    // 複製已通過授權的摘要，將明細展開為同一層 JSON 欄位。
    internal RequisitionDetail(
        RequisitionSummary summary, string? purpose, DraftLineResponse[] lines, bool isComplete, bool canSubmit,
        bool canWithdraw, RequisitionHistoryEntry[] history, RequisitionPreviousRound[] previousRounds) : base(summary)
    {
        Purpose = purpose;
        Lines = lines;
        IsComplete = isComplete;
        CanSubmit = canSubmit;
        CanWithdraw = canWithdraw;
        History = history;
        PreviousRounds = previousRounds;
    }

    public string? Purpose { get; }
    public DraftLineResponse[] Lines { get; }
    public bool IsComplete { get; }
    public bool CanSubmit { get; }
    public bool CanWithdraw { get; }
    public RequisitionHistoryEntry[] History { get; }
    public RequisitionPreviousRound[] PreviousRounds { get; }
}
