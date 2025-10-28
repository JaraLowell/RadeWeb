#!/bin/bash
# Reset Database Script for RadeWeb (Linux/macOS)
# This script handles database migration conflicts by resetting the migration history

echo "RadeWeb Database Reset Utility"
echo "=============================="

# Check if dotnet ef is installed
if ! command -v dotnet-ef &> /dev/null; then
    echo "Entity Framework Core tools not found. Installing..."
    dotnet tool install --global dotnet-ef
    
    if [ $? -ne 0 ]; then
        echo "Failed to install Entity Framework Core tools!"
        exit 1
    fi
fi

# Build the project first
echo "Building project..."
dotnet build RadegastWeb.csproj --configuration Release

if [ $? -ne 0 ]; then
    echo "Build failed!"
    exit 1
fi

# Function to backup database
backup_database() {
    local db_path="./data/radegast.db"
    if [ -f "$db_path" ]; then
        local backup_name="radegast_backup_$(date +%Y%m%d_%H%M%S).db"
        echo "Creating backup: $backup_name"
        cp "$db_path" "./data/$backup_name"
        echo "Backup created successfully!"
        return 0
    else
        echo "Database file not found at $db_path"
        return 1
    fi
}

# Function to reset migration history
reset_migration_history() {
    echo "Resetting migration history..."
    
    # Delete migration history table if it exists
    sqlite3 "./data/radegast.db" "DROP TABLE IF EXISTS __EFMigrationsHistory;" 2>/dev/null
    
    # Mark all migrations as applied
    echo "Marking all migrations as applied..."
    dotnet ef database update --project RadegastWeb.csproj --no-build
    
    return $?
}

# Function to recreate database
recreate_database() {
    echo "Recreating database from scratch..."
    
    # Drop the database
    dotnet ef database drop --project RadegastWeb.csproj --force
    
    # Create and update database
    dotnet ef database update --project RadegastWeb.csproj --no-build
    
    return $?
}

# Main menu
echo ""
echo "Select an option:"
echo "1) Use safe migration (preserves all data - RECOMMENDED)"
echo "2) Try to fix migration history manually (preserves data)"
echo "3) Backup and recreate database (data loss)"
echo "4) Just recreate database without backup (data loss)"
echo "5) Exit"
echo ""
read -p "Enter your choice (1-5): " choice

case $choice in
    1)
        echo "Using safe migration approach (RECOMMENDED)..."
        if [ -f "./safe-migration.sh" ]; then
            chmod +x ./safe-migration.sh
            ./safe-migration.sh
            exit $?
        else
            echo "Safe migration script not found. Please ensure safe-migration.sh exists."
            exit 1
        fi
        ;;
    2)
        echo "Attempting to fix migration history manually..."
        backup_database
        reset_migration_history
        if [ $? -eq 0 ]; then
            echo "Migration history fixed successfully!"
        else
            echo "Failed to fix migration history. Consider option 3."
            exit 1
        fi
        ;;
    3)
        backup_database
        if [ $? -eq 0 ]; then
            recreate_database
            if [ $? -eq 0 ]; then
                echo "Database recreated successfully!"
            else
                echo "Failed to recreate database!"
                exit 1
            fi
        else
            echo "Backup failed. Aborting recreation."
            exit 1
        fi
        ;;
    4)
        echo "WARNING: This will delete all existing data!"
        read -p "Are you sure? (yes/no): " confirm
        if [ "$confirm" = "yes" ]; then
            recreate_database
            if [ $? -eq 0 ]; then
                echo "Database recreated successfully!"
            else
                echo "Failed to recreate database!"
                exit 1
            fi
        else
            echo "Operation cancelled."
        fi
        ;;
    5)
        echo "Exiting..."
        exit 0
        ;;
    *)
        echo "Invalid choice!"
        exit 1
        ;;
esac

echo "Database reset operation completed!"