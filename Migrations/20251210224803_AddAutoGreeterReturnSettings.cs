using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RadegastWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoGreeterReturnSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoGreeterReturnEnabled",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AutoGreeterReturnMessage",
                table: "Accounts",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "AutoGreeterReturnTimeHours",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoGreeterReturnEnabled",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "AutoGreeterReturnMessage",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "AutoGreeterReturnTimeHours",
                table: "Accounts");
        }
    }
}
