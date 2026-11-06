using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Procurement;
using Xunit;

namespace Procurement.Benchmarks;

// 用獨立空庫預載可重現案件，所有量測請求仍經正式 HTTP、安全與交易入口。
internal sealed class BenchmarkFixture : IDisposable
{
    internal const string Connection = "Host=benchmark-db;Database=procurement_benchmark;Username=procurement_benchmark";
    internal const string Requisitions = "/api/requisitions";
    internal const string Draft = "draft";
    internal const string ManagerPending = "manager_pending";
    internal const string FinancePending = "finance_pending";
    internal const string Approved = "approved";
    internal const int OwnerCount = 4;
    internal const int RowsPerOwner = 2500;
    internal const int CsvCount = 10000;
    internal static readonly DateTimeOffset FixtureDate = new(2024, 6, 18, 2, 30, 0, TimeSpan.Zero);

    internal static readonly Line[] OriginalLines =
    [
        new("測試品項一", "個", 1, 2.37m),
        new("測試品項二", "個", 2, 2.37m),
        new("測試品項三", "個", 3, 2.37m),
        new("測試品項四", "個", 4, 2.37m),
        new("測試品項五", "個", 5, 2.37m)
    ];
    internal const string OriginalPurpose = "效能測試初始用途";
    internal const decimal OriginalAmount = 35.55m;

    internal Actor[] Applicants { get; private set; } = [];
    internal Actor Manager { get; } = new("benchmark_manager", new Department { Name = "效能測試審核部門" }, "Manager");
    internal Actor Finance { get; }
    internal SeedRow[] Rows { get; private set; } = [];
    private readonly List<Actor> actors = [];

    // 兩關共用審核部門，仍保持兩個互斥角色與不同登入。
    private BenchmarkFixture() => Finance = new("benchmark_finance", Manager.Department, "Finance");

    // 依場景建立完整資料與合法登入，準備失敗也會釋放已開啟的 client。
    internal static async Task<BenchmarkFixture> Start(bool csv)
    {
        // 等正常防偽端點可用，表示 Kestrel 已完成 migration 和啟動。
        await WaitForHttp();
        var fixture = new BenchmarkFixture();
        try
        {
            // 負載分開四位所有人，匯出則以同部門兩位所有人驗證不可越權。
            var departments = Enumerable
                .Range(1, csv ? 1 : OwnerCount)
                .Select(index => new Department { Name = $"效能測試部門 {index}" })
                .ToArray();
            fixture.Applicants = Enumerable
                .Range(0, csv ? 2 : OwnerCount)
                .Select(index => new Actor($"benchmark_owner_{index + 1}", departments[csv ? 0 : index], "Applicant"))
                .ToArray();
            fixture.actors.AddRange([fixture.Manager, fixture.Finance, .. fixture.Applicants]);
            fixture.Rows = csv ? fixture.CsvRows() : fixture.LoadRows();

            // 所有種子資料同交易提交，量測前以真實密碼登入，並更新成登入後的防偽權杖。
            await fixture.Seed(departments);
            foreach (var actor in fixture.actors)
                await actor.Login();
            return fixture;
        }
        catch
        {
            // 準備中途失敗也釋放 Cookie 容器及連線。
            fixture.Dispose();
            throw;
        }
    }

    // 只重試啟動期間的匿名 GET，避免重送登入或業務異動。
    private static async Task WaitForHttp()
    {
        using var client = CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!deadline.IsCancellationRequested)
        {
            // 啟動尚未開啟連接埠時，等待下一次就緒探測。
            try
            {
                using var response = await client.GetAsync("/api/auth/csrf", deadline.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (HttpRequestException)
            {
                // 啟動期間可能尚無 HTTP listener。
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
        }
        throw new TimeoutException("效能測試服務未在期限內就緒。");
    }

    // 用各自 Cookie 容器維持真實登入；並行流量由同一身分的 client 共用連線池。
    private static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    })
    {
        BaseAddress = new Uri("http://benchmark-app:8080"),
        Timeout = TimeSpan.FromMinutes(6)
    };

    // 四位申請人各有 2,500 張，預留的寫入案件互不重疊且具備合法起始狀態。
    private SeedRow[] LoadRows()
    {
        // 先分配會異動的既有單，按各操作的序號輪替所有人和審核關卡。
        var rows = new List<SeedRow>(OwnerCount * RowsPerOwner);
        foreach (var (operation, count) in new[] { (Operation.Save, 1080), (Operation.Submit, 540), (Operation.Approve, 540) })
        {
            for (var index = 0; index < count; index++)
            {
                // 相鄰申請人及同一申請人的下一單輪替關卡，兩關總量相同。
                var applicant = index % OwnerCount;
                var status = operation == Operation.Approve
                    ? ((index / OwnerCount + applicant) % 2 == 0 ? ManagerPending : FinancePending)
                    : Draft;
                rows.Add(NewRow(Applicants[applicant], status, $"基準請購 {rows.Count + 1}", FixtureDate, operation));
            }
        }

        // 用完整結案歷程補齊各所有人的資料範圍，明細讀取選這些穩定案件。
        foreach (var applicant in Applicants)
        {
            var count = rows.Count(row => row.Owner == applicant);
            for (; count < RowsPerOwner; count++)
                rows.Add(NewRow(applicant, Approved, $"基準請購 {rows.Count + 1}", FixtureDate));
        }
        return [.. rows];
    }

