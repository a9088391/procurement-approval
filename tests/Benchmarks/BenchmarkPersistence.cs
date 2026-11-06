using System.Text.Json;
using Npgsql;
using static Procurement.Benchmarks.BenchmarkFixture;

namespace Procurement.Benchmarks;

public sealed partial class RequisitionBenchmarkTests
{
    // 草稿保存更換內容與每筆明細值，使僅更新版本或僅更新主檔都會被驗出。
    private static Payload SavedPayload(SeedRow row) => new(row.Subject + " 已保存", "效能測試更新用途", 41.10m,
    [
        new("更新品項五", "盒", 5, 2.74m),
        new("更新品項四", "盒", 4, 2.74m),
        new("更新品項三", "盒", 3, 2.74m),
        new("更新品項二", "盒", 2, 2.74m),
        new("更新品項一", "盒", 1, 2.74m)
    ]);

    // 核對每種合法寫入從既有狀態實際前進的結果。
    private static string ExpectedStatus(SeedRow row, bool changed) => (changed, row.Operation, row.Status) switch
    {
        (true, Operation.Submit, _) => ManagerPending,
        (true, Operation.Approve, ManagerPending) => FinancePending,
        (true, Operation.Approve, FinancePending) => Approved,
        _ => row.Status
    };

    // 獨立讀取全部案件，區分 HTTP 回應、完整提交與計畫以外的異動。
    private static async Task<Persistence> ReadBack(BenchmarkFixture fixture, IEnumerable<Observation> writes)
    {
        // 原資料與觀測回應各自依識別對應，遍歷整庫也能偵測多寫或刪除的案件。
        var expected = fixture.Rows.ToDictionary(row => row.Id);
        var observations = writes.ToDictionary(write => write.Request.Row?.Id ?? throw new InvalidOperationException());
        var durable = new HashSet<Guid>();
        var successful = new HashSet<Guid>();
        var errors = new List<string>();
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(ReadBackSql, connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync();

        // 一次查詢完整資料，讓各項判斷使用同一個已提交的 PostgreSQL 快照。
        while (await reader.ReadAsync())
        {
            var id = reader.GetGuid(0);
            if (!expected.Remove(id, out var row))
            {
                errors.Add($"persistence extra_header id={id:N}");
                continue;
            }
            var stored = JsonSerializer.Deserialize<Stored>(reader.GetString(1), JsonSerializerOptions.Web)
                ?? throw new InvalidOperationException("資料庫核對結果不得為空。");
            observations.TryGetValue(id, out var observation);

            // 完整提交獨立計數；成功寫入還要求 HTTP 回應的版本與更新時間一致。
            var changed = observation is not null && Matches(fixture, row, stored, changed: true);
            if (changed)
            {
                durable.Add(id);
                if (observation is { Outcome: "ok", Receipt: { } receipt }
                    && receipt.Version == stored.Version
                    && receipt.UpdatedAt == stored.UpdatedAt)
                    successful.Add(id);
                else
                    errors.Add($"persistence response_mismatch op={row.Operation} id={id:N}");
            }
            else if (observation?.Receipt is not null || !Matches(fixture, row, stored, changed: false))
                errors.Add($"persistence invalid_state op={row.Operation} id={id:N}");
        }

        // 缺少應存在的主檔也屬不一致，即使該張單並未安排寫入。
        foreach (var row in expected.Values)
            errors.Add($"persistence missing_header op={row.Operation} id={row.Id:N}");
        return new Persistence(durable, successful, errors);
    }

    // 獨立重建預期主檔、五個位置、當輪原始指派與完整歷程，逐筆對照實際結果。
    private static bool Matches(BenchmarkFixture fixture, SeedRow row, Stored stored, bool changed)
    {
        // 版本與更新時間須同步，內容更新只應出現在指定的草稿保存。
        var status = ExpectedStatus(row, changed);
        var payload = changed && row.Operation == Operation.Save
            ? SavedPayload(row) : new Payload(row.Subject, OriginalPurpose, OriginalAmount, OriginalLines);
        var versionMatches = changed
            ? Guid.TryParseExact(stored.Version, "N", out _)
                && stored.Version != row.Version
                && stored.UpdatedAt > row.Date
            : stored.Version == row.Version && stored.UpdatedAt == row.Date;
        if (stored.Number != row.Number
            || stored.Owner != row.Owner.User.Id
            || stored.Status != status
            || stored.CreatedAt != row.Date
            || stored.Subject != payload.Subject
            || stored.Purpose != payload.Purpose
            || stored.EstimatedAmount != payload.EstimatedAmount
            || !versionMatches
            || !stored.Lines.Select(line => line.Position).SequenceEqual(Enumerable.Range(0, payload.Lines.Length))
            || !stored.Lines.Select(line => new Line(line.ItemName, line.Unit, line.Quantity, line.UnitPrice)).SequenceEqual(payload.Lines))
            return false;

        // 草稿從未送審，不能在保存時產生快照或附帶事件。
        if (status == Draft)
            return stored.Rounds.Length == 0 && stored.History.Length == 0;

        // 送審建立第一輪並使用寫入瞬間；既有審核只追加歷程，快照與指派必須保留原值。
        var submittedAt = changed && row.Operation == Operation.Submit ? stored.UpdatedAt : row.Date;
        var managerId = fixture.Manager.User.Id;
        var financeId = fixture.Finance.User.Id;
        if (stored.Rounds is not [var round]
            || round.Number != 1
            || round.DepartmentId != row.Owner.Department.Id
            || round.DepartmentName != row.Owner.Department.Name
            || round.ManagerId != managerId
            || round.FinanceId != financeId
            || round.ManagerReplacementId is not null
            || round.FinanceReplacementId is not null
            || round.SubmittedAt != submittedAt
            || !JsonElement.DeepEquals(round.Content, JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web)))
            return false;

        // 用關卡、正確操作者、提交時間及唯一事件順序核對，保留已完成的決定。
        var history = new List<StoredEvent> { new(1, "submitted", null, row.Owner.User.Id, submittedAt, null, null, null) };
        if (status is FinancePending or Approved)
            history.Add(new StoredEvent(1, "approved", "manager", managerId, row.Status == ManagerPending ? stored.UpdatedAt : row.Date,
                null, null, null));
        if (status == Approved)
            history.Add(new StoredEvent(1, "approved", "finance", financeId, row.Status == FinancePending ? stored.UpdatedAt : row.Date,
                null, null, null));
        return history.SequenceEqual(stored.History);
    }

