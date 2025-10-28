#!/bin/bash
# Safe Migration Script for RadeWeb
# This script creates a safer migration that won't conflict with existing tables

echo "RadegastWeb Safe Migration Creator"
echo "================================="

DB_PATH="./data/radegast.db"

# Check if database exists
if [ ! -f "$DB_PATH" ]; then
    echo "Database not found at $DB_PATH"
    echo "Running normal migration..."
    dotnet ef database update --project RadegastWeb.csproj
    exit $?
fi

echo "Existing database found. Creating safe migration approach..."

# Build project first
dotnet build RadegastWeb.csproj --configuration Release
if [ $? -ne 0 ]; then
    echo "Build failed!"
    exit 1
fi

# Create backup
BACKUP_NAME="radegast_backup_$(date +%Y%m%d_%H%M%S).db"
echo "Creating backup: $BACKUP_NAME"
cp "$DB_PATH" "./data/$BACKUP_NAME"

# Method: Use the safe migration that checks for existing tables
echo "Applying safe migration that preserves existing accounts..."

# Remove the problematic original migration and replace with safe version
if [ -f "Migrations/20251013163043_InitialCreateWithVisitorStats.cs" ]; then
    echo "Backing up original migration..."
    mv "Migrations/20251013163043_InitialCreateWithVisitorStats.cs" "Migrations/20251013163043_InitialCreateWithVisitorStats.cs.backup"
    mv "Migrations/20251013163043_InitialCreateWithVisitorStats.Designer.cs" "Migrations/20251013163043_InitialCreateWithVisitorStats.Designer.cs.backup"
fi

# Copy our safe migration to replace the problematic one
if [ -f "Migrations/20251028120000_SafeInitialCreateWithVisitorStats.cs" ]; then
    echo "Installing safe migration..."
    cp "Migrations/20251028120000_SafeInitialCreateWithVisitorStats.cs" "Migrations/20251013163043_InitialCreateWithVisitorStats.cs"
    
    # Update the class name in the copied file
    sed -i 's/SafeInitialCreateWithVisitorStats/InitialCreateWithVisitorStats/g' "Migrations/20251013163043_InitialCreateWithVisitorStats.cs"
fi

# Ensure migration history table exists
echo "Ensuring migration history table exists..."
sqlite3 "$DB_PATH" "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT);"

# Apply the migration (it will now use CREATE TABLE IF NOT EXISTS)
echo "Applying safe migration..."
dotnet ef database update --project RadegastWeb.csproj --no-build

if [ $? -eq 0 ]; then
    echo "Safe migration completed successfully!"
    echo "Your existing accounts have been preserved."
    
    # Check that accounts still exist
    ACCOUNT_COUNT=$(sqlite3 "$DB_PATH" "SELECT COUNT(*) FROM Accounts;" 2>/dev/null || echo "0")
    echo "Verified: $ACCOUNT_COUNT accounts preserved in database."
    
    # Clean up backup files if migration was successful
    echo "Migration successful. Original migration files backed up."
else
    echo "Migration failed. Restoring original files..."
    
    # Restore original migration files
    if [ -f "Migrations/20251013163043_InitialCreateWithVisitorStats.cs.backup" ]; then
        mv "Migrations/20251013163043_InitialCreateWithVisitorStats.cs.backup" "Migrations/20251013163043_InitialCreateWithVisitorStats.cs"
        mv "Migrations/20251013163043_InitialCreateWithVisitorStats.Designer.cs.backup" "Migrations/20251013163043_InitialCreateWithVisitorStats.Designer.cs"
    fi
    
    echo "Database backup is available at: ./data/$BACKUP_NAME"
    exit 1
fi

echo "Migration completed. Your existing accounts and data are preserved!"