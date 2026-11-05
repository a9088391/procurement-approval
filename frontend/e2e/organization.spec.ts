// 真實管理畫面驗證組織設定；故障攔截只測試瀏覽器恢復，不替代 API／PostgreSQL 測試。
import { randomBytes } from "node:crypto";
import { readFileSync, statSync } from "node:fs";
import { expect, test, type Locator, type Page } from "@playwright/test";

// 目標或憑證權限不符就中止，避免測試寫入展示環境。
if (
  process.env.TEST_TARGET !== "procurement-approval-browser" ||
  process.env.BASE_URL !== "http://procurement-web.test:8080" ||
  (statSync("/local").mode & 0o777) !== 0o700 ||
  (statSync("/local/initial-accounts.json").mode & 0o777) !== 0o600
)
  throw new Error("拒絕未知或未受保護的瀏覽器測試目標。");

// 只從隔離環境讀管理及候選審核人憑證，不在診斷中保存內容。
const accounts: Record<string, string> = JSON.parse(
  readFileSync("/local/initial-accounts.json", "utf8"),
);
for (const name of ["admin", "manager", "finance"]) {
  // 開跑前確認本案例會使用的三種既有帳號都有足夠長的密碼。
  if (typeof accounts[name] !== "string" || accounts[name].length < 12)
    throw new Error("示範帳號 fixture 不完整。");
}

// 只累計錯誤數，不保存可能包含敏感資料的瀏覽器錯誤內容。
const runtimeErrors = new WeakMap<Page, number>();
let monitoredPages: Page[] = [];

// 記錄本案例開啟的每個頁面，避免第二個 Cookie context 漏掉執行期錯誤。
function monitorRuntimeErrors(page: Page) {
  // 每分頁獨立計數，且只保留數字供案例結束時檢查。
  runtimeErrors.set(page, 0);
  monitoredPages.push(page);
  page.on(
    "pageerror",
    // 只計數未捕捉的頁面例外，不記錄錯誤內容。
    () => runtimeErrors.set(page, (runtimeErrors.get(page) ?? 0) + 1),
  );
  page.on(
    "console",
    // 測試會刻意中斷讀取，略過資源失敗訊息並統計其他 console error。
    (entry) => {
      if (
        entry.type() === "error" &&
        !entry.text().startsWith("Failed to load resource:")
      )
        runtimeErrors.set(page, (runtimeErrors.get(page) ?? 0) + 1);
    },
  );
}

