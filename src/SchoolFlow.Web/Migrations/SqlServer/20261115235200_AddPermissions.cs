using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolFlow.Web.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudienceGroups",
                table: "Workflows",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Owners",
                table: "Workflows",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EntraRole",
                table: "Users",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Groups",
                table: "Users",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActorEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ActorName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Target = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DirectoryGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ObjectId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryGroups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_At",
                table: "AuditEntries",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryGroups_ObjectId",
                table: "DirectoryGroups",
                column: "ObjectId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "DirectoryGroups");

            migrationBuilder.DropColumn(
                name: "AudienceGroups",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "Owners",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "EntraRole",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Groups",
                table: "Users");
        }
    }
}
