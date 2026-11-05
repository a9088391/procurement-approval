// 簽核測試使用隔離 PostgreSQL 與真實角色；僅攔截傳輸以驗證恢復畫面。
import { randomBytes } from "node:crypto";
import { readFileSync, statSync } from "node:fs";
import {
  expect,
  test,
  type APIRequestContext,
  type Locator,
  type Page,
} from "@playwright/test";
import type { OrganizationDto, RequisitionDetailDto } from "../src/api";

const baseURL = process.env.BASE_URL;

// 與既有瀏覽器套件相同，目標或憑證權限不符時在發出請求前中止。
if (
  process.env.TEST_TARGET !== "procurement-approval-browser" ||
  baseURL !== "http://procurement-web.test:8080" ||
  (statSync("/local").mode & 0o777) !== 0o700 ||
  (statSync("/local/initial-accounts.json").mode & 0o777) !== 0o600
) {
  throw new Error("拒絕未知或未受保護的瀏覽器測試目標。");
}

const demoAccounts: Record<string, string> = JSON.parse(
  readFileSync("/local/initial-accounts.json", "utf8"),
);
if (typeof demoAccounts.admin !== "string" || demoAccounts.admin.length < 12)
  throw new Error("隔離環境的管理帳號 fixture 不完整。");

const runId = randomBytes(6).toString("hex");
const departmentName = `簽核測試-${runId}`;
const roles = ["applicant", "manager", "finance"] as const;
type Role = (typeof roles)[number];
const finalPassword = randomBytes(24).toString("hex");
const runtimeErrors = new Map<Page, number>();

// 以唯一前綴隔離每輪資料，測試毋須重設資料卷或變更預載業務帳號。
function actorName(role: Role) {
  return `e2e-approval-${runId}-${role}`;
}

// 所有頁面的執行錯誤只計數，避免將請求、Cookie 或畫面內容寫進診斷。
function monitor(page: Page) {
  runtimeErrors.set(page, 0);
  page.on("pageerror", () => {
    // 只累計未捕捉的例外，不輸出原始錯誤。
    runtimeErrors.set(page, (runtimeErrors.get(page) ?? 0) + 1);
  });
  page.on("console", (entry) => {
    // 測試刻意使資源請求失敗，其餘 console error 仍須計數。
    if (
      entry.type() === "error" &&
      !entry.text().startsWith("Failed to load resource:")
    )
      runtimeErrors.set(page, (runtimeErrors.get(page) ?? 0) + 1);
  });
}

// 取得該 Cookie context 的防偽權杖，斷言只檢查狀態碼。
async function csrf(request: APIRequestContext) {
  const response = await request.get("/api/auth/csrf");
  expect(response.status()).toBe(200);
  return (await response.json()).token as string;
}

// 以真實 API 建立測試前置登入，確認成功且避免憑證進入斷言。
async function apiLogin(
  request: APIRequestContext,
  userName: string,
  password: string,
) {
  const response = await request.post("/api/auth/login", {
    headers: { "X-CSRF-TOKEN": await csrf(request) },
    data: { userName, password },
  });
  expect(response.status()).toBe(204);
}

// 由真實管理入口準備互異的三人與部門，不依賴既有資料卷是否已跑過本套件。
test.beforeAll(async ({ playwright }) => {
  // 等待實際網站可用再建立資料，避免以固定延遲猜測應用啟動時間。
  const admin = await playwright.request.newContext({ baseURL });
  try {
    await expect
      .poll(
        // 輪詢只讀首頁，不對尚未就緒的服務重送寫入。
        async () => {
          try {
            return (await admin.get("/")).status();
          } catch {
            return 0;
          }
        },
        { timeout: 30000 },
      )
      .toBe(200);

    // 新增本輪部門，再讀取系統產生的識別；只使用具有正確資格的新帳號。
    await apiLogin(admin, "admin", demoAccounts.admin);
    const headers = { "X-CSRF-TOKEN": await csrf(admin) };
    const department = await admin.post("/api/admin/departments", {
      headers,
      data: { name: departmentName },
    });
    expect(department.status()).toBe(204);
    const initial = await admin.get("/api/admin/organization");
    expect(initial.status()).toBe(200);
    const firstSnapshot: OrganizationDto = await initial.json();
    const departmentId = firstSnapshot.departments.find(
      // 只取本輪建立的部門，避免選到同一資料卷的歷史 fixture。
      ({ name }) => name === departmentName,
    )?.id;
    expect(Boolean(departmentId)).toBe(true);

    // 使用正常改密碼入口解除臨時憑證限制，不用額外測試 API 或 DB 直寫。
    const temporary = randomBytes(24).toString("hex");
    for (const role of roles) {
      const name = actorName(role);
      const created = await admin.post("/api/admin/accounts", {
        headers,
        data: {
          userName: name,
          password: temporary,
          departmentId,
          isManager: role === "manager",
          isFinance: role === "finance",
        },
      });
      expect(created.status()).toBe(204);
      const account = await playwright.request.newContext({ baseURL });
      try {
        await apiLogin(account, name, temporary);
        const me = await account.get("/api/auth/me");
        expect(me.status()).toBe(200);
        const identity = await me.json();
        const changed = await account.post("/api/auth/change-password", {
          headers: { "X-CSRF-TOKEN": me.headers()["x-csrf-token"] },
          data: {
            currentPassword: temporary,
            newPassword: finalPassword,
            version: identity.version,
          },
        });
        expect(changed.status()).toBe(204);
      } finally {
        await account.dispose();
      }
    }

    // 保存真實管理版本和兩個不同的人選，讓送審以當前設定建立快照。
    const updated = await admin.get("/api/admin/organization");
    expect(updated.status()).toBe(200);
    const { accounts }: OrganizationDto = await updated.json();
    // 每個角色都從本輪設定確認，缺少其中一個便中止，避免用錯人選。
    function configuredActor(role: Role) {
      const account = accounts.find(
        // 使用本輪的唯一名稱，不從其他測試的角色猜測人選。
        ({ userName }) => userName === actorName(role),
      );
      if (!account) throw new Error("簽核測試帳號未完整建立。");
      return account;
    }
    const applicant = configuredActor("applicant");
    const manager = configuredActor("manager");
    const finance = configuredActor("finance");
    const settings = await admin.post(`/api/admin/accounts/${applicant.id}`, {
      headers,
      data: {
        version: applicant.version,
        active: true,
        isManager: false,
        isFinance: false,
        departmentId,
        managerReviewerId: manager.id,
        financeReviewerId: finance.id,
      },
    });
    expect(settings.status()).toBe(204);
  } finally {
    await admin.dispose();
  }
});

