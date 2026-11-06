# 測試與驗證

## 指定 HTTP 負載與獨立 CSV 匯出

在專案根目錄執行。兩組命令各自建立 Compose 網路與 PostgreSQL tmpfs，網站及資料庫均無主機連接埠。效能測試使用 Release Kestrel、正式 API、實際 PostgreSQL 及各業務帳號的 Cookie 和 CSRF 權杖。測試只接受空白測試庫，預載後會先執行 PostgreSQL `ANALYZE`。API 容器的日誌等級設為 `Warning`。每個場景結束後執行該組的 `down`，即使測試失敗也一樣。重跑前清理舊場景，讓 PostgreSQL 從空白重新套用 migration。

```sh
docker compose -p procurement-approval-benchmark-load -f compose.benchmark.yaml up --build -d benchmark-app
docker compose -p procurement-approval-benchmark-load -f compose.benchmark.yaml run --rm benchmark Load
docker compose -p procurement-approval-benchmark-load -f compose.benchmark.yaml down
```

```sh
docker compose -p procurement-approval-benchmark-csv -f compose.benchmark.yaml up --build -d benchmark-app
docker compose -p procurement-approval-benchmark-csv -f compose.benchmark.yaml run --rm benchmark Csv
docker compose -p procurement-approval-benchmark-csv -f compose.benchmark.yaml down
```

負載場景初始有 10,000 張請購，每張 5 筆明細。四位申請人各有 2,500 張，分別查本人未篩選的清單、明細及統計。草稿保存、送審及核准使用預先保留且互不重疊的既有單。主管與財務各執行本人有資格、輪到本人審核的案件。預熱 60 秒送出 1,800 個請求，完成後正式量測 300 秒。專用排程執行緒以單調時鐘安排每秒 30 個時槽，下一筆不等待前一筆回應。正式量測的 300 個單秒區間各須實際送出 30 筆，任何一筆落後超過一個時槽（約 33.33 毫秒）也會判定該輪未通過。各操作的 p95 依實際開始到完整讀完 HTTP 回應本文計算。計時結束後核對 PostgreSQL 的主檔、版本、明細、快照與操作歷程。成功寫入必須同時有 HTTP 成功回應、完整落庫與一致的版本及更新時間。未取得資料庫核對結果時，寫入數顯示 `unverified`。預熱與正式量測都會更新既有資料，請購與明細總數保持 10,000 與 50,000。

CSV 使用全新的測試庫。合法申請人有 10,000 張符合日期、部門與狀態篩選的送審單。另放入一張同部門、同條件但屬於他人的單，以及本人日期與狀態不符的各一張單。測試從請求開始計到完整接收下載，並逐列核對七欄、唯一單號、篩選與權限。含逗號、引號、換行的舊資料樣本和公式前綴也會核對。CSV 另行執行，時間上限為 5 秒。

### 2026-11-06 ARM64 實測

主機為 Apple M3（8 核）、24 GiB RAM、macOS 27.0.1。OrbStack 的 Linux ARM64 Docker VM 提供 8 vCPU 與約 11.73 GiB RAM，容器無個別 CPU 或記憶體配額。Docker Engine 29.8.2、Compose 5.5.0、PostgreSQL 18.6、.NET SDK 10.0.403、ASP.NET Core 與 EF Core 10.0.14、Npgsql 及其 EF Core provider 10.0.3、xUnit 2.9.3。

兩輪獨立 CSV 完整接收分別為 **106.20 毫秒**與 **109.11 毫秒**，每次接收 1,529,000 bytes。兩輪均核對 10,000 筆唯一資料及全部欄值，排除 3 張哨兵單。同部門的他人單據，原申請人查明細回 404，實際所有人查明細回 200 且能以相同條件匯出該筆資料。

修正版兩輪皆從 PostgreSQL 核對 10,000 張請購與 50,000 筆明細，逐單恰有五筆。每輪預熱的 1,800 筆與正式的 9,000 筆都送出並完整接收，且無拒絕、衝突或錯誤。預熱的 360 筆寫入與正式的 1,800 筆寫入全數對帳成功。兩輪皆在量測期間暫時防止主機閒置休眠。

修正版第一輪的 300 個單秒區間各實送 30 筆，但有一個時槽落後 53.352 毫秒，超過 33.333 毫秒的排程檢查上限，該輪未通過。第二輪同樣每秒實送 30 筆，最大排程落後 13.842 毫秒，通過完整基準。下表只列正式量測。兩輪的預定、實送、完成及 HTTP 成功數相同。

