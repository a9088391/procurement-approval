# 測試與驗證

## 最終整合實測

2026-11-05，修正登入撤銷與 CSRF 檢查順序後，獨立重跑 Release HTTP＋真實 PostgreSQL 套件為 **32/32 passed**（8 秒、0 skipped）。原競爭案例仍嚴格要求一個成功、一個失效登入；新增的兩個案例先固定重現舊 Cookie＋舊 token 回錯誤類別，再驗證修正，不放寬斷言。對應格式檢查、Release 建置均通過，0 warning／0 error。

以最終程式重建完整網站後，獨立重跑下方 Chromium 套件兩輪均 **11/11 passed**（各 5.6 秒、retries 0）。兩輪沿用相同測試資料庫，依公開 setup 只重啟自身 app 以重建記憶體節流。四帳號、真改密碼／撤銷及未知結果恢復均有執行；標示 mocked 的案例維持下方揭露的邊界。

另在主機 `127.0.0.1:5080` 完成單一 Compose 啟動、HTTP 登入／改密碼／登出及停止重啟保存，詳見[本機展示](local-demo.md)。IAB 實際開啟相同網址，1280×720 登入畫面非空、無橫向溢出／錯誤覆蓋，主控台無 warning／error；Chromium 自動化另外涵蓋 1280px／390px 登入與已登入畫面。型別、樣式及正式前端建置證據可重用，後端順序修正未改前端資產。

以上是功能與本機整合結果，不代表負載容量、其他平台、其他瀏覽器或正式外部部署已通過。

## HTTP 與資料庫

所有命令在專案根目錄執行，使用 Docker Compose 專案 `procurement-approval-test`。測試 PostgreSQL `18.6` 在獨立內部網路與 tmpfs，無發布連接埠，不連到展示資料庫。測試庫僅在這個隔離環境採 trust 登入，不得複製至展示或其他部署。

```sh
docker compose -p procurement-approval-test -f compose.test.yaml run --rm tests
docker compose -p procurement-approval-test -f compose.test.yaml run --rm tests dotnet build tests/Procurement.IntegrationTests.csproj -c Release --no-restore
docker compose -p procurement-approval-test -f compose.test.yaml run --rm tests dotnet format tests/Procurement.IntegrationTests.csproj --verify-no-changes --no-restore
```

首次執行會還原 lock file 固定相依；若要檢查鎖檔與套件一致，執行：

```sh
docker compose -p procurement-approval-test -f compose.test.yaml run --rm tests dotnet restore tests/Procurement.IntegrationTests.csproj --locked-mode
```

`WebApplicationFactory` 執行真實 HTTP middleware、Identity 與 EF；不使用 InMemory。測試啟動前拒絕缺少設定、非 `db` 主機、非 `procurement_test` 資料庫或使用者，避免誤對展示庫執行故障注入。每個案例使用隨機帳號，測試不平行執行；初始化案例只處理測試庫的四個示範帳號。重跑不需清除資料庫。若中斷於故障注入，測試庫可能仍留測試 trigger，可停止並移除**該測試專案**的容器後重跑，tmpfs 會重新建立：

```sh
docker compose -p procurement-approval-test -f compose.test.yaml down
```

測試不輸出密碼、Cookie 或連線憑證。程式碼與 HTTP 失敗斷言也應避免將這些值納入診斷。

## 覆蓋方式

- `AuthTests`：401 JSON、Cookie 設定、資料庫當前角色／停用、12／128 及 11／129 邊界、CSRF 無效無寫入、鎖定前／等於／後、來源節流、閒置與絕對期限、登出與全部登入撤銷、舊 Cookie 重播。
- `TransactionTests`：過期版本、錯誤目前密碼、Cookie 驗證後被撤銷或停用、撤銷完成後舊 Cookie／CSRF 呼叫改密碼與登出得到 `401 unauthenticated`、兩個實際 HTTP 請求同時等待 PostgreSQL 列鎖，以及刪除登入後密碼更新故障的一起回滾。後續 HTTP 讀取與登入確認最終結果。
- `SetupTests`：實際 CLI 子程序建立管理／示範帳號、受保護檔案權限、重試保存原憑證、輸出失敗不留帳號、一般輸出不含初始密碼。

WAF 只以 DI 取代 `TimeProvider` 及測試交易攔截器，正式程式沒有調時間或注入故障的 API。時間案例直接推進測試時鐘，不等待十五分鐘或八小時；競爭案例以 PostgreSQL 等待狀態或交易屏障確定真正重疊，不用任意延遲假裝競爭。

2026-11-05 首個 tracer 測試先觀察到 `/api/auth/me` 的 `404`，接上端點後得到 `401` JSON。停用競爭測試另先觀察到錯誤 `204`；修正在帳號列鎖前清除 EF 舊追蹤狀態，重新載入目前資料後，該案例回 `401`。這些是已實跑的 Red／Green，並非所有測試都先有 Red。

