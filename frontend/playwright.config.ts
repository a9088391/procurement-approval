// 失敗產物不自動保存畫面、請求或 Cookie；只手動截取遮罩後的畫面。
import { defineConfig } from "@playwright/test";

// 固定版本 Playwright 的此開關避免失敗時自動產生含表單內容的 AI page snapshot。
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";

export default defineConfig({
  testDir: "./e2e",
  // 改密碼案例共用隔離 fixture；循序執行且不重跑，避免不明結果被再次寫入。
  workers: 1,
  retries: 0,
  timeout: 60000,
  expect: { timeout: 10000 },
  reporter: "list",

  use: {
    baseURL: process.env.BASE_URL,
    browserName: "chromium",
    trace: "off",
    video: "off",
    screenshot: "off",
    viewport: { width: 1280, height: 900 },
  },
});
