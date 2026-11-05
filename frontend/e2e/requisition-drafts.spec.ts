// 透過真實業務登入驗證本人草稿；攔截只用來觀察瀏覽器失敗恢復。
import { randomBytes } from "node:crypto";
import { readFileSync, statSync } from "node:fs";
import {
  expect,
  test,
  type APIRequestContext,
  type Locator,
  type Page,
} from "@playwright/test";

// 目標或憑證權限不符就中止，避免測試寫入展示環境。
if (
  process.env.TEST_TARGET !== "procurement-approval-browser" ||
  process.env.BASE_URL !== "http://procurement-web.test:8080" ||
  (statSync("/local").mode & 0o777) !== 0o700 ||
  (statSync("/local/initial-accounts.json").mode & 0o777) !== 0o600
)
  throw new Error("拒絕未知或未受保護的瀏覽器測試目標。");

// 只從隔離環境讀取必要的業務帳號，診斷內容不包含憑證。
const accounts: Record<string, string> = JSON.parse(
  readFileSync("/local/initial-accounts.json", "utf8"),
);
for (const name of ["applicant", "manager"]) {
  // 開跑前確認本案例會用到的帳號都有可用密碼。
  if (typeof accounts[name] !== "string" || accounts[name].length < 12)
    throw new Error("示範帳號 fixture 不完整。");
}

// 只保留執行錯誤數，避免錯誤文字洩漏畫面或請求資料。
const runtimeErrors = new WeakMap<Page, number>();
let monitoredPages: Page[] = [];

// 逐頁計數未捕捉例外及非資源載入錯誤，不保留原始訊息。
function monitorRuntimeErrors(page: Page) {
  // 每個 Cookie context 的分頁各自計數，供案例結束檢查。
  runtimeErrors.set(page, 0);
  monitoredPages.push(page);
  page.on(
    "pageerror",
    // 只累計未捕捉例外，不讀取或輸出錯誤內容。
    () => runtimeErrors.set(page, (runtimeErrors.get(page) ?? 0) + 1),
  );
  page.on(
    "console",
    // 請求中斷案例會產生資源錯誤，其他 console error 仍須回報。
    (entry) => {
      if (
        entry.type() === "error" &&
        !entry.text().startsWith("Failed to load resource:")
      )
        runtimeErrors.set(page, (runtimeErrors.get(page) ?? 0) + 1);
    },
  );
}

// 每案從匿名頁開始，確認既有安全瀏覽器目標可讀。
test.beforeEach(async ({ page, request }) => {
  // 重設本案例追蹤集合，避免前案分頁混入結果。
  monitoredPages = [];
  monitorRuntimeErrors(page);

  // 以真實入口回應等待服務就緒，不猜測啟動時間。
  await expect
    .poll(
      // 網站尚未連通時回傳未就緒值，讓輪詢在期限內繼續等待。
      async () => {
        try {
          return (await request.get("/")).status();
        } catch {
          // 網路尚未就緒只記為未就緒，不輸出例外內容。
          return 0;
        }
      },
      { timeout: 30000 },
    )
    .toBe(200);

  // 開啟匿名登入表單，等待應用程式完成初始身分讀取。
  await page.goto("/");
  await expect(
    page.getByRole("heading", { name: "登入", exact: true }),
  ).toBeVisible();
});

// 即使功能斷言通過，案例結束仍須確認所有頁面沒有執行錯誤。
test.afterEach(async () => {
  // 只斷言錯誤數，避免診斷輸出敏感的原始訊息。
  for (const page of monitoredPages) {
    expect(runtimeErrors.get(page) ?? 0).toBe(0);
  }
});

// 密碼在頁面內輸入，避免可用憑證進入 fill 的失敗診斷。
async function secret(input: Locator, value: string) {
  await input.evaluate(
    // 更新欄位後發出 input 事件，讓 React 收到真實欄位變動。
    (element, password) => {
      (element as HTMLInputElement).value = password;
      element.dispatchEvent(new Event("input", { bubbles: true }));
    },
    value,
  );
}

// 以可存取標籤登入，並沿用安全密碼輸入方式。
async function login(page: Page, name: string, password = accounts[name]) {
  // 透過畫面欄位輸入帳號，再以頁面內事件輸入密碼。
  await page.getByLabel("帳號", { exact: true }).fill(name);
  await secret(page.getByLabel("密碼", { exact: true }), password);
  await page.getByRole("button", { name: "登入", exact: true }).click();
}

