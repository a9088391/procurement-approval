// 身分生命週期由單一畫面協調；敏感表單在重新確認身分時卸載，避免跨帳號保留。
import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type FormEvent,
} from "react";
import { createRoot } from "react-dom/client";
import { ApiError, readSession, writeAuth, type IdentityDto } from "./api";
import { Organization } from "./Organization";
import { RequisitionDrafts } from "./RequisitionDrafts";
import "./style.css";

type Operation = "login" | "change-password" | "logout";

const roleNames: Record<string, string> = {
  Administrator: "管理員",
  Applicant: "申請人",
  Manager: "主管",
  Finance: "財務",
};

const unknownHint =
  "操作結果未確認，可能已生效。請重新確認身分或重新登入；若改密碼，請先嘗試新密碼。系統不會自動重送。";

// 協調登入、改密碼、登出與身分重讀，讓同源 Cookie 的狀態決定可用表單。
function App() {
  // phase 決定是否掛載表單；identity 只有在身分讀取完成後才供畫面使用。
  const [identity, setIdentity] = useState<IdentityDto | null>(null);
  const [phase, setPhase] = useState<"loading" | "ready" | "recovery">(
    "loading",
  );

  // 一般提示與未知寫入結果分開，避免後續讀取或拒絕回應覆蓋未決結果。
  const [pending, setPending] = useState(false);
  const [message, setMessage] = useState("");
  const [isError, setIsError] = useState(false);
  const [unknownResult, setUnknownResult] = useState(false);
  const [unknownOrganizationFor, setUnknownOrganizationFor] = useState<
    string | null
  >(null);
  const [unknownDraftFor, setUnknownDraftFor] = useState<string | null>(null);

  // token 只留在記憶體，不寫入網址或瀏覽器儲存空間。
  const token = useRef("");
  // ref 同步阻擋同一輪事件重複進入，pending 則負責停用畫面操作。
  const busy = useRef(false);
  const recovery = useRef<HTMLElement>(null);
  const draftLeave = useRef({ warn: false, unknown: false });
  // 受保護畫面要求確認身分時，已確認保存的事實獨立於失效指引。
  const protectedRecovery = useRef<{ confirmedWrite?: string } | null>(null);
  const actorName = identity?.userName;

  // 草稿送出請求時讀取記憶體權杖，固定回呼讓一般畫面更新不會重啟清單讀取。
  const getDraftToken = useCallback(() => token.current, []);

  // 身分與受保護畫面共用閘門，避免登入切換與舊資料讀回交錯。
  const beginOperation = useCallback(() => {
    // 同步標記所有權，讓畫面更新前抵達的第二個事件也無法提交。
    if (busy.current) return false;
    busy.current = true;
    setPending(true);
    return true;
  }, []);

  // 完成同一輪的寫入和讀回後一併解除兩層閘門，才能開始下一個操作。
  const finishOperation = useCallback(() => {
    busy.current = false;
    setPending(false);
  }, []);

  // 受保護端點拒絕身分或防偽時，先移除資料，再由使用者重新確認。
  const recoverProtectedSession = useCallback(
    (notice: string, confirmedWrite?: string) => {
      // 舊權杖及表單一起清除，保存確認與恢復指引分開供重讀使用。
      protectedRecovery.current = { confirmedWrite };
      token.current = "";
      setIdentity(null);
      setMessage(notice);
      setIsError(true);
      setPhase("recovery");
    },
    [],
  );

  // 身分失效時只保留未決操作的帳號標記；舊草稿和內容一併卸載。
  const recoverDraftSession = useCallback(
    (notice: string, unknownResult: boolean) => {
      if (unknownResult && actorName) setUnknownDraftFor(actorName);
      recoverProtectedSession(notice);
    },
    [actorName, recoverProtectedSession],
  );

  // 編輯器卸載時也會清除提示條件，避免下一個帳號沿用保護狀態。
  const setDraftLeaveWarning = useCallback((warn: boolean, unknown = false) => {
    draftLeave.current = { warn, unknown };
  }, []);

  // 只有會主動關閉編輯器的身分操作需要詢問，取消時保留目前草稿。
  function canLeaveDraft() {
    return (
      !draftLeave.current.warn ||
      window.confirm(
        "繼續會關閉草稿編輯畫面，尚未保存的修改會遺失。確定要繼續嗎？",
      )
    );
  }

  // 身分流程確定要卸載編輯器時，只留下同帳號可見的未知結果標記。
  function rememberUnknownDraft() {
    if (draftLeave.current.unknown && actorName) setUnknownDraftFor(actorName);
  }

  // 舊表單被移除時，把鍵盤焦點帶到恢復區，下一次 Tab 可到重讀按鈕。
  useEffect(() => {
    if (phase === "recovery") recovery.current?.focus();
  }, [phase]);

  // 掛載時向後端查證目前身分，以正確處理重新載入頁面的登入狀態。
  useEffect(() => {
    // 初始讀取共用取消期限；卸載後即使回應較晚抵達，也不更新狀態。
    const controller = new AbortController();
    let active = true;
    const timeout = setTimeout(
      // 逾時時取消整組讀取，避免頁面一直停在確認身分的畫面。
      () => controller.abort(),
      15000,
    );

    // 以同一取消訊號讀取權杖和身分，讓這輪查詢有共同期限。
    readSession(controller.signal)
      // 成功時一起採用權杖與身分，讓就緒的表單使用同一次讀取結果。
      .then((session) => {
        // 卸載後忽略回應，因為此輪 effect 已失去更新畫面的資格。
        if (!active) return;
        token.current = session.token;
        setIdentity(session.identity);
        setPhase("ready");
      })
      // 讀取失敗時開放手動恢復，因為此時尚未確認是否已登入。
      .catch(() => {
        // 卸載觸發的取消也可能進入此處，僅由仍有效的 effect 顯示提示。
        if (!active) return;
        setMessage("無法確認登入狀態，請檢查連線後重新確認身分。");
        setIsError(true);
        setPhase("recovery");
      })
      // 無論讀取結果都解除逾時排程，避免已完成的請求留下計時器。
      .finally(() => clearTimeout(timeout));

    // 清理此輪讀取，避免逾時或延遲回應在卸載後影響畫面。
    return () => {
      // 先標記失效，再清除排程與取消請求，讓延遲回呼略過狀態更新。
      active = false;
      clearTimeout(timeout);
      controller.abort();
    };
  }, []);

  // 手動恢復只讀取 token 與身分，不重送 POST，也不推定先前寫入的成敗。
  async function refresh() {
    // 取得離開確認並共用同步閘門，重讀期間先隱藏原身分和敏感表單。
    if (busy.current || !canLeaveDraft()) return;
    if (!beginOperation()) return;
    rememberUnknownDraft();
    setPhase("loading");
    setIdentity(null);

    // 讀取成功後才顯示對應表單；先前不確定的寫入提示仍由原狀態保留。
    try {
      const session = await readSession();
      // 身分恢復已完成，撤下舊指引但保留已收到的保存確認。
      if (protectedRecovery.current) {
        const { confirmedWrite } = protectedRecovery.current;
        protectedRecovery.current = null;
        setMessage(confirmedWrite ? `${confirmedWrite}。` : "");
        setIsError(false);
      }

      // 手動讀回若確認已換人，清除前一個帳號的未決提示。
      if (session.identity) {
        setUnknownOrganizationFor(
          // 相同帳號仍需要核對先前未確認的操作。
          (current) =>
            current === session.identity?.userName ? current : null,
        );
        setUnknownDraftFor(
          // 草稿未決提示也只供發出原操作的業務帳號核對。
          (current) =>
            current === session.identity?.userName ? current : null,
        );
      }
      token.current = session.token;
      setIdentity(session.identity);
      setPhase("ready");
    } catch {
      // 查證失敗時保留手動入口，讓使用者可在連線恢復後再試。
      setMessage(
        // 優先保留原操作訊息，沒有訊息時才補上通用讀取錯誤。
        (current) => current || "無法確認登入狀態，請檢查連線後重新確認身分。",
      );
      setIsError(true);
      setPhase("recovery");
    } finally {
      // 一併解除事件與畫面層的閘門，讓使用者能依當前狀態操作。
      finishOperation();
    }
  }

  // 集中提交驗證操作，讓寫入回應與接續的身分讀取分別決定畫面及提示。
  async function submit(
    operation: Operation,
    event?: FormEvent<HTMLFormElement>,
  ) {
    // 接管提交並保護草稿編輯；共用閘門讓每次操作只送出一次。
    event?.preventDefault();
    if (busy.current || !canLeaveDraft()) return;
    if (!beginOperation()) return;

    // 在第一個 await 前擷取本次輸入；後續卸載表單不影響已送出的內容。
    const values = event ? new FormData(event.currentTarget) : null;
    if (identity?.mustChangePassword && event) {
      // 臨時憑證流程提交後清空兩個密碼欄位，若遭拒絕需重新輸入。
      for (const input of event.currentTarget.querySelectorAll<HTMLInputElement>(
        'input[type="password"]',
      )) {
        input.value = "";
      }
    }

    // 清空上次的一般提示；未決寫入有獨立狀態。
    setMessage("");
    setIsError(false);

    // 寫入成功與接著讀取身分的成功分開判斷，避免把讀取失敗誤報成寫入失敗。
    let accepted = false;

    try {
      // 依操作組裝 API 所需資料；改密碼一併提交目前身分的版本。
      await writeAuth(
        operation,
        token.current,
        operation === "login"
          ? {
              userName: String(values?.get("userName")),
              password: String(values?.get("password")),
            }
          : operation === "change-password"
            ? {
                currentPassword: String(values?.get("currentPassword")),
                newPassword: String(values?.get("newPassword")),
                version: identity!.version,
              }
            : undefined,
      );

      // 記下寫入已成功，讓後續讀取失敗仍可顯示正確的操作結果。
      accepted = true;

      // 成功登入或改密碼解除身分操作的未知提示；登出清除原帳號的未決提示。
      if (operation !== "logout") {
        setUnknownResult(false);
        rememberUnknownDraft();
      } else {
        setUnknownOrganizationFor(null);
        setUnknownDraftFor(null);
      }

      // 先卸載舊表單並記下此操作的成功提示，再查證切換後的身分。
      setIdentity(null);
      setPhase("loading");
      setMessage(
        operation === "change-password"
          ? "密碼已變更，所有舊登入已失效，請使用新密碼登入。"
          : operation === "logout"
            ? "已登出。"
            : "登入成功。",
      );

      // 重新取得權杖與身分，讓下一個表單對應寫入後的 Cookie 狀態。
      const session = await readSession();
      // 確認換人後清除前一個帳號的未決提示，避免跨身分出現。
      if (session.identity) {
        setUnknownOrganizationFor(
          // 相同帳號重新登入仍可核對原操作；不同帳號不沿用提示。
          (current) =>
            current === session.identity?.userName ? current : null,
        );
        setUnknownDraftFor(
          // 相同業務帳號重新登入可核對先前操作，其他帳號不沿用提示。
          (current) =>
            current === session.identity?.userName ? current : null,
        );
      }
      token.current = session.token;
      setIdentity(session.identity);
      setPhase("ready");
    } catch (error) {
      // 依已確認的寫入狀態選擇提示與恢復入口。
      setIsError(true);
      if (accepted) {
        // 已收到成功回應；只有後續身分讀取失敗，開放重新確認。
        setMessage(
          "操作已成功，但目前無法確認登入狀態。請重新確認身分；若已改密碼，請使用新密碼登入。",
        );
        setPhase("recovery");
      } else if (!(error instanceof ApiError) || error.status >= 500) {
        // 未能確認寫入結果時先卸載表單，保留警示並等待使用者重讀。
        rememberUnknownDraft();
        setIdentity(null);
        setPhase("recovery");
        setUnknownResult(true);
      } else {
        // 一般驗證拒絕保留輸入；身分、防偽或版本失效則要求重新確認。
        setMessage(error.message);
        if (
          ["unauthenticated", "invalid_csrf", "stale_version"].includes(
            error.code,
          )
        ) {
          // 舊身分、權杖或版本已無法使用，清除表單並提示恢復後再操作。
          rememberUnknownDraft();
          setIdentity(null);
          setPhase("recovery");
          setMessage(`${error.message} 請重新確認身分後，由您再次操作。`);
          if (error.code === "unauthenticated") {
            // 登入失效時立即試讀目前身分，讓已確認的匿名狀態回到登入表單。
            try {
              const session = await readSession();
              token.current = session.token;
              setIdentity(session.identity);
              setPhase("ready");
            } catch {
              // 讀取也失敗時保留手動恢復入口，不猜測登入狀態。
            }
          }
        }
      }
    } finally {
      // 完成整輪寫入與可能的重讀後才解除閘門，避免下一次操作使用中途狀態。
      finishOperation();
    }
  }

  // 僅 ready 掛載身分對應的表單；離開 ready 即清除 DOM 中的敏感輸入。
  return (
    <main>
      <header>
        <p className="eyebrow">本機展示</p>
        <h1>請購簽核</h1>
        <p>登入、請購草稿與組織管理</p>
      </header>
      {message || unknownResult ? (
        // 合併一般與未知結果提示；未知結果即使沒有新錯誤仍須以警示呈現。
        <p
          className={isError || unknownResult ? "feedback error" : "feedback"}
          role={isError || unknownResult ? "alert" : "status"}
        >
          {unknownResult ? unknownHint : ""} {message}
        </p>
      ) : null}
      {phase === "loading" ? (
        // 讀取期間用狀態訊息回饋，避免尚未確認身分就顯示表單。
        <p role="status">正在確認身分…</p>
      ) : null}
      {phase === "recovery" ? (
        // 尚未確認身分時說明手動恢復的用途，讓使用者知道這一步只讀取。
        <section aria-labelledby="recovery-title" ref={recovery} tabIndex={-1}>
          <h2 id="recovery-title">請先確認登入狀態</h2>
          <p>重新取得安全權杖與目前身分，不會重送先前的操作。</p>
        </section>
      ) : null}
      {phase === "ready" && !identity ? (
        // 身分已確認為匿名時顯示登入表單，由共用提交函式接管原生提交。
        <section aria-labelledby="login-title">
          <h2 id="login-title">登入</h2>
          <form
            onSubmit={
              // 交由共用提交函式讀取欄位，讓登入也受相同的重複送出保護。
              (event) => void submit("login", event)
            }
          >
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
      ) : null}
      {phase === "ready" && identity ? (
        // 已確認登入時才顯示身分與敏感操作，讓表單隨身分階段卸載。
        <>
          <section aria-labelledby="identity-title">
            <h2 id="identity-title">目前身分</h2>
            <dl>
              <dt>帳號</dt>
              <dd>{identity.userName}</dd>
              <dt>帳號類別</dt>
              <dd>
                {identity.accountType === "admin" ? "管理帳號" : "業務帳號"}
              </dd>
              <dt>角色</dt>
              <dd>
                {
                  // 逐一轉換已知角色，保留未知代碼供接手者辨識，再串成畫面文字。
                  identity.roles
                    .map((role) => roleNames[role] ?? role)
                    .join("、")
                }
              </dd>
            </dl>
            <button
              type="button"
              disabled={pending}
              onClick={
                // 登出沒有表單輸入，直接交由共用寫入與重讀流程。
                () => void submit("logout")
              }
            >
              登出
            </button>
          </section>
          <section aria-labelledby="change-title">
            <h2 id="change-title">變更密碼</h2>
            {identity.mustChangePassword ? (
              <p className="feedback" role="status">
                請先變更臨時密碼。新密碼須與臨時密碼不同，變更後請用新密碼重新登入。
              </p>
            ) : null}
            <p className="hint">變更後，包含本次在內的所有登入都會失效。</p>
            <form
              onSubmit={
                // 將表單事件交給共用流程，讓它在等待回應前取得輸入快照。
                (event) => void submit("change-password", event)
              }
            >
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
                <button type="submit">
                  {pending ? "處理中…" : "變更密碼"}
                </button>
              </fieldset>
            </form>
          </section>
          {identity.accountType === "admin" &&
          identity.roles.includes("Administrator") &&
          identity.mustChangePassword === false ? (
            // 確認具管理資格且已解除改密碼限制後，才載入組織資料。
            <Organization
              key={identity.userName}
              getToken={
                // 實際送出管理寫入時才讀取目前身分的記憶體權杖。
                () => token.current
              }
              pending={pending}
              beginOperation={beginOperation}
              finishOperation={finishOperation}
              onSessionInvalid={recoverProtectedSession}
              unknownResult={unknownOrganizationFor === identity.userName}
              onUnknownResult={
                // 未決結果只對發出該操作的管理帳號顯示。
                () => setUnknownOrganizationFor(identity.userName)
              }
            />
          ) : null}
          {identity.accountType === "business" &&
          !identity.mustChangePassword ? (
            // 主管及財務也是業務帳號；此處只掛載本人草稿，不按附加角色分流。
            <RequisitionDrafts
              key={identity.userName}
              getToken={getDraftToken}
              pending={pending}
              beginOperation={beginOperation}
              finishOperation={finishOperation}
              onSessionInvalid={recoverDraftSession}
              onLeaveWarningChange={setDraftLeaveWarning}
              priorUnknown={unknownDraftFor === identity.userName}
              onUnknownReviewed={
                // 使用者已核對，目前帳號不再需要保留提示。
                () => setUnknownDraftFor(null)
              }
            />
          ) : null}
        </>
      ) : null}
      <button
        className="secondary"
        type="button"
        disabled={pending || phase === "loading"}
        onClick={
          // 此按鈕只重讀身分，保留任何尚未確認的寫入提示。
          () => void refresh()
        }
      >
        重新確認身分
      </button>
    </main>
  );
}

// 掛載單一身分畫面，HTML 入口提供固定的根節點。
createRoot(document.getElementById("root")!).render(<App />);
