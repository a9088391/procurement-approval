// 組織快照及寫入讀回集中在管理入口；身分確認前不掛載設定表單。
import { useCallback, useEffect, useRef, useState } from "react";
import {
  ApiError,
  readOrganization,
  writeOrganization,
  type OrganizationDto,
} from "./api";
import {
  OrganizationForms,
  type SubmitOrganization,
} from "./OrganizationForms";

interface Props {
  getToken: () => string;
  pending: boolean;
  beginOperation: () => boolean;
  finishOperation: () => void;
  onSessionInvalid: (notice: string, confirmedWrite?: string) => void;
  unknownResult: boolean;
  onUnknownResult: () => void;
}

const unknownHint =
  "先前有組織操作結果未確認，可能已生效。請比對重新讀取的設定。若曾重設密碼，請由該帳號確認臨時密碼是否可用。系統不會自動重送。";

// 登入、防偽或管理資格已失效時，舊資料與寫入控制必須一起卸載。
function requiresSessionCheck(error: unknown): error is ApiError {
  return (
    error instanceof ApiError &&
    (error.status === 401 ||
      error.status === 403 ||
      error.code === "invalid_csrf")
  );
}

// 管理員的組織設定畫面與 App 共用操作閘門，避免身分切換與舊資料讀回交錯。
export function Organization({
  getToken,
  pending,
  beginOperation,
  finishOperation,
  onSessionInvalid,
  unknownResult,
  onUnknownResult,
}: Props) {
  // 讀回成功前保留原快照供檢視；需要重讀時停用所有寫入與選擇控制。
  const [readback, setReadback] = useState<{
    snapshot: OrganizationDto;
    revision: number;
  } | null>(null);
  const [requiresRead, setRequiresRead] = useState(false);
  const [notice, setNotice] = useState<{
    text: string;
    error: boolean;
    confirmedWrite?: string;
  } | null>(null);
  const feedback = useRef<HTMLDivElement>(null);

  // 初始讀取、手動重讀及保存後讀回使用同一條錯誤恢復路徑。
  const loadSettings = useCallback(
    async (confirmedWrite = "", signal?: AbortSignal) => {
      try {
        // 逾時視為讀取失敗；畫面已卸載時不採用較晚抵達的結果。
        const next = await readOrganization(
          signal && AbortSignal.any([signal, AbortSignal.timeout(15000)]),
        );
        if (signal?.aborted) return;
        setReadback(
          // 每次成功讀回後重建草稿，避免已移除的選項被表單默默改成其他值。
          (previous) => ({
            snapshot: next,
            revision: (previous?.revision ?? 0) + 1,
          }),
        );
        setRequiresRead(false);
        setNotice({
          text: confirmedWrite
            ? `${confirmedWrite}，已讀取目前設定。`
            : "已讀取目前設定。",
          error: false,
        });
      } catch (error) {
        // 保留已確認的寫入狀態，不能把接續讀取失敗解讀為未保存。
        if (signal?.aborted) return;
        const prefix = confirmedWrite
          ? `${confirmedWrite}，但無法讀回設定。`
          : "";
        if (requiresSessionCheck(error)) {
          // 分別交給 App 保存確認及錯誤指引，恢復身分時只移除指引。
          onSessionInvalid(
            `${prefix}${error.message} 請重新確認身分。`,
            confirmedWrite,
          );
          return;
        }

        // 連線或讀取失敗時保留草稿，直到安全重讀成功才重新開放編輯。
        setRequiresRead(true);
        setNotice({
          text: `${prefix || "無法讀取組織設定。"}請重新讀取組織設定。`,
          error: true,
          confirmedWrite,
        });
      }
    },
    [onSessionInvalid],
  );

  // 首次掛載只執行 GET；同一輪驗證完成前禁止切換身分或開始寫入。
  useEffect(() => {
    // 共用閘門只由持有它的操作釋放，卸載會取消讀取而不覆蓋較新的狀態。
    if (!beginOperation()) return;
    const controller = new AbortController();
    let waiting = true;
    void loadSettings("", controller.signal).finally(
      // 回應有效時釋放閘門；若已取消，改由清理函式釋放。
      () => {
        if (!waiting) return;
        waiting = false;
        finishOperation();
      },
    );

    // 清理未完成讀取，避免卸載後的 Promise 影響下一輪身分。
    return () => {
      controller.abort();
      if (!waiting) return;
      waiting = false;
      finishOperation();
    };
  }, [beginOperation, finishOperation, loadSettings]);

  // 重讀只發出 GET；未知寫入提示獨立保留，不因讀到一份快照而宣告寫入成功。
  async function reload() {
    // 將焦點留在回饋附近，讓鍵盤使用者可繼續選擇重讀或往下檢視。
    if (!beginOperation()) return;
    feedback.current?.focus();

    // 完成這輪讀取後才允許下一個身分或組織操作。
    try {
      await loadSettings(notice?.confirmedWrite);
    } finally {
      finishOperation();
    }
  }

  // 保存讀取時的版本及本次輸入；只有收到成功回應才自動讀取保存結果。
  const submit: SubmitOrganization = async (
    event,
    buildWrite,
    confirmation,
  ) => {
    // 在表單還可讀取時取出必要欄位，並立即清除畫面上的臨時密碼。
    event.preventDefault();
    if (requiresRead || !beginOperation()) return;
    const form = event.currentTarget;
    const values = new FormData(form);
    const password = form.querySelector<HTMLInputElement>(
      'input[type="password"]',
    );
    if (password) password.value = "";
    feedback.current?.focus();
    setNotice(null);

    try {
      // 臨時密碼只從表單快照交給這一次請求，避免留在可編輯狀態中。
      const write = writeOrganization(getToken(), buildWrite(values));
      if (password) values.delete(password.name);
      await write;

      // 204 已確認保存；等讀回成功才重建表單，期間不允許重複送出。
      await loadSettings(confirmation);
    } catch (error) {
      // 身分或權杖不可用時立刻移除設定，防止舊資料在恢復期間可見。
      if (requiresSessionCheck(error)) {
        onSessionInvalid(`${error.message} 請重新確認身分後，由您再次操作。`);
        return;
      }

      // 只將契約明定的拒絕視為未保存；版本過期或目標消失須先重讀。
      if (error instanceof ApiError && error.status < 500) {
        const needsReload =
          error.code === "stale_version" || error.status === 404;
        const invalid =
          error.code === "invalid_input" ||
          error.code === "duplicate_user_name";
        if (needsReload || invalid) {
          setRequiresRead(needsReload);
          setNotice({
            text: needsReload
              ? `${error.message} 請使用「重新讀取組織設定」。`
              : error.message,
            error: true,
          });
          return;
        }
      }

      // 網路、5xx 或無法辨識的回應可能發生在提交後；先阻止再送，等待人工比對。
      onUnknownResult();
      setRequiresRead(true);
    } finally {
      // 無論寫入或讀回結果，只有這輪操作完成才能釋放共用閘門。
      finishOperation();
    }
  };

  // 成功讀取才顯示表單；恢復期間停用寫入但保留可供比對的快照。
  return (
    <div
      className="organization"
      role="region"
      aria-labelledby="organization-title"
    >
      <h2 id="organization-title">組織管理</h2>
      <div ref={feedback} tabIndex={-1}>
        {unknownResult ? (
          // 先前未決的操作持續顯示，後續讀取或保存各自回報結果。
          <p className="feedback error" role="alert">
            {unknownHint}
          </p>
        ) : null}
        {notice ? (
          <p
            className={notice.error ? "feedback error" : "feedback"}
            role={notice.error ? "alert" : "status"}
          >
            {notice.text}
          </p>
        ) : null}
        {pending ? <p role="status">正在處理…</p> : null}
      </div>
      <button
        type="button"
        className="secondary"
        disabled={pending}
        onClick={
          // 僅重讀設定，不沿用或重送前次提交。
          () => void reload()
        }
      >
        重新讀取組織設定
      </button>
      <p className="hint">讀取設定成功後，表單會以最新保存值重建。</p>
      {readback ? (
        <OrganizationForms
          snapshot={readback.snapshot}
          revision={readback.revision}
          disabled={pending || requiresRead}
          onSubmit={submit}
        />
      ) : null}
    </div>
  );
}
