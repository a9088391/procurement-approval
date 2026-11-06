// 有限改派只掛載管理待辦，保留交易確認與清單讀回兩種不同結果。
import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type FormEvent,
} from "react";
import {
  ApiError,
  readReassignments,
  writeReassignment,
  type ReassignmentListDto,
  type ReassignmentStage,
} from "./api";

// 跨面板只保留待核對的案件識別與結果種類，表單和清單隨面板卸載。
export interface ReassignmentReadback {
  id: string;
  result: "confirmed" | "changed" | "unknown";
}

interface Props {
  getToken: () => string;
  pending: boolean;
  beginOperation: () => boolean;
  finishOperation: () => void;
  onSessionInvalid: (notice: string) => void;
  readback: ReassignmentReadback | null;
  onReadbackChange: (readback: ReassignmentReadback | null) => void;
}

const stages = { manager: "主管", finance: "財務" };
const recoveryMessages = {
  confirmed: "改派已保存。請重新讀取結果以取得目前待辦。",
  changed: "待辦狀態或人選已變更。請先重新讀取結果。",
  unknown: "改派結果未確認。請先重新讀取結果，系統不會自動重送。",
};

// 防偽或權限遭拒絕時，不允許目前畫面繼續顯示原帳號的待辦。
function requiresSessionCheck(error: unknown) {
  return (
    error instanceof ApiError &&
    (error.status === 401 ||
      error.status === 403 ||
      error.code === "invalid_csrf")
  );
}

// 明確拒絕的狀態須更新案件版本；其他非預期回應仍可能已寫入。
function requiresFreshSnapshot(error: unknown) {
  return (
    error instanceof ApiError &&
    ((error.status === 409 &&
      ["stale_version", "invalid_state", "reviewers_unavailable"].includes(
        error.code,
      )) ||
      (error.status === 404 && error.code === "not_found"))
  );
}

