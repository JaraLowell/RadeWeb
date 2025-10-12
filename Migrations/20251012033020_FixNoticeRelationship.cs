using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RadegastWeb.Migrations
{
    /// <inheritdoc />
    public partial class FixNoticeRelationship : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Notices_Accounts_AccountId1",
                table: "Notices");

            migrationBuilder.DropIndex(
                name: "IX_Notices_AccountId1",
                table: "Notices");

            migrationBuilder.DropColumn(
                name: "AccountId1",
                table: "Notices");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AccountId1",
                table: "Notices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notices_AccountId1",
                table: "Notices",
                column: "AccountId1");

            migrationBuilder.AddForeignKey(
                name: "FK_Notices_Accounts_AccountId1",
                table: "Notices",
                column: "AccountId1",
                principalTable: "Accounts",
                principalColumn: "Id");
        }
    }
}
