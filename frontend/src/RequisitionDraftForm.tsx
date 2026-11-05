// 草稿表單保留數值原字串；驗證通過才轉成 API 需要的數字。
import type { DraftDto, DraftInputDto } from "./api";

interface LineForm {
  key: number;
  itemName: string;
  unit: string;
  quantity: string;
  unitPrice: string;
}

export interface DraftForm {
  subject: string;
  purpose: string;
  lines: LineForm[];
}

const amountFormat = new Intl.NumberFormat("zh-TW", {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

const lineFields = [
  { field: "itemName", label: "品名", inputMode: "text" },
  { field: "unit", label: "單位", inputMode: "text" },
  { field: "quantity", label: "數量", inputMode: "numeric" },
  { field: "unitPrice", label: "預估單價（TWD）", inputMode: "decimal" },
] as const;

// 列鍵值只須在目前頁面內唯一，讓本機 HTTP 也能正常編輯。
let nextLineKey = 0;

// 統一呈現臺幣金額；無法計算時讓畫面區分空值和零元。
export function formatDraftAmount(value: number | null) {
  return value === null
    ? "尚無可計算金額"
    : `TWD ${amountFormat.format(value)}`;
}

// 每列使用表單專屬的鍵值，移除前一列不會錯置後續輸入或焦點。
function makeLine(line?: DraftDto["lines"][number]): LineForm {
  return {
    key: ++nextLineKey,
    itemName: line?.itemName ?? "",
    unit: line?.unit ?? "",
    quantity: String(line?.quantity ?? ""),
    unitPrice: String(line?.unitPrice ?? ""),
  };
}

// 新建留空，重開以後端內容重建表單並保留明細順序。
export function makeDraftForm(draft?: DraftDto): DraftForm {
  return {
    subject: draft?.subject ?? "",
    purpose: draft?.purpose ?? "",
    lines:
      draft?.lines.map(
        // 每筆資料各自建立本次編輯的列鍵值。
        (line) => makeLine(line),
      ) ?? [],
  };
}

// 依後端 UTF-16 上限驗證；整單用途允許換行，其餘文字保持單行。
function validText(value: string, limit: number, multiline = false) {
  return (
    value.length <= limit &&
    !value.includes("\0") &&
    (multiline || !/[\r\n]/.test(value))
  );
}

// 以 Unicode White_Space 判斷必填內容，和後端的空白判斷保持一致。
function hasText(value: string) {
  return /[^\p{White_Space}]/u.test(value);
}

// 缺值可保留；確認整數詞法及範圍後才允許送出數量。
function parseQuantity(value: string) {
  // 不把空白、小數、符號或科學記號轉成可接受的數字。
  if (value === "") return null;
  if (!/^\d+$/.test(value)) return NaN;

  // 轉型後只接受可保存的整數範圍。
  const quantity = Number(value);
  return quantity >= 1 && quantity <= 10_000 ? quantity : NaN;
}

// 單價以整數分處理，避免先轉浮點數而失去輸入的小數位數。
function parseUnitPrice(value: string) {
  // 先檢查最多兩位小數，尾端的零也計入位數。
  if (value === "") return null;
  if (!/^\d+(?:\.\d{1,2})?$/.test(value)) return NaN;

  // 以整數組成金額，將上限也換算為分後比較。
  const [whole, fraction = ""] = value.split(".");
  const cents = Number(whole) * 100 + Number(fraction.padEnd(2, "0"));
  return cents > 0 && cents <= 100_000_000 ? cents : NaN;
}

// 同一輪檢查產生保存資料與即時預估，避免畫面採用另一套數值解讀方式。
export function analyzeDraftForm({ subject, purpose, lines }: DraftForm) {
  // 缺少必填欄位可保存，超出長度或單行規則則回報第一個問題。
  let error = !validText(subject, 200)
    ? "主旨須為單行，最多 200 個字元。"
    : !validText(purpose, 4000, true)
      ? "整單用途最多 4,000 個字元，且不得包含空字元。"
      : "";
  let complete =
    hasText(subject) &&
    hasText(purpose) &&
    lines.length >= 1 &&
    lines.length <= 100;
  let total: number | null = null;
  const subtotals: (number | null)[] = [];
  const content: DraftInputDto = {
    subject: subject || null,
    purpose: purpose || null,
    lines: [],
  };

  // 依表單順序逐列驗證及計算，缺值不阻擋保存但不能冒充已填妥的金額。
  for (const [index, line] of lines.entries()) {
    const quantity = parseQuantity(line.quantity);
    const cents = parseUnitPrice(line.unitPrice);
    const label = `明細 ${index + 1}`;
    if (!validText(line.itemName, 200))
      error ||= `${label}的品名須為單行，最多 200 個字元。`;
    if (!validText(line.unit, 40))
      error ||= `${label}的單位須為單行，最多 40 個字元。`;
    if (Number.isNaN(quantity))
      error ||= `${label}的數量須為 1–10,000 的整數。`;
    if (Number.isNaN(cents))
      error ||= `${label}的預估單價須大於零且不超過 TWD 1,000,000，最多兩位小數。`;
    if (
      !hasText(line.itemName) ||
      !hasText(line.unit) ||
      quantity === null ||
      cents === null
    )
      complete = false;

    // 已知的小計都要計入上限；達上限後保留首筆錯誤，無須再增加總額。
    const subtotal = quantity && cents ? quantity * cents : null;
    subtotals.push(subtotal === null ? null : subtotal / 100);
    if (subtotal !== null && (total ?? 0) <= 1_000_000_000) {
      total = (total ?? 0) + subtotal;
      if (total > 1_000_000_000)
        error ||= "預估申請金額不得超過 TWD 10,000,000。";
    }

    // 只收錄可編輯欄位；前面有任何錯誤時呼叫端不會送出這份資料。
    content.lines.push({
      itemName: line.itemName || null,
      unit: line.unit || null,
      quantity,
      unitPrice: cents === null ? null : cents / 100,
    });
  }

  // 僅為呈現及 JSON 數字換回元；金額比較與乘加全程以整數分執行。
  return {
    content,
    total: total === null ? null : total / 100,
    subtotals,
    complete,
    error,
  };
}

interface FieldProps {
  form: DraftForm;
  subtotals: (number | null)[];
  onChange: (form: DraftForm) => void;
}

// 主旨、整單用途及有順序的明細都可逐步填寫，異動統一交還編輯器。
export function RequisitionDraftFields({
  form,
  subtotals,
  onChange,
}: FieldProps) {
  // 更新指定明細，不重建其他列，讓鍵盤焦點維持在原控制項。
  function changeLine(
    index: number,
    field: (typeof lineFields)[number]["field"],
    value: string,
  ) {
    const lines = [...form.lines];
    lines[index] = { ...lines[index], [field]: value };
    onChange({ ...form, lines });
  }

  // 原生標籤及欄位群組讓每筆明細的控制項可用鍵盤操作並被辨識。
  return (
    <>
      <label>
        主旨
        <input
          value={form.subject}
          onChange={
            // 直接保存原輸入，保留必要的空白與 Unicode 字元。
            (event) => onChange({ ...form, subject: event.target.value })
          }
        />
      </label>
      <label>
        整單用途
        <textarea
          value={form.purpose}
          rows={4}
          onChange={
            // 保留多行用途，送出時再依後端長度契約檢查。
            (event) => onChange({ ...form, purpose: event.target.value })
          }
        />
      </label>
      {form.lines.map(
        // 群組依目前順序編號，鍵值仍由列本身的表單識別決定。
        (line, index) => (
          <fieldset className="draft-line" key={line.key}>
            <legend>明細 {index + 1}</legend>
            {lineFields.map(
              // 數值採文字輸入，保留尾零和非法字元供保存前檢查。
              ({ field, label, inputMode }) => (
                <label key={field}>
                  {label}
                  <input
                    inputMode={inputMode}
                    value={line[field]}
                    onChange={
                      // 只變更此列的指定欄位。
                      (event) => changeLine(index, field, event.target.value)
                    }
                  />
                </label>
              ),
            )}
            <p>明細小計：{formatDraftAmount(subtotals[index])}</p>
            <button
              className="secondary"
              type="button"
              onClick={
                // 只移除目前的列，其餘輸入和順序維持原樣。
                () =>
                  onChange({
                    ...form,
                    lines: form.lines.filter(
                      // 以列鍵值辨識要移除的輸入。
                      (entry) => entry.key !== line.key,
                    ),
                  })
              }
            >
              移除明細
            </button>
          </fieldset>
        ),
      )}
      <button
        className="secondary"
        type="button"
        onClick={
          // 空白列加入末端，允許先保存其餘已填內容。
          () => onChange({ ...form, lines: [...form.lines, makeLine()] })
        }
      >
        新增明細
      </button>
    </>
  );
}
