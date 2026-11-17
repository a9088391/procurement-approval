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
import type {
  OrganizationDto,
  ReassignmentListDto,
  RequisitionDetailDto,
} from "../src/api";

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
async function login(page: Page, role: Role | "admin") {
  // 管理登出案例也走同一個登入表單與密碼遮蔽路徑。
  const name = role === "admin" ? "admin" : actorName(role);
  const password = role === "admin" ? demoAccounts.admin : finalPassword;
  await page.getByLabel("帳號", { exact: true }).fill(name);
  await page.getByLabel("密碼", { exact: true }).evaluate(
    // 保留實際輸入事件，同時避免密碼出現在 Playwright 動作診斷。
    (element, password) => {
      (element as HTMLInputElement).value = password;
      element.dispatchEvent(new Event("input", { bubbles: true }));
    },
    password,
  );
  await page.getByRole("button", { name: "登入", exact: true }).click();
  await expect(
    page
      .getByRole("region", { name: "目前身分" })
      .getByText(name, { exact: true }),
  ).toBeVisible();
}

// 每次附上本案例自己的版本標記，不依賴既有清單件數。
function rowFor(region: Locator, subject: string) {
  return region
    .getByRole("row")
    .or(region.getByRole("listitem"))
    .filter({ hasText: subject });
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
  const openButton = row.getByRole("button");
  const number = await openButton.getByText(/^PR-/).innerText();
  await openButton.click();
  await expect(
    region.getByRole("heading", { name: subject, exact: true }),
  ).toBeFocused();
  await expect(region.getByText(number, { exact: true }).first()).toBeVisible();
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
async function seedPending(
  request: APIRequestContext,
  subject: string,
  { quantity, unitPrice } = { quantity: 1, unitPrice: 3 },
) {
  // 用申請人的已配對權杖保存合格草稿，只送回保存所回傳的版本。
  const headers = { "X-CSRF-TOKEN": await csrf(request) };
  const draft = await request.post("/api/requisitions/drafts", {
    headers,
    data: {
      subject,
      purpose: "驗證操作讀回",
      lines: [{ itemName: "測試品項", unit: "件", quantity, unitPrice }],
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
    const controls = await region
      .getByRole("button")
      .or(region.getByRole("combobox"))
      .or(region.getByRole("searchbox"))
      .or(region.getByRole("textbox"))
      .or(region.locator('input[type="date"]'))
      .all();
    for (const control of controls) {
      // 確認每個互動控制項都在有效的水平範圍內。
      const box = await control.boundingBox();
      const meetsLeftBoundary = box !== null && box.x >= 0;
      expect(meetsLeftBoundary && box.x + box.width <= width).toBe(true);
    }

    // 僅在本機證據路徑已掛載時截圖，畫面只含合成資料與唯讀操作。
    if (capture && process.env.APPROVAL_EVIDENCE === "1") {
      await region.screenshot({ path: `/evidence/${capture}-${width}.png` });
    }
  }
}

// 量測完整金額的文字邊界，避免表格可以捲動卻仍蓋住相鄰欄位。
async function checkMoneyFits(page: Page, cells: [Locator, string][]) {
  for (const width of [1280, 390]) {
    // 切到實際桌機及手機寬度，確認只由表格容器承擔水平捲動。
    await page.setViewportSize({ width, height: 900 });
    expect(
      await page.evaluate(
        // 一併驗證 viewport 和頁面內容寬度，避免外層也出現水平溢位。
        () => [innerWidth, document.documentElement.scrollWidth],
      ),
    ).toEqual([width, width]);

    // 先確認金額正確，再比對整格內容的文字邊界，包含列表的箭頭。
    for (const [cell, amount] of cells) {
      await expect(cell).toContainText(amount);
      const { textLeft, textRight, cellLeft, cellRight } = await cell.evaluate(
        // Range 取得文字和行內箭頭的實際位置，元素寬度無法檢出此類重疊。
        (element) => {
          const range = document.createRange();
          range.selectNodeContents(element);
          const text = range.getBoundingClientRect();
          const bounds = element.getBoundingClientRect();
          return {
            textLeft: text.left,
            textRight: text.right,
            cellLeft: bounds.left,
            cellRight: bounds.right,
          };
        },
      );
      expect(textLeft).toBeGreaterThanOrEqual(cellLeft);
      expect(textRight).toBeLessThanOrEqual(cellRight);
    }
  }
}

// 確認每次真實 POST 之後的狀態與歷程，防止只靠成功提示判定完成。
async function history(region: Locator, events: string[]) {
  const list = region.getByRole("list", { name: "操作歷程" });
  const items = list.getByRole("listitem");
  await expect(items).toHaveCount(events.length);
  for (const [index, event] of events.entries()) {
    // 同一事件的關卡、操作者、輪次分行顯示；仍核對事件順序及實際寫入的時間。
    const item = items.nth(index);
    for (const field of event.split(" · ")) {
      await expect(item).toContainText(field);
    }
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

// 經實際篩選與瀏覽器下載核對同一批送審資料，再驗證同源換人後的拒絕與復原。
test("請購統計與 CSV 依同條件查詢與下載，換帳號後卸載舊資料", async ({
  page,
  context,
  playwright,
  request: admin,
}) => {
  const panelName = "請購統計與 CSV";
  const subject = `報表-${runId}-${"LongSubject".repeat(15)}`;
  const pendingSubject = `報表待審-${runId}`;
  const otherSubject = `同名部門核准-${runId}`;
  const draftSubject = `報表草稿-${runId}`;
  const applicant = await playwright.request.newContext({ baseURL });
  const manager = await playwright.request.newContext({ baseURL });
  const finance = await playwright.request.newContext({ baseURL });
  try {
    // 使用正常 API 在原部門建立送審單、待審單及未送審草稿。
    await apiLogin(applicant, actorName("applicant"), finalPassword);
    const submitted = await seedPending(applicant, subject);
    const waiting = await seedPending(applicant, pendingSubject);
    const draft = await applicant.post("/api/requisitions/drafts", {
      headers: { "X-CSRF-TOKEN": await csrf(applicant) },
      data: { subject: draftSubject, lines: [] },
    });
    expect(draft.status()).toBe(201);
    if (!submitted.round) throw new Error("報表測試缺少送審快照。");
    const originalDepartmentId = submitted.round.department.id;

    // 另一部門暫用同名，讓測試能辨識相同名稱、不同部門識別的兩個送審快照。
    await apiLogin(admin, "admin", demoAccounts.admin);
    const adminOptions = { headers: { "X-CSRF-TOKEN": await csrf(admin) } };
    const created = await admin.post("/api/admin/departments", {
      ...adminOptions,
      data: { name: departmentName },
    });
    expect(created.status()).toBe(204);
    const organization = await admin.get("/api/admin/organization");
    expect(organization.status()).toBe(200);
    const { departments }: OrganizationDto = await organization.json();
    const clone = departments.find(
      // 新部門與原部門共用名稱，必須比較識別才能選對。
      ({ id, name }) => id !== originalDepartmentId && name === departmentName,
    );
    if (!clone) throw new Error("找不到報表測試的同名部門。");

    // 每次從正式讀回取得帳號版本及現有設定，只改申請人的部門。
    async function moveApplicant(departmentId: string) {
      const settings = await admin.get("/api/admin/organization");
      expect(settings.status()).toBe(200);
      const { accounts }: OrganizationDto = await settings.json();
      const account = accounts.find(
        // 同一工作程序的合成申請人是唯一變更目標。
        ({ userName }) => userName === actorName("applicant"),
      );
      if (!account) throw new Error("找不到報表測試的申請人。");
      // 回送原設定與版本，讓調部門不改變目前資格或預設審核人。
      const updated = await admin.post(`/api/admin/accounts/${account.id}`, {
        ...adminOptions,
        data: {
          departmentId,
          version: account.version,
          active: account.active,
          isManager: account.isManager,
          isFinance: account.isFinance,
          managerReviewerId: account.managerReviewerId,
          financeReviewerId: account.financeReviewerId,
        },
      });
      expect(updated.status()).toBe(204);
    }

    // 建立另一部門的送審快照後還原申請人歸屬；部門改名也不能覆寫快照。
    await moveApplicant(clone.id);
    let otherSubmitted: RequisitionDetailDto;
    try {
      otherSubmitted = await seedPending(applicant, otherSubject);
    } finally {
      await moveApplicant(originalDepartmentId);
      const renamed = await admin.post(`/api/admin/departments/${clone.id}`, {
        ...adminOptions,
        data: {
          name: `報表同名-${runId}`,
          version: clone.version,
          active: true,
        },
      });
      expect(renamed.status()).toBe(204);
    }

    // 讓兩個同名部門各有一筆核准，避免遺漏部門參數時仍通過篩選測試。
    await apiLogin(manager, actorName("manager"), finalPassword);
    await apiLogin(finance, actorName("finance"), finalPassword);
    const managerHeaders = { "X-CSRF-TOKEN": await csrf(manager) };
    const financeHeaders = { "X-CSRF-TOKEN": await csrf(finance) };
    for (const candidate of [submitted, otherSubmitted]) {
      // 財務只能使用主管核准後取得的版本。
      const path = `/api/requisitions/${candidate.id}/approve`;
      const first = await manager.post(path, {
        headers: managerHeaders,
        data: { version: candidate.version },
      });
      expect(first.status()).toBe(200);
      const second = await finance.post(path, {
        headers: financeHeaders,
        data: { version: (await first.json()).version },
      });
      expect(second.status()).toBe(200);
    }

    // 匿名不能進入報表；登入後仍須經請購面板與離開草稿確認。
    const entry = page.getByRole("button", { name: panelName, exact: true });
    await expect(entry).toHaveCount(0);
    await login(page, "applicant");
    await expect(entry).toBeVisible();
    const drafts = page.getByRole("region", { name: "本人請購草稿" });
    await drafts.getByRole("button", { name: "新增草稿", exact: true }).click();
    const draftInput = drafts.getByLabel("主旨", { exact: true });
    await draftInput.fill("尚未保存的報表測試");
    page.once("dialog", (dialog) => {
      // 取消後應保留已輸入的內容，且沒有掛載報表。
      void dialog.dismiss();
    });
    await entry.click();
    await expect(draftInput).toHaveValue("尚未保存的報表測試");
    const report = page.getByRole("region", { name: panelName });
    await expect(report).toHaveCount(0);
    page.once("dialog", (dialog) => {
      // 第二次明確同意離開草稿，報表才能取得讀取閘門。
      void dialog.accept();
    });
    await entry.click();

    // 確認初次讀取排除草稿，並從報表授權資料提供部門選項。
    const filters = report.getByRole("form", { name: "報表條件" });
    const apply = filters.getByRole("button", { name: "套用條件" });
    const clear = filters.getByRole("button", { name: "清除條件" });
    const exportCsv = report.getByRole("button", { name: "下載 CSV" });
    const rows = report.getByRole("table", { name: "符合條件的請購" });
    const groups = report.getByRole("list", {
      name: "依送審部門與目前狀態統計",
    });
    const department = filters.getByLabel("送審部門");
    await expect(exportCsv).toBeEnabled();
    await expect(rows.locator("tbody tr")).toHaveCount(3);
    await expect(rows.getByRole("columnheader")).toHaveText([
      "單號",
      "主旨",
      "申請人",
      "送審部門",
      "目前狀態",
      "預估金額",
      "最近送審時間",
    ]);
    await expect(rowFor(rows, pendingSubject)).toContainText(waiting.number);
    await expect(rows).not.toContainText(draftSubject);
    await expect(department.locator("option")).toHaveCount(3);
    await expect(department.locator('option[value=""]')).toHaveText("全部部門");
    for (const id of [originalDepartmentId, clone.id]) {
      // 下拉選項與核准群組都可區分同名部門。
      const label = `${departmentName} · ${id}`;
      await expect(department.locator(`option[value="${id}"]`)).toHaveText(
        label,
      );
      await expect(
        groups.getByText(`${label} · 核准結案 · 1 件`, { exact: true }),
      ).toBeVisible();
    }
    await expect(
      report.getByRole("heading", { name: panelName }),
    ).toBeFocused();
    await page.keyboard.press("Tab");
    await expect(filters.getByLabel("起日（臺灣時間）")).toBeFocused();
    const status = filters.getByLabel("目前狀態");
    await expect(status.locator("option")).toHaveText([
      "全部狀態",
      "主管待審",
      "財務待審",
      "核准結案",
      "駁回結案",
    ]);

    // 用送審快照算出臺灣日曆日期，驗證三個篩選入口以及尚未套用時的下載限制。
    const day = new Intl.DateTimeFormat("sv-SE", {
      timeZone: "Asia/Taipei",
    }).format(new Date(submitted.round.submittedAt));
    await filters.getByLabel("起日（臺灣時間）").fill(day);
    await filters.getByLabel("迄日（臺灣時間）").fill(day);
    await department.selectOption(submitted.round.department.id);
    await status.selectOption("approved");
    await expect(exportCsv).toBeDisabled();
    const appliedQuery = {
      scope: "submitted",
      startDate: day,
      endDate: day,
      departmentId: submitted.round.department.id,
      status: "approved",
    };
    const [filteredRows, filteredGroups] = await Promise.all([
      page.waitForResponse(
        // 清單的篩選必須走一般請購 GET 的送審範圍。
        (response) => new URL(response.url()).pathname === "/api/requisitions",
      ),
      page.waitForResponse(
        // 另觀察群組查詢，避免只有一個入口接到篩選條件。
        (response) =>
          new URL(response.url()).pathname === "/api/requisitions/statistics",
      ),
      apply.click(),
    ]);
    // 兩個 GET 都必須實際攜帶同一組篩選參數。
    for (const response of [filteredRows, filteredGroups]) {
      expect(response.status()).toBe(200);
      expect(Object.fromEntries(new URL(response.url()).searchParams)).toEqual(
        appliedQuery,
      );
    }
    await expect(rows.locator("tbody tr")).toHaveCount(1);
    await expect(rowFor(rows, subject)).toContainText(submitted.number);
    await expect(groups.getByRole("listitem")).toHaveCount(1);
    await expect(groups).toContainText(departmentName);
    await expect(groups).toContainText("核准結案");
    await expect(groups).toContainText("1 件");
    await expect(groups).toContainText("已核准的預估申請金額");
    await expect(groups).toContainText("TWD 3.00");

    // 由 Chromium 讀回真正下載的位元組，核對 BOM、欄位與唯一資料列。
    const [download, csvResponse] = await Promise.all([
      page.waitForEvent("download"),
      page.waitForResponse(
        // 核對實際下載 GET 與畫面已套用的篩選一致。
        (response) =>
          new URL(response.url()).pathname === "/api/requisitions/csv",
      ),
      exportCsv.click(),
    ]);
    expect(csvResponse.status()).toBe(200);
    expect(Object.fromEntries(new URL(csvResponse.url()).searchParams)).toEqual(
      appliedQuery,
    );
    expect(csvResponse.headers()["content-type"]).toBe(
      "text/csv; charset=utf-8",
    );
    expect(download.suggestedFilename()).toBe("requisitions.csv");
    const downloadPath = await download.path();
    if (!downloadPath) throw new Error("瀏覽器未保存報表 CSV。");
    const bytes = readFileSync(downloadPath);
    expect(bytes.subarray(0, 3)).toEqual(Buffer.from([0xef, 0xbb, 0xbf]));
    // 跳過已驗證的 BOM，再逐列核對 CSV 的內容及結尾。
    const csvText = bytes.subarray(3).toString("utf8");
    expect(csvText.split("\r\n")).toEqual([
      "單號,主旨,申請人顯示名稱,送審部門,目前狀態,TWD 預估金額,最近送審時間",
      expect.stringMatching(
        new RegExp(
          `^${submitted.number},"${subject}","${actorName("applicant")}","${departmentName}",核准結案,3\\.00,${day}T.+\\+08:00$`,
        ),
      ),
      "",
    ]);
    // 瀏覽器開始下載後，等相同條件的清單及統計重新讀取完成再檢查版面。
    await expect(exportCsv).toBeEnabled();
    await checkLayout(page, report, subject, "ticket07-report");
    // 兩種 viewport 都保持搜尋條件、請購表格、分組統計的閱讀順序。
    for (const width of [1280, 390]) {
      await page.setViewportSize({ width, height: 900 });
      const [filtersBox, rowsBox, groupsHeadingBox] = await Promise.all([
        filters.boundingBox(),
        rows.boundingBox(),
        report
          .getByRole("heading", { name: "依送審部門與目前狀態統計" })
          .boundingBox(),
      ]);
      expect(
        filtersBox !== null &&
          rowsBox !== null &&
          groupsHeadingBox !== null &&
          filtersBox.y + filtersBox.height <= rowsBox.y &&
          rowsBox.y + rowsBox.height <= groupsHeadingBox.y,
      ).toBe(true);
    }

    // 驗證倒置日期與不符日期；清除後恢復可見資料，傳輸失敗時須移除舊結果並允許重讀。
    const from = filters.getByLabel("起日（臺灣時間）");
    const to = filters.getByLabel("迄日（臺灣時間）");
    await from.fill("2099-01-01");
    await apply.click();
    await expect(report.getByRole("alert")).toContainText("起日不得晚於迄日");
    await expect(exportCsv).toBeDisabled();
    await to.fill("2099-01-01");
    await apply.click();
    await expect(report.getByText("沒有符合條件的請購。")).toBeVisible();
    await expect(report.getByText("沒有符合條件的統計。")).toBeVisible();
    await clear.click();
    await expect(rows.locator("tbody tr")).toHaveCount(3);
    await page.route(
      "**/api/requisitions/statistics?*",
      // 刻意中斷一次網路傳輸；重讀時由真實 API 回傳。
      (route) => route.abort("failed"),
      { times: 1 },
    );
    await apply.click();
    await expect(report.getByRole("alert")).toContainText("無法讀取報表");
    await expect(rows).toHaveCount(0);
    await expect(groups).toHaveCount(0);
    await expect(exportCsv).toBeDisabled();
    await apply.click();
    await expect(rows.locator("tbody tr")).toHaveCount(3);
    await expect(exportCsv).toBeEnabled();

    // 由同一瀏覽器另一分頁改用財務帳號；舊分頁權杖與新 Cookie 不配對。
    const other = await context.newPage();
    monitor(other);
    await other.goto("/");
    await other.getByRole("button", { name: "登出", exact: true }).click();
    await login(other, "finance");
    const unexpectedDownloads: string[] = [];
    page.on("download", (file) => {
      // 此時只記錄是否觸發下載，不讀取任何可能越權的檔案內容。
      unexpectedDownloads.push(file.suggestedFilename());
    });
    const [blocked] = await Promise.all([
      page.waitForResponse(
        // 觀察下載按鈕送出的真實受保護 GET。
        (response) =>
          new URL(response.url()).pathname === "/api/requisitions/csv" &&
          response.request().method() === "GET",
      ),
      exportCsv.click(),
    ]);
    expect(blocked.status()).toBe(400);
    expect((await blocked.json()).code).toBe("invalid_csrf");
    await expect(report).toHaveCount(0);
    await expect(page.getByRole("region", { name: "目前身分" })).toHaveCount(0);
    await expect(
      page.getByRole("region", { name: "請先確認登入狀態" }),
    ).toBeFocused();
    await page
      .getByRole("button", { name: "重新確認身分", exact: true })
      .click();
    await expect(
      page
        .getByRole("region", { name: "目前身分" })
        .getByText(actorName("finance"), { exact: true }),
    ).toBeVisible();
    await entry.click();
    await expect(rowFor(rows, subject)).toContainText(submitted.number);
    await expect(rowFor(rows, otherSubject)).toContainText(
      otherSubmitted.number,
    );
    await expect(rowFor(rows, pendingSubject)).toHaveCount(0);
    expect(unexpectedDownloads).toHaveLength(0);

    // 管理帳號登入同一分頁後仍沒有業務報表入口或資料。
    await page.getByRole("button", { name: "登出", exact: true }).click();
    await login(page, "admin");
    await expect(entry).toHaveCount(0);
    await expect(report).toHaveCount(0);
  } finally {
    // 只關閉本案例的獨立 API 登入，不更動隔離環境的其他帳號或資料。
    await Promise.all([
      applicant.dispose(),
      manager.dispose(),
      finance.dispose(),
    ]);
  }
});

// 真實撤權不會撤銷啟用帳號的 Cookie；兩種報表操作都須移除已不可見的選項及資料。
test("請購統計與 CSV 撤掉主管角色後重新套用與下載都移除舊部門", async ({
  page,
  playwright,
  request: admin,
}) => {
  // 用本人正常送審，主管從業務畫面讀取，再由獨立管理工作階段調整資格。
  const applicant = await playwright.request.newContext({ baseURL });
  const subject = `報表即時撤權-${runId}`;
  try {
    await apiLogin(applicant, actorName("applicant"), finalPassword);
    const submitted = await seedPending(applicant, subject);
    if (!submitted.round) throw new Error("報表撤權測試缺少送審快照。");
    const { id, name } = submitted.round.department;
    await apiLogin(admin, "admin", demoAccounts.admin);
    await login(page, "manager");
    await page
      .getByRole("button", { name: "請購統計與 CSV", exact: true })
      .click();
    const report = page.getByRole("region", { name: "請購統計與 CSV" });
    const department = report.getByLabel("送審部門", { exact: true });
    const status = report.getByLabel("目前狀態", { exact: true });
    const option = department.locator(`option[value="${id}"]`);
    const apply = report.getByRole("button", { name: "套用條件" });
    const downloadCsv = report.getByRole("button", { name: "下載 CSV" });
    const rows = report.getByRole("table", { name: "符合條件的請購" });
    const groups = report.getByRole("list", {
      name: "依送審部門與目前狀態統計",
    });
    const empty = report.getByText("沒有符合條件的請購。");
    const emptyGroups = report.getByText("沒有符合條件的統計。");
    await expect(rowFor(rows, subject)).toContainText(submitted.number);
    await expect(option).toContainText(name);
    await department.selectOption(id);
    await status.selectOption("manager_pending");
    await apply.click();
    await expect(downloadCsv).toBeEnabled();

    try {
      // 同一已登入主管用非空條件重讀；即使查詢成功，過期部門仍須連同資料移除。
      await setFixtureManagerRole(admin, false);
      await apply.click();
      await expect(empty).toBeVisible();
      await expect(emptyGroups).toBeVisible();
      expect.soft(await option.count()).toBe(0);
      expect
        .soft(
          await department
            .locator("option")
            .allTextContents()
            .then(
              // 連舊名稱都不可由選項看見。
              (labels) => labels.join(" "),
            ),
        )
        .not.toContain(name);
      await expect(status).toHaveValue("manager_pending");
      await expect(downloadCsv).toBeEnabled();

      // 恢復後沿用既有非空條件讀取，讓下一次撤權從真的有結果開始。
      await setFixtureManagerRole(admin, true);
      await apply.click();
      await expect(rowFor(rows, subject)).toContainText(submitted.number);
      await expect(groups).toContainText("主管待審");
      await expect(downloadCsv).toBeEnabled();
      await setFixtureManagerRole(admin, false);
      const [file, response] = await Promise.all([
        page.waitForEvent("download"),
        page.waitForResponse(
          // 下載仍經真正受保護的 CSV 入口，並保留已套用的原部門識別。
          (response) =>
            new URL(response.url()).pathname === "/api/requisitions/csv",
        ),
        downloadCsv.click(),
      ]);
      expect(response.status()).toBe(200);
      expect(new URL(response.url()).searchParams.get("departmentId")).toBe(id);
      const path = await file.path();
      if (!path) throw new Error("瀏覽器未保存撤權後的 CSV。");
      expect(readFileSync(path).toString("utf8")).toBe(
        "\uFEFF單號,主旨,申請人顯示名稱,送審部門,目前狀態,TWD 預估金額,最近送審時間\r\n",
      );

      // 下載開始後須重讀至穩定狀態，舊資料及識別都不能留在畫面。
      await expect(apply).toBeEnabled();
      await expect(report.getByRole("status")).toContainText("已開始下載 CSV");
      expect.soft(await rows.count()).toBe(0);
      expect.soft(await groups.count()).toBe(0);
      expect.soft(await option.count()).toBe(0);
      expect.soft(await empty.isVisible()).toBe(true);
      expect.soft(await emptyGroups.isVisible()).toBe(true);
      await expect(
        page
          .getByRole("region", { name: "目前身分" })
          .getByText(actorName("manager"), { exact: true }),
      ).toBeVisible();

      // 改選全部部門後可以重新套用；恢復角色再查可重新取得合法選項。
      await department.selectOption("");
      await setFixtureManagerRole(admin, true);
      await apply.click();
      await expect(rowFor(rows, subject)).toContainText(submitted.number);
      await expect(option).toContainText(name);

      // 下載傳輸失敗時需先重讀報表；重新套用不會自動重送 CSV。
      let csvRequests = 0;
      page.on("request", (request) => {
        // 只計算本輪兩次使用者操作發出的 CSV 請求。
        if (new URL(request.url()).pathname === "/api/requisitions/csv")
          csvRequests++;
      });
      await page.route(
        "**/api/requisitions/csv?*",
        // 模擬下載請求還未收到可用資料便失敗。
        (route) => route.abort("failed"),
        { times: 1 },
      );
      await downloadCsv.click();
      await expect(report.getByRole("alert")).toHaveText(
        "無法下載 CSV，請重新套用條件後再下載。",
      );
      await expect(rows).toHaveCount(0);
      await expect(downloadCsv).toBeDisabled();
      await apply.click();
      await expect(downloadCsv).toBeEnabled();
      expect(csvRequests).toBe(1);

      // CSV 已開始下載而讀回失敗時，保留下載事實；重讀只會更新報表。
      await page.route(
        "**/api/requisitions/statistics?*",
        // 只中斷下載之後的統計讀取，下載本身仍走真實 API。
        (route) => route.abort("failed"),
        { times: 1 },
      );
      await Promise.all([page.waitForEvent("download"), downloadCsv.click()]);
      await expect(report.getByRole("alert")).toHaveText(
        "已開始下載 CSV。 無法讀取報表，請重新套用條件。",
      );
      await expect(rows).toHaveCount(0);
      await expect(downloadCsv).toBeDisabled();
      await apply.click();
      await expect(rowFor(rows, subject)).toContainText(submitted.number);
      await expect(downloadCsv).toBeEnabled();
      expect(csvRequests).toBe(2);
    } finally {
      // 後續簽核案例會沿用本輪的主管，務必還原其資格。
      await setFixtureManagerRole(admin, true);
    }
  } finally {
    // 只關閉本案例的獨立本人工作階段。
    await applicant.dispose();
  }
});

// 模擬先中斷清單傳輸，再讓統計的真實防偽拒絕抵達，驗證登入復原不被網路錯誤遮蔽。
test("請購統計與 CSV 先發生網路錯誤仍處理較晚的防偽拒絕", async ({
  page,
  context,
  playwright,
}) => {
  const applicant = await playwright.request.newContext({ baseURL });
  const subject = `報表防偽錯誤順序-${runId}`;
  try {
    // 由本人建立一筆財務目前不可看的待審單，先在舊分頁取得報表。
    await apiLogin(applicant, actorName("applicant"), finalPassword);
    const submitted = await seedPending(applicant, subject);
    await login(page, "applicant");
    const entry = page.getByRole("button", {
      name: "請購統計與 CSV",
      exact: true,
    });
    await entry.click();
    const report = page.getByRole("region", { name: "請購統計與 CSV" });
    const rows = report.getByRole("table", { name: "符合條件的請購" });
    await expect(rowFor(rows, subject)).toContainText(submitted.number);

    // 另一分頁實際更換共享 Cookie；第一分頁仍持有原身分的權杖。
    const other = await context.newPage();
    monitor(other);
    await other.goto("/");
    await other.getByRole("button", { name: "登出", exact: true }).click();
    await login(other, "finance");

    // 統計請求在 Chromium 已觀察到清單失敗後才放行，拒絕狀態由真實 API 產生。
    const listFailed = page.waitForEvent("requestfailed", {
      // 只等待本輪清單請求被中斷。
      predicate: (request) =>
        new URL(request.url()).pathname === "/api/requisitions",
    });
    await page.route(
      "**/api/requisitions?*",
      // 只中斷清單的瀏覽器傳輸。
      (route) => route.abort("failed"),
    );
    await page.route(
      "**/api/requisitions/statistics?*",
      // 保留原請求及 Cookie，等清單失敗後才送給伺服器。
      async (route) => {
        await listFailed;
        await route.continue();
      },
    );
    const [blocked] = await Promise.all([
      page.waitForResponse(
        // 查詢失效必須由實際的統計端點回應。
        (response) =>
          new URL(response.url()).pathname === "/api/requisitions/statistics",
      ),
      report.getByRole("button", { name: "套用條件" }).click(),
    ]);
    // 兩個請求都已處理後才解除攔截，避免提前放行仍暫停的統計請求。
    await page.unrouteAll({ behavior: "wait" });
    expect(blocked.status()).toBe(400);
    expect((await blocked.json()).code).toBe("invalid_csrf");

    // 確認舊畫面與身分卸載，重驗後只能依目前財務資格取得資料。
    await expect(report).toHaveCount(0);
    await expect(page.getByRole("region", { name: "目前身分" })).toHaveCount(0);
    await expect(
      page.getByRole("region", { name: "請先確認登入狀態" }),
    ).toBeFocused();
    await page
      .getByRole("button", { name: "重新確認身分", exact: true })
      .click();
    await expect(
      page
        .getByRole("region", { name: "目前身分" })
        .getByText(actorName("finance"), { exact: true }),
    ).toBeVisible();
    await entry.click();
    await expect(
      report.getByRole("button", { name: "下載 CSV" }),
    ).toBeEnabled();
    await expect(rowFor(rows, subject)).toHaveCount(0);
  } finally {
    // 結束獨立的資料準備工作階段。
    await applicant.dispose();
  }
});

// 從真實授權結果篩選草稿及已送審請購，並驗證明細載入期間和返回清單的狀態。
test("請購列表套用主旨或單號與狀態，獨立開啟明細後保留條件", async ({
  page,
}) => {
  // 建立同一關鍵字下的兩種狀態，另保存一筆非命中項目。
  await login(page, "applicant");
  const keyword = `FILTER-${runId}`;
  const pendingSubject = `${keyword} 主管`;
  const pending = await seedPending(page.request, pendingSubject);
  const draftSubject = `${keyword} 草稿`;
  const otherSubject = `其他請購-${runId}`;
  await seedPending(page.request, otherSubject);
  await saveThroughUI(page, draftSubject, "測試草稿");
  const region = await approvals(page);
  const list = region.getByRole("region", { name: "請購列表" });
  const form = region.getByRole("form", { name: "請購搜尋條件" });
  const search = form.getByLabel("主旨或單號", { exact: true });
  const status = form.getByLabel("目前狀態", { exact: true });
  const apply = form.getByRole("button", { name: "套用條件", exact: true });
  const reload = region.getByRole("button", { name: "重新讀取請購列表" });
  const back = region.getByRole("button", { name: "返回請購列表" });
  const listHeading = region.getByRole("heading", {
    name: "請購查閱與簽核",
    exact: true,
  });
  const detailHeading = region.getByRole("heading", {
    name: pendingSubject,
    exact: true,
  });
  const detailUrl = `${baseURL}/api/requisitions/${pending.id}`;
  const openRow = rowFor(list, pending.number).getByRole("button");

  // 用真實清單 GET 的完整網址核對每次重新讀取未傳入報表條件。
  async function refreshFrom(button: Locator) {
    const [response] = await Promise.all([
      page.waitForResponse(
        // 只比對無參數的清單 GET。
        (response) =>
          response.url() === `${baseURL}/api/requisitions` &&
          response.request().method() === "GET",
      ),
      button.click(),
    ]);
    expect(response.status()).toBe(200);
  }

  // 以不同大小寫與前後空白查主旨。編輯狀態不會立即影響結果。
  await expect(rowFor(list, draftSubject)).toHaveCount(1);
  await expect(rowFor(list, pending.number)).toHaveCount(1);
  await expect(rowFor(list, otherSubject)).toHaveCount(1);
  await expect(status.locator("option")).toHaveText([
    "全部狀態",
    "草稿",
    "撤回修改中",
    "主管待審",
    "財務待審",
    "核准結案",
    "駁回結案",
  ]);
  await search.fill(` ${keyword.toLowerCase()} `);
  await refreshFrom(apply);
  await expect(list.getByRole("button")).toHaveCount(2);
  await expect(rowFor(list, draftSubject)).toHaveCount(1);
  await expect(rowFor(list, pending.number)).toHaveCount(1);
  await status.selectOption("draft");
  const unapplied = region.getByText("條件尚未套用，以下仍為上次結果。");
  await expect(unapplied).toBeVisible();
  await expect(list.getByRole("button")).toHaveCount(2);
  await refreshFrom(apply);
  await expect(list.getByRole("button")).toHaveCount(1);
  await expect(rowFor(list, draftSubject)).toHaveCount(1);
  await expect(unapplied).toHaveCount(0);

  // 同時比對單號和狀態。狀態不符時顯示無符合結果。
  await search.fill(pending.number.toLowerCase());
  await refreshFrom(apply);
  await expect(region.getByText("沒有符合搜尋條件的請購。")).toBeVisible();
  await expect(list).toHaveCount(0);
  await status.selectOption("manager_pending");
  await refreshFrom(apply);
  await expect(list.getByRole("button")).toHaveCount(1);

  // 暫停實際明細 GET，確認尚未收到回應時清單與表單已卸載。
  let release!: () => void;
  // 保留放行方法，測試不靠固定延遲猜測畫面和網路的先後。
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route(
    detailUrl,
    // 保留真實回應。檢查載入畫面後再放行明細請求。
    async (route) => {
      await held;
      await route.continue();
    },
    { times: 1 },
  );
  try {
    await openRow.click();
    await expect(region.getByText("正在讀取請購明細…")).toBeVisible();
    await expect(list).toHaveCount(0);
    await expect(form).toHaveCount(0);
    await expect(reload).toHaveCount(0);
  } finally {
    release();
  }
  await expect(detailHeading).toBeFocused();
  await expect(list).toHaveCount(0);
  await expect(form).toHaveCount(0);

  // 返回清單重新讀取授權結果，沿用先前的條件。
  await refreshFrom(back);
  await expect(listHeading).toBeFocused();
  await expect(search).toHaveValue(pending.number.toLowerCase());
  await expect(status).toHaveValue("manager_pending");
  await expect(list.getByRole("button")).toHaveCount(1);
  await expect(rowFor(list, pending.number)).toHaveCount(1);

  // 模擬第一次開啟時傳輸中斷。清單與篩選恢復，並可重讀同筆明細。
  const retry = region.getByRole("button", {
    name: "重新讀取結果",
    exact: true,
  });
  await page.route(
    detailUrl,
    // 只中斷第一次明細查詢，人工重讀仍走真實 API。
    (route) => route.abort("failed"),
    { times: 1 },
  );
  await openRow.click();
  await expect(region.getByRole("alert")).toHaveText(
    "無法讀取請購結果，請重新讀取。",
  );
  await expect(listHeading).toBeFocused();
  await expect(openRow).toBeEnabled();
  await expect(search).toHaveValue(pending.number.toLowerCase());
  await expect(status).toHaveValue("manager_pending");
  await expect(back).toHaveCount(0);
  const [retried] = await Promise.all([
    page.waitForResponse(
      // 重讀必須是同一筆的 GET。
      (response) =>
        response.url() === detailUrl && response.request().method() === "GET",
    ),
    retry.click(),
  ]);
  expect(retried.status()).toBe(200);
  await expect(detailHeading).toBeFocused();
  await expect(list).toHaveCount(0);
  await expect(form).toHaveCount(0);
  await expect(retry).toHaveCount(0);
  await refreshFrom(back);
  await expect(list.getByRole("button")).toHaveCount(1);
  await expect(search).toHaveValue(pending.number.toLowerCase());
  await expect(status).toHaveValue("manager_pending");

  // 只模擬第一次明細 GET 查無。清掉舊清單，保留條件供手動重新讀取。
  let writes = 0;
  page.on("request", (request) => {
    // 觀察 404 與手動重讀是否意外發出請購寫入。
    if (
      request.method() === "POST" &&
      request.url().startsWith(`${baseURL}/api/requisitions/`)
    )
      writes++;
  });
  await page.route(
    detailUrl,
    // 回傳正式錯誤格式，讓 UI 辨識通用的不存在或不可見。
    (route) =>
      route.fulfill({
        status: 404,
        json: { code: "not_found", message: "找不到指定的請購單。" },
      }),
    { times: 1 },
  );
  await openRow.click();
  await expect(region.getByRole("alert")).toHaveText(
    "找不到指定的請購單，請重新讀取請購列表。",
  );
  await expect(listHeading).toBeFocused();
  await expect(list).toHaveCount(0);
  await expect(back).toHaveCount(0);
  await expect(retry).toHaveCount(0);
  await expect(search).toHaveValue(pending.number.toLowerCase());
  await expect(status).toHaveValue("manager_pending");
  await refreshFrom(reload);
  await expect(list.getByRole("button")).toHaveCount(1);
  await expect(rowFor(list, pending.number)).toHaveCount(1);
  expect(writes).toBe(0);

  // 清除條件後重新讀取完整的授權清單。
  await refreshFrom(
    form.getByRole("button", { name: "清除條件", exact: true }),
  );
  await expect(search).toHaveValue("");
  await expect(status).toHaveValue("");
  await expect(rowFor(list, draftSubject)).toHaveCount(1);
  await expect(rowFor(list, pending.number)).toHaveCount(1);
  await expect(rowFor(list, otherSubject)).toHaveCount(1);
});

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
  await expect(applicant.getByRole("columnheader")).toHaveText([
    "主旨 / 單號",
    "申請人",
    "目前狀態",
    "預估申請金額",
  ]);
  await openApproval(applicant, subject);
  await expect(applicant.getByRole("columnheader")).toHaveText([
    "品名",
    "單位",
    "數量",
    "預估單價",
    "預估小計",
  ]);
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
  await expect(
    applicant
      .locator("dl")
      .filter({ hasText: "送審輪次" })
      .getByText("第 1 輪", { exact: true }),
  ).toBeVisible();
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

    // 第二案使用合法的單價與整單金額上限，仍由主管真實駁回。
    await seedPending(page.request, rejectedSubject, {
      quantity: 10,
      unitPrice: 1_000_000,
    });
    await manager.getByRole("button", { name: "返回請購列表" }).click();
    await openApproval(manager, rejectedSubject);
    await manager.getByRole("button", { name: "駁回", exact: true }).click();
    await expect(manager.getByText("駁回結案", { exact: true })).toBeVisible();
    await expect(
      manager.getByRole("button", { name: "核准", exact: true }),
    ).toHaveCount(0);
    // 用同一位申請人核對上限金額排版，列表箭頭與明細兩個金額欄都須留在自己的欄內。
    await applicant.getByRole("button", { name: "返回請購列表" }).click();
    const maxTotal = "TWD 10,000,000.00";
    const maxUnitPrice = "TWD 1,000,000.00";
    await checkMoneyFits(page, [
      [rowFor(applicant, rejectedSubject).getByRole("cell").last(), maxTotal],
    ]);
    await openApproval(applicant, rejectedSubject);
    const lineCells = applicant
      .getByRole("region", { name: "請購品項" })
      .getByRole("cell");
    await checkMoneyFits(page, [
      [lineCells.filter({ hasText: maxUnitPrice }), maxUnitPrice],
      [lineCells.filter({ hasText: maxTotal }), maxTotal],
    ]);

    // 申請人仍讀得到駁回歷程，且先前已送審的請購不可回到草稿編輯。
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

// 實際撤回後只供本人修改並重送，同時驗證失聯結果及舊輪次隔離。
test("本人撤回後核對原輪快照、修改同單重送，審核者只見最新輪", async ({
  page,
  browser,
}) => {
  const subject = `撤回舊主旨-${runId}-${"長快照".repeat(36)}`;
  const editedSubject = `重送新主旨-${runId}`;
  const itemName = `舊輪明細-${"OldSnapshotItem".repeat(10)}`;
  const editedItem = `新輪明細-${runId}`;
  const managerContext = await browser.newContext();

  try {
    // 申請人從真實草稿表單建立第一輪，送審內容由伺服器建立快照。
    await login(page, "applicant");
    await saveThroughUI(page, subject, itemName);
    const owner = await approvals(page);
    await openApproval(owner, subject);
    const [firstResponse] = await Promise.all([
      page.waitForResponse(
        // 僅將同一請購的送審 POST 當作第一輪寫入結果。
        (response) =>
          response.url().endsWith("/submit") &&
          response.request().method() === "POST",
      ),
      owner.getByRole("button", { name: "送出簽核", exact: true }).click(),
    ]);
    expect(firstResponse.status()).toBe(200);
    const firstRound: RequisitionDetailDto = await firstResponse.json();
    expect(firstRound.round?.number).toBe(1);
    const roundDepartmentName = firstRound.round?.department.name;
    expect(roundDepartmentName).toBeTruthy();

    // 主管先以原指派開啟資料，之後確認撤回即時移除查閱權限。
    const managerPage = await managerContext.newPage();
    const manager = await openActor(managerPage, "manager");
    await expect(rowFor(manager, subject)).toHaveCount(1);
    await openApproval(manager, subject);

    // 伺服器實際完成撤回後切斷回應，檢查畫面保留未知結果且沒有自動重送。
    let withdrawPosts = 0;
    await page.route(
      `**/api/requisitions/${firstRound.id}/withdraw`,
      async (route) => {
        withdrawPosts++;
        expect(route.request().postDataJSON()).toEqual({
          version: firstRound.version,
        });
        const committed = await route.fetch();
        expect(committed.status()).toBe(200);
        await route.abort("failed");
      },
    );
    const withdrawButton = owner.getByRole("button", {
      name: "撤回修改",
      exact: true,
    });
    await expect(withdrawButton).toBeVisible();
    await withdrawButton.click();
    const unknown = owner
      .getByRole("alert")
      .filter({ hasText: "操作結果未確認" });
    await expect(unknown).toBeVisible();
    await expect(
      owner.getByRole("button", { name: "撤回修改", exact: true }),
    ).toBeDisabled();
    const readback = owner.getByRole("button", {
      name: "重新讀取結果",
      exact: true,
    });
    await expect(readback).toBeEnabled();
    expect(withdrawPosts).toBe(1);

    // 由使用者明確 GET 核對同一筆，確認撤回狀態與原送審輪次。
    await readback.click();
    await expect(owner.getByText("撤回修改中", { exact: true })).toBeVisible();
    await expect(unknown).toHaveCount(0);
    await expect(
      owner.getByRole("button", { name: "撤回修改", exact: true }),
    ).toHaveCount(0);
    await expect(owner.getByRole("status")).toContainText("已讀取最新狀態");
    await history(owner, [
      `送出簽核 · ${actorName("applicant")}`,
      `撤回修改 · ${actorName("applicant")}`,
    ]);
    const previousRound = owner.locator("details");
    await expect(previousRound).toHaveCount(1);
    await previousRound.locator("summary").click();
    await expect(previousRound).toContainText(subject);
    await expect(previousRound).toContainText("補充辦公設備");
    await expect(previousRound).toContainText(itemName);
    const oldDepartment = previousRound
      .getByText("送審部門", { exact: true })
      .locator("xpath=following-sibling::dd[1]");
    expect(await oldDepartment.textContent()).toBe(roundDepartmentName);
    expect(withdrawPosts).toBe(1);

    // 主管重讀清單後看不到該單，直接讀取同一識別亦只能得到不可見結果。
    await manager
      .getByRole("button", { name: "返回請購列表", exact: true })
      .click();
    await expect(rowFor(manager, subject)).toHaveCount(0);
    const hidden = await managerPage.request.get(
      `${baseURL}/api/requisitions/${firstRound.id}`,
      {
        headers: { "X-CSRF-TOKEN": await csrf(managerPage.request) },
      },
    );
    expect(hidden.status()).toBe(404);

    // 本人以原單號開啟撤回內容；撤回歷程不可刪除，已送審單不提供刪除。
    await page
      .getByRole("button", { name: "本人請購草稿", exact: true })
      .click();
    const drafts = page.getByRole("region", { name: "本人請購草稿" });
    const editorButton = drafts.getByRole("button", {
      name: `開啟草稿 ${firstRound.number}`,
      exact: true,
    });
    await expect(editorButton).toBeEnabled();
    await editorButton.click();
    const form = drafts.getByRole("form", {
      name: `編輯草稿 ${firstRound.number}`,
    });
    await expect(form).toContainText("撤回修改中");
    await expect(
      form.getByRole("button", { name: "刪除草稿", exact: true }),
    ).toHaveCount(0);
    await form.getByLabel("主旨", { exact: true }).fill(editedSubject);
    await form
      .getByRole("textbox", { name: "整單用途", exact: true })
      .fill("修改後用途");
    const line = form.getByRole("group", { name: "明細 1" });
    await line.getByLabel("品名", { exact: true }).fill(editedItem);
    const [savedResponse] = await Promise.all([
      page.waitForResponse(
        // 保存必須更新原識別，不能建立第二張草稿。
        (response) =>
          response.url().endsWith(`/drafts/${firstRound.id}`) &&
          response.request().method() === "POST",
      ),
      form.getByRole("button", { name: "保存草稿", exact: true }).click(),
    ]);
    expect(savedResponse.status()).toBe(200);
    const saved = await savedResponse.json();
    expect(saved.id).toBe(firstRound.id);
    expect(saved.number).toBe(firstRound.number);
    expect(saved.status).toBe("withdrawn");
    await expect(drafts.getByRole("status")).toHaveText("草稿已保存。");
    await expect(
      form.getByRole("button", { name: "刪除草稿", exact: true }),
    ).toHaveCount(0);

    // 回到既有簽核面板，以同識別與新保存內容重送為第二輪。
    await page
      .getByRole("button", { name: "請購查閱與簽核", exact: true })
      .click();
    const ownerApprovals = page.getByRole("region", {
      name: "請購查閱與簽核",
    });
    await expect(rowFor(ownerApprovals, editedSubject)).toHaveCount(1);
    await openApproval(ownerApprovals, editedSubject);
    await expect(
      ownerApprovals.getByText("撤回修改中", { exact: true }),
    ).toBeVisible();
    await expect(
      ownerApprovals.getByText(editedItem, { exact: true }),
    ).toBeVisible();
    const [secondResponse] = await Promise.all([
      page.waitForResponse(
        // 第二輪仍使用既有 submit 操作及原單識別。
        (response) =>
          response.url().endsWith("/submit") &&
          response.request().method() === "POST",
      ),
      ownerApprovals
        .getByRole("button", { name: "送出簽核", exact: true })
        .click(),
    ]);
    expect(secondResponse.status()).toBe(200);
    const secondRound: RequisitionDetailDto = await secondResponse.json();
    expect(secondRound.id).toBe(firstRound.id);
    expect(secondRound.number).toBe(firstRound.number);
    expect(secondRound.status).toBe("manager_pending");
    expect(secondRound.round?.number).toBe(2);
    expect(secondRound.previousRounds).toHaveLength(1);
    expect(secondRound.previousRounds[0].subject).toBe(subject);
    await history(ownerApprovals, [
      `送出簽核 · ${actorName("applicant")}`,
      `撤回修改 · ${actorName("applicant")}`,
      `送出簽核 · ${actorName("applicant")}`,
    ]);

    // 展開舊輪快照，在手機與桌面寬度檢查內容換行及鍵盤可用控制項。
    const previousRounds = ownerApprovals.locator("details");
    await expect(previousRounds).toHaveCount(1);
    await previousRounds.locator("summary").click();
    await expect(previousRounds).not.toContainText("修改後用途");
    await expect(previousRounds).toContainText(itemName);
    const oldRoundDepartment = previousRounds
      .getByText("送審部門", { exact: true })
      .locator("xpath=following-sibling::dd[1]");
    expect(await oldRoundDepartment.textContent()).toBe(roundDepartmentName);
    await checkLayout(
      page,
      ownerApprovals,
      itemName,
      "approval-withdrawal-round",
    );

    // 重送後原主管只取得第二輪明細與事件，不能讀到先前輪次內容。
    await manager
      .getByRole("button", { name: "重新讀取請購列表", exact: true })
      .click();
    await expect(rowFor(manager, editedSubject)).toHaveCount(1);
    await openApproval(manager, editedSubject);
    await expect(manager.getByText("主管待審", { exact: true })).toBeVisible();
    await expect(manager.getByText(editedItem, { exact: true })).toBeVisible();
    await expect(manager.getByText(itemName, { exact: true })).toHaveCount(0);
    await expect(manager.locator("details")).toHaveCount(0);
    await history(manager, [`送出簽核 · ${actorName("applicant")}`]);

    // 主管首次核准後，申請人讀回最新狀態即可確認撤回按鈕已消失。
    await manager.getByRole("button", { name: "核准", exact: true }).click();
    await expect(manager.getByText("財務待審", { exact: true })).toBeVisible();
    await ownerApprovals
      .getByRole("button", { name: "返回請購列表", exact: true })
      .click();
    await expect(rowFor(ownerApprovals, editedSubject)).toHaveCount(1);
    await openApproval(ownerApprovals, editedSubject);
    await expect(
      ownerApprovals.getByText("財務待審", { exact: true }),
    ).toBeVisible();
    await expect(
      ownerApprovals.getByRole("button", { name: "撤回修改", exact: true }),
    ).toHaveCount(0);
    await history(ownerApprovals, [
      `送出簽核 · ${actorName("applicant")}`,
      `撤回修改 · ${actorName("applicant")}`,
      `送出簽核 · ${actorName("applicant")}`,
      `主管核准 · ${actorName("manager")}`,
    ]);
  } finally {
    // 關閉本案例獨立的主管瀏覽器工作階段。
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

    // 先經過草稿與統計面板再回簽核；報表讀取成功也不能清掉原筆未知結果。
    await page
      .getByRole("button", { name: "本人請購草稿", exact: true })
      .click();
    await page
      .getByRole("button", { name: "請購統計與 CSV", exact: true })
      .click();
    await expect(
      page
        .getByRole("region", { name: "請購統計與 CSV" })
        .getByRole("button", { name: "下載 CSV", exact: true }),
    ).toBeEnabled();

    // 另一筆可照常作決定，已成功提示與前一筆未知結果須同時存在。
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
    // 等原筆明細讀回並出現在獨立畫面，再核對該單的狀態。
    await expect(
      region.getByRole("heading", { name: subject, exact: true }),
    ).toBeVisible();
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

for (const resubmit of [false, true]) {
  const scenario = resubmit ? "已重送" : "仍撤回";

  // 讓撤回先提交，主管只遺失較晚核准的回應；依重讀當下的授權核對畫面。
  test(`撤回後遺失核准回應：${scenario}時結束無法核對的舊操作`, async ({
    page,
    playwright,
  }) => {
    const subject = `撤回先於核准-${scenario}-${runId}`;
    const otherSubject = `另筆待核對-${runId}`;
    const applicant = await playwright.request.newContext({ baseURL });
    try {
      // 用獨立的本人 Cookie 準備同一主管的待審單，避免覆寫瀏覽器身分。
      await apiLogin(applicant, actorName("applicant"), finalPassword);
      const headers = { "X-CSRF-TOKEN": await csrf(applicant) };
      const first = await seedPending(applicant, subject);
      const other = resubmit
        ? null
        : await seedPending(applicant, otherSubject);
      await login(page, "manager");
      const region = await approvals(page);
      const readback = region.getByRole("button", { name: /^重新讀取結果/ });

      // 404 分支另保留一筆可核對的結果，確認清除範圍只有原單。
      if (other) {
        await openApproval(region, otherSubject);
        await page.route(
          `**/api/requisitions/${other.id}/reject`,
          async (route) => {
            // 真實駁回已提交，瀏覽器只知道回應遺失。
            const committed = await route.fetch();
            expect(committed.status()).toBe(200);
            await route.abort("failed");
          },
        );
        await region.getByRole("button", { name: "駁回", exact: true }).click();
        await expect(readback).toBeEnabled();
        await region.getByRole("button", { name: "返回請購列表" }).click();
      }

      // 攔到主管原始 POST 才撤回；送達伺服器後核對真實 404，再中斷瀏覽器回應。
      await openApproval(region, subject);
      const detailPath = `/api/requisitions/${first.id}`;
      let approvePosts = 0;
      let withdrawnVersion = "";
      await page.route(`**${detailPath}/approve`, async (route) => {
        // 只對首次請求安排撤回，之後即使出現意外重送也交給次數斷言。
        approvePosts++;
        if (approvePosts === 1) {
          const withdrawn = await applicant.post(`${detailPath}/withdraw`, {
            headers,
            data: { version: first.version },
          });
          expect(withdrawn.status()).toBe(200);
          const detail: RequisitionDetailDto = await withdrawn.json();
          expect(detail.status).toBe("withdrawn");
          withdrawnVersion = detail.version;
          const rejected = await route.fetch();
          expect(rejected.status()).toBe(404);
        }
        await route.abort("failed");
      });
      await region.getByRole("button", { name: "核准", exact: true }).click();
      const targetReadback = readback.nth(other ? 1 : 0);
      await expect(targetReadback).toBeEnabled();
      await expect(
        region.getByRole("button", { name: "核准", exact: true }),
      ).toBeDisabled();
      expect(approvePosts).toBe(1);

      // 在讀回前才重送；只有第二輪現在能被主管讀取。
      if (resubmit) {
        const submitted = await applicant.post(`${detailPath}/submit`, {
          headers,
          data: { version: withdrawnVersion },
        });
        expect(submitted.status()).toBe(200);
      }
      const [read] = await Promise.all([
        page.waitForResponse(
          // 讀回必須對原單發出 GET，不能將別筆結果誤當成這筆。
          (response) =>
            response.url().endsWith(detailPath) &&
            response.request().method() === "GET",
        ),
        targetReadback.click(),
      ]);
      expect(read.status()).toBe(resubmit ? 200 : 404);

      // 兩種回應都應明示無法確認舊操作，且不再要求重讀這一筆。
      if (resubmit) {
        await history(region, [
          `第 2 輪 · 送出簽核 · ${actorName("applicant")}`,
        ]);
        await expect(
          region.getByText("請核對操作歷程。", { exact: false }),
        ).toHaveCount(0);
        await expect(region.getByRole("alert")).toContainText(
          "無法確認先前操作結果",
        );
        await expect(readback).toHaveCount(0);
        await expect(
          region.getByRole("button", { name: "核准", exact: true }),
        ).toBeEnabled();

        // 由主管明確決定第二輪，確認舊結果的提示不會鎖住新一輪。
        await region.getByRole("button", { name: "駁回", exact: true }).click();
        await expect(region.getByRole("status")).toHaveText("已駁回。");
        await history(region, [
          `第 2 輪 · 送出簽核 · ${actorName("applicant")}`,
          `第 2 輪 · 主管駁回 · ${actorName("manager")}`,
        ]);
      } else {
        // 只移除查無的案件，重新載入清單後仍可核對另一筆未知結果。
        await expect(
          region.getByRole("button", { name: "返回請購列表", exact: true }),
        ).toHaveCount(0);
        await expect(
          region.getByText("目前無法讀取這筆請購，無法確認先前操作結果。", {
            exact: false,
          }),
        ).toBeVisible();
        await expect(readback).toHaveCount(1);
        await region
          .getByRole("button", { name: "重新讀取請購列表", exact: true })
          .click();
        await expect(rowFor(region, otherSubject)).toHaveCount(1);
        await expect(rowFor(region, subject)).toHaveCount(0);
        await readback.click();
        // 等保留的另一筆明細出現，再核對該單結案狀態。
        await expect(
          region.getByRole("heading", { name: otherSubject, exact: true }),
        ).toBeVisible();
        await expect(
          region.getByText("駁回結案", { exact: true }),
        ).toBeVisible();
        await expect(readback).toHaveCount(0);
      }
      expect(approvePosts).toBe(1);
    } finally {
      await applicant.dispose();
    }
  });
}

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

    // 欄位不完整、巢狀歷程／輪次或資格旗標錯誤都不能結束讀回。
    const detailUrl = `**/api/requisitions/${first.id}`;
    for (const invalid of [
      null,
      { history: [null] },
      { canWithdraw: null },
      { previousRounds: [null] },
      {
        previousRounds: [
          {
            round: null,
            subject: null,
            purpose: null,
            estimatedAmount: null,
            lines: [null],
          },
        ],
      },
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
        region.getByRole("button", { name: "返回請購列表", exact: true }),
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
    // GET 成功後仍須等畫面採用該筆明細，列表也可能已有同一個狀態。
    await expect(
      region.getByRole("heading", { name: firstSubject, exact: true }),
    ).toBeVisible();
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

// 只更動本輪合成主管的角色，使用真實管理版本並在個案結束後恢復。
async function setFixtureManagerRole(
  admin: APIRequestContext,
  isManager: boolean,
) {
  // 先讀取目前版本與設定，避免覆寫角色以外的帳號欄位。
  const snapshot = await admin.get("/api/admin/organization");
  expect(snapshot.status()).toBe(200);
  const { accounts }: OrganizationDto = await snapshot.json();
  const account = accounts.find(
    // 只使用本輪建立的主管，不動預載示範角色。
    ({ userName }) => userName === actorName("manager"),
  );
  if (!account) throw new Error("簽核測試主管未完整建立。");

  // 更新資格仍走正式管理操作與防偽檢查。
  const response = await admin.post(`/api/admin/accounts/${account.id}`, {
    headers: { "X-CSRF-TOKEN": await csrf(admin) },
    data: {
      version: account.version,
      active: account.active,
      isManager,
      isFinance: account.isFinance,
      departmentId: account.departmentId,
      managerReviewerId: account.managerReviewerId,
      financeReviewerId: account.financeReviewerId,
    },
  });
  expect(response.status()).toBe(204);
}

// 等待目前角色可用的管理入口，切換後保留相同 locator 供面板卸載斷言。
async function reassignments(page: Page) {
  const entry = page.getByRole("button", { name: "有限改派", exact: true });
  await expect(entry).toBeVisible();
  await entry.click();
  const panel = page.getByRole("region", { name: "有限改派" });
  await expect(panel).toBeVisible();
  return panel;
}

// 管理端只用系統單號找案件，原始請購主旨永遠不作為管理端 locator。
function reassignmentRow(panel: Locator, number: string) {
  return panel.getByRole("listitem").filter({
    has: panel.page().getByRole("button", { name: number, exact: true }),
  });
}

// 開啟指定單的表單並檢查焦點，避免背景讀取後遺失鍵盤位置。
async function editReassignment(row: Locator, number: string) {
  await row.getByRole("button", { name: number, exact: true }).click();
  await expect(
    row.getByRole("heading", { name: `改派 ${number}` }),
  ).toBeFocused();
  return row.getByRole("form", { name: `改派 ${number}` });
}

// 從管理員入口驗證有限欄位、分關角色、暫停到續辦，以及業務端實際保存的歷程。
test("有限改派只向管理員顯示待辦所需資訊，資格恢復後保留歷程", async ({
  page,
  browser,
}) => {
  // 分別登入管理員與申請人；送審後撤掉本輪主管資格，製造可復原的暫停。
  const subject = `有限改派測試-${runId}`;
  const business = await browser.newContext();
  const applicantPage = await business.newPage();
  await apiLogin(page.request, "admin", demoAccounts.admin);
  await page.reload();
  await expect(page).toHaveTitle(/請購簽核/);
  await expect(page).toHaveURL(new URL("/", baseURL).href);
  const applicant = await openActor(applicantPage, "applicant");
  const { number, id } = await seedPending(applicantPage.request, subject);
  const reloadBusiness = applicant.getByRole("button", {
    name: "重新讀取請購列表",
  });
  await reloadBusiness.click();
  await openApproval(applicant, subject);
  await expect(
    applicantPage.getByRole("button", { name: "有限改派", exact: true }),
  ).toHaveCount(0);
  await setFixtureManagerRole(page.request, false);

  try {
    // 申請人重讀仍保留主管待審，管理員只見單號、送審部門、狀態與兩關人選。
    await applicant.getByRole("button", { name: "返回請購列表" }).click();
    await openApproval(applicant, subject);
    await expect(applicant.getByRole("alert")).toContainText(
      "簽核已暫停，保留目前關卡",
    );
    await expect(
      applicant.getByText("主管待審", { exact: true }),
    ).toBeVisible();
    const panel = await reassignments(page);
    const row = reassignmentRow(panel, number);
    await expect(row).toBeVisible();
    for (const value of [
      departmentName,
      "主管待審",
      "簽核已暫停",
      actorName("manager"),
      actorName("finance"),
    ]) {
      // 每一個有限欄位都從指定待辦的摘要查證。
      await expect(row.getByText(value, { exact: true })).toBeVisible();
    }
    for (const value of [
      subject,
      "驗證操作讀回",
      "測試品項",
      "預估申請金額",
      "主旨",
      "整單用途",
      "請購明細",
    ]) {
      // 已知測試單的私密欄位和值皆不可顯示。
      await expect(panel).not.toContainText(value);
    }

    // 鍵盤可到兩關選單與原因欄；手機和桌機不裁切關鍵表單控制項。
    const form = await editReassignment(row, number);
    const stage = form.getByLabel("改派關卡");
    const reviewer = form.getByLabel("接手審核者");
    const reason = form.getByLabel("改派原因");
    await page.keyboard.press("Tab");
    await expect(stage).toBeFocused();
    await expect(stage.locator("option")).toHaveText(["主管", "財務"]);
    await expect(
      reviewer.getByRole("option", { name: "manager", exact: true }),
    ).toHaveCount(1);
    await expect(
      reviewer.getByRole("option", { name: "finance", exact: true }),
    ).toHaveCount(0);
    await expect(
      reviewer.getByRole("option", { name: actorName("manager"), exact: true }),
    ).toHaveCount(0);
    await stage.selectOption("finance");
    await expect(
      reviewer.getByRole("option", { name: "manager", exact: true }),
    ).toHaveCount(0);
    await expect(
      reviewer.getByRole("option", { name: actorName("finance"), exact: true }),
    ).toHaveCount(0);
    await checkLayout(page, row, departmentName, "reassignment");

    // 只有白字元及過長的 UTF-16 原因在前端拒絕，焦點回到原欄位。
    const save = form.getByRole("button", { name: "保存改派" });
    let posts = 0;
    page.on("request", (request) => {
      // 只計本案寫入次數，不收集請求資料。
      if (
        request.method() === "POST" &&
        request.url().endsWith(`/api/admin/reassignments/${id}`)
      )
        posts++;
    });
    await reviewer.selectOption({ label: "finance" });
    await reason.fill("\u0085 \u3000");
    await save.click();
    await expect(panel.getByRole("alert")).toContainText(
      "請填寫 1–500 字的改派原因",
    );
    await expect(reason).toBeFocused();
    await reason.fill("📝".repeat(251));
    await save.click();
    await expect(reason).toBeFocused();
    expect(posts).toBe(0);

    // 模擬伺服器的明確輸入拒絕，確認安全輸入和選項可在原表單修正。
    await page.route(
      `**/api/admin/reassignments/${id}`,
      async (route) => {
        // 後端驗證的真實 HTTP 案例另有測試；此處只演練 UI 收到拒絕的分支。
        await route.fulfill({
          status: 400,
          json: { code: "invalid_input", message: "請修正改派原因。" },
        });
      },
      { times: 1 },
    );
    await reason.fill("保留這段理由");
    await save.click();
    await expect(panel.getByRole("alert")).toHaveText("請修正改派原因。");
    await expect(reason).toHaveValue("保留這段理由");
    await expect(reviewer.locator("option:checked")).toHaveText("finance");

    // 先改派財務，確認主管仍待審且未恢復；前後空白依後端規則正規化。
    await reason.fill("\u0085\uFEFF財務接手\u3000");
    await save.click();
    await expect(
      panel.getByRole("status").filter({ hasText: "改派已保存" }),
    ).toBeVisible();
    await expect(row.getByText("簽核已暫停", { exact: true })).toBeVisible();
    await expect(row.getByText("主管待審", { exact: true })).toBeVisible();
    await expect(row.getByText("finance", { exact: true })).toBeVisible();

    // 再改派原待審關卡後，已恢復資格仍停在主管待審，由新人處理。
    await editReassignment(row, number);
    await reviewer.selectOption({ label: "manager" });
    await reason.fill("主管接手");
    await save.click();
    await expect(
      panel.getByRole("status").filter({ hasText: "改派已保存" }),
    ).toBeVisible();
    await expect(row.getByText("簽核已暫停", { exact: true })).toHaveCount(0);
    await expect(
      panel.getByRole("heading", { name: "有限改派" }),
    ).toBeFocused();
    await expect(row.getByText("主管待審", { exact: true })).toBeVisible();
    await expect(row.getByText("manager", { exact: true })).toBeVisible();
    expect(posts).toBe(3);

    // 在業務畫面核對同輪的前後人選、原因、管理操作者與時間，內容仍取自原快照。
    await applicant.getByRole("button", { name: "返回請購列表" }).click();
    await openApproval(applicant, subject);
    await history(applicant, [
      `送出簽核 · ${actorName("applicant")}`,
      "第 1 輪 · 財務改派 · admin",
      "第 1 輪 · 主管改派 · admin",
    ]);
    const events = applicant
      .getByRole("list", { name: "操作歷程" })
      .getByRole("listitem");
    await expect(events.nth(1)).toContainText(actorName("finance"));
    await expect(events.nth(1)).toContainText("財務接手");
    await expect(events.nth(2)).toContainText(actorName("manager"));
    await expect(events.nth(2)).toContainText("主管接手");
    await expect(
      applicant.getByText("驗證操作讀回", { exact: true }),
    ).toBeVisible();
    await expect(
      applicant.getByText("測試品項", { exact: true }),
    ).toBeVisible();
    const detail = await applicantPage.request.get(`/api/requisitions/${id}`, {
      headers: { "X-CSRF-TOKEN": await csrf(applicantPage.request) },
    });
    expect(detail.status()).toBe(200);
    const persisted: RequisitionDetailDto = await detail.json();
    expect(persisted.history[1].reassignment?.reason).toBe("\uFEFF財務接手");

    // 新主管的正常簽核推進到財務關；管理面板此後只能選未完成的財務關。
    await apiLogin(applicantPage.request, "manager", demoAccounts.manager);
    await applicantPage.reload();
    const newManager = await approvals(applicantPage);
    await openApproval(newManager, subject);
    await newManager.getByRole("button", { name: "核准", exact: true }).click();
    await expect(
      newManager.getByText("財務待審", { exact: true }),
    ).toBeVisible();
    await panel.getByRole("button", { name: "重新讀取改派待辦" }).click();
    await expect(row.getByText("財務待審", { exact: true })).toBeVisible();
    await editReassignment(row, number);
    await expect(stage.locator("option")).toHaveText(["財務"]);
  } finally {
    // 恢復共用測試角色後釋放獨立 Cookie context。
    await setFixtureManagerRole(page.request, true);
    await business.close();
  }
});

// 過期與傳輸中斷都要求人工讀回；同帳號重驗後不重送，且待辦消失仍保留不確定性。
test("有限改派過期與結果未確認分別讀回，同帳號恢復後不重送", async ({
  page,
  context,
  request,
}) => {
  // 開啟舊版待辦，再用真實 API 先改派同關卡，讓舊畫面收到實際版本衝突。
  const subject = `改派結果讀回-${runId}`;
  await apiLogin(request, actorName("applicant"), finalPassword);
  const { id, number, version } = await seedPending(request, subject);
  await apiLogin(page.request, "admin", demoAccounts.admin);
  await page.reload();
  const panel = await reassignments(page);
  const row = reassignmentRow(panel, number);
  const form = await editReassignment(row, number);
  const stage = form.getByLabel("改派關卡");
  const reviewer = form.getByLabel("接手審核者");
  const reason = form.getByLabel("改派原因");
  const save = form.getByRole("button", { name: "保存改派" });
  const refresh = panel.getByRole("button", { name: "重新讀取結果" });
  const headers = { "X-CSRF-TOKEN": await csrf(page.request) };
  const snapshot = await page.request.get("/api/admin/reassignments", {
    headers,
  });
  expect(snapshot.status()).toBe(200);
  const { finances }: ReassignmentListDto = await snapshot.json();
  const finance = finances.find(
    // 使用本機示範角色作為另一次交易先選中的合格接手人。
    ({ userName }) => userName === "finance",
  );
  if (!finance) throw new Error("改派測試缺少合格財務。");
  const path = `/api/admin/reassignments/${id}`;
  const competing = await page.request.post(path, {
    headers,
    data: {
      version,
      stage: "finance",
      reviewerId: finance.id,
      reason: "另一工作階段先改派",
    },
  });
  expect(competing.status()).toBe(204);

  // 計數瀏覽器實際要求的 POST 和 GET，獨立 API 夾具請求不算作畫面操作。
  let posts = 0;
  let reads = 0;
  page.on("request", (request) => {
    // 只計本案寫入與待辦讀取，不輸出防偽值。
    if (request.url().endsWith(path) && request.method() === "POST") posts++;
    if (
      request.url().endsWith("/api/admin/reassignments") &&
      request.method() === "GET"
    )
      reads++;
  });
  await stage.selectOption("finance");
  await reviewer.selectOption({ label: "finance" });
  await reason.fill("舊版不可保存");
  const [stale] = await Promise.all([
    // 明確確認拒絕是後端版本衝突，沒有落入未知寫入分支。
    page.waitForResponse((response) => response.url().endsWith(path)),
    save.click(),
  ]);
  expect(stale.status()).toBe(409);
  expect((await stale.json()).code).toBe("stale_version");
  await expect(panel.getByRole("alert")).toContainText("待辦狀態或人選已變更");
  await expect(save).toHaveCount(0);
  expect(reads).toBe(0);
  await refresh.click();
  await expect(row.getByText("finance", { exact: true })).toBeVisible();
  expect(reads).toBe(1);
  expect(posts).toBe(1);

  // 用新版明確選擇不同人選；真實 POST 提交後只中斷回到瀏覽器的傳輸。
  await editReassignment(row, number);
  await stage.selectOption("finance");
  await reviewer.selectOption({ label: actorName("finance") });
  await reason.fill("已提交但回應遺失");
  await page.route(
    `**${path}`,
    async (route) => {
      // 瀏覽器收不到 204，實際資料庫仍保存該次寫入。
      const committed = await route.fetch();
      expect(committed.status()).toBe(204);
      await route.abort("failed");
    },
    { times: 1 },
  );
  await save.click();
  await expect(panel.getByRole("alert")).toContainText("改派結果未確認");
  await expect(panel.getByRole("alert")).toBeFocused();
  await expect(save).toHaveCount(0);
  expect(reads).toBe(1);

  // 往返組織面板及同帳號身分恢復都保留標記，沒有背景 GET 或 POST。
  await page.getByRole("button", { name: "組織管理", exact: true }).click();
  await expect(page.getByText("「有限改派」有待讀取的結果。")).toBeVisible();
  await reassignments(page);
  await expect(refresh).toBeEnabled();
  expect(reads).toBe(1);
  await context.clearCookies({ name: "procurement.csrf" });
  await refresh.click();
  await expect(panel).toHaveCount(0);
  await expect(
    page.getByRole("region", { name: "請先確認登入狀態" }),
  ).toBeFocused();
  await page.getByRole("button", { name: "重新確認身分" }).click();
  await expect(panel.getByRole("alert")).toContainText("改派結果未確認");
  expect(reads).toBe(2);

  // 申請人從實際已保存資料確認兩筆同關改派；撤回後管理讀回只知道案件已離開待辦。
  const businessHeaders = { "X-CSRF-TOKEN": await csrf(request) };
  const detail = await request.get(`/api/requisitions/${id}`, {
    headers: businessHeaders,
  });
  expect(detail.status()).toBe(200);
  const persisted: RequisitionDetailDto = await detail.json();
  expect(
    persisted.history.map(
      // 確認只有競爭者及第二次瀏覽器寫入留下改派，舊版沒有部分寫入。
      ({ kind, stage, reassignment }) => [
        kind,
        stage,
        reassignment?.reason ?? null,
      ],
    ),
  ).toEqual([
    ["submitted", null, null],
    ["reassigned", "finance", "另一工作階段先改派"],
    ["reassigned", "finance", "已提交但回應遺失"],
  ]);
  const withdrawn = await request.post(`/api/requisitions/${id}/withdraw`, {
    headers: businessHeaders,
    data: { version: persisted.version },
  });
  expect(withdrawn.status()).toBe(200);
  await refresh.click();
  await expect(panel.getByRole("alert")).toHaveText(
    "原改派結果仍未確認。該單目前已不在待辦清單中。",
  );
  await expect(row).toHaveCount(0);
  expect(reads).toBe(3);
  expect(posts).toBe(2);

  // 重新送審後，申請人仍能在畫面分辨原輪兩筆同關改派與目前的新輪次。
  const previous: RequisitionDetailDto = await withdrawn.json();
  const resubmitted = await request.post(`/api/requisitions/${id}/submit`, {
    headers: businessHeaders,
    data: { version: previous.version },
  });
  expect(resubmitted.status()).toBe(200);
  await apiLogin(page.request, actorName("applicant"), finalPassword);
  await page.reload();
  const business = await approvals(page);
  await openApproval(business, subject);
  await history(business, [
    `第 1 輪 · 送出簽核 · ${actorName("applicant")}`,
    "第 1 輪 · 財務改派 · admin",
    "第 1 輪 · 財務改派 · admin",
    `第 1 輪 · 撤回修改 · ${actorName("applicant")}`,
    `第 2 輪 · 送出簽核 · ${actorName("applicant")}`,
  ]);
  const events = business
    .getByRole("list", { name: "操作歷程" })
    .getByRole("listitem");
  await expect(events.nth(1)).toContainText("另一工作階段先改派");
  await expect(events.nth(2)).toContainText("已提交但回應遺失");
});

// 驗證 204 後的讀回與換帳號隔離，以及明確登出時清除未確認結果。
test("有限改派已保存與讀回失敗分開，分頁換帳號不沿用管理資料", async ({
  page,
  context,
  request,
}) => {
  // 由業務帳號建立待辦，再由同頁面的管理身分開啟改派。
  const subject = `已確認改派-${runId}`;
  await apiLogin(request, actorName("applicant"), finalPassword);
  const { id, number } = await seedPending(request, subject);
  await apiLogin(page.request, "admin", demoAccounts.admin);
  await page.reload();
  const panel = await reassignments(page);
  const row = reassignmentRow(panel, number);
  const form = await editReassignment(row, number);
  await form.getByLabel("接手審核者").selectOption({ label: "manager" });
  await form.getByLabel("改派原因").fill("確認保存後改登入");
  let posts = 0;
  page.on("request", (request) => {
    // 只計提交次數，不記錄本文或憑證。
    if (
      request.method() === "POST" &&
      request.url().endsWith(`/api/admin/reassignments/${id}`)
    )
      posts++;
  });
  await page.route(
    "**/api/admin/reassignments",
    async (route) => {
      // POST 仍走真實 API，僅使接續的 GET 傳輸失敗。
      await route.abort("failed");
    },
    { times: 1 },
  );
  await form.getByRole("button", { name: "保存改派" }).click();
  const confirmed = panel.getByRole("status").filter({ hasText: "改派已保存" });
  await expect(confirmed).toHaveText(
    "改派已保存。請重新讀取結果以取得目前待辦。",
  );
  await expect(panel.getByRole("alert")).toContainText("無法讀取改派待辦");
  await expect(row).toHaveCount(0);

  // 同帳號恢復先保留確認結果，重新進面板仍須使用者明確觸發讀回。
  const refresh = panel.getByRole("button", { name: "重新讀取結果" });
  await page.getByRole("button", { name: "重新確認身分" }).click();
  await expect(refresh).toBeEnabled();
  await expect(confirmed).toBeVisible();
  await expect(row).toHaveCount(0);

  // 在另一分頁更換共用 Cookie；改派 GET 必須帶舊管理權杖並遭真實 API 拒絕。
  const second = await context.newPage();
  await apiLogin(second.request, actorName("applicant"), finalPassword);
  const [denied] = await Promise.all([
    // 檢查該次讀回的 HTTP 回應，不能因另一次查詢的錯誤而誤判。
    page.waitForResponse((response) =>
      response.url().endsWith("/api/admin/reassignments"),
    ),
    refresh.click(),
  ]);
  expect(denied.status()).toBe(400);
  expect((await denied.json()).code).toBe("invalid_csrf");
  expect(Boolean(denied.request().headers()["x-csrf-token"])).toBe(true);
  await expect(panel).toHaveCount(0);
  await expect(page.getByText(number, { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "重新確認身分" }).click();
  await expect(page.getByRole("region", { name: "目前身分" })).toContainText(
    actorName("applicant"),
  );
  await expect(
    page.getByRole("button", { name: "有限改派", exact: true }),
  ).toHaveCount(0);
  await expect(page.getByText("改派已保存", { exact: false })).toHaveCount(0);

  // 先前的確認只屬原操作，已確認換人後再回原帳號不恢復該標記。
  await apiLogin(second.request, "admin", demoAccounts.admin);
  await page.getByRole("button", { name: "重新確認身分" }).click();
  await reassignments(page);
  await expect(row.getByText("manager", { exact: true })).toBeVisible();
  await expect(refresh).toHaveCount(0);
  expect(posts).toBe(1);

  // 另一筆真實改派已提交但回應中斷，確認主動登出會結束本頁的待讀回狀態。
  await editReassignment(row, number);
  await form
    .getByLabel("接手審核者")
    .selectOption({ label: actorName("manager") });
  await form.getByLabel("改派原因").fill("登出前遺失回應");
  await page.route(
    `**/api/admin/reassignments/${id}`,
    async (route) => {
      // 真實寫入只送一次；讓畫面無法取得已保存的 204。
      const committed = await route.fetch();
      expect(committed.status()).toBe(204);
      await route.abort("failed");
    },
    { times: 1 },
  );
  await form.getByRole("button", { name: "保存改派" }).click();
  await expect(panel.getByRole("alert")).toContainText("改派結果未確認");
  await expect(refresh).toBeEnabled();
  await page.getByRole("button", { name: "登出", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "登入", exact: true }),
  ).toBeVisible();
  await expect(panel).toHaveCount(0);
  await expect(page.getByText("改派結果未確認", { exact: false })).toHaveCount(
    0,
  );

  // 同一管理帳號重新登入後由普通 GET 載入目前人選，不恢復標記或重送 POST。
  await login(page, "admin");
  await reassignments(page);
  await expect(
    row.getByText(actorName("manager"), { exact: true }),
  ).toBeVisible();
  await expect(refresh).toHaveCount(0);
  await expect(panel.getByRole("alert")).toHaveCount(0);
  expect(posts).toBe(2);
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
    // 原主管沒有本人請購，撤權後真實授權清單應顯示原本的空資料提示。
    await expect(manager.getByText("目前沒有可查閱的請購。")).toBeVisible();
    await expect(manager.getByRole("region", { name: "請購列表" })).toHaveCount(
      0,
    );
    await expect(manager.getByText("沒有符合搜尋條件的請購。")).toHaveCount(0);

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
