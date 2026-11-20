using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolFlow.Web.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddDueDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DueNotify",
                table: "ApprovalTasks",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "DueOn",
                table: "ApprovalTasks",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DueSoonSentAt",
                table: "ApprovalTasks",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OverdueSentAt",
                table: "ApprovalTasks",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DueNotify",
                table: "ApprovalTasks");

            migrationBuilder.DropColumn(
                name: "DueOn",
                table: "ApprovalTasks");

            migrationBuilder.DropColumn(
                name: "DueSoonSentAt",
                table: "ApprovalTasks");

            migrationBuilder.DropColumn(
                name: "OverdueSentAt",
                table: "ApprovalTasks");
        }
    }
}
