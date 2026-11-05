using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Procurement;
using Xunit;

// 真正啟動 CLI 子程序，檢查受保護密碼輸出、重試與失敗時的資料庫結果。
public sealed class SetupTests
{
    // 確認未設定或非指定的資料庫會被拒絕，避免測試故障注入落到展示環境。
    [Theory]
    [InlineData(null)]
    [InlineData("Host=db;Database=procurement;Username=procurement")]
    [InlineData("Host=localhost;Database=procurement_test;Username=procurement_test")]
    public void Test_harness_rejects_non_disposable_targets(string? connection)
    {
        Assert.Throws<InvalidOperationException>(() => Harness.VerifyTarget(connection));
    }

    // 啟動真正的 CLI 並收集狀態碼與輸出，讓案例驗證實際命令入口及憑證揭露邊界。
    private static async Task<(int Exit, string Output)> Command(string directory, string command)
    {
        // 執行測試載入的同一組應用程式，並將憑證輸出指向該案例的暫存目錄。
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add(command);
        start.Environment["LocalDirectory"] = directory;
        using var process = Process.Start(start) ?? throw new InvalidOperationException();

        // 同時讀取兩個輸出串流以免子程序阻塞，內容留在記憶體供斷言檢查。
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    // 驗證命令接續及重跑後憑證檔內容相同，並用原始密碼登入確認帳號類別正確。
    [Fact]
    public async Task Cli_preserves_credentials_and_accounts_on_repeated_initialization()
    {
        // 先啟動隔離測試主機，讓 CLI 與 HTTP 驗證使用同一測試資料庫。
        await using var app = new Harness();
        using var client = app.Client();

        // 僅測試資料庫的固定示範帳號屬本測試；其他案例使用不同的隨機帳號。
        await app.WithDb(db => db
            .Users
            .Where(u => LocalSetup.Names.Contains(u.UserName ?? ""))
            .ExecuteDeleteAsync());

        // 使用新目錄保存本次輸出，避免讀到先前測試的憑證。
        var directory = Path.Combine(Path.GetTempPath(), "procurement-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 先建立管理員並保存原始輸出，作為後續重跑的比較基準。
            var first = await Command(directory, "create-admin");
            Assert.Equal(0, first.Exit);
            var file = Path.Combine(directory, "initial-accounts.json");
            var initial = await File.ReadAllTextAsync(file);
            var accounts = JsonSerializer.Deserialize<Dictionary<string, string>>(initial)
                ?? throw new InvalidOperationException();

            // Unix 額外檢查目錄和憑證權限，確認其他使用者不能讀取初始密碼。
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(directory));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            }

            // 接著補齊示範帳號並重跑一次，確認兩次都成功且憑證檔內容與原始內容相同。
            var second = await Command(directory, "initialize-demo");
            var third = await Command(directory, "initialize-demo");
            Assert.Equal(0, second.Exit);
            Assert.Equal(0, third.Exit);
            Assert.True(initial == await File.ReadAllTextAsync(file), "重試初始化必須保留原始憑證檔案。");

            // 檢查輸出沒有密碼，再以原憑證逐一登入並確認帳號類別符合預期。
            foreach (var pair in accounts)
            {
                Assert.False((first.Output + second.Output + third.Output).Contains(pair.Value), "一般輸出不可含密碼。");
                using var login = app.Client();
                await Harness.Token(login);
                Assert.Equal(
                    HttpStatusCode.NoContent,
                    (await login.PostAsJsonAsync(
                        "/api/auth/login",
                        new LoginRequest(pair.Key, pair.Value))).StatusCode);
                var identity = await Harness.Me(login);
                Assert.Equal(pair.Key == "admin" ? "admin" : "business", identity.AccountType);
                Assert.False(identity.MustChangePassword);
            }
        }
        finally
        {
            // 清除本案例專用的暫存目錄，避免測試憑證留在磁碟。
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    // 在憑證輸出處注入故障，檢查命令回報失敗與後續登入回應。
    [Fact]
    public async Task Failed_credential_output_leaves_no_partial_accounts()
    {
        // 啟動隔離主機並清除固定示範帳號，排除上次執行留下的資料。
        await using var app = new Harness();
        using var client = app.Client();
        await app.WithDb(db => db
            .Users
            .Where(u => LocalSetup.Names.Contains(u.UserName ?? ""))
            .ExecuteDeleteAsync());
        var directory = Path.Combine(
            Path.GetTempPath(),
            "procurement-cli-failure-" + Guid.NewGuid().ToString("N"));

        // 以同名目錄阻止憑證檔輸出，故障發生在帳號提交之前。
        Directory.CreateDirectory(Path.Combine(directory, "initial-accounts.json"));
        try
        {
            // 確認命令回報失敗，再以測試密碼逐一嘗試示範帳號，檢查登入皆被拒絕。
            Assert.NotEqual(0, (await Command(directory, "initialize-demo")).Exit);
            foreach (var name in LocalSetup.Names)
            {
                await Harness.Token(client);
                Assert.Equal(
                    HttpStatusCode.Unauthorized,
                    (await client.PostAsJsonAsync(
                        "/api/auth/login",
                        new LoginRequest(name, "unknown-password"))).StatusCode);
            }
        }
        finally
        {
            // 無論斷言是否通過，都移除這次用來阻擋輸出的目錄。
            Directory.Delete(directory, true);
        }
    }
}
