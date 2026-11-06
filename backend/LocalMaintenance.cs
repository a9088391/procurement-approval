using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Procurement;

// 獨立維護命令只開啟必要的資料庫交易，避免啟動網站、套用 migration 或初始化金鑰。
internal static class LocalMaintenance
{
    // 明列所有業務及 Identity 資料表；先取帳號表鎖，沿用登入先存取帳號再存取登入紀錄的順序。
    private static readonly string[] ResetTables =
    [
        "AspNetUsers", "Sessions", "AspNetRoles", "AspNetRoleClaims", "AspNetUserClaims",
        "AspNetUserLogins", "AspNetUserRoles", "AspNetUserTokens", "Departments",
        "Requisitions", "RequisitionLines", "RequisitionRounds", "RequisitionApprovalEvents"
    ];

    // 將維護命令的失敗轉成安全訊息，不輸出可能含有連線或檔案細節的例外。
    internal static async Task<int> Run(string command, int argumentCount, CancellationToken ct = default)
    {
        // 不接受任何額外參數，避免命令列攜帶憑證或被誤解為可選擇維護目標。
        if (argumentCount != 1)
            return Fail("本機維護命令不接受額外參數。");

        // 只讀取一般應用程式組態，不建置或啟動網站及其記錄器。
        try
        {
            var configuration = WebApplication.CreateBuilder().Configuration;
            var directory = configuration["LocalDirectory"] ?? "/local";
            if (command == "reset-demo")
                return await ResetDemo(configuration, directory, ct);

            // 復原與一般網站採用同樣的連線選擇；重設刻意使用上方獨立的固定目標。
            await using var db = OpenContext(LocalSetup.DatabaseConnection(configuration, directory));
            return await RecoverAdmin(db, directory, ct);
        }
        catch (PostgresException error) when (command == "reset-demo" && error.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            // 等鎖失敗時交易已取消，請操作者等其他作業結束後重新確認。
            return Fail("展示重設等待鎖定逾時，請等待其他操作完成，再重新預覽並確認。");
        }
        catch (Exception)
        {
            return Fail("本機維護命令失敗。");
        }
    }

    // 直接使用網站的 EF Core provider，讓 CLI 沿用資料模型並避免啟用網站記錄器。
    private static AuthDb OpenContext(string connection) => new(new DbContextOptionsBuilder<AuthDb>()
        .UseNpgsql(connection)
        .Options);

    // 以原本的組織交易順序復原固定管理員，所有其他帳號和業務資料維持原樣。
    private static async Task<int> RecoverAdmin(AuthDb db, string directory, CancellationToken ct)
    {
        // 先鎖組織，再以登入和改密碼共用的帳號列鎖重新確認管理員身分。
        var auth = new AuthService(db, new PasswordHasher<AppUser>(), TimeProvider.System);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockOrganization(ct);
        var id = await FixedAdminId(db, ct);
        var admin = id is null ? null : await auth.LockUser(id, ct);
        if (admin is not
            {
                UserName: "admin",
                NormalizedUserName: "ADMIN",
                AccountType: "admin",
                Active: true
            }
            || !await auth.RoleNames(admin.Id).ContainsAsync("Administrator", ct))
            return Fail("找不到可復原的管理帳號。");

        // 沿用憑證輪替和登入撤銷；輸出失敗會使這些尚未提交的異動一起回滾。
        var passwords = LocalSetup.GeneratePasswords(["admin"]);
        await auth.ReplaceCredentials(admin, passwords["admin"], false, ct);
        await db.SaveChangesAsync(ct);
        var filename = LocalSetup.WriteFreshAccounts(directory, "admin-recovery", passwords);
        return await CommitWithCredentials(tx, filename, ct);
    }

