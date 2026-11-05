// 管理表單只組裝核准的欄位；快照與恢復由管理入口集中協調。
import { useState, type FormEvent } from "react";
import type {
  BusinessAccountDto,
  DepartmentDto,
  OrganizationDto,
  OrganizationWrite,
} from "./api";

export type SubmitOrganization = (
  event: FormEvent<HTMLFormElement>,
  buildWrite: (values: FormData) => OrganizationWrite,
  confirmation: string,
) => Promise<void>;

interface FormProps {
  snapshot: OrganizationDto;
  revision: number;
  disabled: boolean;
  onSubmit: SubmitOrganization;
}

interface NamedFormProps extends FormProps {
  departmentName: (department: DepartmentDto) => string;
}

// 欄位值以表單原樣送出，名稱的修剪和長度由 API 統一判斷。
function field(values: FormData, name: string) {
  return String(values.get(name) ?? "");
}

// 部門與帳號表單使用同一份讀回資料及標籤，避免個別下拉欄位辨識方式不同。
export function OrganizationForms(props: FormProps) {
  // 先找出同名部門，讓長清單的每個選項不必再次掃描全部部門。
  const names = new Set<string>();
  const duplicates = new Set<string>();
  for (const department of props.snapshot.departments) {
    if (names.has(department.name)) duplicates.add(department.name);
    names.add(department.name);
  }

  // 停用部門顯示狀態；名稱重複才加穩定識別碼，以便選對記錄。
  function departmentName(department: DepartmentDto) {
    return `${department.name}${department.active ? "" : "（已停用）"}${duplicates.has(department.name) ? ` · ${department.id}` : ""}`;
  }

  // 兩區保留各自選取，讀回後才用新快照替換可編輯表單。
  return (
    <>
      <DepartmentManagement {...props} departmentName={departmentName} />
      <AccountManagement {...props} departmentName={departmentName} />
    </>
  );
}

// 部門管理以讀回資料呈現目前名稱與狀態，新增和更新各送自己的欄位。
function DepartmentManagement({
  snapshot,
  revision,
  disabled,
  onSubmit,
  departmentName,
}: NamedFormProps) {
  // 由已選識別對應目前快照，若重讀後目標已消失就不再顯示舊表單。
  const [selectedId, setSelectedId] = useState("");
  const { departments } = snapshot;
  const department = departments.find(
    // 使用穩定識別對應部門，容許不同部門使用相同名稱。
    (item) => item.id === selectedId,
  );

  // 讀回成功才以新資料重建選定部門的草稿，驗證拒絕時仍保留原輸入。
  return (
    <section aria-labelledby="department-title">
      <h3 id="department-title">部門管理</h3>
      <form
        key={revision}
        aria-label="新增部門"
        onSubmit={
          // 新增部門只送名稱，識別及啟用狀態由後端建立。
          (event) =>
            void onSubmit(
              event,
              // 欄位保留原字元，交由 API 修剪和驗證。
              (values) => ({
                path: "departments",
                body: { name: field(values, "name") },
              }),
              "已新增部門",
            )
        }
      >
        <fieldset disabled={disabled}>
          <label htmlFor="new-department-name">新部門名稱</label>
          <input id="new-department-name" name="name" required />
          <p className="hint">去除首尾空白後須為 1–100 個字元。</p>
          <button type="submit">新增部門</button>
        </fieldset>
      </form>

      <label htmlFor="select-department">選擇部門</label>
      <select
        id="select-department"
        disabled={disabled}
        value={department?.id ?? ""}
        onChange={
          // 只切換畫面選取，未保存的內容不會送往後端。
          (event) => setSelectedId(event.currentTarget.value)
        }
      >
        <option value="">請選擇要編輯的部門</option>
        {departments.map(
          // 停用部門仍可選取以查閱或重新啟用。
          (item) => (
            <option key={item.id} value={item.id}>
              {departmentName(item)}
            </option>
          ),
        )}
      </select>
      {department ? (
        <form
          key={`${department.id}:${revision}`}
          aria-label="編輯部門"
          onSubmit={
            // 更新一律提交目前草稿所依據的版本，避免覆蓋其他工作階段。
            (event) =>
              void onSubmit(
                event,
                // 傳回明確的啟用狀態，並以穩定識別指向原部門。
                (values) => ({
                  path: `departments/${encodeURIComponent(department.id)}`,
                  body: {
                    name: field(values, "name"),
                    active: field(values, "active") === "true",
                    version: department.version,
                  },
                }),
                "已儲存部門設定",
              )
          }
        >
          <fieldset disabled={disabled}>
            <p>目前部門：{departmentName(department)}</p>
            <label htmlFor="department-name">部門名稱</label>
            <input
              id="department-name"
              name="name"
              defaultValue={department.name}
              required
            />
            <label htmlFor="department-active">部門狀態</label>
            <select
              id="department-active"
              name="active"
              defaultValue={String(department.active)}
            >
              <option value="true">啟用</option>
              <option value="false">停用</option>
            </select>
            <button type="submit">儲存部門設定</button>
          </fieldset>
        </form>
      ) : null}
    </section>
  );
}

