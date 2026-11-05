using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Procurement.Api.Migrations
{
    /// <inheritdoc />
    public partial class RequisitionApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RequisitionRounds",
                columns: table => new
                {
                    RequisitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    DepartmentId = table.Column<string>(type: "text", nullable: false),
                    DepartmentName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ManagerId = table.Column<string>(type: "text", nullable: false),
                    FinanceId = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequisitionRounds", x => new { x.RequisitionId, x.Number });
                    table.CheckConstraint("CK_RequisitionRounds_DistinctReviewers", " \"ManagerId\" <> \"FinanceId\" ");
                    table.ForeignKey(
                        name: "FK_RequisitionRounds_AspNetUsers_FinanceId",
                        column: x => x.FinanceId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequisitionRounds_AspNetUsers_ManagerId",
                        column: x => x.ManagerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequisitionRounds_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequisitionRounds_Requisitions_RequisitionId",
                        column: x => x.RequisitionId,
                        principalTable: "Requisitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RequisitionApprovalEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequisitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoundNumber = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Stage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    ActorId = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequisitionApprovalEvents", x => x.Id);
                    table.CheckConstraint("CK_RequisitionApprovalEvents_KindStage", "(\"Kind\" = 'submitted' AND \"Stage\" IS NULL) OR\n(\"Kind\" IN ('approved', 'rejected') AND \"Stage\" IS NOT NULL AND \"Stage\" IN ('manager', 'finance'))");
                    table.ForeignKey(
                        name: "FK_RequisitionApprovalEvents_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequisitionApprovalEvents_RequisitionRounds_RequisitionId_R~",
                        columns: x => new { x.RequisitionId, x.RoundNumber },
                        principalTable: "RequisitionRounds",
                        principalColumns: new[] { "RequisitionId", "Number" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionApprovalEvents_ActorId",
                table: "RequisitionApprovalEvents",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionApprovalEvents_RequisitionId_RoundNumber_Stage",
                table: "RequisitionApprovalEvents",
                columns: new[] { "RequisitionId", "RoundNumber", "Stage" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionRounds_DepartmentId",
                table: "RequisitionRounds",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionRounds_FinanceId",
                table: "RequisitionRounds",
                column: "FinanceId");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionRounds_ManagerId",
                table: "RequisitionRounds",
                column: "ManagerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequisitionApprovalEvents");

            migrationBuilder.DropTable(
                name: "RequisitionRounds");
        }
    }
}
