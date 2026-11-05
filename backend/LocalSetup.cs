using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Procurement;

// 本機命令先持久保存受保護輸出，再提交帳號，避免輸出失敗後遺失初始密碼。
public static class LocalSetup
{
    public static readonly string[] Names = ["admin", "applicant", "manager", "finance"];

    // 建立本機資料目錄並限制目標目錄的 Unix 權限，以保護本機憑證。呼叫端須提供可信的上層路徑。
    public static void ProtectDirectory(string path)
    {
        // 目標目錄已存在時，拒絕該項目本身為符號連結。
        if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException("本機資料目錄不可為符號連結。");

        // 建立目錄，並在 Unix 將目標目錄設為 0700，僅允許擁有者存取。
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // 準備資料庫啟動需要的本機密碼。只補缺少的檔案，讓重啟沿用既有憑證。
    public static void Prepare(string directory)
    {
        // 先準備目標目錄，缺少密碼檔時才產生隨機密碼。
        ProtectDirectory(directory);
        var path = Path.Combine(directory, "database-password");
        if (!File.Exists(path))
            WriteProtected(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        // 沿用既有檔案時也檢查該項目是否為符號連結及其 Unix 權限。
        CheckProtected(path);
    }

    // 在可信的上層目錄內寫入敏感內容。先同步暫存檔再搬移，以免留下半寫的正式檔案或覆蓋既有憑證。
    private static void WriteProtected(string path, string value)
    {
        // 用新建模式開檔，並要求 Unix 暫存檔以 0600 建立。
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        // 暫存檔放在目標的同一目錄，讓搬移不需跨檔案系統。
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // 先清空文字緩衝，再將檔案串流同步到磁碟。
            using (var stream = new FileStream(temporary, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(value);
                writer.Flush();
                stream.Flush(true);
            }

            // 完整寫入後才讓正式路徑可見，並保留先前已存在的檔案。
            File.Move(temporary, path, false);
        }
        finally
        {
            // 任何失敗只清理這次的暫存檔，不動正式憑證。
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    // 檢查目標檔案項目及其 Unix 權限，讓重跑只沿用符合條件的檔案。上層路徑須可信。
    private static void CheckProtected(string path)
    {
        // 拒絕目標檔案本身為符號連結，並要求 Unix 權限為 0600。
        if (new FileInfo(path).LinkTarget is not null)
            throw new IOException("敏感輸出不可為符號連結。");
        if (!OperatingSystem.IsWindows()
            && File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            throw new IOException("敏感輸出權限必須為 0600。");
    }

    // 依模式補齊管理員或四種示範帳號。共用已保存的憑證，讓初始化可以重跑而不覆寫既有帳號。
    public static async Task Initialize(
        AuthDb db,
        IPasswordHasher<AppUser> hasher,
        string directory,
        bool adminOnly,
        CancellationToken ct = default)
    {
        // 先保護輸出目錄，再開啟涵蓋所有帳號和角色異動的交易。
        ProtectDirectory(directory);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 與組織管理共用交易級 advisory lock，避免並行建立同名帳號或角色。
        await db.LockOrganization(ct);

        // 憑證檔先保存，資料庫失敗後才能沿用同一組密碼重試，不把密碼寫到 console。
        var path = Path.Combine(directory, "initial-accounts.json");
        if (!File.Exists(path))
        {
            var generated = Names.ToDictionary(
                n => n,
                _ => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
            WriteProtected(path, JsonSerializer.Serialize(generated, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }

        // 檢查目標憑證檔本身與 Unix 權限後才讀取。格式錯誤時停止，避免以無效資料建立帳號。
        CheckProtected(path);
        var passwords = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path, ct))
            ?? throw new IOException("初始帳號檔案格式錯誤。");

        // 依模式挑選需要補齊的帳號，只處理資料庫尚未存在的項目。
        foreach (var name in adminOnly ? ["admin"] : Names)
        {
            if (await db.Users.AnyAsync(u => u.NormalizedUserName == name.ToUpperInvariant(), ct))
                continue;

            // 缺少合法初始密碼時停下，避免建立使用者無法登入的帳號。
            if (!passwords.TryGetValue(name, out var password) || !AuthService.ValidPassword(password))
                throw new IOException("初始帳號檔案缺少合法密碼。");

            // 依固定帳號名稱區分管理與業務身分，資料庫只保存密碼雜湊。
            var user = new AppUser
            {
                UserName = name,
                NormalizedUserName = name.ToUpperInvariant(),
                AccountType = name == "admin" ? "admin" : "business",
                SecurityStamp = Guid.NewGuid().ToString(),
                LockoutEnabled = true
            };
            user.PasswordHash = hasher.HashPassword(user, password);
            db.Users.Add(user);

            // 為帳號選擇預設角色並重用既有角色，缺少時才一併新增。
            var roleName = name == "admin" ? "Administrator"
                : name == "manager" ? "Manager"
                : name == "finance" ? "Finance"
                : "Applicant";
            var role = await db.Roles.SingleOrDefaultAsync(r => r.NormalizedName == roleName.ToUpperInvariant(), ct);
            if (role is null)
            {
                role = new IdentityRole(roleName) { NormalizedName = roleName.ToUpperInvariant() };
                db.Roles.Add(role);
            }

            // 同時建立帳號與角色的關聯，避免新增帳號後缺少預期權限。
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
        }

        // 所有帳號與角色處理完成後一次提交，任一步失敗都不留下部分新增。
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
