#!/usr/bin/env pwsh
# Reset Database Script for RadeWeb
# This script handles database migration conflicts by resetting the migration history

Write-Host "RadeWeb Database Reset Utility" -ForegroundColor Green
Write-Host "==============================" -ForegroundColor Green

# Check if dotnet ef is installed
dotnet ef --version 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Entity Framework Core tools not found. Installing..." -ForegroundColor Yellow
    dotnet tool install --global dotnet-ef
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Failed to install Entity Framework Core tools!" -ForegroundColor Red
        exit 1
    }
}

# Build the project first
Write-Host "Building project..." -ForegroundColor Blue
dotnet build RadegastWeb.csproj --configuration Release

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Function to backup database
function Backup-Database {
    $dbPath = ".\data\radegast.db"
    if (Test-Path $dbPath) {
        $backupName = "radegast_backup_$(Get-Date -Format 'yyyyMMdd_HHmmss').db"
        Write-Host "Creating backup: $backupName" -ForegroundColor Blue
        Copy-Item $dbPath ".\data\$backupName"
        Write-Host "Backup created successfully!" -ForegroundColor Green
        return $true
    } else {
        Write-Host "Database file not found at $dbPath" -ForegroundColor Red
        return $false
    }
}

# Function to reset migration history
function Reset-MigrationHistory {
    Write-Host "Resetting migration history..." -ForegroundColor Blue
    
    # Delete migration history table if it exists using sqlite3 or direct SQL
    try {
        # Try to use sqlite3 command if available
        sqlite3 ".\data\radegast.db" "DROP TABLE IF EXISTS __EFMigrationsHistory;" 2>$null
    } catch {
        # If sqlite3 is not available, we'll let EF handle it
        Write-Host "SQLite command line tool not available, proceeding with EF reset..." -ForegroundColor Yellow
    }
    
    # Mark all migrations as applied
    Write-Host "Marking all migrations as applied..." -ForegroundColor Blue
    dotnet ef database update --project RadegastWeb.csproj --no-build
    
    return $LASTEXITCODE -eq 0
}

# Function to recreate database
function Invoke-DatabaseRecreation {
    Write-Host "Recreating database from scratch..." -ForegroundColor Blue
    
    # Drop the database
    dotnet ef database drop --project RadegastWeb.csproj --force
    
    # Create and update database
    dotnet ef database update --project RadegastWeb.csproj --no-build
    
    return $LASTEXITCODE -eq 0
}

# Main menu
Write-Host ""
Write-Host "Select an option:" -ForegroundColor Yellow
Write-Host "1) Try to fix migration history (preserves data)" -ForegroundColor White
Write-Host "2) Backup and recreate database (data loss)" -ForegroundColor White
Write-Host "3) Just recreate database without backup (data loss)" -ForegroundColor White
Write-Host "4) Exit" -ForegroundColor White
Write-Host ""
$choice = Read-Host "Enter your choice (1-4)"

switch ($choice) {
    "1" {
        Write-Host "Attempting to fix migration history..." -ForegroundColor Blue
        Backup-Database
        if (Reset-MigrationHistory) {
            Write-Host "Migration history fixed successfully!" -ForegroundColor Green
        } else {
            Write-Host "Failed to fix migration history. Consider option 2." -ForegroundColor Red
            exit 1
        }
    }
    "2" {
        if (Backup-Database) {
            if (Invoke-DatabaseRecreation) {
                Write-Host "Database recreated successfully!" -ForegroundColor Green
            } else {
                Write-Host "Failed to recreate database!" -ForegroundColor Red
                exit 1
            }
        } else {
            Write-Host "Backup failed. Aborting recreation." -ForegroundColor Red
            exit 1
        }
    }
    "3" {
        Write-Host "WARNING: This will delete all existing data!" -ForegroundColor Red
        $confirm = Read-Host "Are you sure? (yes/no)"
        if ($confirm -eq "yes") {
            if (Invoke-DatabaseRecreation) {
                Write-Host "Database recreated successfully!" -ForegroundColor Green
            } else {
                Write-Host "Failed to recreate database!" -ForegroundColor Red
                exit 1
            }
        } else {
            Write-Host "Operation cancelled." -ForegroundColor Yellow
        }
    }
    "4" {
        Write-Host "Exiting..." -ForegroundColor Yellow
        exit 0
    }
    default {
        Write-Host "Invalid choice!" -ForegroundColor Red
        exit 1
    }
}

Write-Host "Database reset operation completed!" -ForegroundColor Green