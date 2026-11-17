import type { FormEventHandler } from "react";

interface LoginFormProps {
  pending: boolean;
  onSubmit: FormEventHandler<HTMLFormElement>;
}

// 匿名使用者只看登入欄位。提交由父層處理，沿用既有身分生命週期。
export function LoginForm({ pending, onSubmit }: LoginFormProps) {
  return (
    <section aria-labelledby="login-title">
      <h2 id="login-title">登入</h2>
      <form onSubmit={onSubmit}>
        <fieldset disabled={pending}>
          <label htmlFor="userName">帳號</label>
          <input
            id="userName"
            name="userName"
            autoComplete="username"
            required
            maxLength={256}
          />
          <label htmlFor="password">密碼</label>
          <input
            id="password"
            name="password"
            type="password"
            autoComplete="current-password"
            required
            minLength={12}
            maxLength={128}
            aria-describedby="password-help"
          />
          <p id="password-help" className="hint">
            密碼須為 12–128 個字元，不會移除空白。
          </p>
          <button type="submit">{pending ? "處理中…" : "登入"}</button>
        </fieldset>
      </form>
    </section>
  );
}
