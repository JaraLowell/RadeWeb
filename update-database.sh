#!/bin/bash
# Update Database Script for RadeWeb (Linux/macOS)
# This script applies any pending database migrations

echo "Updating RadeWeb database..."

# Check if dotnet ef is installed
if ! command -v dotnet-ef &> /dev/null; then
    echo "Entity Framework Core tools not found. Installing..."
    dotnet tool install --global dotnet-ef
fi

# Apply migrations
echo "Applying database migrations..."
dotnet ef database update --project RadegastWeb.csproj

if [ $? -eq 0 ]; then
    echo "Database updated successfully!"
else
    echo "Database update failed!"
    exit 1
fi