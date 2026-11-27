using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolFlow.Web.Migrations.SqlServer
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
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PublicId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    StoredName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    OwnerEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    WorkflowId = table.Column<int>(type: "int", nullable: false),
                    RunId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
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
