using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RadegastWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddInteractiveNoticeFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AcceptedResponse",
                table: "Notices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "Notices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRequestId",
                table: "Notices",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasResponse",
                table: "Notices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsInteractive",
                table: "Notices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "RespondedAt",
                table: "Notices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionId",
                table: "Notices",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcceptedResponse",
                table: "Notices");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "Notices");

            migrationBuilder.DropColumn(
                name: "ExternalRequestId",
                table: "Notices");

            migrationBuilder.DropColumn(
                name: "HasResponse",
                table: "Notices");

            migrationBuilder.DropColumn(
                name: "IsInteractive",
                table: "Notices");

            migrationBuilder.DropColumn(
                name: "RespondedAt",
                table: "Notices");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "Notices");
        }
    }
}
