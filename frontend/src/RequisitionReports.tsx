// 同一份已套用條件驅動送審清單、統計與 CSV；離開面板就卸載報表資料。
import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type FormEvent,
} from "react";
import {
  ApiError,
  readRequisitionCsv,
  readRequisitionReport,
  requiresSessionCheck,
  submittedStatuses,
  type RequisitionReportCriteria,
} from "./api";
import { RequisitionTime, statusNames } from "./RequisitionApprovals";
import { formatDraftAmount } from "./RequisitionDraftForm";

interface Props {
  getToken: () => string;
  pending: boolean;
  beginOperation: () => boolean;
  finishOperation: () => void;
  onSessionInvalid: (notice: string) => void;
}

const emptyCriteria: RequisitionReportCriteria = {
  startDate: "",
  endDate: "",
  departmentId: "",
  status: "",
};

const allDepartments = "全部部門";
const unavailableDepartment = "unavailable";

type Report = Awaited<ReturnType<typeof readRequisitionReport>> & {
  criteria: RequisitionReportCriteria;
};

type Department = Report["groups"][number]["department"];

// 只有收到受保護的 CSV 成功回應才建立下載連結，避免瀏覽器直接送出未配對的 GET。
function saveCsv(blob: Blob) {
  // 暫時掛載連結，讓瀏覽器依固定檔名開始儲存。
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = "requisitions.csv";
  document.body.append(link);
  try {
    link.click();
  } finally {
    // 讓下載先啟動，再釋放瀏覽器物件網址。
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }
}