// 等待已登入的本人草稿清單與新增操作都可用。
async function draftRegion(page: Page) {
  // 身分畫面及清單讀取完成後，才交回可操作區域。
  await expect(
    page.getByRole("heading", { name: "目前身分", exact: true }),
  ).toBeVisible();
  const region = page.getByRole("region", { name: "本人請購草稿" });
  await expect(
    region.getByRole("button", { name: "新增草稿", exact: true }),
  ).toBeEnabled();
  return region;
}

// 以固定標記取得表單，保存後名稱改變仍沿用同一個表單定位。
function draftEditor(region: Locator) {
  return region.locator("form[aria-labelledby='draft-editor-title']");
}

// 以唯一合成主旨找到清單列並讀取真實草稿詳情。
async function openDraft(region: Locator, subject: string) {
  // 只在唯一目標列中使用系統提供的開啟按鈕。
  const row = region.getByRole("listitem").filter({ hasText: subject });
  await expect(row).toHaveCount(1);
  await row.getByRole("button", { name: /^開啟草稿 / }).click();
  const form = region.getByRole("form", { name: /^編輯草稿 / });

  // 等待單筆 GET 完成並確認焦點回到編輯標題。
  await expect(form).toBeVisible();
  await expect(
    region.getByRole("heading", { name: /^編輯草稿 / }),
  ).toBeFocused();
  return form;
}

// 只比較防偽 API 的成功狀態，避免權杖進入斷言或輸出。
async function csrf(request: APIRequestContext) {
  // 確認同一 Cookie context 取得了可用的防偽權杖。
  const response = await request.get("/api/auth/csrf");
  expect(response.status()).toBe(200);
  return (await response.json()).token as string;
}

// 透過指定 Cookie context 登入同一業務帳號，僅回傳狀態。
async function apiLogin(
  request: APIRequestContext,
  name: string,
  password: string,
) {
  // 使用該 context 的防偽權杖建立獨立的真實登入。
  const response = await request.post("/api/auth/login", {
    headers: { "X-CSRF-TOKEN": await csrf(request) },
    data: { userName: name, password },
  });
  return response.status();
}

// 以瀏覽器實際寬度及文件寬度確認草稿畫面沒有水平溢出。
async function checkDraftLayout(page: Page, region: Locator) {
  // 驗證專案要求的桌機和手機寬度，最後保留實際套用的 viewport。
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 900 });
    expect(
      await page.evaluate(
        // 讀取瀏覽器實際寬度，避免只相信設定值。
        () => window.innerWidth,
      ),
    ).toBe(width);
    expect(
      await page.evaluate(
        // 文件寬度不可超出 viewport，否則會產生水平捲動。
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    ).toBe(true);
    const box = await region.boundingBox();
    expect(box !== null && box.x >= 0 && box.x + box.width <= width).toBe(true);
  }
}

