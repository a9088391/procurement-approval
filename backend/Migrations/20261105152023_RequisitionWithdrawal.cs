using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Procurement.Api.Migrations
{
    /// <inheritdoc />
    public partial class RequisitionWithdrawal : Migration
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

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionApprovalEvents_RequisitionId_RoundNumber_Kind",
                table: "RequisitionApprovalEvents",
                columns: new[] { "RequisitionId", "RoundNumber", "Kind" },
                unique: true,
                filter: " \"Stage\" IS NULL ");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RequisitionApprovalEvents_RequisitionId_RoundNumber_Kind",
                table: "RequisitionApprovalEvents");

            migrationBuilder.DropIndex(
                name: "IX_RequisitionApprovalEvents_RequisitionId_RoundNumber_Stage",
                table: "RequisitionApprovalEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RequisitionApprovalEvents_KindStage",
                table: "RequisitionApprovalEvents");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionApprovalEvents_RequisitionId_RoundNumber_Stage",
                table: "RequisitionApprovalEvents",
                columns: new[] { "RequisitionId", "RoundNumber", "Stage" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RequisitionApprovalEvents_KindStage",
                table: "RequisitionApprovalEvents",
                sql: "(\"Kind\" = 'submitted' AND \"Stage\" IS NULL) OR\n(\"Kind\" IN ('approved', 'rejected') AND \"Stage\" IS NOT NULL AND \"Stage\" IN ('manager', 'finance'))");
        }
    }
}
