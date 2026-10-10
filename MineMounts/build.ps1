# Build Script for MineMount
# Run this script to build the launcher and generate the installer

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  MineMount Build Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if dotnet SDK is installed
$dotnetCmd = "dotnet"
try {
    $dotnetVersion = & $dotnetCmd --version 2>$null
    if (-not $dotnetVersion) { throw "no sdk" }
} catch {
    $localDotnet = "$env:LOCALAPPDATA\dotnet\dotnet.exe"
    if (Test-Path $localDotnet) {
        $dotnetCmd = $localDotnet
        $dotnetVersion = & $dotnetCmd --version
    } else {
        Write-Host "ERROR: .NET 8 SDK not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Red
        exit 1
    }
}
Write-Host "Found .NET SDK: $dotnetVersion ($dotnetCmd)" -ForegroundColor Green

# Check if Inno Setup is installed
$innoPaths = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$innoPath = $innoPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $innoPath) {
    Write-Host "WARNING: Inno Setup not found. Installer will not be generated." -ForegroundColor Yellow
    $hasInno = $false
} else {
    Write-Host "Found Inno Setup: $innoPath" -ForegroundColor Green
    $hasInno = $true
}

Write-Host ""

# Step 1: Restore packages
Write-Host "Step 1: Restoring NuGet packages..." -ForegroundColor Yellow
& $dotnetCmd restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to restore packages." -ForegroundColor Red
    exit 1
}
Write-Host "Packages restored successfully." -ForegroundColor Green
Write-Host ""

# Step 2: Build
Write-Host "Step 2: Building project..." -ForegroundColor Yellow
& $dotnetCmd build -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Build failed." -ForegroundColor Red
    exit 1
}
Write-Host "Build completed successfully." -ForegroundColor Green
Write-Host ""

# Step 3: Publish
Write-Host "Step 3: Publishing as Self-Contained..." -ForegroundColor Yellow
& $dotnetCmd publish MineMount\MineMount.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o Publish
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Publish failed." -ForegroundColor Red
    exit 1
}
Write-Host "Published successfully to Publish\" -ForegroundColor Green
Write-Host ""

# Read app version from csproj (single source of truth)
$appVersion = "1.0.0"
try {
    [xml]$csproj = Get-Content "MineMount\MineMount.csproj"
    $v = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ($v) { $appVersion = $v.Trim() }
} catch {
    Write-Host "WARNING: Could not read version from csproj, using $appVersion." -ForegroundColor Yellow
}
Write-Host "App version: $appVersion" -ForegroundColor Green
Write-Host ""

# Step 4: Generate Installer
if ($hasInno) {
    Write-Host "Step 4: Generating installer..." -ForegroundColor Yellow
    & $innoPath "Installer\MineMount.iss" "/DMyAppVersion=$appVersion"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Installer generation failed." -ForegroundColor Red
        exit 1
    }
    Write-Host "Installer generated successfully." -ForegroundColor Green
    Write-Host ""
} else {
    Write-Host "Step 4: Skipping installer generation (Inno Setup not found)." -ForegroundColor Yellow
    Write-Host ""
}

# Step 5: Update package for the in-app updater (UpdateService expects MineMount-Update.zip
# containing MineMount.exe at the zip root, otherwise CheckForUpdatesAsync reports no update)
Write-Host "Step 5: Building update package..." -ForegroundColor Yellow
$updateZip = "Output\MineMount-Update.zip"
if (Test-Path $updateZip) { Remove-Item $updateZip -Force }
$publishExe = "Publish\MineMount.exe"
if (-not (Test-Path $publishExe)) {
    Write-Host "ERROR: $publishExe not found, cannot build update package." -ForegroundColor Red
    exit 1
}
Compress-Archive -Path $publishExe -DestinationPath $updateZip -CompressionLevel Optimal
Write-Host "Update package created: $updateZip" -ForegroundColor Green
Write-Host ""

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Build Complete!" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Output files:" -ForegroundColor White
Write-Host "  - MineMount.exe: Publish\MineMount.exe" -ForegroundColor Gray
if ($hasInno) {
    Write-Host "  - Installer: Output\MineMount-Setup.exe" -ForegroundColor Gray
}
Write-Host "  - Update package: Output\MineMount-Update.zip" -ForegroundColor Gray
Write-Host ""
Write-Host "Release checklist (GitHub): upload MineMount-Setup.exe, MineMount.exe AND MineMount-Update.zip" -ForegroundColor Yellow
Write-Host ""