// 驗證部分金額、原字串驗證、完整編輯、刪除確認及手機版面。
test("草稿逐步保存、精確計算、重開與確認刪除", async ({ page }) => {
  const runId = randomBytes(6).toString("hex");
  const subject = `e2e-draft-${runId}`;

  // 由申請人真實登入並等待本人清單完成讀取。
  await login(page, "applicant");
  const region = await draftRegion(page);
  await expect(
    region.getByRole("heading", { name: "本人請購草稿" }),
  ).toBeVisible();

  // 建立可逐步填寫的新草稿，並驗證標籤與鍵盤焦點入口。
  await region.getByRole("button", { name: "新增草稿", exact: true }).click();
  const newForm = draftEditor(region);
  await expect(
    newForm.getByRole("heading", { name: "新請購草稿" }),
  ).toBeFocused();
  await expect(newForm.getByLabel("主旨", { exact: true })).toBeVisible();
  await expect(
    newForm.getByRole("textbox", { name: "整單用途", exact: true }),
  ).toBeVisible();
  await newForm.getByLabel("主旨", { exact: true }).fill(subject);
  await region.getByRole("button", { name: "新增明細", exact: true }).click();
  const firstLine = newForm.getByRole("group", { name: "明細 1" });
  await expect(firstLine.getByLabel("品名", { exact: true })).toBeVisible();
  await expect(firstLine.getByLabel("單位", { exact: true })).toBeVisible();
  await expect(firstLine.getByLabel("數量", { exact: true })).toBeVisible();
  const price = firstLine.getByLabel("預估單價（TWD）", { exact: true });
  await firstLine.getByLabel("數量", { exact: true }).fill("3");
  await price.fill("0.290");

  // 非法尾零必須保留原值，顯示驗證錯誤且不得送出任何建立請求。
  let createPosts = 0;
  page.on(
    "request",
    // 只計算草稿建立 POST，不保留 URL 參數或請求內容。
    (request) => {
      if (
        request.method() === "POST" &&
        new URL(request.url()).pathname === "/api/requisitions/drafts"
      )
        createPosts++;
    },
  );
  await newForm.getByRole("button", { name: "保存草稿", exact: true }).click();
  await expect(region.getByRole("alert")).toContainText("最多兩位小數");
  await expect(price).toHaveValue("0.290");
  expect(createPosts).toBe(0);

  // 修正為合法單價後，確認部分 TWD 小計並保存不完整草稿。
  await price.fill("0.29");
  await expect(
    newForm.getByText("預估申請金額（部分）：TWD 0.87", { exact: true }),
  ).toBeVisible();
  await newForm.getByRole("button", { name: "保存草稿", exact: true }).click();
  await expect(region.getByRole("status")).toHaveText("草稿已保存。");
  expect(createPosts).toBe(1);

  // 以實際手機與桌機寬度檢查版面，並驗證清單返回及重新開啟。
  await checkDraftLayout(page, region);
  await newForm
    .getByRole("button", { name: "返回草稿清單", exact: true })
    .click();
  const partialForm = await openDraft(region, subject);
  const reopenedFirstLine = partialForm.getByRole("group", { name: "明細 1" });
  await expect(
    partialForm.getByText("預估申請金額（部分）：TWD 0.87", { exact: true }),
  ).toBeVisible();
  await expect(
    reopenedFirstLine.getByLabel("數量", { exact: true }),
  ).toHaveValue("3");
  await expect(
    reopenedFirstLine.getByLabel("預估單價（TWD）", { exact: true }),
  ).toHaveValue("0.29");

  // 補齊必填內容並新增一列，驗證精確金額合計 0.20 + 0.87。
  await partialForm
    .getByRole("textbox", { name: "整單用途", exact: true })
    .fill("展示採購需求");
  await reopenedFirstLine
    .getByLabel("品名", { exact: true })
    .fill("測試筆記本");
  await reopenedFirstLine.getByLabel("單位", { exact: true }).fill("本");
  await reopenedFirstLine.getByLabel("數量", { exact: true }).fill("2");
  await reopenedFirstLine
    .getByLabel("預估單價（TWD）", { exact: true })
    .fill("0.10");
  await region.getByRole("button", { name: "新增明細", exact: true }).click();
  const secondLine = partialForm.getByRole("group", { name: "明細 2" });
  await secondLine.getByLabel("品名", { exact: true }).fill("測試卡片");
  await secondLine.getByLabel("單位", { exact: true }).fill("張");
  await secondLine.getByLabel("數量", { exact: true }).fill("3");
  await secondLine.getByLabel("預估單價（TWD）", { exact: true }).fill("0.29");
  await expect(
    partialForm.getByText("預估申請金額：TWD 1.07", { exact: true }),
  ).toBeVisible();

  // 更新後返回清單再讀回，確認伺服器保存了完整欄位及金額。
  await partialForm
    .getByRole("button", { name: "保存草稿", exact: true })
    .click();
  await expect(region.getByRole("status")).toHaveText("草稿已保存。");
  await partialForm
    .getByRole("button", { name: "返回草稿清單", exact: true })
    .click();
  const completeForm = await openDraft(region, subject);
  await expect(completeForm.getByLabel("主旨", { exact: true })).toHaveValue(
    subject,
  );
  await expect(
    completeForm.getByRole("textbox", { name: "整單用途", exact: true }),
  ).toHaveValue("展示採購需求");
  await expect(
    completeForm.getByText("預估申請金額：TWD 1.07", { exact: true }),
  ).toBeVisible();
  await expect(
    completeForm.getByRole("group", { name: "明細 2" }),
  ).toBeVisible();

  // 取消確認時保持草稿；再次確認後刪除，並於重新載入後核對清單。
  let deletePosts = 0;
  page.on(
    "request",
    // 只計算草稿刪除寫入，不保存識別或 request body。
    (request) => {
      if (
        request.method() === "POST" &&
        new URL(request.url()).pathname.endsWith("/delete")
      )
        deletePosts++;
    },
  );
  page.once(
    "dialog",
    // 先取消原生確認，驗證草稿仍留在表單與清單。
    async (dialog) => {
      expect(dialog.type()).toBe("confirm");
      await dialog.dismiss();
    },
  );
  await completeForm
    .getByRole("button", { name: "刪除草稿", exact: true })
    .click();
  await expect(completeForm).toBeVisible();
  await expect(
    region.getByRole("listitem").filter({ hasText: subject }),
  ).toHaveCount(1);
  expect(deletePosts).toBe(0);

  page.once(
    "dialog",
    // 只有使用者第二次明確接受後才允許刪除請求。
    async (dialog) => {
      expect(dialog.type()).toBe("confirm");
      await dialog.accept();
    },
  );
  await completeForm
    .getByRole("button", { name: "刪除草稿", exact: true })
    .click();
  await expect(region.getByRole("status")).toHaveText("草稿已刪除。");
  await expect(
    region.getByRole("listitem").filter({ hasText: subject }),
  ).toHaveCount(0);
  expect(deletePosts).toBe(1);
  await page.reload();
  const reloadedRegion = await draftRegion(page);
  await expect(
    reloadedRegion.getByRole("listitem").filter({ hasText: subject }),
  ).toHaveCount(0);
});

