#!/bin/bash
# Quick fix script for "table already exists" migration error
# This script specifically handles the SQLite migration conflict

echo "RadegastWeb Migration Conflict Fix"
echo "================================="

DB_PATH="./data/radegast.db"

# Check if database exists
if [ ! -f "$DB_PATH" ]; then
    echo "Database not found at $DB_PATH"
    echo "Running normal migration..."
    dotnet ef database update --project RadegastWeb.csproj
    exit $?
fi

echo "Database found. Checking for migration conflict..."

# Build project first
dotnet build RadegastWeb.csproj --configuration Release
if [ $? -ne 0 ]; then
    echo "Build failed!"
    exit 1
fi

# Try to apply migrations normally first
echo "Attempting normal migration..."
dotnet ef database update --project RadegastWeb.csproj --no-build 2>&1 | tee migration_output.log

# Check if the error was about tables already existing
if grep -q "already exists" migration_output.log; then
    echo ""
    echo "Migration conflict detected (tables already exist)."
    echo "This happens when the database was created outside of EF migrations."
    echo ""
    
    # Create backup
    BACKUP_NAME="radegast_backup_$(date +%Y%m%d_%H%M%S).db"
    echo "Creating backup: $BACKUP_NAME"
    cp "$DB_PATH" "./data/$BACKUP_NAME"
    
    # Method 1: Try to add all migrations to history without running them
    echo "Attempting to fix migration history..."
    
    # Delete the migration history table to reset it
    sqlite3 "$DB_PATH" "DROP TABLE IF EXISTS __EFMigrationsHistory;" 2>/dev/null
    
    # Get all migration IDs and add them to the history table
    echo "Adding migrations to history table..."
    sqlite3 "$DB_PATH" "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT);"
    
    # Add each migration to the history
    sqlite3 "$DB_PATH" "INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES 
        ('20251013163043_InitialCreateWithVisitorStats', '8.0.0'),
        ('20251022093619_AddAvatarRelayUuid', '8.0.0'),
        ('20251023005245_FixResidentLastNames', '8.0.0'),
        ('20251028111813_AddInteractiveNoticeFields', '8.0.0');"
    
    # Check if the AvatarRelayUuid column exists in the Accounts table
    echo "Checking for missing columns..."
    HAS_AVATAR_RELAY=$(sqlite3 "$DB_PATH" "PRAGMA table_info(Accounts);" | grep -c "AvatarRelayUuid")
    
    if [ "$HAS_AVATAR_RELAY" -eq 0 ]; then
        echo "Adding missing AvatarRelayUuid column..."
        sqlite3 "$DB_PATH" "ALTER TABLE Accounts ADD COLUMN AvatarRelayUuid TEXT;"
    fi
    
    # Try migration again
    echo "Retrying migration..."
    dotnet ef database update --project RadegastWeb.csproj --no-build
    
    if [ $? -eq 0 ]; then
        echo "Migration fixed successfully!"
        rm -f migration_output.log
    else
        echo "Migration still failing. Manual intervention may be required."
        echo "Backup is available at: ./data/$BACKUP_NAME"
        exit 1
    fi
else
    # Check if migration was successful
    if [ $? -eq 0 ]; then
        echo "Migration completed successfully!"
        rm -f migration_output.log
    else
        echo "Migration failed with a different error. Check the output above."
        exit 1
    fi
fi

echo "Database is now up to date!"