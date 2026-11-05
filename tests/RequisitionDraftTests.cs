using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
using static OrganizationTests;

// 透過正式 HTTP 與隔離 PostgreSQL 驗證本人草稿的保存結果。
public sealed class RequisitionDraftTests
{
    internal const string DraftsPath = "/api/requisitions/drafts";
    private const string ForgedId = "\"ea356fea-7dc4-4083-9ccd-c679eb577eca\"";
    private const string ForgedTime = "\"2026-11-04T12:00:00Z\"";

    // 草稿允許未填完整，重開時保留明細順序、讀取版本及已知的精確小計。
    [Fact]
    public async Task Incomplete_draft_reopens_with_the_exact_calculable_amount()
    {
        // 以沒有部門或審核人設定的業務帳號登入，確認草稿不需要先符合送審資格。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();

        // 第一列只有數量，第二列有足夠資料計算新臺幣 0.87 元。
        using var created = await client.PostAsJsonAsync(DraftsPath, new
        {
            subject = "影印室補給",
            lines = new[]
            {
                new { itemName = "影印紙", quantity = 2, unitPrice = (decimal?)null },
                new { itemName = "標籤", quantity = 3, unitPrice = (decimal?)0.29m }
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = saved.GetProperty("id").GetGuid();
        var version = saved.GetProperty("version").GetString();

        // 發出另一個請求查回持久化內容，確認回應不把已知小計視為完整。
        using var opened = await client.GetAsync($"{DraftsPath}/{id}");
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var draft = await opened.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(version));
        Assert.Equal(version, draft.GetProperty("version").GetString());
        Assert.Equal("影印室補給", draft.GetProperty("subject").GetString());
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("purpose").ValueKind);
        Assert.False(draft.GetProperty("isComplete").GetBoolean());
        Assert.Equal(0.87m, draft.GetProperty("estimatedAmount").GetDecimal());
        var lines = draft.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal("影印紙", lines[0].GetProperty("itemName").GetString());
        Assert.Equal(2, lines[0].GetProperty("quantity").GetInt32());
        Assert.Equal(JsonValueKind.Null, lines[0].GetProperty("estimatedAmount").ValueKind);
        Assert.Equal("標籤", lines[1].GetProperty("itemName").GetString());
        Assert.Equal(0.87m, lines[1].GetProperty("estimatedAmount").GetDecimal());
    }

    // 保存應保留權威欄位、更新版本與時間，並依相同順序讀回主檔及明細。
    [Fact]
    public async Task Save_reopens_complete_content_and_moves_the_updated_draft_to_the_top()
    {
        // 建立兩筆同時刻草稿，固定驗證時間相同時的識別排序。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();
        var original = await CreateDraft(client, new { });
        var other = await CreateDraft(client, new { subject = "稍後處理" });
        var originalId = original.GetProperty("id").GetGuid();
        var otherId = other.GetProperty("id").GetGuid();
        var before = await client.GetFromJsonAsync<JsonElement>(DraftsPath);
        Assert.Equal(new[] { originalId, otherId }.Order(), before.EnumerateArray().Select(row => row.GetProperty("id").GetGuid()));
        Assert.False(original.GetProperty("isComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, original.GetProperty("estimatedAmount").ValueKind);

        // 經過可觀察的時鐘變化再保存完整內容，品項順序刻意不同於名稱排序。
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        using var response = await client.PostAsJsonAsync(PathOf(original), new
        {
            subject = " 影印室補給 ",
            purpose = "第一段\n第二段",
            lines = new[]
            {
                new { itemName = "標籤", unit = "包", quantity = 3, unitPrice = 0.29m },
                new { itemName = "影印紙", unit = "疊", quantity = 2, unitPrice = 0.31m }
            },
            version = VersionOf(original)
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<JsonElement>();

        // 從新請求驗證資料、精確金額與完整性，並核對系統欄位及清單投影。
        var opened = await ReadDraft(client, original);
        Assert.True(JsonElement.DeepEquals(saved, opened));
        Assert.Equal(originalId, opened.GetProperty("id").GetGuid());
        Assert.Equal($"PR-{originalId:N}", opened.GetProperty("number").GetString());
        Assert.Equal(app.UserId, opened.GetProperty("applicantId").GetString());
        Assert.Equal("draft", opened.GetProperty("status").GetString());
        Assert.Equal(original.GetProperty("createdAt").GetDateTimeOffset(), opened.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(app.Clock.GetUtcNow(), opened.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.NotEqual(VersionOf(original), VersionOf(opened));
        Assert.Equal(" 影印室補給 ", opened.GetProperty("subject").GetString());
        Assert.Equal("第一段\n第二段", opened.GetProperty("purpose").GetString());
        Assert.True(opened.GetProperty("isComplete").GetBoolean());
        Assert.Equal(1.49m, opened.GetProperty("estimatedAmount").GetDecimal());
        var lines = opened.GetProperty("lines");
        Assert.Equal(new[] { "標籤", "影印紙" }, lines.EnumerateArray().Select(line => line.GetProperty("itemName").GetString()));
        Assert.Equal("包", lines[0].GetProperty("unit").GetString());
        Assert.Equal(3, lines[0].GetProperty("quantity").GetInt32());
        Assert.Equal(0.29m, lines[0].GetProperty("unitPrice").GetDecimal());
        Assert.Equal(0.87m, lines[0].GetProperty("estimatedAmount").GetDecimal());
        Assert.Equal(0.62m, lines[1].GetProperty("estimatedAmount").GetDecimal());
        var list = await client.GetFromJsonAsync<JsonElement>(DraftsPath);
        Assert.Equal(new[] { originalId, otherId }, list.EnumerateArray().Select(row => row.GetProperty("id").GetGuid()));
        Assert.Equal(new[] { "id", "number", "subject", "updatedAt" }, list[0].EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(opened.GetProperty("subject").GetString(), list[0].GetProperty("subject").GetString());
    }

    // 明細數量只影響內容完整性，超過送審門檻仍能保存和重開草稿。
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public async Task Drafts_keep_all_lines_even_when_the_count_is_incomplete(int count, bool complete)
    {
        // 準備除了筆數外都完整的內容，讓預期結果只取決於 1–100 的門檻。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();
        var created = await CreateDraft(client, new
        {
            subject = "辦公耗材",
            purpose = "補貨",
            lines = Enumerable.Repeat(new { itemName = "標籤", unit = "張", quantity = 1, unitPrice = 0.01m }, count).ToArray()
        });

        // 確認重開保留全部明細及正確的完整性，未計算時保留 null 金額。
        var opened = await ReadDraft(client, created);
        Assert.True(JsonElement.DeepEquals(created, opened));
        Assert.Equal(count, opened.GetProperty("lines").GetArrayLength());
        Assert.Equal(complete, opened.GetProperty("isComplete").GetBoolean());
        if (count == 0)
            Assert.Equal(JsonValueKind.Null, opened.GetProperty("estimatedAmount").ValueKind);
        else
            Assert.Equal(count * 0.01m, opened.GetProperty("estimatedAmount").GetDecimal());
    }

    // 明確 null 允許清空草稿，原有明細及金額也應一併移除。
    [Fact]
    public async Task Explicit_null_fields_clear_a_draft_and_are_accepted_on_create()
    {
        // 建立兩種輸入，分別驗證建立允許 null 與保存可清除原本已填的資料。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();
        var empty = await CreateDraft(client, new { subject = (string?)null, purpose = (string?)null, lines = (object?)null });
        var original = await CreateDraft(client, new { subject = "既有主旨", purpose = "既有用途", lines = new[] { new { quantity = 2, unitPrice = 1 } } });
        using var response = await PostRaw(client, PathOf(original),
            $$"""{"subject":null,"purpose":null,"lines":null,"version":{{original.GetProperty("version").GetRawText()}}}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 不從保存回應推論刪除了舊資料，改以新請求驗證兩筆草稿的持久狀態。
        var cleared = await ReadDraft(client, original);
        foreach (var draft in new[] { empty, cleared })
        {
            Assert.Equal(JsonValueKind.Null, draft.GetProperty("subject").ValueKind);
            Assert.Equal(JsonValueKind.Null, draft.GetProperty("purpose").ValueKind);
            Assert.Equal(JsonValueKind.Null, draft.GetProperty("estimatedAmount").ValueKind);
            Assert.Equal(0, draft.GetProperty("lines").GetArrayLength());
            Assert.False(draft.GetProperty("isComplete").GetBoolean());
        }
        Assert.NotEqual(VersionOf(original), VersionOf(cleared));
    }

    // 數量、單價與整單金額的端點值可精確保存，超過部分合計的請求不能更動原單。
    [Fact]
    public async Task Exact_amount_limits_are_inclusive_and_partial_amount_overflow_is_rejected()
    {
        // 同一張草稿包含單價上下界及數量下界，再重開確認精確小數。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();
        var prices = await CreateDraft(client, new
        {
            lines = new[] { new { quantity = 1, unitPrice = 1_000_000m }, new { quantity = 1, unitPrice = 0.01m } }
        });
        Assert.Equal(1_000_000.01m, (await ReadDraft(client, prices)).GetProperty("estimatedAmount").GetDecimal());

        // 數量上限乘固定單價正好到總額上限，未填其他欄位仍可保存該小計。
        var limit = await CreateDraft(client, new { lines = new[] { new { quantity = 10_000, unitPrice = 1000 } } });
        Assert.Equal(10_000_000m, (await ReadDraft(client, limit)).GetProperty("estimatedAmount").GetDecimal());
        const string lines = """[{"quantity":10000,"unitPrice":1000},{"itemName":"未填單價"},{"quantity":1,"unitPrice":0.01}]""";

        // 多出一分即拒絕，即使另一列不能計算，並讀回原本版本與明細。
        await Expect(await PostRaw(client, DraftsPath, $$"""{"lines":{{lines}}}"""), HttpStatusCode.BadRequest, "invalid_input");
        await RejectSave(client, limit, lines);
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>(DraftsPath)).GetArrayLength());
    }

    // 原始數字 token 保留輸入型別及全部精度，避免測試工具預先轉型或四捨五入。
    [Theory]
    [InlineData("quantity", "0")]
    [InlineData("quantity", "-1")]
    [InlineData("quantity", "10001")]
    [InlineData("quantity", "1.5")]
    [InlineData("quantity", "1.0")]
    [InlineData("quantity", "1e1")]
    [InlineData("quantity", "\"1\"")]
    [InlineData("unitPrice", "0")]
    [InlineData("unitPrice", "-0.01")]
    [InlineData("unitPrice", "1000000.01")]
    [InlineData("unitPrice", "0.123")]
    [InlineData("unitPrice", "1.230")]
    [InlineData("unitPrice", "1.23000000000000000000000000001")]
    [InlineData("unitPrice", "\"0.29\"")]
    [InlineData("unitPrice", "1e-2")]
    public async Task Invalid_numeric_tokens_do_not_create_or_overwrite_drafts(string field, string token)
    {
        // 準備一張可讀回的草稿，並核對送出的 JSON 中確實保留原 token。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();
        var original = await CreateDraft(client, new { subject = "保留", lines = new[] { new { quantity = 3, unitPrice = 0.29m } } });
        var lines = $$"""[{"{{field}}":{{token}}}]""";
        using var payload = JsonDocument.Parse(lines);
        Assert.Equal(token, payload.RootElement[0].GetProperty(field).GetRawText());

        // 建立與更新皆拒絕同一原始輸入，清單及原始草稿內容都保持原狀。
        await Expect(await PostRaw(client, DraftsPath, $$"""{"lines":{{lines}}}"""), HttpStatusCode.BadRequest, "invalid_input");
        await RejectSave(client, original, lines);
        Assert.Single((await client.GetFromJsonAsync<JsonElement>(DraftsPath)).EnumerateArray());
    }

    // 同一份輸入不能攜帶系統權威欄位，也不能藉明細中的未知欄位繞過限制。
    [Theory]
    [InlineData("id", ForgedId, false)]
    [InlineData("applicantId", ForgedId, false)]
    [InlineData("departmentId", ForgedId, false)]
    [InlineData("number", "\"PR-ea356fea7dc440839ccdc679eb577eca\"", false)]
    [InlineData("status", "\"approved\"", false)]
    [InlineData("submittedAt", ForgedTime, false)]
    [InlineData("createdAt", ForgedTime, false)]
    [InlineData("updatedAt", ForgedTime, false)]
    [InlineData("estimatedAmount", "1.23", false)]
    [InlineData("currency", "\"USD\"", false)]
    [InlineData("estimatedAmount", "1.23", true)]
    [InlineData("currency", "\"USD\"", true)]
    public async Task Forged_authority_and_amount_members_are_rejected_at_both_endpoints(string field, string token, bool nested)
    {
        // 以有正確所有權和版本的草稿驗證欄位邊界，避免先被其他錯誤擋下。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();
        var original = await CreateDraft(client, new { subject = "系統欄位不可改" });
        var unknown = $"\"{field}\":{token}";
        var content = nested ? $"\"lines\":[{{{unknown}}}]" : $"\"lines\":[],{unknown}";

        // 分別測建立和完整更新，再從 HTTP 比對原資料且確認沒有新增草稿。
        await Expect(await PostRaw(client, DraftsPath, $"{{{content}}}"), HttpStatusCode.BadRequest, "invalid_input");
        await Expect(await PostRaw(client, PathOf(original),
            $$"""{"subject":"不應保存","purpose":null,{{content}},"version":{{original.GetProperty("version").GetRawText()}}}"""),
            HttpStatusCode.BadRequest, "invalid_input");
        Assert.True(JsonElement.DeepEquals(original, await ReadDraft(client, original)));
        Assert.Single((await client.GetFromJsonAsync<JsonElement>(DraftsPath)).EnumerateArray());
    }

    // 驗證更新缺少任一明確欄位、內容無法解析或明細含 null 時，都不會清除舊內容。
    [Fact]
    public async Task Malformed_requests_null_rows_and_omitted_update_keys_are_rejected()
    {
        // 使用已填入主旨、用途及明細的草稿，讓欄位被靜默清除時可從 HTTP 發現。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();
        var original = await CreateDraft(client, new { subject = "保留", purpose = "補貨", lines = new[] { new { quantity = 1, unitPrice = 2 } } });
        var version = original.GetProperty("version").GetRawText();

        // 三個可編輯欄位分別省略，其餘鍵及版本保持有效，另測缺少版本和壞掉的 JSON。
        var invalid = new[]
        {
            $$"""{"purpose":null,"lines":null,"version":{{version}}}""",
            $$"""{"subject":null,"lines":null,"version":{{version}}}""",
            $$"""{"subject":null,"purpose":null,"version":{{version}}}""",
            """{"subject":null,"purpose":null,"lines":null}""",
            """{"subject":"截斷"""
        };
        foreach (var body in invalid)
            await Expect(await PostRaw(client, PathOf(original), body), HttpStatusCode.BadRequest, "invalid_input");

        // null 可代表欄位未填，但不可用來代表明細本身。
        await Expect(await PostRaw(client, DraftsPath, """{"lines":[null]}"""), HttpStatusCode.BadRequest, "invalid_input");
        await RejectSave(client, original, "[null]");
        Assert.Single((await client.GetFromJsonAsync<JsonElement>(DraftsPath)).EnumerateArray());
    }

    // 文字長度以 UTF-16 計，單行不接受換行，所有文字欄位均拒絕 NUL。
    [Theory]
    [InlineData("subject", 200, true)]
    [InlineData("purpose", 4000, false)]
    [InlineData("itemName", 200, true)]
    [InlineData("unit", 40, true)]
    public async Task Text_limits_preserve_legal_input_and_reject_unrepresentable_values(string field, int limit, bool singleLine)
    {
        // 用佔兩個 UTF-16 單位的字元到達上限，驗證建立及重開都保留完整字串。
        await using var app = new Harness();
        await app.Seed();
        using var client = await app.SignedIn();
        var maximum = string.Concat(Enumerable.Repeat("😀", limit / 2));
        Assert.Equal(limit, maximum.Length);
        var original = await CreateDraft(client, TextPayload(field, maximum));
        Assert.Equal(maximum, TextField(await ReadDraft(client, original), field).GetString());

        // 確認送出的原始 JSON 解碼成指定的 NUL、CR 或 LF，而非含反斜線的文字。
        var invalidValues = singleLine ? new[] { maximum + "字", "甲\0乙", "甲\r乙", "甲\n乙" } : [maximum + "字", "甲\0乙"];
        foreach (var invalid in invalidValues)
        {
            var payload = TextPayload(field, invalid);
            var create = JsonSerializer.SerializeToElement(payload);
            Assert.Equal(invalid, TextField(create, field).GetString());
            await Expect(await PostRaw(client, DraftsPath, create.GetRawText()), HttpStatusCode.BadRequest, "invalid_input");
            payload["version"] = VersionOf(original);
            await Expect(await PostRaw(client, PathOf(original), JsonSerializer.Serialize(payload)), HttpStatusCode.BadRequest, "invalid_input");
        }

        // 無效輸入既不新增草稿，也不覆寫長度合法的既有文字及版本。
        Assert.True(JsonElement.DeepEquals(original, await ReadDraft(client, original)));
        Assert.Single((await client.GetFromJsonAsync<JsonElement>(DraftsPath)).EnumerateArray());
    }

    // 查詢及異動按所有人隔離，即使擁有主管或財務角色也不能辨識他人的草稿。
    [Theory]
    [InlineData("Manager")]
    [InlineData("Finance")]
    public async Task Other_business_roles_can_only_access_their_own_drafts(string role)
    {
        // 每個帳號都走正式入口建立資料，驗證另一個角色仍可正常操作自己的草稿。
        await using var ownerApp = new Harness();
        await ownerApp.Seed();
        using var owner = await ownerApp.SignedIn();
        var original = await CreateDraft(owner, new { subject = "本人草稿" });
        await using var otherApp = new Harness();
        await otherApp.Seed(roleName: role);
        using var other = await otherApp.SignedIn();
        Assert.Contains(role, (await Harness.Me(other)).Roles);
        var own = await CreateDraft(other, new { subject = "另一帳號的草稿" });
        var list = await other.GetFromJsonAsync<JsonElement>(DraftsPath);
        Assert.Equal(own.GetProperty("id").GetGuid(), Assert.Single(list.EnumerateArray()).GetProperty("id").GetGuid());

        // 比對不存在與他人的請求回應，並透過擁有者確認所有異動都被拒絕。
        var existing = PathOf(original);
        var missing = $"{DraftsPath}/{Guid.NewGuid()}";
        using var inaccessible = await other.GetAsync(existing);
        using var absent = await other.GetAsync(missing);
        Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Equal(await absent.Content.ReadAsStringAsync(), await inaccessible.Content.ReadAsStringAsync());
        var version = VersionOf(original);
        await Expect(await other.PostAsJsonAsync(existing, new { subject = "越權", purpose = "測試", lines = Array.Empty<object>(), version }),
            HttpStatusCode.NotFound, "not_found");
        await Expect(await other.PostAsJsonAsync($"{existing}/delete", new { version }), HttpStatusCode.NotFound, "not_found");
        Assert.True(JsonElement.DeepEquals(original, await ReadDraft(owner, original)));
    }

    // 管理與強制改密碼的身分即使帶有有效 Cookie 和 CSRF，也不能讀寫草稿。
    [Fact]
    public async Task Admin_and_password_change_required_sessions_cannot_read_or_write_drafts()
    {
        // 使用正式管理 API 建立須改密碼的業務帳號，另建可確認的現有草稿。
        await using var adminApp = new Harness();
        await adminApp.Seed("admin", "Administrator");
        using var admin = await adminApp.SignedIn();
        var department = await CreateDepartment(admin);
        var temporary = TemporaryPassword();
        var account = await CreateAccount(admin, department.Id, temporary);
        using var pending = await adminApp.SignedIn(account.UserName, temporary);
        Assert.True((await Harness.Me(pending)).MustChangePassword);
        await using var ownerApp = new Harness();
        await ownerApp.Seed();
        using var owner = await ownerApp.SignedIn();
        var draft = await CreateDraft(owner, new { subject = "受保護" });

        // 對兩種身分直接測清單、明細及三個異動入口，再確認原草稿保留。
        foreach (var denied in new[] { admin, pending })
        {
            await Expect(await denied.GetAsync(DraftsPath), HttpStatusCode.Forbidden, "forbidden");
            await Expect(await denied.GetAsync(PathOf(draft)), HttpStatusCode.Forbidden, "forbidden");
            await Expect(await denied.PostAsJsonAsync(DraftsPath, new { }), HttpStatusCode.Forbidden, "forbidden");
            await Expect(await denied.PostAsJsonAsync(PathOf(draft), Edit(draft, "越權")), HttpStatusCode.Forbidden, "forbidden");
            await Expect(await denied.PostAsJsonAsync($"{PathOf(draft)}/delete", new { version = VersionOf(draft) }),
                HttpStatusCode.Forbidden, "forbidden");
        }
        Assert.True(JsonElement.DeepEquals(draft, await ReadDraft(owner, draft)));
    }

    // 未登入時先回登入失效，有效登入缺少防偽權杖時三種異動都被拒絕。
    [Fact]
    public async Task Authentication_precedes_csrf_and_missing_csrf_never_changes_a_draft()
    {
        // 匿名 client 不取得 token，確認草稿端點沿用共用的 auth-first 規則。
        await using var app = new Harness();
        await app.Seed();
        using var anonymous = app.Client();
        await Expect(await anonymous.GetAsync(DraftsPath), HttpStatusCode.Unauthorized, "unauthenticated");
        await Expect(await anonymous.PostAsJsonAsync(DraftsPath, new { }), HttpStatusCode.Unauthorized, "unauthenticated");

        // 建立有效原稿，再只移除權杖標頭，讓 CSRF 失敗而登入保持有效。
        using var owner = await app.SignedIn();
        var draft = await CreateDraft(owner, new { subject = "保留" });
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await Expect(await owner.PostAsJsonAsync(DraftsPath, new { }), HttpStatusCode.BadRequest, "invalid_csrf");
        await Expect(await owner.PostAsJsonAsync(PathOf(draft), Edit(draft, "不可保存")), HttpStatusCode.BadRequest, "invalid_csrf");
        await Expect(await owner.PostAsJsonAsync($"{PathOf(draft)}/delete", new { version = VersionOf(draft) }),
            HttpStatusCode.BadRequest, "invalid_csrf");
        Assert.True(JsonElement.DeepEquals(draft, await ReadDraft(owner, draft)));
        Assert.Single((await owner.GetFromJsonAsync<JsonElement>(DraftsPath)).EnumerateArray());
    }

    // 同一個 Cookie 容器切換帳號後，草稿清單與明細只接受目前身分的讀取權杖。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Draft_reads_validate_a_supplied_token_against_the_current_cookie_identity(bool detail)
    {
        // 登出 A 後以同一個 HTTP client 登入 B，模擬分頁共用 Cookie 但保留 A 的舊權杖。
        const string header = "X-CSRF-TOKEN";
        await using var first = new Harness();
        await first.Seed();
        await using var second = new Harness();
        await second.Seed();
        using var browser = await first.SignedIn();
        var staleToken = browser.DefaultRequestHeaders.GetValues(header).Single();
        await Expect(await browser.PostAsync("/api/auth/logout", null), HttpStatusCode.NoContent);
        await Expect(await browser.GetAsync(DraftsPath), HttpStatusCode.Unauthorized, "unauthenticated");
        await Expect(await Harness.Login(browser, second.UserName, second.Password), HttpStatusCode.NoContent);
        await Harness.Token(browser);
        var draft = await CreateDraft(browser, new { subject = "目前帳號的草稿" });
        var path = detail ? PathOf(draft) : DraftsPath;

        // 用合法 B 權杖讀取後只替換標頭，確定拒絕來自身分失配而非草稿所有權。
        var matched = await browser.GetFromJsonAsync<JsonElement>(path);
        var readDraft = detail ? matched : Assert.Single(matched.EnumerateArray());
        Assert.Equal(draft.GetProperty("id").GetGuid(), readDraft.GetProperty("id").GetGuid());
        browser.DefaultRequestHeaders.Remove(header);
        browser.DefaultRequestHeaders.Add(header, staleToken);
        await Expect(await browser.GetAsync(path), HttpStatusCode.BadRequest, "invalid_csrf");

        // 直接使用有效 Cookie 的 GET 仍支援既有無標頭用法，內容與合法權杖的結果一致。
        browser.DefaultRequestHeaders.Remove(header);
        var tokenless = await browser.GetFromJsonAsync<JsonElement>(path);
        Assert.True(JsonElement.DeepEquals(matched, tokenless));
    }

    // 身分回應的權杖應和 JSON 中的使用者一致，並可直接用於後續草稿讀寫。
    [Fact]
    public async Task Identity_response_token_is_paired_with_its_authenticated_user()
    {
        // 在同一個 Cookie 容器內先驗證匿名回應，再建立 A、B 兩個可切換的帳號。
        const string header = "X-CSRF-TOKEN";
        await using var first = new Harness();
        await first.Seed();
        await using var second = new Harness();
        await second.Seed();
        using var browser = first.Client();
        using var anonymous = await browser.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.False(anonymous.Headers.Contains(header));

        // 用 A 的身分回應標頭建立及重開草稿，證明權杖可搭配相同身分的 Cookie。
        await Expect(await first.Login(browser), HttpStatusCode.NoContent);
        var staleToken = await UseTokenFromIdentity(browser, first.UserName, header);
        var firstDraft = await CreateDraft(browser, new { subject = "第一個帳號的草稿" });
        Assert.Equal(first.UserId, firstDraft.GetProperty("applicantId").GetString());
        Assert.True(JsonElement.DeepEquals(firstDraft, await ReadDraft(browser, firstDraft)));

        // 保留 A 標頭但換成 B 的 Cookie，直接證明草稿查詢和建立都拒絕失配。
        await Expect(await browser.PostAsync("/api/auth/logout", null), HttpStatusCode.NoContent);
        await Expect(await Harness.Login(browser, second.UserName, second.Password), HttpStatusCode.NoContent);
        browser.DefaultRequestHeaders.Remove(header);
        browser.DefaultRequestHeaders.Add(header, staleToken);
        await Expect(await browser.GetAsync(DraftsPath), HttpStatusCode.BadRequest, "invalid_csrf");
        await Expect(await browser.PostAsJsonAsync(DraftsPath, new { subject = "不可保存" }),
            HttpStatusCode.BadRequest, "invalid_csrf");

        // 取得 B 的配對權杖後可建立並查詢，清單只包含 B 剛成功建立的草稿。
        await UseTokenFromIdentity(browser, second.UserName, header);
        var secondDraft = await CreateDraft(browser, new { subject = "第二個帳號的草稿" });
        Assert.Equal(second.UserId, secondDraft.GetProperty("applicantId").GetString());
        var list = await browser.GetFromJsonAsync<JsonElement>(DraftsPath);
        Assert.Equal(secondDraft.GetProperty("id").GetGuid(), Assert.Single(list.EnumerateArray()).GetProperty("id").GetGuid());
    }

    // 閒置期限或登出只撤銷該次登入，重新登入仍可讀回本人已保存的草稿。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logged_out_or_expired_sessions_cannot_change_saved_drafts(bool expire)
    {
        // 在同一時刻登入及保存，讓測試能精確停在原工作階段的到期整點。
        await using var app = new Harness();
        await app.Seed();
        using var owner = await app.SignedIn();
        var draft = await CreateDraft(owner, new { subject = "下次繼續", lines = new[] { new { quantity = 2, unitPrice = 1.23m } } });
        if (expire)
            app.Clock.Advance(TimeSpan.FromMinutes(30));
        else
            await Expect(await owner.PostAsync("/api/auth/logout", null), HttpStatusCode.NoContent);

        // 舊登入無法讀取、更新或刪除，改以相同帳號的新工作階段驗證資料完整。
        await Expect(await owner.GetAsync(PathOf(draft)), HttpStatusCode.Unauthorized, "unauthenticated");
        await Expect(await owner.PostAsJsonAsync(PathOf(draft), Edit(draft, "不可保存")), HttpStatusCode.Unauthorized, "unauthenticated");
        await Expect(await owner.PostAsJsonAsync($"{PathOf(draft)}/delete", new { version = VersionOf(draft) }),
            HttpStatusCode.Unauthorized, "unauthenticated");
        using var fresh = await app.SignedIn();
        Assert.True(JsonElement.DeepEquals(draft, await ReadDraft(fresh, draft)));
    }

    // 過期的保存及刪除都不可作用於新版草稿，合法刪除後清單和明細都找不到。
    [Fact]
    public async Task Delete_requires_the_current_version_and_removes_the_draft_from_read_paths()
    {
        // 建立後保存新版本，讓下一個舊版本請求有明確的對照資料。
        await using var app = new Harness();
        await app.Seed();
        using var owner = await app.SignedIn();
        var draft = await CreateDraft(owner, new { subject = "舊主旨" });
        await Expect(await owner.PostAsJsonAsync(PathOf(draft), Edit(draft, "新主旨")), HttpStatusCode.OK);
        var current = await ReadDraft(owner, draft);
        Assert.NotEqual(VersionOf(draft), VersionOf(current));

        // 同一舊版本嘗試保存及刪除後重新查詢，確定成功的版本與內容都保留。
        await Expect(await owner.PostAsJsonAsync(PathOf(draft), Edit(draft, "過期主旨")), HttpStatusCode.Conflict, "stale_version");
        var delete = $"{PathOf(draft)}/delete";
        await Expect(await owner.PostAsJsonAsync(delete, new { version = VersionOf(draft) }), HttpStatusCode.Conflict, "stale_version");
        Assert.True(JsonElement.DeepEquals(current, await ReadDraft(owner, draft)));

        // 使用新版本刪除，再確認兩個讀取入口及再次刪除都反映相同結果。
        await Expect(await owner.PostAsJsonAsync(delete, new { version = VersionOf(current) }), HttpStatusCode.NoContent);
        await Expect(await owner.GetAsync(PathOf(draft)), HttpStatusCode.NotFound, "not_found");
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>(DraftsPath)).EnumerateArray());
        await Expect(await owner.PostAsJsonAsync(delete, new { version = VersionOf(current) }), HttpStatusCode.NotFound, "not_found");
    }

    // 建立走正式 HTTP，並將測試資料定位到回應中的系統識別。
    internal static async Task<JsonElement> CreateDraft(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync(DraftsPath, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // 重新查詢正式端點，驗證持久化及目前登入對資料的存取權。
    internal static Task<JsonElement> ReadDraft(HttpClient client, JsonElement draft) =>
        client.GetFromJsonAsync<JsonElement>(PathOf(draft));

    // 路徑使用正式回應的 UUID，讓後續請求測到同一張草稿。
    internal static string PathOf(JsonElement draft) => $"{DraftsPath}/{draft.GetProperty("id").GetGuid()}";

    // 版本以不透明字串原樣傳回，避免測試依賴伺服器實作格式。
    internal static string? VersionOf(JsonElement draft) => draft.GetProperty("version").GetString();

    // 從同一次身分回應核對 JSON 使用者及權杖，後續請求只使用這份回應的標頭。
    private static async Task<string> UseTokenFromIdentity(HttpClient client, string userName, string header)
    {
        // 直接讀取公開的身分端點，缺少權杖時只輸出欄位資訊。
        using var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var identity = await response.Content.ReadFromJsonAsync<Procurement.IdentityResponse>();
        Assert.Equal(userName, identity?.UserName);
        Assert.True(response.Headers.TryGetValues(header, out var values), "身分回應須提供防偽權杖。");
        var token = values?.SingleOrDefault();
        Assert.NotNull(token);

        // 取代登入前使用的權杖，避免後續草稿請求同時攜帶兩個身分的值。
        client.DefaultRequestHeaders.Remove(header);
        client.DefaultRequestHeaders.Add(header, token);
        return token;
    }

    // 簡單保存使用完整欄位，避免省略欄位意外讓授權或版本案例停在格式驗證。
    internal static object Edit(JsonElement draft, string subject) =>
        new { subject, purpose = "測試用途", lines = new[] { new { quantity = 1, unitPrice = 0.29m } }, version = VersionOf(draft) };

    // 直接送原始 JSON，以保留超精度數值和異常型別的原貌。
    internal static async Task<HttpResponseMessage> PostRaw(HttpClient client, string path, string json)
    {
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync(path, body);
    }

    // 帶有效版本測保存驗證，拒絕後從新請求核對整份草稿保持原樣。
    private static async Task RejectSave(HttpClient client, JsonElement original, string lines)
    {
        await Expect(await PostRaw(client, PathOf(original),
            $$"""{"subject":"不應保存","purpose":null,"lines":{{lines}},"version":{{original.GetProperty("version").GetRawText()}}}"""),
            HttpStatusCode.BadRequest, "invalid_input");
        Assert.True(JsonElement.DeepEquals(original, await ReadDraft(client, original)));
    }

    // 只改本案例的目標文字欄位，其餘保留合法值以隔離拒絕原因。
    private static Dictionary<string, object?> TextPayload(string field, string value)
    {
        return new()
        {
            ["subject"] = field == "subject" ? value : "補給",
            ["purpose"] = field == "purpose" ? value : "辦公用途",
            ["lines"] = new[] { new { itemName = field == "itemName" ? value : "品項", unit = field == "unit" ? value : "件" } }
        };
    }

    // 用同一路徑定位已送出的文字與 API 讀回值，包含巢狀明細欄位。
    private static JsonElement TextField(JsonElement payload, string field) =>
        field is "itemName" or "unit" ? payload.GetProperty("lines")[0].GetProperty(field) : payload.GetProperty(field);
}
