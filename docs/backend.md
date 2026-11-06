# 後端與 API 契約

目前提供本機登入、密碼變更、部門及業務帳號管理，以及本人請購草稿、固定兩關簽核、分權查閱與有限改派。後端以 ASP.NET Core 10 單體提供同源 API 與編譯後的前端，PostgreSQL 為獨立容器。

## 入口與責任

- `backend/Program.cs` 負責組態、migration、Cookie／CSRF 與強制改密碼防護，並掛載身分、管理、草稿與簽核 API。
- `backend/AuthEndpoints.cs`：以 `MapGroup("/api/auth")` 定義身分 Minimal API。發出 CSRF 權杖，其餘操作交給 `AuthService`。
- `backend/AuthModel.cs`：Identity 帳號、伺服器端登入紀錄、EF 對應與明確 DTO。
- `backend/AuthService.cs`：登入、鎖定、密碼長度、登入期限、撤銷及交易規則。
- `backend/OrganizationEndpoints.cs`、`OrganizationModel.cs`、`OrganizationService.cs` 負責管理路由、部門與管理 DTO，以及設定資格、驗證、版本和交易。
- `backend/RequisitionDraftEndpoints.cs` 及 `RequisitionDraftContracts.cs` 定義草稿 Minimal API 及 DTO。
- `backend/RequisitionDraftService.cs` 集中本人草稿查詢、版本和交易。`RequisitionDraftInput.cs` 驗證內容及計算金額，`Requisition.cs` 定義主檔、明細及 EF 對應。
- `backend/RequisitionApprovalEndpoints.cs` 與 `RequisitionApprovalContracts.cs` 定義簽核、查閱與有限改派 API 及公開 DTO。
- `backend/RequisitionApprovalService.cs` 集中查閱授權、有效指派、資格、版本與關卡轉換。有限改派沿用組織服務的管理身分與交易邊界。`RequisitionRound.cs` 定義送審快照、歷程及資料庫約束。
- `backend/RequisitionReporting.cs` 定義共用篩選、統計 DTO 與 CSV 編碼。`RequisitionApprovalService.cs` 用同一個可見查詢及最新送審範圍投影清單、統計及匯出。
- `backend/LocalSetup.cs`：本機憑證檔與補充式初始化，不提供 HTTP 維護端點。
- `backend/LocalMaintenance.cs` 負責本機 CLI 的管理員復原與展示重設。`Program.cs` 在啟動網站、套用 migration 或準備金鑰前分流這兩個命令。
- `backend/Migrations/`：以固定版本 EF CLI 產生的 schema 與模型快照。

HTTP 輸入使用明確 DTO。帳號資格、密碼及登入異動在同一交易內鎖定 `AspNetUsers` 對應帳號列，再檢查當下資料。管理帳號與業務帳號各有獨立資格。

## 本機維護交易

`recover-admin` 取得組織鎖與管理帳號列鎖後，重新確認固定 `admin` 的帳號類別、啟用狀態及管理角色。密碼雜湊、版本、鎖定狀態與舊登入撤銷沿用 `AuthService.ReplaceCredentials`，在同一交易提交。

`reset-demo.sh` 固定本機 Docker Unix socket，核對 Compose 專案與工作目錄、實際容器、資料卷的名稱、擁有者及儲存選項，以及網站的本機映像。資料卷須為無自訂選項的本機卷，資料庫容器只掛載該卷與憑證目錄。維護程序以已核對的容器 ID 共用 `db` 網路命名空間，連線固定走 loopback 與 `procurement` 資料庫。命令在顯示目標前核對 PostgreSQL 18 的實際資料目錄 `/var/lib/postgresql/18/docker`、模型表清單與 migration。升級 PostgreSQL 時須核對實際資料目錄及重跑隔離重設。

預覽讀取明列資料表的 PostgreSQL 行版本摘要。收到精確確認後，交易取得組織、migration 紀錄與目標資料表鎖，再比較版本摘要和目前資格。預覽後的資料異動會使確認失效。清空與重建示範帳號在同一交易。`TRUNCATE … RESTRICT` 會拒絕涉及清單外的外鍵，migration 紀錄保留。新增模型表時，須同步檢查清空範圍與重設測試。