// 部門欄位只列出啟用或原有歸屬，讓停用部門及舊的未分配狀態可以保留。
function DepartmentSelect({
  departments,
  departmentName,
  id,
  account,
}: {
  departments: DepartmentDto[];
  departmentName: NamedFormProps["departmentName"];
  id: string;
  account?: BusinessAccountDto;
}) {
  // 新建需要選部門；只允許原本無歸屬的既有帳號維持未分配。
  const currentId = account?.departmentId ?? "";
  return (
    <>
      <label htmlFor={id}>所屬部門</label>
      <select
        id={id}
        name="departmentId"
        defaultValue={currentId}
        required={!account}
      >
        {account?.departmentId === null ? (
          <option value="">未分配</option>
        ) : !account ? (
          <option value="">請選擇啟用中的部門</option>
        ) : null}
        {departments.map(
          // 允許保留目前已停用部門，但不提供其他停用部門供重新指派。
          (department) =>
            department.active || department.id === currentId ? (
              <option key={department.id} value={department.id}>
                {departmentName(department)}
              </option>
            ) : null,
        )}
      </select>
    </>
  );
}

// 主管與財務各用明確的是非選項，讓兩個角色可獨立設定及兼任。
function RoleSelects({
  id,
  account,
}: {
  id: string;
  account?: BusinessAccountDto;
}) {
  return (
    <>
      <label htmlFor={`${id}-manager`}>主管角色</label>
      <select
        id={`${id}-manager`}
        name="isManager"
        defaultValue={String(account?.isManager ?? false)}
      >
        <option value="false">無</option>
        <option value="true">有</option>
      </select>
      <label htmlFor={`${id}-finance`}>財務角色</label>
      <select
        id={`${id}-finance`}
        name="isFinance"
        defaultValue={String(account?.isFinance ?? false)}
      >
        <option value="false">無</option>
        <option value="true">有</option>
      </select>
    </>
  );
}

// 兩關候選人以同一份快照篩選；已保存的舊人選仍可保留或清除。
function ReviewerSelect({
  accounts,
  accountId,
  stage,
  savedId,
  value,
  otherValue,
  onChange,
}: {
  accounts: BusinessAccountDto[];
  accountId: string;
  stage: "manager" | "finance";
  savedId: string | null;
  value: string;
  otherValue: string;
  onChange: (id: string) => void;
}) {
  // 兩關標籤分開，帳號是否具資格以目前啟用狀態及對應角色判定。
  const name = `${stage}ReviewerId`;
  const label = stage === "manager" ? "預設主管審核人" : "預設財務審核人";
  return (
    <>
      <label htmlFor={name}>{label}</label>
      <select
        id={name}
        name={name}
        value={value}
        onChange={
          // 讓另一關同步排除本關目前選取，改派時可先清除原選擇。
          (event) => onChange(event.currentTarget.value)
        }
      >
        <option value="">未設定</option>
        {accounts.map(
          // 為本關建立目前可選的人選，以及需要保留顯示的既有失格人選。
          (candidate) => {
            // 同一張單的兩關人選不可相同，也不可選擇本人。
            if (candidate.id === accountId || candidate.id === otherValue)
              return null;

            // 只有原人選可在資格喪失後保留，標示原因供管理員判斷。
            const eligible =
              candidate.active &&
              (stage === "manager" ? candidate.isManager : candidate.isFinance);
            if (!eligible && candidate.id !== savedId) return null;
            return (
              <option key={candidate.id} value={candidate.id}>
                {candidate.userName}
                {eligible ? "" : "（目前不具資格）"}
              </option>
            );
          },
        )}
      </select>
    </>
  );
}