// 案例從各自的匿名瀏覽器開始，避免沿用前一案例的工作階段。
test.beforeEach(async ({ page }) => {
  runtimeErrors.clear();
  monitor(page);
  await page.goto("/");
  await expect(page).toHaveTitle(/請購簽核/);
  await expect(
    page.getByRole("heading", { name: "登入", exact: true }),
  ).toBeVisible();
});

// 將未捕捉錯誤視為測試失敗，僅輸出錯誤數。
test.afterEach(() => {
  for (const count of runtimeErrors.values()) expect(count).toBe(0);
});

// 使用真實登入表單並只在瀏覽器內輸入密碼，避免 fill 失敗時輸出秘密。
async function login(page: Page, role: Role) {
  await page.getByLabel("帳號", { exact: true }).fill(actorName(role));
  await page.getByLabel("密碼", { exact: true }).evaluate(
    // 保留實際輸入事件，同時避免密碼出現在 Playwright 動作診斷。
    (element, password) => {
      (element as HTMLInputElement).value = password;
      element.dispatchEvent(new Event("input", { bubbles: true }));
    },
    finalPassword,
  );
  await page.getByRole("button", { name: "登入", exact: true }).click();
  await expect(
    page
      .getByRole("region", { name: "目前身分" })
      .getByText(actorName(role), { exact: true }),
  ).toBeVisible();
}

// 每次附上本案例自己的版本標記，不依賴既有清單件數。
function rowFor(region: Locator, subject: string) {
  return region.getByRole("listitem").filter({ hasText: subject });
}

// 從業務面板進入查閱並等待清單 GET；當前面板切換完成後才使用資料。
async function approvals(page: Page) {
  await page
    .getByRole("button", { name: "請購查閱與簽核", exact: true })
    .click();
  const region = page.getByRole("region", { name: "請購查閱與簽核" });
  await expect(
    region.getByRole("button", { name: "重新讀取請購列表", exact: true }),
  ).toBeEnabled();
  return region;
}

// 只從本案例的唯一清單列進明細，檢查系統單號和鍵盤焦點。
async function openApproval(region: Locator, subject: string) {
  const row = rowFor(region, subject);
  await expect(row).toHaveCount(1);
  const numberButton = row.getByRole("button");
  const number = await numberButton.innerText();
  await numberButton.click();
  await expect(
    region.getByRole("heading", { name: `請購明細 ${number}`, exact: true }),
  ).toBeFocused();
}

// 透過本人草稿表單建立完全符合送審規則的已保存內容。
async function saveThroughUI(page: Page, subject: string, itemName: string) {
  // 先驗證有未保存內容時可以取消切換並保留同一個草稿編輯器。
  const drafts = page.getByRole("region", { name: "本人請購草稿" });
  await drafts.getByRole("button", { name: "新增草稿", exact: true }).click();
  const form = drafts.getByRole("form", { name: "新請購草稿" });
  await form.getByLabel("主旨", { exact: true }).fill(subject);
  page.once("dialog", (dialog) => {
    // 取消離開確認，保留目前輸入。
    void dialog.dismiss();
  });
  await page
    .getByRole("button", { name: "請購查閱與簽核", exact: true })
    .click();
  await expect(form.getByLabel("主旨", { exact: true })).toHaveValue(subject);

  // 填齊所有明細並等待真實 POST 成功，再離開已保存表單。
  await form.getByLabel("整單用途", { exact: true }).fill("補充辦公設備");
  await form.getByRole("button", { name: "新增明細", exact: true }).click();
  const line = form.getByRole("group", { name: "明細 1" });
  await line.getByLabel("品名", { exact: true }).fill(itemName);
  await line.getByLabel("單位", { exact: true }).fill("件");
  await line.getByLabel("數量", { exact: true }).fill("3");
  await line.getByLabel("預估單價（TWD）", { exact: true }).fill("0.29");
  await form.getByRole("button", { name: "保存草稿", exact: true }).click();
  await expect(drafts.getByRole("status")).toHaveText("草稿已保存。");
}

