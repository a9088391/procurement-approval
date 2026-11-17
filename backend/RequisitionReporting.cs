using System.Globalization;
using System.Text;

namespace Procurement;

// 同一批可見送審資料依入口回傳原有摘要、統計群組或基本 CSV。
internal enum RequisitionReadKind
{
    List,
    Statistics,
    Csv
}

// 以已驗證的範圍、識別及 UTC 界線組成 SQL 條件，避免查詢依賴主機時區。
internal sealed record RequisitionReportFilter(bool SubmittedOnly, DateTimeOffset? StartUtc, DateTimeOffset? EndUtcExclusive,
    string? DepartmentId, string? Status)
{
    internal static readonly TimeZoneInfo Taiwan = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    // 三種查詢使用相同驗證；沒有任何已知參數的一般清單保留既有範圍。
    internal static RequisitionReportFilter? Read(IQueryCollection query)
    {
        // 缺省與空值不同，拒絕重複參數及既定格式以外的值。
        if (!ReadSingle(query, "scope", out var scope)
            || !ReadSingle(query, "startDate", out var startValue)
            || !ReadSingle(query, "endDate", out var endValue)
            || !ReadSingle(query, "departmentId", out var department)
            || !ReadSingle(query, "status", out var status)
            || scope is not (
                null
                or "submitted")
            || !ReadDate(startValue, out var start)
            || !ReadDate(endValue, out var end)
            || (department is not null
                && (department.Length != 32
                    || !Guid.TryParseExact(department, "N", out _)))
            || status is not (
                null
                or Requisition.ManagerPending
                or Requisition.FinancePending
                or Requisition.Approved
                or Requisition.Rejected))
            return null;

        // 排除無法表示的 UTC 起點或翌日終點；迄日以翌日零時切分，不依賴資料庫小數秒精度。
        if (start > end
            || start == DateOnly.MinValue
            || end == DateOnly.MaxValue)
            return null;

        // 清單只要指定任一報表參數即採送審範圍；未知部門識別仍交給 SQL 回傳空集合。
        var submittedOnly = scope is not null
            || start is not null
            || end is not null
            || department is not null
            || status is not null;
        return new RequisitionReportFilter(submittedOnly,
            start is { } first ? ToUtc(first) : null,
            end is { } last ? ToUtc(last.AddDays(1)) : null,
            department?.ToLowerInvariant(), status);
    }

    // 拒絕空字串及同名多值，避免框架自動選取第一個值改變篩選語意。
    private static bool ReadSingle(IQueryCollection query, string name, out string? value)
    {
        // 參數缺省才表示不篩選，提供參數時必須恰有一個非空值。
        value = null;
        if (!query.TryGetValue(name, out var values))
            return true;
        value = values.Count == 1 ? values[0] : null;
        return !string.IsNullOrEmpty(value);
    }

    // 僅接受 YYYY-MM-DD，固定文化及字面往返檢查拒絕空白或非標準寫法。
    private static bool ReadDate(string? value, out DateOnly? date)
    {
        // 未指定日期就維持開放界線；只有完整且可還原成原字串的日期會取得值。
        date = null;
        if (value is null)
            return true;
        const string format = "yyyy-MM-dd";
        if (value.Length != format.Length
            || !DateOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || parsed.ToString(format, CultureInfo.InvariantCulture) != value)
            return false;
        date = parsed;
        return true;
    }

    // 以臺灣當日開始的時區規則轉成 UTC，歷史午夜跳時則以該次跳時的時間作為界線。
    private static DateTimeOffset ToUtc(DateOnly day)
    {
        // 若午夜落在歷史時差缺口，改取前一刻的時差；若有兩個午夜，採較早的瞬間。
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        var offset = Taiwan.IsInvalidTime(midnight) ? Taiwan.GetUtcOffset(midnight.AddTicks(-1))
            : Taiwan.IsAmbiguousTime(midnight) ? Taiwan.GetAmbiguousTimeOffsets(midnight).Max()
            : Taiwan.GetUtcOffset(midnight);
        return new DateTimeOffset(midnight, offset).ToUniversalTime();
    }
}