// 帳號表單以文字呈現最後一次讀回的設定，保存時帶回同一份資料的版本。
function AccountSettings({
  account,
  snapshot,
  disabled,
  onSubmit,
  departmentName,
}: Omit<NamedFormProps, "revision"> & { account: BusinessAccountDto }) {
  // 只保存需彼此限制的選取值；每次讀回後以最新資料重建草稿。
  const [manager, setManager] = useState(account.managerReviewerId ?? "");
  const [finance, setFinance] = useState(account.financeReviewerId ?? "");

  // 快照摘要用名稱顯示部門及審核人，讓長名稱即使在下拉欄位截斷仍能閱讀。
  const department = snapshot.departments.find(
    // 以不可變的部門識別查詢目前顯示名稱。
    (item) => item.id === account.departmentId,
  );
  const savedManager = snapshot.accounts.find(
    // 資格異動後仍顯示原主管審核人的名稱，供管理員核對及修正。
    (item) => item.id === account.managerReviewerId,
  );
  const savedFinance = snapshot.accounts.find(
    // 資格異動後仍顯示原財務審核人的名稱，供管理員核對及修正。
    (item) => item.id === account.financeReviewerId,
  );
  const roles = [
    ...(account.isManager ? ["主管"] : []),
    ...(account.isFinance ? ["財務"] : []),
  ];

  // 一般設定及密碼重設分成兩個表單，避免保存設定順帶更換密碼。
  return (
    <>
      <form
        aria-label="編輯業務帳號"
        onSubmit={
          // 保存各項設定與版本，讓後端在同一操作確認資格及並行寫入。
          (event) =>
            void onSubmit(
              event,
              // 空選項明確送 null，避免省略欄位誤清除既有資料。
              (values) => ({
                path: `accounts/${encodeURIComponent(account.id)}`,
                body: {
                  version: account.version,
                  active: field(values, "active") === "true",
                  departmentId: field(values, "departmentId") || null,
                  isManager: field(values, "isManager") === "true",
                  isFinance: field(values, "isFinance") === "true",
                  managerReviewerId: field(values, "managerReviewerId") || null,
                  financeReviewerId: field(values, "financeReviewerId") || null,
                },
              }),
              "已儲存業務帳號設定",
            )
        }
      >
        <fieldset disabled={disabled}>
          <p>
            目前帳號：{account.userName}（{account.active ? "啟用" : "停用"}）
          </p>
          <p>目前主管／財務角色：{roles.join("、") || "未設定"}</p>
          <p>目前部門：{department ? departmentName(department) : "未分配"}</p>
          <p>目前主管審核人：{savedManager?.userName ?? "未設定"}</p>
          <p>目前財務審核人：{savedFinance?.userName ?? "未設定"}</p>
          <p>
            密碼狀態：
            {account.mustChangePassword
              ? "須先變更臨時密碼"
              : "無須先變更臨時密碼"}
          </p>
          <label htmlFor="account-active">帳號狀態</label>
          <select
            id="account-active"
            name="active"
            defaultValue={String(account.active)}
          >
            <option value="true">啟用</option>
            <option value="false">停用</option>
          </select>
          <DepartmentSelect
            id="edit-account-department"
            account={account}
            departments={snapshot.departments}
            departmentName={departmentName}
          />
          <RoleSelects id="edit-account" account={account} />
          <ReviewerSelect
            accounts={snapshot.accounts}
            accountId={account.id}
            stage="manager"
            savedId={account.managerReviewerId}
            value={manager}
            otherValue={finance}
            onChange={setManager}
          />
          <ReviewerSelect
            accounts={snapshot.accounts}
            accountId={account.id}
            stage="finance"
            savedId={account.financeReviewerId}
            value={finance}
            otherValue={manager}
            onChange={setFinance}
          />
          <button type="submit">儲存業務帳號設定</button>
        </fieldset>
      </form>

      <form
        aria-label="重設業務帳號密碼"
        onSubmit={
          // 重設只提交臨時密碼和帳號版本，後端保存時撤銷原有登入。
          (event) =>
            void onSubmit(
              event,
              // 密碼依使用者輸入原樣送出，包含首尾空白。
              (values) => ({
                path: `accounts/${encodeURIComponent(account.id)}/reset-password`,
                body: {
                  version: account.version,
                  newPassword: field(values, "newPassword"),
                },
              }),
              "已重設業務帳號密碼",
            )
        }
      >
        <fieldset disabled={disabled}>
          <label htmlFor="reset-account-password">新的臨時密碼</label>
          <input
            id="reset-account-password"
            name="newPassword"
            type="password"
            autoComplete="new-password"
            required
            minLength={12}
            maxLength={128}
            aria-describedby="reset-account-password-hint"
          />
          <p className="hint" id="reset-account-password-hint">
            12–128 個字元，須與原密碼不同。重設會撤銷此帳號的所有登入。
          </p>
          <button type="submit">重設業務帳號密碼</button>
        </fieldset>
      </form>
    </>
  );
}