| 操作 | 各輪請求數 | 各輪成功寫入 | p95 上限 | 第一輪 p95 | 第二輪 p95 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 清單 | 4,500 | — | 500 ms | 20.39 ms | 20.06 ms |
| 明細 | 1,800 | — | 500 ms | 9.94 ms | 9.83 ms |
| 統計 | 900 | — | 1,000 ms | 10.62 ms | 10.67 ms |
| 草稿保存 | 900 | 900 | 1,000 ms | 3.71 ms | 3.70 ms |
| 送審 | 450 | 450 | 1,000 ms | 17.53 ms | 18.65 ms |
| 合法審核 | 450 | 450 | 1,000 ms | 17.63 ms | 17.26 ms |

較早兩輪曾因測試器排程落後，單秒實送分別落在 21–39 與 29–31 筆。這兩輪雖送足總量，僅供診斷。以上通過結果限本機 ARM64 的指定資料量、角色分布與請求組合。其他平台與更高容量尚待量測。

## 管理員復原與展示重設

CLI 復原的自動化案例由正式 `recover-admin` 子程序及 HTTP 驗證。測試使用下方的隔離 PostgreSQL，涵蓋解除鎖定、舊密碼和多個舊登入失效、新密碼可登入、業務登入保留，以及輸出失敗時回滾。

```sh
docker compose -p procurement-approval-test -f compose.test.yaml run --rm tests dotnet test tests/Procurement.IntegrationTests.csproj -c Release --filter FullyQualifiedName~SetupTests
```

### 隔離重設驗收

在可拋棄的專案副本執行。使用專用 Compose 專案名稱、獨立資料卷及該副本的 `.local-browser/`，先核對 `127.0.0.1:5081` 可用。以下三個命令分別啟動、復原與要求重設。輸入確認前先完成資料準備及取消檢查。

```sh
docker compose -p procurement-approval-reset-test -f compose.yaml -f compose.browser.yaml up --build -d app
docker compose -p procurement-approval-reset-test -f compose.yaml -f compose.browser.yaml run --rm --no-deps app recover-admin
COMPOSE_FILE=compose.yaml:compose.browser.yaml sh reset-demo.sh procurement-approval-reset-test
```

1. 先在 `http://127.0.0.1:5081` 建立部門、預設審核人與一個額外業務帳號。由申請人送審、撤回、修改並重送，再由管理員改派，保存單號並記下可查的兩輪內容與完整歷程。停止並重啟網站和資料庫，核對原登入仍可讀取同一份資料。
2. 以兩個瀏覽器工作階段登入 `admin`。執行復原命令後，重新載入兩個畫面，應回到登入表單。舊密碼應遭拒絕，新憑證可登入。用申請人的原登入重讀請購和歷程，再用新管理登入核對部門、帳號和改派設定。
3. 在專用測試 PostgreSQL 中建立另一個資料庫，放入一筆可辨識的哨兵。執行重設後先檢查它顯示的專案、資料庫、清除範圍與無備份無法復原的訊息。按 Enter 取消。保持相同 Compose 專案，再執行一次並在提示後輸入錯誤的確認句，例如 `reset wrong-project/db/procurement`。這兩次結束後，重新讀回組織、請購與哨兵，均應保留。
4. 再執行重設並依提示輸入精確確認句。從新的 `reset-accounts-*.json` 取得四個新密碼。舊登入、舊密碼及額外業務帳號應失效，新帳號可登入。用管理畫面確認部門及改派清單為空，以新申請人確認已知單號無法讀取。到非目標資料庫讀回相同哨兵，重啟後再用新憑證登入。

### 重跑重設失敗路徑