// 統計金額保留精確十進位，部門識別和當次可見的顯示名稱分開。
internal sealed record RequisitionStatistics(RequisitionStatisticsGroup[] Groups);
internal sealed record RequisitionStatisticsGroup(RequisitionDepartment Department, string Status, int Count, decimal EstimatedAmount);

// 匯出只保留必要欄位，內容來自當前已授權且通過篩選的送審資料。
internal sealed record RequisitionCsvRow(string Number, string? Subject, string Applicant, string Department, string Status,
    decimal? EstimatedAmount, DateTimeOffset SubmittedAt);

// 保留 HTTP 200 以完成唯讀交易；回應開始時才編碼，不在組織鎖內等客戶端接收資料。
internal sealed class RequisitionCsvDownload(RequisitionCsvRow[] rows) : IResult, IStatusCodeHttpResult
{
    public int? StatusCode => StatusCodes.Status200OK;

    // 交由框架設定下載標頭，回應中止時同步停止 CSV 寫入。
    public Task ExecuteAsync(HttpContext http) =>
        Results.Stream(stream => WriteCsv(stream, http.RequestAborted), "text/csv; charset=utf-8", "requisitions.csv")
            .ExecuteAsync(http);

    // 固定 UTF-8 BOM、CRLF 與欄位順序，讓試算表可辨識中文並依標準讀回每欄。
    private async Task WriteCsv(Stream stream, CancellationToken ct)
    {
        // 保留框架管理的回應串流，使用 BOM 讓 Excel 辨識編碼。
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true)
        {
            NewLine = "\r\n"
        };
        const string header = "單號,主旨,申請人顯示名稱,送審部門,目前狀態,TWD 預估金額,最近送審時間";
        await writer.WriteLineAsync(header.AsMemory(), ct);

        // 使用者文字才需要公式防護；單號、狀態、數字及日期由伺服器產生。
        foreach (var row in rows)
        {
            // 以臺灣時間輸出明確時差；金額用固定小數點及兩位小數，不受主機文化影響。
            var line = string.Join(',', row.Number, QuoteText(row.Subject ?? ""), QuoteText(row.Applicant), QuoteText(row.Department),
                StatusName(row.Status), row.EstimatedAmount?.ToString("F2", CultureInfo.InvariantCulture),
                TimeZoneInfo.ConvertTime(row.SubmittedAt, RequisitionReportFilter.Taiwan).ToString("O", CultureInfo.InvariantCulture));
            await writer.WriteLineAsync(line.AsMemory(), ct);
        }
        await writer.FlushAsync(ct);
    }

    // 先加單引號防止公式執行，再以 RFC 4180 跳脫欄內引號、分隔符與換行。
    private static string QuoteText(string text) => $"\"{(NeedsFormulaProtection(text) ? "'" : "")}{text.Replace("\"", "\"\"")}\"";

    // 試算表可能忽略前置空白或控制字元，必須檢查後續的公式觸發符號。
    private static bool NeedsFormulaProtection(string text)
    {
        // 換行、Tab、NUL 與全形運算符也需保護；第一個一般文字已足以排除公式前綴。
        foreach (var character in text.EnumerateRunes())
        {
            if (character.Value is '=' or '+' or '-' or '@' or '＝' or '＋' or '－' or '＠' or '\t' or '\r' or '\n' or '\0')
                return true;
            if (!Rune.IsWhiteSpace(character)
                && !Rune.IsControl(character)
                && Rune.GetUnicodeCategory(character) != UnicodeCategory.Format)
                return false;
        }
        return false;
    }

    // CSV 狀態沿用請購畫面的標籤；暫停仍屬原待審關卡。
    private static string StatusName(string status) => status switch
    {
        Requisition.ManagerPending => "主管待審",
        Requisition.FinancePending => "財務待審",
        Requisition.Approved => "核准結案",
        Requisition.Rejected => "駁回結案",
        _ => throw new InvalidOperationException("CSV 僅能匯出已送審狀態。")
    };
}
