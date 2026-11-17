// 真實管理畫面驗證組織設定；故障攔截只測試瀏覽器恢復，不替代 API／PostgreSQL 測試。
import { randomBytes } from "node:crypto";
import { readFileSync, statSync } from "node:fs";
import {
  expect,
  test,
  type ElementHandle,
  type Locator,
  type Page,
  type Request,
} from "@playwright/test";
import type { OrganizationDto } from "../src/api";

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

// 在表單卸載後檢查原欄位，只回傳是否清空，避免診斷輸出實際密碼。
async function passwordIsEmpty(input: ElementHandle<HTMLElement | SVGElement>) {
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

// 部門列以快照名稱識別，讓測試能確認寫入後只有一筆正確的資料。
function departmentRow(organization: Locator, name: string) {
  return organization
    .getByRole("table", { name: "部門清單", exact: true })
    .getByRole("row", { name, exact: true });
}

// 帳號列以唯一帳號查找，避免對其他使用者打開編輯表單。
function accountRow(organization: Locator, name: string) {
  return organization
    .getByRole("table", { name: "業務帳號清單", exact: true })
    .getByRole("row", { name, exact: true });
}

// 新增與編輯只顯示各自的表單，清單與批次控制從文件卸載。
async function expectOrganizationEditor(organization: Locator, title: string) {
  await expect(
    organization.getByRole("heading", { name: title, exact: true }),
  ).toBeVisible();
  await expect(organization.getByRole("table")).toHaveCount(0);
  await expect(organization.getByRole("checkbox")).toHaveCount(0);
  await expect(
    organization.getByRole("button", { name: "批次停用", exact: true }),
  ).toHaveCount(0);
  await expect(
    organization.getByRole("button", { name: /^新增(部門|業務帳號)表單$/ }),
  ).toHaveCount(0);
}

// 四種管理表單的保存與取消都維持同列，且觸控目標至少 44px 高。
async function expectOrganizationFormActions(
  form: Locator,
  label: string,
  width: number,
) {
  // 以實際 viewport 與按鈕外框驗證排版，不只檢查指定尺寸。
  const viewportWidth = await form.evaluate(
    // 讀取瀏覽器目前實際寬度，確認調整尺寸已套用。
    () => window.innerWidth,
  );
  expect(viewportWidth).toBe(width);
  const submit = form.getByRole("button", { name: label, exact: true });
  const cancel = form.getByRole("button", { name: "取消", exact: true });
  await expect(submit).toBeVisible();
  await expect(cancel).toBeVisible();
  await expect(cancel).toHaveAttribute("type", "button");
  const submitBox = await submit.boundingBox();
  const cancelBox = await cancel.boundingBox();
  if (!submitBox || !cancelBox) throw new Error("保存操作列不可見。");

  // X 軸順序和一致的 Y 座標確認兩個操作實際水平排列。
  expect(submitBox.x).toBeGreaterThanOrEqual(0);
  expect(submitBox.x + submitBox.width).toBeLessThan(cancelBox.x);
  expect(cancelBox.x).toBeGreaterThanOrEqual(0);
  expect(Math.abs(submitBox.y - cancelBox.y)).toBeLessThanOrEqual(1);
  expect(cancelBox.x + cancelBox.width).toBeLessThanOrEqual(width);
  expect(submitBox.height).toBeGreaterThanOrEqual(44);
  expect(cancelBox.height).toBeGreaterThanOrEqual(44);
}

// 保存後可從對應清單重新開啟資料，沒有留下任何編輯表單。
async function expectOrganizationList(
  organization: Locator,
  name: "部門清單" | "業務帳號清單",
) {
  await expect(
    organization.getByRole("table", { name, exact: true }),
  ).toBeVisible();
  await expect(organization.getByRole("form")).toHaveCount(0);
}

// 以實際 viewport、鍵盤操作及清單捲動區檢查兩種資源的管理畫面。
async function organizationLayout(
  page: Page,
  runId: string,
  accountName: string,
) {
  const region = page.getByRole("region", { name: "組織管理" });
  const menu = region.getByRole("navigation", { name: "組織管理選單" });
  const departments = menu.getByRole("button", {
    name: "部門管理",
    exact: true,
  });
  const accounts = menu.getByRole("button", {
    name: "業務帳號管理",
    exact: true,
  });
  const add = region.getByRole("button", {
    name: "新增部門表單",
    exact: true,
  });

  // 返回清單會捨棄未保存的輸入，所以整段版面操作不能出現任何組織寫入。
  let writes = 0;
  const trackWrite = (request: Request) => {
    if (
      request.method() === "POST" &&
      new URL(request.url()).pathname.startsWith("/api/admin/")
    )
      writes++;
  };
  page.on("request", trackWrite);

  // 桌機垂直排列，手機橫向切換；兩種寬度都檢查四種表單與取消焦點。
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await departments.click();
    await expect(departments).toHaveAttribute("aria-pressed", "true");
    await expect(region.getByRole("form")).toHaveCount(0);
    await expect(region.getByRole("table", { name: "部門清單" })).toBeVisible();
    const departmentsBox = (await departments.boundingBox())!;
    const accountsBox = (await accounts.boundingBox())!;
    if (width === 1280) {
      expect(departmentsBox.x).toBe(accountsBox.x);
      expect(departmentsBox.y + departmentsBox.height).toBeLessThan(
        accountsBox.y,
      );
    } else {
      expect(departmentsBox.y).toBe(accountsBox.y);
      expect(departmentsBox.x + departmentsBox.width).toBeLessThan(
        accountsBox.x,
      );
    }
    await region
      .getByRole("button", { name: "重新讀取組織設定", exact: true })
      .focus();
    await page.keyboard.press("Tab");
    await expect(departments).toBeFocused();
    await page.keyboard.press("Tab");
    await expect(accounts).toBeFocused();
    await page.keyboard.press("Tab");
    await expect(add).toBeFocused();
    await page.keyboard.press("Enter");
    const createHeading = region.getByRole("heading", {
      name: "新增部門",
      exact: true,
    });
    await expect(createHeading).toBeFocused();
    await expectOrganizationEditor(region, "新增部門");
    await expect(
      region.getByRole("button", { name: "儲存部門設定", exact: true }),
    ).toHaveCount(1);
    const createDepartmentForm = region.getByRole("form", {
      name: "新增部門",
    });
    await expectOrganizationFormActions(
      createDepartmentForm,
      "儲存部門設定",
      width,
    );
    const backToDepartments = createDepartmentForm.getByRole("button", {
      name: "取消",
      exact: true,
    });
    const saveDepartment = createDepartmentForm.getByRole("button", {
      name: "儲存部門設定",
      exact: true,
    });
    await page.keyboard.press("Tab");
    const departmentName = region.getByLabel("新部門名稱", { exact: true });
    await expect(departmentName).toBeFocused();
    await expect(departmentName).toHaveValue("");
    await departmentName.fill("尚未保存的部門");
    await expect(departmentName).toHaveValue("尚未保存的部門");
    await page.keyboard.press("Tab");
    await expect(saveDepartment).toBeFocused();
    await page.keyboard.press("Tab");
    await expect(backToDepartments).toBeFocused();
    await page.keyboard.press("Enter");
    await expectOrganizationList(region, "部門清單");
    await expect(
      region.getByRole("heading", { name: "部門管理", exact: true }),
    ).toBeFocused();
    await expect(departmentRow(region, "尚未保存的部門")).toHaveCount(0);

    // 編輯部門仍保留完整原生欄位；取消以鍵盤離開不提交草稿。
    const editedDepartmentName = `e2e-dept-${runId}-edited`;
    await departmentRow(region, editedDepartmentName)
      .getByRole("button", {
        name: `編輯 ${editedDepartmentName}`,
        exact: true,
      })
      .click();
    await expectOrganizationEditor(region, "編輯部門");
    const editDepartmentForm = region.getByRole("form", { name: "編輯部門" });
    const editDepartmentHeading = region.getByRole("heading", {
      name: "編輯部門",
      exact: true,
    });
    await expect(editDepartmentHeading).toBeFocused();
    await expectOrganizationFormActions(
      editDepartmentForm,
      "儲存部門設定",
      width,
    );
    await page.keyboard.press("Tab");
    await expect(editDepartmentForm.getByLabel("部門名稱")).toBeFocused();
    const cancelEditDepartment = editDepartmentForm.getByRole("button", {
      name: "取消",
      exact: true,
    });
    await cancelEditDepartment.focus();
    await page.keyboard.press("Enter");
    await expectOrganizationList(region, "部門清單");
    await expect(departmentRow(region, editedDepartmentName)).toBeVisible();

    // 帳號表格保留完整欄位，只讓手機在自己的清單區內橫向捲動。
    await accounts.click();
    await expect(accounts).toHaveAttribute("aria-pressed", "true");
    await expect(region.getByRole("table", { name: "部門清單" })).toHaveCount(
      0,
    );
    const tableRegion = region.getByRole("region", { name: "業務帳號清單" });
    await expect(tableRegion.getByRole("columnheader").last()).toHaveText(
      "操作",
    );
    const dimensions = await tableRegion.evaluate(
      // 讀取實際 viewport、文件及清單外框，避免只相信指定的尺寸。
      (element) => ({
        viewportWidth: window.innerWidth,
        documentWidth: document.documentElement.scrollWidth,
        tableWidth: element.getBoundingClientRect().width,
        visible: element.clientWidth,
        content: element.scrollWidth,
      }),
    );
    expect(dimensions.viewportWidth).toBe(width);
    expect(dimensions.documentWidth).toBeLessThanOrEqual(width);
    expect(dimensions.tableWidth).toBeLessThanOrEqual(width);
    if (width === 390)
      expect(dimensions.content).toBeGreaterThan(dimensions.visible);

    // 新增業務帳號表單不掛載清單；原生鍵盤啟動取消後返回同一清單。
    await region
      .getByRole("button", { name: "新增業務帳號表單", exact: true })
      .click();
    await expectOrganizationEditor(region, "新增業務帳號");
    const createAccountForm = region.getByRole("form", {
      name: "新增業務帳號",
    });
    await expect(
      region.getByRole("heading", { name: "新增業務帳號", exact: true }),
    ).toBeFocused();
    await expectOrganizationFormActions(
      createAccountForm,
      "儲存業務帳號設定",
      width,
    );
    await page.keyboard.press("Tab");
    await expect(
      createAccountForm.getByLabel("新業務帳號名稱", { exact: true }),
    ).toBeFocused();
    const cancelCreateAccount = createAccountForm.getByRole("button", {
      name: "取消",
      exact: true,
    });
    await cancelCreateAccount.focus();
    await page.keyboard.press("Enter");
    await expectOrganizationList(region, "業務帳號清單");

    // 從列尾開啟帳號編輯，確認原生選單與其餘編輯控制項都在可用範圍。
    const edit = accountRow(region, accountName).getByRole("button", {
      name: `編輯 ${accountName}`,
      exact: true,
    });
    await edit.press("Enter");
    const editHeading = region.getByRole("heading", {
      name: "編輯業務帳號",
      exact: true,
    });
    await expect(editHeading).toBeFocused();
    await expectOrganizationEditor(region, "編輯業務帳號");
    const editor = region.getByRole("form", { name: "編輯業務帳號" });
    await expectOrganizationFormActions(editor, "儲存業務帳號設定", width);
    const controls = editor.locator("input, select, button");
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
    await editor.getByLabel("所屬部門", { exact: true }).focus();
    await expect(editor.getByLabel("所屬部門", { exact: true })).toBeFocused();

    // 證據只寫入既有隔離目錄，且遮蔽畫面上的所有輸入欄位。
    await page.screenshot({
      path: `/evidence/organization-${runId}-${width}.png`,
      fullPage: true,
      mask: [page.locator("input")],
    });

    // 編輯頁的取消位於欄位及保存之後；取消不提交且焦點回到清單標題。
    const backToAccounts = editor.getByRole("button", {
      name: "取消",
      exact: true,
    });
    const accountStatus = editor.getByLabel("帳號狀態", { exact: true });
    await editHeading.focus();
    await page.keyboard.press("Tab");
    await expect(accountStatus).toBeFocused();
    await accountStatus.selectOption("false");
    await expect(accountStatus).toHaveValue("false");
    await backToAccounts.focus();
    await page.keyboard.press("Enter");
    await expectOrganizationList(region, "業務帳號清單");
    await expect(
      region.getByRole("heading", { name: "業務帳號管理", exact: true }),
    ).toBeFocused();
    await expect(
      accountRow(region, accountName).getByRole("cell", {
        name: "啟用",
        exact: true,
      }),
    ).toBeVisible();
  }
  page.off("request", trackWrite);
  expect(writes).toBe(0);
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
  const menu = organization.getByRole("navigation", { name: "組織管理選單" });
  const departmentMenu = menu.getByRole("button", {
    name: "部門管理",
    exact: true,
  });
  const accountMenu = menu.getByRole("button", {
    name: "業務帳號管理",
    exact: true,
  });
  await expect(departmentMenu).toHaveAttribute("aria-pressed", "true");
  await expect(
    organization.getByRole("table", { name: "部門清單" }),
  ).toBeVisible();
  await expect(
    organization.getByRole("table", { name: "業務帳號清單" }),
  ).toHaveCount(0);
  await expect(organization.getByRole("form")).toHaveCount(0);
  await organization
    .getByRole("button", { name: "新增部門表單", exact: true })
    .click();

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
    .getByRole("button", { name: "儲存部門設定", exact: true })
    .click();
  await expect(organization.getByRole("alert")).toContainText(
    "部門名稱須為 1–100 字元",
  );
  await expectOrganizationEditor(organization, "新增部門");
  await expect(departmentNameInput).toHaveValue(tooLongDepartmentName);
  await departmentNameInput.fill(departmentName);
  await newDepartmentForm
    .getByRole("button", { name: "儲存部門設定", exact: true })
    .click();
  await expectStatus(organization, "已新增部門，已讀取目前設定。");
  await expectOrganizationList(organization, "部門清單");

  // 修改新部門名稱與狀態，保存返回清單後再開啟核對讀回的表單。
  await departmentRow(organization, departmentName)
    .getByRole("button", { name: `編輯 ${departmentName}`, exact: true })
    .click();
  await expect(
    organization.getByRole("form", { name: "新增部門" }),
  ).toHaveCount(0);
  const editDepartmentForm = organization.getByRole("form", {
    name: "編輯部門",
  });
  await expectOrganizationEditor(organization, "編輯部門");
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
  await expectOrganizationList(organization, "部門清單");
  await departmentRow(organization, editedDepartmentName)
    .getByRole("button", { name: `編輯 ${editedDepartmentName}`, exact: true })
    .click();
  await expect(
    organization.getByRole("form", { name: "編輯部門" }).getByLabel("部門名稱"),
  ).toHaveValue(editedDepartmentName);
  await expect(
    organization.getByRole("form", { name: "編輯部門" }).getByLabel("部門狀態"),
  ).toHaveValue("true");

  // 以隨機帳號及臨時密碼建立業務帳號，密碼只經頁面內 secret 注入。
  await accountMenu.click();
  await expect(accountMenu).toHaveAttribute("aria-pressed", "true");
  await expect(organization.getByRole("form")).toHaveCount(0);
  await organization
    .getByRole("button", { name: "新增業務帳號表單", exact: true })
    .click();
  const newAccountForm = organization.getByRole("form", {
    name: "新增業務帳號",
  });
  await expectOrganizationEditor(organization, "新增業務帳號");
  await expect(
    organization.getByRole("button", { name: "儲存業務帳號設定", exact: true }),
  ).toHaveCount(1);
  await newAccountForm
    .getByLabel("新業務帳號名稱", { exact: true })
    .fill(accountName);
  const createPassword = newAccountForm.getByLabel("臨時密碼", {
    exact: true,
  });
  await secret(createPassword, temporaryPassword);
  const originalCreatePassword = await createPassword.elementHandle();
  await newAccountForm
    .getByLabel("所屬部門", { exact: true })
    .selectOption({ label: editedDepartmentName });
  await newAccountForm
    .getByRole("button", { name: "儲存業務帳號設定", exact: true })
    .click();
  await expectStatus(organization, "已新增業務帳號，已讀取目前設定。");
  await expectOrganizationList(organization, "業務帳號清單");
  expect(await passwordIsEmpty(originalCreatePassword)).toBe(true);
  await originalCreatePassword.dispose();

  // 對讀回的新帳號修改角色及兩位不同的種子審核人。
  await accountRow(organization, accountName)
    .getByRole("button", { name: `編輯 ${accountName}`, exact: true })
    .click();
  await expect(
    organization.getByRole("form", { name: "新增業務帳號" }),
  ).toHaveCount(0);
  const editAccountForm = organization.getByRole("form", {
    name: "編輯業務帳號",
  });
  await expectOrganizationEditor(organization, "編輯業務帳號");
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
  await expectOrganizationList(organization, "業務帳號清單");
  await accountRow(organization, accountName)
    .getByRole("button", { name: `編輯 ${accountName}`, exact: true })
    .click();
  await expect(
    organization.getByText("已儲存業務帳號設定，已讀取目前設定。", {
      exact: true,
    }),
  ).toHaveCount(0);
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
  await expect(departmentMenu).toHaveAttribute("aria-pressed", "true");
  await accountMenu.click();
  const persistedRow = accountRow(organization, accountName);
  await expect(
    persistedRow.getByRole("cell", { name: editedDepartmentName, exact: true }),
  ).toBeVisible();
  await expect(
    persistedRow.getByRole("cell", { name: "主管、財務", exact: true }),
  ).toBeVisible();
  await expect(
    persistedRow.getByRole("cell", { name: "啟用", exact: true }),
  ).toBeVisible();
  await persistedRow
    .getByRole("button", { name: `編輯 ${accountName}`, exact: true })
    .click();
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

  // 在兩種寬度驗證鍵盤與清單，取消未送出的測試草稿後準備重設密碼。
  await organizationLayout(page, runId, accountName);
  await persistedRow
    .getByRole("button", { name: `編輯 ${accountName}`, exact: true })
    .click();

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

    // 收合重設區時捨棄輸入；只計算重設 POST，不記錄請求內容或密碼。
    let resetPosts = 0;
    page.on("request", (request) => {
      if (
        request.method() === "POST" &&
        new URL(request.url()).pathname.endsWith("/reset-password")
      )
        resetPosts++;
    });
    const resetDisclosure = organization.getByText("重設密碼", { exact: true });
    const resetField = organization.locator("#reset-account-password");
    await expect(resetField).toHaveCount(0);
    await resetDisclosure.click();
    const resetForm = organization.getByRole("form", {
      name: "重設業務帳號密碼",
    });
    const resetPasswordInput = resetForm.getByLabel("新的臨時密碼", {
      exact: true,
    });
    await secret(resetPasswordInput, randomBytes(24).toString("hex"));
    await resetDisclosure.click();
    await expect(resetField).toHaveCount(0);
    await resetDisclosure.click();
    const originalResetPassword = await resetPasswordInput.elementHandle();
    expect(await passwordIsEmpty(originalResetPassword)).toBe(true);
    expect(resetPosts).toBe(0);

    // 管理員以真實 UI 送出重設，回到清單後確認原欄位也已清空。
    await secret(resetPasswordInput, resetPassword);
    await resetForm
      .getByRole("button", { name: "重設業務帳號密碼", exact: true })
      .click();
    await expectStatus(organization, "已重設業務帳號密碼，已讀取目前設定。");
    await expectOrganizationList(organization, "業務帳號清單");
    expect(await passwordIsEmpty(originalResetPassword)).toBe(true);
    await originalResetPassword.dispose();
    expect(resetPosts).toBe(1);

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
    .getByRole("button", { name: "新增部門表單", exact: true })
    .click();
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByLabel("新部門名稱", { exact: true })
    .fill(originalName);
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByRole("button", { name: "儲存部門設定", exact: true })
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
    await departmentRow(organization, originalName)
      .getByRole("button", { name: `編輯 ${originalName}`, exact: true })
      .click();
    await departmentRow(secondOrganization, originalName)
      .getByRole("button", { name: `編輯 ${originalName}`, exact: true })
      .click();
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
    await expectOrganizationList(organization, "部門清單");

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
    const cancelStaleEdit = staleEditForm.getByRole("button", {
      name: "取消",
      exact: true,
    });
    let releaseStalePost!: () => void;
    const stalePostHeld = new Promise<void>(
      // 解除屏障後才讓這筆真實過期版本寫入抵達 API。
      (resolve) => {
        releaseStalePost = resolve;
      },
    );
    await secondPage.route(
      "**/api/admin/departments/*",
      async (route) => {
        // 只暫停下一筆部門保存；解除後仍由真實 API 判定版本衝突。
        await stalePostHeld;
        await route.continue();
      },
      { times: 1 },
    );

    try {
      await staleEditForm
        .getByLabel("部門名稱", { exact: true })
        .fill(loserName);
      const staleRequest = secondPage.waitForRequest(
        // 確認暫停屏障前，目標 POST 已從瀏覽器實際送出。
        (request) =>
          request.method() === "POST" &&
          new URL(request.url()).pathname.startsWith("/api/admin/departments/"),
      );
      const staleResponse = secondPage.waitForResponse(
        // 鎖定實際部門保存回應，確認真實版本衝突與畫面提示。
        (response) =>
          response.request().method() === "POST" &&
          new URL(response.url()).pathname.startsWith(
            "/api/admin/departments/",
          ),
      );
      await staleEditForm
        .getByRole("button", { name: "儲存部門設定", exact: true })
        .click();
      await staleRequest;
      await expectStatus(secondOrganization, "正在處理…");
      await expect(cancelStaleEdit).toBeDisabled();
      releaseStalePost();

      expect((await staleResponse).status()).toBe(409);
      await expect(secondOrganization.getByRole("alert")).toContainText(
        "資料已變更",
      );
      await expectOrganizationEditor(secondOrganization, "編輯部門");
      await expect(staleEditForm.getByLabel("部門名稱")).toHaveValue(loserName);
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
      // 409 後欄位及保存鎖定，但操作完成後仍可取消離開。
      await expect(cancelStaleEdit).toBeEnabled();

      // 手動 GET 留在原表單，以勝者的新版本替換舊草稿並重新啟用保存。
      await secondOrganization
        .getByRole("button", { name: "重新讀取組織設定", exact: true })
        .click();
      await expectStatus(secondOrganization, "已讀取目前設定。");
      await expectOrganizationEditor(secondOrganization, "編輯部門");
      await expect(
        staleEditForm.getByLabel("部門名稱", { exact: true }),
      ).toHaveValue(winnerName);
      await expect(
        staleEditForm.getByRole("button", {
          name: "儲存部門設定",
          exact: true,
        }),
      ).toBeEnabled();
      expect(stalePosts).toBe(1);

      // 讀回後以鍵盤取消，清單呈現勝者且不重送過期草稿。
      await cancelStaleEdit.focus();
      await secondPage.keyboard.press("Enter");
      await expectOrganizationList(secondOrganization, "部門清單");
      await expect(departmentRow(secondOrganization, winnerName)).toBeVisible();
      await expect(departmentRow(secondOrganization, loserName)).toHaveCount(0);
      expect(stalePosts).toBe(1);
    } finally {
      // 即使 pending 斷言失敗，也解除屏障避免第二個工作階段遺留攔截。
      releaseStalePost();
    }

    // 攔截一次保存和讀回以模擬目標消失，避免改動真實部門資料。
    await departmentRow(secondOrganization, winnerName)
      .getByRole("button", { name: `編輯 ${winnerName}`, exact: true })
      .click();
    await secondPage.route(
      "**/api/admin/departments/*",
      // 只有測試中的這次保存模擬 404；其餘操作繼續使用真 API。
      (route) =>
        route.fulfill({
          status: 404,
          json: { code: "not_found", message: "找不到指定的設定資料。" },
        }),
      { times: 1 },
    );
    await staleEditForm.getByLabel("部門名稱", { exact: true }).fill(loserName);
    await staleEditForm
      .getByRole("button", { name: "儲存部門設定", exact: true })
      .click();
    await expect(secondOrganization.getByRole("alert")).toContainText(
      "找不到指定的設定資料。",
    );
    await expectOrganizationEditor(secondOrganization, "編輯部門");
    await expect(staleEditForm.getByLabel("部門名稱")).toHaveValue(loserName);
    await expect(staleEditForm.getByLabel("部門名稱")).toBeDisabled();

    await secondPage.route(
      "**/api/admin/organization",
      // 保留真實快照的其他資料，只從這一次回應省略指定部門。
      async (route) => {
        const response = await route.fetch();
        expect(response.status()).toBe(200);
        const snapshot: OrganizationDto = await response.json();
        await route.fulfill({
          response,
          json: {
            ...snapshot,
            departments: snapshot.departments.filter(
              (department) => department.name !== winnerName,
            ),
          },
        });
      },
      { times: 1 },
    );
    const reload = secondOrganization.getByRole("button", {
      name: "重新讀取組織設定",
      exact: true,
    });
    await reload.click();
    await expectOrganizationList(secondOrganization, "部門清單");
    await expect(departmentRow(secondOrganization, winnerName)).toHaveCount(0);
    await reload.click();
    await expect(departmentRow(secondOrganization, winnerName)).toBeVisible();
    await expectOrganizationList(secondOrganization, "部門清單");
    expect(stalePosts).toBe(2);
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
  await expectStatus(organization, "已讀取目前設定。");

  // 建立一筆可勾選的專用部門，使本案例單獨執行時也有列尾及批次操作可驗證。
  const staleDepartmentName = `e2e-pending-${runId}`;
  const identity = await page.request.get("/api/auth/me");
  expect(identity.status()).toBe(200);
  const fixture = await page.request.post("/api/admin/departments", {
    headers: { "X-CSRF-TOKEN": identity.headers()["x-csrf-token"] },
    data: { name: staleDepartmentName },
  });
  expect(fixture.status()).toBe(204);
  await organization
    .getByRole("button", { name: "重新讀取組織設定", exact: true })
    .click();
  const staleDepartment = departmentRow(organization, staleDepartmentName);
  const staleCheckbox = staleDepartment.getByRole("checkbox");
  await staleCheckbox.check();
  const batch = organization.getByRole("button", {
    name: "批次停用",
    exact: true,
  });
  await expect(batch).toBeEnabled();

  // 持有真實 204 觸發的第一次讀回，先驗證等待中的畫面，再模擬傳輸失敗。
  let nextOrganizationReadFailure: "held" | "transport" | "auth" | null = null;
  let releaseReadback!: () => void;
  const readbackHeld = new Promise<void>((resolve) => {
    releaseReadback = resolve;
  });
  let organizationReads = 0;
  let readbackFailurePosts = 0;
  page.on("request", (request) => {
    // 只記錄此段的部門寫入次數，避免保存後續讀回時重送。
    if (
      request.method() === "POST" &&
      new URL(request.url()).pathname === "/api/admin/departments"
    )
      readbackFailurePosts++;
  });
  await page.route("**/api/admin/organization", async (route) => {
    // 只有明確標記的下一次 GET 使用故障回應，其餘讀取走真實 API。
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    organizationReads++;
    const failure = nextOrganizationReadFailure;
    nextOrganizationReadFailure = null;
    if (failure === "held") {
      await readbackHeld;
      await route.abort("failed");
    } else if (failure === "transport") {
      await route.abort("failed");
    } else if (failure === "auth") {
      await route.fulfill({
        status: 401,
        json: { code: "unauthenticated", message: "請先登入。" },
      });
    } else {
      await route.continue();
    }
  });
  const addDepartment = organization.getByRole("button", {
    name: "新增部門表單",
    exact: true,
  });
  nextOrganizationReadFailure = "held";
  await addDepartment.click();
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByLabel("新部門名稱", { exact: true })
    .fill(readbackFailureName);
  try {
    const [created] = await Promise.all([
      page.waitForResponse(
        // 對真實新增回應確認 204，避免把讀回失敗誤認為交易未提交。
        (response) =>
          response.request().method() === "POST" &&
          new URL(response.url()).pathname === "/api/admin/departments",
      ),
      organization
        .getByRole("form", { name: "新增部門" })
        .getByRole("button", { name: "儲存部門設定", exact: true })
        .click(),
    ]);
    expect(created.status()).toBe(204);

    // GET 尚未完成時已切回舊清單，原勾選可供核對，所有寫入控制暫停。
    await expect.poll(() => organizationReads).toBe(1);
    await expectOrganizationList(organization, "部門清單");
    await expectStatus(organization, "正在處理…");
    await expect(staleCheckbox).toBeChecked();
    await expect(staleCheckbox).toBeDisabled();
    await expect(addDepartment).toBeDisabled();
    await expect(batch).toBeDisabled();
    await expect(
      staleDepartment.getByRole("button", {
        name: `編輯 ${staleDepartmentName}`,
        exact: true,
      }),
    ).toBeDisabled();
    await expect(
      staleDepartment.getByRole("button", {
        name: `停用 ${staleDepartmentName}`,
        exact: true,
      }),
    ).toBeDisabled();
    expect(readbackFailurePosts).toBe(1);
  } finally {
    // 即使畫面斷言失敗也解除攔截，避免後續工作階段卡在這輪讀取。
    releaseReadback();
  }
  await expect(organization.getByRole("alert")).toContainText(
    "已新增部門，但無法讀回設定。",
  );
  await expectOrganizationList(organization, "部門清單");
  await expect(
    organization.getByRole("button", { name: "新增部門表單", exact: true }),
  ).toBeDisabled();
  await expect(
    organization.getByRole("checkbox", {
      name: "選取所有可停用部門",
      exact: true,
    }),
  ).toBeDisabled();
  await expect(departmentRow(organization, readbackFailureName)).toHaveCount(0);
  expect(organizationReads).toBe(1);
  expect(readbackFailurePosts).toBe(1);

  // 往返管理面板只保留原交易的確認；等使用者手動讀回，才載入設定或表單。
  const organizationTab = page.getByRole("button", {
    name: "組織管理",
    exact: true,
  });
  const reassignmentTab = page.getByRole("button", {
    name: "有限改派",
    exact: true,
  });
  const reload = organization.getByRole("button", {
    name: "重新讀取組織設定",
    exact: true,
  });
  await reassignmentTab.click();
  await expect(organization).toHaveCount(0);
  nextOrganizationReadFailure = "transport";
  await organizationTab.click();
  await expect(organization.getByRole("alert")).toContainText("已新增部門");
  await expect(reload).toBeEnabled();
  await expect(organization.getByRole("form")).toHaveCount(0);
  expect(organizationReads).toBe(1);

  // 手動讀回再次遇到傳輸失敗，仍保留成功確認及下一次讀取入口。
  await reload.click();
  await expect(organization.getByRole("alert")).toContainText(
    "已新增部門，但無法讀回設定。",
  );
  expect(organizationReads).toBe(2);

  // 若讀回要求重新驗證，確認在身分未定時不顯示，重驗原帳號才恢復。
  nextOrganizationReadFailure = "auth";
  await reload.click();
  await expect(
    page.getByRole("heading", { name: "請先確認登入狀態", exact: true }),
  ).toBeVisible();
  await expect(page.getByText("已新增部門", { exact: false })).toHaveCount(0);
  await page.getByRole("button", { name: "重新確認身分", exact: true }).click();
  await expect(organization.getByRole("alert")).toContainText("已新增部門");
  await expect(reload).toBeEnabled();
  expect(organizationReads).toBe(3);

  // 成功讀回只載入同一筆真實資料，並在畫面保留本次成功結果。
  await reload.click();
  await expectStatus(organization, "已新增部門，已讀取目前設定。");
  await expect(departmentRow(organization, readbackFailureName)).toHaveCount(1);
  expect(organizationReads).toBe(4);

  // 成功讀回已消耗待讀回標記；往返後可正常載入設定且只留下同一筆部門。
  await reassignmentTab.click();
  await expect(organization).toHaveCount(0);
  await organizationTab.click();
  await expectStatus(organization, "已讀取目前設定。");
  await expect(departmentRow(organization, readbackFailureName)).toHaveCount(1);
  expect(organizationReads).toBe(5);
  expect(readbackFailurePosts).toBe(1);

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
    .getByRole("button", { name: "新增部門表單", exact: true })
    .click();
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByLabel("新部門名稱", { exact: true })
    .fill(unknownDepartmentName);
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByRole("button", { name: "儲存部門設定", exact: true })
    .click();
  await expect(organization.getByRole("alert")).toContainText(
    "先前有組織操作結果未確認，可能已生效",
  );
  await expect(organization.getByRole("alert")).toContainText(
    "系統不會自動重送",
  );
  await expectOrganizationEditor(organization, "新增部門");
  const unknownDraft = organization.getByLabel("新部門名稱", { exact: true });
  await expect(unknownDraft).toHaveValue(unknownDepartmentName);
  await expect(unknownDraft).toBeDisabled();
  expect(unknownPosts).toBe(1);

  // 手動 GET 需以獨立 alert/status 同時呈現未決結果與本次讀回成功。
  await organization
    .getByRole("button", { name: "重新讀取組織設定", exact: true })
    .click();
  await expect(organization.getByRole("alert")).toContainText(
    "先前有組織操作結果未確認，可能已生效",
  );
  await expectStatus(organization, "已讀取目前設定。");
  await expectOrganizationEditor(organization, "新增部門");
  await expect(unknownDraft).toBeEnabled();
  await expect(unknownDraft).toHaveValue("");
  await organization.getByRole("button", { name: "取消", exact: true }).click();
  await expect(departmentRow(organization, unknownDepartmentName)).toHaveCount(
    1,
  );
  expect(unknownPosts).toBe(1);

  // 未決提示保留時仍可明確建立另一部門，成功狀態與兩筆資料都要讀回。
  await organization
    .getByRole("button", { name: "新增部門表單", exact: true })
    .click();
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByLabel("新部門名稱", { exact: true })
    .fill(successAfterUnknownName);
  await organization
    .getByRole("form", { name: "新增部門" })
    .getByRole("button", { name: "儲存部門設定", exact: true })
    .click();
  await expect(organization.getByRole("alert")).toContainText(
    "先前有組織操作結果未確認，可能已生效",
  );
  await expectStatus(organization, "已新增部門，已讀取目前設定。");
  await expectOrganizationList(organization, "部門清單");
  await expect(departmentRow(organization, unknownDepartmentName)).toHaveCount(
    1,
  );
  await expect(
    departmentRow(organization, successAfterUnknownName),
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
    .getByRole("button", { name: "新增部門表單", exact: true })
    .click();
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
      .getByRole("button", { name: "儲存部門設定", exact: true })
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
    departmentRow(recoveredOrganization, readbackFailureName),
  ).toHaveCount(1);
  await expect(
    departmentRow(recoveredOrganization, unknownDepartmentName),
  ).toHaveCount(1);
  await expect(
    departmentRow(recoveredOrganization, successAfterUnknownName),
  ).toHaveCount(1);
  await expect(
    departmentRow(recoveredOrganization, csrfRecoveryDepartmentName),
  ).toHaveCount(0);
  expect(unknownPosts).toBe(1);
  expect(csrfRecoveryPosts).toBe(1);
});

