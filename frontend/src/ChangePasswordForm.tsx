import type { FormEventHandler } from "react";

interface ChangePasswordFormProps {
  mustChangePassword: boolean;
  passwordOpen: boolean;
  pending: boolean;
  onSubmit: FormEventHandler<HTMLFormElement>;
}

// 保留常駐的改密碼面板外框，僅在收合時卸載未送出的密碼欄位。
export function ChangePasswordForm({
  mustChangePassword,
  passwordOpen,
  pending,
  onSubmit,
}: ChangePasswordFormProps) {
  return (
    <section
      id="password-panel"
      className="password-panel"
      aria-labelledby="change-title"
      hidden={!mustChangePassword && !passwordOpen}
    >
      <h2 id="change-title">變更密碼</h2>
      {mustChangePassword ? (
        <p className="feedback" role="status">
          請先變更臨時密碼。新密碼須與臨時密碼不同，變更後請用新密碼重新登入。
        </p>
      ) : null}
      <p className="hint">變更後，包含本次在內的所有登入都會失效。</p>
      {mustChangePassword || passwordOpen ? (
        // 收合時卸載輸入，避免未送出的密碼繼續留在畫面。
        <form onSubmit={onSubmit}>
          <fieldset disabled={pending}>
            <label htmlFor="currentPassword">目前密碼</label>
            <input
              id="currentPassword"
              name="currentPassword"
              type="password"
              autoComplete="current-password"
              required
              minLength={12}
              maxLength={128}
            />
            <label htmlFor="newPassword">新密碼</label>
            <input
              id="newPassword"
              name="newPassword"
              type="password"
              autoComplete="new-password"
              required
              minLength={12}
              maxLength={128}
              aria-describedby="new-password-help"
            />
            <p id="new-password-help" className="hint">
              12–128 個字元；請自行安全保存新密碼。
            </p>
            <button type="submit">{pending ? "處理中…" : "變更密碼"}</button>
          </fieldset>
        </form>
      ) : null}
    </section>
  );
}
