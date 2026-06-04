using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lugiano.Workflow.SyncService.Migrations
{
    /// <inheritdoc />
    public partial class AddDoctorsAndCorrectionRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CorrectionRequest",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DoctorNoteId = table.Column<int>(type: "int", nullable: false),
                    WorkflowCaseId = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ReviewerEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    ReviewerComments = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MissingItemsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RecipientDoctorIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RecipientOverrideEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    RoundNumber = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorrectionRequest", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Doctor",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChiroTouchDoctorId = table.Column<int>(type: "int", nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Credentials = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Npi = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Doctor", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionRequest_DoctorNoteId_State",
                table: "CorrectionRequest",
                columns: new[] { "DoctorNoteId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionRequest_WorkflowCaseId",
                table: "CorrectionRequest",
                column: "WorkflowCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_Doctor_ChiroTouchDoctorId",
                table: "Doctor",
                column: "ChiroTouchDoctorId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CorrectionRequest");

            migrationBuilder.DropTable(
                name: "Doctor");
        }
    }
}
