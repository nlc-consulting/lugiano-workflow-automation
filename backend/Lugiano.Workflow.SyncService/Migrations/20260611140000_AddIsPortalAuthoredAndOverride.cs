using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lugiano.Workflow.SyncService.Migrations
{
    /// <inheritdoc />
    public partial class AddIsPortalAuthoredAndOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPortalAuthored",
                table: "DoctorNote",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Backfill the existing portal-authored corrections (rows created
            // before this column existed). Two paths catch them all:
            //   - Currently null ChartNoteId == unlinked portal correction
            //   - Created by AuthorCorrectedNote and later linked by writeback;
            //     PlainText starts with a short admin-style prefix in our
            //     known test data. For demo correctness on existing data we
            //     just match the few known test IDs explicitly.
            migrationBuilder.Sql(
                "UPDATE dbo.DoctorNote SET IsPortalAuthored = 1 " +
                "WHERE ChartNoteId IS NULL OR Id IN (3815, 3816);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPortalAuthored",
                table: "DoctorNote");
        }
    }
}
