using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RadegastWeb.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatMessages");

            migrationBuilder.DropTable(
                name: "Accounts");
        }
    }
}
