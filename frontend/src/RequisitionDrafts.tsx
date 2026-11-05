// 本人草稿的讀寫與身分操作共用閘門；身分待確認時，整個草稿畫面卸載。
import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type FormEvent,
} from "react";
import {
  ApiError,
  deleteDraft,
  readDraft,
  readDraftList,
  saveDraft,
  type DraftDto,
  type DraftSummaryDto,
} from "./api";
import {
  analyzeDraftForm,
  formatDraftAmount,
  makeDraftForm,
  RequisitionDraftFields,
  type DraftForm,
} from "./RequisitionDraftForm";

interface Props {
  getToken: () => string;
  pending: boolean;
  beginOperation: () => boolean;
  finishOperation: () => void;
  onSessionInvalid: (notice: string, unknownResult: boolean) => void;
  onLeaveWarningChange: (warn: boolean, unknown?: boolean) => void;
  priorUnknown: boolean;
  onUnknownReviewed: () => void;
}

interface Editor {
  draft?: DraftDto;
  form: DraftForm;
  initialForm: DraftForm;
}

type UnknownWrite = "unknown-create" | "unknown-save" | "unknown-delete";
type Recovery = "stale" | "missing" | "checked-create" | UnknownWrite;

const updatedAtFormat = new Intl.DateTimeFormat("zh-TW", {
  dateStyle: "medium",
  timeStyle: "short",
  timeZone: "Asia/Taipei",
});

// 建立同一份初始快照；編輯產生新物件後才提示未保存變更。
function makeEditor(draft?: DraftDto): Editor {
  const form = makeDraftForm(draft);
  return { draft, form, initialForm: form };
}

// 僅採用契約定義的草稿查無回應，以免把連線錯誤當成已不存在。
function draftMissing(error: unknown) {
  return (
    error instanceof ApiError &&
    error.status === 404 &&
    error.code === "not_found"
  );
}

// 登入或防偽已失效時移除舊資料，重新確認前不接受其他草稿操作。
function requiresSessionCheck(error: unknown): error is ApiError {
  return (
    error instanceof ApiError &&
    (error.status === 401 ||
      error.status === 403 ||
      error.code === "invalid_csrf")
  );
}

