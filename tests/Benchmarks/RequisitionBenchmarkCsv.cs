using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using Xunit;
using static Procurement.Benchmarks.BenchmarkFixture;

namespace Procurement.Benchmarks;

public sealed partial class RequisitionBenchmarkTests
{
    // 獨立場景直接量測完整 CSV，再確認每一列和目前登入者的資料範圍。
    [Fact]
    public async Task Csv()
    {
        // 起迄日及狀態同時生效；另一名申請人同部門且有完全符合篩選的單。
        using var fixture = await Start(csv: true);
        var path = Requisitions + "/csv?startDate=2024-06-18&endDate=2024-06-18&status=manager_pending"
            + $"&departmentId={fixture.Applicants[0].Department.Id}";
        var owner = fixture.Applicants[0].Client;
        var other = fixture.Applicants[1].Client;
        output.WriteLine("fixture headers=10003 matching_visible=10000 matching_hidden=1 visible_filter_misses=2");

        // 從送出正常 HTTP GET 計時至內容完整讀取，欄位核對在計時停止後才執行。
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var started = Stopwatch.GetTimestamp();
        using var response = await owner.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
        var elapsed = Stopwatch.GetElapsedTime(started);
        output.WriteLine(FormattableString.Invariant($"csv http={(int)response.StatusCode} bytes={bytes.Length} elapsed_ms={elapsed.TotalMilliseconds:F2} limit_ms=5000"));
        var rows = ParseCsv(response, bytes);
        var expected = fixture.Rows.Take(CsvCount).ToDictionary(row => row.Number);

        // 每列七個欄位逐一核對，重複單號、排除單或額外資料都無法從預期集合移除。
        Assert.Equal(CsvCount, rows.Count);
        foreach (var fields in rows)
        {
            Assert.Equal(7, fields.Length);
            Assert.True(expected.Remove(fields[0], out var row), "CSV 出現重複或不在預期範圍的單號。");
            AssertCsvRow(row, fields);
        }
        Assert.Empty(expected);

        // 確認另一人的單本身可正常查閱與下載，同一篩選對原登入者不可見。
        var hidden = fixture.Rows[CsvCount];
        using var forbidden = await owner.GetAsync(Requisitions + $"/{hidden.Id:N}");
        using var allowed = await other.GetAsync(Requisitions + $"/{hidden.Id:N}");
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var otherCsv = await other.GetAsync(path);
        var otherRows = ParseCsv(otherCsv, await otherCsv.Content.ReadAsByteArrayAsync());
        AssertCsvRow(hidden, Assert.Single(otherRows));

        // 時間與內容都須符合才通過；明細權限與匯出範圍分開列出便於核對。
        output.WriteLine($"csv verified_rows={rows.Count} unique_rows={CsvCount} excluded_sentinels=3 " +
            $"hidden_detail_http={(int)forbidden.StatusCode} authorized_detail_http={(int)allowed.StatusCode} other_verified_rows={otherRows.Count}");
        Assert.True(elapsed <= TimeSpan.FromSeconds(5), "CSV 完整下載超過五秒門檻。");
    }

    // 核對 HTTP 下載契約及 UTF-8 BOM，再用 CSV parser 處理引號與欄內換行。
    private static List<string[]> ParseCsv(HttpResponseMessage response, byte[] bytes)
    {
        // 型別、編碼和檔名均由真正的下載回應提供。
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.Equal("attachment", disposition?.DispositionType);
        Assert.Equal("requisitions.csv", disposition?.FileNameStar ?? disposition?.FileName?.Trim('"'));
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));

        // 禁止 parser 自動修剪，讓輸出的實際欄值與契約保持一致。
        using var stream = new MemoryStream(bytes);
        using var parser = new TextFieldParser(stream, new UTF8Encoding(false, true))
        {
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        Assert.Equal(new[] { "單號", "主旨", "申請人顯示名稱", "送審部門", "目前狀態", "TWD 預估金額", "最近送審時間" },
            parser.ReadFields());
        var rows = new List<string[]>();
        while (!parser.EndOfData)
            rows.Add(parser.ReadFields() ?? throw new InvalidOperationException("CSV 資料列不得為空。"));
        return rows;
    }

    // 以固定的臺灣時間、精確金額及種子內容驗證七欄，公式只在匯出時加保護。
    private static void AssertCsvRow(SeedRow row, string[] fields)
    {
        var subject = row.Subject.StartsWith('=') ? "'" + row.Subject : row.Subject;
        Assert.Equal(new[] { row.Number, subject, row.Owner.User.UserName, row.Owner.Department.Name,
            "主管待審", "35.55", "2024-06-18T10:30:00.0000000+08:00" }, fields);
    }
}
