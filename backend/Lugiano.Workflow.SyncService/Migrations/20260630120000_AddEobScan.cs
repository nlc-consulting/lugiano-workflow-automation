using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lugiano.Workflow.SyncService.Migrations
{
    /// <inheritdoc />
    public partial class AddEobScan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EobScan",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceFilename = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ScanDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PageCount = table.Column<int>(type: "int", nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StoredPdfPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ProcessingStartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChunkSize = table.Column<int>(type: "int", nullable: false),
                    ChunkOverlap = table.Column<int>(type: "int", nullable: false),
                    ModelUsed = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false, defaultValue: ""),
                    InputTokens = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<int>(type: "int", nullable: false),
                    EstimatedCostUsd = table.Column<decimal>(type: "decimal(10,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EobScan", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EobScanCheck",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EobScanId = table.Column<int>(type: "int", nullable: false),
                    PageNumber = table.Column<int>(type: "int", nullable: false),
                    CheckNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    CheckDate = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    Payer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Administrator = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PairedCheckId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EobScanCheck", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EobScanCheck_EobScan_EobScanId",
                        column: x => x.EobScanId,
                        principalTable: "EobScan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EobScanLineItem",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EobScanId = table.Column<int>(type: "int", nullable: false),
                    PageNumber = table.Column<int>(type: "int", nullable: false),
                    ClaimNumber = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    PatientNameRaw = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    BillNumber = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    ServiceDate = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CheckNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ProcedureCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BilledAmount = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    AllowedAmount = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    PaidAmount = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    WriteOffAmount = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    ReasonCodesJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EobScanLineItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EobScanLineItem_EobScan_EobScanId",
                        column: x => x.EobScanId,
                        principalTable: "EobScan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EobScan_UploadedAt",
                table: "EobScan",
                column: "UploadedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EobScanCheck_EobScanId_PageNumber",
                table: "EobScanCheck",
                columns: new[] { "EobScanId", "PageNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_EobScanLineItem_EobScanId_PageNumber",
                table: "EobScanLineItem",
                columns: new[] { "EobScanId", "PageNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "EobScanLineItem");
            migrationBuilder.DropTable(name: "EobScanCheck");
            migrationBuilder.DropTable(name: "EobScan");
        }
    }
}
