#!/usr/bin/env pwsh
# Build script for RadegastWeb releases
# Supports both Windows PowerShell and PowerShell Core

param(
    [string]$Version = "1.2.0",
    [string]$Configuration = "Release",
    [switch]$Clean = $false,
    [switch]$WindowsOnly = $false,
    [switch]$LinuxOnly = $false
)

Write-Host "RadegastWeb Release Builder" -ForegroundColor Green
Write-Host "Version: $Version" -ForegroundColor Yellow
Write-Host "Configuration: $Configuration" -ForegroundColor Yellow

# Get script directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = $scriptDir
$outputDir = Join-Path $projectDir "releases"
$tempDir = Join-Path $projectDir "temp-build"

# Clean previous builds if requested
if ($Clean) {
    Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
    if (Test-Path $outputDir) {
        Remove-Item $outputDir -Recurse -Force
    }
    if (Test-Path $tempDir) {
        Remove-Item $tempDir -Recurse -Force
    }
    
    # Clean project
    dotnet clean --configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to clean project"
        exit 1
    }
}

# Create output directories
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

# Function to build for a specific runtime
function Build-Runtime {
    param(
        [string]$Runtime,
        [string]$PlatformName
    )
    
    Write-Host "Building for $PlatformName ($Runtime)..." -ForegroundColor Cyan
    
    $publishDir = Join-Path $tempDir $Runtime
    $archiveName = "RadegastWeb-v$Version-$PlatformName-x64"
    
    # Publish the application
    $publishArgs = @(
        "publish"
        "RadegastWeb.csproj"
        "--configuration", $Configuration
        "--runtime", $Runtime
        "--self-contained", "true"
        "--output", $publishDir
        "--verbosity", "minimal"
        "/p:Version=$Version"
        "/p:AssemblyVersion=$Version.0"
        "/p:FileVersion=$Version.0"
        "/p:PublishSingleFile=false"
        "/p:PublishReadyToRun=true"
        "/p:IncludeNativeLibrariesForSelfExtract=true"
        "/p:IncludeAllContentForSelfExtract=true"
    )
    
    & dotnet @publishArgs
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish for $Runtime"
        return $false
    }
    
    # Create startup scripts
    if ($Runtime -eq "win-x64") {
        # Windows batch file
        $startScript = @"
@echo off
cd /d "%~dp0"
echo Starting RadegastWeb...
echo.
echo Web interface will be available at:
echo   http://localhost:5000
echo   https://localhost:5001
echo.
echo Press Ctrl+C to stop the server
echo.
RadegastWeb.exe
pause
"@
        $startScript | Out-File -FilePath (Join-Path $publishDir "start.bat") -Encoding ASCII
        
        # PowerShell script
        $psScript = @"
Write-Host "Starting RadegastWeb..." -ForegroundColor Green
Write-Host ""
Write-Host "Web interface will be available at:" -ForegroundColor Yellow
Write-Host "  http://localhost:5000" -ForegroundColor Cyan
Write-Host "  https://localhost:5001" -ForegroundColor Cyan
Write-Host ""
Write-Host "Press Ctrl+C to stop the server" -ForegroundColor Yellow
Write-Host ""

try {
    & ".\RadegastWeb.exe"
} catch {
    Write-Error "Failed to start RadegastWeb: `$_"
    Read-Host "Press Enter to exit"
}
"@
        $psScript | Out-File -FilePath (Join-Path $publishDir "start.ps1") -Encoding UTF8
    } else {
        # Linux shell script
        $shellScript = @"
#!/bin/bash
cd "`$(dirname "`$0")"
echo "Starting RadegastWeb..."
echo ""
echo "Web interface will be available at:"
echo "  http://localhost:5000"
echo "  https://localhost:5001"
echo ""
echo "Press Ctrl+C to stop the server"
echo ""
./RadegastWeb
"@
        $shellScript | Out-File -FilePath (Join-Path $publishDir "start.sh") -Encoding UTF8 -NoNewline
        
        # Make shell script executable (if on Linux/WSL)
        if (Get-Command chmod -ErrorAction SilentlyContinue) {
            chmod +x (Join-Path $publishDir "start.sh")
        }
    }
    
    # Create README for the release
    $releaseReadme = @"
# RadegastWeb v$Version - $PlatformName Release

## Quick Start

### Windows:
- Double-click `start.bat` or run `start.ps1` in PowerShell
- Or run `RadegastWeb.exe` directly

### Linux:
- Run `./start.sh` or `./RadegastWeb` directly
- Make sure the script is executable: `chmod +x start.sh`

## Web Interface
Once started, access the web interface at:
- http://localhost:5000 (HTTP)
- https://localhost:5001 (HTTPS)

## Configuration
Edit `appsettings.json` to customize:
- Port numbers
- Logging levels
- Database settings
- Authentication settings

## Data Storage
Application data is stored in:
- `./data/` - Database and account data
- `./logs/` - Application logs

## Requirements
- $PlatformName x64
- No additional runtime required (self-contained)

## Support
For issues and documentation, visit: https://github.com/JaraLowell/RadeWeb
"@
    $releaseReadme | Out-File -FilePath (Join-Path $publishDir "README.txt") -Encoding UTF8
    
    # Create the archive
    Write-Host "Creating archive for $PlatformName..." -ForegroundColor Yellow
    
    if ($Runtime -eq "win-x64") {
        # Create ZIP for Windows
        $zipPath = Join-Path $outputDir "$archiveName.zip"
        if (Get-Command Compress-Archive -ErrorAction SilentlyContinue) {
            Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -Force
        } else {
            Write-Warning "Compress-Archive not available. Archive not created for Windows."
            return $false
        }
    } else {
        # Create tar.gz for Linux
        $tarFileName = "$archiveName.tar.gz"
        $tarPath = Join-Path $outputDir $tarFileName
        if (Get-Command tar -ErrorAction SilentlyContinue) {
            Push-Location $publishDir
            tar -czf $tarPath *
            Pop-Location
        } else {
            Write-Warning "tar not available. Creating zip instead of tar.gz for Linux."
            $zipPath = Join-Path $outputDir "$archiveName.zip"
            if (Get-Command Compress-Archive -ErrorAction SilentlyContinue) {
                Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -Force
            } else {
                Write-Warning "Neither tar nor Compress-Archive available. Archive not created for Linux."
                return $false
            }
        }
    }
    
    Write-Host "Successfully built $PlatformName release" -ForegroundColor Green
    return $true
}

# Build targets
$buildTargets = @()

if (-not $LinuxOnly) {
    $buildTargets += @{Runtime = "win-x64"; Platform = "Windows"}
}

if (-not $WindowsOnly) {
    $buildTargets += @{Runtime = "linux-x64"; Platform = "Linux"}
}

$allSuccessful = $true

foreach ($target in $buildTargets) {
    if (-not (Build-Runtime -Runtime $target.Runtime -PlatformName $target.Platform)) {
        $allSuccessful = $false
    }
}

# Cleanup temp directory
if (Test-Path $tempDir) {
    Remove-Item $tempDir -Recurse -Force
}

# Summary
Write-Host ""
Write-Host "Build Summary:" -ForegroundColor Green
Write-Host "=============" -ForegroundColor Green

if ($allSuccessful) {
    Write-Host "All builds completed successfully!" -ForegroundColor Green
    Write-Host "Release packages created in: $outputDir" -ForegroundColor Yellow
    
    Get-ChildItem $outputDir | ForEach-Object {
        $size = [math]::Round($_.Length / 1MB, 2)
        Write-Host "  $($_.Name) ($size MB)" -ForegroundColor Cyan
    }
} else {
    Write-Host "Some builds failed. Check the output above for details." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Done!" -ForegroundColor Green