    // 重設使用入口核對過的 Compose 專案名稱，並從預覽到提交固定同一條資料庫連線。
    private static async Task<int> ResetDemo(IConfiguration configuration, string directory, CancellationToken ct)
    {
        // 專案名稱由本機重設入口傳入，僅用於提示；有連線覆寫或名稱不合法時，拒絕連線。
        var project = configuration["MaintenanceProject"];
        if (configuration.GetConnectionString("Database") is not null)
            return Fail("展示重設不接受資料庫連線覆寫。");
        if (project is null || !Regex.IsMatch(project, @"\A[a-z0-9][a-z0-9_-]*\z", RegexOptions.CultureInvariant))
            return Fail("請從本專案的展示重設入口執行。");

        // 維護程序共享已核對 db 的網路命名空間；不接受主機、帳號、schema 或資料庫參數。
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 5432,
            Database = "procurement",
            Username = "procurement",
            Password = await LocalSetup.ReadDatabasePassword(directory, ct),
            SearchPath = "public",
            Pooling = false,
            Timeout = 5
        };
        await using var db = OpenContext(connection.ConnectionString);
        await db.Database.OpenConnectionAsync(ct);

        // 先檢查實際連線、完整模型表清單與 migration，再讀取初始化管理員的識別碼。
        if (!await IsResetTarget(db, ct) || !await HasExpectedMigrations(db, ct))
            return Fail("展示重設目標的資料庫或結構不符合預期。");
        var hasher = new PasswordHasher<AppUser>();
        var auth = new AuthService(db, hasher, TimeProvider.System);
        var generation = await FixedAdminId(db, ct);
        if (generation is null || !await auth.RoleNames(generation).ContainsAsync("Administrator", ct))
            return Fail("展示重設需要已初始化的管理帳號。");

        // 用相同資料表清單讀取預覽版本、鎖定與清空，讓確認範圍涵蓋每筆將被刪除的資料。
        var relations = ResetTables.Select(name => $"ONLY public.\"{name}\"").ToArray();
        var tables = string.Join(", ", relations);
        var version = await ReadResetVersion(db, relations, ct);

        // 只在讀到完全相同的確認句後開啟寫入交易；取消前不修改資料或本機檔案。
        var target = $"{project}/db/procurement";
        var confirmation = $"reset {target}";
        Console.WriteLine($"展示重設目標：{target}");
        Console.WriteLine("將清除帳號與登入、組織設定、請購單、所有輪次快照及歷程（含改派），再重建示範帳號。");
        Console.WriteLine("未備份的資料無法復原。");
        Console.WriteLine($"請輸入完整確認句：{confirmation}");
        Console.Out.Flush();
        if (await Console.In.ReadLineAsync(ct) != confirmation)
            return Fail("未明確確認，已取消展示重設。");

