using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HemisAudit.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFirmSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Firms",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserId",
                table: "Firms",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Firms",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Firms");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "Firms");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Firms");
        }
    }
}
