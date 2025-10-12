using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RadegastWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddDisplayNameSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DisplayNames",
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
                    CachedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DisplayNames", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DisplayNames_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DisplayName_Account_Avatar",
                table: "DisplayNames",
                columns: new[] { "AccountId", "AvatarId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DisplayName_CachedAt",
                table: "DisplayNames",
                column: "CachedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DisplayNames");
        }
    }
}
