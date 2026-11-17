// 顯示已授權的請購、輪次與歷程；每次簽核只使用畫面讀到的版本。
import { useCallback, useEffect, useRef, useState } from "react";
import {
  ApiError,
  readRequisition,
  readRequisitions,
  requiresSessionCheck,
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

export const statusNames = {
  draft: "草稿",
  withdrawn: "撤回修改中",
  manager_pending: "主管待審",
  finance_pending: "財務待審",
  approved: "核准結案",
  rejected: "駁回結案",
};

const emptyFilters = { query: "", status: "" };

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

// 一般請購使用同一查無錯誤表示不存在或不可見，兩者都不得留下原明細。
function unavailable(error: unknown) {
  return (
    error instanceof ApiError &&
    error.status === 404 &&
    error.code === "not_found"
  );
}

// 所有輪次時間以展示環境的臺灣時區呈現，同時保留原機器可讀時間。
export function RequisitionTime({ value }: { value: string }) {
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
      <div>
        <dt>送審部門</dt>
        <dd>{round.department.name}</dd>
      </div>
      <div>
        <dt>送審輪次</dt>
        <dd>第 {round.number} 輪</dd>
      </div>
      <div>
        <dt>送審時間</dt>
        <dd>
          <RequisitionTime value={round.submittedAt} />
        </dd>
      </div>
      <div>
        <dt>主管審核者</dt>
        <dd>{round.manager.userName}</dd>
      </div>
      <div>
        <dt>財務審核者</dt>
        <dd>{round.finance.userName}</dd>
      </div>
    </>
  );
}