兩個命令都先將新密碼寫到受保護的本機檔案。Unix 執行時會同步憑證檔和目錄，再提交資料庫交易。提交結果不明時，操作人員可用候選憑證登入核對。操作步驟見[本機展示與保存](local-demo.md)。

## HTTP

請求與回應使用 JSON camelCase。前端使用同源相對路徑；不要把 Cookie 或密碼寫入 console、localStorage 或 URL。`POST` 一律帶有效的 `X-CSRF-TOKEN`。

| 端點 | 請求 | 成功回應 |
| --- | --- | --- |
| `GET /api/auth/csrf` | 無 | `200 {"token":"…"}`，並設定防偽 Cookie |
| `POST /api/auth/login` | `{"userName":"帳號","password":"使用者輸入"}` | `204`，設定登入 Cookie |
| `GET /api/auth/me` | 登入 Cookie | `200 {"userName":"…","accountType":"admin 或 business","roles":["…"],"version":"…","mustChangePassword":false}`，附上同次驗證身分的 `X-CSRF-TOKEN` 回應標頭 |
| `POST /api/auth/change-password` | `{"currentPassword":"使用者輸入","newPassword":"使用者輸入","version":"me 的版本"}` | `204`，撤銷全部登入並清除呼叫者 Cookie |
| `POST /api/auth/logout` | 無本文 | `204`，僅撤銷這次登入並清除 Cookie |

`roles` 目前使用 `Administrator`、`Applicant`、`Manager`、`Finance`，回應按名稱排序。角色、啟用與強制改密碼狀態從資料庫讀取。Cookie 保存帳號及登入識別。`version` 是不透明值，直接傳回即可。`mustChangePassword` 為 `true` 時，現有登入只允許讀取身分、取得 CSRF 權杖、改密碼與登出。改密碼後須用不同於暫時密碼的新密碼登入。

匿名登入表單使用 `/api/auth/csrf` 取得的防偽權杖。已登入畫面採用成功 `me` 回應附帶的權杖，使權杖與當次確認的身分成對取得。切換帳號、登出或改密碼後，重新讀取身分與權杖。改密碼成功會使所有舊登入立即失效，前端應回到登入表單並顯示成功訊息。無權杖或不合法權杖的變更不寫入帳號、密碼、登入或鎖定狀態，也不延長閒置期限。

錯誤一律為 `{"code":"穩定識別碼","message":"繁中訊息"}`：

| HTTP | code | 呼叫端處理 |
| --- | --- | --- |
| 400 | `invalid_input` | 修正格式、必填、密碼長度或版本 |
| 400 | `invalid_csrf` | 重新取得防偽 token，再由使用者重試 |
| 400 | `wrong_password` | 修正目前密碼 |
| 401 | `invalid_credentials` | 登入帳密不正確，或帳號不可使用 |
| 401 | `unauthenticated` | 回登入表單；不會回傳 HTML 轉址 |
| 403 | `forbidden` | 使用符合資格的身分，或先變更臨時密碼 |
| 404 | `not_found` | 重新讀取對應清單，核對目前可存取的資料 |
| 409 | `stale_version` | 重新讀取目前身分或該筆資料，避免使用舊版本 |
| 409 | `invalid_state` | 重新讀取請購狀態，依目前可用操作處理 |
| 409 | `reviewers_unavailable` | 請管理員檢查申請人部門、預設審核人或未完成關卡的資格 |
| 409 | `duplicate_user_name` | 選擇其他帳號名稱 |
| 423 | `account_locked` | 連續失敗已鎖定，等待解除後由使用者重試 |
| 429 | `rate_limited` | 同來源登入過密，稍後再由使用者重試 |
| 500 | `server_error` | 結果未確認，先重新讀取相關資料，再由使用者決定是否重送 |