// 恢復類案例先透過真實 API 建立已送審的單，將瀏覽器動作集中於被測的關卡。
async function seedPending(request: APIRequestContext, subject: string) {
  // 用申請人的已配對權杖保存合格草稿，只送回保存所回傳的版本。
  const headers = { "X-CSRF-TOKEN": await csrf(request) };
  const draft = await request.post("/api/requisitions/drafts", {
    headers,
    data: {
      subject,
      purpose: "驗證操作讀回",
      lines: [{ itemName: "測試品項", unit: "件", quantity: 1, unitPrice: 3 }],
    },
  });
  expect(draft.status()).toBe(201);
  const { id, version } = await draft.json();
  const submitted = await request.post(`/api/requisitions/${id}/submit`, {
    headers,
    data: { version },
  });
  expect(submitted.status()).toBe(200);
  const detail: RequisitionDetailDto = await submitted.json();
  expect(detail.status).toBe("manager_pending");
  return detail;
}

// 用原角色確認目前清單範圍、精確金額、長字串斷行與鍵盤焦點。
async function checkLayout(
  page: Page,
  region: Locator,
  longValue: string,
  capture?: string,
) {
  for (const width of [1280, 390]) {
    // 讀取實際 viewport，另比對文字元素本身，避免只量到外層文件。
    await page.setViewportSize({ width, height: 900 });
    expect(
      await page.evaluate(
        // 同時檢查水平捲動和實際瀏覽器寬度。
        () => [innerWidth, document.documentElement.scrollWidth],
      ),
    ).toEqual([width, width]);
    const longText = region.getByText(longValue, { exact: true });
    expect(
      await longText.evaluate(
        // 長字串需在自己的可用寬度內換行。
        (element) => element.scrollWidth <= element.clientWidth,
      ),
    ).toBe(true);
    for (const button of await region.getByRole("button").all()) {
      // 確認每個互動控制項都在有效的水平範圍內。
      const box = await button.boundingBox();
      expect(box !== null && box.x >= 0 && box.x + box.width <= width).toBe(
        true,
      );
    }

    // 僅在本機證據路徑已掛載時截圖，畫面只含合成資料與唯讀操作。
    if (capture && process.env.APPROVAL_EVIDENCE === "1") {
      await region.screenshot({ path: `/evidence/${capture}-${width}.png` });
    }
  }
}

// 確認每次真實 POST 之後的狀態與歷程，防止只靠成功提示判定完成。
async function history(region: Locator, events: string[]) {
  const list = region.getByRole("list", { name: "操作歷程" });
  const items = list.getByRole("listitem");
  await expect(items).toHaveCount(events.length);
  for (const [index, event] of events.entries()) {
    // 核對歷程順序、關卡、操作者和實際寫入的時間。
    const item = items.nth(index);
    await expect(item).toContainText(event);
    await expect(item.locator("time")).toHaveAttribute("datetime", /T/);
  }
}

// 建立另一個 Cookie context 的真實登入，保證獨立簽核分頁不會共用認證。
async function openActor(page: Page, role: Role) {
  monitor(page);
  await page.goto("/");
  await login(page, role);
  return approvals(page);
}

