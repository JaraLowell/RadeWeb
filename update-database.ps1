#!/usr/bin/env pwsh
# Update Database Script for RadeWeb
# This script applies any pending database migrations

Write-Host "Updating RadeWeb database..." -ForegroundColor Green

try {
    # Check if dotnet ef is installed
    $efVersion = dotnet ef --version 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Entity Framework Core tools not found. Installing..." -ForegroundColor Yellow
        dotnet tool install --global dotnet-ef
    }

    # Apply migrations
    Write-Host "Applying database migrations..." -ForegroundColor Blue
    dotnet ef database update --project RadegastWeb.csproj

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Database updated successfully!" -ForegroundColor Green
    } else {
        Write-Host "Database update failed!" -ForegroundColor Red
        exit 1
    }
}
catch {
    Write-Host "Error updating database: $_" -ForegroundColor Red
    exit 1
}