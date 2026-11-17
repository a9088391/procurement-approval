using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Procurement.Api.Migrations
{
    /// <inheritdoc />
    public partial class RequisitionReassignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RequisitionApprovalEvents_RequisitionId_RoundNumber_Stage",
                table: "RequisitionApprovalEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RequisitionApprovalEvents_KindStage",
                table: "RequisitionApprovalEvents");

            // 保留 ManagerId 與 FinanceId 原始指派，替代人選寫入獨立欄位。
            migrationBuilder.AddColumn<string>(
                name: "FinanceReplacementId",
                table: "RequisitionRounds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManagerReplacementId",
                table: "RequisitionRounds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NewReviewerId",
                table: "RequisitionApprovalEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousReviewerId",
                table: "RequisitionApprovalEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "RequisitionApprovalEvents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionRounds_FinanceReplacementId",
                table: "RequisitionRounds",
                column: "FinanceReplacementId");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionRounds_ManagerReplacementId",
                table: "RequisitionRounds",
                column: "ManagerReplacementId");

            // 以替代人選優先的有效指派檢查兩關簽核人不可相同。
            migrationBuilder.AddCheckConstraint(
                name: "CK_RequisitionRounds_DistinctEffectiveReviewers",
                table: "RequisitionRounds",
                sql: " COALESCE(\"ManagerReplacementId\", \"ManagerId\") <> COALESCE(\"FinanceReplacementId\", \"FinanceId\") ");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionApprovalEvents_NewReviewerId",
                table: "RequisitionApprovalEvents",
                column: "NewReviewerId");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionApprovalEvents_PreviousReviewerId",
                table: "RequisitionApprovalEvents",
                column: "PreviousReviewerId");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionApprovalEvents_RequisitionId_RoundNumber",
                table: "RequisitionApprovalEvents",
                columns: new[] { "RequisitionId", "RoundNumber" });

            // 唯一索引僅涵蓋核准與駁回事件，因此同一關可記錄多次改派。
            migrationBuilder.CreateIndex(
                name: "IX_RequisitionApprovalEvents_RequisitionId_RoundNumber_Stage",
                table: "RequisitionApprovalEvents",
                columns: new[] { "RequisitionId", "RoundNumber", "Stage" },
                unique: true,
                filter: " \"Kind\" IN ('approved', 'rejected') ");

            // 改派事件須指定 manager 或 finance 階段。
            migrationBuilder.AddCheckConstraint(
                name: "CK_RequisitionApprovalEvents_KindStage",
                table: "RequisitionApprovalEvents",
                sql: "(\"Kind\" IN ('submitted', 'withdrawn') AND \"Stage\" IS NULL) OR\n(\"Kind\" IN ('approved', 'rejected', 'reassigned') AND \"Stage\" IS NOT NULL AND \"Stage\" IN ('manager', 'finance'))");

            // 改派須記錄不同的前後簽核人及非空原因。其他事件的三欄維持 NULL，以保留既有歷程相容性。
            migrationBuilder.AddCheckConstraint(
                name: "CK_RequisitionApprovalEvents_Reassignment",
                table: "RequisitionApprovalEvents",
                sql: "(\"Kind\" = 'reassigned'\n    AND \"PreviousReviewerId\" IS NOT NULL AND \"NewReviewerId\" IS NOT NULL\n    AND \"PreviousReviewerId\" <> \"NewReviewerId\" AND \"Reason\" IS NOT NULL AND length(\"Reason\") > 0) OR\n(\"Kind\" <> 'reassigned' AND \"PreviousReviewerId\" IS NULL AND \"NewReviewerId\" IS NULL AND \"Reason\" IS NULL)");

            // Restrict 外鍵保留改派人選及事件與帳號的關聯。
            migrationBuilder.AddForeignKey(
                name: "FK_RequisitionApprovalEvents_AspNetUsers_NewReviewerId",
                table: "RequisitionApprovalEvents",
                column: "NewReviewerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RequisitionApprovalEvents_AspNetUsers_PreviousReviewerId",
                table: "RequisitionApprovalEvents",
                column: "PreviousReviewerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RequisitionRounds_AspNetUsers_FinanceReplacementId",
                table: "RequisitionRounds",
                column: "FinanceReplacementId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RequisitionRounds_AspNetUsers_ManagerReplacementId",
                table: "RequisitionRounds",
                column: "ManagerReplacementId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 降版移除改派欄位並恢復不接受 reassigned 的舊 Kind 約束。已有改派事件會使本 migration 降版失敗並回滾。
            migrationBuilder.DropForeignKey(
                name: "FK_RequisitionApprovalEvents_AspNetUsers_NewReviewerId",
                table: "RequisitionApprovalEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_RequisitionApprovalEvents_AspNetUsers_PreviousReviewerId",
                table: "RequisitionApprovalEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_RequisitionRounds_AspNetUsers_FinanceReplacementId",
                table: "RequisitionRounds");

            migrationBuilder.DropForeignKey(
                name: "FK_RequisitionRounds_AspNetUsers_ManagerReplacementId",
                table: "RequisitionRounds");

            migrationBuilder.DropIndex(
                name: "IX_RequisitionRounds_FinanceReplacementId",
                table: "RequisitionRounds");

            migrationBuilder.DropIndex(
                name: "IX_RequisitionRounds_ManagerReplacementId",
                table: "RequisitionRounds");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RequisitionRounds_DistinctEffectiveReviewers",
                table: "RequisitionRounds");

            migrationBuilder.DropIndex(
                name: "IX_RequisitionApprovalEvents_NewReviewerId",
                table: "RequisitionApprovalEvents");

            migrationBuilder.DropIndex(
                name: "IX_RequisitionApprovalEvents_PreviousReviewerId",
                table: "RequisitionApprovalEvents");

            migrationBuilder.DropIndex(
                name: "IX_RequisitionApprovalEvents_RequisitionId_RoundNumber",
                table: "RequisitionApprovalEvents");

            migrationBuilder.DropIndex(
                name: "IX_RequisitionApprovalEvents_RequisitionId_RoundNumber_Stage",
                table: "RequisitionApprovalEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RequisitionApprovalEvents_KindStage",
                table: "RequisitionApprovalEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RequisitionApprovalEvents_Reassignment",
                table: "RequisitionApprovalEvents");

            migrationBuilder.DropColumn(
                name: "FinanceReplacementId",
                table: "RequisitionRounds");

            migrationBuilder.DropColumn(
                name: "ManagerReplacementId",
                table: "RequisitionRounds");

            migrationBuilder.DropColumn(
                name: "NewReviewerId",
                table: "RequisitionApprovalEvents");

            migrationBuilder.DropColumn(
                name: "PreviousReviewerId",
                table: "RequisitionApprovalEvents");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "RequisitionApprovalEvents");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionApprovalEvents_RequisitionId_RoundNumber_Stage",
                table: "RequisitionApprovalEvents",
                columns: new[] { "RequisitionId", "RoundNumber", "Stage" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RequisitionApprovalEvents_KindStage",
                table: "RequisitionApprovalEvents",
                sql: "(\"Kind\" IN ('submitted', 'withdrawn') AND \"Stage\" IS NULL) OR\n(\"Kind\" IN ('approved', 'rejected') AND \"Stage\" IS NOT NULL AND \"Stage\" IN ('manager', 'finance'))");
        }
    }
}
