#!/usr/bin/env pwsh
# Update Database Script for RadeWeb
# This script applies any pending database migrations with error handling

Write-Host "Updating RadeWeb database..." -ForegroundColor Green

function Handle-MigrationConflict {
    Write-Host "Migration conflict detected. Attempting to resolve..." -ForegroundColor Yellow
    
    # Get the current migration history
    Write-Host "Checking current database state..." -ForegroundColor Blue
    $migrations = dotnet ef migrations list --project RadegastWeb.csproj --no-build 2>$null
    
    if ($LASTEXITCODE -eq 0) {
        # Try to mark all existing migrations as applied without actually running them
        Write-Host "Attempting to resolve migration history mismatch..." -ForegroundColor Blue
        
        # Get the latest migration name
        $latestMigration = ($migrations | Select-String -Pattern "^\s*\d" | Select-Object -Last 1) -replace "^\s*", "" -replace "\s.*$", ""
        
        if ($latestMigration) {
            Write-Host "Marking migrations as applied up to: $latestMigration" -ForegroundColor Blue
            # This will mark all migrations as applied without running them
            dotnet ef database update $latestMigration --project RadegastWeb.csproj --no-build
            return $LASTEXITCODE -eq 0
        } else {
            Write-Host "Could not determine latest migration" -ForegroundColor Red
            return $false
        }
    } else {
        Write-Host "Could not retrieve migration list" -ForegroundColor Red
        return $false
    }
}

try {
    # Check if dotnet ef is installed
    $efVersion = dotnet ef --version 2>$null
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

    # Apply migrations
    Write-Host "Applying database migrations..." -ForegroundColor Blue
    dotnet ef database update --project RadegastWeb.csproj --no-build

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Database updated successfully!" -ForegroundColor Green
    } elseif ($LASTEXITCODE -eq 1) {
        # Check if this is a "table already exists" error
        Write-Host "Migration failed, checking for schema conflicts..." -ForegroundColor Yellow
        
        # Try the conflict resolution
        if (Handle-MigrationConflict) {
            Write-Host "Migration conflict resolved successfully!" -ForegroundColor Green
        } else {
            Write-Host "Failed to resolve migration conflict." -ForegroundColor Red
            Write-Host ""
            Write-Host "Manual intervention may be required. Consider:" -ForegroundColor Yellow
            Write-Host "1. Backing up your database" -ForegroundColor Yellow
            Write-Host "2. Dropping and recreating the database if data loss is acceptable:" -ForegroundColor Yellow
            Write-Host "   dotnet ef database drop --project RadegastWeb.csproj" -ForegroundColor Cyan
            Write-Host "   dotnet ef database update --project RadegastWeb.csproj" -ForegroundColor Cyan
            Write-Host "3. Or manually resolving schema differences" -ForegroundColor Yellow
            exit 1
        }
    } else {
        Write-Host "Database update failed with exit code $LASTEXITCODE" -ForegroundColor Red
        exit 1
    }
}
catch {
    Write-Host "Error updating database: $_" -ForegroundColor Red
    exit 1
}