        // 等鎖有上限。先鎖組織、migration 紀錄與待清資料表，再依讀取當下的已提交資料重新比對。
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", ct);
        await db.LockOrganization(ct);
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE public.\"__EFMigrationsHistory\" IN SHARE MODE", ct);
        var lockTables = $"LOCK TABLE {tables} IN ACCESS EXCLUSIVE MODE";
        await db.Database.ExecuteSqlRawAsync(lockTables, ct);

        // 清空前保留版本與資格檢查，拒絕任何等候確認時已提交的資料異動或另一個重設。
        if (!await HasExpectedMigrations(db, ct)
            || await FixedAdminId(db, ct) != generation
            || !await auth.RoleNames(generation).ContainsAsync("Administrator", ct)
            || version != await ReadResetVersion(db, relations, ct))
            return Fail("確認期間展示資料已變更，已取消展示重設。請重新檢查目標。");

        // SQL 識別字僅來自固定清單。ONLY 和 RESTRICT 避免波及未列出的繼承表或外部參照表。
        var truncate = $"TRUNCATE TABLE {tables} RESTART IDENTITY RESTRICT";
        await db.Database.ExecuteSqlRawAsync(truncate, ct);

        // 清空、重建與提交前的憑證輸出須全部成功；中途故障由同一交易回滾。
        var passwords = LocalSetup.GeneratePasswords(LocalSetup.Names);
        await LocalSetup.SeedAccounts(db, hasher, passwords, LocalSetup.Names, ct);
        var filename = LocalSetup.WriteFreshAccounts(directory, "reset-accounts", passwords);
        return await CommitWithCredentials(tx, filename, ct);
    }

    // 用單一 PostgreSQL 快照串流所有目標資料列的版本，不讀取內容，也不在記憶體保留整份資料。
    private static async Task<string> ReadResetVersion(AuthDb db, string[] relations, CancellationToken ct)
    {
        // 資料表識別字來自固定清單；以表序、寫入交易與實體位置辨識這次預覽看見的資料列版本。
        var queries = relations.Select((relation, index) =>
            $"SELECT {index} AS table_index, xmin::text AS write_id, ctid AS row_location FROM {relation}");
        await using var command = db
            .Database
            .GetDbConnection()
            .CreateCommand();
        command.Transaction = db
            .Database
            .CurrentTransaction
            ?.GetDbTransaction();
        command.CommandText = $"""
            SELECT concat(table_index, ':', write_id, ':', row_location, chr(10))
            FROM ({string.Join(" UNION ALL ", queries)}) AS versions
            ORDER BY table_index, row_location
            """;

        // 依固定順序逐列計算摘要；不將資料列識別或摘要輸出到一般紀錄。
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
            hash.AppendData(Encoding.UTF8.GetBytes(await reader.GetFieldValueAsync<string>(0, ct)));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // 模型新增資料表時須先更新明確清單；也用 PostgreSQL 回傳值驗證固定的實際目標。
    private static async Task<bool> IsResetTarget(AuthDb db, CancellationToken ct)
    {
        // 比對當前模型避免將來新增的資料表在重設時被靜默跳過。
        var modelTables = db
            .Model
            .GetRelationalModel()
            .Tables;
        if (modelTables.Any(t => t.Schema is not (null or "public"))
            || !ResetTables.ToHashSet(StringComparer.Ordinal).SetEquals(modelTables.Select(t => t.Name)))
            return false;

        // 驗證實際連線、schema 與固定映像的資料目錄，避免替代 PGDATA 通過重設預覽。
        return await db
            .Database
            .SqlQueryRaw<bool>("""
                SELECT (current_database() = 'procurement'
                    AND session_user = 'procurement'
                    AND current_user = 'procurement'
                    AND inet_server_addr() = '127.0.0.1'::inet
                    AND inet_server_port() = 5432
                    AND current_setting('data_directory') = '/var/lib/postgresql/18/docker'
                    AND current_setting('search_path') = 'public') AS "Value"
                """)
            .SingleAsync(ct);
    }

    // 要求 migration 紀錄與程式完全相符，拒絕未初始化、舊版及未知新版資料庫。
    private static async Task<bool> HasExpectedMigrations(AuthDb db, CancellationToken ct)
    {
        var applied = await db.Database.GetAppliedMigrationsAsync(ct);
        return db
            .Database
            .GetMigrations()
            .SequenceEqual(applied, StringComparer.Ordinal);
    }

    // 固定名稱與類別代表一代初始化管理員；只取識別碼，不載入憑證。
    private static Task<string?> FixedAdminId(AuthDb db, CancellationToken ct) => db
        .Users
        .Where(u => u.UserName == "admin"
            && u.NormalizedUserName == "ADMIN"
            && u.AccountType == "admin"
            && u.Active)
        .Select(u => (string?)u.Id)
        .SingleOrDefaultAsync(ct);

    // 在提交前讓操作者能找到受保護候選檔；提交結果不明時保留檔案供登入查證。
    private static async Task<int> CommitWithCredentials(IDbContextTransaction tx, string filename, CancellationToken ct)
    {
        // 檔案已寫入，先提示它仍未確認可用，避免程序中斷時找不到它。
        Console.Error.WriteLine($"待提交的憑證檔：{filename}。命令未明確成功時，請先以登入確認。");
        try
        {
            await tx.CommitAsync(ct);
        }
        catch (Exception)
        {
            return Fail($"無法確認交易是否已提交。請保留 {filename}，並先以登入確認。");
        }

        // 已確認提交才以成功狀態回報，且只顯示檔名。
        Console.WriteLine(filename);
        return 0;
    }

    // 拒絕或失敗共用非零狀態，訊息只包含已知且安全的操作資訊。
    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
