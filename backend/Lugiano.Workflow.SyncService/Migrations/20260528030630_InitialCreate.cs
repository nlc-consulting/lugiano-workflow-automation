using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lugiano.Workflow.SyncService.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SyncKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastSeenId = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowCase",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CurrentState = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowCase", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DoctorNote",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkflowCaseId = table.Column<int>(type: "int", nullable: false),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    ChartNoteId = table.Column<int>(type: "int", nullable: false),
                    DoctorId = table.Column<int>(type: "int", nullable: true),
                    NoteDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SoapPtr = table.Column<int>(type: "int", nullable: true),
                    RawRtf = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PlainText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoctorNote", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DoctorNote_WorkflowCase_WorkflowCaseId",
                        column: x => x.WorkflowCaseId,
                        principalTable: "WorkflowCase",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowEvent",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkflowCaseId = table.Column<int>(type: "int", nullable: false),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceTable = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceRecordId = table.Column<long>(type: "bigint", nullable: false),
                    EventDataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowEvent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowEvent_WorkflowCase_WorkflowCaseId",
                        column: x => x.WorkflowCaseId,
                        principalTable: "WorkflowCase",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorNote_ChartNoteId",
                table: "DoctorNote",
                column: "ChartNoteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DoctorNote_WorkflowCaseId",
                table: "DoctorNote",
                column: "WorkflowCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncState_SyncKey",
                table: "SyncState",
                column: "SyncKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCase_PatientId",
                table: "WorkflowCase",
                column: "PatientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowEvent_SourceTable_SourceRecordId",
                table: "WorkflowEvent",
                columns: new[] { "SourceTable", "SourceRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowEvent_WorkflowCaseId",
                table: "WorkflowEvent",
                column: "WorkflowCaseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DoctorNote");

            migrationBuilder.DropTable(
                name: "SyncState");

            migrationBuilder.DropTable(
                name: "WorkflowEvent");

            migrationBuilder.DropTable(
                name: "WorkflowCase");
        }
    }
}