// 依伺服器順序顯示已保存明細，不重新計算金額或改排品項。
function RequisitionLines({ lines }: { lines: RequisitionDetailDto["lines"] }) {
  return (
    <>
      {lines.length ? (
        <div
          className="table-frame"
          role="region"
          aria-label="請購品項"
          tabIndex={0}
        >
          <table className="line-table">
            <thead>
              <tr>
                <th scope="col">品名</th>
                <th scope="col">單位</th>
                <th scope="col" className="quantity">
                  數量
                </th>
                <th scope="col" className="money">
                  預估單價
                </th>
                <th scope="col" className="money">
                  預估小計
                </th>
              </tr>
            </thead>
            <tbody>
              {lines.map(
                // 明細保留 API 的順序，以索引識別同一份已讀取快照中的位置。
                (line, index) => (
                  <tr key={index}>
                    <td>{line.itemName || "未填品名"}</td>
                    <td>{line.unit || "未填單位"}</td>
                    <td className="quantity">{line.quantity ?? "未填數量"}</td>
                    <td className="money">
                      {formatDraftAmount(line.unitPrice)}
                    </td>
                    <td className="money">
                      {formatDraftAmount(line.estimatedAmount)}
                    </td>
                  </tr>
                ),
              )}
            </tbody>
          </table>
        </div>
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
      <dl className="document-fields">
        <div>
          <dt>主旨</dt>
          <dd>{snapshot.subject || "未填主旨"}</dd>
        </div>
        <div>
          <dt>整單用途</dt>
          <dd className="requisition-purpose">
            {snapshot.purpose || "未填用途"}
          </dd>
        </div>
        <RequisitionRoundFields round={round} />
        <div>
          <dt>預估申請金額</dt>
          <dd>{formatDraftAmount(snapshot.estimatedAmount)}</dd>
        </div>
      </dl>
      <h5>請購明細</h5>
      <RequisitionLines lines={snapshot.lines} />
    </>
  );
}

// 純顯示 API 已授權的目前內容與輪次，不從設定重建資格或歷史資料。
function RequisitionContent({ detail }: { detail: RequisitionDetailDto }) {
  const { round, history } = detail;
  // 只有內容還可編輯時，才需要顯示送審受阻的說明。
  const editable = detail.status === "draft" || detail.status === "withdrawn";

  // 撤回期間的頂層內容已可編輯，歷史送審版本改由獨立區塊呈現。
  return (
    <>
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
      {editable && !detail.canSubmit ? (
        <p className="hint">
          {detail.isComplete
            ? "目前無法送審。請管理員確認申請人的部門及預設審核人設定。"
            : "請先在本人請購草稿填妥主旨、用途與 1–100 筆有效明細並保存。"}
        </p>
      ) : null}
      <div className="detail-grid">
        <div>
          <h3>
            {detail.status === "withdrawn" ? "目前可編輯內容" : "請購資料"}
          </h3>
          <dl className="document-fields">
            <div>
              <dt>申請人</dt>
              <dd>{detail.applicant.userName}</dd>
            </div>
            {round && detail.status !== "withdrawn" ? (
              <RequisitionRoundFields round={round} />
            ) : null}
            <div>
              <dt>整單用途</dt>
              <dd className="requisition-purpose">
                {detail.purpose || "未填用途"}
              </dd>
            </div>
          </dl>
          <h3>請購明細</h3>
          <RequisitionLines lines={detail.lines} />
          <dl className="document-total">
            <dt>預估申請金額</dt>
            <dd>{formatDraftAmount(detail.estimatedAmount)}</dd>
          </dl>
          {detail.previousRounds.length ? (
            <>
              <h3>先前送審輪次</h3>
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
        </div>
        <div className="detail-history">
          <h3>操作歷程</h3>
          {history.length ? (
            <ol className="timeline" aria-label="操作歷程" role="list">
              {history.map(
                // 同輪同關可多次改派；用事件順序區分唯讀且無事件識別的紀錄。
                (
                  { roundNumber, kind, stage, actor, occurredAt, reassignment },
                  index,
                ) => (
                  <li key={`${roundNumber}-${kind}-${stage}-${index}`}>
                    <strong>
                      {kind === "submitted"
                        ? "送出簽核"
                        : kind === "withdrawn"
                          ? "撤回修改"
                          : `${stage === "manager" ? "主管" : "財務"}${kind === "reassigned" ? "改派" : kind === "approved" ? "核准" : "駁回"}`}
                    </strong>
                    <p>
                      {actor.userName}
                      {" · "}
                      <RequisitionTime value={occurredAt} />
                    </p>
                    <p>第 {roundNumber} 輪</p>
                    {kind === "reassigned" && reassignment ? (
                      <dl>
                        <dt>原審核者</dt>
                        <dd>{reassignment.previousReviewer.userName}</dd>
                        <dt>接手審核者</dt>
                        <dd>{reassignment.newReviewer.userName}</dd>
                        <dt>改派原因</dt>
                        <dd className="requisition-purpose">
                          {reassignment.reason}
                        </dd>
                      </dl>
                    ) : null}
                  </li>
                ),
              )}
            </ol>
          ) : (
            <p className="hint">目前沒有簽核歷程。</p>
          )}
        </div>
      </div>
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
  const [openingDetail, setOpeningDetail] = useState(false);
  const [retryId, setRetryId] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ text: string; error: boolean } | null>(
    null,
  );
  const [readError, setReadError] = useState("");
  // 欄位編輯與已套用條件分開保存，返回清單或手動重讀不會套用未送出的值。
  const [filters, setFilters] = useState(emptyFilters);
  const [appliedFilters, setAppliedFilters] = useState(emptyFilters);
  const listHeading = useRef<HTMLHeadingElement>(null);
  const detailHeading = useRef<HTMLHeadingElement>(null);
  const detailId = detail?.id;

  // 切到載入、明細或返回清單時聚焦新標題，同一筆的背景讀取不改變焦點。
  useEffect(() => {
    (openingDetail || detailId ? detailHeading : listHeading).current?.focus();
  }, [detailId, openingDetail]);

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

  // 套用條件、返回或重讀皆重新檢查目前查閱資格。
  async function showList(nextFilters?: typeof emptyFilters) {
    if (!beginOperation()) return;
    try {
      // 只有明確套用或清除才取代生效條件，其他清單重讀沿用原條件。
      if (nextFilters) {
        setFilters(nextFilters);
        setAppliedFilters(nextFilters);
      }
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
      // 切換單筆時立刻離開清單或原單，避免混用兩筆資料或把清單留在明細頁。
      setOpeningDetail(true);
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
      // 初次讀取失敗時回到清單側顯示原有的重讀入口。
      setOpeningDetail(false);
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

  // 只比對這次授權讀回的摘要。狀態沿用清單原值，文字只查原始單號或主旨。
  const query = appliedFilters.query.trim().toLowerCase();
  const matchingList =
    query || appliedFilters.status
      ? list?.filter(({ number, subject, status }) => {
          const matchesStatus =
            !appliedFilters.status || status === appliedFilters.status;
          const matchesQuery =
            !query ||
            number.toLowerCase().includes(query) ||
            subject?.toLowerCase().includes(query);
          return matchesStatus && matchesQuery;
        })
      : list;
  const unapplied =
    list !== null &&
    (filters.query !== appliedFilters.query ||
      filters.status !== appliedFilters.status);

  // 本次重讀與其他筆待核對結果各自保留，避免操作其他請購時清掉原提示。
  const pendingReadback = Object.entries(readbacks);
  const canAct =
    !pending &&
    retryId !== detailId &&
    !(detailId && readbacks[detailId]?.confirmed);
  return (
    <section className="requisition-approvals" aria-label="請購查閱與簽核">
      {detail ? (
        <>
          <button
            type="button"
            className="text-control detail-back"
            disabled={pending}
            onClick={
              // 離開明細時重讀清單，保留先前尚未核對的操作。
              () => void showList()
            }
          >
            <span aria-hidden="true">← </span>返回請購列表
          </button>
          <div className="detail-heading">
            <div>
              <h2 tabIndex={-1} ref={detailHeading}>
                {detail.subject || "未填主旨"}
              </h2>
              <p className="identifier">{detail.number}</p>
            </div>
            <p className={`requisition-status status-${detail.status}`}>
              {statusNames[detail.status]}
            </p>
          </div>
        </>
      ) : openingDetail ? (
        <div className="page-heading">
          <h2 tabIndex={-1} ref={detailHeading}>
            請購明細
          </h2>
          <p className="hint" role="status">
            正在讀取請購明細…
          </p>
        </div>
      ) : (
        <div className="page-heading">
          <h2 tabIndex={-1} ref={listHeading}>
            請購查閱與簽核
          </h2>
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
        </div>
      )}
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
          </div>
          <RequisitionContent detail={detail} />
        </>
      ) : !openingDetail ? (
        <>
          <form
            aria-label="請購搜尋條件"
            onSubmit={
              // Enter 與套用按鈕都重讀可見清單，再依這次條件篩選。
              (event) => {
                event.preventDefault();
                void showList(filters);
              }
            }
          >
            <fieldset className="approval-filter-grid" disabled={pending}>
              <div className="approval-filter-field">
                <label htmlFor="approval-query">主旨或單號</label>
                <input
                  id="approval-query"
                  type="search"
                  value={filters.query}
                  onChange={
                    // 保留原輸入，只有比對時才去除空白並統一大小寫。
                    (event) =>
                      setFilters({ ...filters, query: event.target.value })
                  }
                />
              </div>
              <div className="approval-filter-field">
                <label htmlFor="approval-status">目前狀態</label>
                <select
                  id="approval-status"
                  value={filters.status}
                  onChange={
                    // 使用與摘要相同的狀態值，選擇後等套用才改變清單。
                    (event) =>
                      setFilters({ ...filters, status: event.target.value })
                  }
                >
                  <option value="">全部狀態</option>
                  {Object.entries(statusNames).map(
                    // 清單包含草稿與撤回修改中，選項沿用完整的簽核狀態名稱。
                    ([status, label]) => (
                      <option key={status} value={status}>
                        {label}
                      </option>
                    ),
                  )}
                </select>
              </div>
              <div className="requisition-actions">
                <button type="submit">套用條件</button>
                <button
                  type="button"
                  className="secondary"
                  onClick={
                    // 清空欄位及已套用條件後讀回完整的可見清單。
                    () => void showList(emptyFilters)
                  }
                >
                  清除條件
                </button>
              </div>
            </fieldset>
          </form>
          {unapplied ? (
            <p role="status">條件尚未套用，以下仍為上次結果。</p>
          ) : null}
          {matchingList?.length ? (
            <div
              className="table-frame"
              role="region"
              aria-label="請購列表"
              tabIndex={0}
            >
              <table className="approval-table">
                <thead>
                  <tr>
                    <th scope="col">主旨 / 單號</th>
                    <th scope="col">申請人</th>
                    <th scope="col">目前狀態</th>
                    <th scope="col" className="money">
                      預估申請金額
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {matchingList.map(
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
                      <tr key={id}>
                        <td>
                          <button
                            type="button"
                            className="text-control approval-open"
                            disabled={pending}
                            onClick={
                              // 顯示完整單號，實際以系統識別取得即時明細。
                              () => void showDetail(id)
                            }
                          >
                            <span className="subject">
                              {subject || "未填主旨"}
                            </span>
                            <span className="identifier">{number}</span>
                          </button>
                        </td>
                        <td>{applicant.userName}</td>
                        <td>
                          <span
                            className={`requisition-status status-${status}`}
                          >
                            {statusNames[status]}
                          </span>
                          {paused || canDecide ? (
                            <span className="status-note">
                              {paused ? "簽核暫停" : "待您簽核"}
                            </span>
                          ) : null}
                        </td>
                        <td className="money">
                          {formatDraftAmount(estimatedAmount)}
                          <span className="row-chevron" aria-hidden="true">
                            ›
                          </span>
                        </td>
                      </tr>
                    ),
                  )}
                </tbody>
              </table>
            </div>
          ) : (
            <p>
              {list
                ? list.length
                  ? "沒有符合搜尋條件的請購。"
                  : "目前沒有可查閱的請購。"
                : pending
                  ? "正在讀取請購…"
                  : "尚未取得請購列表。"}
            </p>
          )}
        </>
      ) : null}
    </section>
  );
}
