using System.Text.Json;
using System.Text.Json.Serialization;

namespace Procurement;

// 建立允許空白草稿，身分、狀態與金額一律由系統決定。
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateDraftRequest(string? Subject, string? Purpose, DraftLineRequest?[]? Lines);

// 保存是完整取代，允許明確清空欄位，但不可因遺漏欄位清除原值。
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SaveDraftRequest(
    [property: JsonRequired] string? Subject,
    [property: JsonRequired] string? Purpose,
    [property: JsonRequired] DraftLineRequest?[]? Lines,
    string? Version);

// 刪除要附上讀取時的版本，避免移除已被另一個視窗更新的草稿。
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DeleteDraftRequest(string? Version);

// 保留數量和單價的原始數字 token，才能在轉型前拒絕不合法的格式或精度。
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DraftLineRequest(string? ItemName, string? Unit, JsonElement? Quantity, JsonElement? UnitPrice);

// 清單只公開足以辨識本人的草稿及最近更新時間。
internal sealed record DraftSummary(Guid Id, string Number, string? Subject, DateTimeOffset UpdatedAt);

// 明細小計由系統計算；缺少任一數值時保留 null，供編輯畫面標示未填完。
internal sealed record DraftLineResponse(
    string? ItemName, string? Unit, int? Quantity, decimal? UnitPrice, decimal? EstimatedAmount);

// 完整性只描述草稿欄位與筆數，總額可能只涵蓋可計算的明細。
internal sealed record DraftResponse(
    Guid Id,
    string Number,
    string ApplicantId,
    string Status,
    string? Subject,
    string? Purpose,
    DraftLineResponse[] Lines,
    decimal? EstimatedAmount,
    bool IsComplete,
    string Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
