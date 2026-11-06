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

// 清單只回傳辨識草稿所需的摘要，開啟單筆時才取得內容和版本。
export interface DraftSummaryDto {
  id: string;
  number: string;
  subject: string | null;
  updatedAt: string;
}

interface DraftLineInputDto {
  itemName: string | null;
  unit: string | null;
  quantity: number | null;
  unitPrice: number | null;
}

// 寫入只接受可編輯欄位；空值允許逐步填寫，也能明確清除既有欄位。
export interface DraftInputDto {
  subject: string | null;
  purpose: string | null;
  lines: DraftLineInputDto[];
}

// 詳情與保存回應的金額、完整性和版本都由後端決定。
export interface DraftDto extends DraftSummaryDto {
  applicantId: string;
  status: "draft" | "withdrawn";
  purpose: string | null;
  lines: (DraftLineInputDto & { estimatedAmount: number | null })[];
  estimatedAmount: number | null;
  isComplete: boolean;
  version: string;
  createdAt: string;
}

// 簽核查詢只公開可見摘要；角色資格及暫停狀態採用 API 的目前判斷。
type RequisitionPerson = { id: string; userName: string };

// 管理員只讀取待辦所需欄位；識別和版本只用來送回同一筆待辦。
export type ReassignmentStage = "manager" | "finance";

export interface ReassignmentItemDto {
  id: string;
  number: string;
  department: Pick<DepartmentDto, "id" | "name">;
  stage: ReassignmentStage;
  paused: boolean;
  version: string;
  manager: RequisitionPerson;
  finance: RequisitionPerson;
}

export interface ReassignmentListDto {
  items: ReassignmentItemDto[];
  managers: RequisitionPerson[];
  finances: RequisitionPerson[];
}

interface ReassignmentInputDto {
  version: string;
  stage: ReassignmentStage;
  reviewerId: string;
  reason: string;
}

export interface RequisitionSummaryDto extends DraftSummaryDto {
  applicant: RequisitionPerson;
  status:
    | "draft"
    | "withdrawn"
    | "manager_pending"
    | "finance_pending"
    | "approved"
    | "rejected";
  estimatedAmount: number | null;
  version: string;
  createdAt: string;
  paused: boolean;
  canDecide: boolean;
  round: {
    number: number;
    department: { id: string; name: string };
    submittedAt: string;
    manager: RequisitionPerson;
    finance: RequisitionPerson;
  } | null;
}

// 明細另含已保存內容及歷程；送審後的內容、人選和部門取自該輪快照。
export interface RequisitionDetailDto extends RequisitionSummaryDto {
  purpose: string | null;
  lines: DraftDto["lines"];
  isComplete: boolean;
  canSubmit: boolean;
  canWithdraw: boolean;
  previousRounds: RequisitionRoundSnapshotDto[];
  history: {
    roundNumber: number;
    kind: "submitted" | "withdrawn" | "approved" | "rejected" | "reassigned";
    stage: ReassignmentStage | null;
    actor: RequisitionPerson;
    occurredAt: string;
    reassignment?: {
      previousReviewer: RequisitionPerson;
      newReviewer: RequisitionPerson;
      reason: string;
    };
  }[];
}

// 只供申請人查閱的既有送審輪次，沿用明細的輪次與明細 DTO。
export interface RequisitionRoundSnapshotDto {
  round: NonNullable<RequisitionSummaryDto["round"]>;
  subject: string | null;
  purpose: string | null;
  estimatedAmount: number | null;
  lines: DraftDto["lines"];
}

export type ApprovalAction = "submit" | "withdraw" | "approve" | "reject";

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
  let token = csrf.token;
  let identity: IdentityDto | null;

  // 只有明確的未登入回應可作為匿名；其他錯誤交由畫面進入恢復。
  try {
    // 已登入身分只配對同一回應的權杖，避免共用 Cookie 切換時混合不同帳號。
    const response = await request("auth/me", { signal });
    const identityToken = response.headers.get("X-CSRF-TOKEN");
    if (!identityToken) throw new Error("身分回應缺少防偽權杖。");
    identity = await response.json();
    token = identityToken;
  } catch (error) {
    if (!(error instanceof ApiError) || error.code !== "unauthenticated")
      throw error;
    identity = null;
  }

  // 已登入權杖與身分同源；只有明確匿名時才沿用最初取得的登入權杖。
  return { token, identity };
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