// 只列出快照中的業務帳號；新建表單不接受類別或管理角色。
function AccountManagement({
  snapshot,
  revision,
  disabled,
  onSubmit,
  departmentName,
}: NamedFormProps) {
  // 編輯目標與新建表單分離；以目前快照判定可管理的已選帳號。
  const [selectedId, setSelectedId] = useState("");
  const account = snapshot.accounts.find(
    // 以穩定識別尋找目標，避免重讀後仍引用舊的帳號資料。
    (item) => item.id === selectedId,
  );
  const hasActiveDepartment = snapshot.departments.some(
    // 有可分配部門時才開放新增帳號。
    (item) => item.active,
  );

  // 新建、編輯及重設個別顯示，讓管理員能清楚區別保存的對象及內容。
  return (
    <section aria-labelledby="account-title">
      <h3 id="account-title">業務帳號管理</h3>
      {!hasActiveDepartment ? (
        <p className="hint">請先新增或啟用至少一個部門，再建立業務帳號。</p>
      ) : null}
      <form
        key={revision}
        aria-label="新增業務帳號"
        onSubmit={
          // 新業務帳號只收臨時密碼、啟用部門及兩個可設定角色。
          (event) =>
            void onSubmit(
              event,
              // 明確傳回兩個布林值，密碼不做修剪或預填。
              (values) => ({
                path: "accounts",
                body: {
                  userName: field(values, "userName"),
                  password: field(values, "password"),
                  departmentId: field(values, "departmentId"),
                  isManager: field(values, "isManager") === "true",
                  isFinance: field(values, "isFinance") === "true",
                },
              }),
              "已新增業務帳號",
            )
        }
      >
        <fieldset disabled={disabled || !hasActiveDepartment}>
          <label htmlFor="new-account-name">新業務帳號名稱</label>
          <input
            id="new-account-name"
            name="userName"
            autoComplete="off"
            required
          />
          <p className="hint">去除首尾空白後須為 1–64 個字元。</p>
          <label htmlFor="new-account-password">臨時密碼</label>
          <input
            id="new-account-password"
            name="password"
            type="password"
            autoComplete="new-password"
            required
            minLength={12}
            maxLength={128}
            aria-describedby="new-account-password-hint"
          />
          <p className="hint" id="new-account-password-hint">
            12–128 個字元，保留首尾空白。此帳號登入後須先變更密碼。
          </p>
          <DepartmentSelect
            id="new-account-department"
            departments={snapshot.departments}
            departmentName={departmentName}
          />
          <RoleSelects id="new-account" />
          <button type="submit">新增業務帳號</button>
        </fieldset>
      </form>

      <label htmlFor="select-account">選擇業務帳號</label>
      <select
        id="select-account"
        disabled={disabled}
        value={account?.id ?? ""}
        onChange={
          // 切換目標時捨棄原帳號的表單，不保存未提交的輸入。
          (event) => setSelectedId(event.currentTarget.value)
        }
      >
        <option value="">請選擇要編輯的業務帳號</option>
        {snapshot.accounts.map(
          // 停用帳號仍可查閱及重新啟用，以穩定識別保存原有關聯。
          (item) => (
            <option key={item.id} value={item.id}>
              {item.userName}
              {item.active ? "" : "（已停用）"}
            </option>
          ),
        )}
      </select>
      {account ? (
        <AccountSettings
          key={`${account.id}:${revision}`}
          account={account}
          snapshot={snapshot}
          disabled={disabled}
          onSubmit={onSubmit}
          departmentName={departmentName}
        />
      ) : null}
    </section>
  );
}
