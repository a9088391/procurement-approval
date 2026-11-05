// 顯示已授權的請購、輪次與歷程；每次簽核只使用畫面讀到的版本。
import { useCallback, useEffect, useRef, useState } from "react";
import {
  ApiError,
  readRequisition,
  readRequisitions,
  writeApproval,
  type ApprovalAction,
  type RequisitionDetailDto,
  type RequisitionRoundSnapshotDto,
  type RequisitionSummaryDto,
} from "./api";
import { formatDraftAmount } from "./RequisitionDraftForm";

interface Props {
  getToken: () => string;
  pending: boolean;
  beginOperation: () => boolean;
  finishOperation: () => void;
  onSessionInvalid: (notice: string) => void;
  readbacks: Readonly<Record<string, ApprovalReadback>>;
  onReadbackChange: (id: string, readback: ApprovalReadback | null) => void;
}

// 同帳號可跨畫面核對原請求版本、當時輪次與已確認的動作。
export interface ApprovalReadback {
  version: string;
  roundNumber?: number;
  confirmed?: ApprovalAction;
}

const statusNames = {
  draft: "草稿",
  withdrawn: "撤回修改中",
  manager_pending: "主管待審",
  finance_pending: "財務待審",
  approved: "核准結案",
  rejected: "駁回結案",
};

const actionNames = {
  submit: "送出簽核",
  withdraw: "撤回修改",
  approve: "核准",
  reject: "駁回",
};
const dateFormat = new Intl.DateTimeFormat("zh-TW", {
  dateStyle: "medium",
  timeStyle: "short",
  timeZone: "Asia/Taipei",
});

// 只有契約明確拒絕的寫入可當作未執行；其他異常須人工核對。
const writeRejections = new Set([
  "invalid_input",
  "stale_version",
  "invalid_state",
  "reviewers_unavailable",
]);

// 讀寫共用身分恢復；強制改密碼可能在驗證配對權杖前先回 403。
function requiresSessionCheck(error: unknown): error is ApiError {
  return (
    error instanceof ApiError &&
    (error.status === 401 ||
      error.status === 403 ||
      error.code === "invalid_csrf")
  );
}

// 一般請購使用同一查無錯誤表示不存在或不可見，兩者都不得留下原明細。
function unavailable(error: unknown) {
  return (
    error instanceof ApiError &&
    error.status === 404 &&
    error.code === "not_found"
  );
}

// 所有輪次時間以展示環境的臺灣時區呈現，同時保留原機器可讀時間。
function RequisitionTime({ value }: { value: string }) {
  return <time dateTime={value}>{dateFormat.format(new Date(value))}</time>;
}

// 目前輪與歷史輪共用同一組後端快照欄位，避免顯示語意分岔。
function RequisitionRoundFields({
  round,
}: {
  round: NonNullable<RequisitionSummaryDto["round"]>;
}) {
  return (
    <>
      <dt>送審輪次</dt>
      <dd>第 {round.number} 輪</dd>
      <dt>送審部門</dt>
      <dd>{round.department.name}</dd>
      <dt>送審時間</dt>
      <dd>
        <RequisitionTime value={round.submittedAt} />
      </dd>
      <dt>主管審核者</dt>
      <dd>{round.manager.userName}</dd>
      <dt>財務審核者</dt>
      <dd>{round.finance.userName}</dd>
    </>
  );
}

// 依伺服器順序顯示已保存明細，不重新計算金額或改排品項。
function RequisitionLines({ lines }: { lines: RequisitionDetailDto["lines"] }) {
  return (
    <>
      <h4>請購明細</h4>
      {lines.length ? (
        <ol>
          {lines.map(
            // 明細保留 API 的順序，以索引識別同一份已讀取快照中的位置。
            (line, index) => (
              <li key={index} className="draft-line">
                <dl>
                  <dt>品名</dt>
                  <dd>{line.itemName || "未填品名"}</dd>
                  <dt>單位</dt>
                  <dd>{line.unit || "未填單位"}</dd>
                  <dt>數量</dt>
                  <dd>{line.quantity ?? "未填數量"}</dd>
                  <dt>預估單價</dt>
                  <dd>{formatDraftAmount(line.unitPrice)}</dd>
                  <dt>預估小計</dt>
                  <dd>{formatDraftAmount(line.estimatedAmount)}</dd>
                </dl>
              </li>
            ),
          )}
        </ol>
      ) : (
        <p className="hint">目前沒有明細。</p>
      )}
    </>
  );
}