// 驗證兩種結案與角色操作，也檢查瀏覽器保留管理 API 接受的原始部門名稱。
test("申請人、主管、財務依序完成簽核，保留 U+FEFF 部門快照及駁回歷程", async ({
  page,
  browser,
  request,
}) => {
  const subject = `兩關核准-${runId}-${"長主旨".repeat(58)}`;
  const itemName = "LongItem".repeat(24);
  const rejectedSubject = `主管駁回-${runId}`;
  const applicantEvent = `送出簽核 · ${actorName("applicant")}`;
  const managerEvent = `主管核准 · ${actorName("manager")}`;
  const financeEvent = `財務核准 · ${actorName("finance")}`;
  const roundName = "\uFEFF";

  // 檢查原始 DOM 文字，避免查詢工具的空白正規化掩蓋 U+FEFF 是否保留。
  async function expectRoundName(region: Locator) {
    const label = region.getByText("送審部門", { exact: true });
    const name = await label
      .locator("xpath=following-sibling::dd[1]")
      .textContent();
    expect(name).toBe(roundName);
  }

  // 只修改本輪專屬部門，證明送到管理 API 的是實際 U+FEFF 且原值可讀回。
  await apiLogin(request, "admin", demoAccounts.admin);
  const settings = await request.get("/api/admin/organization");
  expect(settings.status()).toBe(200);
  const { departments }: OrganizationDto = await settings.json();
  const department = departments.find(
    // 以本輪唯一名稱取得待更新的部門及版本。
    ({ name }) => name === departmentName,
  );
  if (!department) throw new Error("找不到本輪簽核測試部門。");
  const body = JSON.stringify({
    name: roundName,
    active: department.active,
    version: department.version,
  });
  expect(Buffer.from(body).includes(Buffer.from([0xef, 0xbb, 0xbf]))).toBe(
    true,
  );
  const rename = await request.post(`/api/admin/departments/${department.id}`, {
    headers: {
      "X-CSRF-TOKEN": await csrf(request),
      "Content-Type": "application/json",
    },
    data: body,
  });
  expect(rename.status()).toBe(204);
  const saved = await request.get("/api/admin/organization");
  expect(saved.status()).toBe(200);
  const updated: OrganizationDto = await saved.json();
  const savedName = updated.departments.find(
    // 以識別讀回同一個已更名部門。
    ({ id }) => id === department.id,
  )?.name;
  expect(savedName).toHaveLength(1);
  expect(savedName?.codePointAt(0)).toBe(0xfeff);

  // 申請人以鍵盤從標題進入列表操作，重開保存明細後才送審。
  await login(page, "applicant");
  await saveThroughUI(page, subject, itemName);
  const applicant = await approvals(page);
  // 等草稿列實際載入才按 Tab，避免清單 GET 仍在進行時跳過停用的按鈕。
  await expect(rowFor(applicant, subject)).toHaveCount(1);
  const reloadList = applicant.getByRole("button", {
    name: "重新讀取請購列表",
  });
  await expect(reloadList).toBeEnabled();
  await expect(
    applicant.getByRole("heading", { name: "請購查閱與簽核", exact: true }),
  ).toBeFocused();
  await page.keyboard.press("Tab");
  await expect(reloadList).toBeFocused();
  await checkLayout(page, applicant, subject);
  await openApproval(applicant, subject);
  await expect(applicant.getByText("草稿", { exact: true })).toBeVisible();
  await expect(
    applicant.getByText("TWD 0.87", { exact: true }).first(),
  ).toBeVisible();
  await expect(
    applicant.getByRole("button", { name: "核准", exact: true }),
  ).toHaveCount(0);
  const [submitted] = await Promise.all([
    page.waitForResponse(
      // 核對畫面操作的真實送審回應，避免把查詢回應視為成功。
      (response) =>
        response.url().endsWith("/submit") &&
        response.request().method() === "POST",
    ),
    applicant.getByRole("button", { name: "送出簽核", exact: true }).click(),
  ]);
  expect(submitted.status()).toBe(200);
  const submittedDetail: RequisitionDetailDto = await submitted.json();
  expect(submittedDetail.round?.department.name).toBe(roundName);
  await expect(applicant.getByText("主管待審", { exact: true })).toBeVisible();
  await expect(applicant.getByRole("status")).toHaveText("已送出簽核。");
  await expect(
    applicant.getByRole("button", { name: "重新讀取結果", exact: true }),
  ).toHaveCount(0);
  await expectRoundName(applicant);
  await expect(
    applicant.getByText(actorName("manager"), { exact: true }),
  ).toBeVisible();
  await expect(
    applicant.getByText(actorName("finance"), { exact: true }),
  ).toBeVisible();
  await expect(applicant.getByText("第 1 輪", { exact: true })).toBeVisible();
  await history(applicant, [applicantEvent]);
  await checkLayout(page, applicant, itemName, "approval-detail");

  // 財務在主管作決定前看不到案件，主管由指定帳號完成第一關。
  const managerContext = await browser.newContext();
  const financeContext = await browser.newContext();
  try {
    const finance = await openActor(await financeContext.newPage(), "finance");
    await expect(rowFor(finance, subject)).toHaveCount(0);
    const manager = await openActor(await managerContext.newPage(), "manager");
    await expect(rowFor(manager, subject)).toContainText("待您簽核");
    await openApproval(manager, subject);
    await expectRoundName(manager);
    await expect(
      manager.getByRole("button", { name: "駁回", exact: true }),
    ).toBeEnabled();
    await manager.getByRole("button", { name: "核准", exact: true }).click();
    await expect(manager.getByText("財務待審", { exact: true })).toBeVisible();
    await expect(
      manager.getByRole("button", { name: "核准", exact: true }),
    ).toHaveCount(0);
    await history(manager, [applicantEvent, managerEvent]);

    // 財務讀回才可查閱及審核；最後由申請人重新開啟已結案資料。
    await finance.getByRole("button", { name: "重新讀取請購列表" }).click();
    await openApproval(finance, subject);
    await finance.getByRole("button", { name: "核准", exact: true }).click();
    await expect(finance.getByText("核准結案", { exact: true })).toBeVisible();
    await expect(
      finance.getByRole("button", { name: "駁回", exact: true }),
    ).toHaveCount(0);
    await history(finance, [applicantEvent, managerEvent, financeEvent]);
    await applicant.getByRole("button", { name: "返回請購列表" }).click();
    const [reopened] = await Promise.all([
      page.waitForResponse(
        // 再開時確認 GET 仍讀的是原單，而非沿用 POST 的畫面資料。
        (response) =>
          response.url().endsWith(`/${submittedDetail.id}`) &&
          response.request().method() === "GET",
      ),
      openApproval(applicant, subject),
    ]);
    expect(reopened.status()).toBe(200);
    const latest: RequisitionDetailDto = await reopened.json();
    expect(latest.round?.department.name).toBe(roundName);
    await expect(
      applicant.getByText("核准結案", { exact: true }),
    ).toBeVisible();
    await expectRoundName(applicant);
    await history(applicant, [applicantEvent, managerEvent, financeEvent]);

    // 第二案由主管真實駁回，申請人仍讀得到歷程，已送審單不再是可編輯草稿。
    await seedPending(page.request, rejectedSubject);
    await manager.getByRole("button", { name: "返回請購列表" }).click();
    await openApproval(manager, rejectedSubject);
    await manager.getByRole("button", { name: "駁回", exact: true }).click();
    await expect(manager.getByText("駁回結案", { exact: true })).toBeVisible();
    await expect(
      manager.getByRole("button", { name: "核准", exact: true }),
    ).toHaveCount(0);
    await applicant.getByRole("button", { name: "返回請購列表" }).click();
    await openApproval(applicant, rejectedSubject);
    await expect(
      applicant.getByText("駁回結案", { exact: true }),
    ).toBeVisible();
    await history(applicant, [
      applicantEvent,
      `主管駁回 · ${actorName("manager")}`,
    ]);
    await expect(
      applicant.getByRole("button", { name: "送出簽核", exact: true }),
    ).toHaveCount(0);
    await page
      .getByRole("button", { name: "本人請購草稿", exact: true })
      .click();
    const drafts = page.getByRole("region", { name: "本人請購草稿" });
    await expect(
      drafts.getByRole("button", { name: "新增草稿" }),
    ).toBeEnabled();
    await expect(rowFor(drafts, subject)).toHaveCount(0);
    await expect(rowFor(drafts, rejectedSubject)).toHaveCount(0);
  } finally {
    await financeContext.close();
    await managerContext.close();
  }
});