// 驗證第二個本人工作階段收到真實版本衝突並手動讀回勝者。
test("過期草稿保存顯示衝突、保留輸入並讀回目前版本", async ({
  page,
  browser,
}) => {
  const runId = randomBytes(6).toString("hex");
  const subject = `e2e-conflict-${runId}`;
  const winnerSubject = `${subject}-winner`;
  const loserSubject = `${subject}-stale`;
  const laterPurpose = `e2e-recovered-${runId}`;

  // 第一個工作階段建立獨有草稿並保存可重開的初始版本。
  await login(page, "applicant");
  const firstRegion = await draftRegion(page);
  await firstRegion
    .getByRole("button", { name: "新增草稿", exact: true })
    .click();
  const firstNewForm = draftEditor(firstRegion);
  await firstNewForm.getByLabel("主旨", { exact: true }).fill(subject);
  await firstNewForm
    .getByRole("textbox", { name: "整單用途", exact: true })
    .fill("初始版本");
  await firstRegion
    .getByRole("button", { name: "新增明細", exact: true })
    .click();
  const firstLine = firstNewForm.getByRole("group", { name: "明細 1" });
  await firstLine.getByLabel("品名", { exact: true }).fill("版本測試品");
  await firstLine.getByLabel("單位", { exact: true }).fill("件");
  await firstLine.getByLabel("數量", { exact: true }).fill("1");
  await firstLine.getByLabel("預估單價（TWD）", { exact: true }).fill("1.00");
  await firstNewForm
    .getByRole("button", { name: "保存草稿", exact: true })
    .click();
  await expect(firstRegion.getByRole("status")).toHaveText("草稿已保存。");
  await firstNewForm
    .getByRole("button", { name: "返回草稿清單", exact: true })
    .click();

  // 第二個 Cookie context 用相同本人登入，讀取相同的舊版本。
  const secondContext = await browser.newContext({
    baseURL: process.env.BASE_URL,
  });
  const secondPage = await secondContext.newPage();
  monitorRuntimeErrors(secondPage);
  try {
    // 以真實登入 API 建立獨立 session，再等待其草稿清單可用。
    expect(
      await apiLogin(secondContext.request, "applicant", accounts.applicant),
    ).toBe(204);
    await secondPage.goto("/");
    const secondRegion = await draftRegion(secondPage);
    const staleForm = await openDraft(secondRegion, subject);
    const winnerForm = await openDraft(firstRegion, subject);

    // 第一個工作階段先保存勝者，另一表單仍保留已讀取的舊版本。
    await winnerForm.getByLabel("主旨", { exact: true }).fill(winnerSubject);
    await winnerForm
      .getByRole("button", { name: "保存草稿", exact: true })
      .click();
    await expect(firstRegion.getByRole("status")).toHaveText("草稿已保存。");

    // 第二個工作階段送出過期版本，記錄唯一 POST 及輸入保留情形。
    let stalePosts = 0;
    secondPage.on(
      "request",
      // 只計算草稿寫入次數，不保存識別、標頭或請求內容。
      (request) => {
        if (
          request.method() === "POST" &&
          new URL(request.url()).pathname.startsWith(
            "/api/requisitions/drafts/",
          )
        )
          stalePosts++;
      },
    );
    await staleForm.getByLabel("主旨", { exact: true }).fill(loserSubject);
    const [staleResponse] = await Promise.all([
      secondPage.waitForResponse(
        // 鎖定第二個工作階段的真實草稿更新回應。
        (response) =>
          response.request().method() === "POST" &&
          /^\/api\/requisitions\/drafts\/[^/]+$/.test(
            new URL(response.url()).pathname,
          ),
      ),
      staleForm.getByRole("button", { name: "保存草稿", exact: true }).click(),
    ]);
    expect(staleResponse.status()).toBe(409);
    await expect(secondRegion.getByRole("alert")).toHaveText(
      "草稿已變更，請重新讀取目前草稿。",
    );
    await expect(staleForm.getByLabel("主旨", { exact: true })).toHaveValue(
      loserSubject,
    );
    await expect(staleForm.getByLabel("主旨", { exact: true })).toBeDisabled();
    await expect(
      staleForm.getByRole("button", { name: "保存草稿", exact: true }),
    ).toBeDisabled();
    expect(stalePosts).toBe(1);

    // 使用者明確讀回勝者版本後，舊輸入解除阻擋且不會重送 POST。
    await staleForm
      .getByRole("button", { name: "重新讀取目前草稿", exact: true })
      .click();
    await expect(staleForm.getByLabel("主旨", { exact: true })).toHaveValue(
      winnerSubject,
    );
    expect(stalePosts).toBe(1);

    // 以讀回版本再次編輯保存，返回清單重開確認後續寫入可完成。
    await staleForm
      .getByRole("textbox", { name: "整單用途", exact: true })
      .fill(laterPurpose);
    await staleForm
      .getByRole("button", { name: "保存草稿", exact: true })
      .click();
    await expect(secondRegion.getByRole("status")).toHaveText("草稿已保存。");
    await staleForm
      .getByRole("button", { name: "返回草稿清單", exact: true })
      .click();
    const recoveredForm = await openDraft(secondRegion, winnerSubject);
    await expect(
      recoveredForm.getByRole("textbox", { name: "整單用途", exact: true }),
    ).toHaveValue(laterPurpose);
    expect(stalePosts).toBe(2);
  } finally {
    // 關閉本案例的第二個 session，不清理或重設任何資料。
    await secondContext.close();
  }
});

