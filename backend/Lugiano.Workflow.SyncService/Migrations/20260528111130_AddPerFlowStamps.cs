using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lugiano.Workflow.SyncService.Migrations
{
    /// <inheritdoc />
    public partial class AddPerFlowStamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DoctorNotesReceivedAt",
                table: "WorkflowCase",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InsuranceAddedAt",
                table: "WorkflowCase",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PipVerifiedAt",
                table: "WorkflowCase",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DoctorNotesReceivedAt",
                table: "WorkflowCase");

            migrationBuilder.DropColumn(
                name: "InsuranceAddedAt",
                table: "WorkflowCase");

            migrationBuilder.DropColumn(
                name: "PipVerifiedAt",
                table: "WorkflowCase");
        }
    }
}