    // 按位置、輪次及事件識別排序，避免自然列順序掩蓋資料錯置。
    private const string ReadBackSql = """
        SELECT h."Id", jsonb_build_object(
            'number', h."Number", 'owner', h."ApplicantId", 'status', h."Status", 'version', h."Version",
            'createdAt', h."CreatedAt", 'updatedAt', h."UpdatedAt",
            'subject', h."Subject", 'purpose', h."Purpose", 'estimatedAmount', h."EstimatedAmount",
            'lines', COALESCE((
                SELECT jsonb_agg(jsonb_build_object('position', l."Position", 'itemName', l."ItemName",
                    'unit', l."Unit", 'quantity', l."Quantity", 'unitPrice', l."UnitPrice") ORDER BY l."Position")
                FROM "RequisitionLines" l WHERE l."RequisitionId" = h."Id"), '[]'::jsonb),
            'rounds', COALESCE((
                SELECT jsonb_agg(jsonb_build_object('number', r."Number", 'departmentId', r."DepartmentId",
                    'departmentName', r."DepartmentName", 'submittedAt', r."SubmittedAt",
                    'managerId', r."ManagerId", 'financeId', r."FinanceId",
                    'managerReplacementId', r."ManagerReplacementId", 'financeReplacementId', r."FinanceReplacementId",
                    'content', r."Content") ORDER BY r."Number")
                FROM "RequisitionRounds" r WHERE r."RequisitionId" = h."Id"), '[]'::jsonb),
            'history', COALESCE((
                SELECT jsonb_agg(jsonb_build_object('roundNumber', e."RoundNumber", 'kind', e."Kind", 'stage', e."Stage",
                    'actorId', e."ActorId", 'occurredAt', e."OccurredAt", 'previousReviewerId', e."PreviousReviewerId",
                    'newReviewerId', e."NewReviewerId", 'reason', e."Reason") ORDER BY e."Id")
                FROM "RequisitionApprovalEvents" e WHERE e."RequisitionId" = h."Id"), '[]'::jsonb))
        FROM "Requisitions" h;
        """;

    // 從獨立 SQL 保存對帳欄位，保留位置與歷程欄位以偵測非預期異動。
    private sealed record Stored(string Number, string Owner, string Status, string Version,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Subject, string Purpose, decimal EstimatedAmount,
        StoredLine[] Lines, StoredRound[] Rounds, StoredEvent[] History);
    private sealed record StoredLine(int Position, string ItemName, string Unit, int Quantity, decimal UnitPrice);
    private sealed record StoredRound(int Number, string DepartmentId, string DepartmentName, DateTimeOffset SubmittedAt,
        string ManagerId, string FinanceId, string? ManagerReplacementId, string? FinanceReplacementId, JsonElement Content);
    private sealed record StoredEvent(int RoundNumber, string Kind, string? Stage, string ActorId, DateTimeOffset OccurredAt,
        string? PreviousReviewerId, string? NewReviewerId, string? Reason);
    private sealed record Persistence(HashSet<Guid> Durable, HashSet<Guid> Successful, List<string> Errors);
}