- **錯誤掛載**。在兩個可拋棄的 Compose 專案各建立資料卷和哨兵。另用測試覆寫建立一個錯掛其他測試卷的 `db` 容器，把該卷以唯讀方式掛在 `/var/lib/postgresql`，將容器入口改為 `sleep` 以防在錯掛卷啟動 PostgreSQL。用 `docker inspect` 核對容器的專案標籤和實際掛載後，針對錯掛的專案執行腳本。應在確認提示前回報資料卷不符。移除錯掛容器後，從原本兩個專案讀回各自的哨兵。
- **同名卷與替代資料目錄**。使用可拋棄的測試卷複本，在測試覆寫中保留原卷名，但為本機卷加入指向複本的 bind `driver_opts`。另一個案例保留標準掛載，再將複本掛到 `/foreign`，設定 `PGDATA=/foreign/18/docker`。兩案都應在確認提示前被拒。用 `SHOW data_directory` 核對第二案的伺服器確實使用替代路徑，再從原專案讀回哨兵。
- **過期確認**。在同一個隔離專案開啟兩個重設預覽，先完成其中一個，再送出另一個的確認句。第二次應被拒絕，第一次產生的密碼仍可登入。另開一次預覽，在另一個工作階段用 HTTP 新增部門並確認保存，再送出舊確認句。應回報資料變更並保留新增部門與原請購。
- **鎖逾時**。讓腳本停在預覽。在專用測試庫的互動式 `psql` 執行 `SELECT pg_advisory_lock(80101);` 並保持連線。輸入正確確認句後，應回報等待鎖定逾時。結束 `psql` 釋放鎖，從網站重讀確認原資料保留。
- **重建失敗回滾**。在專用測試庫加上下面的暫用 trigger，確認重設應回報失敗。用原管理密碼登入，再核對組織、兩輪快照及改派歷程完整。驗證後執行 `DROP TRIGGER reset_test_fail ON public."AspNetUsers"; DROP FUNCTION public.reset_test_fail();`，才能再驗證成功路徑。

```sql
CREATE FUNCTION public.reset_test_fail() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'test seed failure';
END;
$$;
CREATE TRIGGER reset_test_fail BEFORE INSERT ON public."AspNetUsers"
FOR EACH ROW EXECUTE FUNCTION public.reset_test_fail();
```

2026-11-06 在 macOS ARM64、Compose 5.5.0 和 PostgreSQL 18.6 實測。修正前後的 Release HTTP 回歸各 **185／185**。Chromium 1.64.1 的既有回歸 **35／35** 通過，並以最終映像定向複驗實際復原與重設。兩次都看到舊登入回到登入表單、舊密碼遭拒、新密碼可登入。鎖定套件還原、Release 建置、.NET 格式、EF 模型一致性、TypeScript、Vite、Prettier 與 Shell 語法檢查均通過。

在專用資料卷從上一版原地升級，經 HTTP 核對組織設定、請購、兩輪快照與改派歷程。按 Enter、錯誤確認、非目標連線、錯掛測試卷與鎖逾時均保留資料。同名卷轉接和替代 `PGDATA` 都在確認前被拒，切換 Docker context 的模擬也證明後續命令使用同一個已核對的 socket。注入重建失敗時整筆交易回滾。另一個重設或新增部門先提交時，先前的確認句會失效。確認清空後四個新示範帳號可登入，非目標資料庫的哨兵仍可讀取，重啟後再核對新登入與清空結果。

## 請購統計與 CSV

