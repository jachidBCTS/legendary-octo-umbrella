using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolFlow.Web.Migrations.Sqlite
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
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<int>(
                name: "DefinitionId",
                table: "Runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RunDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