    // 加入同部門越權單、日期與狀態各一張排除單，讓結果列數相同時仍能辨識錯誤。
    private SeedRow[] CsvRows()
    {
        // 兩列覆蓋分隔符、引號、欄內換行與公式前綴，其餘主旨亦各不相同。
        var own = Enumerable
            .Range(0, CsvCount)
            .Select(index => NewRow(Applicants[0], ManagerPending, index switch
            {
                0 => "測試,\"引號\"\n第二行",
                1 => "=1+1",
                _ => $"匯出請購 {index + 1}"
            }, FixtureDate));
        return [.. own,
            NewRow(Applicants[1], ManagerPending, "同部門不可見", FixtureDate),
            NewRow(Applicants[0], ManagerPending, "起日以前", FixtureDate.AddDays(-1)),
            NewRow(Applicants[0], FinancePending, "已進財務", FixtureDate)];
    }

    // 單號與版本沿用正式格式，讓 fixture 可直接操作原有版本契約。
    private static SeedRow NewRow(Actor owner, string status, string subject, DateTimeOffset date, Operation? operation = null) =>
        new(Guid.NewGuid(), Guid.NewGuid().ToString("N"), owner, status, subject, date, operation);

    // 以原有 Identity 模型與批次 SQL 建立種子資料，空庫檢查和寫入同交易。
    private async Task Seed(Department[] departments)
    {
        // 固定容器網路目標且確認未有資料；交易鎖阻止另一個 runner 同時使用空庫。
        await using var db = new AuthDb(new DbContextOptionsBuilder<AuthDb>().UseNpgsql(Connection).Options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(80101)");
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var tx = (NpgsqlTransaction)transaction.GetDbTransaction();
        await using var empty = new NpgsqlCommand("""
            SELECT NOT (EXISTS (SELECT FROM "AspNetUsers")
                     OR EXISTS (SELECT FROM "AspNetRoles")
                     OR EXISTS (SELECT FROM "Departments")
                     OR EXISTS (SELECT FROM "Requisitions"))
            """, connection, tx);
        Assert.True(await empty.ExecuteScalarAsync() is true, "效能測試需要由專用 Compose 啟動的空白資料庫。");

        // 帳號沿用正式 hasher 及 Identity 角色，先儲存人選再連結預設指派。
        db.Departments.AddRange([.. departments, Manager.Department]);
        foreach (var actor in actors)
            actor.HashPassword();
        db.Users.AddRange(actors.Select(actor => actor.User));
        await db.SaveChangesAsync();
        foreach (var membership in actors.GroupBy(actor => actor.Role))
        {
            // 依初始示範帳號模式各配一個業務角色，申請人共用角色定義。
            var role = new IdentityRole(membership.Key) { NormalizedName = membership.Key.ToUpperInvariant() };
            db.Roles.Add(role);
            db.UserRoles.AddRange(membership.Select(actor => new IdentityUserRole<string> { UserId = actor.User.Id, RoleId = role.Id }));
        }
        foreach (var actor in Applicants)
        {
            // 三人分工由不同帳號擔任，讓實際送審使用既有資格檢查。
            actor.User.ManagerReviewerId = Manager.User.Id;
            actor.User.FinanceReviewerId = Finance.User.Id;
        }
        await db.SaveChangesAsync();

        // 參數化的集合插入避免逐張往返；快照與各狀態對應的原有歷程同步建立。
        await using var insert = new NpgsqlCommand(SeedSql, connection, tx) { CommandTimeout = 60 };
        insert.Parameters.AddWithValue("ids", Rows.Select(row => row.Id).ToArray());
        insert.Parameters.AddWithValue("versions", Rows.Select(row => row.Version).ToArray());
        insert.Parameters.AddWithValue("owners", Rows.Select(row => row.Owner.User.Id).ToArray());
        insert.Parameters.AddWithValue("departments", Rows.Select(row => row.Owner.Department.Id).ToArray());
        insert.Parameters.AddWithValue("names", Rows.Select(row => row.Owner.Department.Name).ToArray());
        insert.Parameters.AddWithValue("statuses", Rows.Select(row => row.Status).ToArray());
        insert.Parameters.AddWithValue("subjects", Rows.Select(row => row.Subject).ToArray());
        insert.Parameters.AddWithValue("dates", Rows.Select(row => row.Date).ToArray());
        insert.Parameters.AddWithValue("purpose", OriginalPurpose);
        insert.Parameters.AddWithValue("amount", OriginalAmount);
        insert.Parameters.AddWithValue("lines", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(OriginalLines, JsonSerializerOptions.Web));
        insert.Parameters.AddWithValue("manager", Manager.User.Id);
        insert.Parameters.AddWithValue("finance", Finance.User.Id);
        await insert.ExecuteNonQueryAsync();
        await transaction.CommitAsync();

        // 批次預載後先建立 PostgreSQL 統計資訊，將準備成本與 HTTP 量測分離。
        await db.Database.ExecuteSqlRawAsync("""
            ANALYZE "Requisitions", "RequisitionLines", "RequisitionRounds", "RequisitionApprovalEvents",
                    "AspNetUsers", "AspNetRoles", "AspNetUserRoles", "Departments";
            """);
    }

    // 使用同一批資料產生主檔、零起算的明細位置、首輪快照與狀態所需的所有既有事件。
    private const string SeedSql = """
        CREATE TEMP TABLE benchmark_seed ON COMMIT DROP AS
        SELECT * FROM unnest(@ids::uuid[], @versions::text[], @owners::text[], @departments::text[],
                            @names::text[], @statuses::text[], @subjects::text[], @dates::timestamptz[])
            AS seed(id, version, owner_id, department_id, department_name, status, subject, instant);

        INSERT INTO "Requisitions" ("Id", "Number", "ApplicantId", "Status", "Subject", "Purpose",
                                    "EstimatedAmount", "Version", "CreatedAt", "UpdatedAt")
        SELECT id, 'PR-' || replace(id::text, '-', ''), owner_id, status, subject, @purpose, @amount, version, instant, instant
        FROM benchmark_seed;

        INSERT INTO "RequisitionLines" ("RequisitionId", "Position", "ItemName", "Unit", "Quantity", "UnitPrice")
        SELECT id, ordinal - 1, line->>'itemName', line->>'unit', (line->>'quantity')::int, (line->>'unitPrice')::numeric
        FROM benchmark_seed CROSS JOIN jsonb_array_elements(@lines) WITH ORDINALITY AS detail(line, ordinal);

        INSERT INTO "RequisitionRounds" ("RequisitionId", "Number", "DepartmentId", "DepartmentName", "SubmittedAt",
                                         "ManagerId", "FinanceId", "Content")
        SELECT id, 1, department_id, department_name, instant, @manager, @finance,
               jsonb_build_object('subject', subject, 'purpose', @purpose, 'estimatedAmount', @amount, 'lines', @lines)
        FROM benchmark_seed WHERE status <> 'draft';

        INSERT INTO "RequisitionApprovalEvents" ("RequisitionId", "RoundNumber", "Kind", "Stage", "ActorId", "OccurredAt")
        SELECT id, 1, 'submitted', NULL, owner_id, instant FROM benchmark_seed WHERE status <> 'draft';

        INSERT INTO "RequisitionApprovalEvents" ("RequisitionId", "RoundNumber", "Kind", "Stage", "ActorId", "OccurredAt")
        SELECT id, 1, 'approved', 'manager', @manager, instant
        FROM benchmark_seed WHERE status IN ('finance_pending', 'approved');

        INSERT INTO "RequisitionApprovalEvents" ("RequisitionId", "RoundNumber", "Kind", "Stage", "ActorId", "OccurredAt")
        SELECT id, 1, 'approved', 'finance', @finance, instant FROM benchmark_seed WHERE status = 'approved';
        """;

    // 每個案例結束釋放連線池，資料庫隨該次專用 Compose 環境銷毀。
    public void Dispose()
    {
        foreach (var actor in actors)
            actor.Client.Dispose();
    }

    // 測試用帳號的隨機密碼只存於記憶體，並交由正式登入驗證。
    internal sealed class Actor(string name, Department department, string role)
    {
        private readonly string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        internal AppUser User { get; } = new()
        {
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            DepartmentId = department.Id,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            LockoutEnabled = true
        };
        internal Department Department { get; } = department;
        internal string Role { get; } = role;
        internal HttpClient Client { get; } = CreateClient();

        // 使用正式 Identity 預設 hasher，並透過 HTTP 確認有效登入與角色。
        internal async Task Login()
        {
            using var response = await Harness.Login(Client, User.UserName ?? "", password);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            await Harness.Token(Client);
            var identity = await Harness.Me(Client);
            Assert.Equal(User.UserName, identity.UserName);
            Assert.Equal("business", identity.AccountType);
            Assert.False(identity.MustChangePassword);
            Assert.Equal(Role, Assert.Single(identity.Roles));
        }

        // 產生正式服務可驗證的雜湊，不把明文寫進資料庫。
        internal void HashPassword() => User.PasswordHash = new PasswordHasher<AppUser>().HashPassword(User, password);
    }

    // 測試資料只保留合約需要的識別、起始狀態及可核對內容。
    internal sealed record SeedRow(Guid Id, string Version, Actor Owner, string Status, string Subject, DateTimeOffset Date, Operation? Operation)
    {
        internal string Number => $"PR-{Id:N}";
    }

    // 用同一個測試 DTO 描述輸入欄位與預期快照，不共用正式資料轉換實作。
    internal sealed record Line(string ItemName, string Unit, int Quantity, decimal UnitPrice);
    internal sealed record Payload(string Subject, string Purpose, decimal EstimatedAmount, Line[] Lines);
}

internal enum Operation { List, Detail, Statistics, Save, Submit, Approve }
