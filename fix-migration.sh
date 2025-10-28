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
    
    echo "RECOMMENDED: Use the safe migration approach that preserves all existing accounts."
    read -p "Would you like to use the safe migration method? (y/n): " use_safe
    
    if [ "$use_safe" = "y" ] || [ "$use_safe" = "Y" ]; then
        if [ -f "./safe-migration.sh" ]; then
            echo "Using safe migration..."
            chmod +x ./safe-migration.sh
            ./safe-migration.sh
            exit $?
        else
            echo "Safe migration script not found. Falling back to manual method..."
        fi
    fi
    
    # Create backup
    BACKUP_NAME="radegast_backup_$(date +%Y%m%d_%H%M%S).db"
    echo "Creating backup: $BACKUP_NAME"
    cp "$DB_PATH" "./data/$BACKUP_NAME"
    
    # Method 1: Safely sync migration history with existing database
    echo "Attempting to sync migration history with existing database..."
    
    # Create migration history table if it doesn't exist
    echo "Ensuring migration history table exists..."
    sqlite3 "$DB_PATH" "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT);"
    
    # Check which tables actually exist in the database
    echo "Checking existing database structure..."
    EXISTING_TABLES=$(sqlite3 "$DB_PATH" ".tables")
    
    echo "Found existing tables: $EXISTING_TABLES"
    
    # Only add migrations to history if their tables actually exist
    if echo "$EXISTING_TABLES" | grep -q "Accounts"; then
        echo "Accounts table exists - marking InitialCreate migration as applied..."
        sqlite3 "$DB_PATH" "INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('20251013163043_InitialCreateWithVisitorStats', '8.0.0');"
    fi
    
    # Check and add missing columns that were added in later migrations
    echo "Checking for missing columns that should exist..."
    
    # Check for AvatarRelayUuid column (added in migration 20251022093619)
    HAS_AVATAR_RELAY=$(sqlite3 "$DB_PATH" "PRAGMA table_info(Accounts);" | grep -c "AvatarRelayUuid" || echo "0")
    
    if [ "$HAS_AVATAR_RELAY" -eq 0 ]; then
        echo "Adding missing AvatarRelayUuid column..."
        sqlite3 "$DB_PATH" "ALTER TABLE Accounts ADD COLUMN AvatarRelayUuid TEXT;"
        # Mark this migration as applied since we manually added the column
        sqlite3 "$DB_PATH" "INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('20251022093619_AddAvatarRelayUuid', '8.0.0');"
    else
        echo "AvatarRelayUuid column already exists - marking migration as applied..."
        sqlite3 "$DB_PATH" "INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('20251022093619_AddAvatarRelayUuid', '8.0.0');"
    fi
    
    # Check for other migrations that might need to be marked as applied
    if echo "$EXISTING_TABLES" | grep -q "Accounts"; then
        # Mark subsequent migrations as applied if the base structure exists
        sqlite3 "$DB_PATH" "INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES 
            ('20251023005245_FixResidentLastNames', '8.0.0'),
            ('20251028111813_AddInteractiveNoticeFields', '8.0.0');"
    fi
    
    echo "Migration history synchronized with existing database structure."
    
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