const reassignmentPath = "admin/reassignments";

// 待辦 GET 驗證畫面身分與 Cookie 的配對，並確認清單可安全用於讀回。
export async function readReassignments(token: string, signal?: AbortSignal) {
  // 僅接受契約的成功狀態與完整格式，避免把錯誤回應當成案件已離開待辦。
  const response = await request(reassignmentPath, {
    signal,
    headers: { "X-CSRF-TOKEN": token },
  });
  if (response.status !== 200) throw new Error("無法確認改派清單回應。");
  const value: unknown = await response.json();
  if (!isReassignmentList(value)) throw new Error("改派清單格式不符。");
  return value;
}

// 每次只送指定待辦、關卡、人選及原因，僅 204 代表已確認保存。
export async function writeReassignment(
  token: string,
  id: string,
  input: ReassignmentInputDto,
) {
  const response = await post(
    `${reassignmentPath}/${encodeURIComponent(id)}`,
    token,
    input,
  );
  if (response.status !== 204) throw new Error("無法確認改派寫入回應。");
}

const requisitionPath = "requisitions";
const draftPath = `${requisitionPath}/drafts`;

// 清單 GET 附上畫面身分的權杖，避免共用 Cookie 切換帳號後採用另一人的資料。
export async function readDraftList(
  token: string,
  signal?: AbortSignal,
): Promise<DraftSummaryDto[]> {
  return (
    await request(draftPath, { signal, headers: { "X-CSRF-TOKEN": token } })
  ).json();
}

// 開啟或核對草稿時，以同一個畫面身分的權杖讀取當下詳情及版本。
export async function readDraft(token: string, id: string): Promise<DraftDto> {
  return (
    await request(`${draftPath}/${encodeURIComponent(id)}`, {
      headers: { "X-CSRF-TOKEN": token },
    })
  ).json();
}

// 建立不帶版本，更新只附上讀取時的版本，兩者均使用同一防偽邊界。
export async function saveDraft(
  token: string,
  content: DraftInputDto,
  current?: Pick<DraftDto, "id" | "version">,
): Promise<DraftDto> {
  // 依是否已有系統識別選擇入口，且只送出一次。
  const response = current
    ? await post(`${draftPath}/${encodeURIComponent(current.id)}`, token, {
        ...content,
        version: current.version,
      })
    : await post(draftPath, token, content);

  // 必須收到對應的成功狀態及完整回應，才能把保存視為已確認。
  if (response.status !== (current ? 200 : 201))
    throw new Error("無法確認草稿保存回應。");
  return response.json();
}

// 刪除提交讀取時的版本；僅契約定義的 204 代表已確認刪除。
export async function deleteDraft(
  token: string,
  { id, version }: Pick<DraftDto, "id" | "version">,
) {
  const response = await post(
    `${draftPath}/${encodeURIComponent(id)}/delete`,
    token,
    { version },
  );
  if (response.status !== 204) throw new Error("無法確認草稿刪除回應。");
}

// 和草稿 GET 一樣，查詢必須驗證目前畫面身分與共享 Cookie 的配對。
export async function readRequisitions(
  token: string,
  signal?: AbortSignal,
): Promise<RequisitionSummaryDto[]> {
  return (
    await request(requisitionPath, {
      signal,
      headers: { "X-CSRF-TOKEN": token },
    })
  ).json();
}

// 明細及歷程由同一個已授權回應取得，且同時提供下一次操作需要的版本。
export async function readRequisition(
  token: string,
  id: string,
): Promise<RequisitionDetailDto> {
  const response = await request(
    `${requisitionPath}/${encodeURIComponent(id)}`,
    {
      headers: { "X-CSRF-TOKEN": token },
    },
  );
  return decodeRequisitionDetail(response, id);
}

