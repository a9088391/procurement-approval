using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using static Procurement.Benchmarks.BenchmarkFixture;

namespace Procurement.Benchmarks;

// 透過獨立 Kestrel、PostgreSQL 和真實登入，量測正式請購 API 的可見資料與成功異動。
public sealed partial class RequisitionBenchmarkTests(ITestOutputHelper output)
{
    private const int Rate = 30;
    private const double SlotMs = 1000d / Rate;
    private const int WarmSeconds = 60;
    private const int MeasuredSeconds = 300;
    private static readonly TimeSpan Drain = TimeSpan.FromSeconds(30);

    // 每 20 個時槽為一個固定比例循環，將寫入分散在讀取之間。
    private static readonly Operation[] Mix =
    [
        Operation.List, Operation.Detail, Operation.List, Operation.Statistics, Operation.List,
        Operation.Save, Operation.List, Operation.Detail, Operation.List, Operation.Submit,
        Operation.List, Operation.Detail, Operation.List, Operation.Statistics, Operation.List,
        Operation.Save, Operation.List, Operation.Detail, Operation.List, Operation.Approve
    ];

    // 完整預熱後獨立量測五分鐘，再比對兩段的回應、持久化及未異動資料。
    [Fact]
    public async Task Load()
    {
        // 先以資料庫確認初始規模，再由 HTTP 檢查各身分的實際可見範圍及讀取內容。
        using var fixture = await Start(csv: false);
        await VerifyFixtureSize();
        await VerifyReadScopes(fixture);
        var plan = Plan(fixture);
        output.WriteLine("fixture applicants=4 visible_per_applicant=2500 " +
            "draft_save=1080 draft_submit=540 manager_pending=270 finance_pending=270 approved=7840");

        // 預熱流量先停止且等待完成，正式量測才使用另一批有效版本及案件。
        var warm = await RunPhase(plan[..(WarmSeconds * Rate)], WarmSeconds);
        if (warm.Results.Any(result => result.StartMs.HasValue && !result.ElapsedMs.HasValue))
        {
            // 取消的用戶端請求可能仍在伺服器執行，這種預熱不具備正式量測前提。
            Report("warmup", warm, persistence: null);
            Assert.Fail("預熱請求未完整接收，停止正式量測。");
        }
        var measured = await RunPhase(plan[(WarmSeconds * Rate)..], MeasuredSeconds);

        // 以獨立連線核對兩段所有案件，先輸出數據與失敗摘要，再決定門檻是否通過。
        var writes = warm
            .Results
            .Concat(measured.Results)
            .Where(result => result.Request.IsWrite);
        Persistence persistence;
        try
        {
            persistence = await ReadBack(fixture, writes);
        }
        catch (Exception error) when (error is NpgsqlException or JsonException)
        {
            // 即使資料庫核對失敗，仍保留已完成的 HTTP 證據。
            Report("warmup", warm, persistence: null);
            Report("measured", measured, persistence: null);
            throw;
        }
        var warmPassed = Report("warmup", warm, persistence);
        var measuredPassed = Report("measured", measured, persistence);
        output.WriteLine($"persistence durable_writes={persistence.Durable.Count} matched_writes={persistence.Successful.Count} mismatches={persistence.Errors.Count}");
        foreach (var error in persistence.Errors.Take(8))
            output.WriteLine(error);
        Assert.True(
            warmPassed
                && measuredPassed
                && persistence.Errors.Count == 0,
            "請求量、正式送出速率、成功寫入、持久化或各操作延遲未通過，請查閱量測輸出。");
    }

