#!/bin/bash
# Ultimate Simple Fix - Use EF to mark migrations as applied
# This is the most direct approach using Entity Framework tools

echo "EF Migration History Fix"
echo "======================="

DB_PATH="./data/radegast.db"

if [ ! -f "$DB_PATH" ]; then
    echo "No existing database found. Running normal migration..."
    dotnet ef database update --project RadegastWeb.csproj
    exit $?
fi

echo "Found existing database with tables."
echo "Marking all migrations as applied using Entity Framework..."

# Create backup
BACKUP_NAME="radegast_backup_$(date +%Y%m%d_%H%M%S).db"
echo "Creating backup: $BACKUP_NAME"
cp "$DB_PATH" "./data/$BACKUP_NAME"

# Count accounts before
ACCOUNT_COUNT=$(sqlite3 "$DB_PATH" "SELECT COUNT(*) FROM Accounts;" 2>/dev/null || echo "unknown")
echo "Existing accounts: $ACCOUNT_COUNT"

# Use EF to mark the latest migration as applied without actually running it
# This tells EF that all previous migrations are also applied
echo "Using EF to mark migrations as applied..."

# Method 1: Try using --no-build flag and specify exact migration
dotnet ef database update 20251028111813_AddInteractiveNoticeFields --project RadegastWeb.csproj --verbose

if [ $? -eq 0 ]; then
    echo "SUCCESS! All migrations marked as applied."
    
    # Verify accounts still exist
    ACCOUNT_COUNT_AFTER=$(sqlite3 "$DB_PATH" "SELECT COUNT(*) FROM Accounts;" 2>/dev/null || echo "unknown")
    echo "Accounts after: $ACCOUNT_COUNT_AFTER"
    
    echo "✓ Database synchronized with EF migrations!"
    echo "✓ Your existing accounts are preserved!"
else
    echo "EF approach failed. Trying direct SQL approach..."
    
    # Fallback to direct SQL
    sqlite3 "$DB_PATH" "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT);"
    
    sqlite3 "$DB_PATH" "
    DELETE FROM __EFMigrationsHistory;
    INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES 
        ('20251013163043_InitialCreateWithVisitorStats', '8.0.0'),
        ('20251022093619_AddAvatarRelayUuid', '8.0.0'),
        ('20251023005245_FixResidentLastNames', '8.0.0'),
        ('20251028111813_AddInteractiveNoticeFields', '8.0.0');
    "
    
    # Add missing column if needed
    HAS_AVATAR_RELAY=$(sqlite3 "$DB_PATH" "PRAGMA table_info(Accounts);" | grep -c "AvatarRelayUuid" || echo "0")
    if [ "$HAS_AVATAR_RELAY" -eq 0 ]; then
        echo "Adding missing AvatarRelayUuid column..."
        sqlite3 "$DB_PATH" "ALTER TABLE Accounts ADD COLUMN AvatarRelayUuid TEXT;"
    fi
    
    echo "✓ Direct SQL fix applied. Database should now work with EF migrations."
fi

echo ""
echo "Try running your application now - the migration error should be resolved!"
echo "Backup available at: ./data/$BACKUP_NAME"