using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lugiano.Workflow.SyncService.Migrations
{
    /// <inheritdoc />
    // Adds Confidence + HallucinationReason to EobScanCheck so the post-extraction
    // reconciliation pass (CheckConfidenceScorer) can tier each row without
    // dropping data. Both nullable — existing rows read as null which the
    // scorer/aggregator treats as "unscored ⇒ high" (backward compatible).
    public partial class AddCheckConfidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Confidence",
                table: "EobScanCheck",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HallucinationReason",
                table: "EobScanCheck",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Confidence", table: "EobScanCheck");
            migrationBuilder.DropColumn(name: "HallucinationReason", table: "EobScanCheck");
        }
    }
}
