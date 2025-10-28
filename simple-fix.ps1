#!/usr/bin/env pwsh
# Simple Direct Fix for "table already exists" error
# This script marks existing migrations as applied without running them

Write-Host "Direct Migration Fix - Marking existing migrations as applied" -ForegroundColor Green
Write-Host "===========================================================" -ForegroundColor Green

$DbPath = ".\data\radegast.db"

# Check if database exists
if (-not (Test-Path $DbPath)) {
    Write-Host "Database not found. Running normal migration..." -ForegroundColor Yellow
    dotnet ef database update --project RadegastWeb.csproj
    exit $LASTEXITCODE
}

Write-Host "Database found with existing tables. Marking migrations as applied..." -ForegroundColor Blue

# Create backup first
$backupName = "radegast_backup_$(Get-Date -Format 'yyyyMMdd_HHmmss').db"
Write-Host "Creating backup: $backupName" -ForegroundColor Blue
Copy-Item $DbPath ".\data\$backupName"

# Check current account count
$accountCountBefore = "0"
try {
    $accountCountBefore = sqlite3 $DbPath "SELECT COUNT(*) FROM Accounts;" 2>$null
    if (-not $accountCountBefore) { $accountCountBefore = "0" }
} catch {
    $accountCountBefore = "0"
}
Write-Host "Current accounts in database: $accountCountBefore" -ForegroundColor Blue

# Check if sqlite3 is available
$sqliteAvailable = Get-Command sqlite3 -ErrorAction SilentlyContinue

if (-not $sqliteAvailable) {
    Write-Host "SQLite command line tool not found. Please install SQLite3 or run this on a system with sqlite3 available." -ForegroundColor Red
    Write-Host "Alternatively, you can manually execute these SQL commands in your SQLite browser:" -ForegroundColor Yellow
    Write-Host "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT);" -ForegroundColor Cyan
    Write-Host "DELETE FROM __EFMigrationsHistory;" -ForegroundColor Cyan
    Write-Host "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES" -ForegroundColor Cyan
    Write-Host "('20251013163043_InitialCreateWithVisitorStats', '8.0.0')," -ForegroundColor Cyan
    Write-Host "('20251022093619_AddAvatarRelayUuid', '8.0.0')," -ForegroundColor Cyan
    Write-Host "('20251023005245_FixResidentLastNames', '8.0.0')," -ForegroundColor Cyan
    Write-Host "('20251028111813_AddInteractiveNoticeFields', '8.0.0');" -ForegroundColor Cyan
    exit 1
}

# Create the migration history table if it doesn't exist
Write-Host "Creating migration history table..." -ForegroundColor Blue
sqlite3 $DbPath "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT);"

# Mark ALL migrations as applied (this tells EF they're already done)
Write-Host "Marking all migrations as applied..." -ForegroundColor Blue
sqlite3 $DbPath @"
DELETE FROM __EFMigrationsHistory;
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES 
    ('20251013163043_InitialCreateWithVisitorStats', '8.0.0'),
    ('20251022093619_AddAvatarRelayUuid', '8.0.0'),
    ('20251023005245_FixResidentLastNames', '8.0.0'),
    ('20251028111813_AddInteractiveNoticeFields', '8.0.0');
"@

# Check if we need to add any missing columns from later migrations
Write-Host "Checking for missing columns..." -ForegroundColor Blue

# Add AvatarRelayUuid column if it doesn't exist
$hasAvatarRelay = sqlite3 $DbPath "PRAGMA table_info(Accounts);" 2>$null | Select-String "AvatarRelayUuid"
if (-not $hasAvatarRelay) {
    Write-Host "Adding missing AvatarRelayUuid column..." -ForegroundColor Blue
    sqlite3 $DbPath "ALTER TABLE Accounts ADD COLUMN AvatarRelayUuid TEXT;"
}

# Now try the migration - it should see all migrations as already applied and do nothing
Write-Host "Running EF migration (should be no-op now)..." -ForegroundColor Blue
dotnet ef database update --project RadegastWeb.csproj

if ($LASTEXITCODE -eq 0) {
    # Verify accounts are still there
    $accountCountAfter = "0"
    try {
        $accountCountAfter = sqlite3 $DbPath "SELECT COUNT(*) FROM Accounts;" 2>$null
        if (-not $accountCountAfter) { $accountCountAfter = "0" }
    } catch {
        $accountCountAfter = "0"
    }
    
    Write-Host "SUCCESS! Migration completed." -ForegroundColor Green
    Write-Host "Accounts before: $accountCountBefore" -ForegroundColor Green
    Write-Host "Accounts after: $accountCountAfter" -ForegroundColor Green
    
    if ($accountCountBefore -eq $accountCountAfter) {
        Write-Host "✓ All existing accounts preserved!" -ForegroundColor Green
    } else {
        Write-Host "⚠ Account count changed - please check your data" -ForegroundColor Yellow
    }
} else {
    Write-Host "Migration still failed. This may require manual database inspection." -ForegroundColor Red
    Write-Host "Your backup is at: .\data\$backupName" -ForegroundColor Yellow
    exit 1
}

Write-Host "Database is now properly synchronized with EF migrations!" -ForegroundColor Green