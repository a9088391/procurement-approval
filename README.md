# 請購簽核作品

以 ASP.NET Core 10、Identity、EF Core 10、PostgreSQL 18 與 React／TypeScript／Vite 建立的同源單體。目前提供本機帳密登入、身分查看、變更密碼與登出；管理帳號與業務帳號分離。組織管理、請購簽核、報表、管理員復原及展示重設尚未提供。

## 啟動與操作

需要 Docker／Docker Compose、macOS ARM64，以及可用的 `127.0.0.1:5080`。在本目錄執行：

```sh
docker compose -p procurement-approval up --build -d
```

開啟 `http://127.0.0.1:5080`。使用本機編輯器查看 `.local/initial-accounts.json` 取得隨機初始密碼；不要將檔案內容貼到聊天、截圖或公開紀錄。帳號為 `admin`（管理帳號）、`applicant`（申請人）、`manager`（主管）、`finance`（財務）。

登入後可查看帳號類別與角色。變更密碼須輸入目前密碼及 12–128 個 UTF-16 字元的新密碼；成功後全部舊登入失效，回登入頁使用新密碼。初始密碼檔不會隨變更更新。登出只撤銷本次登入；重新載入仍維持登出。

若顯示「操作結果未確認」，請先按「重新確認身分」，必要時重新登入；改密碼請先嘗試新密碼。系統不會自動重送變更。安全權杖失效時也須重新確認身分，再由使用者操作。

## 維護入口

- [本機展示與保存](docs/local-demo.md)：前提、初始帳號、停止／重啟與資料保存。
- [前端開發](docs/frontend.md)：程式入口、DTO、狀態恢復與固定工具鏈。
- [後端與 API](docs/backend.md)：身分規則、錯誤契約、交易及資料存取。
- [測試](docs/testing.md)：隔離 HTTP／PostgreSQL 與瀏覽器驗證命令、覆蓋及限制。

展示僅限本機 HTTP，無 TLS，不可直接公開到外網。資料庫不發布主機連接埠；保留資料 volume 與 `.local/keys/` 才能保留資料及原登入期限。未量測負載容量，其他作業系統與 CPU 平台尚未實測。
