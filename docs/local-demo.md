# 本機展示與保存

前提是 macOS ARM64、Docker 與 Docker Compose 可用，且 `127.0.0.1:5080` 未被其他服務占用。所有命令在專案根目錄執行。首次需連網下載固定版本映像與套件。

```sh
docker compose -p procurement-approval up --build -d
```

入口是 `http://127.0.0.1:5080`，API 與 React 前端同源。資料庫留在 Compose 內部網路。此設定只供本機 HTTP 展示。管理帳號可維護部門及業務帳號。業務帳號可管理本人草稿，並依當前資格查看或簽核請購。完整操作見 [README](../README.md)。

初始化程序建立 `.local/`，目錄權限 `0700`，隨機資料庫密碼及 `initial-accounts.json` 權限 `0600`。在本機編輯器開啟 `.local/initial-accounts.json`，取得 `admin`、`applicant`、`manager`、`finance` 的隨機初始密碼。不要貼到一般紀錄、截圖、聊天或公開文件。`admin` 是管理帳號，其餘是業務帳號；預載業務帳號已具備示範角色。首次送審前，由管理員為示範申請人設定部門及兩位預設審核人。

初始密碼檔不是密碼查詢功能；改密碼後該檔仍保留原值，不能用它查回新密碼。後端只保存 Identity 密碼雜湊。初始化先安全寫出完整檔案，再提交帳號；輸出失敗不會留下部分帳號。重試保留既有檔案與帳號，僅補缺少的示範帳號，不覆寫既有密碼、資格或資料。

也可明確執行本機維護命令：

```sh
docker compose -p procurement-approval run --rm app create-admin
docker compose -p procurement-approval run --rm app initialize-demo
```

這些命令不接受密碼參數、不印密碼，不透過 HTTP 開放；第一個只補管理帳號，第二個補最小示範帳號。管理員復原與展示重設尚未提供，不要以刪除資料庫或憑證檔代替復原。

停止及重啟：

```sh
docker compose -p procurement-approval stop
docker compose -p procurement-approval up -d
```

帳號、密碼雜湊、登入紀錄、部門、預設審核人、草稿、簽核輪次、有效指派及歷程保存在專案的 `database` named volume，Cookie 防護金鑰保存在 `.local/keys/`。保留兩者，重啟後可繼續使用原設定與有效登入，並重新開啟已保存的草稿、撤回後保存的修改、各輪快照、決定及已提交的改派。`.local/` 含本機敏感資料，已排除於 Git 和 Docker build context。金鑰存取由目錄權限限制。

`docker compose down` 會移除本專案容器與網路，預設保留資料 volume；不要加 `--volumes`。刪除 volume 會失去帳號與資料，刪除 `.local/keys/` 會使現有 Cookie 無法解密。這些刪除不是復原方式；目前沒有備份／復原工具，資料遺失無法靠退回程式修復。

不要輸出 `docker compose config` 的敏感值、應用 Cookie 或請求本文。一般應用錯誤只記追蹤識別碼，不印例外、SQL 參數、密碼與連線憑證；需要排查時先以測試環境重現。

## 已驗證環境與啟動診斷

2026-11-05 在 macOS ARM64、Docker CLI 28.0.1、Compose 5.5.0、Linux ARM64 daemon 29.8.2（8 CPU、約 12.6 GB 記憶體）實跑上述單一啟動命令，網站與 API 可操作。容器實際發布為 `127.0.0.1:5080`，資料庫 `5432/tcp` 無主機綁定。

實際透過 HTTP 變更測試管理員密碼後，執行上述 stop／up：原登入及目前身分仍有效、變更後密碼可重新登入、舊密碼被拒絕，初始帳號檔與 Data Protection 金鑰未變。驗證結束已把本次測試管理員改回原隨機示範密碼，並確認登出後舊 Cookie 不可重播。沒有重設資料庫。

固定 runtime 映像未包含可選 Kerberos 函式庫，啟動可能出現 `Cannot load library libgssapi_krb5.so.2`。Npgsql 10 預設探測 GSS 連線加密；缺少函式庫時會處理並回退，此訊息本身不表示密碼認證或啟動失敗。[Npgsql 官方說明](https://www.npgsql.org/doc/security.html#gss-session-encryption-gss-api)。本版保留原連線設定，未為消除訊息而停用安全選項或增裝系統套件；不宣稱資料庫連線已加密。

第一次新建 schema 時，EF 也可能先記錄 `__EFMigrationsHistory` 查詢失敗，隨後成功套用初始 migration。應以 migration 完成、HTTP 結果及服務狀態共同判斷；若未完成或網站不可用，仍須排查，不能把其他錯誤一律當作無害。
