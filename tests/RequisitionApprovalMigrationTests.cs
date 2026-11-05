using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Procurement;
using Xunit;

// 在隔離 PostgreSQL 的臨時 schema 驗證舊草稿能原樣升級。
public sealed class RequisitionApprovalMigrationTests
{
    // 用升級前的 schema 先保存草稿，再比較 EF 升級前後全部主檔與明細。
    [Fact]
    public async Task Upgrade_preserves_preexisting_drafts_and_adds_no_rounds_to_them()
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
            var before = await StoredDraft(db);

            // 套用與正式啟動相同的遷移；舊草稿的欄位、精度、版本和順序都保持原值。
            await db.Database.MigrateAsync();
            Assert.Equal(before, await StoredDraft(db));
            var approvalRows = await db.Database.SqlQueryRaw<long>("""
                SELECT ((SELECT count(*) FROM "RequisitionRounds") +
                    (SELECT count(*) FROM "RequisitionApprovalEvents")) AS "Value"
                """).SingleAsync();
            Assert.Equal(0, approvalRows);
        }
        finally
        {
            // 僅清理本案建立、以隨機名稱識別的測試 schema。
            await using var drop = new NpgsqlCommand($"""DROP SCHEMA "{schema}" CASCADE""", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

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