2026-11-05 初次 ARM64 Docker 驗證使用 SDK `10.0.403`、執行階段 `10.0.14`，PostgreSQL `18.6`；三十個案例在同一測試庫連續兩次通過。locked restore、格式 check-only 及 Release 建置通過，建置零警告／零錯誤。後端 Docker runtime 映像亦成功建置。這是當時的歷史結果；後續獨立重跑仍發現下述競爭缺陷。

同日獨立重跑出現一個 `204`、一個 `400` 的改密碼競爭結果。新增的兩個確定性案例證實：撤銷完成後，舊身分 token 會先被 CSRF 拒絕成 `400 invalid_csrf`。將既有失效登入拒絕提前後，兩個案例都回 `401 unauthenticated`，原競爭案例仍嚴格要求一個 `204`、一個 `401`；有效登入及匿名登入的 CSRF 拒絕也維持。修正後六個針對性案例及完整三十二個案例通過，格式 check-only 與 Release 建置通過，零警告／零錯誤。

以上是後端 slice 的結果；完整前端、瀏覽器、展示連接埠與重啟保存結果見本文件開頭的最終整合實測。HTTP 測試本身不能替代這些檢查。未量測負載容量，也未驗證其他平台。

## 前端與瀏覽器

前端型別、建置及格式命令見 [前端開發](frontend.md)。瀏覽器使用獨立 Compose 專案 `procurement-approval-browser`、`.local-browser/` 憑證及自己的 `database` volume；不連展示庫或 HTTP 整合測試庫。應用只綁定 `127.0.0.1:5081`，資料庫不發布連接埠。需支援 Compose `!override` 的版本。

在專案根目錄依序執行：

```sh
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml run --rm --no-deps frontend-tools npm ci
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml up --build -d app
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml restart app
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml run --rm e2e
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml down
```

最後一個命令只停止並移除隔離瀏覽器容器／網路，保留 volume 及憑證，不加 `--volumes`。不能把專案名稱或掛載目錄改成展示環境。測試 fail-closed 檢查目標 URL、環境標記及憑證權限，使用 Chromium、單一 worker，不自動重試；trace、video 與自動失敗截圖關閉。手動截圖只保存遮罩後的畫面到忽略的本機證據目錄；不得複製 Cookie、密碼或請求本文至測試輸出。

每次重跑前只 `restart app` 這個隔離專案的應用程序，重建正式規則的記憶體來源節流計數；不重設資料庫、密碼或金鑰。若緊接著重跑而未重啟，前一輪登入嘗試仍可能計入正式的每分鐘 30 次上限，合法得到 429；不要放寬斷言或繞過節流。

真 API 流程涵蓋四種示範身分類別、錯誤帳密後修正、登出及重新載入、錯誤目前密碼後修正、改密碼成功回登入、舊密碼拒絕／新密碼登入、另一個舊登入失效。密碼測試最後只把隔離 `manager` fixture 改回原值，便於重跑，不重設資料庫。

標示 `mocked` 的案例以 Playwright 攔截特定回應，驗證 CSRF／401／500／transport 的畫面恢復、敏感表單清除、禁止自動重送及 pending 防重複提交；它們不證明後端交易或撤銷機制。桌機 `1280px` 與手機 `390px` 檢查登入及已登入畫面的可見輸入、無水平 overflow、標籤及鍵盤焦點；監測未處理的瀏覽器程式錯誤。

另以真 API 成功提交改密碼後中斷瀏覽器回應，驗證結果未知提示在重新讀取失敗及舊密碼登入被拒絕後仍保留，最後用新密碼登入恢復。此中斷由測試驅動，不新增正式故障端點。非敏感短字串以真實輸入確認原生 minlength 阻擋送出；敏感 fixture 的程式注入不當作原生長度驗證證據。

2026-11-05，ARM64 Docker 的 Node `24.22.2`／npm `11.19.1` 完成鎖檔還原、strict 型別、Vite 正式建置及 Prettier check-only。官方 Playwright `1.64.1` 容器實際使用 Node `26.13.0`／npm `11.20.0` 執行測試（不作前端建置），11 個瀏覽器案例全數通過，5.7 秒。登入與已登入畫面的桌機／手機遮罩截圖已檢視，沒有水平裁切或遮擋控制項。瀏覽器容器透過保留的 `.test` DNS 別名存取同源網站；正式展示仍使用 `127.0.0.1:5080`。完整展示環境的停止／重啟保存及獨立 IAB 驗收亦已另行完成，見本文件開頭；隔離瀏覽器測試本身不證明展示保存。
