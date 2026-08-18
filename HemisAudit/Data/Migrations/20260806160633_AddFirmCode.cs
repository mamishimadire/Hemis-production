using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HemisAudit.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFirmCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FirmCode",
                table: "Firms",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Firms_FirmCode",
                table: "Firms",
                column: "FirmCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Firms_FirmCode",
                table: "Firms");

            migrationBuilder.DropColumn(
                name: "FirmCode",
                table: "Firms");
        }
    }
}
