// 真 API 驗證身分與撤銷；攔截案例只驗證瀏覽器的錯誤恢復，不取代後端安全測試。
import { randomBytes } from "node:crypto";
import { readFileSync, statSync } from "node:fs";
import {
  expect,
  test,
  type APIRequestContext,
  type Locator,
  type Page,
} from "@playwright/test";

// 目標或憑證權限不符就中止，避免改密碼案例誤觸展示環境。
if (
  process.env.TEST_TARGET !== "procurement-approval-browser" ||
  process.env.BASE_URL !== "http://procurement-web.test:8080" ||
  (statSync("/local").mode & 0o777) !== 0o700 ||
  (statSync("/local/initial-accounts.json").mode & 0o777) !== 0o600
)
  throw new Error("拒絕未知或未受保護的瀏覽器測試目標。");

// 只從隔離環境的本機檔讀取 fixture，開跑前先確認四種角色都有可用的輸入。
const accounts: Record<string, string> = JSON.parse(
  readFileSync("/local/initial-accounts.json", "utf8"),
);
for (const name of ["admin", "applicant", "manager", "finance"]) {
  if (typeof accounts[name] !== "string" || accounts[name].length < 12)
    throw new Error("示範帳號 fixture 不完整。");
}

// 只累計錯誤數，不保存可能包含敏感資料的瀏覽器錯誤內容。
const runtimeErrors = new WeakMap<Page, number>();

// 每案先準備匿名頁面與錯誤計數，避免服務啟動時間或舊頁面狀態干擾斷言。
test.beforeEach(async ({ page, request }) => {
  // 分頁各自計數，讓驗證結果只對應本次案例。
  runtimeErrors.set(page, 0);
  page.on(
    "pageerror",
    // 僅計數未捕捉的頁面例外，避免紀錄敏感錯誤內容。
    () => runtimeErrors.set(page, (runtimeErrors.get(page) ?? 0) + 1),
  );
  page.on(
    "console",
    // 收集 console 錯誤；測試刻意注入請求失敗，因此略過資源載入錯誤。
    (entry) => {
      if (
        entry.type() === "error" &&
        !entry.text().startsWith("Failed to load resource:")
      )
        runtimeErrors.set(page, (runtimeErrors.get(page) ?? 0) + 1);
    },
  );

  // 先輪詢網站入口是否可讀，以實際回應判斷服務已啟動。
  await expect
    .poll(
      // 網站尚未連通時給輪詢一個未就緒值，讓它在期限內繼續等待。
      async () => {
        try {
          return (await request.get("/")).status();
        } catch {
          return 0;
        }
      },
      { timeout: 30000 },
    )
    .toBe(200);

  // 開啟頁面並等匿名表單出現，確認 React 已完成初始身分讀取。
  await page.goto("/");
  await expect(
    page.getByRole("heading", { name: "登入", exact: true }),
  ).toBeVisible();
});

// 案例結束時確認頁面沒有額外執行錯誤，即使功能斷言已通過也要檢查。
test.afterEach(async ({ page }) => {
  expect(runtimeErrors.get(page)).toBe(0);
});

// 在頁面內設定密碼，避免可用憑證進入 fill 的失敗診斷紀錄。
async function secret(input: Locator, value: string) {
  await input.evaluate(
    // 更新欄位後發出 input 事件，使頁面仍能收到欄位變動。
    (element, password) => {
      (element as HTMLInputElement).value = password;
      element.dispatchEvent(new Event("input", { bubbles: true }));
    },
    value,
  );
}

// 畫面登入的 fixture 密碼經 secret 輸入，避免進入 fill 的診斷紀錄。
async function login(page: Page, name: string, password = accounts[name]) {
  // 透過可存取標籤填寫帳密，再點擊真正的提交按鈕。
  await page.getByLabel("帳號", { exact: true }).fill(name);
  await secret(page.getByLabel("密碼", { exact: true }), password);
  await page.getByRole("button", { name: "登入", exact: true }).click();
}

// API 輔助操作也使用同一個 Cookie context 取得防偽 token。
async function csrf(request: APIRequestContext) {
  // 先確認請求成功，避免把錯誤回應當成可用的 token。
  const response = await request.get("/api/auth/csrf");
  expect(response.status()).toBe(200);
  return (await response.json()).token as string;
}