受保護端點先辨識登入；未登入或登入已失效時回 `401 unauthenticated`，不讓舊身分綁定的 token 把撤銷誤報為 CSRF 錯誤。登入仍有效時，缺少或不合法 CSRF 回 `400 invalid_csrf`。匿名登入端點同樣必須通過 CSRF，才能執行帳密檢查與登入寫入。

## 組織管理 API

以下操作只接受啟用中的管理帳號及有效登入，寫入也會在交易中重查資格。管理設定只包含部門與業務帳號。JSON 欄位使用 camelCase，所有 `POST` 都要帶 `X-CSRF-TOKEN`。

| 端點 | 請求本文 | 成功 |
| --- | --- | --- |
| `GET /api/admin/organization` | 無 | `200`，回傳 `departments` 及 `accounts` |
| `POST /api/admin/departments` | `name` | `204` |
| `POST /api/admin/departments/{id}` | `name`、`active`、`version` | `204` |
| `POST /api/admin/accounts` | `userName`、`password`、`departmentId`、`isManager`、`isFinance` | `204` |
| `POST /api/admin/accounts/{id}` | `version`、`active`、`departmentId`、`isManager`、`isFinance`、`managerReviewerId`、`financeReviewerId` | `204` |
| `POST /api/admin/accounts/{id}/reset-password` | `version`、`newPassword` | `204` |

部門資料包含 `id`、`name`、`active`、`version`。帳號資料包含 `id`、`userName`、`mustChangePassword` 及帳號保存所需欄位。管理端點不回傳密碼、雜湊或登入識別。布林欄位要明確提供；更新時可用 `null` 保留舊帳號原本未分配部門的狀態，或清除預設人選。送出較舊版本會得到 `409 stale_version`，請重新讀取設定再決定如何修改。

部門名稱修剪首尾空白後須為 1–100 個 UTF-16 字元。新帳號名稱同樣修剪首尾空白，須為 1–64 個 UTF-16 字元，重複名稱以不分大小寫判斷。部門名稱、新帳號名稱、臨時密碼及強制改密碼的新密碼都必須能由單行表單輸入，任何位置含 CR 或 LF 均回 `400 invalid_input`。密碼長度 12–128 個 UTF-16 字元，首尾空白會保留。新業務帳號具申請人角色，主管與財務可兼任。重設時需使用不同於現有密碼的臨時密碼。新分配部門須為啟用狀態，原有歸屬可保留。新選的預設審核人須啟用、有對應角色，且申請人和兩位審核人各自不同。停用或撤權保留指向該人選的設定，管理員可查閱並替換。

## 本人可編輯請購 API

限啟用中的業務帳號使用。所有讀寫皆需有效登入，`POST` 另須帶同源防偽權杖。草稿 GET 可只用 Cookie，也可附上 `X-CSRF-TOKEN` 供伺服器確認本次 Cookie 與讀取端身分一致。草稿畫面會附上此標頭，權杖失配回 `400 invalid_csrf`。管理帳號沒有草稿權限，失效登入得到 `401 unauthenticated`。他人草稿和不存在的草稿同樣回 `404 not_found`。

| 端點 | 請求本文 | 成功 |
| --- | --- | --- |
| `GET /api/requisitions/drafts` | 無 | `200`，本人草稿及撤回修改中的摘要，依更新時間新到舊排列，時間相同時依識別排序 |
| `POST /api/requisitions/drafts` | `subject`、`purpose`、`lines` 可省略或為 `null` | `201`，保存後的草稿 |
| `GET /api/requisitions/drafts/{id}` | 無 | `200`，目前可編輯內容 |
| `POST /api/requisitions/drafts/{id}` | `version`、`subject`、`purpose`、`lines`，須明確提供可編輯欄位，可用 `null` 清除 | `200`，保存後的可編輯內容 |
| `POST /api/requisitions/drafts/{id}/delete` | `version` | `204`，只限本人從未送審的草稿 |