// 從不同真實登入更新同一筆版本，確認舊畫面只在 GET 讀回後恢復。
test("另一個工作階段先核准後，舊版本駁回必須讀回，歷程只留下核准", async ({
  page,
  browser,
  playwright,
}) => {
  const subject = `版本競爭-${runId}`;
  const applicant = await playwright.request.newContext({ baseURL });
  const other = await browser.newContext();
  try {
    // 在真資料庫建立待審單，讓兩個不同 Cookie context 各讀取同一版本。
    await apiLogin(applicant, actorName("applicant"), finalPassword);
    const { id } = await seedPending(applicant, subject);
    await login(page, "manager");
    const stale = await approvals(page);
    await openApproval(stale, subject);
    const current = await openActor(await other.newPage(), "manager");
    await openApproval(current, subject);
    await current.getByRole("button", { name: "核准", exact: true }).click();
    await expect(current.getByText("財務待審", { exact: true })).toBeVisible();

    // 只觀察精確的拒絕請求，確認畫面鎖住舊按鈕且不報作成功。
    const [rejected] = await Promise.all([
      page.waitForResponse(
        // 此請求必須是此筆的駁回 POST。
        (response) =>
          response.url().endsWith(`/${id}/reject`) &&
          response.request().method() === "POST",
      ),
      stale.getByRole("button", { name: "駁回", exact: true }).click(),
    ]);
    expect(rejected.status()).toBe(409);
    expect((await rejected.json()).code).toBe("stale_version");
    await expect(stale.getByRole("alert")).toContainText("請先重新讀取結果");
    await expect(
      stale.getByRole("button", { name: "駁回", exact: true }),
    ).toBeDisabled();

    // 使用者主動讀回同筆狀態，舊關卡操作消失且只有一個有效決定。
    await stale
      .getByRole("button", { name: "重新讀取結果", exact: true })
      .click();
    await expect(stale.getByText("財務待審", { exact: true })).toBeVisible();
    await expect(
      stale.getByRole("button", { name: "駁回", exact: true }),
    ).toHaveCount(0);
    await history(stale, [
      `送出簽核 · ${actorName("applicant")}`,
      `主管核准 · ${actorName("manager")}`,
    ]);
  } finally {
    await other.close();
    await applicant.dispose();
  }
});

// 檢查實際防偽拒絕先卸載原帳號內容，再由使用者確認目前身分。
async function confirmAfterBlockedRead(
  page: Page,
  region: Locator,
  id: string,
) {
  const [blocked] = await Promise.all([
    page.waitForResponse(
      // 只觀察要讀回的請購 GET，避免把身分或清單請求誤認為結果。
      (response) =>
        response.url().endsWith(`/${id}`) &&
        response.request().method() === "GET",
    ),
    region.getByRole("button", { name: "重新讀取結果", exact: true }).click(),
  ]);
  expect(blocked.status()).toBe(400);
  expect((await blocked.json()).code).toBe("invalid_csrf");
  await expect(region).toHaveCount(0);
  await expect(page.getByRole("region", { name: "目前身分" })).toHaveCount(0);
  await page.getByRole("button", { name: "重新確認身分", exact: true }).click();
}