// 先註冊對話框處理，無論確認或取消都讓原生對話框結束後才進行斷言。
async function deactivationConfirmation(
  page: Page,
  action: Locator,
  accept: boolean,
) {
  const [dialog] = await Promise.all([
    page.waitForEvent("dialog").then(
      // 保留確認文字供檢查，處理對話框時不觸發第二次點擊。
      async (dialog) => {
        const result = { type: dialog.type(), message: dialog.message() };
        if (accept) await dialog.accept();
        else await dialog.dismiss();
        return result;
      },
    ),
    action.click(),
  ]);
  expect(dialog.type).toBe("confirm");
  return dialog.message;
}

// 真實停用與讀回保留資料與版本，取消確認不送出請求。
test("組織清單單筆與批次停用可取消並重新啟用", async ({ page }) => {
  const runId = randomBytes(6).toString("hex");
  const departmentName = `e2e-deactivate-dept-${runId}`;
  const otherDepartmentName = `e2e-deactivate-other-${runId}`;
  const accountNames = [
    `e2e-deactivate-a-${runId}`,
    `e2e-deactivate-b-${runId}`,
  ];
  await login(page, "admin", accounts.admin);
  const organization = page.getByRole("region", { name: "組織管理" });
  await expectStatus(organization, "已讀取目前設定。");

  // 用真實管理 API 建立本案的專用目標，避免動到隔離庫已存在的示範帳號。
  const identity = await page.request.get("/api/auth/me");
  expect(identity.status()).toBe(200);
  const token = identity.headers()["x-csrf-token"];
  expect(Boolean(token)).toBe(true);
  const headers = { "X-CSRF-TOKEN": token };
  for (const name of [departmentName, otherDepartmentName]) {
    const response = await page.request.post("/api/admin/departments", {
      headers,
      data: { name },
    });
    expect(response.status()).toBe(204);
  }
  const beforeAccounts = await page.request.get("/api/admin/organization");
  expect(beforeAccounts.status()).toBe(200);
  const departments: OrganizationDto = await beforeAccounts.json();
  const department = departments.departments.find(
    // 用本案的唯一名稱取得伺服器建立的識別，不猜測版本或資料列順序。
    (item) => item.name === departmentName,
  );
  if (!department) throw new Error("找不到本案部門 fixture。");
  for (const userName of accountNames) {
    const response = await page.request.post("/api/admin/accounts", {
      headers,
      data: {
        userName,
        password: randomBytes(24).toString("hex"),
        departmentId: department.id,
        isManager: true,
        isFinance: false,
      },
    });
    expect(response.status()).toBe(204);
  }
  const current = await page.request.get("/api/admin/organization");
  expect(current.status()).toBe(200);
  const snapshot: OrganizationDto = await current.json();
  const accountTargets = snapshot.accounts
    .filter((item) => accountNames.includes(item.userName))
    .map(({ id, version }) => ({ id, version }));
  expect(accountTargets).toHaveLength(2);

  // 只記錄停用要求的識別和版本，避免保存其他寫入或憑證。
  const requests: { path: string; body: unknown }[] = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (
      request.method() === "POST" &&
      (path === "/api/admin/departments/deactivate" ||
        path === "/api/admin/accounts/deactivate")
    )
      requests.push({ path, body: request.postDataJSON() });
  });
  const reload = organization.getByRole("button", {
    name: "重新讀取組織設定",
    exact: true,
  });
  await reload.click();
  const deptRow = departmentRow(organization, departmentName);
  const otherRow = departmentRow(organization, otherDepartmentName);
  const selectDept = deptRow.getByRole("checkbox", {
    name: `選取 ${departmentName}`,
  });
  const selectOther = otherRow.getByRole("checkbox", {
    name: `選取 ${otherDepartmentName}`,
  });
  const deptAction = deptRow.getByRole("button", {
    name: `停用 ${departmentName}`,
    exact: true,
  });
  const selectAll = organization.getByRole("checkbox", {
    name: "選取所有可停用部門",
    exact: true,
  });
  const batch = organization.getByRole("button", {
    name: "批次停用",
    exact: true,
  });
  await expect(batch).toBeDisabled();

  // 驗證全選、部分勾選及單列停用範圍，即使勾選的是另一列也只寫入當前列。
  await selectAll.check();
  await expect(selectDept).toBeChecked();
  await expect(selectOther).toBeChecked();
  await selectAll.uncheck();
  await selectOther.check();
  await expect(selectAll).toHaveJSProperty("indeterminate", true);
  await expect(batch).toBeEnabled();
  expect(await deactivationConfirmation(page, deptAction, false)).toContain(
    departmentName,
  );
  expect(requests).toHaveLength(0);
  await expect(selectOther).toBeChecked();
  await expect(
    deptRow.getByRole("cell", { name: "啟用", exact: true }),
  ).toBeVisible();
  expect(await deactivationConfirmation(page, deptAction, true)).toContain(
    "會保留歷程",
  );
  await expectStatus(organization, "已停用部門，已讀取目前設定。");
  expect(requests).toEqual([
    {
      path: "/api/admin/departments/deactivate",
      body: { items: [{ id: department.id, version: department.version }] },
    },
  ]);
  await expect(
    deptRow.getByRole("cell", { name: "停用", exact: true }),
  ).toBeVisible();
  await expect(deptAction).toBeDisabled();
  await expect(selectDept).toBeDisabled();
  await expect(selectOther).not.toBeChecked();
  await expect(batch).toBeDisabled();

  // 切換清單會清除勾選；帳號批次取消後保留兩筆，確認後只送一個請求。
  const menu = organization.getByRole("navigation", { name: "組織管理選單" });
  const accountsMenu = menu.getByRole("button", {
    name: "業務帳號管理",
    exact: true,
  });
  const departmentsMenu = menu.getByRole("button", {
    name: "部門管理",
    exact: true,
  });
  await selectOther.check();
  await accountsMenu.click();
  await expect(batch).toBeDisabled();
  await departmentsMenu.click();
  await expect(selectOther).not.toBeChecked();
  await accountsMenu.click();

  const rows = accountNames.map((name) => accountRow(organization, name));
  for (const row of rows) {
    await row.getByRole("checkbox").check();
    await expect(
      row.getByRole("cell", {
        name: `${departmentName}（已停用）`,
        exact: true,
      }),
    ).toBeVisible();
  }
  await expectStatus(organization, "已選取 2 筆");
  expect(await deactivationConfirmation(page, batch, false)).toContain(
    "2 筆業務帳號",
  );
  expect(requests).toHaveLength(1);
  for (const row of rows) {
    await expect(row.getByRole("checkbox")).toBeChecked();
    await expect(
      row.getByRole("cell", { name: "啟用", exact: true }),
    ).toBeVisible();
  }
  expect(await deactivationConfirmation(page, batch, true)).toContain(
    "帳號現有的登入將失效",
  );
  await expectStatus(organization, "已停用 2 筆業務帳號，已讀取目前設定。");
  expect(requests).toHaveLength(2);
  expect(requests[1]).toEqual({
    path: "/api/admin/accounts/deactivate",
    body: { items: accountTargets },
  });
  await expect(batch).toBeDisabled();
  for (const row of rows) {
    await expect(row.getByRole("checkbox")).toBeDisabled();
    await expect(
      row.getByRole("cell", { name: "停用", exact: true }),
    ).toBeVisible();
  }

  // 整頁重新載入後仍保留原資料，從列尾編輯可單獨重新啟用其中一筆。
  await page.reload();
  await expect(
    deptRow.getByRole("cell", { name: "停用", exact: true }),
  ).toBeVisible();
  await deptRow
    .getByRole("button", { name: `編輯 ${departmentName}`, exact: true })
    .click();
  const editDepartment = organization.getByRole("form", { name: "編輯部門" });
  await expectOrganizationEditor(organization, "編輯部門");
  await editDepartment
    .getByLabel("部門狀態", { exact: true })
    .selectOption("true");
  await editDepartment
    .getByRole("button", { name: "儲存部門設定", exact: true })
    .click();
  await expectStatus(organization, "已儲存部門設定，已讀取目前設定。");
  await expectOrganizationList(organization, "部門清單");
  await expect(selectDept).toBeEnabled();

  await accountsMenu.click();
  for (const row of rows) {
    await expect(
      row.getByRole("cell", { name: "停用", exact: true }),
    ).toBeVisible();
    await expect(
      row.getByRole("cell", { name: "主管", exact: true }),
    ).toBeVisible();
    await expect(
      row.getByRole("cell", { name: departmentName, exact: true }),
    ).toBeVisible();
  }
  await rows[0]
    .getByRole("button", { name: `編輯 ${accountNames[0]}`, exact: true })
    .click();
  const editAccount = organization.getByRole("form", { name: "編輯業務帳號" });
  await expectOrganizationEditor(organization, "編輯業務帳號");
  await editAccount
    .getByLabel("帳號狀態", { exact: true })
    .selectOption("true");
  await editAccount
    .getByRole("button", { name: "儲存業務帳號設定", exact: true })
    .click();
  await expectStatus(organization, "已儲存業務帳號設定，已讀取目前設定。");
  await expectOrganizationList(organization, "業務帳號清單");
  await expect(
    rows[0].getByRole("cell", { name: "啟用", exact: true }),
  ).toBeVisible();
  await expect(rows[0].getByRole("checkbox")).toBeEnabled();
  await expect(
    rows[1].getByRole("cell", { name: "停用", exact: true }),
  ).toBeVisible();
  expect(requests).toHaveLength(2);
});