摘要包含 `id`、`number`、`subject`、`updatedAt`。單筆另回傳 `applicantId`、`status`（`draft` 或 `withdrawn`）、`purpose`、`lines`、`estimatedAmount`、`isComplete`、`version` 和 `createdAt`。識別、單號、申請人、時間、狀態、版本及計算金額由伺服器決定。部門與送審時間會由送審操作決定。輸入未宣告的欄位會回 `400 invalid_input`。

每筆明細接受 `itemName`、`unit`、`quantity`、`unitPrice`，可省略或為 `null`。主旨及品名最多 200、用途最多 4,000、單位最多 40 個 UTF-16 字元。主旨、品名及單位是單行欄位，所有文字禁止 NUL。數量為 1–10,000 的 JSON 整數。單價為大於零、至多 1,000,000 的 JSON 十進位數值，最多兩位小數。數值採一般十進位寫法，拒絕引號、指數記法及超精度。每列及整單以精確十進位計算 TWD，已能計算的金額合計不得超過 10,000,000。完全無法計算時回傳 `null`。明細回應另包含該列 `estimatedAmount`。

`isComplete` 表示主旨、用途和 1–100 筆明細的必填內容已填齊。草稿可先保存任一未填齊的內容及可計算小計。讀取時的 `version` 需原樣帶回保存或刪除。過期版本得到 `409 stale_version`，保留目前內容。收到網路錯誤或 `500 server_error` 時，先讀回本人草稿或清單以確認結果，再決定下一步。

## 簽核與分權查閱 API

限啟用中、已完成改密碼要求的業務帳號。GET 可使用有效 Cookie，若帶有 `X-CSRF-TOKEN` 則必須配對當次登入。前端在清單與明細都附上配對權杖。無權查閱及查無請購共用 `404 not_found`。管理帳號在請購 API 回 `403 forbidden`。

| 端點 | 請求本文 | 成功 |
| --- | --- | --- |
| `GET /api/requisitions` | 無 | `200`，依更新時間新到舊排列，同時間依識別排序 |
| `GET /api/requisitions/{id}` | 無 | `200`，可查閱的請購明細及歷程 |
| `POST /api/requisitions/{id}/submit` | `version` | `200`，保存新一輪並進入主管待審 |
| `POST /api/requisitions/{id}/withdraw` | `version` | `200`，本人於首次決定前撤回，回到可編輯狀態 |
| `POST /api/requisitions/{id}/approve` | `version` | `200`，主管核准進入財務待審，財務核准後結案 |
| `POST /api/requisitions/{id}/reject` | `version` | `200`，目前關卡駁回結案 |

清單項目包含 `id`、`number`、`subject`、`applicant`、`status`、`estimatedAmount`、`version`、`createdAt`、`updatedAt`、`paused`、`canDecide` 與 `round`。人員資料含 `id`、`userName`。`round` 含 `number`、`department`（`id`、`name`）、`submittedAt`、`manager`、`finance`，表示最近一次送審的部門與有效指派。從未送審時為 `null`。

明細及 POST 成功回應另含 `purpose`、`lines`、`isComplete`、`canSubmit`、`canWithdraw`、`history` 與 `previousRounds`。撤回修改期間，頂層欄位呈現目前保存的可編輯內容。`previousRounds` 每筆含 `round`、`subject`、`purpose`、`estimatedAmount` 及 `lines`。申請人會在這裡取得先前快照的原始人選，撤回修改期間也包含剛撤回的一輪。其他帳號取得空陣列。

歷程每筆含 `roundNumber`、`kind`、`stage`、`actor` 及 `occurredAt`。送審和撤回事件的 `kind` 分別為 `submitted` 和 `withdrawn`，`stage` 為 `null`。決定的 `kind` 為 `approved` 或 `rejected`，`stage` 為 `manager` 或 `finance`。改派的 `kind` 為 `reassigned`，沿用相同關卡值，另含 `reassignment`，其中 `previousReviewer`、`newReviewer` 為人員資料，`reason` 為交接原因。

