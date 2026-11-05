// DTO 與 HTTP 邊界集中於此；Cookie 由瀏覽器管理，權杖只留在記憶體。

// 目前身分的查詢結果；version 隨改密碼送回，避免使用過期狀態寫入。
export interface IdentityDto {
  userName: string;
  accountType: "admin" | "business";
  roles: string[];
  version: string;
  mustChangePassword: boolean;
}

// 登入僅提交帳密；角色與資格由後端判斷。
export interface LoginRequestDto {
  userName: string;
  password: string;
}

// 改密碼的可編輯輸入與版本，不攜帶可任意更新的帳號實體。
export interface PasswordRequestDto {
  currentPassword: string;
  newPassword: string;
  version: string;
}

// 管理讀取保留停用的部門及版本，供畫面確認既有歸屬與保存時比對。
export interface DepartmentDto {
  id: string;
  name: string;
  active: boolean;
  version: string;
}

// 帳號設定僅包含可管理欄位，null 表示未分配或未設定。
interface AccountSettingsDto {
  active: boolean;
  version: string;
  departmentId: string | null;
  isManager: boolean;
  isFinance: boolean;
  managerReviewerId: string | null;
  financeReviewerId: string | null;
}

// 業務帳號讀取多帶識別、名稱與改密碼狀態，完全不含憑證。
export interface BusinessAccountDto extends AccountSettingsDto {
  id: string;
  userName: string;
  mustChangePassword: boolean;
}

// 部門及帳號一起讀取，讓選項和版本採用同一份設定。
export interface OrganizationDto {
  departments: DepartmentDto[];
  accounts: BusinessAccountDto[];
}

// 各路徑攜帶明確的寫入欄位，不把讀取資料或密碼帶到其他操作。
export type OrganizationWrite =
  | { path: "departments"; body: { name: string } }
  | {
      path: `departments/${string}`;
      body: Pick<DepartmentDto, "name" | "active" | "version">;
    }
  | {
      path: "accounts";
      body: {
        userName: string;
        password: string;
        departmentId: string;
        isManager: boolean;
        isFinance: boolean;
      };
    }
  | { path: `accounts/${string}`; body: AccountSettingsDto }
  | {
      path: `accounts/${string}/reset-password`;
      body: Pick<PasswordRequestDto, "newPassword" | "version">;
    };

// 防偽端點回應只供本次頁面的後續寫入使用。
interface CsrfResponseDto {
  token: string;
}

// 保留後端錯誤代碼供恢復流程判斷，message 則用於畫面提示。
interface ErrorResponseDto {
  code: string;
  message: string;
}

// 可解析的 HTTP 錯誤與連線／回應解析失敗分開，讓呼叫端保留未知結果。
export class ApiError extends Error {
  // 建立可依狀態碼與錯誤碼分類的錯誤，讓畫面保留後端提供的提示。
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
  ) {
    // 交由 Error 保存訊息，供一般錯誤處理與畫面共用。
    super(message);
  }
}

// 共用同源及逾時設定；呼叫端提供 signal 時，由呼叫端管理取消期限。
async function request(path: string, options?: RequestInit): Promise<Response> {
  // 同源請求帶上 Cookie；停用快取以讀取目前的身分與權杖。
  const response = await fetch(`/api/${path}`, {
    ...options,
    credentials: "same-origin",
    cache: "no-store",
    signal: options?.signal ?? AbortSignal.timeout(15000),
  });

  // 只將可解析的 HTTP 拒絕包成 ApiError，解析失敗沿原路徑回報。
  if (!response.ok) {
    const error: ErrorResponseDto = await response.json();
    throw new ApiError(response.status, error.code, error.message);
  }

  // 成功時回傳原始回應，讓讀取端決定是否解析，也支援無內容的寫入回應。
  return response;
}

// 讀取防偽權杖和目前身分，讓畫面依照伺服器確認的狀態選擇表單。
export async function readSession(signal?: AbortSignal) {
  // 先取得防偽權杖，即使身分查詢確認為匿名，後續登入仍能提交。
  const csrf: CsrfResponseDto = await (
    await request("auth/csrf", { signal })
  ).json();
  let identity: IdentityDto | null;

  // 只有明確的未登入回應可作為匿名；其他錯誤交由畫面進入恢復。
  try {
    identity = await (await request("auth/me", { signal })).json();
  } catch (error) {
    if (!(error instanceof ApiError) || error.code !== "unauthenticated")
      throw error;
    identity = null;
  }

  // 兩個讀取都得到可判斷的結果後，再一起交給畫面更新。
  return { token: csrf.token, identity };
}

// 共用登入、改密碼及登出的 POST 邊界，讓每次變更都帶同源防偽權杖。
export async function writeAuth(
  operation: "login" | "change-password" | "logout",
  token: string,
  body?: LoginRequestDto | PasswordRequestDto,
) {
  // 只送出一次，因為連線失敗或 500 可能發生在寫入已生效之後。
  await post(`auth/${operation}`, token, body);
}

// 取得最新組織快照；有 signal 時由畫面決定這一輪讀取的有效期限。
export async function readOrganization(
  signal?: AbortSignal,
): Promise<OrganizationDto> {
  return (await request("admin/organization", { signal })).json();
}

// 管理寫入沿用同源防偽邊界，成功後由畫面另行查詢保存結果。
export async function writeOrganization(
  token: string,
  operation: OrganizationWrite,
) {
  // 只將契約定義的 204 視為寫入確認，其他成功狀態也需要人工讀回。
  const response = await post(`admin/${operation.path}`, token, operation.body);
  if (response.status !== 204) throw new Error("無法確認組織寫入回應。");
}

// 所有 POST 共用防偽標頭與逾時設定，不自動重試可能已生效的操作。
function post(path: string, token: string, body?: object) {
  return request(path, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
    body: body ? JSON.stringify(body) : undefined,
  });
}
