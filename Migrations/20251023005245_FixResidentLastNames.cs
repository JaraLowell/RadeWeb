using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RadegastWeb.Migrations
{
    /// <inheritdoc />
    public partial class FixResidentLastNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Update existing records to remove "Resident" from LegacyLastName field
            // This will make the LegacyFullName property return clean names without "Resident"
            migrationBuilder.Sql(@"
                UPDATE GlobalDisplayNames 
                SET LegacyLastName = '' 
                WHERE LegacyLastName = 'Resident'
                   OR LegacyLastName = 'resident'
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Note: Cannot easily restore "Resident" last names since we don't know 
            // which ones originally had it. This is a one-way cleanup migration.
        }
    }
}
