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
import { ChangePasswordForm } from "./ChangePasswordForm";
import { LoginForm } from "./LoginForm";
import { Organization } from "./Organization";
import { Reassignments, type ReassignmentReadback } from "./Reassignments";
import {
  RequisitionApprovals,
  type ApprovalReadback,
} from "./RequisitionApprovals";
import { RequisitionDrafts } from "./RequisitionDrafts";
import { RequisitionReports } from "./RequisitionReports";
import "./style.css";

type Operation = "login" | "change-password" | "logout";
type BusinessPanel = "drafts" | "approvals" | "reports";

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
  // 選用的改密碼表單預設收合，強制改密碼由身分資料決定。
  const [passwordOpen, setPasswordOpen] = useState(false);
  const [unknownOrganizationFor, setUnknownOrganizationFor] = useState<
    string | null
  >(null);
  // 組織操作收到 204 後，只保留該帳號的保存確認，設定和表單仍隨面板卸載。
  const [organizationConfirmation, setOrganizationConfirmation] = useState<{
    actor: string;
    label: string;
  } | null>(null);
  const [unknownDraftFor, setUnknownDraftFor] = useState<string | null>(null);
  // 只跨畫面保留待核對的識別、版本及結果；請購資料本身隨受保護面板卸載。
  const [approvalContext, setApprovalContext] = useState<{
    actor: string;
    panel: BusinessPanel;
    readbacks: Record<string, ApprovalReadback>;
  } | null>(null);
  // 改派讀回只跨同一管理帳號保留，畫面失效時仍卸載原待辦資料及表單。
  const [reassignmentContext, setReassignmentContext] = useState<{
    actor: string;
    active: boolean;
    readback: ReassignmentReadback | null;
  } | null>(null);

  // token 只留在記憶體，不寫入網址或瀏覽器儲存空間。
  const token = useRef("");
  // ref 同步阻擋同一輪事件重複進入，pending 則負責停用畫面操作。
  const busy = useRef(false);
  const recovery = useRef<HTMLElement>(null);
  const draftLeave = useRef({ warn: false, unknown: false });
  // 記錄身分重驗是否來自受保護端點拒絕，以便清除過期指引。
  const protectedRecovery = useRef(false);
  const actorName = identity?.userName;
  // 面板選擇與待核對的簽核結果只屬於目前帳號。
  const currentApproval =
    approvalContext?.actor === actorName ? approvalContext : null;
  const businessPanel = currentApproval?.panel ?? "drafts";

  // 受保護請求使用目前身分的記憶體權杖，固定回呼避免重新啟動清單讀取。
  const getProtectedToken = useCallback(() => token.current, []);

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

  // 離開已確認身分時一併重設改密碼表單的展開狀態，避免重讀後沿用。
  const clearConfirmedIdentity = useCallback(() => {
    setIdentity(null);
    setPasswordOpen(false);
  }, []);

  // 受保護端點拒絕身分或防偽時，先移除資料，再由使用者重新確認。
  const recoverProtectedSession = useCallback(
    (notice: string) => {
      // 清除舊權杖及表單；確認訊息仍由發出原操作的帳號持有。
      protectedRecovery.current = true;
      token.current = "";
      clearConfirmedIdentity();
      setMessage(notice);
      setIsError(true);
      setPhase("recovery");
    },
    [clearConfirmedIdentity],
  );

  // 保存或清除目前管理帳號的確認；固定回呼讓面板不因標記改變而重新查詢。
  const markOrganizationWrite = useCallback(
    (label: string | null) => {
      if (!actorName) return;
      setOrganizationConfirmation(
        // 只為目前帳號新增確認；清除也只適用於該帳號發出的操作。
        (current) => {
          if (label !== null) return { actor: actorName, label };
          return current?.actor === actorName ? null : current;
        },
      );
    },
    [actorName],
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

  // 重驗時確認換人才清除舊標記；暫時失效可由原帳號讀回，明確登出另在成功分支清除管理標記。
  function keepPanelContextsFor(confirmed: IdentityDto | null) {
    if (!confirmed) return;
    setApprovalContext(
      // 已確認換人時移除舊帳號的面板選擇和全部待核對標記。
      (current) => (current?.actor === confirmed.userName ? current : null),
    );
    setReassignmentContext(
      // 改派也只供發出原操作的管理帳號核對。
      (current) => (current?.actor === confirmed.userName ? current : null),
    );
    setOrganizationConfirmation(
      // 已確認換人時不沿用原組織操作的保存確認。
      (current) => (current?.actor === confirmed.userName ? current : null),
    );
  }

  // 管理面板在同一帳號間切換時保留待核對結果，進行中的讀寫禁止切換。
  function switchAdminPanel(active: boolean) {
    if (busy.current || !actorName) return;
    setReassignmentContext(
      // 面板選擇可切換，待核對結果仍屬於原帳號。
      (current) => ({
        actor: actorName,
        active,
        readback: current?.actor === actorName ? current.readback : null,
      }),
    );
  }

  // 只允許原管理帳號的操作保留或清除其改派讀回入口。
  function markReassignmentWrite(readback: ReassignmentReadback | null) {
    setReassignmentContext(
      // 目前帳號的面板選擇獨立於結果，讀回後不切回組織管理。
      (current) =>
        current && current.actor === actorName
          ? { ...current, readback }
          : current,
    );
  }

  // 切換請購面板前尊重草稿的離開確認，保留同一帳號的未決寫入。
  function switchBusinessPanel(panel: BusinessPanel) {
    // 共用閘門仍由目前的讀寫持有時，不能卸載操作中的面板。
    if (busy.current || businessPanel === panel) return;
    // 使用者可取消關閉有未保存修改的草稿，並留下未決的寫入提示供讀回。
    if (!actorName || !canLeaveDraft()) return;
    rememberUnknownDraft();
    setApprovalContext(
      // 只有相同帳號的未決操作能跨面板保留。
      (current) => ({
        actor: actorName,
        panel,
        readbacks: current?.actor === actorName ? current.readbacks : {},
      }),
    );
  }

  // 待核對的操作按請購識別保存，避免另一張單的成功回應清除原先的標記。
  function markApprovalWrite(id: string, readback: ApprovalReadback | null) {
    setApprovalContext((current) => {
      // 只更新目前畫面操作者的標記；新帳號即使能讀同一張單也不沿用。
      if (!current || current.actor !== actorName) return current;
      if (!readback && !current.readbacks[id]) return current;
      const readbacks = { ...current.readbacks };
      if (readback) readbacks[id] = readback;
      else delete readbacks[id];
      return { ...current, readbacks };
    });
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
    clearConfirmedIdentity();

    // 讀取成功後才顯示對應表單；先前不確定的寫入提示仍由原狀態保留。
    try {
      const session = await readSession();
      keepPanelContextsFor(session.identity);
      // 身分恢復已完成，撤下舊指引；已確認保存由同帳號面板呈現。
      if (protectedRecovery.current) {
        protectedRecovery.current = false;
        setMessage("");
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

      // 成功登入或改密碼解除身分操作的未知提示；明確登出清除原管理帳號的讀回與未決提示。
      if (operation !== "logout") {
        setUnknownResult(false);
        rememberUnknownDraft();
      } else {
        setUnknownOrganizationFor(null);
        setOrganizationConfirmation(null);
        setUnknownDraftFor(null);
        setReassignmentContext(null);
      }

      // 先卸載舊表單並記下此操作的成功提示，再查證切換後的身分。
      clearConfirmedIdentity();
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
      keepPanelContextsFor(session.identity);
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
        clearConfirmedIdentity();
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
          clearConfirmedIdentity();
          setPhase("recovery");
          setMessage(`${error.message} 請重新確認身分後，由您再次操作。`);
          if (error.code === "unauthenticated") {
            // 登入失效時立即試讀目前身分，讓已確認的匿名狀態回到登入表單。
            try {
              const session = await readSession();
              keepPanelContextsFor(session.identity);
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

  // 只允許目前管理帳號採用其面板選擇及待核對結果。
  const currentReassignment =
    reassignmentContext?.actor === actorName ? reassignmentContext : null;
  const reassignmentActive = currentReassignment?.active === true;

  // 未登入與身分待確認共用同一個手動讀回入口。
  const refreshControl = (
    <button
      className="text-control identity-refresh"
      type="button"
      disabled={pending || phase === "loading"}
      onClick={
        // 此按鈕只重讀身分，保留任何尚未確認的寫入提示。
        () => void refresh()
      }
    >
      重新確認身分
    </button>
  );

  // 僅 ready 掛載身分對應的表單；離開 ready 即清除 DOM 中的敏感輸入。
  return (
    <>
      <header className={identity ? "masthead" : "masthead masthead-anonymous"}>
        <div className="frame masthead-inner">
          <h1 className="wordmark">
            <svg
              viewBox="0 0 24 28"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.7"
              strokeLinecap="round"
              strokeLinejoin="round"
              aria-hidden="true"
            >
              <path d="M4 2.5h10l6 6v17H4zM14 2.5v6h6M8 13h8m-8 5h8m-8 4h5" />
            </svg>
            請購簽核
          </h1>
          {phase === "ready" && identity ? (
            <section className="account" aria-labelledby="identity-title">
              <h2 id="identity-title" className="visually-hidden">
                目前身分
              </h2>
              <dl className="account-identity">
                <dt className="visually-hidden">帳號</dt>
                <dd className="account-name">{identity.userName}</dd>
                <dt className="visually-hidden">帳號類別</dt>
                <dd className="visually-hidden">
                  {identity.accountType === "admin" ? "管理帳號" : "業務帳號"}
                </dd>
                <dt className="visually-hidden">角色</dt>
                <dd className="account-roles">
                  {
                    // 保留未知角色代碼，避免身分摘要隱去伺服器回傳的角色。
                    identity.roles
                      .map((role) => roleNames[role] ?? role)
                      .join("、") ||
                      (identity.accountType === "admin"
                        ? "管理帳號"
                        : "業務帳號")
                  }
                </dd>
              </dl>
              {!identity.mustChangePassword ? (
                <button
                  className="text-control"
                  type="button"
                  aria-label="變更密碼表單"
                  aria-controls="password-panel"
                  aria-expanded={passwordOpen}
                  disabled={pending}
                  onClick={
                    // 僅展開表單；是否離開草稿仍由提交時的共用確認決定。
                    () => setPasswordOpen(!passwordOpen)
                  }
                >
                  變更密碼
                </button>
              ) : null}
              <button
                className="text-control account-logout"
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
          ) : null}
        </div>
      </header>
      <main className={identity ? "frame workspace" : "auth-card"}>
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
          <section
            aria-labelledby="recovery-title"
            ref={recovery}
            tabIndex={-1}
          >
            <h2 id="recovery-title">請先確認登入狀態</h2>
            <p>重新取得安全權杖與目前身分，不會重送先前的操作。</p>
          </section>
        ) : null}
        {phase === "ready" && !identity ? (
          // 身分已確認為匿名時顯示登入表單，由共用提交函式接管原生提交。
          <LoginForm
            pending={pending}
            onSubmit={
              // 交由共用提交函式同步擷取欄位並保護重複送出。
              (event) => void submit("login", event)
            }
          />
        ) : null}
        {phase === "ready" && identity ? (
          // 已確認登入時才顯示身分與敏感操作，讓表單隨身分階段卸載。
          <>
            <ChangePasswordForm
              mustChangePassword={identity.mustChangePassword}
              passwordOpen={passwordOpen}
              pending={pending}
              onSubmit={
                // 交由共用流程在等待回應前取得輸入快照。
                (event) => void submit("change-password", event)
              }
            />
            {identity.accountType === "admin" &&
            identity.roles.includes("Administrator") &&
            identity.mustChangePassword === false ? (
              // 確認管理資格並解除改密碼限制後，只掛載選擇的管理面板。
              <>
                <nav className="panel-nav" aria-label="管理功能">
                  <button
                    type="button"
                    aria-pressed={!reassignmentActive}
                    disabled={pending}
                    onClick={
                      // 選中原面板時保留畫面及焦點。
                      () => {
                        if (reassignmentActive) switchAdminPanel(false);
                      }
                    }
                  >
                    組織管理
                  </button>
                  <button
                    type="button"
                    aria-pressed={reassignmentActive}
                    disabled={pending}
                    onClick={
                      // 只有管理員切換到改派面板時才讀取待辦。
                      () => {
                        if (!reassignmentActive) switchAdminPanel(true);
                      }
                    }
                  >
                    有限改派
                  </button>
                </nav>
                {!reassignmentActive && currentReassignment?.readback ? (
                  <p role="status">「有限改派」有待讀取的結果。</p>
                ) : null}
                {reassignmentActive ? (
                  <Reassignments
                    key={identity.userName}
                    getToken={getProtectedToken}
                    pending={pending}
                    beginOperation={beginOperation}
                    finishOperation={finishOperation}
                    onSessionInvalid={recoverProtectedSession}
                    readback={currentReassignment.readback}
                    onReadbackChange={markReassignmentWrite}
                  />
                ) : (
                  <Organization
                    key={identity.userName}
                    getToken={getProtectedToken}
                    pending={pending}
                    beginOperation={beginOperation}
                    finishOperation={finishOperation}
                    onSessionInvalid={recoverProtectedSession}
                    confirmedWrite={
                      organizationConfirmation?.actor === identity.userName
                        ? organizationConfirmation.label
                        : ""
                    }
                    onConfirmedWriteChange={markOrganizationWrite}
                    unknownResult={unknownOrganizationFor === identity.userName}
                    onUnknownResult={
                      // 未決結果只對發出該操作的管理帳號顯示。
                      () => setUnknownOrganizationFor(identity.userName)
                    }
                  />
                )}
              </>
            ) : null}
            {identity.accountType === "business" &&
            !identity.mustChangePassword ? (
              // 所有業務帳號都使用同一組入口；一般查詢依當前權限回傳資料。
              <>
                <nav className="panel-nav" aria-label="請購功能">
                  <button
                    type="button"
                    aria-pressed={businessPanel === "drafts"}
                    disabled={pending}
                    onClick={
                      // 選擇草稿畫面，重按已選的面板保留原表單。
                      () => switchBusinessPanel("drafts")
                    }
                  >
                    本人請購草稿
                  </button>
                  <button
                    type="button"
                    aria-pressed={businessPanel === "approvals"}
                    disabled={pending}
                    onClick={
                      // 簽核畫面沿用業務面板的離開與未決結果保護。
                      () => switchBusinessPanel("approvals")
                    }
                  >
                    請購查閱與簽核
                  </button>
                  <button
                    type="button"
                    aria-pressed={businessPanel === "reports"}
                    disabled={pending}
                    onClick={
                      // 從草稿進入報表也須遵循相同的離開確認。
                      () => switchBusinessPanel("reports")
                    }
                  >
                    請購統計與 CSV
                  </button>
                </nav>
                {businessPanel === "reports" ? (
                  <RequisitionReports
                    key={identity.userName}
                    getToken={getProtectedToken}
                    pending={pending}
                    beginOperation={beginOperation}
                    finishOperation={finishOperation}
                    onSessionInvalid={recoverProtectedSession}
                  />
                ) : businessPanel === "approvals" && currentApproval ? (
                  <RequisitionApprovals
                    key={identity.userName}
                    getToken={getProtectedToken}
                    pending={pending}
                    beginOperation={beginOperation}
                    finishOperation={finishOperation}
                    onSessionInvalid={recoverProtectedSession}
                    readbacks={currentApproval.readbacks}
                    onReadbackChange={markApprovalWrite}
                  />
                ) : (
                  <RequisitionDrafts
                    key={identity.userName}
                    getToken={getProtectedToken}
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
                )}
              </>
            ) : null}
          </>
        ) : null}
        {refreshControl}
      </main>
    </>
  );
}

// 掛載單一身分畫面，HTML 入口提供固定的根節點。
createRoot(document.getElementById("root")!).render(<App />);