狀態為 `draft`、`withdrawn`、`manager_pending`、`finance_pending`、`approved` 或 `rejected`。資格阻礙以 `paused` 標示，請購維持原待審狀態。主管待審時兩個尚未完成的關卡都須有合格審核人。主管作成決定後，案件進入財務關時只重查未完成的財務指派。暫停不改變首次決定前可撤回的條件。

本人可查閱自己的全部請購。只有本人可查草稿與撤回修改中的單，也只有本人可查舊輪次。主管可查目前部門審核中或已結案的單及最新一輪有效指派給本人的單。財務以最新一輪的主管核准事件判斷是否已進財務關。角色範圍可合併，同一張單仍須有三位不同的申請人、主管及財務。其他業務帳號獲准時只會取得最新一輪的資料與歷程。能查閱的業務帳號只有在輪到本人且資格符合時才可作出決定。

送審和重送都重新驗證本人已保存的可編輯內容、申請人部門及兩位預設審核人的資格。送審部門採當次既有歸屬，部門停用保留歷史關聯。舊帳號尚未分配部門時，系統保留目前內容，管理員先補上部門才可送審。寫入只接受目前版本，欄位、狀態及指派都由伺服器決定。版本過期、狀態不符或資格不足時保留資料，請依回應讀回最新明細。回應遺失或收到 500 時應先讀回，避免重複提交。

## 請購統計與 CSV API

三個 GET 沿用請購查閱的業務身分、目前授權與 Cookie／權杖配對。成功回應皆為 `200` 且帶 `Cache-Control: no-store`。

| 端點 | 回應 |
| --- | --- |
| `GET /api/requisitions?scope=submitted` | 沿用請購清單的摘要陣列和排序，範圍為最新送審輪次且目前在待審或結案狀態的資料 |
| `GET /api/requisitions/statistics` | `{ "groups": [...] }`，每組包含 `department`（`id`、`name`）、`status`、`count` 與 TWD `estimatedAmount` |
| `GET /api/requisitions/csv` | `text/csv; charset=utf-8`，附件檔名 `requisitions.csv`，資料列順序與對應清單相同 |

三個端點接受相同的選用查詢參數。

| 參數 | 格式與行為 |
| --- | --- |
| `scope` | 指定時須為 `submitted`。統計與 CSV 預設採送審範圍。清單若帶任一報表參數，也採送審範圍。原本不帶報表參數的清單仍含本人草稿及撤回單 |
| `startDate`、`endDate` | `YYYY-MM-DD` 臺灣曆日，可單獨使用。包含起日零時、排除迄日翌日零時。可表示的界線最早起日為 `0001-01-02`，最晚迄日為 `9999-12-30` |
| `departmentId` | 最新輪次的送審部門識別，32 位十六進位字元。格式正確但沒有可見資料時回傳空結果 |
| `status` | `manager_pending`、`finance_pending`、`approved` 或 `rejected`。暫停維持原待審狀態 |

伺服器先確認身分，適用既有的 `401`、`403` 及 `400 invalid_csrf` 錯誤。已授權請求若含空值、重複值、格式錯誤或起日大於迄日，回 `400 invalid_input`。三個端點各自依目前權限、狀態及最新輪次查詢。空集合回空陣列、空 `groups` 或僅表頭的 CSV。

統計依穩定部門識別與目前狀態計數、以十進位加總 TWD。更名後仍屬同一部門，群組名稱取自符合條件的最新可見送審快照。逐單清單和 CSV 各用該單送審時的名稱。

CSV 採 UTF-8 BOM、逗號與 CRLF，每單一列。欄位依序為單號、主旨、申請人顯示名稱（沿用清單的 `userName`）、送審部門、目前狀態、TWD 預估金額、最近送審時間。金額固定兩位小數，送審時間為帶臺灣時差的 ISO 8601。使用者文字欄位用雙引號包覆，欄內雙引號成對編碼。可能被辨識為公式的文字只在輸出的副本加單引號。報表讀取完成後才在交易之外寫入 CSV 回應。