// 從指定 Cookie context 登入，僅回傳狀態以免斷言輸出回應或帳密。
async function apiLogin(
  request: APIRequestContext,
  name: string,
  password: string,
) {
  // 在同一 context 先取得權杖，再把它與帳密一併送到登入 API。
  const response = await request.post("/api/auth/login", {
    headers: { "X-CSRF-TOKEN": await csrf(request) },
    data: { userName: name, password },
  });
  return response.status();
}

// 還原主管 fixture 的密碼供後續案例使用，並用 soft assertion 保留還原診斷。
async function restoreManagerPassword(
  request: APIRequestContext,
  password: string,
) {
  try {
    // 使用已提交的新密碼登入；登入失敗時停止後續還原請求。
    const status = await apiLogin(request, "manager", password);
    expect.soft(status, "隔離 fixture 還原登入").toBe(204);
    if (status !== 204) return;

    // 取得已登入身分的目前版本，避免用舊版本變更密碼。
    const read = await request.get("/api/auth/me");
    expect.soft(read.status(), "隔離 fixture 還原身分讀取").toBe(200);
    if (read.status() !== 200) return;
    const identity = await read.json();

    // 以新權杖和目前版本還原 fixture，供後續案例使用原密碼。
    const response = await request.post("/api/auth/change-password", {
      headers: { "X-CSRF-TOKEN": await csrf(request) },
      data: {
        currentPassword: password,
        newPassword: accounts.manager,
        version: identity.version,
      },
    });
    expect.soft(response.status(), "隔離 fixture 密碼還原").toBe(204);
  } catch {
    // 還原本身也可能已生效；保留未確認診斷，不再次提交改密碼。
    expect
      .soft(
        false,
        "隔離 fixture 還原結果未確認，沒有自動重送；請先檢查隔離環境。",
      )
      .toBe(true);
  }
}