// 驗證真實登出後舊頁面保存收到 401，身份復原不保留草稿輸入。
test("失效 session 復原時清除舊草稿並隔離不同業務帳號", async ({ page }) => {
  const runId = randomBytes(6).toString("hex");
  const subject = `e2e-session-${runId}`;
  const savedPurpose = `saved-${runId}`;

  // 由原申請人建立並保存一筆草稿，之後再修改未保存內容。
  await login(page, "applicant");
  const region = await draftRegion(page);
  await region.getByRole("button", { name: "新增草稿", exact: true }).click();
  const newForm = draftEditor(region);
  await newForm.getByLabel("主旨", { exact: true }).fill(subject);
  await newForm
    .getByRole("textbox", { name: "整單用途", exact: true })
    .fill(savedPurpose);
  await region.getByRole("button", { name: "新增明細", exact: true }).click();
  const line = newForm.getByRole("group", { name: "明細 1" });
  await line.getByLabel("品名", { exact: true }).fill("登入撤銷測試品");
  await line.getByLabel("單位", { exact: true }).fill("件");
  await line.getByLabel("數量", { exact: true }).fill("1");
  await line.getByLabel("預估單價（TWD）", { exact: true }).fill("2.00");
  await newForm.getByRole("button", { name: "保存草稿", exact: true }).click();
  await expect(region.getByRole("status")).toHaveText("草稿已保存。");
  await newForm
    .getByRole("textbox", { name: "整單用途", exact: true })
    .fill("尚未保存的舊頁輸入");

  // 從相同 Cookie context 以真實登出 API 撤銷 session，再提交一次 UI 保存。
  const logout = await page.request.post("/api/auth/logout", {
    headers: { "X-CSRF-TOKEN": await csrf(page.request) },
  });
  expect(logout.status()).toBe(204);
  const [rejectedSave] = await Promise.all([
    page.waitForResponse(
      // 只等這筆既有草稿的保存回應，不匹配清單或單筆 GET。
      (response) =>
        response.request().method() === "POST" &&
        /^\/api\/requisitions\/drafts\/[^/]+$/.test(
          new URL(response.url()).pathname,
        ),
    ),
    newForm.getByRole("button", { name: "保存草稿", exact: true }).click(),
  ]);
  expect(rejectedSave.status()).toBe(401);

  // 確認失效處理卸載草稿表單與本人區域，再由使用者手動重驗身份。
  await expect(page.getByRole("region", { name: "本人請購草稿" })).toHaveCount(
    0,
  );
  await expect(
    page.getByRole("heading", { name: "請先確認登入狀態" }),
  ).toBeVisible();
  await expect(
    page.getByRole("textbox", { name: "整單用途", exact: true }),
  ).toHaveCount(0);
  await page.getByRole("button", { name: "重新確認身分", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "登入", exact: true }),
  ).toBeVisible();

  // 另一個業務帳號登入後不能看到原申請人的唯一草稿。
  await login(page, "manager");
  const otherRegion = await draftRegion(page);
  await expect(
    otherRegion.getByRole("listitem").filter({ hasText: subject }),
  ).toHaveCount(0);

  // 以管理者明確登出，再由原申請人重新登入並讀回先前已保存內容。
  await page.getByRole("button", { name: "登出", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "登入", exact: true }),
  ).toBeVisible();
  await login(page, "applicant");
  const recoveredRegion = await draftRegion(page);
  const recoveredForm = await openDraft(recoveredRegion, subject);
  await expect(
    recoveredForm.getByRole("textbox", { name: "整單用途", exact: true }),
  ).toHaveValue(savedPurpose);
});

