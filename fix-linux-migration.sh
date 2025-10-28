#!/bin/bash
# Fix migration script - applies the "CREATE TABLE IF NOT EXISTS" fix
# Run this on your Linux machine to fix the migration file

echo "Fixing migration file to use CREATE TABLE IF NOT EXISTS..."

MIGRATION_FILE="Migrations/20251013163043_InitialCreateWithVisitorStats.cs"

if [ ! -f "$MIGRATION_FILE" ]; then
    echo "Migration file not found: $MIGRATION_FILE"
    exit 1
fi

# Create backup
cp "$MIGRATION_FILE" "$MIGRATION_FILE.backup"
echo "Created backup: $MIGRATION_FILE.backup"

# Apply the fix using sed to replace CreateTable with CREATE TABLE IF NOT EXISTS
echo "Applying fix..."

# This creates a completely new migration file with the correct IF NOT EXISTS syntax
cat > "$MIGRATION_FILE" << 'EOF'
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
            // Use raw SQL with IF NOT EXISTS to prevent "table already exists" errors
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""Accounts"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_Accounts"" PRIMARY KEY,
                    ""FirstName"" TEXT NOT NULL,
                    ""LastName"" TEXT NOT NULL,
                    ""Password"" TEXT NOT NULL,
                    ""DisplayName"" TEXT NOT NULL,
                    ""GridUrl"" TEXT NOT NULL,
                    ""IsConnected"" INTEGER NOT NULL,
                    ""CreatedAt"" TEXT NOT NULL,
                    ""LastLoginAt"" TEXT NULL,
                    ""AvatarUuid"" TEXT NULL,
                    ""CurrentRegion"" TEXT NULL,
                    ""Status"" TEXT NOT NULL
                );
            ");

            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""GlobalDisplayNames"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_GlobalDisplayNames"" PRIMARY KEY,
                    ""AvatarId"" TEXT NOT NULL,
                    ""DisplayNameValue"" TEXT NOT NULL,
                    ""UserName"" TEXT NOT NULL,
                    ""LegacyFirstName"" TEXT NOT NULL,
                    ""LegacyLastName"" TEXT NOT NULL,
                    ""IsDefaultDisplayName"" INTEGER NOT NULL,
                    ""NextUpdate"" TEXT NOT NULL,
                    ""LastUpdated"" TEXT NOT NULL,
                    ""CachedAt"" TEXT NOT NULL
                );
            ");

            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""VisitorStats"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_VisitorStats"" PRIMARY KEY,
                    ""AvatarId"" TEXT NOT NULL,
                    ""RegionName"" TEXT NOT NULL,
                    ""SimHandle"" INTEGER NOT NULL,
                    ""VisitDate"" TEXT NOT NULL,
                    ""FirstSeenAt"" TEXT NOT NULL,
                    ""LastSeenAt"" TEXT NOT NULL,
                    ""AvatarName"" TEXT NULL,
                    ""DisplayName"" TEXT NULL,
                    ""RegionX"" INTEGER NOT NULL,
                    ""RegionY"" INTEGER NOT NULL
                );
            ");

            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""ChatMessages"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_ChatMessages"" PRIMARY KEY,
                    ""AccountId"" TEXT NOT NULL,
                    ""SenderName"" TEXT NOT NULL,
                    ""Message"" TEXT NOT NULL,
                    ""ChatType"" TEXT NOT NULL,
                    ""Channel"" TEXT NULL,
                    ""Timestamp"" TEXT NOT NULL,
                    ""SenderUuid"" TEXT NULL,
                    ""SenderId"" TEXT NULL,
                    ""TargetId"" TEXT NULL,
                    ""SessionId"" TEXT NULL,
                    ""SessionName"" TEXT NULL,
                    ""RegionName"" TEXT NULL,
                    CONSTRAINT ""FK_ChatMessages_Accounts_AccountId"" FOREIGN KEY (""AccountId"") REFERENCES ""Accounts"" (""Id"") ON DELETE CASCADE
                );
            ");

            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""Notices"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_Notices"" PRIMARY KEY,
                    ""AccountId"" TEXT NOT NULL,
                    ""Title"" TEXT NOT NULL,
                    ""Message"" TEXT NOT NULL,
                    ""FromName"" TEXT NOT NULL,
                    ""FromId"" TEXT NOT NULL,
                    ""GroupId"" TEXT NULL,
                    ""GroupName"" TEXT NULL,
                    ""Timestamp"" TEXT NOT NULL,
                    ""Type"" TEXT NOT NULL,
                    ""HasAttachment"" INTEGER NOT NULL,
                    ""AttachmentName"" TEXT NULL,
                    ""AttachmentType"" TEXT NULL,
                    ""RequiresAcknowledgment"" INTEGER NOT NULL,
                    ""IsAcknowledged"" INTEGER NOT NULL,
                    ""IsRead"" INTEGER NOT NULL,
                    CONSTRAINT ""FK_Notices_Accounts_AccountId"" FOREIGN KEY (""AccountId"") REFERENCES ""Accounts"" (""Id"") ON DELETE CASCADE
                );
            ");

            // Create indexes only if they don't exist
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_ChatMessage_Account_Session_Time"" ON ""ChatMessages"" (""AccountId"", ""SessionId"", ""Timestamp"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_ChatMessage_Account_Type_Time"" ON ""ChatMessages"" (""AccountId"", ""ChatType"", ""Timestamp"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_ChatMessage_SessionId"" ON ""ChatMessages"" (""SessionId"");");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_GlobalDisplayName_Avatar"" ON ""GlobalDisplayNames"" (""AvatarId"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_GlobalDisplayName_CachedAt"" ON ""GlobalDisplayNames"" (""CachedAt"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_GlobalDisplayName_LastUpdated"" ON ""GlobalDisplayNames"" (""LastUpdated"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_Notice_Account_Read_Time"" ON ""Notices"" (""AccountId"", ""IsRead"", ""Timestamp"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_Notice_Account_Time"" ON ""Notices"" (""AccountId"", ""Timestamp"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_Notice_Account_Type_Time"" ON ""Notices"" (""AccountId"", ""Type"", ""Timestamp"");");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_VisitorStats_Avatar_Region_Date"" ON ""VisitorStats"" (""AvatarId"", ""RegionName"", ""VisitDate"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_VisitorStats_FirstSeenAt"" ON ""VisitorStats"" (""FirstSeenAt"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_VisitorStats_Region_Date"" ON ""VisitorStats"" (""RegionName"", ""VisitDate"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_VisitorStats_VisitDate"" ON ""VisitorStats"" (""VisitDate"");");
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
EOF

echo "✅ Migration file fixed!"
echo "The migration now uses 'CREATE TABLE IF NOT EXISTS' instead of 'CREATE TABLE'"
echo ""
echo "Now run: dotnet ef database update --project RadegastWeb.csproj"
echo ""
echo "Your existing accounts will be preserved!"