using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolFlow.Web.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalChains",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    OwnerEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    StepsJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalChains", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Instructions = table.Column<string>(type: "TEXT", nullable: false),
                    Assignees = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Rule = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    ApprovedBy = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    DecidedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Comment = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Departments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FinalApprovers = table.Column<string>(type: "TEXT", nullable: false),
                    FinalRule = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Locations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FreshserviceId = table.Column<long>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Link = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EmailStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    EmailAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    EmailError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    EmailNextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkflowId = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkflowName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    WorkflowVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    DefinitionJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CurrentNodeId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ContextJson = table.Column<string>(type: "TEXT", nullable: false),
                    SubmitterEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    SubmitterName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    WaitingTicketId = table.Column<long>(type: "INTEGER", nullable: true),
                    TicketId = table.Column<long>(type: "INTEGER", nullable: true),
                    TriggerTicketId = table.Column<long>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    DepartmentId = table.Column<long>(type: "INTEGER", nullable: true),
                    DepartmentName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EmailNotifications = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Email);
                });

            migrationBuilder.CreateTable(
                name: "WebhookLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workflows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    DraftJson = table.Column<string>(type: "TEXT", nullable: false),
                    PublishedJson = table.Column<string>(type: "TEXT", nullable: true),
                    PublishedVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TriggerType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalChains_OwnerEmail",
                table: "ApprovalChains",
                column: "OwnerEmail");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalTasks_RunId",
                table: "ApprovalTasks",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalTasks_Status",
                table: "ApprovalTasks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_FreshserviceId",
                table: "Locations",
                column: "FreshserviceId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_EmailStatus",
                table: "Notifications",
                column: "EmailStatus");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserEmail_ReadAt",
                table: "Notifications",
                columns: new[] { "UserEmail", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RunEvents_RunId",
                table: "RunEvents",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_DepartmentId",
                table: "Runs",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_SubmitterEmail",
                table: "Runs",
                column: "SubmitterEmail");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_WaitingTicketId",
                table: "Runs",
                column: "WaitingTicketId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_WorkflowId_TriggerTicketId",
                table: "Runs",
                columns: new[] { "WorkflowId", "TriggerTicketId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalChains");

            migrationBuilder.DropTable(
                name: "ApprovalTasks");

            migrationBuilder.DropTable(
                name: "Departments");

            migrationBuilder.DropTable(
                name: "Locations");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "RunEvents");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "WebhookLogs");

            migrationBuilder.DropTable(
                name: "Workflows");
        }
    }
}
