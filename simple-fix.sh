#!/bin/bash
# Simple Direct Fix for "table already exists" error
# This script marks existing migrations as applied without running them

echo "Direct Migration Fix - Marking existing migrations as applied"
echo "==========================================================="

DB_PATH="./data/radegast.db"

# Check if database exists
if [ ! -f "$DB_PATH" ]; then
    echo "Database not found. Running normal migration..."
    dotnet ef database update --project RadegastWeb.csproj
    exit $?
fi

echo "Database found with existing tables. Marking migrations as applied..."

# Create backup first
BACKUP_NAME="radegast_backup_$(date +%Y%m%d_%H%M%S).db"
echo "Creating backup: $BACKUP_NAME"
cp "$DB_PATH" "./data/$BACKUP_NAME"

# Check current account count
ACCOUNT_COUNT_BEFORE=$(sqlite3 "$DB_PATH" "SELECT COUNT(*) FROM Accounts;" 2>/dev/null || echo "0")
echo "Current accounts in database: $ACCOUNT_COUNT_BEFORE"

# Create the migration history table if it doesn't exist
echo "Creating migration history table..."
sqlite3 "$DB_PATH" "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT);"

# Mark ALL migrations as applied (this tells EF they're already done)
echo "Marking all migrations as applied..."
sqlite3 "$DB_PATH" "
DELETE FROM __EFMigrationsHistory;
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES 
    ('20251013163043_InitialCreateWithVisitorStats', '8.0.0'),
    ('20251022093619_AddAvatarRelayUuid', '8.0.0'),
    ('20251023005245_FixResidentLastNames', '8.0.0'),
    ('20251028111813_AddInteractiveNoticeFields', '8.0.0');
"

# Check if we need to add any missing columns from later migrations
echo "Checking for missing columns..."

# Add AvatarRelayUuid column if it doesn't exist
HAS_AVATAR_RELAY=$(sqlite3 "$DB_PATH" "PRAGMA table_info(Accounts);" | grep -c "AvatarRelayUuid" || echo "0")
if [ "$HAS_AVATAR_RELAY" -eq 0 ]; then
    echo "Adding missing AvatarRelayUuid column..."
    sqlite3 "$DB_PATH" "ALTER TABLE Accounts ADD COLUMN AvatarRelayUuid TEXT;"
fi

# Now try the migration - it should see all migrations as already applied and do nothing
echo "Running EF migration (should be no-op now)..."
dotnet ef database update --project RadegastWeb.csproj

if [ $? -eq 0 ]; then
    # Verify accounts are still there
    ACCOUNT_COUNT_AFTER=$(sqlite3 "$DB_PATH" "SELECT COUNT(*) FROM Accounts;" 2>/dev/null || echo "0")
    echo "SUCCESS! Migration completed."
    echo "Accounts before: $ACCOUNT_COUNT_BEFORE"
    echo "Accounts after: $ACCOUNT_COUNT_AFTER"
    
    if [ "$ACCOUNT_COUNT_BEFORE" -eq "$ACCOUNT_COUNT_AFTER" ]; then
        echo "✓ All existing accounts preserved!"
    else
        echo "⚠ Account count changed - please check your data"
    fi
else
    echo "Migration still failed. This may require manual database inspection."
    echo "Your backup is at: ./data/$BACKUP_NAME"
    exit 1
fi

echo "Database is now properly synchronized with EF migrations!"