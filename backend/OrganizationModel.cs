using System.Text.Json.Serialization;

namespace Procurement;

// 部門以停用保留穩定識別，版本供管理設定防止覆蓋新值。
public sealed class Department
{
    internal const int NameLimit = 100;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
}

// 各操作只接受對應的可編輯欄位，拒絕身分類別、管理角色及其他未宣告欄位。
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateDepartmentRequest(string? Name);

// 部門保存帶回讀取時的版本與明確的啟用選擇。
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UpdateDepartmentRequest(string? Name, bool? Active, string? Version);

// 新業務帳號需要臨時密碼及部門，角色選項只接受主管與財務。
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateBusinessAccountRequest(
    string? UserName,
    string? Password,
    string? DepartmentId,
    bool? IsManager,
    bool? IsFinance);

// 更新採完整設定；null 用於保留原本未分部門或清除人選，遺漏欄位不能默默清除。
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UpdateBusinessAccountRequest(
    string? Version,
    bool? Active,
    [property: JsonRequired] string? DepartmentId,
    bool? IsManager,
    bool? IsFinance,
    [property: JsonRequired] string? ManagerReviewerId,
    [property: JsonRequired] string? FinanceReviewerId);

// 重設請求只接收新臨時密碼及版本，無法指定帳號類別或登入狀態。
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ResetBusinessPasswordRequest(string? Version, string? NewPassword);

// 部門回應保留停用狀態及版本，讓管理員仍可查閱既有歸屬。
internal sealed record DepartmentResponse(string Id, string Name, bool Active, string Version);

// 設定回應只公開業務帳號管理所需欄位，排除憑證與登入資料。
internal sealed record BusinessAccountResponse(
    string Id,
    string UserName,
    bool Active,
    string Version,
    string? DepartmentId,
    bool IsManager,
    bool IsFinance,
    string? ManagerReviewerId,
    string? FinanceReviewerId,
    bool MustChangePassword);

// 一次回傳組織設定，讓呼叫端保存後可重查目前版本及選項。
internal sealed record OrganizationResponse(DepartmentResponse[] Departments, BusinessAccountResponse[] Accounts);
