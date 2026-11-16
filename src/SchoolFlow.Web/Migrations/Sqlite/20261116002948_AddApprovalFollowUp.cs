using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolFlow.Web.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddApprovalFollowUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastPolledAt",
                table: "Runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EscalateDays",
                table: "ApprovalTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EscalateTo",
                table: "ApprovalTasks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Escalated",
                table: "ApprovalTasks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "EscalatedAt",
                table: "ApprovalTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Extra",
                table: "ApprovalTasks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastReminderAt",
                table: "ApprovalTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RemindDays",
                table: "ApprovalTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReminderCount",
                table: "ApprovalTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StandIns",
                table: "ApprovalTasks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ClosedDays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClosedDays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Delegations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FromEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ToEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    StartsOn = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndsOn = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Delegations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunComments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    AuthorEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    AuthorName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunComments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClosedDays_Date",
                table: "ClosedDays",
                column: "Date",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_FromEmail",
                table: "Delegations",
                column: "FromEmail");

            migrationBuilder.CreateIndex(
                name: "IX_RunComments_RunId",
                table: "RunComments",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClosedDays");

            migrationBuilder.DropTable(
                name: "Delegations");

            migrationBuilder.DropTable(
                name: "RunComments");

            migrationBuilder.DropColumn(
                name: "LastPolledAt",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "EscalateDays",
                table: "ApprovalTasks");

            migrationBuilder.DropColumn(
                name: "EscalateTo",
                table: "ApprovalTasks");

            migrationBuilder.DropColumn(
                name: "Escalated",
                table: "ApprovalTasks");

            migrationBuilder.DropColumn(
                name: "EscalatedAt",
                table: "ApprovalTasks");

            migrationBuilder.DropColumn(
                name: "Extra",
                table: "ApprovalTasks");

            migrationBuilder.DropColumn(
                name: "LastReminderAt",
                table: "ApprovalTasks");

            migrationBuilder.DropColumn(
                name: "RemindDays",
                table: "ApprovalTasks");

            migrationBuilder.DropColumn(
                name: "ReminderCount",
                table: "ApprovalTasks");

            migrationBuilder.DropColumn(
                name: "StandIns",
                table: "ApprovalTasks");
        }
    }
}
