#!/usr/bin/env pwsh
# Quick fix script for "table already exists" migration error
# This script specifically handles the SQLite migration conflict

Write-Host "RadegastWeb Migration Conflict Fix" -ForegroundColor Green
Write-Host "=================================" -ForegroundColor Green

$DbPath = ".\data\radegast.db"

# Check if database exists
if (-not (Test-Path $DbPath)) {
    Write-Host "Database not found at $DbPath" -ForegroundColor Yellow
    Write-Host "Running normal migration..." -ForegroundColor Blue
    dotnet ef database update --project RadegastWeb.csproj
    exit $LASTEXITCODE
}

Write-Host "Database found. Checking for migration conflict..." -ForegroundColor Blue

# Build project first
dotnet build RadegastWeb.csproj --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Try to apply migrations normally first
Write-Host "Attempting normal migration..." -ForegroundColor Blue
$migrationOutput = dotnet ef database update --project RadegastWeb.csproj --no-build 2>&1
$migrationExitCode = $LASTEXITCODE

# Output the migration result
$migrationOutput

# Check if the error was about tables already existing
if ($migrationOutput -match "already exists") {
    Write-Host ""
    Write-Host "Migration conflict detected (tables already exist)." -ForegroundColor Yellow
    Write-Host "This happens when the database was created outside of EF migrations." -ForegroundColor Yellow
    Write-Host ""
    
    # Create backup
    $backupName = "radegast_backup_$(Get-Date -Format 'yyyyMMdd_HHmmss').db"
    Write-Host "Creating backup: $backupName" -ForegroundColor Blue
    Copy-Item $DbPath ".\data\$backupName"
    
    # Method 1: Try to add all migrations to history without running them
    Write-Host "Attempting to fix migration history..." -ForegroundColor Blue
    
    try {
        # Try to use sqlite3 command if available, otherwise use .NET SQLite
        $sqliteAvailable = Get-Command sqlite3 -ErrorAction SilentlyContinue
        
        if ($sqliteAvailable) {
            # Delete the migration history table to reset it
            sqlite3 $DbPath "DROP TABLE IF EXISTS __EFMigrationsHistory;" 2>$null
            
            # Get all migration IDs and add them to the history table
            Write-Host "Adding migrations to history table..." -ForegroundColor Blue
            sqlite3 $DbPath "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT);"
            
            # Add each migration to the history
            sqlite3 $DbPath @"
INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES 
    ('20251013163043_InitialCreateWithVisitorStats', '8.0.0'),
    ('20251022093619_AddAvatarRelayUuid', '8.0.0'),
    ('20251023005245_FixResidentLastNames', '8.0.0'),
    ('20251028111813_AddInteractiveNoticeFields', '8.0.0');
"@
            
            # Check if the AvatarRelayUuid column exists in the Accounts table
            Write-Host "Checking for missing columns..." -ForegroundColor Blue
            $columnCheck = sqlite3 $DbPath "PRAGMA table_info(Accounts);" | Select-String "AvatarRelayUuid"
            
            if (-not $columnCheck) {
                Write-Host "Adding missing AvatarRelayUuid column..." -ForegroundColor Blue
                sqlite3 $DbPath "ALTER TABLE Accounts ADD COLUMN AvatarRelayUuid TEXT;"
            }
        } else {
            Write-Host "SQLite command line tool not available. Using alternative method..." -ForegroundColor Yellow
            # Alternative: Use EF to mark migrations as applied
            dotnet ef database update 20251028111813_AddInteractiveNoticeFields --project RadegastWeb.csproj --no-build
        }
        
        # Try migration again
        Write-Host "Retrying migration..." -ForegroundColor Blue
        dotnet ef database update --project RadegastWeb.csproj --no-build
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Migration fixed successfully!" -ForegroundColor Green
        } else {
            Write-Host "Migration still failing. Manual intervention may be required." -ForegroundColor Red
            Write-Host "Backup is available at: .\data\$backupName" -ForegroundColor Yellow
            exit 1
        }
    } catch {
        Write-Host "Error during migration fix: $_" -ForegroundColor Red
        Write-Host "Backup is available at: .\data\$backupName" -ForegroundColor Yellow
        exit 1
    }
} else {
    # Check if migration was successful
    if ($migrationExitCode -eq 0) {
        Write-Host "Migration completed successfully!" -ForegroundColor Green
    } else {
        Write-Host "Migration failed with a different error. Check the output above." -ForegroundColor Red
        exit 1
    }
}

Write-Host "Database is now up to date!" -ForegroundColor Green