// 協調業務報表的查詢及匯出，報表授權和金額計算皆使用後端結果。
export function RequisitionReports({
  getToken,
  pending,
  beginOperation,
  finishOperation,
  onSessionInvalid,
}: Props) {
  // 草擬條件與已顯示結果分開，只有同一份條件才能下載。
  const [criteria, setCriteria] = useState(emptyCriteria);
  const [report, setReport] = useState<Report | null>(null);
  const [feedback, setFeedback] = useState<{ text: string; error: boolean }>();
  const heading = useRef<HTMLHeadingElement>(null);

  // 身分或防偽拒絕交由 App 卸載業務畫面；一般錯誤留在報表供重試。
  const showError = useCallback(
    (error: unknown, fallback: string, notice = "") => {
      // 下載已開始時保留這項事實，與後續報表讀取的失敗分開。
      const prefix = notice ? `${notice} ` : "";
      if (requiresSessionCheck(error)) {
        onSessionInvalid(`${prefix}${error.message} 請重新確認身分。`);
      } else {
        // 一般讀取失敗仍可用相同條件重試。
        const message =
          error instanceof ApiError && error.code === "invalid_input"
            ? `${error.message} 請重新套用條件。`
            : fallback;
        setFeedback({
          text: `${prefix}${message}`,
          error: true,
        });
      }
    },
    [onSessionInvalid],
  );

  // 查詢期間先清除結果，讓失敗或換權時都不能繼續下載先前結果。
  const loadReport = useCallback(
    async (
      next: RequisitionReportCriteria,
      signal?: AbortSignal,
      notice = "",
    ) => {
      // 清空舊結果；從下載接續讀取時保留已開始的提示。
      setReport(null);
      setFeedback(notice ? { text: notice, error: false } : undefined);
      try {
        // 使用同一組條件、權杖及期限，同時讀取清單與統計。
        const result = await readRequisitionReport(
          getToken(),
          next,
          signal && AbortSignal.any([signal, AbortSignal.timeout(15000)]),
        );
        if (signal?.aborted) return;
        setReport({ ...result, criteria: next });
      } catch (error) {
        // 取消代表面板已離開；其餘失敗保留手動讀取入口。
        if (signal?.aborted) return;
        showError(error, "無法讀取報表，請重新套用條件。", notice);
      }
    },
    [getToken, showError],
  );

  // 進入報表時讀取可見範圍及部門；卸載即取消尚未完成的讀取。
  useEffect(() => {
    // 讓鍵盤從報表標題開始，並透過共用閘門阻擋操作交錯。
    heading.current?.focus();
    if (!beginOperation()) return;
    const controller = new AbortController();
    let waiting = true;
    void loadReport(emptyCriteria, controller.signal).finally(
      // 清理已交還閘門時，延遲回呼不能再動它。
      () => {
        if (!waiting) return;
        waiting = false;
        finishOperation();
      },
    );

    // 成功後閘門可能已由下一個操作持有；只有初次讀取未完成時才解除。
    return () => {
      controller.abort();
      if (!waiting) return;
      waiting = false;
      finishOperation();
    };
  }, [beginOperation, finishOperation, loadReport]);

  // 表單變更只更新草擬條件，避免選擇時立即送出查詢。
  function changeCriterion(
    field: keyof RequisitionReportCriteria,
    value: string,
  ) {
    // 以目前狀態合併單一欄位，保留其餘輸入。
    setCriteria((current) => ({ ...current, [field]: value }));
    setFeedback(undefined);
  }

  // 讀取或清除都走同一流程，使用共用閘門持有這次查詢。
  async function applyCriteria(next: RequisitionReportCriteria) {
    if (!beginOperation()) return;
    // 清除條件時，表單值和新查詢使用同一份物件。
    setCriteria(next);
    try {
      await loadReport(next);
    } finally {
      finishOperation();
    }
  }

  // 日期倒置時保留輸入讓使用者修正；日期界線仍由後端按臺灣時間驗證。
  function submitFilters(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    // 有設定兩個日期時才比較日曆順序。
    const { startDate, endDate } = criteria;
    const bounded = startDate !== "" && endDate !== "";
    if (bounded && startDate > endDate) {
      setFeedback({ text: "起日不得晚於迄日。", error: true });
      return;
    }
    void applyCriteria(criteria);
  }

  // 條件與畫面一致且取得共用閘門才開始下載，拒絕回應不能建立檔案。
  async function downloadCsv() {
    if (report?.criteria !== criteria || !beginOperation()) return;
    // 下載會重新驗證權限；請求開始後立即撤下先前的結果及部門選項。
    setReport(null);
    setFeedback({ text: "正在準備 CSV…", error: false });
    try {
      const blob = await readRequisitionCsv(getToken(), criteria);
      saveCsv(blob);
      // 下載已開始後只重讀相同條件的報表，讓畫面跟上目前授權。
      await loadReport(criteria, undefined, "已開始下載 CSV。");
    } catch (error) {
      // 失敗時先恢復目前報表，是否再下載由使用者決定。
      showError(error, "無法下載 CSV，請重新套用條件後再下載。");
    } finally {
      finishOperation();
    }
  }

  // 只從這次已授權群組去重產生部門選項，避免沿用角色變更前的部門。
  const departments = new Map<string, Department>();
  for (const { department } of report?.groups ?? [])
    departments.set(department.id, department);

  // 先前選擇不在結果內時只顯示通用標籤，查詢和下載仍可用原條件。
  const missingDepartment =
    criteria.departmentId !== "" && !departments.has(criteria.departmentId);
  const unapplied = report !== null && report.criteria !== criteria;

  // 同名異識別加上 ID，也保留「全部部門」給取消部門篩選的選項。
  const seenNames = new Set([allDepartments]);
  const duplicateNames = new Set<string>();
  for (const { name } of departments.values()) {
    if (seenNames.has(name)) duplicateNames.add(name);
    seenNames.add(name);
  }

  // 選項與統計使用相同的識別提示，使用者能對應選擇和結果。
  function departmentLabel({ id, name }: Department) {
    return duplicateNames.has(name) ? `${name} · ${id}` : name;
  }

  // 先顯示條件與狀態，再列出同一批資料的分組與每張請購。
  return (
    <section className="requisition-reports" aria-labelledby="reports-title">
      <h2 id="reports-title" ref={heading} tabIndex={-1}>
        請購統計與 CSV
      </h2>
      <p className="hint">
        依最近一次送審日期與送審部門查詢，起日與迄日皆包含。
        清單、統計與匯出依目前狀態及權限顯示，排除草稿與撤回修改中的請購。
        部門選項來自本次結果，可選「全部部門」後重新套用。
      </p>
      <form aria-label="報表條件" onSubmit={submitFilters}>
        <fieldset disabled={pending}>
          <label htmlFor="report-start">起日（臺灣時間）</label>
          <input
            id="report-start"
            type="date"
            value={criteria.startDate}
            onChange={
              // 日期欄提供日曆字串，保留臺灣時間解讀給 API。
              (event) => changeCriterion("startDate", event.target.value)
            }
          />
          <label htmlFor="report-end">迄日（臺灣時間）</label>
          <input
            id="report-end"
            type="date"
            value={criteria.endDate}
            onChange={
              // 迄日直接傳送使用者選定的日期，不從瀏覽器時區換算。
              (event) => changeCriterion("endDate", event.target.value)
            }
          />
          <label htmlFor="report-department">送審部門</label>
          <select
            id="report-department"
            value={
              missingDepartment ? unavailableDepartment : criteria.departmentId
            }
            onChange={
              // 名稱可能隨快照改變，篩選只使用部門識別。
              (event) => changeCriterion("departmentId", event.target.value)
            }
          >
            <option value="">{allDepartments}</option>
            {missingDepartment ? (
              <option value={unavailableDepartment} disabled>
                先前選擇的部門
              </option>
            ) : null}
            {Array.from(
              departments.values(),
              // 只顯示由這次報表結果取得的部門。
              (department) => (
                <option key={department.id} value={department.id}>
                  {departmentLabel(department)}
                </option>
              ),
            )}
          </select>
          <label htmlFor="report-status">目前狀態</label>
          <select
            id="report-status"
            value={criteria.status}
            onChange={
              // 送出後端接受的狀態值，顯示沿用簽核標籤。
              (event) => changeCriterion("status", event.target.value)
            }
          >
            <option value="">全部狀態</option>
            {submittedStatuses.map(
              // 暫停仍屬原待審關卡，因此只有四個已送審狀態。
              (status) => (
                <option key={status} value={status}>
                  {statusNames[status]}
                </option>
              ),
            )}
          </select>
          <div className="requisition-actions">
            <button type="submit">套用條件</button>
            <button
              type="button"
              className="secondary"
              onClick={
                // 清除也刷新完整可見範圍與部門選項。
                () => void applyCriteria(emptyCriteria)
              }
            >
              清除條件
            </button>
            <button
              type="button"
              className="secondary"
              disabled={!report || unapplied}
              onClick={downloadCsv}
            >
              下載 CSV
            </button>
          </div>
        </fieldset>
      </form>
      {unapplied ? <p role="status">條件尚未套用，以下仍為上次結果。</p> : null}
      {feedback ? (
        <p
          className={feedback.error ? "feedback error" : "feedback"}
          role={feedback.error ? "alert" : "status"}
        >
          {feedback.text}
        </p>
      ) : !report && pending ? (
        <p role="status">正在讀取報表…</p>
      ) : null}
      {report ? (
        <>
          <h3 id="report-groups">依送審部門與目前狀態統計</h3>
          {report.groups.length ? (
            <ul aria-labelledby="report-groups">
              {report.groups.map(
                // 件數及金額以後端群組為準，避免前端重新加總。
                ({ department, status, count, estimatedAmount }) => (
                  <li key={`${department.id}-${status}`}>
                    <p>
                      {departmentLabel(department)} · {statusNames[status]} ·{" "}
                      {count} 件
                    </p>
                    <p className="hint">
                      {status === "approved"
                        ? "已核准的預估申請金額"
                        : "預估申請金額"}{" "}
                      · {formatDraftAmount(estimatedAmount)}
                    </p>
                  </li>
                ),
              )}
            </ul>
          ) : (
            <p>沒有符合條件的統計。</p>
          )}
          <h3 id="report-list">符合條件的請購</h3>
          {report.list.length ? (
            <ul aria-labelledby="report-list">
              {report.list.map(
                // 使用最近送審快照；暫停可辨識但仍顯示原待審狀態。
                ({
                  id,
                  number,
                  subject,
                  applicant,
                  status,
                  estimatedAmount,
                  paused,
                  round,
                }) => (
                  <li key={id}>
                    <p>{subject}</p>
                    <p className="hint">
                      {number} · {applicant.userName} · {round.department.name}
                    </p>
                    <p className="hint">
                      {statusNames[status]}
                      {paused ? " · 簽核暫停" : ""} ·{" "}
                      {formatDraftAmount(estimatedAmount)} ·{" "}
                      <RequisitionTime value={round.submittedAt} />
                    </p>
                  </li>
                ),
              )}
            </ul>
          ) : (
            <p>沒有符合條件的請購。</p>
          )}
        </>
      ) : null}
    </section>
  );
}