// 共用 Cookie 在另一分頁切換帳號時，舊畫面必須先卸載資料再確認身分。
test("共用 Cookie 切換業務帳號後，舊分頁須重新確認身分才可讀寫", async ({
  page,
  context,
}) => {
  const runId = randomBytes(6).toString("hex");
  const applicantSubject = `e2e-shared-cookie-applicant-${runId}`;
  const managerSubject = `e2e-shared-cookie-manager-${runId}`;
  const savedPurpose = `e2e-shared-cookie-saved-${runId}`;
  const identity = page.getByRole("region", { name: "目前身分" });

  // 第一個分頁保存申請人草稿，保留帶有舊身分的已開啟編輯器。
  await login(page, "applicant");
  const oldRegion = await draftRegion(page);
  await oldRegion
    .getByRole("button", { name: "新增草稿", exact: true })
    .click();
  const oldForm = draftEditor(oldRegion);
  await oldForm.getByLabel("主旨", { exact: true }).fill(applicantSubject);
  await oldForm.getByRole("button", { name: "保存草稿", exact: true }).click();
  await expect(oldRegion.getByRole("status")).toHaveText("草稿已保存。");

  // 第二個分頁使用同一組瀏覽器 Cookie，以真實 API 登出並改用主管登入。
  const otherPage = await context.newPage();
  monitorRuntimeErrors(otherPage);
  const otherRequest = otherPage.request;
  const logout = await otherRequest.post("/api/auth/logout", {
    headers: { "X-CSRF-TOKEN": await csrf(otherRequest) },
  });
  expect(logout.status()).toBe(204);
  expect(await apiLogin(otherRequest, "manager", accounts.manager)).toBe(204);

  // 以主管的目前工作階段建立可辨識草稿，避免空清單掩蓋跨帳號混用。
  const created = await otherRequest.post("/api/requisitions/drafts", {
    headers: { "X-CSRF-TOKEN": await csrf(otherRequest) },
    data: { subject: managerSubject, purpose: null, lines: [] },
  });
  expect(created.status()).toBe(201);
  await expect(identity.getByText("applicant", { exact: true })).toBeVisible();
  await expect(oldForm.getByLabel("主旨", { exact: true })).toHaveValue(
    applicantSubject,
  );

  // 觸發舊分頁的真實 GET，僅斷言狀態與錯誤碼，避免輸出回應資料或權杖。
  const [blockedRead] = await Promise.all([
    page.waitForResponse(
      // 鎖定清單請求，避免將單筆 GET 或身分查詢誤認為拒絕證據。
      (response) =>
        response.request().method() === "GET" &&
        new URL(response.url()).pathname === "/api/requisitions/drafts",
    ),
    oldRegion
      .getByRole("button", { name: "重新讀取草稿清單", exact: true })
      .click(),
  ]);
  expect(blockedRead.status()).toBe(400);
  expect((await blockedRead.json()).code).toBe("invalid_csrf");

  // 確認兩個帳號的資料皆沒有顯示，直到使用者主動確認目前登入狀態。
  await expect(
    page.getByRole("heading", { name: "請先確認登入狀態" }),
  ).toBeVisible();
  await expect(identity).toHaveCount(0);
  await expect(oldRegion).toHaveCount(0);
  await expect(oldForm).toHaveCount(0);
  await expect(page.getByText(managerSubject, { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "重新確認身分", exact: true }).click();
  const recoveredRegion = await draftRegion(page);
  await expect(identity.getByText("manager", { exact: true })).toBeVisible();
  await expect(
    recoveredRegion.getByText(applicantSubject, { exact: true }),
  ).toHaveCount(0);

  // 目前帳號首次保存須成功，並以重開確認後端確實保存新值。
  const managerForm = await openDraft(recoveredRegion, managerSubject);
  await managerForm
    .getByRole("textbox", { name: "整單用途", exact: true })
    .fill(savedPurpose);
  await managerForm
    .getByRole("button", { name: "保存草稿", exact: true })
    .click();
  await expect(recoveredRegion.getByRole("status")).toHaveText("草稿已保存。");
  await managerForm
    .getByRole("button", { name: "返回草稿清單", exact: true })
    .click();
  const savedForm = await openDraft(recoveredRegion, managerSubject);
  await expect(
    savedForm.getByRole("textbox", { name: "整單用途", exact: true }),
  ).toHaveValue(savedPurpose);
});

// 以真實雙屏障切換驗證身分與權杖必須來自同一請求，不能混用兩次查詢。
test("身分查詢期間共用 Cookie A→B→A，錯配權杖須拒絕草稿並可手動恢復", async ({
  page,
  context,
}) => {
  const subject = `e2e-identity-pair-${randomBytes(6).toString("hex")}`;
  const identity = page.getByRole("region", { name: "目前身分" });

  // A 先保存唯一草稿，讓錯配讀取不能被空清單掩蓋。
  await login(page, "applicant");
  const oldRegion = await draftRegion(page);
  await oldRegion
    .getByRole("button", { name: "新增草稿", exact: true })
    .click();
  const oldForm = draftEditor(oldRegion);
  await oldForm.getByLabel("主旨", { exact: true }).fill(subject);
  await oldForm.getByRole("button", { name: "保存草稿", exact: true }).click();
  await expect(oldRegion.getByRole("status")).toHaveText("草稿已保存。");

  // 第二分頁共用 Cookie；APIRequestContext 的 HTTP 不經舊頁路由，避免屏障遞迴。
  const otherPage = await context.newPage();
  monitorRuntimeErrors(otherPage);
  const otherRequest = otherPage.request;
  let csrfGates = 0;
  let identityGates = 0;

  // 先取得 A 的真實權杖回應，再完成 A→B，最後才把原回應交給舊頁。
  await page.route(
    "**/api/auth/csrf",
    // 屏障只使用一次，後續手動恢復會直接讀取目前登入的實際回應。
    async (route) => {
      // 擷取仍屬 A 的回應，不輸出其中的權杖或 Cookie。
      csrfGates++;
      const response = await route.fetch();
      expect(response.status()).toBe(200);

      // 真實登出 A 並登入 B 後，才讓舊頁接著發出 /me。
      const logout = await otherRequest.post("/api/auth/logout", {
        headers: { "X-CSRF-TOKEN": await csrf(otherRequest) },
      });
      expect(logout.status()).toBe(204);
      expect(await apiLogin(otherRequest, "manager", accounts.manager)).toBe(
        204,
      );
      await route.fulfill({ response });
    },
    { times: 1 },
  );

  // /me 送出時已是 B；確認真實回應只識別 B，再完成 B→A 才放行。
  await page.route(
    "**/api/auth/me",
    // 保留原始身分回應及標頭，讓產品實際決定採用哪一個權杖。
    async (route) => {
      // 擷取使用 B Cookie 的身分回應，只斷言非敏感帳號名稱。
      identityGates++;
      const response = await route.fetch();
      expect(response.status()).toBe(200);
      expect((await response.json()).userName).toBe("manager");

      // 切回 A 後再交付 B 身分，讓後續自動清單使用真實的 A Cookie。
      const logout = await otherRequest.post("/api/auth/logout", {
        headers: { "X-CSRF-TOKEN": await csrf(otherRequest) },
      });
      expect(logout.status()).toBe(204);
      expect(
        await apiLogin(otherRequest, "applicant", accounts.applicant),
      ).toBe(204);
      await route.fulfill({ response });
    },
    { times: 1 },
  );

  // 只觀察身分確認後的第一筆自動清單 GET，不能靠額外重讀修正錯配。
  const [blockedRead] = await Promise.all([
    page.waitForResponse(
      // 鎖定真實清單回應，不攔截或改寫其防偽檢查結果。
      (response) =>
        response.request().method() === "GET" &&
        new URL(response.url()).pathname === "/api/requisitions/drafts",
    ),
    page.getByRole("button", { name: "重新確認身分", exact: true }).click(),
  ]);
  expect(csrfGates).toBe(1);
  expect(identityGates).toBe(1);
  expect(blockedRead.status()).toBe(400);
  expect((await blockedRead.json()).code).toBe("invalid_csrf");

  // 錯配拒絕後卸載身分與業務區域，不顯示 A 的已保存草稿。
  await expect(
    page.getByRole("heading", { name: "請先確認登入狀態" }),
  ).toBeVisible();
  await expect(identity).toHaveCount(0);
  await expect(oldRegion).toHaveCount(0);
  await expect(oldForm).toHaveCount(0);
  await expect(page.getByText(subject, { exact: true })).toHaveCount(0);

  // 兩個屏障耗盡後，明確重驗目前 A，並重開原草稿確認恢復完整。
  await page.getByRole("button", { name: "重新確認身分", exact: true }).click();
  const recoveredRegion = await draftRegion(page);
  await expect(identity.getByText("applicant", { exact: true })).toBeVisible();
  await expect(identity.getByText("manager", { exact: true })).toHaveCount(0);
  const recoveredForm = await openDraft(recoveredRegion, subject);
  await expect(recoveredForm.getByLabel("主旨", { exact: true })).toHaveValue(
    subject,
  );
  expect(csrfGates).toBe(1);
  expect(identityGates).toBe(1);
});

// 驗證建立已提交但回應遺失時不重送，並以清單及詳情人工核對。
test("建立回應遺失後保留草稿並手動讀回已建立資料", async ({ page }) => {
  const runId = randomBytes(6).toString("hex");
  const subject = `e2e-unknown-create-${runId}`;
  const purpose = `unknown-recovery-${runId}`;

  // 由申請人登入並開啟可用的本人草稿畫面。
  await login(page, "applicant");
  const region = await draftRegion(page);
  await region.getByRole("button", { name: "新增草稿", exact: true }).click();
  const form = draftEditor(region);
  await form.getByLabel("主旨", { exact: true }).fill(subject);
  await form
    .getByRole("textbox", { name: "整單用途", exact: true })
    .fill(purpose);
  await region.getByRole("button", { name: "新增明細", exact: true }).click();
  const line = form.getByRole("group", { name: "明細 1" });
  await line.getByLabel("品名", { exact: true }).fill("未知結果核對品");
  await line.getByLabel("單位", { exact: true }).fill("件");
  await line.getByLabel("數量", { exact: true }).fill("1");
  await line.getByLabel("預估單價（TWD）", { exact: true }).fill("3.00");

  // 真實 POST 先抵達 API 並確認 201，再只中斷瀏覽器收到的回應。
  let createPosts = 0;
  let failNextListRead = false;
  await page.route("**/api/requisitions/drafts", async (route) => {
    // 首次清單恢復讀取刻意失敗一次；其餘 GET 仍使用真實 API。
    if (route.request().method() === "GET" && failNextListRead) {
      failNextListRead = false;
      await route.abort("failed");
    } else if (route.request().method() === "POST") {
      createPosts++;
      const response = await route.fetch();
      expect(response.status()).toBe(201);
      failNextListRead = true;
      await route.abort("failed");
    } else {
      await route.continue();
    }
  });
  await form.getByRole("button", { name: "保存草稿", exact: true }).click();
  await expect(region.getByRole("alert")).toContainText("操作結果未確認");
  await expect(region.getByRole("alert")).toContainText("系統不會自動重送");
  await expect(form.getByLabel("主旨", { exact: true })).toHaveValue(subject);
  await expect(form.getByLabel("主旨", { exact: true })).toBeDisabled();
  expect(createPosts).toBe(1);

  // 第一次清單讀取故意失敗，原本的未知結果仍可見且欄位保留。
  await region
    .getByRole("button", { name: "重新讀取草稿清單", exact: true })
    .click();
  const readErrorAlert = region
    .getByRole("alert")
    .filter({ hasText: "無法讀取草稿清單，請重新讀取。" });
  const unknownResultAlert = region
    .getByRole("alert")
    .filter({ hasText: "操作結果未確認" });
  await expect(readErrorAlert).toBeVisible();
  await expect(unknownResultAlert).toBeVisible();
  await expect(form.getByLabel("主旨", { exact: true })).toHaveValue(subject);
  expect(createPosts).toBe(1);

  // 第二次清單 GET 顯示唯一新資料；確認清單不會重送建立 POST。
  await region
    .getByRole("button", { name: "重新讀取草稿清單", exact: true })
    .click();
  const row = region.getByRole("listitem").filter({ hasText: subject });
  await expect(row).toHaveCount(1);
  await expect(
    region.getByRole("button", { name: "已核對清單，繼續編輯", exact: true }),
  ).toBeVisible();
  expect(createPosts).toBe(1);

  // 明確完成清單核對後離開建立表單，以已保存項目重新開啟內容。
  await region
    .getByRole("button", { name: "已核對清單，繼續編輯", exact: true })
    .click();
  await form
    .getByRole("button", { name: "放棄修改並返回", exact: true })
    .click();
  const savedForm = await openDraft(region, subject);
  await expect(
    savedForm.getByRole("textbox", { name: "整單用途", exact: true }),
  ).toHaveValue(purpose);
  await expect(savedForm.getByLabel("主旨", { exact: true })).toHaveValue(
    subject,
  );
  expect(createPosts).toBe(1);
});
