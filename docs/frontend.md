# 前端開發與身分恢復

登入、身分、改密碼、登出與組織管理都使用同一頁面。React 處理畫面與表單，`src/api.ts` 集中明確 DTO 與 HTTP 邊界；後端負責密碼、目前資格、登入期限、鎖定及撤銷的權威判斷。前端驗證僅協助填寫，不替代 API 驗證。

## 程式入口

- `frontend/index.html`／`src/main.tsx`：React 掛載、可存取表單與身分生命週期。
- `frontend/src/api.ts`：請求／回應 DTO、同源 HTTP、防偽 token 與錯誤。
- `frontend/src/Organization.tsx` 管理組織快照、提交、讀回與錯誤恢復。
- `frontend/src/OrganizationForms.tsx` 管理部門、業務帳號及密碼重設表單，使用 `api.ts` 定義的欄位。
- `frontend/src/style.css`：單欄版面、手機排版與鍵盤焦點。
- `frontend/e2e/auth.spec.ts`：真 API 成功／錯誤流程及標示 mocked 的 UI 恢復案例。
- `Dockerfile`：固定 Node 建置靜態檔，再複製至 ASP.NET Core `/app/wwwroot`；正式環境只有一個網站來源。

每次載入先取得 CSRF token，再讀取目前身分。登入後、登出後與改密碼後重新取得 token，避免沿用綁定舊身分的權杖。Cookie 由瀏覽器管理；前端不讀登入 Cookie、不把帳密或 token 存入 localStorage、sessionStorage、網址或 console。

寫入期間停用表單及其他操作，另以同步 guard 擋住重複事件。一般驗證錯誤保留原表單供修正。身分失效、權杖失效、過期版本及未知結果會卸載敏感表單；重新確認身分後重新建立空白表單。401 會安全重讀身分，讀取也失敗時保留手動恢復入口。

網路／500 失敗可能發生於交易提交後，因此不自動重送 POST，也不宣稱失敗代表沒有生效。「重新確認身分」只執行 GET，不證明先前寫入的最終結果；原本未知結果提示在後續讀取失敗或操作被拒絕時仍保留，直到成功登入或成功變更密碼。若改密碼可能已生效，使用者先嘗試新密碼。成功回應之後讀取身分失敗，會分別提示操作已成功但身分尚待確認。

管理員完成身分驗證後才會載入組織設定。業務帳號若需要先改密碼，會在既有改密碼表單看到提示。管理畫面的部門和帳號設定使用讀取時的版本，保存成功後重新查詢；預設人選及部門選單可辨識已保存但不再符合資格的設定。

組織管理與身分操作共用送出閘門。臨時密碼在提交時從表單清除。一般驗證錯誤保留其他欄位供修正。版本過期、目標失效或寫入結果未確認時，先按「重新讀取組織設定」比對最新值，重新讀取只執行 GET。若已收到成功回應但讀回失敗，畫面仍會告知保存已完成。先前未決的警示與本次操作的結果分開顯示。登入、管理資格或安全權杖失效時，管理資料會卸載，可按「重新確認身分」恢復。

## 固定工具鏈與命令

Node `24.22.2`、React／React DOM `19.3.1`、TypeScript `7.0.2`、Vite `8.3.5`、Prettier `3.9.10`、Playwright `1.64.1`；型別套件與全部相依固定於 `package.json`／`package-lock.json`。只依賴 React 與 React DOM 作為 production dependencies。不需要額外 React plugin 即可編譯此 TSX；目前未設定 Fast Refresh。

在根目錄使用 Linux 容器還原及檢查，避免將 macOS native 套件混入 Linux 建置：

```sh
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml run --rm --no-deps frontend-tools npm ci
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml run --rm --no-deps frontend-tools npm run build
docker compose -p procurement-approval-browser -f compose.yaml -f compose.browser.yaml run --rm --no-deps frontend-tools npm run format:check
```

`build` 包含 `tsc --noEmit`，啟用 strict、noUnusedLocals 及 noUnusedParameters，型別檢查也包含瀏覽器測試。格式命令採 check-only，不會改檔。

需要本機 Vite 開發時，使用符合固定版本的 Node，在 `frontend/` 執行 `npm ci`、`npm run dev`；勿與容器共用跨平台的 `node_modules`。先啟動 `5080` 後端，瀏覽器只開啟 Vite 輸出的 `127.0.0.1:5173`。Vite 將相對 `/api` 代理至 `127.0.0.1:5080`，避免在兩個來源交替操作同一個登入流程。正式驗收使用同源 Compose 網站。
