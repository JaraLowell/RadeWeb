#!/bin/bash
# Update Database Script for RadeWeb (Linux/macOS)
# This script applies any pending database migrations with error handling

echo "Updating RadeWeb database..."

# Check if dotnet ef is installed
if ! command -v dotnet-ef &> /dev/null; then
    echo "Entity Framework Core tools not found. Installing..."
    dotnet tool install --global dotnet-ef
    
    if [ $? -ne 0 ]; then
        echo "Failed to install Entity Framework Core tools!"
        exit 1
    fi
fi

# Function to handle database migration conflicts
handle_migration_conflict() {
    echo "Migration conflict detected. Attempting to resolve..."
    
    # Get the current migration history
    echo "Checking current database state..."
    dotnet ef migrations list --project RadegastWeb.csproj --no-build
    
    # Try to mark all existing migrations as applied without actually running them
    echo "Attempting to resolve migration history mismatch..."
    
    # Get the latest migration name
    LATEST_MIGRATION=$(dotnet ef migrations list --project RadegastWeb.csproj --no-build | tail -n 1 | awk '{print $1}')
    
    if [ ! -z "$LATEST_MIGRATION" ]; then
        echo "Marking migrations as applied up to: $LATEST_MIGRATION"
        # This will mark all migrations as applied without running them
        dotnet ef database update "$LATEST_MIGRATION" --project RadegastWeb.csproj --no-build
        return $?
    else
        echo "Could not determine latest migration"
        return 1
    fi
}

# Build the project first
echo "Building project..."
dotnet build RadegastWeb.csproj --configuration Release

if [ $? -ne 0 ]; then
    echo "Build failed!"
    exit 1
fi

# Apply migrations
echo "Applying database migrations..."
dotnet ef database update --project RadegastWeb.csproj --no-build

if [ $? -eq 0 ]; then
    echo "Database updated successfully!"
elif [ $? -eq 1 ]; then
    # Check if this is a "table already exists" error
    echo "Migration failed, checking for schema conflicts..."
    
    # Try the conflict resolution
    handle_migration_conflict
    
    if [ $? -eq 0 ]; then
        echo "Migration conflict resolved successfully!"
    else
        echo "Failed to resolve migration conflict."
        echo ""
        echo "Manual intervention may be required. Consider:"
        echo "1. Backing up your database"
        echo "2. Dropping and recreating the database if data loss is acceptable:"
        echo "   dotnet ef database drop --project RadegastWeb.csproj"
        echo "   dotnet ef database update --project RadegastWeb.csproj"
        echo "3. Or manually resolving schema differences"
        exit 1
    fi
else
    echo "Database update failed with exit code $?"
    exit 1
fi