## 有限改派 API

限具 `Administrator` 角色且已完成改密碼要求的有效管理帳號。GET 可用有效 Cookie，附 `X-CSRF-TOKEN` 時須與 Cookie 配對。管理畫面讀寫都附上配對權杖。

| 端點 | 請求本文 | 成功 |
| --- | --- | --- |
| `GET /api/admin/reassignments` | 無 | `200`，有限待辦 `items` 與具對應資格的候選人 `managers`、`finances` |
| `POST /api/admin/reassignments/{id}` | `version`、`stage`、`reviewerId`、`reason` | `204`，改派完成後以 GET 讀回目前指派 |

待辦項目只有識別 `id`、單號 `number`、`department`（`id`、`name`）、目前待審關卡 `stage`（`manager` 或 `finance`）、`paused`、`version`、目前人選 `manager`、`finance`。兩位人選與候選清單只含 `id`、`userName`。清單只含當輪待審案件。回應及錯誤皆不包含主旨、用途、明細、金額或申請人欄位。

寫入以指定關卡替換單一人選。候選人須是啟用且具對應角色的業務帳號，並與申請人、另一關審核人及該關目前人選不同。原因去除首尾空白後須有 1–500 個 UTF-16 碼元，禁止 NUL。錯誤使用既有 `code`／`message` 形狀。`400 invalid_input` 表示本文不符契約，`404 not_found` 表示該單不在有限待辦範圍，`409 stale_version`、`invalid_state`、`reviewers_unavailable` 分別表示過期、關卡已完成、人選不合格。回應遺失時，GET 只可核對目前指派，無法單憑現值判定先前請求是否提交。

## 安全及一致性

密碼長度為 12–128 個 .NET UTF-16 字元，不修剪密碼；帳號名稱使用 invariant uppercase 比對。Identity 負責具鹽密碼雜湊，資料庫不保存明文密碼。連續錯誤五次鎖定十五分鐘，成功登入清除失敗次數；鎖定到期的當下可以登入。同直連來源每個一分鐘視窗最多三十次登入嘗試，包含不存在帳號；單體程序重啟會重建來源計數，不信任外來 Forwarded 標頭。

每次登入有獨立資料庫紀錄。閒置達三十分鐘、建立達八小時即拒絕；活動不能延長八小時上限。Cookie 為 session cookie，`HttpOnly`、`SameSite=Strict`；本機 HTTP 不設定強制 Secure，沒有傳輸加密，不能直接公開到外網。

Cookie 驗證與寫入操作都使用 PostgreSQL 帳號列鎖。端點在自己的交易內重新載入目前帳號、登入與版本；不能僅信任 middleware 已驗證的結果，也不能讓 EF 追蹤的舊實體取代重新載入。鎖定後才檢查停用、撤銷與版本，交易提交順序決定有效先後。更改密碼及刪除所有登入在同一交易，資料庫故障會一起回滾；登出只刪自己的登入。一般 GET 驗證會更新最後活動時間。

資料庫交易已提交後，Cookie 或 HTTP 回應仍可能傳送失敗；此時收到錯誤不代表操作未生效。前端應重新確認身分或請使用者登入，不自動重送密碼變更。交易內故障的回滾保證，不能延伸成所有 HTTP 500 都無寫入。

組織操作與本機初始化先取得同一 PostgreSQL transaction advisory lock，再鎖定操作者與目標帳號。部門資格、預設人選、版本和角色更新因此依交易順序判定。成功的管理寫入在同一筆交易更新操作者這次登入的活動時間，絕對期限仍以登入建立時間計算。停用帳號或重設密碼會在同一筆交易撤銷所有登入。重新啟用後須重新登入，原 Cookie 已失效。

草稿服務鎖定操作者的帳號列，重查當下的登入與業務資格。同一帳號的草稿版本檢查、寫入和撤權依此列鎖的交易提交順序生效。保存會在同一交易取代有順序的明細、更新主檔與該次登入活動時間。交易內任一步失敗都會回滾。刪除主檔時 PostgreSQL 外鍵會同步刪除該草稿明細。

