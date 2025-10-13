using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RadegastWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalDisplayNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GlobalDisplayNames",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AvatarId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DisplayNameValue = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LegacyFirstName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LegacyLastName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsDefaultDisplayName = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextUpdate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CachedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalDisplayNames", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GlobalDisplayName_Avatar",
                table: "GlobalDisplayNames",
                column: "AvatarId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GlobalDisplayName_CachedAt",
                table: "GlobalDisplayNames",
                column: "CachedAt");

            migrationBuilder.CreateIndex(
                name: "IX_GlobalDisplayName_LastUpdated",
                table: "GlobalDisplayNames",
                column: "LastUpdated");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlobalDisplayNames");
        }
    }
}
