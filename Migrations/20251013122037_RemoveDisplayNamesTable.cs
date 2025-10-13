using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RadegastWeb.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDisplayNamesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // First, migrate any data from DisplayNames to GlobalDisplayNames
            migrationBuilder.Sql(@"
                INSERT INTO GlobalDisplayNames (Id, AvatarId, DisplayNameValue, UserName, LegacyFirstName, LegacyLastName, IsDefaultDisplayName, NextUpdate, LastUpdated, CachedAt)
                SELECT 
                    LOWER(HEX(RANDOMBLOB(4)) || '-' || HEX(RANDOMBLOB(2)) || '-' || HEX(RANDOMBLOB(2)) || '-' || HEX(RANDOMBLOB(2)) || '-' || HEX(RANDOMBLOB(6))), -- Generate new GUID
                    AvatarId,
                    DisplayNameValue,
                    UserName,
                    LegacyFirstName,
                    LegacyLastName,
                    IsDefaultDisplayName,
                    NextUpdate,
                    LastUpdated,
                    CachedAt
                FROM DisplayNames 
                WHERE AvatarId NOT IN (SELECT AvatarId FROM GlobalDisplayNames)
                GROUP BY AvatarId
                HAVING MAX(LastUpdated) = LastUpdated;
            ");

            // Now drop the DisplayNames table
            migrationBuilder.DropTable(
                name: "DisplayNames");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Recreate DisplayNames table structure
            migrationBuilder.CreateTable(
                name: "DisplayNames",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
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
                    table.PrimaryKey("PK_DisplayNames", x => x.Id);
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
    }
}