// 每次只提交讀取時的版本，收到 200 即確認交易；內容無法解讀時交由畫面提供讀回入口。
export async function writeApproval(
  token: string,
  action: ApprovalAction,
  { id, version }: Pick<RequisitionDetailDto, "id" | "version">,
): Promise<RequisitionDetailDto | null> {
  // 只有契約定義的成功狀態能確認寫入，其他回應須保留不確定性。
  const response = await post(
    `${requisitionPath}/${encodeURIComponent(id)}/${action}`,
    token,
    { version },
  );
  if (response.status !== 200) throw new Error("無法確認簽核寫入回應。");

  // 已確認寫入後，回應內容遺失只影響讀取，不改變交易結果。
  try {
    return await decodeRequisitionDetail(response, id);
  } catch {
    return null;
  }
}

const requisitionStatuses = new Set<unknown>([
  "draft",
  "withdrawn",
  "manager_pending",
  "finance_pending",
  "approved",
  "rejected",
] satisfies RequisitionDetailDto["status"][]);

// 將回應的物件與純量分開，讓下列欄位在檢查前維持未知型別。
function isRecord(candidate: unknown): candidate is Record<string, unknown> {
  return (
    typeof candidate === "object" &&
    candidate !== null &&
    !Array.isArray(candidate)
  );
}

// 單號、識別與版本必須有內容，才能辨認同一筆資料及提交版本。
function isNonBlankText(value: unknown) {
  return typeof value === "string" && value.trim().length > 0;
}

// 尚未填寫的草稿欄位保留 null，以區分欄位遺失或型別錯誤。
function isNullableText(value: unknown) {
  return value === null || typeof value === "string";
}

// 只採用可呈現的有限數字；未填的草稿數量及金額保留 null。
function isNullableNumber(value: unknown) {
  return (
    value === null || (typeof value === "number" && Number.isFinite(value))
  );
}

// 時間先確認可解析，避免將無效時間交給 Intl.DateTimeFormat。
function isTimestamp(value: unknown) {
  return typeof value === "string" && Number.isFinite(Date.parse(value));
}

// 送審輪次從第一輪開始，歷程和快照共用相同的輪次表示。
function isRoundNumber(roundNumber: unknown) {
  return (
    typeof roundNumber === "number" &&
    Number.isSafeInteger(roundNumber) &&
    roundNumber > 0
  );
}

// 各類參與人員沿用相同欄位，歷程允許保留舊人員的空白顯示名稱。
function isRequisitionPerson(value: unknown) {
  return (
    isRecord(value) &&
    isNonBlankText(value.id) &&
    typeof value.userName === "string"
  );
}

// 部門名稱由後端驗證，前端只檢查型別並保留原值。
function isRequisitionDepartment(value: unknown) {
  return (
    isRecord(value) &&
    isNonBlankText(value.id) &&
    typeof value.name === "string"
  );
}

// 每筆待辦必須有配對的版本與審核者，避免以無法辨識的資料送出改派。
function isReassignmentItem(value: unknown) {
  return (
    isRecord(value) &&
    isNonBlankText(value.id) &&
    isNonBlankText(value.number) &&
    isRequisitionDepartment(value.department) &&
    (value.stage === "manager" || value.stage === "finance") &&
    typeof value.paused === "boolean" &&
    isNonBlankText(value.version) &&
    isRequisitionPerson(value.manager) &&
    isRequisitionPerson(value.finance)
  );
}

// 只有完整待辦及兩組角色選項才可作為目前狀態的依據。
function isReassignmentList(value: unknown): value is ReassignmentListDto {
  return (
    isRecord(value) &&
    Array.isArray(value.items) &&
    value.items.every(isReassignmentItem) &&
    Array.isArray(value.managers) &&
    value.managers.every(isRequisitionPerson) &&
    Array.isArray(value.finances) &&
    value.finances.every(isRequisitionPerson)
  );
}

// 已送審的輪次須能顯示當時的部門、時間與兩位審核者。
function isRequisitionRound(value: unknown) {
  return (
    isRecord(value) &&
    isRoundNumber(value.number) &&
    isRequisitionDepartment(value.department) &&
    isTimestamp(value.submittedAt) &&
    isRequisitionPerson(value.manager) &&
    isRequisitionPerson(value.finance)
  );
}

