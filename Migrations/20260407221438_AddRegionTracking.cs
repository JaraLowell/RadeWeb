using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RadegastWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddRegionTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegionStatuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RegionName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    GridUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsOnline = table.Column<bool>(type: "INTEGER", nullable: false),
                    RegionHandle = table.Column<ulong>(type: "INTEGER", nullable: true),
                    RegionUuid = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    LocationX = table.Column<uint>(type: "INTEGER", nullable: true),
                    LocationY = table.Column<uint>(type: "INTEGER", nullable: true),
                    AccessLevel = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    AgentCount = table.Column<int>(type: "INTEGER", nullable: true),
                    SizeX = table.Column<uint>(type: "INTEGER", nullable: true),
                    SizeY = table.Column<uint>(type: "INTEGER", nullable: true),
                    ResponseTimeMs = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CheckedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegionStatuses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegionStatus_CheckedAt",
                table: "RegionStatuses",
                column: "CheckedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RegionStatus_Region_Online_Time",
                table: "RegionStatuses",
                columns: new[] { "RegionName", "IsOnline", "CheckedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RegionStatus_Region_Time",
                table: "RegionStatuses",
                columns: new[] { "RegionName", "CheckedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegionStatuses");
        }
    }
}
