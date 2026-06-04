using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lugiano.Workflow.SyncService.Migrations
{
    /// <inheritdoc />
    public partial class AddScrubResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScrubResult",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DoctorNoteId = table.Column<int>(type: "int", nullable: false),
                    WorkflowCaseId = table.Column<int>(type: "int", nullable: false),
                    Verdict = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OverallConfidence = table.Column<int>(type: "int", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FindingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RawResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModelUsed = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    RanAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrubResult", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScrubResult_DoctorNoteId_RanAt",
                table: "ScrubResult",
                columns: new[] { "DoctorNoteId", "RanAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScrubResult");
        }
    }
}