// 明細仍可包含不完整草稿，但每一列須有約定的欄位才能呈現。
function isRequisitionLine(value: unknown) {
  return (
    isRecord(value) &&
    isNullableText(value.itemName) &&
    isNullableText(value.unit) &&
    isNullableNumber(value.quantity) &&
    isNullableNumber(value.unitPrice) &&
    isNullableNumber(value.estimatedAmount)
  );
}

// 舊事件仍採原契約；改派另須有有效關卡、前後人選及原因。
function isRequisitionHistoryEntry(value: unknown) {
  // 所有事件都須可呈現輪次、操作者與時間。
  if (
    !isRecord(value) ||
    !isRoundNumber(value.roundNumber) ||
    !isRequisitionPerson(value.actor) ||
    !isTimestamp(value.occurredAt)
  )
    return false;

  // 送審與撤回無關卡，審核決定及改派則限兩種固定關卡。
  if (value.kind === "submitted" || value.kind === "withdrawn")
    return value.stage === null;
  if (value.stage !== "manager" && value.stage !== "finance") return false;
  if (value.kind === "approved" || value.kind === "rejected") return true;

  // 理由的內容依 API 保存的原值呈現，避免用 JavaScript trim 誤拒合法字元。
  const reassignment = value.reassignment;
  return (
    value.kind === "reassigned" &&
    isRecord(reassignment) &&
    isRequisitionPerson(reassignment.previousReviewer) &&
    isRequisitionPerson(reassignment.newReviewer) &&
    typeof reassignment.reason === "string"
  );
}

// 舊輪次需完整驗證當時內容，且不正規化部門名稱等原始顯示字串。
function isRequisitionRoundSnapshot(value: unknown) {
  return (
    isRecord(value) &&
    isRequisitionRound(value.round) &&
    isNullableText(value.subject) &&
    isNullableText(value.purpose) &&
    isNullableNumber(value.estimatedAmount) &&
    Array.isArray(value.lines) &&
    value.lines.every(isRequisitionLine)
  );
}

// 驗證同一筆請購的版本、動作旗標及可呈現資料，業務資格仍由後端決定。
function isRequisitionDetail(
  value: unknown,
  id: string,
): value is RequisitionDetailDto {
  return (
    isRecord(value) &&
    value.id === id &&
    isNonBlankText(value.version) &&
    isNonBlankText(value.number) &&
    requisitionStatuses.has(value.status) &&
    isNullableText(value.subject) &&
    isNullableText(value.purpose) &&
    isRequisitionPerson(value.applicant) &&
    isNullableNumber(value.estimatedAmount) &&
    isTimestamp(value.createdAt) &&
    isTimestamp(value.updatedAt) &&
    typeof value.paused === "boolean" &&
    typeof value.canDecide === "boolean" &&
    typeof value.canWithdraw === "boolean" &&
    typeof value.isComplete === "boolean" &&
    typeof value.canSubmit === "boolean" &&
    (value.status === "draft"
      ? value.round === null
      : isRequisitionRound(value.round)) &&
    Array.isArray(value.lines) &&
    value.lines.every(isRequisitionLine) &&
    Array.isArray(value.previousRounds) &&
    value.previousRounds.every(isRequisitionRoundSnapshot) &&
    Array.isArray(value.history) &&
    value.history.every(isRequisitionHistoryEntry)
  );
}

// GET 和 POST 200 採用同一明細格式；資料無效時讓呼叫端保留讀回入口。
async function decodeRequisitionDetail(response: Response, id: string) {
  const value: unknown = await response.json();
  if (!isRequisitionDetail(value, id)) throw new Error("請購明細格式不符。");
  return value;
}

// 所有 POST 共用防偽標頭與逾時設定，不自動重試可能已生效的操作。
function post(path: string, token: string, body?: object) {
  return request(path, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
    body: body ? JSON.stringify(body) : undefined,
  });
}
