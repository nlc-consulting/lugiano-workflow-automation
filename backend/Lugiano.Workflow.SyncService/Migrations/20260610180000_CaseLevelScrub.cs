using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lugiano.Workflow.SyncService.Migrations
{
    /// <inheritdoc />
    public partial class CaseLevelScrub : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ScrubResult.DoctorNoteId: NULL for case-level scrubs (evaluating
            // the patient's whole bill bundle), still set for legacy per-note
            // scrubs.
            migrationBuilder.AlterColumn<int>(
                name: "DoctorNoteId",
                table: "ScrubResult",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            // DoctorNote.ChartNoteId: NULL for portal-authored corrections.
            // The unique index has to be re-created with a filter so multiple
            // portal-authored rows (all NULL) can coexist.
            migrationBuilder.DropIndex(
                name: "IX_DoctorNote_ChartNoteId",
                table: "DoctorNote");

            migrationBuilder.AlterColumn<int>(
                name: "ChartNoteId",
                table: "DoctorNote",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorNote_ChartNoteId",
                table: "DoctorNote",
                column: "ChartNoteId",
                unique: true,
                filter: "[ChartNoteId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DoctorNote_ChartNoteId",
                table: "DoctorNote");

            migrationBuilder.AlterColumn<int>(
                name: "ChartNoteId",
                table: "DoctorNote",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DoctorNote_ChartNoteId",
                table: "DoctorNote",
                column: "ChartNoteId",
                unique: true);

            migrationBuilder.AlterColumn<int>(
                name: "DoctorNoteId",
                table: "ScrubResult",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
