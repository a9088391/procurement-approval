using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Procurement;
using Xunit;

// 在隔離 PostgreSQL schema 驗證舊草稿、簽核快照和歷程可原樣升級。
public sealed class RequisitionApprovalMigrationTests
{
    private const string ApprovalMigration = "20261105134024_RequisitionApproval";
    private static readonly Guid Pending = Guid.Parse("de161f04-318f-40fa-9840-cdd0c1737c27");
    private static readonly Guid Decided = Guid.Parse("de161f04-318f-40fa-9840-cdd0c1737c28");

    // 從 Ticket 03 保留草稿，從 Ticket 04 保留已送審資料及舊約束，再驗證撤回的新約束。
    [Fact]
    public async Task Upgrade_preserves_preexisting_drafts_and_approvals_and_enforces_event_uniqueness()
    {
        // 測試資料只能放在隔離測試庫自建的 schema，與其他案例和展示資料分開。
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Database");
        Harness.VerifyTarget(connectionString);
        var schema = $"test_approval_upgrade_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"""CREATE SCHEMA "{schema}" """, connection))
            await create.ExecuteNonQueryAsync();

        try
        {
            // 以專案既有 migration 建出 Ticket 03 版本，在真正的舊表插入部分未填明細。
            var isolated = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            await using var db = new AuthDb(new DbContextOptionsBuilder<AuthDb>().UseNpgsql(isolated.ConnectionString).Options);
            await db.GetService<IMigrator>().MigrateAsync("20261105112607_RequisitionDrafts");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "AspNetUsers" ("Id", "AccountType", "Active", "UserName", "EmailConfirmed",
                    "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
                VALUES ('legacy-owner', 'business', true, '既有申請人', false, false, false, false, 0);
                INSERT INTO "Requisitions" ("Id", "Number", "ApplicantId", "Status", "Subject", "Purpose",
                    "EstimatedAmount", "Version", "CreatedAt", "UpdatedAt")
                VALUES ('de161f04-318f-40fa-9840-cdd0c1737c26', 'PR-de161f04318f40fa9840cdd0c1737c26', 'legacy-owner',
                    'draft', '既有草稿', E'舊用途\n未填完', 0.87, '948e74570efe4f37be7a61779618176a',
                    '2026-11-04T01:02:03.123456Z', '2026-11-04T04:05:06.789012Z');
                INSERT INTO "RequisitionLines" ("RequisitionId", "Position", "ItemName", "Unit", "Quantity", "UnitPrice")
                VALUES ('de161f04-318f-40fa-9840-cdd0c1737c26', 0, '標籤', '張', 3, 0.29),
                    ('de161f04-318f-40fa-9840-cdd0c1737c26', 1, '待填', null, null, null);
                """);
            var draftBefore = await StoredDraft(db);

            // 以 Ticket 04 的原 schema 保存已送審快照和既有主管決定。
            await db.GetService<IMigrator>().MigrateAsync(ApprovalMigration);
            await SeedApprovals(db);
            var pendingBefore = await StoredApproval(db, Pending);
            var decidedBefore = await StoredApproval(db, Decided);

            // 套用正式遷移，確認原草稿仍沒有任何輪次；舊主檔、快照與事件保持原值。
            await db.Database.MigrateAsync();
            Assert.Equal(draftBefore, await StoredDraft(db));
            Assert.Equal(pendingBefore, await StoredApproval(db, Pending));
            Assert.Equal(decidedBefore, await StoredApproval(db, Decided));
            var rounds = await db
                .Database
                .SqlQueryRaw<long>("""SELECT count(*) AS "Value" FROM "RequisitionRounds" """)
                .SingleAsync();
            Assert.Equal(2, rounds);
            Assert.Equal(3, await StoredEventCount(db));

            // 同輪送審及撤回可以共存，各只接受一次；核准與駁回仍互斥。
            Assert.Equal(1, await InsertEvent(db, Pending, "withdrawn", null));
            await AssertEventRejected(db, Pending, "withdrawn", null, PostgresErrorCodes.UniqueViolation);
            await AssertEventRejected(db, Pending, "submitted", null, PostgresErrorCodes.UniqueViolation);
            await AssertEventRejected(db, Decided, "rejected", "manager", PostgresErrorCodes.UniqueViolation);
            await AssertEventRejected(db, Decided, "approved", null, PostgresErrorCodes.CheckViolation);
            await AssertEventRejected(db, Decided, "withdrawn", "finance", PostgresErrorCodes.CheckViolation);
            Assert.Equal(4, await StoredEventCount(db));

