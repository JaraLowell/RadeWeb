using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RadegastWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddStatsDisplayNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StatsDisplayNames",
                columns: table => new
                {
                    AvatarId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AvatarName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatsDisplayNames", x => x.AvatarId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StatsDisplayName_LastUpdated",
                table: "StatsDisplayNames",
                column: "LastUpdated");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StatsDisplayNames");
        }
    }
}
