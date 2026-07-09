using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RadegastWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddIsFriendToGlobalDisplayNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFriend",
                table: "GlobalDisplayNames",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_GlobalDisplayName_IsFriend",
                table: "GlobalDisplayNames",
                column: "IsFriend");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GlobalDisplayName_IsFriend",
                table: "GlobalDisplayNames");

            migrationBuilder.DropColumn(
                name: "IsFriend",
                table: "GlobalDisplayNames");
        }
    }
}