    // 直接從專用 PostgreSQL 讀取總量及逐單明細數，避免僅憑預載設定宣稱初始規模。
    private async Task VerifyFixtureSize()
    {
        // 單一唯讀敘述使用同一個資料庫快照，零筆或超過五筆的請購都會被計入。
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM "Requisitions"),
                   (SELECT count(*) FROM "RequisitionLines"),
                   (SELECT count(*) FROM (
                       SELECT h."Id"
                       FROM "Requisitions" h
                       LEFT JOIN "RequisitionLines" l ON l."RequisitionId" = h."Id"
                       GROUP BY h."Id"
                       HAVING count(l."Position") <> 5
                   ) AS different_line_counts);
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        // 先輸出實際筆數，種子資料不符時停止量測並保留可定位的證據。
        var headers = reader.GetInt64(0);
        var lines = reader.GetInt64(1);
        var different = reader.GetInt64(2);
        output.WriteLine($"fixture headers={headers} lines={lines} headers_without_five_lines={different}");
        Assert.Equal(10000L, headers);
        Assert.Equal(50000L, lines);
        Assert.Equal(0L, different);
    }

    // 用所有申請人的正式 GET 核對範圍，逐一確認清單、明細及統計都有資料可處理。
    private static async Task VerifyReadScopes(BenchmarkFixture fixture)
    {
        foreach (var applicant in fixture.Applicants)
        {
            // 比對每張單的識別與所有人，避免清單重複、遺漏或納入其他申請人的單。
            var own = fixture.Rows.Where(row => row.Owner == applicant).ToArray();
            Assert.Equal(RowsPerOwner, own.Length);
            var list = await applicant.Client.GetFromJsonAsync<JsonElement>(Requisitions);
            Assert.Equal(own.Length, list.GetArrayLength());
            var ids = list
                .EnumerateArray()
                .Select(row => row.GetProperty("id").GetGuid())
                .ToHashSet();
            Assert.True(ids.SetEquals(own.Select(row => row.Id)));

            // 以已結案案件確認明細仍提供正確快照和五筆資料。
            var sample = own.First(row => row.Status == Approved);
            var detail = await applicant.Client.GetFromJsonAsync<JsonElement>(Requisitions + $"/{sample.Id:N}");
            Assert.Equal(sample.Id, detail.GetProperty("id").GetGuid());
            Assert.Equal(sample.Subject, detail.GetProperty("subject").GetString());
            Assert.Equal(Approved, detail.GetProperty("status").GetString());
            Assert.Equal(OriginalLines.Length, detail.GetProperty("lines").GetArrayLength());

            // 使用已知資料獨立計算統計，依目前送審部門、狀態、件數及金額驗證。
            var expected = own
                .Where(row => row.Status != Draft)
                .GroupBy(row => row.Status)
                .ToDictionary(group => group.Key, group => group.Count());
            var stats = await applicant.Client.GetFromJsonAsync<JsonElement>(Requisitions + "/statistics");
            var groups = stats.GetProperty("groups");
            Assert.Equal(expected.Count, groups.GetArrayLength());
            foreach (var group in groups.EnumerateArray())
            {
                Assert.Equal(
                    applicant.Department.Id,
                    group
                        .GetProperty("department")
                        .GetProperty("id")
                        .GetString());
                Assert.True(expected.Remove(group.GetProperty("status").GetString() ?? "", out var count));
                Assert.Equal(count, group.GetProperty("count").GetInt32());
                Assert.Equal(count * OriginalAmount, group.GetProperty("estimatedAmount").GetDecimal());
            }
        }
    }

    // 量測前決定請求和資料版本，時槽執行時不做安排案例用的額外查詢。
    private static LoadRequest[] Plan(BenchmarkFixture fixture)
    {
        // 每類操作各有輪替索引，避免固定流量比例讓某一身分承擔整類請求。
        var writeRows = fixture
            .Rows
            .Where(row => row.Operation.HasValue)
            .ToLookup(row => row.Operation)
            .ToDictionary(group => group.Key.GetValueOrDefault(), group => group.ToArray());
        var details = fixture
            .Rows
            .Where(row => row.Status == Approved)
            .ToLookup(row => row.Owner)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var counts = new int[Enum.GetValues<Operation>().Length];
        var plan = new LoadRequest[(WarmSeconds + MeasuredSeconds) * Rate];
        for (var slot = 0; slot < plan.Length; slot++)
        {
            // 一筆寫入對應唯一案件，明細則在各申請人已結案的單中輪替。
            var operation = Mix[slot % Mix.Length];
            var index = counts[(int)operation]++;
            var applicant = fixture.Applicants[index % OwnerCount];
            SeedRow? row = null;
            if (writeRows.TryGetValue(operation, out var available))
                row = available[index];
            else if (operation == Operation.Detail)
            {
                var owned = details[applicant];
                row = owned[index / OwnerCount % owned.Length];
            }
            var actor = operation == Operation.Approve
                ? (row?.Status == ManagerPending ? fixture.Manager : fixture.Finance)
                : applicant;
            var path = operation switch
            {
                Operation.List => Requisitions,
                Operation.Detail => Requisitions + $"/{row?.Id:N}",
                Operation.Statistics => Requisitions + "/statistics",
                Operation.Save => Requisitions + $"/drafts/{row?.Id:N}",
                Operation.Submit => Requisitions + $"/{row?.Id:N}/submit",
                Operation.Approve => Requisitions + $"/{row?.Id:N}/approve",
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            plan[slot] = new LoadRequest(operation, actor, path, row);
        }

        // 校驗批次和案件一對一，避免重複操作在正式量測期間製造人為衝突。
        var plannedWrites = plan.Where(request => request.IsWrite).ToArray();
        Assert.Equal(writeRows.Values.Sum(rows => rows.Length), plannedWrites.Length);
        Assert.Equal(
            plannedWrites.Length,
            plannedWrites
                .Select(request => request.Row?.Id)
                .Distinct()
                .Count());
        Assert.All(plannedWrites, request => Assert.NotNull(request.Row));
        return plan;
    }

    // 將時槽放在專用執行緒，避免 HTTP 完成後的工作集區續行拖延送出時機。
    private static async Task<Phase> RunPhase(LoadRequest[] plan, int seconds)
    {
        // 長時間工作在預設排程器上取得專用執行緒；個別 HTTP 任務仍可並行完成。
        var window = TimeSpan.FromSeconds(seconds);
        using var deadline = new CancellationTokenSource();
        var pending = await Task.Factory.StartNew(() => Schedule(plan, window, deadline),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // 送出視窗結束後只等已安排的回應，取消與排空仍有原本的時間上限。
        var results = await Task.WhenAll(pending).WaitAsync(Drain + TimeSpan.FromSeconds(5));
        return new Phase(seconds, results);
    }

    // 依單調時鐘的絕對時槽送出，時槽中央保留兩側餘裕，且不等待 HTTP 回應。
    private static Task<Observation>[] Schedule(LoadRequest[] plan, TimeSpan window, CancellationTokenSource deadline)
    {
        // 在專用執行緒就緒後才開始量測及取消倒數，排除建立執行緒的時間。
        var pending = new Task<Observation>[plan.Length];
        var origin = Stopwatch.GetTimestamp();
        deadline.CancelAfter(window + Drain);
        for (var slot = 0; slot < plan.Length; slot++)
        {
            // 即使某筆送出較慢，下一個時槽仍使用原來的絕對目標。
            WaitUntil(origin, TimeSpan.FromMilliseconds((slot + 0.5) * SlotMs));
            pending[slot] = Dispatch(plan[slot], origin, window, deadline.Token);
        }

        // 預熱與正式階段都保留完整視窗，較快回應也不會提早啟動下一階段。
        WaitUntil(origin, window);
        return pending;
    }

    // 只休眠專用排程執行緒，醒來後重查單調時鐘，避免計時器續行與 HTTP 爭用工作執行緒。
    private static void WaitUntil(long origin, TimeSpan target)
    {
        TimeSpan remaining;
        while ((remaining = target - Stopwatch.GetElapsedTime(origin)) > TimeSpan.Zero)
            Thread.Sleep(Math.Max(1, (int)remaining.TotalMilliseconds));
    }

    // 從真正呼叫 HTTP 開始計時，完整接收內容後停止，再辨識合約及結果。
    private static async Task<Observation> Dispatch(LoadRequest request, long origin, TimeSpan window, CancellationToken ct)
    {
        // 先準備合法請求，時鐘以交給 HTTP handler 前的時間為起點。
        using var message = new HttpRequestMessage(request.IsWrite ? HttpMethod.Post : HttpMethod.Get, request.Path);
        if (request.Row is { } row && request.IsWrite)
        {
            // 草稿保存送出完整替換內容；送審與審核只提供讀取時的版本。
            if (request.Operation == Operation.Save)
            {
                var content = SavedPayload(row);
                message.Content = JsonContent.Create(new { row.Version, content.Subject, content.Purpose, content.Lines });
            }
            else
                message.Content = JsonContent.Create(new { row.Version });
        }

        // 即使前面時槽出現堆積，逾期也不把事後補發算進正式視窗。
        var started = Stopwatch.GetTimestamp();
        var offset = Stopwatch.GetElapsedTime(origin, started);
        if (offset >= window)
            return new Observation(request, null, null, 0, "not_sent", null);
        var status = 0;
        double? elapsed = null;
        try
        {
            // 清單只讀非空 JSON 陣列的前綴及排空到 EOF，避免為數百 MB 回應配置完整緩衝區。
            using var response = await request.Actor.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            status = (int)response.StatusCode;
            if (response.StatusCode != HttpStatusCode.OK || request.Operation == Operation.List)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var prefix = new byte[2];
                var read = await stream.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, ct);
                await stream.CopyToAsync(Stream.Null, ct);
                elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var outcome = status switch
                {
                    200 when IsJson(response)
                        && read == prefix.Length
                        && prefix.AsSpan().SequenceEqual("[{"u8) => "ok",
                    409 => "conflict",
                    >= 400 and < 500 => "rejection",
                    >= 500 => "server_error",
                    _ => "contract_error"
                };
                return new Observation(request, offset.TotalMilliseconds, elapsed, status, outcome, null);
            }

            // 小型回應保留內容以驗證明細、統計和寫入回傳的識別、狀態及版本。
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var receipt = IsJson(response) ? VerifyResponse(request, body) : null;
            return new Observation(request, offset.TotalMilliseconds, elapsed, status,
                receipt is null ? "contract_error" : "ok", request.IsWrite ? receipt : null);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException)
        {
            // 留下安全的例外型別和回應狀態，無法完整接收的請求不列入延遲樣本。
            var outcome = error is JsonException ? "contract_error" : "transport_error";
            return new Observation(request, offset.TotalMilliseconds, elapsed, status, outcome, null, error.GetType().Name);
        }
    }

    // 確認成功回應仍符合 JSON 合約，避免 HTML 或空本文被視為有效資料。
    private static bool IsJson(HttpResponseMessage response) => response.Content.Headers.ContentType?.MediaType == "application/json";

    // 以輕量 DTO 驗證每個小型回應，寫入版本供後續獨立資料庫對帳。
    private static Receipt? VerifyResponse(LoadRequest request, byte[] body)
    {
        // 統計資料會隨合法寫入變動，仍須回傳至少一組目前申請人的送審資料。
        if (request.Operation == Operation.Statistics)
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("groups", out var groups)
                && groups.ValueKind == JsonValueKind.Array
                && groups.GetArrayLength() > 0
                ? new Receipt(default, "", "", default) : null;
        }

        // 所有明細及寫入回應都須對應指定的單及狀態；寫入還須換發版本。
        var result = JsonSerializer.Deserialize<Receipt>(body, JsonSerializerOptions.Web);
        var row = request.Row;
        if (result is null
            || row is null
            || result.Id != row.Id
            || result.Status != ExpectedStatus(row, request.IsWrite)
            || !Guid.TryParseExact(result.Version, "N", out _))
            return null;
        return request.IsWrite == (result.Version != row.Version) ? result : null;
    }

    // 分開呈現固定流量、HTTP 成敗、已持久化成功及各操作最近秩 p95。
    private bool Report(string name, Phase phase, Persistence? persistence)
    {
        // 以實際送出時間檢查每秒流量及排程誤差，另列出視窗截止後的最長尾端。
        var seconds = new int[phase.Seconds];
        var lag = 0d;
        var lateSlots = 0;
        var tail = 0d;
        for (var slot = 0; slot < phase.Results.Length; slot++)
        {
            var result = phase.Results[slot];
            if (result.StartMs is not { } sent)
                continue;
            seconds[(int)(sent / 1000)]++;
            var delay = sent - (slot + 0.5) * SlotMs;
            lag = Math.Max(lag, delay);
            if (delay > SlotMs)
                lateSlots++;
            if (result.ElapsedMs is { } elapsed)
                tail = Math.Max(tail, sent + elapsed - phase.Seconds * 1000);
        }
        // 正式視窗每秒都須實送 30 筆且不得落後一個時槽，避免延後集中送出仍通過。
        var belowRate = seconds.Count(count => count < Rate);
        var aboveRate = seconds.Count(count => count > Rate);
        var formal = phase.Seconds == MeasuredSeconds;
        var ratePassed = belowRate == 0
            && aboveRate == 0
            && lateSlots == 0;
        var rateResult = formal ? (ratePassed ? "pass" : "fail") : "observed_only";
        output.WriteLine(FormattableString.Invariant(
            $"phase={name} window_s={phase.Seconds} starts_per_second_min={seconds.Min()} max={seconds.Max()} seconds_below_rate={belowRate} seconds_above_rate={aboveRate} max_dispatch_lag_ms={lag:F3} lag_limit_ms={SlotMs:F3} slots_above_lag_limit={lateSlots} rate_fidelity={rateResult} tail_ms={tail:F2}"));

        // 即使速率失敗也輸出逐操作結果；預熱須送足及成功，延遲門檻只套用正式量測。
        var passed = !formal || ratePassed;
        foreach (var operation in Enum.GetValues<Operation>())
        {
            // 完整回應才有延遲；只有 HTTP 合約與持久化都對應的寫入才計入成功寫入。
            var items = phase.Results.Where(result => result.Request.Operation == operation).ToArray();
            var sent = items.Count(item => item.StartMs.HasValue);
            var complete = items.Count(item => item.ElapsedMs.HasValue);
            var ok = items.Count(item => item.Outcome == "ok");
            // 只有實際對帳才有可報告的寫入數；未執行或失敗的對帳無法判定是否提交。
            var isWrite = operation is Operation.Save or Operation.Submit or Operation.Approve;
            int? persisted = 0;
            int? writes = 0;
            if (isWrite)
            {
                persisted = persistence is null ? null : items.Count(item =>
                    item.Request.Row is { } row && persistence.Durable.Contains(row.Id));
                writes = persistence is null ? null : items.Count(item =>
                    item.Request.Row is { } row && persistence.Successful.Contains(row.Id));
            }
            var durableLabel = persisted?.ToString(CultureInfo.InvariantCulture) ?? "unverified";
            var writesLabel = writes?.ToString(CultureInfo.InvariantCulture) ?? "unverified";

            // 從已完整接收的回應計算延遲並分開列出失敗，對帳狀態不影響這些 HTTP 指標。
            var samples = items
                .Where(item => item.ElapsedMs.HasValue)
                .Select(item => item.ElapsedMs.GetValueOrDefault())
                .Order()
                .ToArray();
            var p95 = samples.Length == 0 ? double.PositiveInfinity : samples[(int)Math.Ceiling(samples.Length * 0.95) - 1];
            // 依操作名稱指定正式量測總筆數及 p95 毫秒上限，避免 enum 順序變更套錯門檻。
            var (measuredCount, p95LimitMs) = operation switch
            {
                Operation.List => (4500, 500),
                Operation.Detail => (1800, 500),
                Operation.Statistics => (900, 1000),
                Operation.Save => (900, 1000),
                Operation.Submit => (450, 1000),
                Operation.Approve => (450, 1000),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            };
            var expected = measuredCount * phase.Seconds / MeasuredSeconds;
            var conflicts = items.Count(item => item.Outcome == "conflict");
            var rejected = items.Count(item => item.Outcome is "conflict" or "rejection");
            var serverErrors = items.Count(item => item.Outcome == "server_error");
            var transportErrors = items.Count(item => item.Outcome == "transport_error");
            var contractErrors = items.Count(item => item.Outcome == "contract_error");
            output.WriteLine(FormattableString.Invariant(
                $"phase={name} op={operation} planned={items.Length} sent={sent} completed={complete} ok={ok} durable={durableLabel} successful_writes={writesLabel} rejected={rejected} conflicts={conflicts} server_errors={serverErrors} transport_errors={transportErrors} contract_errors={contractErrors} errors={serverErrors + transportErrors + contractErrors} p95_ms={p95:F2} limit_ms={p95LimitMs}"));
            passed &= items.Length == expected
                && sent == expected
                && complete == expected
                && ok == expected
                && (!isWrite || writes == expected)
                && (!formal || p95 <= p95LimitMs);
        }

        // 抽樣輸出分類及狀態碼，避免回應或連線內容進入公開紀錄。
        foreach (var failure in phase.Results.Where(item => item.Outcome != "ok").Take(8))
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"phase={name} failure op={failure.Request.Operation} outcome={failure.Outcome} http={failure.Status} type={failure.ErrorType}"));
        return passed;
    }

    // 記錄送出前就確定的路徑、身分與資料，避免用回應內容決定下一筆流量。
    private sealed record LoadRequest(Operation Operation, Actor Actor, string Path, SeedRow? Row)
    {
        internal bool IsWrite => Operation is Operation.Save or Operation.Submit or Operation.Approve;
    }

    // 結果保留實際時槽和完整回應時間，便於分辨少送、逾時或合約錯誤。
    private sealed record Observation(LoadRequest Request, double? StartMs, double? ElapsedMs, int Status,
        string Outcome, Receipt? Receipt, string? ErrorType = null);
    private sealed record Phase(int Seconds, Observation[] Results);
    private sealed record Receipt(Guid Id, string Status, string Version, DateTimeOffset UpdatedAt);
}
