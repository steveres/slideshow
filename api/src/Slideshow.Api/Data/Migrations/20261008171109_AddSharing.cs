using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slideshow.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShareHideMap",
                table: "Albums",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ShareToken",
                table: "Albums",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Albums_ShareToken",
                table: "Albums",
                column: "ShareToken",
                unique: true,
                filter: "[ShareToken] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Albums_ShareToken",
                table: "Albums");

            migrationBuilder.DropColumn(
                name: "ShareHideMap",
                table: "Albums");

            migrationBuilder.DropColumn(
                name: "ShareToken",
                table: "Albums");
        }
    }
}