// 集中保存本頁的清單與單筆編輯狀態，任何版本或未知寫入皆需人工讀回。
export function RequisitionDrafts({
  getToken,
  pending,
  beginOperation,
  finishOperation,
  onSessionInvalid,
  onLeaveWarningChange,
  priorUnknown,
  onUnknownReviewed,
}: Props) {
  // 寫入回饋與 GET 錯誤分開，才能在讀回失敗時維持正確的先前結果。
  const [drafts, setDrafts] = useState<DraftSummaryDto[] | null>(null);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [recovery, setRecovery] = useState<Recovery | null>(null);
  const [notice, setNotice] = useState<{
    text: string;
    error: boolean;
    uncertain?: true;
  } | null>(null);
  const [readError, setReadError] = useState("");
  const listHeading = useRef<HTMLHeadingElement>(null);
  const editorHeading = useRef<HTMLHeadingElement>(null);

  // 由編輯與讀回狀態推導保護範圍，避免切換身分時靜默捨棄修改或未確認結果。
  const isEditing = editor !== null;
  const dirty = editor !== null && editor.form !== editor.initialForm;
  const unresolved = recovery?.startsWith("unknown-") ?? false;
  const needsReview = unresolved || recovery === "checked-create";
  const hasRecoveredUnknown = notice?.uncertain === true || priorUnknown;
  const unknownResult = needsReview || hasRecoveredUnknown;
  const warnBeforeLeaving = dirty || needsReview;
  const requiresReadback = recovery !== null || priorUnknown;
  const fieldsLocked = pending || requiresReadback;
  const canOpenEditor = !pending && !isEditing;
  const canStartDraft = canOpenEditor && !priorUnknown;

  // 只在需要時攔截頁面卸載，並將相同的提示條件同步給頁內身分操作。
  useEffect(() => {
    onLeaveWarningChange(warnBeforeLeaving, needsReview);
    if (!warnBeforeLeaving) return;

    // 交由瀏覽器提供離開確認，輸入不寫入瀏覽器儲存空間。
    function warnBeforeUnload(event: BeforeUnloadEvent) {
      event.preventDefault();
    }

    // 切換狀態或卸載時撤銷保護，避免下個帳號沿用這輪編輯狀態。
    window.addEventListener("beforeunload", warnBeforeUnload);
    return () => {
      window.removeEventListener("beforeunload", warnBeforeUnload);
      onLeaveWarningChange(false);
    };
  }, [needsReview, onLeaveWarningChange, warnBeforeLeaving]);

  // 開啟編輯器後將焦點移到標題，讓鍵盤從表單開頭開始操作。
  useEffect(() => {
    if (isEditing) editorHeading.current?.focus();
  }, [isEditing]);

  // 初次及手動讀取共用失敗處理；取消後不採用較晚到達的清單。
  const loadList = useCallback(
    async (signal?: AbortSignal, keepUnknown = false) => {
      try {
        // 帶上已確認身分的權杖，初次讀取仍受元件卸載與逾時限制。
        const next = await readDraftList(
          getToken(),
          signal && AbortSignal.any([signal, AbortSignal.timeout(15000)]),
        );
        if (signal?.aborted) return false;
        setDrafts(next);
        setReadError("");
        return true;
      } catch (error) {
        // 身分錯誤由 App 統一復原，其他讀取錯誤保留現有畫面。
        if (signal?.aborted) return false;
        if (requiresSessionCheck(error)) {
          onSessionInvalid(`${error.message} 請重新確認身分。`, keepUnknown);
        } else {
          setReadError("無法讀取草稿清單，請重新讀取。");
        }
        return false;
      }
    },
    [getToken, onSessionInvalid],
  );

  // 掛載只發出清單 GET，取消時解除自己持有的閘門。
  useEffect(() => {
    // 共用閘門讓清單讀取不會與這個頁面的身分切換交錯。
    if (!beginOperation()) return;
    const controller = new AbortController();
    let waiting = true;
    void loadList(controller.signal).finally(
      // 正常結束由本輪讀取釋放；取消時改由清理函式釋放。
      () => {
        if (!waiting) return;
        waiting = false;
        finishOperation();
      },
    );

    // 取消未完成的讀取，已完成的讀取不再有資格釋放後續操作。
    return () => {
      controller.abort();
      if (!waiting) return;
      waiting = false;
      finishOperation();
    };
  }, [beginOperation, finishOperation, loadList]);

  // 重讀清單成功後才能人工核對未知的建立結果，讀取本身從不重送寫入。
  async function reloadList() {
    if (!beginOperation()) return;
    try {
      // 確認收到清單才顯示人工核對入口；失敗時保留原未知狀態。
      if (
        (await loadList(undefined, unknownResult)) &&
        recovery === "unknown-create"
      ) {
        setRecovery("checked-create");
      }
    } finally {
      finishOperation();
    }
  }

  // 建立表單只修改畫面；首次保存才要求後端配置草稿識別。
  function startDraft() {
    setNotice(null);
    setEditor(makeEditor());
  }

  // 明確離開編輯器，將焦點帶回可重新開啟草稿的清單。
  function leaveEditor() {
    setEditor(null);
    setRecovery(null);
    listHeading.current?.focus();
  }

  // 開啟既有草稿或核對當前版本；只有成功讀回才替換原表單。
  async function loadDraft(id: string) {
    if (!beginOperation()) return;
    setReadError("");

    try {
      // 開啟與重讀使用同一個身分的權杖，防止讀回其他帳號的資料。
      const next = await readDraft(getToken(), id);

      // 未確認的寫入若仍是相同版本，保留原輸入供人工決定後續操作。
      if (!unresolved || editor?.draft?.version !== next.version) {
        setEditor(makeEditor(next));
      }

      // 同步清單摘要並回到表單開頭，避免讀回按鈕移除後失去鍵盤焦點。
      const { number, subject, updatedAt } = next;
      setDrafts(
        // 只更新剛讀取的摘要，清單的其他項目保持原樣。
        (current) =>
          current?.map((draft) =>
            draft.id === id ? { id, number, subject, updatedAt } : draft,
          ) ?? null,
      );
      setRecovery(null);
      editorHeading.current?.focus();

      // 讀到目前狀態能供比對，但無法判斷先前的未知寫入是否為變更來源。
      setNotice(
        unresolved
          ? {
              text: "先前操作結果未確認。已讀取目前草稿，請核對內容後再決定是否操作。",
              error: true,
              uncertain: true,
            }
          : null,
      );
    } catch (error) {
      // 依契約區分目前已查無、身分失效及暫時讀取失敗。
      if (requiresSessionCheck(error)) {
        onSessionInvalid(`${error.message} 請重新確認身分。`, unknownResult);
      } else if (draftMissing(error)) {
        setDrafts(
          // 此筆已無法從清單開啟，其他摘要照常保留。
          (current) => current?.filter((draft) => draft.id !== id) ?? null,
        );
        if (recovery === "unknown-delete") leaveEditor();
        else if (editor) setRecovery("missing");
        setNotice({
          text: unresolved
            ? "先前操作結果未確認。目前已查無此草稿。"
            : "目前已查無此草稿。",
          error: true,
          uncertain: unresolved ? true : undefined,
        });
      } else {
        // GET 失敗時保留輸入、版本與未知結果，允許再次讀回。
        setReadError("無法讀取草稿內容，請重新讀取。");
      }
    } finally {
      finishOperation();
    }
  }

  // 只把契約明確拒絕的寫入視為未發生，其餘結果一律先交由使用者讀回。
  function handleWriteError(error: unknown, unknown: UnknownWrite) {
    // 身分復原會卸載舊資料，使用者必須重新開啟已保存的草稿。
    if (requiresSessionCheck(error)) {
      onSessionInvalid(
        `${error.message} 請重新確認身分，並重新開啟已保存的草稿。`,
        unknownResult,
      );
      return;
    }

    // 驗證錯誤保留表單，過期版本則需取得最新詳情；查無時不能繼續寫入。
    if (
      error instanceof ApiError &&
      error.status === 400 &&
      error.code === "invalid_input"
    ) {
      setNotice({ text: error.message, error: true });
    } else if (
      error instanceof ApiError &&
      error.status === 409 &&
      error.code === "stale_version"
    ) {
      setRecovery("stale");
      setNotice({ text: "草稿已變更，請重新讀取目前草稿。", error: true });
    } else if (draftMissing(error)) {
      setDrafts(
        // 查無時先移除已失效的清單摘要，編輯內容仍由使用者決定是否捨棄。
        (current) =>
          current?.filter(({ id }) => id !== editor?.draft?.id) ?? null,
      );
      setRecovery("missing");
      setNotice({ text: "目前已查無此草稿，請返回清單。", error: true });
    } else {
      // 回應遺失、逾時或 5xx 都可能發生在提交之後，阻擋重送直到人工讀回。
      setRecovery(unknown);
      setNotice({
        text: "操作結果未確認，可能已生效。請先讀取最新資料，系統不會自動重送。",
        error: true,
      });
    }
  }

  // 編輯中的預覽以本地整數分計算，未修改時直接呈現後端金額與完整性。
  const analysis = editor && analyzeDraftForm(editor.form);
  const savedDraft = editor?.draft;
  const authoritative = dirty ? undefined : savedDraft;
  const amount = authoritative
    ? authoritative.estimatedAmount
    : (analysis?.total ?? null);
  const complete = authoritative
    ? authoritative.isComplete
    : analysis?.complete;
  const subtotals = authoritative
    ? authoritative.lines.map(
        // 保存後及重開時採用 API 小計，避免前端畫面另行推定保存結果。
        (line) => line.estimatedAmount,
      )
    : (analysis?.subtotals ?? []);

  // 送出合法的編輯欄位與原版本，成功回應直接提供目前資料供畫面採用。
  async function submit(event: FormEvent<HTMLFormElement>) {
    // 不完整可保存；已提供但不合法的值須先修改，且不能被轉型成合法值。
    event.preventDefault();
    if (!analysis || fieldsLocked) return;
    if (analysis.error) {
      setNotice({ text: analysis.error, error: true });
      return;
    }

    // 在等待回應前取得共用閘門，其他身分或草稿操作不得插入。
    if (!beginOperation()) return;
    setNotice(null);
    setReadError("");

    try {
      // 由伺服器回傳內容、版本及金額；同時更新清單的對應摘要。
      const saved = await saveDraft(getToken(), analysis.content, savedDraft);
      const nextEditor = makeEditor(saved);
      const { id, number, subject, updatedAt } = saved;
      setEditor(nextEditor);
      setDrafts(
        // 保存後的摘要排在前面，其餘維持原順序。
        (current) => [
          { id, number, subject, updatedAt },
          ...(current?.filter((draft) => draft.id !== id) ?? []),
        ],
      );
      setNotice({ text: "草稿已保存。", error: false });
    } catch (error) {
      handleWriteError(error, savedDraft ? "unknown-save" : "unknown-create");
    } finally {
      finishOperation();
    }
  }

  // 確認後才送出刪除與原版本，未知回應時交由目前草稿的 GET 核對。
  async function removeDraft() {
    // 只接受目前的既有草稿，且確認訊息清楚告知尚未保存的修改會一同捨棄。
    const draft = savedDraft;
    if (!draft || fieldsLocked) return;
    const hint = dirty ? "尚未保存的修改也會捨棄。" : "";
    if (!window.confirm(`確定刪除草稿 ${draft.number}？${hint}`)) return;
    if (!beginOperation()) return;
    setNotice(null);
    setReadError("");

    try {
      // 收到 204 才回報刪除成功，並從畫面移除該筆資料。
      await deleteDraft(getToken(), draft);
      setDrafts(
        // 保留清單中其餘草稿與目前順序。
        (current) => current?.filter(({ id }) => id !== draft.id) ?? null,
      );
      leaveEditor();
      setNotice({ text: "草稿已刪除。", error: false });
    } catch (error) {
      handleWriteError(error, "unknown-delete");
    } finally {
      finishOperation();
    }
  }

  // 只有過期或未知的既有草稿可用詳情讀回；未知建立則以清單核對。
  const canReloadDraft =
    recovery === "stale" ||
    recovery === "unknown-save" ||
    recovery === "unknown-delete";

  // 清單保持可檢視，編輯器開啟時須先明確返回才切換到另一張草稿。
  return (
    <section className="requisition-drafts" aria-labelledby="draft-list-title">
      <h2 id="draft-list-title" tabIndex={-1} ref={listHeading}>
        本人請購草稿
      </h2>
      {notice ? (
        <p
          className={notice.error ? "feedback error" : "feedback"}
          role={notice.error ? "alert" : "status"}
        >
          {notice.text}
        </p>
      ) : null}
      {readError ? (
        <p className="feedback error" role="alert">
          {readError}
        </p>
      ) : null}
      {priorUnknown ? (
        <>
          <p className="feedback error" role="alert">
            先前操作結果未確認，請核對本人草稿清單，必要時開啟草稿確認內容。系統不會自動重送。
          </p>
          <button
            className="secondary"
            type="button"
            disabled={pending || !drafts}
            onClick={
              // 解除提示後將焦點移回仍可操作的內容。
              () => {
                onUnknownReviewed();
                (editorHeading.current ?? listHeading.current)?.focus();
              }
            }
          >
            已核對先前操作
          </button>
        </>
      ) : null}
      <div className="draft-actions">
        <button
          type="button"
          disabled={!canStartDraft || !drafts}
          onClick={startDraft}
        >
          新增草稿
        </button>
        <button
          className="secondary"
          type="button"
          disabled={pending}
          onClick={
            // 只讀取清單，不改動表單，也不重送寫入。
            () => void reloadList()
          }
        >
          重新讀取草稿清單
        </button>
      </div>
      {drafts?.length ? (
        <ul>
          {drafts.map(
            // 清單摘要足以辨識草稿，詳情等開啟時再讀取。
            ({ id, number, subject, updatedAt }) => (
              <li key={id}>
                <p>{subject?.trim() ? subject : "未填主旨"}</p>
                <p className="hint">
                  最近更新{" "}
                  <time dateTime={updatedAt}>
                    {updatedAtFormat.format(new Date(updatedAt))}
                  </time>
                </p>
                <button
                  className="secondary"
                  type="button"
                  disabled={!canOpenEditor}
                  onClick={
                    // 以系統識別讀取目前草稿，不使用舊摘要填寫表單。
                    () => void loadDraft(id)
                  }
                >
                  開啟草稿 {number}
                </button>
              </li>
            ),
          )}
        </ul>
      ) : (
        <p>
          {drafts
            ? "目前沒有草稿。"
            : pending
              ? "正在讀取草稿清單…"
              : "尚未讀取草稿清單。"}
        </p>
      )}
      {editor ? (
        <form
          aria-labelledby="draft-editor-title"
          onSubmit={
            // 明確送出表單時才執行建立或保存。
            (event) => void submit(event)
          }
        >
          <h3 id="draft-editor-title" ref={editorHeading} tabIndex={-1}>
            {editor.draft ? `編輯草稿 ${editor.draft.number}` : "新請購草稿"}
          </h3>
          <p className="hint">草稿可以留空。金額只涵蓋可計算的明細。</p>
          <fieldset disabled={fieldsLocked}>
            <RequisitionDraftFields
              form={editor.form}
              subtotals={subtotals}
              onChange={
                // 變更表單副本，保留讀取版本及初始快照。
                (form) => setEditor({ ...editor, form })
              }
            />
            <p>
              {analysis?.error
                ? "請修正輸入後查看預估申請金額。"
                : `預估申請金額${complete ? "" : "（部分）"}：${formatDraftAmount(amount)}`}
            </p>
            <div className="draft-actions">
              <button type="submit">保存草稿</button>
              {editor.draft ? (
                <button
                  className="secondary"
                  type="button"
                  onClick={
                    // 刪除既有草稿前先取得使用者確認。
                    () => void removeDraft()
                  }
                >
                  刪除草稿
                </button>
              ) : null}
            </div>
          </fieldset>
          {canReloadDraft && savedDraft ? (
            <>
              <p className="hint">
                重新讀取可能以目前已保存的內容取代表單，請先確認未保存的修改。
              </p>
              <button
                className="secondary"
                type="button"
                disabled={pending}
                onClick={
                  // 僅在使用者要求時讀回目前版本。
                  () => void loadDraft(savedDraft.id)
                }
              >
                重新讀取目前草稿
              </button>
            </>
          ) : null}
          {recovery === "checked-create" ? (
            <>
              <p className="hint">
                請先核對草稿清單。若先前已建立，再次保存會新增另一張草稿。
              </p>
              <button
                className="secondary"
                type="button"
                disabled={pending}
                onClick={
                  // 明確確認已核對清單才解除保存限制，保留目前輸入。
                  () => {
                    setRecovery(null);
                    editorHeading.current?.focus();
                  }
                }
              >
                已核對清單，繼續編輯
              </button>
            </>
          ) : null}
          <div className="draft-actions">
            <button
              className="secondary"
              type="button"
              disabled={pending || unresolved}
              onClick={leaveEditor}
            >
              {dirty ? "放棄修改並返回" : "返回草稿清單"}
            </button>
          </div>
        </form>
      ) : null}
    </section>
  );
}
