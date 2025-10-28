#!/usr/bin/env pwsh
# Safe Migration Script for RadeWeb
# This script creates a safer migration that won't conflict with existing tables

Write-Host "RadegastWeb Safe Migration Creator" -ForegroundColor Green
Write-Host "=================================" -ForegroundColor Green

$DbPath = ".\data\radegast.db"

# Check if database exists
if (-not (Test-Path $DbPath)) {
    Write-Host "Database not found at $DbPath" -ForegroundColor Yellow
    Write-Host "Running normal migration..." -ForegroundColor Blue
    dotnet ef database update --project RadegastWeb.csproj
    exit $LASTEXITCODE
}

Write-Host "Existing database found. Creating safe migration approach..." -ForegroundColor Blue

# Build project first
dotnet build RadegastWeb.csproj --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Create backup
$backupName = "radegast_backup_$(Get-Date -Format 'yyyyMMdd_HHmmss').db"
Write-Host "Creating backup: $backupName" -ForegroundColor Blue
Copy-Item $DbPath ".\data\$backupName"

# Method: Use the safe migration that checks for existing tables
Write-Host "Applying safe migration that preserves existing accounts..." -ForegroundColor Blue

try {
    # Remove the problematic original migration and replace with safe version
    if (Test-Path "Migrations\20251013163043_InitialCreateWithVisitorStats.cs") {
        Write-Host "Backing up original migration..." -ForegroundColor Blue
        Move-Item "Migrations\20251013163043_InitialCreateWithVisitorStats.cs" "Migrations\20251013163043_InitialCreateWithVisitorStats.cs.backup"
        Move-Item "Migrations\20251013163043_InitialCreateWithVisitorStats.Designer.cs" "Migrations\20251013163043_InitialCreateWithVisitorStats.Designer.cs.backup"
    }

    # Copy our safe migration to replace the problematic one
    if (Test-Path "Migrations\20251028120000_SafeInitialCreateWithVisitorStats.cs") {
        Write-Host "Installing safe migration..." -ForegroundColor Blue
        Copy-Item "Migrations\20251028120000_SafeInitialCreateWithVisitorStats.cs" "Migrations\20251013163043_InitialCreateWithVisitorStats.cs"
        
        # Update the class name in the copied file
        $content = Get-Content "Migrations\20251013163043_InitialCreateWithVisitorStats.cs"
        $content = $content -replace "SafeInitialCreateWithVisitorStats", "InitialCreateWithVisitorStats"
        Set-Content "Migrations\20251013163043_InitialCreateWithVisitorStats.cs" $content
    }

    # Ensure migration history table exists
    Write-Host "Ensuring migration history table exists..." -ForegroundColor Blue
    $sqliteAvailable = Get-Command sqlite3 -ErrorAction SilentlyContinue
    
    if ($sqliteAvailable) {
        sqlite3 $DbPath "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT);"
    }

    # Apply the migration (it will now use CREATE TABLE IF NOT EXISTS)
    Write-Host "Applying safe migration..." -ForegroundColor Blue
    dotnet ef database update --project RadegastWeb.csproj --no-build

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Safe migration completed successfully!" -ForegroundColor Green
        Write-Host "Your existing accounts have been preserved." -ForegroundColor Green
        
        # Check that accounts still exist
        if ($sqliteAvailable) {
            $accountCount = sqlite3 $DbPath "SELECT COUNT(*) FROM Accounts;" 2>$null
            if ($accountCount) {
                Write-Host "Verified: $accountCount accounts preserved in database." -ForegroundColor Green
            }
        }
        
        # Clean up backup files if migration was successful
        Write-Host "Migration successful. Original migration files backed up." -ForegroundColor Green
    } else {
        Write-Host "Migration failed. Restoring original files..." -ForegroundColor Red
        
        # Restore original migration files
        if (Test-Path "Migrations\20251013163043_InitialCreateWithVisitorStats.cs.backup") {
            Move-Item "Migrations\20251013163043_InitialCreateWithVisitorStats.cs.backup" "Migrations\20251013163043_InitialCreateWithVisitorStats.cs"
            Move-Item "Migrations\20251013163043_InitialCreateWithVisitorStats.Designer.cs.backup" "Migrations\20251013163043_InitialCreateWithVisitorStats.Designer.cs"
        }
        
        Write-Host "Database backup is available at: .\data\$backupName" -ForegroundColor Yellow
        exit 1
    }
} catch {
    Write-Host "Error during migration: $_" -ForegroundColor Red
    
    # Restore original migration files
    if (Test-Path "Migrations\20251013163043_InitialCreateWithVisitorStats.cs.backup") {
        Move-Item "Migrations\20251013163043_InitialCreateWithVisitorStats.cs.backup" "Migrations\20251013163043_InitialCreateWithVisitorStats.cs"
        Move-Item "Migrations\20251013163043_InitialCreateWithVisitorStats.Designer.cs.backup" "Migrations\20251013163043_InitialCreateWithVisitorStats.Designer.cs"
    }
    
    Write-Host "Database backup is available at: .\data\$backupName" -ForegroundColor Yellow
    exit 1
}

Write-Host "Migration completed. Your existing accounts and data are preserved!" -ForegroundColor Green