            // 舊版無法表示撤回歷程；在隔離 schema 確認降版會失敗且完整保留資料。
            var pendingWithWithdrawal = await StoredApproval(db, Pending);
            var downgrade = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(ApprovalMigration));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, downgrade.SqlState);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(pendingWithWithdrawal, await StoredApproval(db, Pending));
            Assert.Equal(decidedBefore, await StoredApproval(db, Decided));
            await AssertEventRejected(db, Pending, "withdrawn", null, PostgresErrorCodes.UniqueViolation);
        }
        finally
        {
            // 僅清理本案建立、以隨機名稱識別的測試 schema。
            await using var drop = new NpgsqlCommand($"""DROP SCHEMA "{schema}" CASCADE""", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    // 用舊 schema 建立一張主管待審及一張主管已核准的請購，涵蓋兩種舊事件。
    private static Task SeedApprovals(AuthDb db) => db.Database.ExecuteSqlRawAsync("""
        INSERT INTO "Departments" ("Id", "Name", "Active", "Version")
        VALUES ('legacy-department', '既有部門', true, 'dept-version');
        INSERT INTO "AspNetUsers" ("Id", "AccountType", "Active", "UserName", "EmailConfirmed",
            "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
        VALUES ('legacy-manager', 'business', true, '舊主管', false, false, false, false, 0),
            ('legacy-finance', 'business', true, '舊財務', false, false, false, false, 0);
        INSERT INTO "Requisitions" ("Id", "Number", "ApplicantId", "Status", "Subject", "Purpose",
            "EstimatedAmount", "Version", "CreatedAt", "UpdatedAt")
        VALUES ('de161f04-318f-40fa-9840-cdd0c1737c27', 'PR-de161f04318f40fa9840cdd0c1737c27', 'legacy-owner',
            'manager_pending', '既有待審', '既有用途', 0.87, 'pending-version', '2026-11-04T01:02:03Z', '2026-11-04T04:05:06Z'),
            ('de161f04-318f-40fa-9840-cdd0c1737c28', 'PR-de161f04318f40fa9840cdd0c1737c28', 'legacy-owner',
            'finance_pending', '既有已決定', '既有用途', 0.87, 'decided-version', '2026-11-04T01:02:03Z', '2026-11-04T05:06:07Z');
        INSERT INTO "RequisitionRounds" ("RequisitionId", "Number", "DepartmentId", "DepartmentName", "ManagerId", "FinanceId",
            "SubmittedAt", "Content")
        SELECT "Id", 1, 'legacy-department', '當時部門名', 'legacy-manager', 'legacy-finance', '2026-11-04T04:05:06Z',
            jsonb_build_object('subject', "Subject", 'purpose', "Purpose", 'estimatedAmount', "EstimatedAmount", 'lines',
                jsonb_build_array(jsonb_build_object('itemName', '標籤', 'unit', '張', 'quantity', 3, 'unitPrice', 0.29)))
        FROM "Requisitions" WHERE "Status" IN ('manager_pending', 'finance_pending');
        INSERT INTO "RequisitionApprovalEvents" ("RequisitionId", "RoundNumber", "Kind", "Stage", "ActorId", "OccurredAt")
        VALUES ('de161f04-318f-40fa-9840-cdd0c1737c27', 1, 'submitted', null, 'legacy-owner', '2026-11-04T04:05:06Z'),
            ('de161f04-318f-40fa-9840-cdd0c1737c28', 1, 'submitted', null, 'legacy-owner', '2026-11-04T04:05:06Z'),
            ('de161f04-318f-40fa-9840-cdd0c1737c28', 1, 'approved', 'manager', 'legacy-manager', '2026-11-04T05:06:07Z');
        """);

    // 同一輪直接探測資料庫約束，不以 HTTP 先攔截作為 schema 仍有效的證據。
    private static Task<int> InsertEvent(AuthDb db, Guid id, string kind, string? stage) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "RequisitionApprovalEvents" ("RequisitionId", "RoundNumber", "Kind", "Stage", "ActorId", "OccurredAt")
            VALUES ({id}, 1, {kind}, {stage}, 'legacy-owner', '2026-11-04T06:07:08Z')
            """);

    // 核對 PostgreSQL 拒絕原因，區分不合法的事件形狀及重複的合法形狀。
    private static async Task AssertEventRejected(AuthDb db, Guid id, string kind, string? stage, string sqlState)
    {
        var failure = await Assert.ThrowsAsync<PostgresException>(() => InsertEvent(db, id, kind, stage));
        Assert.Equal(sqlState, failure.SqlState);
    }

    // 用遷移後的實際資料表計數，確認失敗的寫入沒有增加事件。
    private static Task<long> StoredEventCount(AuthDb db) => db
        .Database
        .SqlQueryRaw<long>("""SELECT count(*) AS "Value" FROM "RequisitionApprovalEvents" """)
        .SingleAsync();

    // 比較整筆主檔、快照和有順序的事件，涵蓋原版本、部門、原指派及時間。
    private static Task<string> StoredApproval(AuthDb db, Guid id) => db
        .Database
        .SqlQuery<string>($"""
            SELECT jsonb_build_object('header', to_jsonb(d), 'round', to_jsonb(r), 'history',
                (SELECT jsonb_agg(to_jsonb(e) ORDER BY e."Id")
                 FROM "RequisitionApprovalEvents" e WHERE e."RequisitionId" = d."Id"))::text AS "Value"
            FROM "Requisitions" d JOIN "RequisitionRounds" r ON r."RequisitionId" = d."Id"
            WHERE d."Id" = {id}
            """)
        .SingleAsync();

    // 以 PostgreSQL JSONB 序列化欄位值及明細順序，避免漏比任何既有資料欄位。
    private static Task<string> StoredDraft(AuthDb db) => db
        .Database
        .SqlQueryRaw<string>("""
            SELECT jsonb_build_object('draft', to_jsonb(d), 'lines',
                (SELECT jsonb_agg(to_jsonb(l) ORDER BY l."Position")
                 FROM "RequisitionLines" l WHERE l."RequisitionId" = d."Id"))::text AS "Value"
            FROM "Requisitions" d WHERE d."Id" = 'de161f04-318f-40fa-9840-cdd0c1737c26'
            """)
        .SingleAsync();
}
