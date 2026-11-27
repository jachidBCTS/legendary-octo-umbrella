using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolFlow.Web.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class ShareRunDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "DefinitionJson",
                table: "Runs",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<int>(
                name: "DefinitionId",
                table: "Runs",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RunDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunDefinitions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_CreatedAt",
                table: "Runs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_DefinitionId",
                table: "Runs",
                column: "DefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalTasks_DecidedBy",
                table: "ApprovalTasks",
                column: "DecidedBy");

            migrationBuilder.CreateIndex(
                name: "IX_RunDefinitions_Hash",
                table: "RunDefinitions",
                column: "Hash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Runs_RunDefinitions_DefinitionId",
                table: "Runs",
                column: "DefinitionId",
                principalTable: "RunDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Runs_RunDefinitions_DefinitionId",
                table: "Runs");

            migrationBuilder.DropTable(
                name: "RunDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_Runs_CreatedAt",
                table: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_Runs_DefinitionId",
                table: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_ApprovalTasks_DecidedBy",
                table: "ApprovalTasks");

            migrationBuilder.DropColumn(
                name: "DefinitionId",
                table: "Runs");

            migrationBuilder.AlterColumn<string>(
                name: "DefinitionJson",
                table: "Runs",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);
        }
    }
}