送審、撤回、簽核及有限改派沿用同一把組織交易鎖，與管理撤權依提交順序生效。取得組織鎖後才重查操作者的登入、角色、資料版本及當前指派。申請人的帳號列鎖也和草稿保存及刪除共用，讓撤回、保存與重送依序生效。撤回與首關決定競爭時，先提交的結果會使落後操作的版本、狀態或可見範圍失效。每次寫入在同一筆交易保存狀態、版本及歷程，送審時同時保存新快照。改派在同一筆交易保存有效指派、請購版本與改派歷程，原待審關卡及原快照保留。查閱沿用相同鎖序，讓明細的摘要、快照與歷程對應同一狀態。

## 效能測試邊界

`compose.benchmark.yaml` 使用 `Dockerfile` 的 `backend-runtime` 建置 Release API，並連接專用 PostgreSQL。負載工具位於 `tests/Benchmarks/`，需明確設定 `RunPerformanceBenchmarks=true` 才會編入測試。這組工具在隔離資料庫準備資料和核對持久化結果，實際量測則走同一套 Kestrel、登入、授權和業務端點。重跑步驟與數據見[指定 HTTP 負載與獨立 CSV 匯出](testing.md#指定-http-負載與獨立-csv-匯出)。

## 固定工具鏈與開發

SDK `10.0.403`，ASP.NET Core／Identity／EF `10.0.14`，Npgsql EF provider `10.0.3`，PostgreSQL `18.6`。`global.json` 禁止 SDK roll-forward，NuGet lock files 固定相依；Npgsql 要求 EF `[10.0.4, 11.0.0)`，目前固定版本符合。

本機沒有相同 SDK 時使用測試 Compose 的 SDK 容器，不必安裝全域工具。需要新增 migration 時，先修改模型，再執行：

```sh
docker compose -p procurement-approval-test -f compose.test.yaml run --rm tests sh -c 'dotnet tool restore && dotnet ef migrations add 名稱 --project backend'
```

啟動使用 `MigrateAsync` 套用 migration，產生檔由工具管理。`OrganizationManagement` 新增部門與帳號欄位，既有帳號以未分配部門、未設定預設人選、無強制改密碼要求的狀態升級，保留原識別與憑證。降版移除新欄位及部門表會遺失組織設定與強制改密碼狀態，已變更的帳密及角色仍保留。若需回到升級前資料，須使用升級前的資料庫備份與相符的應用版本。

`RequisitionDrafts` 新增 `Requisitions`、`RequisitionLines` 及草稿清單所需索引。既有帳號與設定沿用原有資料。執行此 migration 的降版會移除這兩張表，已保存的草稿需要透過備份才能復原。

簽核遷移新增 `RequisitionRounds` 與 `RequisitionApprovalEvents`，舊草稿沿用原狀態與版本。輪次以 JSONB 保存當次內容與順序，部門與指派保留關聯，部門名稱採送審當時的快照。套用降版會移除輪次及歷程表，且不能把已送審的請購還原成舊版草稿。需回到升級前的狀態時，使用升級前備份及相符的應用版本。

撤回遷移調整操作歷程的種類及唯一性約束，保留既有草稿、輪次及事件。資料庫限制每輪最多一筆送審、一筆撤回，以及每個審核關卡最多一筆決定。已有撤回歷程時，舊版約束無法接納現有資料。要回到升級前的資料狀態，須使用升級前的備份和相符的應用版本。

`RequisitionReassignment` 保留舊輪次及事件，新增有效指派欄位及改派歷程欄位。兩關目前人選、歷程前後人選及原快照均保有資料庫關聯，資料庫仍限制每關最多一筆決定。已有改派事件時，退回舊 schema 的遷移會遭資料庫拒絕並回滾。請搭配支援改派的程式及 schema，舊版程式依原始人選判斷簽核權。回到升級前的資料狀態需使用升級前備份與相符的應用版本。