// 真正寫入後遺失回應，失敗讀回、切換面板與另一筆成功都不能清掉原待核對結果。
test("回應中斷後保留原筆未知結果，同帳號恢復身分後可讀回且不重送", async ({
  page,
  context,
  playwright,
}) => {
  const subject = `回應遺失-${runId}`;
  const otherSubject = `獨立操作-${runId}`;
  const applicant = await playwright.request.newContext({ baseURL });
  try {
    // 準備兩筆輪到主管的請購，只中斷第一筆寫入回應並讓首次讀回拿到無效版本。
    await apiLogin(applicant, actorName("applicant"), finalPassword);
    const { id } = await seedPending(applicant, subject);
    await seedPending(applicant, otherSubject);
    await login(page, "manager");
    const region = await approvals(page);
    await openApproval(region, subject);
    let writes = 0;
    await page.route(`**/api/requisitions/${id}/approve`, async (route) => {
      // API 確認已提交後，瀏覽器只看到傳輸失敗。
      writes++;
      const committed = await route.fetch();
      expect(committed.status()).toBe(200);
      await route.abort("failed");
    });
    await region.getByRole("button", { name: "核准", exact: true }).click();
    const unknown = region
      .getByRole("alert")
      .filter({ hasText: "操作結果未確認" });
    await expect(unknown).toBeVisible();
    await expect(
      region.getByRole("button", { name: "核准", exact: true }),
    ).toBeDisabled();
    await page.route(
      `**/api/requisitions/${id}`,
      // 即使其餘資料合法，只有可用版本的 GET 才能核對未知結果。
      async (route) => {
        const authorized = await route.fetch();
        expect(authorized.status()).toBe(200);
        const detail: RequisitionDetailDto = await authorized.json();
        await route.fulfill({ status: 200, json: { ...detail, version: " " } });
      },
      { times: 1 },
    );
    await region
      .getByRole("button", { name: "重新讀取結果", exact: true })
      .click();
    await expect(
      region.getByRole("alert").filter({ hasText: "無法讀取請購結果" }),
    ).toBeVisible();
    await expect(unknown).toBeVisible();
    await expect(region.getByText("主管待審", { exact: true })).toBeVisible();

    // 離開後另一筆可照常作決定；已成功提示與前一筆未知結果同時存在。
    await page
      .getByRole("button", { name: "本人請購草稿", exact: true })
      .click();
    await approvals(page);
    await openApproval(region, otherSubject);
    await region.getByRole("button", { name: "駁回", exact: true }).click();
    await expect(region.getByRole("status")).toHaveText("已駁回。");
    await expect(unknown).toBeVisible();

    // 真實防偽 Cookie 失效先卸載舊畫面；相同登入帳號恢復後保留待核對入口。
    await context.clearCookies({ name: "procurement.csrf" });
    await confirmAfterBlockedRead(page, region, id);
    await expect(
      page
        .getByRole("region", { name: "目前身分" })
        .getByText(actorName("manager"), { exact: true }),
    ).toBeVisible();
    await expect(unknown).toBeVisible();
    await region
      .getByRole("button", { name: "重新讀取結果", exact: true })
      .click();
    await expect(region.getByText("財務待審", { exact: true })).toBeVisible();
    await expect(unknown).toHaveCount(0);
    await expect(
      region.getByRole("button", { name: "核准", exact: true }),
    ).toHaveCount(0);
    await history(region, [
      `送出簽核 · ${actorName("applicant")}`,
      `主管核准 · ${actorName("manager")}`,
    ]);
    expect(writes).toBe(1);
  } finally {
    await applicant.dispose();
  }
});

// 已收到 200 後只遺失明細；原帳號可恢復成功提示，換帳號則隔離原資料和標記。
test("已確認寫入與讀回失敗分開，同帳號可恢復，跨分頁換帳號移除舊提示", async ({
  page,
  context,
  playwright,
}) => {
  const firstSubject = `明細損壞-${runId}`;
  const secondSubject = `身分隔離-${runId}`;
  const applicant = await playwright.request.newContext({ baseURL });
  try {
    // 先建立兩筆案件；測試只替換已確定為 200 的回應 body，狀態與資料仍由 API 決定。
    await apiLogin(applicant, actorName("applicant"), finalPassword);
    const first = await seedPending(applicant, firstSubject);
    const second = await seedPending(applicant, secondSubject);
    await login(page, "manager");
    const region = await approvals(page);
    let writes = 0;
    await page.route("**/api/requisitions/*/reject", async (route) => {
      // 保留真實成功狀態，分別模擬合法 JSON 缺欄位與 JSON 語法錯誤。
      writes++;
      const committed = await route.fetch();
      expect(committed.status()).toBe(200);
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: writes === 1 ? "{}" : "{",
      });
    });

    // 已成功且尚無新版明細時，保留成功訊息並阻止舊版再次駁回。
    await openApproval(region, firstSubject);
    await region.getByRole("button", { name: "駁回", exact: true }).click();
    const confirmed = region
      .getByRole("status")
      .filter({ hasText: "目前無法讀取最新狀態" });
    await expect(confirmed).toContainText("已駁回。");
    await expect(
      region.getByRole("button", { name: "駁回", exact: true }),
    ).toBeDisabled();
    await context.clearCookies({ name: "procurement.csrf" });
    await confirmAfterBlockedRead(page, region, first.id);
    await expect(confirmed).toContainText("已駁回。");
    const readback = region.getByRole("button", {
      name: "重新讀取結果",
      exact: true,
    });

    // 欄位不完整、巢狀歷程或狀態錯誤、以及不同單的 GET 都不能結束讀回。
    const detailUrl = `**/api/requisitions/${first.id}`;
    for (const invalid of [
      null,
      { history: [null] },
      { status: "unknown" },
      { id: second.id },
    ]) {
      await page.route(
        detailUrl,
        // 先取得真實明細，只替換本輪要驗證的回應內容。
        async (route) => {
          const authorized = await route.fetch();
          expect(authorized.status()).toBe(200);
          const detail: RequisitionDetailDto = await authorized.json();
          const json = invalid ? { ...detail, ...invalid } : {};
          await route.fulfill({ status: 200, json });
        },
        { times: 1 },
      );
      await readback.click();
      await expect(region.getByRole("alert")).toContainText("無法讀取請購結果");
      await expect(confirmed).toContainText("已駁回。");
      await expect(readback).toBeEnabled();
      await expect(
        region.getByRole("heading", { name: /^請購明細 / }),
      ).toHaveCount(0);
      expect(writes).toBe(1);
    }

    // 合法新版明細須仍是原請購，畫面取得其結案歷程後才清除讀回入口。
    const [read] = await Promise.all([
      page.waitForResponse(
        // 確認本次手動讀回實際使用同筆 GET。
        (response) =>
          response.url().endsWith(`/${first.id}`) &&
          response.request().method() === "GET",
      ),
      readback.click(),
    ]);
    expect(read.status()).toBe(200);
    const latest: RequisitionDetailDto = await read.json();
    expect(latest.id).toBe(first.id);
    expect(latest.version).not.toBe(first.version);
    await expect(region.getByText("駁回結案", { exact: true })).toBeVisible();
    await expect(region.getByRole("status")).toHaveText(
      "已駁回，並讀取最新狀態。",
    );
    await expect(readback).toHaveCount(0);
    await expect(
      region.getByRole("button", { name: "駁回", exact: true }),
    ).toHaveCount(0);
    await history(region, [
      `送出簽核 · ${actorName("applicant")}`,
      `主管駁回 · ${actorName("manager")}`,
    ]);

    // 另一分頁使用同一組 Cookie 換成財務，舊頁的明細 GET 必須被拒絕並清除原帳號提示。
    await region.getByRole("button", { name: "返回請購列表" }).click();
    await openApproval(region, secondSubject);
    await region.getByRole("button", { name: "駁回", exact: true }).click();
    await expect(confirmed).toContainText("已駁回。");
    const secondPage = await context.newPage();
    monitor(secondPage);
    const logout = await secondPage.request.post("/api/auth/logout", {
      headers: { "X-CSRF-TOKEN": await csrf(secondPage.request) },
    });
    expect(logout.status()).toBe(204);
    await apiLogin(secondPage.request, actorName("finance"), finalPassword);
    await confirmAfterBlockedRead(page, region, second.id);
    await expect(
      page
        .getByRole("region", { name: "目前身分" })
        .getByText(actorName("finance"), { exact: true }),
    ).toBeVisible();
    await expect(region).toHaveCount(0);
    await approvals(page);
    await expect(rowFor(region, secondSubject)).toHaveCount(0);
    await expect(confirmed).toHaveCount(0);
    await expect(
      region.getByRole("button", { name: "重新讀取結果", exact: true }),
    ).toHaveCount(0);
    expect(writes).toBe(2);
  } finally {
    await applicant.dispose();
  }
});

