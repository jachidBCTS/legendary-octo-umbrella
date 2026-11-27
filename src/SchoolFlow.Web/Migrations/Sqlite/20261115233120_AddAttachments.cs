using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolFlow.Web.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    StoredName = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    OwnerEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    WorkflowId = table.Column<int>(type: "INTEGER", nullable: false),
                    RunId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_OwnerEmail_RunId",
                table: "Attachments",
                columns: new[] { "OwnerEmail", "RunId" });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_PublicId",
                table: "Attachments",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_RunId",
                table: "Attachments",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attachments");
        }
    }
}
