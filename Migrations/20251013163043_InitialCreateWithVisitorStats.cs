using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RadegastWeb.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreateWithVisitorStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FirstName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Password = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    GridUrl = table.Column<string>(type: "TEXT", nullable: false),
                    IsConnected = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AvatarUuid = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    CurrentRegion = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                });

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

            migrationBuilder.CreateTable(
                name: "VisitorStats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AvatarId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    RegionName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SimHandle = table.Column<ulong>(type: "INTEGER", nullable: false),
                    VisitDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AvatarName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RegionX = table.Column<uint>(type: "INTEGER", nullable: false),
                    RegionY = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisitorStats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SenderName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    ChatType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Channel = table.Column<string>(type: "TEXT", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SenderUuid = table.Column<string>(type: "TEXT", nullable: true),
                    SenderId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SessionName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RegionName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatMessages_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Notices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    FromName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FromId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    GroupName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    HasAttachment = table.Column<bool>(type: "INTEGER", nullable: false),
                    AttachmentName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AttachmentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RequiresAcknowledgment = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAcknowledged = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notices_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessage_Account_Session_Time",
                table: "ChatMessages",
                columns: new[] { "AccountId", "SessionId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessage_Account_Type_Time",
                table: "ChatMessages",
                columns: new[] { "AccountId", "ChatType", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessage_SessionId",
                table: "ChatMessages",
                column: "SessionId");

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

            migrationBuilder.CreateIndex(
                name: "IX_Notice_Account_Read_Time",
                table: "Notices",
                columns: new[] { "AccountId", "IsRead", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Notice_Account_Time",
                table: "Notices",
                columns: new[] { "AccountId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Notice_Account_Type_Time",
                table: "Notices",
                columns: new[] { "AccountId", "Type", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_VisitorStats_Avatar_Region_Date",
                table: "VisitorStats",
                columns: new[] { "AvatarId", "RegionName", "VisitDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VisitorStats_FirstSeenAt",
                table: "VisitorStats",
                column: "FirstSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_VisitorStats_Region_Date",
                table: "VisitorStats",
                columns: new[] { "RegionName", "VisitDate" });

            migrationBuilder.CreateIndex(
                name: "IX_VisitorStats_VisitDate",
                table: "VisitorStats",
                column: "VisitDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatMessages");

            migrationBuilder.DropTable(
                name: "GlobalDisplayNames");

            migrationBuilder.DropTable(
                name: "Notices");

            migrationBuilder.DropTable(
                name: "VisitorStats");

            migrationBuilder.DropTable(
                name: "Accounts");
        }
    }
}
