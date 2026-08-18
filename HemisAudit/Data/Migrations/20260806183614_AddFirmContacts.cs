using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HemisAudit.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFirmContacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdminContactEmail",
                table: "Firms",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdminContactName",
                table: "Firms",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingContactEmail",
                table: "Firms",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingContactName",
                table: "Firms",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrimaryContactEmail",
                table: "Firms",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PrimaryContactName",
                table: "Firms",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdminContactEmail",
                table: "Firms");

            migrationBuilder.DropColumn(
                name: "AdminContactName",
                table: "Firms");

            migrationBuilder.DropColumn(
                name: "BillingContactEmail",
                table: "Firms");

            migrationBuilder.DropColumn(
                name: "BillingContactName",
                table: "Firms");

            migrationBuilder.DropColumn(
                name: "PrimaryContactEmail",
                table: "Firms");

            migrationBuilder.DropColumn(
                name: "PrimaryContactName",
                table: "Firms");
        }
    }
}