// 完整顯示一輪送審時的內容、人選與部門，不受目前設定變更影響。
function RequisitionRoundSnapshot({
  snapshot,
}: {
  snapshot: RequisitionRoundSnapshotDto;
}) {
  const { round } = snapshot;

  // 部門文字採 API 原值呈現，保留歷史快照中合法的特殊字元。
  return (
    <>
      <dl>
        <dt>主旨</dt>
        <dd>{snapshot.subject || "未填主旨"}</dd>
        <dt>整單用途</dt>
        <dd className="requisition-purpose">
          {snapshot.purpose || "未填用途"}
        </dd>
        <RequisitionRoundFields round={round} />
        <dt>預估申請金額</dt>
        <dd>{formatDraftAmount(snapshot.estimatedAmount)}</dd>
      </dl>
      <RequisitionLines lines={snapshot.lines} />
    </>
  );
}

// 純顯示 API 已授權的目前內容與輪次，不從設定重建資格或歷史資料。
function RequisitionContent({ detail }: { detail: RequisitionDetailDto }) {
  const { round, history } = detail;

  // 撤回期間的頂層內容已可編輯，歷史送審版本改由獨立區塊呈現。
  return (
    <>
      <dl>
        <dt>主旨</dt>
        <dd>{detail.subject || "未填主旨"}</dd>
        <dt>整單用途</dt>
        <dd className="requisition-purpose">{detail.purpose || "未填用途"}</dd>
        <dt>申請人</dt>
        <dd>{detail.applicant.userName}</dd>
        <dt>目前狀態</dt>
        <dd>{statusNames[detail.status]}</dd>
        <dt>預估申請金額</dt>
        <dd>{formatDraftAmount(detail.estimatedAmount)}</dd>
        {round && detail.status !== "withdrawn" ? (
          <RequisitionRoundFields round={round} />
        ) : null}
      </dl>
      {detail.status === "withdrawn" ? (
        <p className="hint">
          請至「本人請購草稿」開啟單號 {detail.number}{" "}
          編輯並保存，再回到「請購查閱與簽核」使用「送出簽核」重送。
        </p>
      ) : null}
      {detail.paused ? (
        <p className="feedback error" role="alert">
          簽核已暫停，保留目前關卡。請聯絡管理員確認尚未完成關卡的審核人資格。
        </p>
      ) : null}
      {(detail.status === "draft" || detail.status === "withdrawn") &&
      !detail.canSubmit ? (
        <p className="hint">
          {detail.isComplete
            ? "目前無法送審。請管理員確認申請人的部門及預設審核人設定。"
            : "請先在本人請購草稿填妥主旨、用途與 1–100 筆有效明細並保存。"}
        </p>
      ) : null}
      {detail.status === "withdrawn" ? <h4>目前可編輯內容</h4> : null}
      <RequisitionLines lines={detail.lines} />
      {detail.previousRounds.length ? (
        <>
          <h4>先前送審輪次</h4>
          {detail.previousRounds.map(
            // 舊輪次只依伺服器回傳的可見範圍呈現，並保留瀏覽器原生展開操作。
            (snapshot) => (
              <details key={snapshot.round.number}>
                <summary>第 {snapshot.round.number} 輪送審快照</summary>
                <RequisitionRoundSnapshot snapshot={snapshot} />
              </details>
            ),
          )}
        </>
      ) : null}
      <h4>操作歷程</h4>
      {history.length ? (
        <ol aria-label="操作歷程">
          {history.map(
            // 每輪的送審和撤回沒有關卡，以事件種類區分；審核決定以關卡區分。
            ({ roundNumber, kind, stage, actor, occurredAt }) => (
              <li key={`${roundNumber}-${stage ?? kind}`}>
                第 {roundNumber} 輪{" · "}
                {kind === "submitted"
                  ? "送出簽核"
                  : kind === "withdrawn"
                    ? "撤回修改"
                    : `${stage === "manager" ? "主管" : "財務"}${kind === "approved" ? "核准" : "駁回"}`}
                {" · "}
                {actor.userName}
                {" · "}
                <RequisitionTime value={occurredAt} />
              </li>
            ),
          )}
        </ol>
      ) : (
        <p className="hint">目前沒有簽核歷程。</p>
      )}
    </>
  );
}

