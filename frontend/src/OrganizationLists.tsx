// 組織管理在同一資源內切換清單與表單，保存與重讀仍共用管理入口。
import { useCallback, useRef, useState, type FormEvent } from "react";
import type { DepartmentDto, OrganizationDto } from "./api";
import {
  CreateAccount,
  CreateDepartment,
  EditAccount,
  EditDepartment,
  type SubmitOrganization,
} from "./OrganizationForms";

interface Props {
  snapshot: OrganizationDto;
  revision: number;
  disabled: boolean;
  pending: boolean;
  onNavigate: () => void;
  onSubmit: SubmitOrganization;
}

const sections = [
  { id: "departments", label: "部門管理", item: "部門", list: "部門清單" },
  {
    id: "accounts",
    label: "業務帳號管理",
    item: "業務帳號",
    list: "業務帳號清單",
  },
] as const;

type Section = (typeof sections)[number];
type Editor = { mode: "create" } | { mode: "edit"; id: string };

interface ListRow {
  id: string;
  name: string;
  active: boolean;
  version: string;
  department?: string;
  roles?: string;
}

// 同一時間只掛載清單或一份表單，避免編輯時誤觸其他資源的操作。
export function OrganizationLists({
  snapshot,
  revision,
  disabled,
  pending,
  onNavigate,
  onSubmit,
}: Props) {
  const [section, setSection] = useState<Section>(sections[0]);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [selection, setSelection] = useState<{
    revision: number;
    ids: Set<string>;
  } | null>(null);
  const shouldFocusList = useRef(false);
  const { departments, accounts } = snapshot;
  const managesDepartments = section.id === "departments";

  // 以目前快照識別編輯目標，找不到時即退回清單，避免沿用舊資料。
  const editId = editor?.mode === "edit" ? editor.id : null;
  const department = managesDepartments
    ? departments.find((item) => item.id === editId)
    : undefined;
  const account = managesDepartments
    ? undefined
    : accounts.find((item) => item.id === editId);
  // 過期表單重新讀取後若目標消失，結束編輯，避免後續讀回重新開啟舊目標。
  if (editId !== null && !department && !account) setEditor(null);
  const creating = editor?.mode === "create";
  const editorTitle = creating ? `新增${section.item}` : `編輯${section.item}`;

  // 同名部門以穩定識別區分；帳號清單依同一份部門快照顯示歸屬。
  const nameCounts = new Map<string, number>();
  const departmentsById = new Map<string, DepartmentDto>();
  for (const item of departments) {
    nameCounts.set(item.name, (nameCounts.get(item.name) ?? 0) + 1);
    departmentsById.set(item.id, item);
  }

  // 保留已停用部門的提示；清單有獨立狀態欄時只顯示名稱與必要識別。
  function departmentName(item: DepartmentDto, includeStatus = true) {
    const status = includeStatus && !item.active ? "（已停用）" : "";
    const identifier =
      (nameCounts.get(item.name) ?? 0) > 1 ? ` · ${item.id}` : "";
    return `${item.name}${status}${identifier}`;
  }

  // 帳號可同時擔任兩種角色；未分配的部門不代入其他選項。
  const rows: ListRow[] = managesDepartments
    ? departments.map((item) => ({
        id: item.id,
        name: departmentName(item, false),
        active: item.active,
        version: item.version,
      }))
    : accounts.map((item) => {
        const assigned = item.departmentId
          ? departmentsById.get(item.departmentId)
          : undefined;
        const roles = [
          ...(item.isManager ? ["主管"] : []),
          ...(item.isFinance ? ["財務"] : []),
        ];
        return {
          id: item.id,
          name: item.userName,
          active: item.active,
          version: item.version,
          department: assigned ? departmentName(assigned) : "未分配",
          roles: roles.join("、") || "未設定",
        };
      });
  const canCreate =
    managesDepartments || departments.some((item) => item.active);

  // 勾選只適用於目前清單版本，讀回錯誤時保留供比對，成功讀回後須重新選取。
  const selectedIds = selection?.revision === revision ? selection.ids : null;
  const eligibleRows = rows.filter((row) => row.active);
  const selectedRows = eligibleRows.filter((row) => selectedIds?.has(row.id));
  const allSelected =
    selectedRows.length > 0 && selectedRows.length === eligibleRows.length;

  // 單筆勾選保留其他列的狀態，並記住本次選取所依據的快照。
  function toggleRow(id: string, checked: boolean) {
    const ids = new Set(selectedIds ?? []);
    if (checked) ids.add(id);
    else ids.delete(id);
    setSelection({ revision, ids });
  }

  // 全選只包含目前啟用的列，停用列仍可由編輯區重新啟用。
  function toggleAll(checked: boolean) {
    setSelection(
      checked
        ? { revision, ids: new Set(eligibleRows.map((row) => row.id)) }
        : null,
    );
  }

  // 單筆與批次共用確認及保存流程；取消不改變勾選，也不送出請求。
  function deactivate(event: FormEvent<HTMLFormElement>, targets: ListRow[]) {
    // 即使鍵盤觸發表單也不允許原生表單送出，恢復期間只提供讀回。
    event.preventDefault();
    if (disabled || targets.length === 0) return;

    // 列出單筆目標或批次數量，並明確告知帳號停用會使既有登入失效。
    const target =
      targets.length === 1
        ? `「${targets[0].name}」`
        : `選取的 ${targets.length} 筆${section.item}`;
    const loginImpact = managesDepartments ? "" : "帳號現有的登入將失效。";
    const message = `確定停用${target}？會保留歷程，可在編輯畫面重新啟用。${loginImpact}`;
    if (!window.confirm(message)) return;

    // 以單一請求送出整批目標的原快照版本，只使用讀回結果更新畫面。
    const label =
      targets.length === 1
        ? `已停用${section.item}`
        : `已停用 ${targets.length} 筆${section.item}`;
    void onSubmit(
      event,
      () => ({
        path: `${section.id}/deactivate`,
        body: { items: targets.map(({ id, version }) => ({ id, version })) },
      }),
      label,
    );
  }

  // 進入編輯頁時聚焦標題；同頁讀回只更新表單，保留回饋處的焦點。
  const focusEditor = useCallback((heading: HTMLHeadingElement | null) => {
    heading?.focus();
  }, []);

  // 使用者離開表單時，清單會重新掛載，將焦點放到新的清單標題。
  const focusList = useCallback((heading: HTMLHeadingElement | null) => {
    if (!heading || !shouldFocusList.current) return;
    shouldFocusList.current = false;
    heading.focus();
  }, []);

  // 主動開啟表單時解除上一頁的訊息，保存後自動返回另保留回饋。
  function openEditor(next: Editor) {
    onNavigate();
    setEditor(next);
  }

  // 返回清單只卸載表單，不發出保存請求。
  function cancelEditor() {
    shouldFocusList.current = true;
    onNavigate();
    setEditor(null);
  }

  // 表單收到 204 才切回清單，保存回饋和讀回仍由共用入口處理。
  const submitEditor: SubmitOrganization = (event, buildWrite, confirmation) =>
    // 保存後焦點留在共用回饋，讓鍵盤使用者接著讀取結果。
    onSubmit(event, buildWrite, confirmation, () => setEditor(null));

  return (
    <div className="organization-layout">
      <nav aria-label="組織管理選單" className="organization-menu">
        {sections.map((item) => (
          <button
            key={item.id}
            type="button"
            aria-pressed={section === item}
            disabled={disabled}
            onClick={() => {
              // 切換管理項目時關閉表單並清除勾選，停在同區則保留。
              if (section === item) return;
              onNavigate();
              setSection(item);
              setEditor(null);
              setSelection(null);
            }}
          >
            {item.label}
          </button>
        ))}
      </nav>
      <section
        className="organization-list"
        aria-labelledby="organization-page-title"
      >
        {creating || department || account ? (
          <div className="organization-editor" key={editId ?? "create"}>
            <div className="organization-page-heading">
              <h3 id="organization-page-title" ref={focusEditor} tabIndex={-1}>
                {editorTitle}
              </h3>
            </div>
            {/* 只有成功讀回才重建表單；錯誤期間維持原輸入及同一筆操作對象。 */}
            {creating ? (
              managesDepartments ? (
                <CreateDepartment
                  key={revision}
                  disabled={disabled}
                  pending={pending}
                  onCancel={cancelEditor}
                  onSubmit={submitEditor}
                />
              ) : (
                <CreateAccount
                  key={revision}
                  snapshot={snapshot}
                  departmentName={departmentName}
                  disabled={disabled || !canCreate}
                  pending={pending}
                  onCancel={cancelEditor}
                  onSubmit={submitEditor}
                />
              )
            ) : department ? (
              <EditDepartment
                key={revision}
                department={department}
                departmentName={departmentName}
                disabled={disabled}
                pending={pending}
                onCancel={cancelEditor}
                onSubmit={submitEditor}
              />
            ) : account ? (
              <EditAccount
                key={revision}
                account={account}
                snapshot={snapshot}
                departmentName={departmentName}
                disabled={disabled}
                pending={pending}
                onCancel={cancelEditor}
                onSubmit={submitEditor}
              />
            ) : null}
          </div>
        ) : (
          <>
            <div className="organization-page-heading">
              <h3 id="organization-page-title" tabIndex={-1} ref={focusList}>
                {section.label}
              </h3>
              <div className="organization-list-actions">
                {selectedRows.length > 0 ? (
                  <span className="hint" role="status">
                    已選取 {selectedRows.length} 筆
                  </span>
                ) : null}
                <button
                  type="button"
                  aria-label={`新增${section.item}表單`}
                  disabled={disabled || !canCreate}
                  onClick={
                    // 新增切換至獨立表單，兩種資源各自保留選單位置。
                    () => openEditor({ mode: "create" })
                  }
                >
                  新增
                </button>
                <form
                  onSubmit={
                    // 批次停用只使用這一區目前仍可停用的勾選列。
                    (event) => deactivate(event, selectedRows)
                  }
                >
                  <button
                    type="submit"
                    className="secondary"
                    disabled={disabled || selectedRows.length === 0}
                  >
                    批次停用
                  </button>
                </form>
              </div>
            </div>
            {!canCreate ? (
              <p className="hint">
                請先新增或啟用至少一個部門，再建立業務帳號。
              </p>
            ) : null}

            {/* 手機可聚焦捲動區，讓表格維持完整欄名及原生列／欄語意。 */}
            <div
              className="table-frame"
              role="region"
              aria-label={section.list}
              tabIndex={0}
            >
              <table
                className={
                  managesDepartments
                    ? "organization-table"
                    : "organization-table organization-accounts"
                }
                aria-label={section.list}
              >
                <thead>
                  <tr>
                    <th scope="col">
                      <input
                        type="checkbox"
                        aria-label={`選取所有可停用${section.item}`}
                        checked={allSelected}
                        disabled={disabled || eligibleRows.length === 0}
                        ref={(checkbox) => {
                          // 原生 checkbox 的部分勾選狀態須由 DOM 屬性設定。
                          if (checkbox)
                            checkbox.indeterminate =
                              !allSelected && selectedRows.length > 0;
                        }}
                        onChange={
                          // 只選取目前清單中仍啟用的列。
                          (event) => toggleAll(event.currentTarget.checked)
                        }
                      />
                    </th>
                    <th scope="col">
                      {managesDepartments ? "部門名稱" : "帳號"}
                    </th>
                    {managesDepartments ? null : (
                      <>
                        <th scope="col">所屬部門</th>
                        <th scope="col">主管／財務角色</th>
                      </>
                    )}
                    <th scope="col">狀態</th>
                    <th scope="col">操作</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.length === 0 ? (
                    <tr>
                      <td colSpan={managesDepartments ? 4 : 6}>
                        目前沒有{section.item}。
                      </td>
                    </tr>
                  ) : null}
                  {rows.map(
                    // 以列的穩定識別選取編輯目標，同名資料仍可各自操作。
                    (row) => (
                      <tr key={row.id} aria-label={row.name}>
                        <td>
                          <input
                            type="checkbox"
                            aria-label={`選取 ${row.name}`}
                            checked={
                              row.active && (selectedIds?.has(row.id) ?? false)
                            }
                            disabled={disabled || !row.active}
                            onChange={
                              // 依穩定識別更新勾選，同名部門也能分開停用。
                              (event) =>
                                toggleRow(row.id, event.currentTarget.checked)
                            }
                          />
                        </td>
                        <th scope="row">{row.name}</th>
                        {managesDepartments ? null : (
                          <>
                            <td>{row.department}</td>
                            <td>{row.roles}</td>
                          </>
                        )}
                        <td>{row.active ? "啟用" : "停用"}</td>
                        <td>
                          <div className="organization-row-actions">
                            <button
                              type="button"
                              className="secondary"
                              aria-label={`編輯 ${row.name}`}
                              disabled={disabled}
                              onClick={
                                // 用穩定識別進入對應的編輯頁。
                                () => openEditor({ mode: "edit", id: row.id })
                              }
                            >
                              編輯
                            </button>
                            <form
                              onSubmit={
                                // 列尾操作只停用本列，按鈕即使另有勾選也不改變範圍。
                                (event) => deactivate(event, [row])
                              }
                            >
                              <button
                                type="submit"
                                className="secondary"
                                aria-label={`停用 ${row.name}`}
                                disabled={disabled || !row.active}
                              >
                                停用
                              </button>
                            </form>
                          </div>
                        </td>
                      </tr>
                    ),
                  )}
                </tbody>
              </table>
            </div>
          </>
        )}
      </section>
    </div>
  );
}