// 同一瀏覽器切換成須改密碼的帳號時，真實 403 會先於防偽檢查抵達舊頁。
test("跨分頁換成須改密碼帳號後，簽核 403 移除舊身分與請購明細", async ({
  page,
  context,
  playwright,
}) => {
  const subject = `強制改密碼隔離-${runId}`;
  const temporaryName = `e2e-approval-${runId}-temporary`;
  const password = randomBytes(24).toString("hex");
  const applicant = await playwright.request.newContext({ baseURL });
  const admin = await playwright.request.newContext({ baseURL });
  try {
    // 用真實入口建立待審單與未解除改密碼限制的業務帳號。
    await apiLogin(applicant, actorName("applicant"), finalPassword);
    const original = await seedPending(applicant, subject);
    await apiLogin(admin, "admin", demoAccounts.admin);
    const created = await admin.post("/api/admin/accounts", {
      headers: { "X-CSRF-TOKEN": await csrf(admin) },
      data: {
        userName: temporaryName,
        password,
        departmentId: original.round?.department.id,
        isManager: false,
        isFinance: false,
      },
    });
    expect(created.status()).toBe(204);

    // 主管明細保持開啟，從共用 Cookie 的分頁換成臨時密碼帳號。
    await login(page, "manager");
    const region = await approvals(page);
    await openApproval(region, subject);
    const oldIdentity = page.getByRole("region", { name: "目前身分" });
    await expect(
      oldIdentity.getByText(actorName("manager"), { exact: true }),
    ).toBeVisible();
    const other = await context.newPage();
    monitor(other);
    const logout = await other.request.post("/api/auth/logout", {
      headers: { "X-CSRF-TOKEN": await csrf(other.request) },
    });
    expect(logout.status()).toBe(204);
    await apiLogin(other.request, temporaryName, password);

    // 舊頁 POST 只收到強制改密碼的 403，申請人讀回時仍是原關卡與原版本。
    const [blocked] = await Promise.all([
      page.waitForResponse(
        // 只觀察原單的真實核准請求。
        (response) =>
          response.url().endsWith(`/${original.id}/approve`) &&
          response.request().method() === "POST",
      ),
      region.getByRole("button", { name: "核准", exact: true }).click(),
    ]);
    expect(blocked.status()).toBe(403);
    expect(await blocked.json()).toMatchObject({
      code: "forbidden",
      message: "請先變更臨時密碼。",
    });
    const persisted = await applicant.get(`/api/requisitions/${original.id}`);
    expect(persisted.status()).toBe(200);
    const unchanged: RequisitionDetailDto = await persisted.json();
    expect(unchanged.status).toBe("manager_pending");
    expect(unchanged.version).toBe(original.version);
    expect(unchanged.history).toEqual(original.history);
    expect(unchanged.history.map((event) => event.kind)).toEqual(["submitted"]);

    // 舊畫面卸載後只手動重讀身分，當前帳號仍須先改密碼才能查閱請購。
    await expect(region).toHaveCount(0);
    await expect(oldIdentity).toHaveCount(0);
    await expect(page.getByText(subject, { exact: true })).toHaveCount(0);
    await page
      .getByRole("button", { name: "重新確認身分", exact: true })
      .click();
    await expect(
      oldIdentity.getByText(temporaryName, { exact: true }),
    ).toBeVisible();
    await expect(
      page.getByText("請先變更臨時密碼", { exact: false }),
    ).toBeVisible();
    await expect(
      page.getByRole("navigation", { name: "請購功能" }),
    ).toHaveCount(0);
    await expect(page.getByText(subject, { exact: true })).toHaveCount(0);
  } finally {
    await admin.dispose();
    await applicant.dispose();
  }
});

