using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RadegastWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddNoticesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccountId1 = table.Column<Guid>(type: "TEXT", nullable: true)
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
                    table.ForeignKey(
                        name: "FK_Notices_Accounts_AccountId1",
                        column: x => x.AccountId1,
                        principalTable: "Accounts",
                        principalColumn: "Id");
                });

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
                name: "IX_Notices_AccountId1",
                table: "Notices",
                column: "AccountId1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Notices");
        }
    }
}
