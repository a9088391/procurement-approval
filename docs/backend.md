# 身分後端與 API 契約

目前提供本機帳密登入、目前身分、改密碼與登出。管理帳號與業務帳號分離；尚未提供組織管理、請購、復原、展示重設或公開註冊。後端以 ASP.NET Core 10 單體提供同源 API 與編譯後的前端，PostgreSQL 為獨立容器。

## 入口與責任

- `backend/Program.cs`：組態、migration、Cookie／CSRF，並呼叫 `MapAuthEndpoints()` 註冊身分 API。
- `backend/AuthEndpoints.cs`：以 `MapGroup("/api/auth")` 定義身分 Minimal API。發出 CSRF 權杖，其餘操作交給 `AuthService`。
- `backend/AuthModel.cs`：Identity 帳號、伺服器端登入紀錄、EF 對應與明確 DTO。
- `backend/AuthService.cs`：登入、鎖定、密碼長度、登入期限、撤銷及交易規則。
- `backend/LocalSetup.cs`：本機憑證檔與補充式初始化，不提供 HTTP 維護端點。
- `backend/Migrations/`：以固定版本 EF CLI 產生的 schema 與模型快照。

不把 EF 實體當作 HTTP 輸入，也不建立通用 Repository。新增帳號或角色管理時，所有帳號資格、密碼及登入異動必須在同一交易先鎖定 `AspNetUsers` 對應帳號列，再檢查當下資料。管理帳號不能授予業務操作資格。

## HTTP

請求與回應使用 JSON camelCase。前端使用同源相對路徑；不要把 Cookie 或密碼寫入 console、localStorage 或 URL。`POST` 一律帶有效的 `X-CSRF-TOKEN`。

| 端點 | 請求 | 成功回應 |
| --- | --- | --- |
| `GET /api/auth/csrf` | 無 | `200 {"token":"…"}`，並設定防偽 Cookie |
| `POST /api/auth/login` | `{"userName":"帳號","password":"使用者輸入"}` | `204`，設定登入 Cookie |
| `GET /api/auth/me` | 登入 Cookie | `200 {"userName":"…","accountType":"admin 或 business","roles":["…"],"version":"…"}` |
| `POST /api/auth/change-password` | `{"currentPassword":"使用者輸入","newPassword":"使用者輸入","version":"me 的版本"}` | `204`，撤銷全部登入並清除呼叫者 Cookie |
| `POST /api/auth/logout` | 無本文 | `204`，僅撤銷這次登入並清除 Cookie |

`roles` 目前使用 `Administrator`、`Applicant`、`Manager`、`Finance`；回應按名稱排序。角色及啟用狀態從資料庫讀取，Cookie 只帶帳號與登入識別，不帶授權角色。`version` 是不透明值，直接傳回即可。

首次顯示登入表單前取得防偽 token；登入成功後重新取得，再讀 `me`。切換帳號、登出或改密碼後，重新取得 token，避免使用綁定舊身分的 token。改密碼成功會使所有舊登入立即失效，前端應回到登入表單並顯示成功訊息。無權杖或不合法權杖的變更不寫入帳號、密碼、登入或鎖定狀態，也不延長閒置期限。

錯誤一律為 `{"code":"穩定識別碼","message":"繁中訊息"}`：

| HTTP | code | 呼叫端處理 |
| --- | --- | --- |
| 400 | `invalid_input` | 修正格式、必填、密碼長度或版本 |
| 400 | `invalid_csrf` | 重新取得防偽 token，再由使用者重試 |
| 400 | `wrong_password` | 修正目前密碼 |
| 401 | `invalid_credentials` | 登入帳密不正確，或帳號不可使用 |
| 401 | `unauthenticated` | 回登入表單；不會回傳 HTML 轉址 |
| 409 | `stale_version` | 重新讀取目前身分，避免覆寫過期版本 |
| 423 | `account_locked` | 連續失敗已鎖定，等待解除後由使用者重試 |
| 429 | `rate_limited` | 同來源登入過密，稍後再由使用者重試 |
| 500 | `server_error` | 結果未確認；先重新讀取身分或由使用者重新登入確認，不自動重送 |

受保護端點先辨識登入；未登入或登入已失效時回 `401 unauthenticated`，不讓舊身分綁定的 token 把撤銷誤報為 CSRF 錯誤。登入仍有效時，缺少或不合法 CSRF 回 `400 invalid_csrf`。匿名登入端點同樣必須通過 CSRF，才能執行帳密檢查與登入寫入。

## 安全及一致性

密碼長度為 12–128 個 .NET UTF-16 字元，不修剪密碼；帳號名稱使用 invariant uppercase 比對。Identity 負責具鹽密碼雜湊，資料庫不保存明文密碼。連續錯誤五次鎖定十五分鐘，成功登入清除失敗次數；鎖定到期的當下可以登入。同直連來源每個一分鐘視窗最多三十次登入嘗試，包含不存在帳號；單體程序重啟會重建來源計數，不信任外來 Forwarded 標頭。

每次登入有獨立資料庫紀錄。閒置達三十分鐘、建立達八小時即拒絕；活動不能延長八小時上限。Cookie 為 session cookie，`HttpOnly`、`SameSite=Strict`；本機 HTTP 不設定強制 Secure，沒有傳輸加密，不能直接公開到外網。

Cookie 驗證與寫入操作都使用 PostgreSQL 帳號列鎖。端點在自己的交易內重新載入目前帳號、登入與版本；不能僅信任 middleware 已驗證的結果，也不能讓 EF 追蹤的舊實體取代重新載入。鎖定後才檢查停用、撤銷與版本，交易提交順序決定有效先後。更改密碼及刪除所有登入在同一交易，資料庫故障會一起回滾；登出只刪自己的登入。一般 GET 驗證會更新最後活動時間。

資料庫交易已提交後，Cookie 或 HTTP 回應仍可能傳送失敗；此時收到錯誤不代表操作未生效。前端應重新確認身分或請使用者登入，不自動重送密碼變更。交易內故障的回滾保證，不能延伸成所有 HTTP 500 都無寫入。

## 固定工具鏈與開發

SDK `10.0.403`，ASP.NET Core／Identity／EF `10.0.14`，Npgsql EF provider `10.0.3`，PostgreSQL `18.6`。`global.json` 禁止 SDK roll-forward，NuGet lock files 固定相依；Npgsql 要求 EF `[10.0.4, 11.0.0)`，目前固定版本符合。

本機沒有相同 SDK 時使用測試 Compose 的 SDK 容器，不必安裝全域工具。需要新增 migration 時，先修改模型，再執行：

```sh
docker compose -p procurement-approval-test -f compose.test.yaml run --rm tests sh -c 'dotnet tool restore && dotnet ef migrations add 名稱 --project backend'
```

產生檔由工具管理，不手改。啟動使用 `MigrateAsync` 套用 migration；日後若變更既有資料或 schema，應另評估相容、備份及復原。本版為全新資料庫的初始 migration，無既有資料轉換。