// 真實撤權後舊分頁必須移除明細；申請人仍可核對原關卡和草稿補正指引。
test("撤掉指派主管角色後舊畫面停止簽核，申請人可見暫停與補正提示", async ({
  page,
  browser,
  playwright,
}) => {
  const pendingSubject = `撤權暫停-${runId}`;
  const completeSubject = `缺少合格人選-${runId}`;
  const incompleteSubject = `草稿未填齊-${runId}`;
  const managerContext = await browser.newContext();
  const admin = await playwright.request.newContext({ baseURL });
  try {
    // 先讓兩個角色都讀到可查閱的原關卡，再由管理員變更即時資格。
    await login(page, "applicant");
    const { id } = await seedPending(page.request, pendingSubject);
    const applicant = await approvals(page);
    await openApproval(applicant, pendingSubject);
    const managerPage = await managerContext.newPage();
    const manager = await openActor(managerPage, "manager");
    await openApproval(manager, pendingSubject);
    await expect(
      manager.getByRole("button", { name: "核准", exact: true }),
    ).toBeEnabled();

    // 只撤掉此輪合成主管的角色；不使用測試專用 API 或資料庫捷徑。
    await apiLogin(admin, "admin", demoAccounts.admin);
    const snapshot = await admin.get("/api/admin/organization");
    expect(snapshot.status()).toBe(200);
    const { accounts }: OrganizationDto = await snapshot.json();
    const account = accounts.find(
      // 本輪的主管識別與版本取自管理回應。
      ({ userName }) => userName === actorName("manager"),
    );
    if (!account) throw new Error("簽核測試主管未完整建立。");
    const revoke = await admin.post(`/api/admin/accounts/${account.id}`, {
      headers: { "X-CSRF-TOKEN": await csrf(admin) },
      data: {
        version: account.version,
        active: account.active,
        isManager: false,
        isFinance: account.isFinance,
        departmentId: account.departmentId,
        managerReviewerId: account.managerReviewerId,
        financeReviewerId: account.financeReviewerId,
      },
    });
    expect(revoke.status()).toBe(204);

    // 仍持舊畫面的主管實際送出時只取得通用 404，原明細及動作隨即清除。
    const [blocked] = await Promise.all([
      managerPage.waitForResponse(
        // 只驗證該筆實際作決定的請求。
        (response) =>
          response.url().endsWith(`/${id}/approve`) &&
          response.request().method() === "POST",
      ),
      manager.getByRole("button", { name: "核准", exact: true }).click(),
    ]);
    expect(blocked.status()).toBe(404);
    expect((await blocked.json()).code).toBe("not_found");
    await expect(manager.getByRole("alert")).toContainText(
      "找不到指定的請購單",
    );
    await expect(
      manager.getByText(pendingSubject, { exact: true }),
    ).toHaveCount(0);
    await expect(
      manager.getByRole("button", { name: "核准", exact: true }),
    ).toHaveCount(0);
    const reload = manager.getByRole("button", { name: "重新讀取請購列表" });
    await reload.click();
    await expect(reload).toBeEnabled();
    await expect(rowFor(manager, pendingSubject)).toHaveCount(0);

    // 申請人仍有權查閱，重讀只顯示暫停與原關卡、原輪次和原歷程。
    await applicant.getByRole("button", { name: "返回請購列表" }).click();
    await openApproval(applicant, pendingSubject);
    await expect(applicant.getByRole("alert")).toContainText(
      "簽核已暫停，保留目前關卡",
    );
    await expect(
      applicant.getByText("主管待審", { exact: true }),
    ).toBeVisible();
    await expect(
      applicant.getByText(actorName("manager"), { exact: true }),
    ).toBeVisible();
    await history(applicant, [`送出簽核 · ${actorName("applicant")}`]);

    // 未填齊與已填齊但缺合格設定各有提示，都沒有送出操作。
    const headers = { "X-CSRF-TOKEN": await csrf(page.request) };
    const incomplete = await page.request.post("/api/requisitions/drafts", {
      headers,
      data: { subject: incompleteSubject, purpose: null, lines: [] },
    });
    expect(incomplete.status()).toBe(201);
    const complete = await page.request.post("/api/requisitions/drafts", {
      headers,
      data: {
        subject: completeSubject,
        purpose: "補正簽核設定",
        lines: [
          { itemName: "測試品項", unit: "件", quantity: 1, unitPrice: 3 },
        ],
      },
    });
    expect(complete.status()).toBe(201);
    await applicant.getByRole("button", { name: "返回請購列表" }).click();
    await openApproval(applicant, incompleteSubject);
    await expect(
      applicant.getByText("請先在本人請購草稿", { exact: false }),
    ).toBeVisible();
    await expect(
      applicant.getByRole("button", { name: "送出簽核", exact: true }),
    ).toHaveCount(0);
    await applicant.getByRole("button", { name: "返回請購列表" }).click();
    await openApproval(applicant, completeSubject);
    await expect(
      applicant.getByText("請管理員確認申請人的部門及預設審核人設定", {
        exact: false,
      }),
    ).toBeVisible();
    await expect(
      applicant.getByRole("button", { name: "送出簽核", exact: true }),
    ).toHaveCount(0);
  } finally {
    await admin.dispose();
    await managerContext.close();
  }
});