// 清單和表單同時受 App 的閘門及成對權杖保護，避免同分頁的身分操作交錯。
export function Reassignments({
  getToken,
  pending,
  beginOperation,
  finishOperation,
  onSessionInvalid,
  readback,
  onReadbackChange,
}: Props) {
  // 已有待核對結果時只提供手動 GET，即使此面板是重新掛載的。
  const [loadOnMount] = useState(readback === null);
  const [snapshot, setSnapshot] = useState<ReassignmentListDto | null>(null);
  const [editor, setEditor] = useState<{
    id: string;
    stage: ReassignmentStage;
  } | null>(null);
  const [feedback, setFeedback] = useState<{
    message: string;
    error: boolean;
  } | null>(null);
  const [readError, setReadError] = useState("");
  const [invalidReason, setInvalidReason] = useState(false);
  const heading = useRef<HTMLHeadingElement>(null);
  const editorHeading = useRef<HTMLHeadingElement>(null);
  const recoveryNotice = useRef<HTMLParagraphElement>(null);
  const selectedId = editor?.id;

  // 從管理選單進入時定位面板，清單讀取完成不另外移動焦點。
  useEffect(() => {
    heading.current?.focus();
  }, []);

  // 開啟單筆編輯後才移到其標題，選擇另一關卡時保留輸入焦點。
  useEffect(() => {
    if (selectedId) editorHeading.current?.focus();
  }, [selectedId]);

  // 寫入或重讀結束後將焦點交給待核對指引，下一次 Tab 可到重讀按鈕。
  useEffect(() => {
    if (readback && !pending) recoveryNotice.current?.focus();
  }, [pending, readback]);

  // 所有讀回先移除舊清單，只有有效且成對的回應才能重新顯示待辦。
  const loadSnapshot = useCallback(
    async (token: string, signal?: AbortSignal) => {
      setSnapshot(null);
      setReadError("");
      try {
        const next = await readReassignments(
          token,
          signal && AbortSignal.any([signal, AbortSignal.timeout(15000)]),
        );
        if (signal?.aborted) return null;
        setSnapshot(next);
        return next;
      } catch (error) {
        // 卸載不顯示提示；身分拒絕則立即交由 App 清除受保護面板。
        if (signal?.aborted) return null;
        if (requiresSessionCheck(error)) {
          onSessionInvalid("無法確認改派所用的管理身分。請重新確認身分。");
        } else {
          setReadError("無法讀取改派待辦，請重新讀取。");
        }
        return null;
      }
    },
    [onSessionInvalid],
  );

  // 初次進入時只讀清單；取消掛載時同步結束本輪所有權。
  useEffect(() => {
    if (!loadOnMount || !beginOperation()) return;
    const controller = new AbortController();
    let waiting = true;
    void loadSnapshot(getToken(), controller.signal).finally(
      // 卸載清理已交還閘門時，完成回呼不能解除較新的操作。
      () => {
        if (!waiting) return;
        waiting = false;
        finishOperation();
      },
    );
    return () => {
      // 只在仍等待本輪回應時交還閘門，避免影響後續提交。
      controller.abort();
      if (!waiting) return;
      waiting = false;
      finishOperation();
    };
  }, [loadOnMount, beginOperation, finishOperation, getToken, loadSnapshot]);

  // 讀回成功後釋出寫入限制，將案件離開清單與先前交易的確定性分開說明。
  async function readResult(
    token: string,
    operation: ReassignmentReadback | null,
  ) {
    // 取回清單前關閉舊表單，下一筆操作必須重新選擇人選及填寫原因。
    setEditor(null);
    const current = await loadSnapshot(token);
    if (!current || !operation) return;

    // 讀到目前指派也不能據以認定原本的未知交易已生效。
    const stillPending = current.items.some(
      // 只在同一筆案件仍可見時使用「已更新」的提示。
      ({ id }) => id === operation.id,
    );
    const result =
      operation.result === "confirmed"
        ? "改派已保存。"
        : operation.result === "unknown"
          ? "原改派結果仍未確認。"
          : "";
    setFeedback({
      message: `${result}${stillPending ? "已讀取目前待辦與審核人選。" : "該單目前已不在待辦清單中。"}`,
      error: operation.result === "unknown",
    });
    onReadbackChange(null);
    // 關閉表單或替換重讀按鈕時，讓鍵盤仍能從面板開頭接續操作。
    heading.current?.focus();
  }

  // 手動重讀只執行 GET，並沿用發出讀取時的管理身分權杖。
  async function reload() {
    if (!beginOperation()) return;
    try {
      setFeedback(null);
      await readResult(getToken(), readback);
    } finally {
      finishOperation();
    }
  }

  // 取得單筆表單與該筆版本的輸入快照，再進入同一輪寫入及讀回。
  async function submit(event: FormEvent<HTMLFormElement>) {
    // 待核對或待辦已失效時即使有重複表單事件，也不允許送出。
    event.preventDefault();
    if (!item || !editor || readback) return;
    const form = event.currentTarget;
    const values = new FormData(form);
    const reviewer = form.elements.namedItem("reviewer") as HTMLSelectElement;
    const reasonField = form.elements.namedItem(
      "reason",
    ) as HTMLTextAreaElement;
    const reviewerId = values.get("reviewer")?.toString() ?? "";
    // .NET Trim 使用 Unicode White_Space；JS trim 會多移除 FEFF 且保留 U+0085。
    const reason = (values.get("reason")?.toString() ?? "").replace(
      /^\p{White_Space}+|\p{White_Space}+$/gu,
      "",
    );
    const reasonRejected =
      !reason || reason.length > 500 || reason.includes("\0");
    setInvalidReason(reasonRejected);

    // 檢查可見角色選項及 UTF-16 長度，明確拒絕時留在原表單供修正。
    if (!reviewerId || reasonRejected) {
      setFeedback({
        message: reasonRejected
          ? "請填寫 1–500 字的改派原因，且不得含 NUL 字元。"
          : "請選擇接手審核者。",
        error: true,
      });
      (reasonRejected ? reasonField : reviewer).focus();
      return;
    }
    if (!beginOperation()) return;
    setFeedback(null);
    const token = getToken();
    const { id, version } = item;

    // 寫入只確認 204；其他異常依契約分為可修正、須重讀或結果未知。
    try {
      try {
        await writeReassignment(token, id, {
          version,
          stage: editor.stage,
          reviewerId,
          reason,
        });
      } catch (error) {
        if (requiresSessionCheck(error)) {
          onSessionInvalid("無法確認改派所用的管理身分。請重新確認身分。");
          return;
        }
        if (
          error instanceof ApiError &&
          error.status === 400 &&
          error.code === "invalid_input"
        ) {
          setFeedback({ message: error.message, error: true });
          return;
        }

        // 過期版本及未知結果一律關閉舊表單，保留同帳號的手動讀回入口。
        setEditor(null);
        setSnapshot(null);
        onReadbackChange({
          id,
          result: requiresFreshSnapshot(error) ? "changed" : "unknown",
        });
        return;
      }

      // 已知 204 的事實先保存；即使這次 GET 遇到拒絕或傳輸失敗也不覆蓋。
      const confirmed: ReassignmentReadback = { id, result: "confirmed" };
      onReadbackChange(confirmed);
      await readResult(token, confirmed);
    } finally {
      finishOperation();
    }
  }

  // 候選人僅取該關具資格的選項，排除兩關已指派的人；申請人由後端查證。
  const item = snapshot?.items.find(
    // 只用目前清單中的案件及版本組裝可編輯資料。
    ({ id }) => id === selectedId,
  );
  const candidates =
    item && editor && snapshot
      ? (editor.stage === "manager"
          ? snapshot.managers
          : snapshot.finances
        ).filter(
          // 同一人不能留在原關或同時接手另一關。
          ({ id }) => id !== item.manager.id && id !== item.finance.id,
        )
      : [];

  // 待辦摘要只列出單號、送審部門、關卡、暫停及兩關人選。
  return (
    <section className="reassignments" aria-labelledby="reassignments-title">
      <h2 id="reassignments-title" ref={heading} tabIndex={-1}>
        有限改派
      </h2>
      <p className="hint">
        只可更換未完成關卡的審核者。資格完整後，案件會繼續原待審關卡。
      </p>
      {readback ? (
        <p
          className={
            readback.result === "confirmed" ? "feedback" : "feedback error"
          }
          role={readback.result === "confirmed" ? "status" : "alert"}
          ref={recoveryNotice}
          tabIndex={-1}
        >
          {recoveryMessages[readback.result]}
        </p>
      ) : null}
      {feedback ? (
        <p
          className={feedback.error ? "feedback error" : "feedback"}
          role={feedback.error ? "alert" : "status"}
        >
          {feedback.message}
        </p>
      ) : null}
      {readError ? (
        <p className="feedback error" role="alert">
          {readError}
        </p>
      ) : null}
      <button
        type="button"
        className="secondary"
        disabled={pending}
        onClick={
          // 手動讀回不重送先前表單。
          () => void reload()
        }
      >
        {readback ? "重新讀取結果" : "重新讀取改派待辦"}
      </button>
      {pending && !snapshot ? <p role="status">正在讀取改派待辦…</p> : null}
      {snapshot && snapshot.items.length === 0 ? (
        <p className="hint">目前沒有待辦。</p>
      ) : null}
      {snapshot && snapshot.items.length > 0 ? (
        <ul aria-label="改派待辦">
          {snapshot.items.map(
            // 待辦只展開目前所選單號的表單，其他列保持唯讀摘要。
            (row) => (
              <li className="draft-line" key={row.id}>
                <button
                  type="button"
                  className="secondary"
                  aria-expanded={selectedId === row.id}
                  disabled={pending || !!readback}
                  onClick={
                    // 明確選擇單號才載入其未完成關卡，且不沿用另一單的輸入。
                    () => {
                      setFeedback(null);
                      setInvalidReason(false);
                      if (selectedId === row.id) {
                        setEditor(null);
                      } else {
                        setEditor({ id: row.id, stage: row.stage });
                      }
                    }
                  }
                >
                  {row.number}
                </button>
                <dl>
                  <dt>送審部門</dt>
                  <dd>{row.department.name}</dd>
                  <dt>目前關卡</dt>
                  <dd>{stages[row.stage]}待審</dd>
                  <dt>主管審核者</dt>
                  <dd>{row.manager.userName}</dd>
                  <dt>財務審核者</dt>
                  <dd>{row.finance.userName}</dd>
                </dl>
                {row.paused ? (
                  <p className="feedback error">簽核已暫停</p>
                ) : null}
                {selectedId === row.id && editor ? (
                  <form
                    aria-labelledby="reassignment-editor-title"
                    noValidate
                    onSubmit={
                      // 以自訂原因驗證保留後端接受的 UTF-16 長度及空白規則。
                      (event) => void submit(event)
                    }
                  >
                    <h3
                      id="reassignment-editor-title"
                      ref={editorHeading}
                      tabIndex={-1}
                    >
                      改派 {row.number}
                    </h3>
                    <fieldset disabled={pending || !!readback}>
                      <label htmlFor="reassignment-stage">改派關卡</label>
                      <select
                        id="reassignment-stage"
                        value={editor.stage}
                        onChange={
                          // 變更關卡時重新選擇該關的角色人選，理由仍留在表單。
                          (event) =>
                            setEditor({
                              id: row.id,
                              stage: event.currentTarget
                                .value as ReassignmentStage,
                            })
                        }
                      >
                        {row.stage === "manager" ? (
                          <option value="manager">主管</option>
                        ) : null}
                        <option value="finance">財務</option>
                      </select>
                      <label htmlFor="reassignment-reviewer">接手審核者</label>
                      <select
                        id="reassignment-reviewer"
                        name="reviewer"
                        key={editor.stage}
                        required
                      >
                        <option value="">請選擇</option>
                        {candidates.map(
                          // 送出穩定識別，畫面只顯示角色候選人的名稱。
                          ({ id, userName }) => (
                            <option key={id} value={id}>
                              {userName}
                            </option>
                          ),
                        )}
                      </select>
                      {!candidates.length ? (
                        <p className="hint">目前沒有可選的接手審核者。</p>
                      ) : null}
                      <label htmlFor="reassignment-reason">改派原因</label>
                      <textarea
                        id="reassignment-reason"
                        name="reason"
                        required
                        rows={3}
                        aria-describedby="reassignment-reason-help"
                        aria-invalid={invalidReason}
                      />
                      <p id="reassignment-reason-help" className="hint">
                        移除前後空白後須為 1–500 字。
                      </p>
                      <button type="submit" disabled={!candidates.length}>
                        保存改派
                      </button>
                    </fieldset>
                  </form>
                ) : null}
              </li>
            ),
          )}
        </ul>
      ) : null}
    </section>
  );
}