使用下方的隔離 [HTTP 與資料庫](#http-與資料庫) 及 [瀏覽器](#前端與瀏覽器) 環境。可定向執行報表案例。

```sh
docker compose -p procurement-approval-test -f compose.test.yaml run --rm tests dotnet test tests/Procurement.IntegrationTests.csproj -c Release --filter FullyQualifiedName~RequisitionReportingTests
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml restart app
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml run --rm e2e npm run test:e2e -- e2e/requisition-approvals.spec.ts --grep '請購統計與 CSV'
```

HTTP 案例透過 PostgreSQL 核對臺灣日界線、調部門及重送、角色聯集、改派與撤權、精確 TWD 合計及相同條件下的清單和七欄 CSV。CSV 由支援引號、逗號和換行的解析器讀取。由於正式寫入要求匯出的文字欄位為單行，換行測試只在隔離資料庫為該案例安排舊資料樣本，仍經正式 API 下載並查回原文。

2026-11-06 在 macOS ARM64、PostgreSQL 18.6 及 .NET SDK 10.0.403 驗證。定向報表測試 **6／6**，完整 Release HTTP **182／182**，另一個從空白啟動的資料庫獨立重跑也是 **182／182**。鎖定還原、Release 建置、格式與 EF 模型一致性檢查通過。

隔離 Chromium（Playwright 1.64.1）最終分批共 **35／35**，包含報表篩選與實際下載、兩個同名送審部門、保留同一登入時的即時撤權，以及另一分頁換帳號後的防偽拒絕。傳輸失敗案例由瀏覽器測試刻意中斷請求，驗證重讀走真實 API，且較晚的真實防偽拒絕仍會觸發身分恢復。已檢查 390／1280 px 版面及鍵盤焦點，TypeScript、Vite 與 Prettier 通過。

隔離 Compose 使用舊版先送審，再從同一資料卷升級。既有登入和原送審明細可讀取，清單、統計與匯出都包含原單。執行 `initialize-demo` 並停止、重啟網站及 PostgreSQL 後，原登入及新登入仍能讀回相同資料，CSV 位元組相同。另以 LibreOffice Calc 26.8 開發版啟用公式匯入，未防護的 `=1+1` 對照組被辨識為公式，實際 HTTP 匯出中的合成主旨保留文字。試算表結果限該次 Calc 匯入，負載與萬筆匯出耗時另行量測。

### 手動驗證

1. 依 [送審步驟](../README.md#送審與兩關簽核) 建立並送出一張已知預估金額的單，保留另一張未送審草稿。登入業務帳號，開啟「請購統計與 CSV」。用該單的臺灣送審日、部門及目前狀態套用條件，核對單號、件數與金額。草稿留在本人草稿入口。
2. 下載 CSV，核對只有七欄且與清單單號一致。可用含中文、逗號、雙引號或以 `=1+1` 開頭的合成主旨核對匯出。試算表應以文字顯示公式樣本，回到請購明細應看到原始主旨。
3. 由兩關依序核准後，將「目前狀態」切為「核准結案」再套用條件，確認該單的金額標為「已核准的預估申請金額」。在另一分頁切換帳號後，原報表的下一次讀取或下載應要求重新確認身分。管理帳號使用管理功能，沒有報表入口。

## 有限改派與待辦恢復實測

2026-11-06 在 macOS ARM64、Docker Compose 5.5.0、固定 .NET SDK 10.0.403 及隔離 PostgreSQL 18.6 完成 Release HTTP 整合套件，**176／176 通過**。另在空白的獨立測試庫套用正式遷移後重跑，同樣 **176／176 通過**。涵蓋改派欄位隔離、未完成資格補齊、舊指派即時撤權、雙管理員競爭、改派與審核及撤權與審核的兩種提交順序，以及提交失敗時回滾指派、版本及歷程。鎖定還原、Release 建置、兩個 .NET 專案的格式檢查及 EF 模型一致性均通過。

隔離網站由撤回與重送版升級。升級前後 3 筆請購、3 筆明細、4 輪快照與 9 筆事件的舊欄位校驗值相同。以 HTTP 建立、暫停並改派一張請購後執行 `initialize-demo`，停止並重啟 app 與 PostgreSQL。原登入與新登入讀回相同的有效指派、快照、版本及歷程，初始帳號檔校驗值相同。隔離遷移測試另驗證已有改派紀錄時，退回舊 schema 遭拒絕並回滾。

使用固定 Playwright 1.64.1 的 Chromium，新增改派 **3／3**、既有簽核 **9／9**、登入與組織 **14／14**、草稿 **6／6** 通過。改派案例另在同一隔離資料卷獨立重跑 **3／3**，檢查 390 px 與 1280 px 的文字換行及鍵盤焦點。版本衝突由真 API 產生。回應中斷在真交易提交後由測試驅動，驗證只手動 GET、不自動重送，並從業務明細讀回歷程。表單遇到 `400 invalid_input` 時保留欄位的畫面檢查使用模擬回應，真實拒絕與持久化由 HTTP／PostgreSQL 測試驗證。固定 Node 容器的 TypeScript、Vite 建置與 Prettier 檢查通過。 組織設定已保存但讀回失敗時，切換管理面板後仍保留確認，手動 GET 成功才結束讀回。此案例的讀回 401 使用模擬回應，保存、成功讀回及身分重驗都走真 API。改派案例另驗證明確登出再登入後清除原待核對標記，只讀取目前指派。

### 手動驗證

1. 依 [操作步驟](../README.md#有限改派與待辦恢復) 建立替補審核人，送審後由管理員撤除舊主管角色。以申請人讀回，確認仍是主管待審且標示暫停。
2. 以管理帳號開啟有限改派。核對單號、送審部門、待審關卡及目前人選，主旨、用途、明細及預估金額應不出現。選擇合格人選並輸入原因後改派。
3. 用申請人重新讀回，核對原內容及部門、前後人選和原因。兩個未完成關卡皆具合格人選時，新主管可從原關卡繼續。若出現過期版本或結果未確認，先按「重新讀取結果」核對目前指派。

## 撤回與重送實測

2026-11-05 在 macOS ARM64、Compose 5.5.0 和隔離 PostgreSQL 18.6 執行 Release HTTP 整合套件，**164／164 通過**。撤回與主管決定分別先進入 PostgreSQL 等鎖佇列，兩種順序都只留下單一合法結果。另驗證撤回與重送提交失敗的回滾、停用先完成時拒絕撤回、舊輪次分權及多輪快照。Release 建置零警告及錯誤，鎖定還原、兩個 .NET 專案格式及 EF 模型一致性皆通過。

Chromium 簽核套件在同一隔離資料卷分批執行兩輪，各 **7／7 通過**。新案例實際提交撤回後中斷回應，手動讀回再保存及重送同一單號。申請人可展開第一輪，主管只見最新輪。展開的明細通過 390／1280 px 的長字串斷行及控制項檢查。另執行登入與組織 **14／14**、草稿 **6／6**，TypeScript、Vite 及 Prettier 皆通過。各瀏覽器批次前重啟隔離 app，避免累積觸發既有登入節流。

2026-11-06 另以真實 HTTP 安排撤回先提交，定向中斷較晚核准的回應。分別驗證仍撤回與已重送兩種讀回。畫面會說明舊操作結果無法確認，保留其他請購的核對入口，並依資格提供新一輪的操作。修正後簽核套件 **9／9**，獨立重跑新增兩項 **2／2**，完整 Release HTTP 仍為 **164／164**。

隔離 Compose 網站從已有固定兩關簽核資料的版本原地升級。升級前後的 152 筆請購、127 筆明細、89 輪快照及 172 筆事件，整列內容校驗一致。建立含撤回與重送的資料後，再執行 `initialize-demo` 並停啟 app 與 PostgreSQL，四張請購資料表的內容校驗一致。原登入與新登入也經 HTTP 讀回相同的既有明細。

### 手動驗證

1. 依 [操作步驟](../README.md#送審與兩關簽核) 設定部門及人選。由申請人保存一筆完整請購並送審，記下單號。主管在第一次決定前可讀取該單。
2. 由申請人開啟該單並撤回。確認狀態變為撤回修改中且歷程新增撤回紀錄。重新讀取主管的列表，該單應消失。由申請人從「本人請購草稿」重開原單，修改主旨及明細並保存。編輯畫面沒有刪除按鈕。
3. 在組織管理為申請人更換部門及預設人選。由申請人重送同一單號，確認從新輪次的主管待審開始。申請人可展開舊快照核對原內容、送審部門及人選；符合查閱資格的主管及財務只取得最新一輪。財務要等新一輪主管核准才取得該輪的財務查閱權。
4. 由新一輪主管作出決定，再由申請人讀回，確認撤回入口已消失。若畫面提示寫入結果未確認，使用「重新讀取結果」核對狀態及歷程後再決定下一步。

## 固定兩關簽核實測

2026-11-05 在 macOS ARM64、Compose 5.5.0 和隔離 PostgreSQL 18.6 執行 Release HTTP 整合套件，**155／155 通過**，包含 39 個新簽核案例。Release 建置零警告及錯誤。鎖定還原、兩個 .NET 專案格式、EF 模型一致性與固定 Node 容器的 TypeScript、Vite、Prettier 檢查均通過。

隔離 Chromium 的簽核套件在同一資料卷連跑兩輪，各 **6／6 通過**。登入與組織回歸 **14／14 通過**，草稿回歸 **6／6 通過**。簽核明細在 390／1280 px 驗證鍵盤焦點、長主旨與長品名斷行及操作按鈕。

隔離 Compose 資料卷從本人草稿版本升級。升級前經 HTTP 保存的草稿，升級後從原草稿及新明細 API 讀回，內容、TWD 金額與版本相同。另用 HTTP 完成兩關核准，執行 `initialize-demo`，停止並重啟 app 與 PostgreSQL，原登入與重新登入均逐欄讀回相同輪次、決定及版本。網站綁定 `127.0.0.1:5081`，PostgreSQL 使用內部連線。

## 本人請購草稿實測

2026-11-05 在 macOS ARM64、Compose 5.5.0 和隔離 PostgreSQL 18.6 執行 Release HTTP 整合套件，**116／116 通過**，包含 53 個本人草稿案例。Release 建置零警告及錯誤，EF 模型一致性與兩個 .NET 專案的格式檢查均通過。

Chromium 的登入與組織管理回歸 **14／14 通過**。本人草稿在同一隔離資料卷連續兩輪 **6／6 通過**，涵蓋 390／1280 px 版面、鍵盤焦點、超精度拒絕、版本衝突、失效登入、同一瀏覽器雙分頁換帳號、回應遺失後人工讀回，以及確認刪除。固定 Node 容器的 TypeScript、Vite 與 Prettier 均通過。

隔離 Compose 網站從既有組織管理資料卷升級，四個示範帳號識別均保留。用 HTTP 保存未填完的草稿，執行 `initialize-demo`，停止並重啟網站與 PostgreSQL 後，原登入及同帳號重新登入都讀回相同的內容、TWD 金額與版本。網站實際綁定 `127.0.0.1:5081`，PostgreSQL 沒有主機連接埠。

## 組織管理實測

2026-11-05 在 macOS ARM64、Compose 5.5.0 和 PostgreSQL 18.6 的隔離環境驗證。Release HTTP 整合套件 **63/63 通過**，含 31 個組織管理案例與 32 個登入基線案例。新增案例以測試時鐘驗證成功寫入更新閒置時間，以及 CR／LF 拒絕後的資料與登入狀態。套件還原、Release 建置、兩個 .NET 專案格式、EF migration 模型一致性、TypeScript、Vite 及 Prettier 均通過。

Chromium 在同一隔離資料庫連續兩輪 **14/14 通過**。管理畫面涵蓋部門、帳號、角色及雙審核人讀回，建立及重設後的強制改密碼，兩個管理工作階段的過期保存，以及 390／1280 px 版面與 Tab 焦點。獨立復原案例驗證保存成功後讀回失敗、先前未決警示與本次成功提示分開顯示。清除隔離工作階段的防偽 Cookie 後，真 API 拒絕寫入，畫面手動確認身分後恢復且只讀取資料。

用隔離瀏覽器資料卷驗證原地升級，原有四個帳號識別與狀態均保留。在該卷建立設定後重啟應用，同一管理登入讀回的完整設定相同。這次未執行降版或量測負載。

## 登入基線實測

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
- `OrganizationTests`、`OrganizationCredentialTests` 驗證管理授權、輸入、部門及審核人關聯、目前角色、停用及強制改密碼。以新的 HTTP 查詢及登入確認保存和撤銷。
- `OrganizationTransactionTests` 在真 PostgreSQL 安排兩個管理請求同時等待資料庫鎖、在 Cookie 驗證後撤銷資格，並以測試 trigger 驗證設定、憑證與登入可一起回滾。
- `RequisitionDraftTests` 從 HTTP 建立、保存、重開及刪除本人草稿，驗證欄位缺漏、原始 JSON 數字的精度與上限、所有權、強制改密碼、偽造欄位、防偽及舊登入。切換 Cookie 帳號後，清單及詳情都拒絕舊權杖，並驗證當前權杖與只帶有效 Cookie 的讀取結果一致。以 `me` 同一回應發出的權杖驗證草稿讀寫，帳號失配時兩種操作均拒絕。
- `RequisitionDraftTransactionTests` 在兩筆相同版本的草稿保存都等待真 PostgreSQL 帳號列鎖後放行，確認一筆成功、另一筆回 409。另以管理 API 先提交停用，以及明細插入故障，驗證拒絕及回滾。每個案例用新的 HTTP 讀取核對最終版本、金額及明細。

- `RequisitionApprovalTests`、`RequisitionApprovalAuthorizationTests` 及 `RequisitionApprovalValidationTests` 從 HTTP 驗證送審欄位、金額、部門與人選快照、核准及駁回、依當前角色與部門查閱、舊登入和過期資料拒絕。測試以既有管理 API 設定資格並核對實際清單、明細與歷程。
- `RequisitionApprovalTransactionTests` 觀察兩筆請求實際等待 PostgreSQL 交易鎖，再核對重複及相反決定。另用管理 API 先提交撤權，以隔離資料庫的延後觸發器使提交故障，驗證原關卡、快照、版本及歷程的一致性。`RequisitionApprovalMigrationTests` 在隔離 schema 比對升級前後的舊草稿及明細。

- `RequisitionReassignmentTests` 與 `RequisitionReassignmentTransactionTests` 透過正式 HTTP API 核對欄位隔離、角色與成對權杖、可用關卡、暫停恢復、原因與前後人選。測試觀察 PostgreSQL 交易等待及延後提交故障，從新 HTTP 讀取檢查最終資料與歷程。

只重跑本人草稿、簽核、撤回重送或有限改派的 HTTP 驗證時，使用 Release 測試篩選。撤回的競爭、交易回滾及遷移案例由簽核篩選涵蓋。

```sh
docker compose -p procurement-approval-test -f compose.test.yaml run --rm tests dotnet test tests/Procurement.IntegrationTests.csproj -c Release --filter FullyQualifiedName~RequisitionDraft
docker compose -p procurement-approval-test -f compose.test.yaml run --rm tests dotnet test tests/Procurement.IntegrationTests.csproj -c Release --filter FullyQualifiedName~RequisitionApproval
docker compose -p procurement-approval-test -f compose.test.yaml run --rm tests dotnet test tests/Procurement.IntegrationTests.csproj -c Release --filter FullyQualifiedName~RequisitionWithdrawal
docker compose -p procurement-approval-test -f compose.test.yaml run --rm tests dotnet test tests/Procurement.IntegrationTests.csproj -c Release --filter 'FullyQualifiedName~RequisitionReassignment|FullyQualifiedName~RequisitionApprovalMigration'
```

WAF 只以 DI 取代 `TimeProvider` 及測試交易攔截器，正式程式沒有調時間或注入故障的 API。時間案例直接推進測試時鐘，不等待十五分鐘或八小時；競爭案例以 PostgreSQL 等待狀態或交易屏障確定真正重疊，不用任意延遲假裝競爭。

2026-11-05 首個 tracer 測試先觀察到 `/api/auth/me` 的 `404`，接上端點後得到 `401` JSON。停用競爭測試另先觀察到錯誤 `204`；修正在帳號列鎖前清除 EF 舊追蹤狀態，重新載入目前資料後，該案例回 `401`。這些是已實跑的 Red／Green，並非所有測試都先有 Red。

2026-11-05 初次 ARM64 Docker 驗證使用 SDK `10.0.403`、執行階段 `10.0.14`，PostgreSQL `18.6`；三十個案例在同一測試庫連續兩次通過。locked restore、格式 check-only 及 Release 建置通過，建置零警告／零錯誤。後端 Docker runtime 映像亦成功建置。這是當時的歷史結果；後續獨立重跑仍發現下述競爭缺陷。

同日獨立重跑出現一個 `204`、一個 `400` 的改密碼競爭結果。新增的兩個確定性案例證實：撤銷完成後，舊身分 token 會先被 CSRF 拒絕成 `400 invalid_csrf`。將既有失效登入拒絕提前後，兩個案例都回 `401 unauthenticated`，原競爭案例仍嚴格要求一個 `204`、一個 `401`；有效登入及匿名登入的 CSRF 拒絕也維持。修正後六個針對性案例及完整三十二個案例通過，格式 check-only 與 Release 建置通過，零警告／零錯誤。

以上是後端歷史驗證結果。對應的前端、瀏覽器、展示連接埠與重啟保存證據見「登入基線實測」。

## 前端與瀏覽器

前端型別、建置及格式命令見 [前端開發](frontend.md)。瀏覽器使用獨立 Compose 專案 `procurement-approval-browser`、`.local-browser/` 憑證及自己的 `database` volume；不連展示庫或 HTTP 整合測試庫。應用只綁定 `127.0.0.1:5081`，資料庫不發布連接埠。需支援 Compose `!override` 的版本。

在專案根目錄依序執行：

```sh
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml run --rm --no-deps frontend-tools npm ci
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml up --build -d app
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml restart app
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml run --rm e2e npm run test:e2e -- e2e/auth.spec.ts e2e/organization.spec.ts
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml restart app
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml run --rm e2e npm run test:e2e -- e2e/requisition-drafts.spec.ts
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml restart app
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml run --rm e2e npm run test:e2e -- e2e/requisition-approvals.spec.ts --grep-invert '有限改派|請購統計與 CSV'
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml restart app
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml run --rm e2e npm run test:e2e -- e2e/requisition-approvals.spec.ts --grep '有限改派'
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml restart app
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml run --rm e2e npm run test:e2e -- e2e/requisition-approvals.spec.ts --grep '請購統計與 CSV'
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml down
```

最後一個命令只停止並移除隔離瀏覽器容器／網路，保留 volume 及憑證，不加 `--volumes`。不能把專案名稱或掛載目錄改成展示環境。測試 fail-closed 檢查目標 URL、環境標記及憑證權限，使用 Chromium、單一 worker，不自動重試；trace、video 與自動失敗截圖關閉。手動截圖只保存遮罩後的畫面到忽略的本機證據目錄；不得複製 Cookie、密碼或請求本文至測試輸出。

每個批次開始前用 `restart app` 重建隔離網站的記憶體節流計數，沿用同一資料卷、帳號與金鑰。分批驗證登入／組織管理、草稿、既有簽核、有限改派與報表，避免合計的登入嘗試超過同來源每分鐘 30 次的正式限制。

真 API 流程涵蓋四種示範身分類別、錯誤帳密後修正、登出及重新載入、錯誤目前密碼後修正、改密碼成功回登入、舊密碼拒絕／新密碼登入、另一個舊登入失效。密碼測試最後只把隔離 `manager` fixture 改回原值，便於重跑，不重設資料庫。

`organization.spec.ts` 使用隨機命名的測試部門和業務帳號，連續重跑會在隔離資料庫留下各輪資料。案例從管理畫面建立部門及帳號，保存兩種審核角色和兩位預設審核人，再重讀確認設定。另從業務帳號驗證新增和重設後的強制改密碼，從兩個管理工作階段驗證過期保存與重新讀取。回應攔截案例另外驗證寫入已成功但讀回失敗，以及寫入結果未確認時的提示與手動讀取。

`requisition-drafts.spec.ts` 使用唯一合成主旨，從畫面驗證部分金額、超精度拒絕、重開及確認刪除，並在 390 px 和 1280 px 檢查表單可用性。兩個工作階段驗證真實的版本衝突及手動讀回。登出後以不同帳號及原帳號重新登入，確認草稿依本人隔離。雙分頁案例在另一分頁切換共用 Cookie 的帳號後，確認舊分頁先卸載內容，再由使用者確認身分、讀回及保存目前帳號的草稿。建立回應遺失的案例先由真 API 保存，再以 Playwright 中斷回應並讓第一次讀回失敗，驗證兩項提示都保留，重讀後可以開啟已保存的草稿。

`requisition-approvals.spec.ts` 透過隔離管理 API 建立唯一命名的測試部門與業務帳號，每輪保留獨立合成資料。畫面驗證申請人保存並送審、主管及財務依序核准、另案駁回、實際的版本衝突與撤權、窄版換行及鍵盤焦點。 案例以管理 API 接受的 U+FEFF 部門名稱驗證快照原值在送審與後續明細讀回時都能呈現。已提交的回應中斷、JSON 語法錯誤、可解析但結構錯誤的本文及讀回失敗由 Playwright 定向攔截，後端仍使用真實 API。案例驗證只讀回、同帳號保留未決提示、另一筆成功不抹除舊結果，以及跨分頁切換到須先改密碼的帳號後，寫入收到 403 並移除舊畫面。重新驗證這套案例前先依上方命令重啟隔離 app，重用既有資料卷。

身分查詢另以兩次回應屏障在同一瀏覽器固定 A→B→A 的切換順序。舊頁收到 B 的身分回應時，瀏覽器 Cookie 已回到 A。草稿查詢必須因權杖失配而拒絕，手動重驗後才能重開 A 的原草稿。

標示 `mocked` 的案例以 Playwright 攔截特定回應，驗證 CSRF／401／500／transport 的畫面恢復、敏感表單清除、禁止自動重送及 pending 防重複提交；它們不證明後端交易或撤銷機制。桌機 `1280px` 與手機 `390px` 檢查登入及已登入畫面的可見輸入、無水平 overflow、標籤及鍵盤焦點；監測未處理的瀏覽器程式錯誤。

另以真 API 成功提交改密碼後中斷瀏覽器回應，驗證結果未知提示在重新讀取失敗及舊密碼登入被拒絕後仍保留，最後用新密碼登入恢復。此中斷由測試驅動，不新增正式故障端點。非敏感短字串以真實輸入確認原生 minlength 阻擋送出；敏感 fixture 的程式注入不當作原生長度驗證證據。

2026-11-05，ARM64 Docker 的 Node `24.22.2`／npm `11.19.1` 完成鎖檔還原、strict 型別、Vite 正式建置及 Prettier check-only。官方 Playwright `1.64.1` 容器實際使用 Node `26.13.0`／npm `11.20.0` 執行測試（不作前端建置），11 個瀏覽器案例全數通過，5.7 秒。登入與已登入畫面的桌機／手機遮罩截圖已檢視，沒有水平裁切或遮擋控制項。瀏覽器容器透過保留的 `.test` DNS 別名存取同源網站；正式展示仍使用 `127.0.0.1:5080`。完整展示環境的停止／重啟保存及獨立 IAB 驗收亦已另行完成，見本文件開頭；隔離瀏覽器測試本身不證明展示保存。