// 使用 App 的同步閘門與身分配對權杖，讓簽核、查詢和切換面板互斥。
export function RequisitionApprovals({
  getToken,
  pending,
  beginOperation,
  finishOperation,
  onSessionInvalid,
  readbacks,
  onReadbackChange,
}: Props) {
  // 保留獨立的寫入提示與讀取錯誤，避免成功後的讀取失敗改寫結果。
  const [list, setList] = useState<RequisitionSummaryDto[] | null>(null);
  const [detail, setDetail] = useState<RequisitionDetailDto | null>(null);
  const [retryId, setRetryId] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ text: string; error: boolean } | null>(
    null,
  );
  const [readError, setReadError] = useState("");
  const listHeading = useRef<HTMLHeadingElement>(null);
  const detailHeading = useRef<HTMLHeadingElement>(null);
  const detailId = detail?.id;

  // 只有清單與單筆之間切換時移動焦點，背景讀取不重設鍵盤位置。
  useEffect(() => {
    (detailId ? detailHeading : listHeading).current?.focus();
  }, [detailId]);

  // 共用清單 GET；重新載入後只顯示最新可見摘要。
  const loadList = useCallback(
    async (signal?: AbortSignal) => {
      // 先清除舊摘要；讀取失敗時不繼續展示可能已撤權的列表。
      setList(null);
      setReadError("");
      try {
        const next = await readRequisitions(
          getToken(),
          signal && AbortSignal.any([signal, AbortSignal.timeout(15000)]),
        );
        if (!signal?.aborted) setList(next);
      } catch (error) {
        // 卸載取消不需要提示；身分拒絕則從 App 恢復。
        if (signal?.aborted) return;
        if (requiresSessionCheck(error)) {
          onSessionInvalid(`${error.message} 請重新確認身分。`);
        } else {
          setReadError("無法讀取請購列表，請重新讀取。");
        }
      }
    },
    [getToken, onSessionInvalid],
  );

  // 面板掛載只讀清單；較晚回應不能更新已卸載的資料或解除下一輪的閘門。
  useEffect(() => {
    // 由本輪清單讀取取得閘門並持有取消控制器。
    if (!beginOperation()) return;
    const controller = new AbortController();
    let waiting = true;
    void loadList(controller.signal).finally(
      // 若清理已解除閘門，完成回呼不再動它。
      () => {
        if (!waiting) return;
        waiting = false;
        finishOperation();
      },
    );

    // 只有尚未完成的閘門需要在清理時解除。
    return () => {
      controller.abort();
      if (!waiting) return;
      waiting = false;
      finishOperation();
    };
  }, [beginOperation, finishOperation, loadList]);

  // 回列表時重新套用查閱資格，避免繼續使用離開時的角色或狀態。
  async function showList() {
    if (!beginOperation()) return;
    try {
      // 清除目前選擇及該單的重讀入口；跨面板的未確認標記另存。
      setDetail(null);
      setRetryId(null);
      await loadList();
    } finally {
      finishOperation();
    }
  }

  // 以目前授權讀回單筆；暫時讀取失敗保留入口，無法核對原輪時明示限制。
  async function showDetail(id: string) {
    if (!beginOperation()) return;
    setReadError("");
    if (detailId !== id) {
      // 切換單筆時先移除原單，避免失敗後用新單的重讀狀態解除原單的限制。
      setDetail(null);
      setNotice(null);
    }
    const readback = readbacks[id];
    try {
      // 版本前進時顯示新明細，但原輪不在已授權歷程內就無法核對未知結果。
      const next = await readRequisition(getToken(), id);
      if (readback && readback.version !== next.version) {
        const cannotConfirm =
          !readback.confirmed &&
          readback.roundNumber !== undefined &&
          !next.history.some(
            // 只用這次回應可見的歷程，不另查舊輪。
            (event) => event.roundNumber === readback.roundNumber,
          );
        onReadbackChange(id, null);
        setNotice({
          text: readback.confirmed
            ? `已${actionNames[readback.confirmed]}，並讀取最新狀態。`
            : cannotConfirm
              ? "已讀取最新狀態，但目前可見的歷程無法確認先前操作結果。"
              : "已讀取最新狀態，請核對操作歷程。",
          error: cannotConfirm,
        });
      } else if (retryId === id && notice?.error) {
        // 已讀回時撤下舊版本或資格拒絕指引，操作由新明細決定。
        setNotice(null);
      }
      setDetail(next);
      setRetryId(null);
      if (detailId === id) detailHeading.current?.focus();
    } catch (error) {
      // 防偽或帳號失效時卸載畫面；暫時性讀取錯誤仍可從結果入口恢復。
      if (requiresSessionCheck(error)) {
        onSessionInvalid(`${error.message} 請重新確認身分。`);
      } else if (unavailable(error)) {
        // 此單已無法讀回，移除同筆入口；查無不代表先前寫入被拒絕。
        setDetail(null);
        setList(null);
        setRetryId(null);
        if (readback) onReadbackChange(id, null);
        if (readback?.confirmed) {
          setNotice({
            text: `已${actionNames[readback.confirmed]}。`,
            error: false,
          });
        }
        setReadError(
          readback && !readback.confirmed
            ? "目前無法讀取這筆請購，無法確認先前操作結果。請重新讀取請購列表。"
            : "找不到指定的請購單，請重新讀取請購列表。",
        );
      } else {
        setRetryId(id);
        setReadError("無法讀取請購結果，請重新讀取。");
      }
    } finally {
      finishOperation();
    }
  }

  // 送審、撤回或審核只從目前明細發出一次；後端驗證狀態、版本與當下資格。
  async function act(action: ApprovalAction) {
    // 重新讀取前不沿用已被拒絕或未確認的版本。
    if (!detail || !canAct) return;
    if (!beginOperation()) return;
    setNotice(null);
    setReadError("");
    // 記下送出當時的輪次，供未知結果只從已授權的歷程核對。
    const readback = {
      version: detail.version,
      roundNumber: detail.round?.number,
    };
    try {
      // 以寫入回應顯示確認結果；即使回應內容遺失，也保留已確認成功的事實。
      const next = await writeApproval(getToken(), action, detail);
      if (next) {
        onReadbackChange(detail.id, null);
        setNotice({ text: `已${actionNames[action]}。`, error: false });
        setDetail(next);
        detailHeading.current?.focus();
      } else {
        onReadbackChange(detail.id, {
          ...readback,
          confirmed: action,
        });
        setRetryId(detail.id);
      }
    } catch (error) {
      // 身分拒絕先卸載資料，其餘區分已拒絕、不可見與結果未知。重讀只執行 GET。
      if (requiresSessionCheck(error)) {
        onSessionInvalid(`${error.message} 請重新確認身分。`);
      } else if (unavailable(error)) {
        setDetail(null);
        setList(null);
        setReadError("找不到指定的請購單，請重新讀取請購列表。");
      } else {
        setRetryId(detail.id);
        if (
          error instanceof ApiError &&
          error.status < 500 &&
          writeRejections.has(error.code)
        ) {
          setNotice({
            text: `${error.message} 請先重新讀取結果。`,
            error: true,
          });
        } else {
          onReadbackChange(detail.id, readback);
        }
      }
    } finally {
      finishOperation();
    }
  }

  // 本次重讀與其他筆待核對結果各自保留，避免操作其他請購時清掉原提示。
  const pendingReadback = Object.entries(readbacks);
  const canAct =
    !pending &&
    retryId !== detailId &&
    !(detailId && readbacks[detailId]?.confirmed);
  return (
    <section className="requisition-approvals" aria-labelledby="approval-title">
      <h2 id="approval-title" tabIndex={-1} ref={listHeading}>
        請購查閱與簽核
      </h2>
      {detail ? (
        <h3 tabIndex={-1} ref={detailHeading}>
          請購明細 {detail.number}
        </h3>
      ) : null}
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
      {pendingReadback.map(
        // 已確認的寫入及未知結果各有獨立訊息，只顯示給原操作帳號。
        ([id, { confirmed }], index) => (
          <div key={id}>
            <p
              className={confirmed ? "feedback" : "feedback error"}
              role={confirmed ? "status" : "alert"}
            >
              {confirmed
                ? `已${actionNames[confirmed]}。目前無法讀取最新狀態，請重新讀取結果。`
                : "有請購操作結果未確認，請重新讀取結果並核對狀態與歷程。系統不會自動重送。"}
            </p>
            <button
              type="button"
              className="secondary"
              disabled={pending}
              onClick={
                // 僅使用原請購識別向伺服器確認資料仍可見。
                () => void showDetail(id)
              }
            >
              重新讀取結果{pendingReadback.length > 1 ? ` ${index + 1}` : ""}
            </button>
          </div>
        ),
      )}
      {retryId && !readbacks[retryId] ? (
        <button
          type="button"
          className="secondary"
          disabled={pending}
          onClick={
            // 讀回被拒絕的請購或讀取失敗的明細，不另發寫入。
            () => void showDetail(retryId)
          }
        >
          重新讀取結果
        </button>
      ) : null}
      {detail ? (
        <>
          <div className="requisition-actions">
            {detail.canWithdraw ? (
              <button
                type="button"
                disabled={!canAct}
                onClick={
                  // 僅依 API 的資格旗標顯示撤回，暫停狀態仍由伺服器判斷資格。
                  () => void act("withdraw")
                }
              >
                撤回修改
              </button>
            ) : null}
            {detail.canSubmit ? (
              <button
                type="button"
                disabled={!canAct}
                onClick={
                  // 送出已保存的版本，表單內容與審核人不由前端代填。
                  () => void act("submit")
                }
              >
                送出簽核
              </button>
            ) : null}
            {detail.canDecide ? (
              <>
                <button
                  type="button"
                  disabled={!canAct}
                  onClick={
                    // 核准的是目前由 API 判定可審核的關卡。
                    () => void act("approve")
                  }
                >
                  核准
                </button>
                <button
                  type="button"
                  className="secondary"
                  disabled={!canAct}
                  onClick={
                    // 駁回採獨立動作，後端決定是否能終止流程。
                    () => void act("reject")
                  }
                >
                  駁回
                </button>
              </>
            ) : null}
            <button
              type="button"
              className="secondary"
              disabled={pending}
              onClick={
                // 離開明細時重讀清單，保留先前尚未核對的操作。
                () => void showList()
              }
            >
              返回請購列表
            </button>
          </div>
          <RequisitionContent detail={detail} />
        </>
      ) : (
        <>
          <button
            type="button"
            className="secondary"
            disabled={pending}
            onClick={
              // 手動重讀列表不會重新送出先前的簽核操作。
              () => void showList()
            }
          >
            重新讀取請購列表
          </button>
          {list?.length ? (
            <ul>
              {list.map(
                // 摘要狀態由伺服器提供；能查看與有權作決定分開顯示。
                ({
                  id,
                  number,
                  subject,
                  applicant,
                  status,
                  estimatedAmount,
                  paused,
                  canDecide,
                }) => (
                  <li key={id}>
                    <p>{subject || "未填主旨"}</p>
                    <p className="hint">
                      {applicant.userName}
                      {" · "}
                      {statusNames[status]}
                      {" · "}
                      {formatDraftAmount(estimatedAmount)}
                      {paused ? " · 簽核暫停" : canDecide ? " · 待您簽核" : ""}
                    </p>
                    <button
                      type="button"
                      className="secondary"
                      disabled={pending}
                      onClick={
                        // 單號辨識項目，實際以系統識別取得即時明細。
                        () => void showDetail(id)
                      }
                    >
                      {number}
                    </button>
                  </li>
                ),
              )}
            </ul>
          ) : (
            <p>
              {list
                ? "目前沒有可查閱的請購。"
                : pending
                  ? "正在讀取請購…"
                  : "尚未取得請購列表。"}
            </p>
          )}
        </>
      )}
    </section>
  );
}