// 此處只檢查兩種寬度的可見性與水平邊界；遮罩截圖另供人工檢視。
async function layout(page: Page, name: string) {
  for (const width of [1280, 390]) {
    // 同時確認實際 viewport 與頁面捲動寬度，避免只以設定值判定版面。
    await page.setViewportSize({ width, height: 900 });
    expect(
      await page.evaluate(
        // 從瀏覽器實際讀取寬度，核對本次縮放已套用。
        () => window.innerWidth,
      ),
    ).toBe(width);
    expect(
      await page.evaluate(
        // 比對文件與 viewport 寬度，檢查頁面是否產生水平捲動。
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    ).toBe(true);

    // 再逐一確認欄位可見且落在 viewport 內，避免只看整頁寬度漏掉欄位問題。
    const inputs = page.locator("input");
    for (const input of await inputs.all()) {
      await expect(input).toBeVisible();
      const box = await input.boundingBox();
      expect(
        box !== null &&
          box.width > 150 &&
          box.x >= 0 &&
          box.x + box.width <= width,
      ).toBe(true);
    }

    // 保存版面證據前遮蔽所有輸入欄位，避免輸出憑證。
    await page.screenshot({
      path: `/evidence/${name}-${width}.png`,
      fullPage: true,
      mask: [inputs],
    });
  }
}

// 從匿名表單驗證標籤、長度、鍵盤順序與版面，並確認過短密碼由瀏覽器擋下。
test("匿名表單、標籤、鍵盤焦點與桌機手機版面", async ({ page }) => {
  // 依可存取名稱定位欄位，核對登入輸入限制。
  await expect(page.getByLabel("帳號", { exact: true })).toHaveAttribute(
    "maxlength",
    "256",
  );
  await expect(page.getByLabel("密碼", { exact: true })).toHaveAttribute(
    "minlength",
    "12",
  );
  await expect(page.getByLabel("密碼", { exact: true })).toHaveAttribute(
    "maxlength",
    "128",
  );
  // 以實際 Tab 操作檢查焦點順序，再驗證桌機和手機版面。
  await page.keyboard.press("Tab");
  await expect(page.getByLabel("帳號", { exact: true })).toBeFocused();
  await page.keyboard.press("Tab");
  await expect(page.getByLabel("密碼", { exact: true })).toBeFocused();
  await expect(page.locator("body")).not.toHaveText("");
  await layout(page, "login");

  // 提交前開始計數，讓案例能確認瀏覽器沒有發出任何 POST。
  let posts = 0;
  page.on(
    "request",
    // 只計入寫入請求，避免頁面讀取混入驗證結果。
    (request) => {
      if (request.method() === "POST") posts++;
    },
  );
  await page.getByLabel("帳號", { exact: true }).fill("applicant");
  // 非敏感短字串用真實輸入驗證瀏覽器 minlength，不以程式注入的 fixture 取代。
  await page.getByLabel("密碼", { exact: true }).fill("short-test!");
  await page.getByRole("button", { name: "登入", exact: true }).click();
  expect(
    await page.getByLabel("密碼", { exact: true }).evaluate(
      // 讀取瀏覽器原生驗證狀態，確認是長度限制擋下提交。
      (input) => (input as HTMLInputElement).validity.tooShort,
    ),
  ).toBe(true);
  expect(posts).toBe(0);
});

// 循序用真 API 驗證四種角色，並藉重載確認登出在畫面之外仍有效。
test("四種示範身分與正常登出、重新載入維持登出", async ({ page }) => {
  for (const [name, category, role] of [
    ["admin", "管理帳號", "管理員"],
    ["applicant", "業務帳號", "申請人"],
    ["manager", "業務帳號", "主管"],
    ["finance", "業務帳號", "財務"],
  ]) {
    // 比對已登入畫面顯示的帳號、類別與角色；管理帳號另外驗證身分頁版面。
    await login(page, name);
    await expect(page.getByRole("heading", { name: "目前身分" })).toBeVisible();
    await expect(page.locator("dd").nth(0)).toHaveText(name);
    await expect(page.locator("dd").nth(1)).toHaveText(category);
    await expect(page.locator("dd").nth(2)).toContainText(role);
    if (name === "admin") await layout(page, "identity");

    // 確認登出提示與空白欄位，再重載確認下次讀取仍為匿名。
    await page.getByRole("button", { name: "登出", exact: true }).click();
    await expect(
      page.getByRole("status").filter({ hasText: "已登出。" }),
    ).toHaveText("已登出。");
    await expect(page.getByLabel("帳號", { exact: true })).toHaveValue("");
    await page.reload();
    await expect(
      page.getByRole("heading", { name: "登入", exact: true }),
    ).toBeVisible();
  }
});

// 以真實登入驗證錯誤密碼可修正，並從頁面環境檢查瀏覽器可見的儲存資料。
test("錯誤登入可修正後成功，Cookie 不可供 JavaScript 讀取", async ({
  page,
}) => {
  // 先送出錯誤密碼並等拒絕訊息，再用 fixture 密碼確認可重新提交。
  await login(page, "applicant", randomBytes(24).toString("hex"));
  await expect(page.getByRole("alert")).toContainText("帳號或密碼");

  await login(page, "applicant");
  await expect(page.getByRole("heading", { name: "目前身分" })).toBeVisible();

  // 在登入完成後檢查 JavaScript 能讀到的 Cookie 和儲存空間。
  expect(
    await page.evaluate(
      // 搜尋登入 Cookie 名稱，避免把實際 Cookie 值帶回測試程序。
      () => document.cookie.includes("procurement.session"),
    ),
  ).toBe(false);
  expect(
    await page.evaluate(
      // 只回報兩種儲存空間的項目數，不讀取內容。
      () => localStorage.length + sessionStorage.length,
    ),
  ).toBe(0);
});

// 以兩個獨立登入驗證改密碼修正與撤銷，避免只從當前頁面推論其他登入失效。
test("錯誤目前密碼修正、成功回登入、全部舊登入撤銷及新密碼登入", async ({
  page,
  browser,
}) => {
  // 建立另一個 context 持有舊登入，並準備此案專用的新密碼。
  const previous = await browser.newContext({ baseURL: process.env.BASE_URL });
  const newPassword = randomBytes(24).toString("hex");
  // 只以真 API 的成功回應決定是否還原，不從 UI 提示推測提交結果。
  let changed = false;

  try {
    // 同一帳號分別從 API 與 UI 登入，保留兩組待撤銷的 Cookie。
    expect(await apiLogin(previous.request, "manager", accounts.manager)).toBe(
      204,
    );
    await login(page, "manager");
    await expect(page.getByRole("heading", { name: "目前身分" })).toBeVisible();

    // 先送錯誤的目前密碼，確認拒絕後可只更正這一欄。
    await secret(
      page.getByLabel("目前密碼", { exact: true }),
      randomBytes(24).toString("hex"),
    );
    await secret(page.getByLabel("新密碼", { exact: true }), newPassword);
    await page.getByRole("button", { name: "變更密碼", exact: true }).click();
    await expect(page.getByRole("alert")).toContainText("目前密碼");

    // 只替換目前密碼，讓下一次成功也能驗證新密碼欄位有保留。
    await secret(
      page.getByLabel("目前密碼", { exact: true }),
      accounts.manager,
    );

    // 先註冊回應等待，再觸發提交，避免漏接較快的成功回應。
    const [response] = await Promise.all([
      page
        .waitForResponse(
          // 鎖定改密碼 POST，以免身分重讀等其他回應提前解除等待。
          (response) =>
            response.url().endsWith("/api/auth/change-password") &&
            response.request().method() === "POST",
        )
        // 收到成功狀態即記下還原依據，讓後續 UI 斷言失敗時仍能清理。
        .then((response) => {
          changed = response.status() === 204;
          return response;
        }),
      page.getByRole("button", { name: "變更密碼", exact: true }).click(),
    ]);

    // 確認寫入成功後 UI 返回匿名狀態，並顯示已變更提示。
    expect(response.status()).toBe(204);
    await expect(
      page.getByRole("heading", { name: "登入", exact: true }),
    ).toBeVisible();
    await expect(page.getByRole("status")).toContainText("密碼已變更");

    // 真 API 證明另一個舊登入失效，再從畫面確認舊密碼拒絕與新密碼可用。
    expect((await previous.request.get("/api/auth/me")).status()).toBe(401);
    await login(page, "manager", accounts.manager);
    await expect(page.getByRole("alert")).toContainText("帳號或密碼");
    await login(page, "manager", newPassword);
    await expect(page.getByRole("heading", { name: "目前身分" })).toBeVisible();
  } finally {
    // 僅復原此隔離 fixture 的密碼，不重設或清除展示庫。
    if (changed) await restoreManagerPassword(previous.request, newPassword);
    await previous.close();
  }
});

// 直接撤銷頁面使用的登入，藉手動重讀確認舊身分與敏感欄位不再顯示。
test("真實登入撤銷後重新確認回登入並清除敏感表單", async ({ page }) => {
  // 在登入後填入未提交的新密碼，讓案例能觀察敏感表單是否被卸載。
  await login(page, "finance");
  await expect(page.getByRole("heading", { name: "目前身分" })).toBeVisible();
  await secret(
    page.getByLabel("新密碼", { exact: true }),
    randomBytes(24).toString("hex"),
  );
  // 共用頁面 Cookie 的 API 請求先撤銷登入，再由 UI 重讀確認表單清除。
  const response = await page.request.post("/api/auth/logout", {
    headers: { "X-CSRF-TOKEN": await csrf(page.request) },
  });
  expect(response.status()).toBe(204);

  // 從畫面重讀，確認回到匿名表單且新舊表單沒有殘留敏感值。
  await page.getByRole("button", { name: "重新確認身分" }).click();
  await expect(
    page.getByRole("heading", { name: "登入", exact: true }),
  ).toBeVisible();
  await expect(page.getByLabel("帳號", { exact: true })).toHaveValue("");
  await expect(page.getByLabel("密碼", { exact: true })).toHaveValue("");
  await expect(page.getByLabel("新密碼", { exact: true })).toHaveCount(0);
});

// 以下攔截特定失敗回應；恢復後的登入或登出仍走真 API。
// 只讓第一次登出收到 CSRF 拒絕，驗證重讀與使用者再送出是兩個動作。
test("mocked CSRF 失效只手動恢復並重試，不自動重送", async ({ page }) => {
  // 先建立真實登入，讓第一次被拒後可以查回同一身分。
  await login(page, "admin");
  await expect(page.getByRole("heading", { name: "目前身分" })).toBeVisible();

  // 計數每次登出，讓自動重送和使用者明確重試能被區分。
  let posts = 0;
  await page.route(
    "**/api/auth/logout",
    // 第一次回傳防偽錯誤，之後放行以驗證手動重試可完成真登出。
    async (route) => {
      posts++;
      if (posts === 1)
        await route.fulfill({
          status: 400,
          json: { code: "invalid_csrf", message: "安全權杖已失效。" },
        });
      else await route.continue();
    },
  );

  // 驗證拒絕提示與手動恢復畫面，再確認重讀沒有重送 POST。
  await page.getByRole("button", { name: "登出", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("由您再次操作");
  await expect(
    page.getByRole("heading", { name: "請先確認登入狀態" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "重新確認身分" }).click();
  await expect(page.getByRole("heading", { name: "目前身分" })).toBeVisible();
  expect(posts).toBe(1);
  await expect(page.getByLabel("目前密碼", { exact: true })).toHaveValue("");

  // 由使用者再次按登出，確認第二次 POST 才完成操作。
  await page.getByRole("button", { name: "登出", exact: true }).click();
  await expect(
    page.getByRole("status").filter({ hasText: "已登出。" }),
  ).toHaveText("已登出。");
  expect(posts).toBe(2);
});

// 讓改密碼回傳登入失效，藉另一帳號重新登入驗證頁面內容的身分隔離。
test("mocked 401 回登入、重新登入另一帳號不保留前帳號內容", async ({
  page,
}) => {
  // 從申請人的真登入開始，保留改密碼表單可操作的前提。
  await login(page, "applicant");
  await expect(page.getByRole("heading", { name: "目前身分" })).toBeVisible();

  // 攔截改密碼回應並計數，驗證身分恢復沒有再次提交原輸入。
  let posts = 0;
  await page.route(
    "**/api/auth/change-password",
    // 先以真 API 登出，再回傳模擬的寫入拒絕，使接續的 me 可確認為匿名。
    async (route) => {
      posts++;
      await page.request.post("/api/auth/logout", {
        headers: { "X-CSRF-TOKEN": await csrf(page.request) },
      });
      await route.fulfill({
        status: 401,
        json: { code: "unauthenticated", message: "登入已失效。" },
      });
    },
  );

  // 填寫並提交改密碼，確認拒絕後直接回到登入且只有一次改密碼請求。
  await secret(
    page.getByLabel("目前密碼", { exact: true }),
    accounts.applicant,
  );
  await secret(
    page.getByLabel("新密碼", { exact: true }),
    randomBytes(24).toString("hex"),
  );
  await page.getByRole("button", { name: "變更密碼", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "登入", exact: true }),
  ).toBeVisible();
  await expect(page.getByRole("alert")).toContainText("登入已失效");
  expect(posts).toBe(1);

  // 改以財務登入，檢查只顯示新帳號、空白表單與當前提示狀態。
  await login(page, "finance");
  await expect(page.locator("dd").first()).toHaveText("finance");
  await expect(page.getByLabel("新密碼", { exact: true })).toHaveValue("");
  await expect(page.getByRole("alert")).toHaveCount(0);
});

// 在登入請求進行中嘗試再次提交，確認畫面阻擋重複送出且失敗後解除停用。
test("mocked pending 阻擋重複送出，失敗後可再次操作", async ({ page }) => {
  // 明確扣住回應到重複輸入檢查結束，不用任意延遲猜測 pending 時段。
  let release!: () => void;
  const gate = new Promise<void>(
    // 保留解除等待的函式，讓案例在完成 pending 斷言後才放行回應。
    (resolve) => {
      release = resolve;
    },
  );
  let posts = 0;
  await page.route(
    "**/api/auth/login",
    // 先計入 POST 並等案例放行，再回傳拒絕以檢查失敗後的控制項。
    async (route) => {
      posts++;
      await gate;
      await route.fulfill({
        status: 401,
        json: { code: "invalid_credentials", message: "帳號或密碼不正確。" },
      });
    },
  );

  // 在同一請求仍未完成時按 Enter，確認沒有出現第二個 POST。
  await login(page, "admin");
  await expect(page.getByRole("button", { name: "處理中…" })).toBeDisabled();
  await page.keyboard.press("Enter");
  expect(posts).toBe(1);

  // 放行拒絕回應後確認顯示錯誤，並可由使用者再次操作。
  release();
  await expect(page.getByRole("alert")).toContainText("帳號或密碼");
  await expect(
    page.getByRole("button", { name: "登入", exact: true }),
  ).toBeEnabled();
});

// 讓改密碼真的生效但讓瀏覽器遺失回應，檢驗它如何維持未知提示並恢復登入。
test("模擬改密碼已提交但回應遺失，保留未知提示並用新密碼恢復", async ({
  page,
  browser,
}) => {
  // 建立獨立還原 context，避免清理操作依賴被測頁面的登入或路由攔截。
  const cleanup = await browser.newContext({ baseURL: process.env.BASE_URL });
  const newPassword = randomBytes(24).toString("hex");
  let committed = false;
  let posts = 0;

  try {
    // 先完成真實登入，才能從 UI 發出後端可接受的改密碼。
    await login(page, "manager");
    await expect(page.getByRole("heading", { name: "目前身分" })).toBeVisible();

    await page.route(
      "**/api/auth/change-password",
      // 先確認真 API 已提交，再中斷瀏覽器回應，重現使用者無法確認的情境。
      async (route) => {
        // 先以真 API 的 204 確認寫入完成，並保留獨立清理所需的旗標。
        posts++;
        const response = await route.fetch();
        expect(response.status()).toBe(204);
        committed = true;

        // 瀏覽器只看到連線失敗，因此必須依未知結果處理。
        await route.abort("failed");
      },
    );

    // 從 UI 送出操作後確認未知提示與使用新密碼的指引。
    await secret(
      page.getByLabel("目前密碼", { exact: true }),
      accounts.manager,
    );
    await secret(page.getByLabel("新密碼", { exact: true }), newPassword);
    await page.getByRole("button", { name: "變更密碼", exact: true }).click();
    await expect(page.getByRole("alert")).toContainText("操作結果未確認");
    await expect(page.getByRole("alert")).toContainText("先嘗試新密碼");

    // 先讓 GET 恢復失敗再成功，兩者都不能消除先前 POST 的未知提示。
    await page.route(
      "**/api/auth/csrf",
      // 只中斷恢復的第一個讀取，讓案例驗證此失敗不會抹掉原提示。
      (route) => route.abort("failed"),
    );
    await page.getByRole("button", { name: "重新確認身分" }).click();
    await expect(
      page.getByRole("button", { name: "重新確認身分" }),
    ).toBeEnabled();
    await expect(page.getByRole("alert")).toContainText("操作結果未確認");

    // 移除讀取攔截並重讀，確認真實身分已變匿名且原 POST 沒有重送。
    await page.unroute("**/api/auth/csrf");
    await page.getByRole("button", { name: "重新確認身分" }).click();
    await expect(
      page.getByRole("heading", { name: "登入", exact: true }),
    ).toBeVisible();
    expect(posts).toBe(1);

    // 舊密碼拒絕後仍應保留警示；新密碼成功登入後才解除。
    await login(page, "manager", accounts.manager);
    await expect(page.getByRole("alert")).toContainText("帳號或密碼");
    await expect(page.getByRole("alert")).toContainText("操作結果未確認");
    await login(page, "manager", newPassword);
    await expect(page.getByRole("heading", { name: "目前身分" })).toBeVisible();
    await expect(page.getByRole("alert")).toHaveCount(0);
  } finally {
    // 即使瀏覽器沒有收到回應，已觀察到真 API 提交便以獨立 context 還原。
    if (committed) await restoreManagerPassword(cleanup.request, newPassword);
    await cleanup.close();
  }
});

// 故障注入階段未向後端送出登入，只驗證未知提示與禁止自動重送的 UI 行為。
for (const failure of ["server", "transport"] as const) {
  // 同一恢復流程分別驗證 500 和連線失敗，確認兩者都保守處理寫入結果。
  test(`mocked ${failure} 未知結果不誤報成功、不重送，確認後可重新登入`, async ({
    page,
  }) => {
    // 攔截並計數登入 POST，讓測試能驗證未知狀態下沒有自動重送。
    let posts = 0;
    await page.route(
      "**/api/auth/login",
      // 依案例回傳 500 或中斷連線，分開重現兩種無法確認的回應。
      async (route) => {
        posts++;
        if (failure === "server")
          await route.fulfill({
            status: 500,
            json: { code: "server_error", message: "系統暫時無法完成操作。" },
          });
        else await route.abort("failed");
      },
    );

    // 觸發登入後驗證未知提示，以及可用的身分恢復入口。
    await login(page, "admin");
    await expect(page.getByRole("alert")).toContainText(
      "操作結果未確認，可能已生效",
    );
    await expect(page.getByRole("alert")).toContainText("不會自動重送");
    await expect(
      page.getByRole("heading", { name: "請先確認登入狀態" }),
    ).toBeVisible();

    // 重讀後回到匿名表單，原提示仍在，POST 計數也維持一次。
    await page.getByRole("button", { name: "重新確認身分" }).click();
    await expect(
      page.getByRole("heading", { name: "登入", exact: true }),
    ).toBeVisible();
    await expect(page.getByRole("alert")).toContainText("操作結果未確認");
    expect(posts).toBe(1);

    // 解除攔截並手動登入，確認真實成功時才清除警示。
    await page.unroute("**/api/auth/login");
    await login(page, "admin");
    await expect(page.getByRole("heading", { name: "目前身分" })).toBeVisible();
    await expect(page.getByRole("alert")).toHaveCount(0);
  });
}
