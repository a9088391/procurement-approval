using System.Globalization;
using System.Text.Json;

namespace Procurement;

// 集中處理草稿可編輯內容，拒絕無法保存的輸入，並保留可計算的精確小計。
internal sealed record RequisitionDraftInput(
    string? Subject, string? Purpose, List<RequisitionLine> Lines, decimal? EstimatedAmount)
{
    private const int MaxQuantity = 10_000;
    private const decimal MaxUnitPrice = 1_000_000m;
    private const decimal MaxTotal = 10_000_000m;

    // 將建立與保存的欄位轉成同一種已驗證內容，兩個入口共用金額與輸入規則。
    internal static RequisitionDraftInput? Read(string? subject, string? purpose, DraftLineRequest?[]? requests)
    {
        // 草稿文字可留空，原始換行及 NUL 依欄位限制檢查，不修剪使用者內容。
        if (!ValidText(subject, Requisition.SubjectLimit) || !ValidText(purpose, Requisition.PurposeLimit, multiline: true))
            return null;

        // 按原始順序準備明細，只有數量和單價均存在的列會計入已知金額。
        var lines = new List<RequisitionLine>();
        decimal? total = null;
        foreach (var request in requests ?? [])
        {
            // 整列不能是 null，個別欄位可以留空，填入的數字則必須完全合法。
            if (request is null
                || !ValidText(request.ItemName, RequisitionLine.ItemNameLimit)
                || !ValidText(request.Unit, RequisitionLine.UnitLimit)
                || !TryQuantity(request.Quantity, out var quantity)
                || !TryPrice(request.UnitPrice, out var price))
                return null;

            // 用已驗證的十進位數字計算，不讓未填完的列掩蓋整單超限。
            var line = new RequisitionLine
            {
                Position = lines.Count,
                ItemName = request.ItemName,
                Unit = request.Unit,
                Quantity = quantity,
                UnitPrice = price
            };
            if (line.EstimatedAmount is { } amount)
            {
                total = (total ?? 0m) + amount;
                if (total > MaxTotal)
                    return null;
            }
            lines.Add(line);
        }

        // 沒有任何可計算列時保留 null，避免顯示成已知的零元。
        return new RequisitionDraftInput(subject, purpose, lines, total);
    }

    // 草稿超過送審筆數或缺少欄位仍可保存，完整標記只描述已填內容。
    internal static bool IsComplete(Requisition draft) =>
        !string.IsNullOrWhiteSpace(draft.Subject)
        && !string.IsNullOrWhiteSpace(draft.Purpose)
        && draft.Lines.Count is >= 1 and <= 100
        && draft.Lines.All(line =>
            !string.IsNullOrWhiteSpace(line.ItemName)
            && !string.IsNullOrWhiteSpace(line.Unit)
            && line.Quantity.HasValue
            && line.UnitPrice.HasValue);

    // 送審重查已保存內容與金額，讓輪次只收錄完整且可核對的申請。
    internal static bool CanSubmit(Requisition draft)
    {
        // 驗證必要欄位及每列上下限後才計算總額，避免把無效舊資料帶入歷程。
        if (!IsComplete(draft) || draft.Lines.Any(line =>
            line.Quantity is < 1 or > MaxQuantity || line.UnitPrice is <= 0m or > MaxUnitPrice))
            return false;

        // 草稿數值的精度已由輸入及資料庫欄位限制；總額須符合明細及整單上限。
        var amount = draft.Lines.Sum(line => line.EstimatedAmount);
        return amount <= MaxTotal && amount == draft.EstimatedAmount;
    }

    // 拒絕 PostgreSQL 無法保存的 NUL，並使單行欄位可由表單完整讀回。
    private static bool ValidText(string? value, int limit, bool multiline = false) =>
        value is null || (value.Length <= limit
            && !value.Contains('\0')
            && (multiline || !AuthService.ContainsCrOrLf(value)));

    // 只接受空值或原始 JSON 整數，禁止數字字串、小數或科學記號。
    private static bool TryQuantity(JsonElement? token, out int? quantity)
    {
        // null 代表尚未填寫，與合法的正整數分開處理。
        quantity = null;
        if (token is not { } raw)
            return true;

        // 依固定文化設定解析，確認詞法及數量範圍再交給計算。
        if (raw.ValueKind != JsonValueKind.Number
            || !int.TryParse(raw.GetRawText(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value is < 1 or > MaxQuantity)
            return false;

        quantity = value;
        return true;
    }

    // 單價是一般十進位 JSON 數字，轉成 decimal 之前先驗證最多兩位小數。
    private static bool TryPrice(JsonElement? token, out decimal? price)
    {
        // 允許未填單價，已填值則要保留原始 token 供精度檢查。
        price = null;
        if (token is not { } raw)
            return true;
        if (raw.ValueKind != JsonValueKind.Number)
            return false;

        // 小數尾端即使全是零也要計入位數，避免解析時靜默縮減精度。
        var text = raw.GetRawText();
        var point = text.IndexOf('.');
        if ((point >= 0
                && text.Length - point - 1 > 2)
            || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || value is <= 0m
                or > MaxUnitPrice)
            return false;

        price = value;
        return true;
    }
}