// 每案先準備匿名頁面與錯誤計數，等待既有瀏覽器服務實際可讀。
test.beforeEach(async ({ page, request }) => {
  // 測試新分頁時重建追蹤集合，保留本案例自己的錯誤統計。
  monitoredPages = [];
  monitorRuntimeErrors(page);

  // 先輪詢網站入口是否可讀，以實際回應判斷服務已啟動。
  await expect
    .poll(
      // 網站尚未連通時回傳未就緒值，讓輪詢在期限內繼續等待。
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

// 即使主要行為斷言通過，案例結束仍須確認所有頁面沒有執行錯誤。
test.afterEach(async () => {
  // 每頁只回報錯誤數，避免輸出可能含敏感資料的瀏覽器訊息。
  for (const page of monitoredPages) {
    expect(runtimeErrors.get(page) ?? 0).toBe(0);
  }
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

// 畫面登入的密碼經 secret 輸入，避免進入 fill 的診斷紀錄。
async function login(page: Page, name: string, password: string) {
  // 透過可存取標籤填寫帳密，再點擊真正的提交按鈕。
  await page.getByLabel("帳號", { exact: true }).fill(name);
  await secret(page.getByLabel("密碼", { exact: true }), password);
  await page.getByRole("button", { name: "登入", exact: true }).click();
}

// 只回傳密碼欄位是否清空，避免失敗診斷意外輸出實際欄位值。
async function passwordIsEmpty(input: Locator) {
  return input.evaluate(
    // 只把遮蔽後的布林結果交回測試程序。
    (element) => (element as HTMLInputElement).value.length === 0,
  );
}

// 同時鎖定 status 角色及完整文字，避免舊提示或 pending 訊息解除等待。
async function expectStatus(root: Locator | Page, text: string) {
  const exactStatus = root
    .getByRole("status")
    .and(root.getByText(text, { exact: true }));
  await expect(exactStatus).toBeVisible();
}

// 以實際 viewport、鍵盤焦點及可見控制項邊界檢查管理畫面。
async function organizationLayout(page: Page, runId: string) {
  const region = page.getByRole("region", { name: "組織管理" });

  // 以鍵盤從重新讀取按鈕前往第一個表單欄位，確認焦點順序可操作。
  await region
    .getByRole("button", { name: "重新讀取組織設定", exact: true })
    .focus();
  await page.keyboard.press("Tab");
  await expect(region.getByLabel("新部門名稱", { exact: true })).toBeFocused();

  // 以桌機與手機實際寬度檢查頁面和管理控制項沒有水平裁切。
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 900 });
    expect(
      await page.evaluate(
        // 從瀏覽器讀取實際寬度，核對 viewport 已套用。
        () => window.innerWidth,
      ),
    ).toBe(width);
    expect(
      await page.evaluate(
        // 比對文件與 viewport 寬度，檢查頁面水平捲動。
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    ).toBe(true);

    // 逐一檢查管理區的輸入、選單與按鈕都可見且位於 viewport 內。
    const controls = region.locator("input, select, button");
    for (const control of await controls.all()) {
      await expect(control).toBeVisible();
      const box = await control.boundingBox();
      expect(
        box !== null &&
          box.width > 0 &&
          box.x >= 0 &&
          box.x + box.width <= width,
      ).toBe(true);
    }

    // 證據只寫入既有隔離目錄，且遮蔽畫面上的所有輸入欄位。
    await page.screenshot({
      path: `/evidence/organization-${runId}-${width}.png`,
      fullPage: true,
      mask: [page.locator("input")],
    });
  }
}

// 建立並編輯部門及業務帳號，驗證持久化、強制改密碼與錯誤恢復路徑。
test("組織管理建立、重讀、雙審核人及強制改密碼", async ({ page, browser }) => {
  const runId = randomBytes(6).toString("hex");
  const departmentName = `e2e-dept-${runId}`;
  const editedDepartmentName = `${departmentName}-edited`;
  const accountName = `e2e-reviewer-${runId}`;
  const temporaryPassword = randomBytes(24).toString("hex");
  const resetPassword = randomBytes(24).toString("hex");
  const changedPassword = randomBytes(24).toString("hex");

  // 管理員透過真正的登入流程進入組織管理畫面。
  await login(page, "admin", accounts.admin);
  const organization = page.getByRole("region", { name: "組織管理" });
  await expect(organization).toBeVisible();
  await expect(page.getByRole("navigation", { name: "請購功能" })).toHaveCount(
    0,
  );
  await expect(
    organization.getByRole("form", { name: "新增部門" }),
  ).toBeVisible();

  // 伺服器拒絕超長部門名稱後仍保留欄位，管理員修正同一草稿再保存。
  const newDepartmentForm = organization.getByRole("form", {
    name: "新增部門",
  });
  const departmentNameInput = newDepartmentForm.getByLabel("新部門名稱", {
    exact: true,
  });
  const tooLongDepartmentName = "x".repeat(101);
  await departmentNameInput.fill(tooLongDepartmentName);
  await newDepartmentForm
    .getByRole("button", { name: "新增部門", exact: true })
    .click();
  await expect(organization.getByRole("alert")).toContainText(
    "部門名稱須為 1–100 字元",
  );
  await expect(departmentNameInput).toHaveValue(tooLongDepartmentName);
  await departmentNameInput.fill(departmentName);
  await newDepartmentForm
    .getByRole("button", { name: "新增部門", exact: true })
    .click();
  await expectStatus(organization, "已新增部門，已讀取目前設定。");

  // 修改新部門名稱與狀態，再確認保存後由管理快照重建表單。
  const departmentPicker = organization.getByLabel("選擇部門", {
    exact: true,
  });
  await departmentPicker.selectOption({ label: departmentName });
  const editDepartmentForm = organization.getByRole("form", {
    name: "編輯部門",
  });
  await editDepartmentForm
    .getByLabel("部門名稱", { exact: true })
    .fill(editedDepartmentName);
  await editDepartmentForm
    .getByLabel("部門狀態", { exact: true })
    .selectOption("true");
  await editDepartmentForm
    .getByRole("button", { name: "儲存部門設定", exact: true })
    .click();
  await expectStatus(organization, "已儲存部門設定，已讀取目前設定。");
  await expect(
    organization.getByRole("form", { name: "編輯部門" }).getByLabel("部門名稱"),
  ).toHaveValue(editedDepartmentName);
  await expect(
    organization.getByRole("form", { name: "編輯部門" }).getByLabel("部門狀態"),
  ).toHaveValue("true");

  // 以隨機帳號及臨時密碼建立業務帳號，密碼只經頁面內 secret 注入。
  const newAccountForm = organization.getByRole("form", {
    name: "新增業務帳號",
  });
  await newAccountForm
    .getByLabel("新業務帳號名稱", { exact: true })
    .fill(accountName);
  const createPassword = newAccountForm.getByLabel("臨時密碼", {
    exact: true,
  });
  await secret(createPassword, temporaryPassword);
  await newAccountForm
    .getByLabel("所屬部門", { exact: true })
    .selectOption({ label: editedDepartmentName });
  await newAccountForm
    .getByRole("button", { name: "新增業務帳號", exact: true })
    .click();
  await expectStatus(organization, "已新增業務帳號，已讀取目前設定。");
  expect(await passwordIsEmpty(createPassword)).toBe(true);

  // 對讀回的新帳號修改角色及兩位不同的種子審核人。
  const accountPicker = organization.getByLabel("選擇業務帳號", {
    exact: true,
  });
  await accountPicker.selectOption({ label: accountName });
  const editAccountForm = organization.getByRole("form", {
    name: "編輯業務帳號",
  });
  await editAccountForm
    .getByLabel("帳號狀態", { exact: true })
    .selectOption("true");
  await editAccountForm
    .getByLabel("所屬部門", { exact: true })
    .selectOption({ label: editedDepartmentName });
  await editAccountForm
    .getByLabel("主管角色", { exact: true })
    .selectOption("true");
  await editAccountForm
    .getByLabel("財務角色", { exact: true })
    .selectOption("true");
  await editAccountForm
    .getByLabel("預設主管審核人", { exact: true })
    .selectOption({ label: "manager" });
  await editAccountForm
    .getByLabel("預設財務審核人", { exact: true })
    .selectOption({ label: "finance" });
  await editAccountForm
    .getByRole("button", { name: "儲存業務帳號設定", exact: true })
    .click();
  await expectStatus(organization, "已儲存業務帳號設定，已讀取目前設定。");
  await expect(editAccountForm).toContainText("目前主管／財務角色：主管、財務");
  await expect(editAccountForm).toContainText(
    `目前部門：${editedDepartmentName}`,
  );
  await expect(editAccountForm).toContainText("目前主管審核人：manager");
  await expect(editAccountForm).toContainText("目前財務審核人：finance");

  // 額外重讀並重新載入頁面，確認角色與兩關人選來自持久化讀取。
  await organization
    .getByRole("button", { name: "重新讀取組織設定", exact: true })
    .click();
  await expectStatus(organization, "已讀取目前設定。");
  await expect(editAccountForm).toContainText("目前主管／財務角色：主管、財務");
  await page.reload();
  await expect(page.getByRole("region", { name: "組織管理" })).toBeVisible();
  await page
    .getByLabel("選擇業務帳號", { exact: true })
    .selectOption({ label: accountName });
  const persistedAccountForm = page.getByRole("form", {
    name: "編輯業務帳號",
  });
  await expect(persistedAccountForm).toContainText(
    "目前主管／財務角色：主管、財務",
  );
  await expect(persistedAccountForm).toContainText(
    `目前部門：${editedDepartmentName}`,
  );
  await expect(persistedAccountForm).toContainText("目前主管審核人：manager");
  await expect(persistedAccountForm).toContainText("目前財務審核人：finance");

  // 以目前讀回的部門及帳號畫面檢查鍵盤焦點、真實手機與桌機寬度。
  await page
    .getByLabel("選擇部門", { exact: true })
    .selectOption({ label: editedDepartmentName });
  await organizationLayout(page, runId);
  await page
    .getByLabel("選擇業務帳號", { exact: true })
    .selectOption({ label: accountName });

  // 在獨立 Cookie context 登入新帳號，確認第一次登入只進入改密碼狀態。
  const businessContext = await browser.newContext({
    baseURL: process.env.BASE_URL,
  });
  const businessPage = await businessContext.newPage();
  monitorRuntimeErrors(businessPage);
  try {
    await businessPage.goto("/");
    await expect(
      businessPage.getByRole("heading", { name: "登入", exact: true }),
    ).toBeVisible();
    await login(businessPage, accountName, temporaryPassword);
    await expectStatus(
      businessPage,
      "請先變更臨時密碼。新密碼須與臨時密碼不同，變更後請用新密碼重新登入。",
    );
    await expect(
      businessPage.getByRole("navigation", { name: "請購功能" }),
    ).toHaveCount(0);
    await expect(
      businessPage.getByRole("heading", { name: "組織管理", exact: true }),
    ).toHaveCount(0);

    // 管理員以真實 UI 重設臨時密碼，並確認舊欄位不再保留輸入。
    const resetForm = organization.getByRole("form", {
      name: "重設業務帳號密碼",
    });
    const resetPasswordInput = resetForm.getByLabel("新的臨時密碼", {
      exact: true,
    });
    await secret(resetPasswordInput, resetPassword);
    await resetForm
      .getByRole("button", { name: "重設業務帳號密碼", exact: true })
      .click();
    await expectStatus(organization, "已重設業務帳號密碼，已讀取目前設定。");
    expect(await passwordIsEmpty(resetPasswordInput)).toBe(true);

    // 以同一個業務 Cookie context 確認重設已撤銷舊登入，重載後回到匿名。
    const revokedIdentity = await businessPage.request.get("/api/auth/me");
    expect(revokedIdentity.status()).toBe(401);
    await businessPage.reload();
    await expect(
      businessPage.getByRole("heading", { name: "登入", exact: true }),
    ).toBeVisible();

    // 第二組臨時密碼仍須先強制變更，成功後以新密碼建立一般業務身分。
    await login(businessPage, accountName, resetPassword);
    await expectStatus(
      businessPage,
      "請先變更臨時密碼。新密碼須與臨時密碼不同，變更後請用新密碼重新登入。",
    );
    await expect(
      businessPage.getByRole("heading", { name: "組織管理", exact: true }),
    ).toHaveCount(0);
    await secret(
      businessPage.getByLabel("目前密碼", { exact: true }),
      resetPassword,
    );
    await secret(
      businessPage.getByLabel("新密碼", { exact: true }),
      changedPassword,
    );
    await businessPage
      .getByRole("button", { name: "變更密碼", exact: true })
      .click();
    await expect(
      businessPage.getByRole("heading", { name: "登入", exact: true }),
    ).toBeVisible();
    await login(businessPage, accountName, changedPassword);
    await expect(
      businessPage.getByRole("heading", { name: "目前身分", exact: true }),
    ).toBeVisible();
    await expect(businessPage.locator("dd").nth(0)).toHaveText(accountName);
    await expect(businessPage.locator("dd").nth(1)).toHaveText("業務帳號");
    await expect(
      businessPage.getByRole("heading", { name: "組織管理", exact: true }),
    ).toHaveCount(0);
  } finally {
    // 關閉只供本案例使用的 Cookie context，不讀取或輸出其中的密碼。
    await businessContext.close();
  }
});

// 兩個真實管理 Cookie 編輯同一版本，過期保存須拒絕且只能手動讀回勝者。
test("組織設定版本過期顯示衝突、停用編輯並讀回勝者", async ({
  page,
  browser,
}) => {
  const runId = randomBytes(6).toString("hex");
  const originalName = `e2e-race-${runId}`;
  const winnerName = `${originalName}-winner`;
  const loserName = `${originalName}-loser`;

  // 第一個管理工作階段登入並建立這個案例獨有的部門版本。
  await login(page, "admin", accounts.admin);
  const organization = page.getByRole("region", { name: "組織管理" });
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByLabel("新部門名稱", { exact: true })
    .fill(originalName);
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByRole("button", { name: "新增部門", exact: true })
    .click();
  await expectStatus(organization, "已新增部門，已讀取目前設定。");

  // 第二個 Cookie context 登入後讀取同一個初始版本，與第一頁保持隔離。
  const secondContext = await browser.newContext({
    baseURL: process.env.BASE_URL,
  });
  const secondPage = await secondContext.newPage();
  monitorRuntimeErrors(secondPage);
  try {
    await secondPage.goto("/");
    await expect(
      secondPage.getByRole("heading", { name: "登入", exact: true }),
    ).toBeVisible();
    await login(secondPage, "admin", accounts.admin);
    const secondOrganization = secondPage.getByRole("region", {
      name: "組織管理",
    });
    await expect(secondOrganization).toBeVisible();

    // 兩個表單選取同一部門，保留它們各自首次讀到的版本。
    await organization
      .getByLabel("選擇部門", { exact: true })
      .selectOption({ label: originalName });
    await secondOrganization
      .getByLabel("選擇部門", { exact: true })
      .selectOption({ label: originalName });
    const firstEditForm = organization.getByRole("form", {
      name: "編輯部門",
    });
    const staleEditForm = secondOrganization.getByRole("form", {
      name: "編輯部門",
    });
    await expect(firstEditForm.getByLabel("部門名稱")).toHaveValue(
      originalName,
    );
    await expect(staleEditForm.getByLabel("部門名稱")).toHaveValue(
      originalName,
    );

    // 第一個工作階段保存勝者，第二個仍使用舊版本提交不同名稱。
    await firstEditForm
      .getByLabel("部門名稱", { exact: true })
      .fill(winnerName);
    await firstEditForm
      .getByRole("button", { name: "儲存部門設定", exact: true })
      .click();
    await expectStatus(organization, "已儲存部門設定，已讀取目前設定。");

    let stalePosts = 0;
    secondPage.on(
      "request",
      // 計算此次第二頁的部門寫入次數，不記錄 URL、body 或帳密。
      (request) => {
        if (
          request.method() === "POST" &&
          new URL(request.url()).pathname.startsWith("/api/admin/departments/")
        )
          stalePosts++;
      },
    );
    await staleEditForm.getByLabel("部門名稱", { exact: true }).fill(loserName);
    await staleEditForm
      .getByRole("button", { name: "儲存部門設定", exact: true })
      .click();
    await expect(secondOrganization.getByRole("alert")).toContainText(
      "資料已變更",
    );
    await expect(secondOrganization.getByRole("alert")).toContainText(
      "請使用「重新讀取組織設定」",
    );
    await expect(staleEditForm.getByLabel("部門名稱")).toBeDisabled();
    await expect(
      staleEditForm.getByRole("button", {
        name: "儲存部門設定",
        exact: true,
      }),
    ).toBeDisabled();
    expect(stalePosts).toBe(1);

    // 手動 GET 後畫面顯示勝者的新版本，舊草稿不再能覆蓋保存值。
    await secondOrganization
      .getByRole("button", { name: "重新讀取組織設定", exact: true })
      .click();
    await expectStatus(secondOrganization, "已讀取目前設定。");
    await expect(
      secondOrganization
        .getByRole("form", { name: "編輯部門" })
        .getByLabel("部門名稱"),
    ).toHaveValue(winnerName);
    await expect(
      secondOrganization
        .getByLabel("選擇部門", { exact: true })
        .getByRole("option", { name: loserName, exact: true }),
    ).toHaveCount(0);
    expect(stalePosts).toBe(1);
  } finally {
    // 關閉此案例的第二組管理 Cookie，不改動資料或重送未確認操作。
    await secondContext.close();
  }
});

// 獨立驗證組織寫入的讀回失敗、未知結果後續操作與 CSRF 身分復原。
test("組織寫入恢復保留未知結果並完成後續保存", async ({ page }) => {
  const runId = randomBytes(6).toString("hex");
  const readbackFailureName = `e2e-readback-${runId}`;
  const unknownDepartmentName = `e2e-unknown-${runId}`;
  const successAfterUnknownName = `e2e-after-unknown-${runId}`;
  const csrfRecoveryDepartmentName = `e2e-csrf-${runId}`;

  // 此獨立案例使用一個全新的管理登入，避免主流程失敗遮蔽錯誤恢復。
  await login(page, "admin", accounts.admin);
  const organization = page.getByRole("region", { name: "組織管理" });
  await expect(organization).toBeVisible();

  // 真實新增收到 204 後中斷後續 GET，畫面須保留已保存但未讀回的狀態。
  let failNextOrganizationRead = false;
  await page.route("**/api/admin/organization", async (route) => {
    // 僅中斷被明確標記的下一次 GET，其餘讀取照常使用真 API。
    if (failNextOrganizationRead && route.request().method() === "GET") {
      failNextOrganizationRead = false;
      await route.abort("failed");
    } else {
      await route.continue();
    }
  });
  failNextOrganizationRead = true;
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByLabel("新部門名稱", { exact: true })
    .fill(readbackFailureName);
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByRole("button", { name: "新增部門", exact: true })
    .click();
  await expect(organization.getByRole("alert")).toContainText(
    "已新增部門，但無法讀回設定。",
  );
  await expect(
    organization
      .getByRole("form", { name: "新增部門" })
      .getByLabel("新部門名稱"),
  ).toBeDisabled();
  await organization
    .getByRole("button", { name: "重新讀取組織設定", exact: true })
    .click();
  await expectStatus(organization, "已新增部門，已讀取目前設定。");
  await expect(
    organization
      .getByLabel("選擇部門", { exact: true })
      .getByRole("option", { name: readbackFailureName, exact: true }),
  ).toHaveCount(1);

  // 只對唯一未知項目提交真 API，再中斷瀏覽器回應並保留 POST 次數。
  let unknownPosts = 0;
  await page.route("**/api/admin/departments", async (route) => {
    // 只截取目標名稱，讓後續成功保存與 CSRF 案例都繼續到真 API。
    if (route.request().method() === "POST") {
      const body = route.request().postDataJSON() as { name?: string };
      if (body.name === unknownDepartmentName) {
        unknownPosts++;
        const response = await route.fetch();
        expect(response.status()).toBe(204);
        await route.abort("failed");
      } else {
        await route.continue();
      }
    } else {
      await route.continue();
    }
  });
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByLabel("新部門名稱", { exact: true })
    .fill(unknownDepartmentName);
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByRole("button", { name: "新增部門", exact: true })
    .click();
  await expect(organization.getByRole("alert")).toContainText(
    "先前有組織操作結果未確認，可能已生效",
  );
  await expect(organization.getByRole("alert")).toContainText(
    "系統不會自動重送",
  );
  expect(unknownPosts).toBe(1);

  // 手動 GET 需以獨立 alert/status 同時呈現未決結果與本次讀回成功。
  await organization
    .getByRole("button", { name: "重新讀取組織設定", exact: true })
    .click();
  await expect(organization.getByRole("alert")).toContainText(
    "先前有組織操作結果未確認，可能已生效",
  );
  await expectStatus(organization, "已讀取目前設定。");
  await expect(
    organization
      .getByLabel("選擇部門", { exact: true })
      .getByRole("option", { name: unknownDepartmentName, exact: true }),
  ).toHaveCount(1);
  expect(unknownPosts).toBe(1);

  // 未決提示保留時仍可明確建立另一部門，成功狀態與兩筆資料都要讀回。
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByLabel("新部門名稱", { exact: true })
    .fill(successAfterUnknownName);
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByRole("button", { name: "新增部門", exact: true })
    .click();
  await expect(organization.getByRole("alert")).toContainText(
    "先前有組織操作結果未確認，可能已生效",
  );
  await expectStatus(organization, "已新增部門，已讀取目前設定。");
  await expect(
    organization
      .getByLabel("選擇部門", { exact: true })
      .getByRole("option", { name: unknownDepartmentName, exact: true }),
  ).toHaveCount(1);
  await expect(
    organization
      .getByLabel("選擇部門", { exact: true })
      .getByRole("option", { name: successAfterUnknownName, exact: true }),
  ).toHaveCount(1);
  expect(unknownPosts).toBe(1);

  // 真正移除測試防偽 Cookie；保留 session 及同一頁面記憶體中的 token。
  const cookiesBeforeClear = await page.context().cookies();
  const cookieNamesBeforeClear = cookiesBeforeClear.map(
    // 只取名稱，絕不把 Cookie 值交給斷言或診斷輸出。
    (cookie) => cookie.name,
  );
  expect(cookieNamesBeforeClear).toContain("procurement.session");
  expect(cookieNamesBeforeClear).toContain("procurement.csrf");
  await page.context().clearCookies({ name: "procurement.csrf" });
  const cookiesAfterClear = await page.context().cookies();
  const cookieNamesAfterClear = cookiesAfterClear.map(
    // 再次只取名稱，確認清除範圍精確且登入 Cookie 仍在。
    (cookie) => cookie.name,
  );
  expect(cookieNamesAfterClear).toContain("procurement.session");
  expect(cookieNamesAfterClear).not.toContain("procurement.csrf");

  // 證明舊記憶體 token 隨單次 POST 到達後端並由真實 CSRF 驗證拒絕。
  let csrfRecoveryPosts = 0;
  let retainedTokenHeader = false;
  page.on(
    "request",
    // 只保留目標 POST 次數與標頭是否存在，不讀取或輸出權杖值。
    (request) => {
      if (
        request.method() === "POST" &&
        new URL(request.url()).pathname === "/api/admin/departments"
      ) {
        csrfRecoveryPosts++;
        retainedTokenHeader = Boolean(request.headers()["x-csrf-token"]);
      }
    },
  );
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByLabel("新部門名稱", { exact: true })
    .fill(csrfRecoveryDepartmentName);
  const [csrfResponse] = await Promise.all([
    page.waitForResponse(
      // 鎖定真實組織新增請求，避免其他讀取解除等待。
      (response) =>
        response.request().method() === "POST" &&
        new URL(response.url()).pathname === "/api/admin/departments",
    ),
    organization
      .getByRole("form", { name: "新增部門" })
      .getByRole("button", { name: "新增部門", exact: true })
      .click(),
  ]);
  expect(csrfResponse.status()).toBe(400);
  expect((await csrfResponse.json()).code).toBe("invalid_csrf");
  expect(csrfRecoveryPosts).toBe(1);
  expect(retainedTokenHeader).toBe(true);
  await expect(
    page.getByRole("heading", { name: "請先確認登入狀態", exact: true }),
  ).toBeVisible();
  await expect(
    page
      .getByRole("alert")
      .and(
        page.getByText(
          "安全權杖已失效，請重新整理後再試。 請重新確認身分後，由您再次操作。",
          { exact: true },
        ),
      ),
  ).toBeVisible();
  await expect(page.getByRole("region", { name: "組織管理" })).toHaveCount(0);

  // 同管理工作階段只讀重驗後，舊錯誤清除且未知提醒與新讀回各自保留。
  await page.getByRole("button", { name: "重新確認身分", exact: true }).click();
  const recoveredOrganization = page.getByRole("region", {
    name: "組織管理",
  });
  await expect(recoveredOrganization).toBeVisible();
  await expect(recoveredOrganization.getByRole("alert")).toContainText(
    "先前有組織操作結果未確認，可能已生效",
  );
  await expectStatus(recoveredOrganization, "已讀取目前設定。");
  await expect(
    page.getByRole("alert").filter({ hasText: "安全權杖已失效" }),
  ).toHaveCount(0);
  await expect(
    recoveredOrganization
      .getByLabel("選擇部門", { exact: true })
      .getByRole("option", { name: readbackFailureName, exact: true }),
  ).toHaveCount(1);
  await expect(
    recoveredOrganization
      .getByLabel("選擇部門", { exact: true })
      .getByRole("option", { name: unknownDepartmentName, exact: true }),
  ).toHaveCount(1);
  await expect(
    recoveredOrganization
      .getByLabel("選擇部門", { exact: true })
      .getByRole("option", { name: successAfterUnknownName, exact: true }),
  ).toHaveCount(1);
  await expect(
    recoveredOrganization
      .getByLabel("選擇部門", { exact: true })
      .getByRole("option", { name: csrfRecoveryDepartmentName, exact: true }),
  ).toHaveCount(0);
  expect(unknownPosts).toBe(1);
  expect(csrfRecoveryPosts).toBe(1);
});
