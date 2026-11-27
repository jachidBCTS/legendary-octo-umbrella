using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolFlow.Web.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DailyDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DigestMinute",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DigestSentOn",
                table: "Users",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DigestMinute",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DigestSentOn",
                table: "Users");
        }
    }
}
