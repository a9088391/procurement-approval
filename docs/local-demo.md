# 本機展示與保存

前提是 macOS ARM64、Docker 與 Docker Compose 可用，且 `127.0.0.1:5080` 未被其他服務占用。所有命令在專案根目錄執行。首次需連網下載固定版本映像與套件。

```sh
docker compose -p procurement-approval up --build -d
```

入口是 `http://127.0.0.1:5080`，API 與 React 前端同源。資料庫留在 Compose 內部網路。此設定只供本機 HTTP 展示。管理帳號可維護部門及業務帳號。業務帳號可管理本人草稿，並依當前資格查看、簽核請購或使用請購統計與 CSV。完整操作見 [README](../README.md)。

初始化程序建立 `.local/`，目錄權限 `0700`，隨機資料庫密碼及 `initial-accounts.json` 權限 `0600`。首次初始化後，在本機編輯器開啟 `.local/initial-accounts.json`，取得 `admin`、`applicant`、`manager`、`finance` 的隨機初始密碼。若曾復原管理員或重設展示，請使用該次命令顯示的憑證檔。請在本機保管密碼。`admin` 是管理帳號，其餘是業務帳號。預載業務帳號已具備示範角色。首次送審前，由管理員為示範申請人設定部門及兩位預設審核人。

初始密碼檔不是密碼查詢功能；改密碼後該檔仍保留原值，不能用它查回新密碼。後端只保存 Identity 密碼雜湊。初始化先安全寫出完整檔案，再提交帳號；輸出失敗不會留下部分帳號。重試保留既有檔案與帳號，僅補缺少的示範帳號，不覆寫既有密碼、資格或資料。

也可明確執行本機維護命令：

```sh
docker compose -p procurement-approval run --rm app create-admin
docker compose -p procurement-approval run --rm app initialize-demo
```

`create-admin` 補建缺少的管理帳號，`initialize-demo` 補建缺少的示範帳號。兩者均透過受保護的檔案交付密碼。

## 管理員密碼復原

先依上方命令建置並啟動目前版本。忘記 `admin` 密碼或需要解除其鎖定時，在專案根目錄執行：

```sh
docker compose -p procurement-approval run --rm --no-deps app recover-admin
```

完成後，在本機編輯器開啟命令顯示的 `.local/admin-recovery-<識別碼>.json`，用其中的新密碼登入 `admin`。舊管理員密碼與所有舊管理員登入立即失效。業務帳號、其有效登入、組織設定、請購、各輪快照與歷程均保留。先前的初始帳號檔仍是當時的憑證紀錄。

## 清空展示資料並重新開始

先用本機 Docker context 建置並啟動目前版本，保持目標專案的 `db` 運行。在專案根目錄執行：

```sh
sh reset-demo.sh procurement-approval
```

腳本先核對實際 Compose 容器、預設資料卷及目錄與網站映像。通過後顯示 `procurement-approval/db/procurement`、要清除的帳號與登入、組織設定、請購、所有輪次快照及歷程，以及未備份的資料無法復原。確認目標與影響後，才輸入 `reset procurement-approval/db/procurement` 並按 Enter。要取消，直接按 Enter 或結束命令。使用另一個 Compose 專案名稱時，啟動、維護與確認都要使用該專案顯示的名稱。預覽後若有人寫入資料或更新登入紀錄，命令會取消，請核對資料後重新預覽。

重設完成後，在本機編輯器開啟命令顯示的 `.local/reset-accounts-<識別碼>.json`，取得四個新建示範帳號的隨機密碼。先前的密碼和登入均失效。要再次送審，請先用新 `admin` 建立部門並指定預設審核人。

命令用檔名指示憑證位置。復原與重設各次都使用獨立的 `0600` 憑證檔。若命令中斷或回報無法確認提交，保留它顯示的候選憑證檔，先用候選新密碼登入並核對資料結果，再決定是否重試。遇到鎖定等待逾時，可在保留 `db` 運行的情況下停止本專案的 `app`，重試前先核對目前資料，完成後再啟動網站。

## 停止與重啟

停止及重啟：

```sh
docker compose -p procurement-approval stop
docker compose -p procurement-approval up -d
```

帳號、密碼雜湊、登入紀錄、部門、預設審核人、草稿、簽核輪次、有效指派及歷程保存在專案的 `database` named volume，Cookie 防護金鑰保存在 `.local/keys/`。保留兩者，重啟後可繼續使用原設定與有效登入，並重新開啟已保存的草稿、撤回後保存的修改、各輪快照、決定及已提交的改派。`.local/` 含本機敏感資料，已排除於 Git 和 Docker build context。金鑰存取由目錄權限限制。

`docker compose down` 會移除本專案容器與網路，預設保留資料 volume。刪除 volume 會失去帳號與資料，刪除 `.local/keys/` 會使現有 Cookie 無法解密。已清除的展示資料須從事先保留的備份還原，專案目前沒有內建資料庫備份工具。

不要輸出 `docker compose config` 的敏感值、應用 Cookie 或請求本文。一般應用錯誤只記追蹤識別碼，不印例外、SQL 參數、密碼與連線憑證；需要排查時先以測試環境重現。

## 已驗證環境與啟動診斷

2026-11-05 在 macOS ARM64、Docker CLI 28.0.1、Compose 5.5.0、Linux ARM64 daemon 29.8.2（8 CPU、約 12.6 GB 記憶體）實跑上述單一啟動命令，網站與 API 可操作。容器實際發布為 `127.0.0.1:5080`，資料庫 `5432/tcp` 無主機綁定。

實際透過 HTTP 變更測試管理員密碼後，執行上述 stop／up：原登入及目前身分仍有效、變更後密碼可重新登入、舊密碼被拒絕，初始帳號檔與 Data Protection 金鑰未變。驗證結束已把本次測試管理員改回原隨機示範密碼，並確認登出後舊 Cookie 不可重播。沒有重設資料庫。

2026-11-06 在相同架構的獨立 Compose 資料卷驗證原地升級、停啟保存、復原、取消、確認重設及重設後停啟。最終複驗的網站綁定 `127.0.0.1:5190` 和 `127.0.0.1:5192`，資料庫留在專用容器。透過 HTTP 及 Chromium 驗證舊登入失效、新憑證可用，並核對非目標資料庫的哨兵。詳見[測試與驗證](testing.md#管理員復原與展示重設)。

固定 runtime 映像未包含可選 Kerberos 函式庫，啟動可能出現 `Cannot load library libgssapi_krb5.so.2`。Npgsql 10 預設探測 GSS 連線加密；缺少函式庫時會處理並回退，此訊息本身不表示密碼認證或啟動失敗。[Npgsql 官方說明](https://www.npgsql.org/doc/security.html#gss-session-encryption-gss-api)。本版保留原連線設定，未為消除訊息而停用安全選項或增裝系統套件；不宣稱資料庫連線已加密。

第一次新建 schema 時，EF 也可能先記錄 `__EFMigrationsHistory` 查詢失敗，隨後成功套用初始 migration。應以 migration 完成、HTTP 結果及服務狀態共同判斷；若未完成或網站不可用，仍須排查，不能把其他錯誤一律當作無害。
