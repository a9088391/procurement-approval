// 開發時也透過同源相對路徑呼叫 API；正式環境由 ASP.NET Core 提供靜態檔。
import { defineConfig } from "vite";

export default defineConfig({
  server: { proxy: { "/api": "http://127.0.0.1:5080" } },
});
