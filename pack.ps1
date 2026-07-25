# Package QuickDictForICC plugin as .icpx file
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$configuration = if ($env:BuildConfiguration) { $env:BuildConfiguration } else { "Release" }
$publishDir = Join-Path $scriptDir "bin\$configuration\net6.0-windows10.0.19041.0\publish"
$packagesDir = Join-Path $scriptDir "packages"

# Auto-publish if the publish folder is missing
if (-not (Test-Path $publishDir)) {
    Write-Host "Publish folder not found, running dotnet publish -c $configuration ..." -ForegroundColor Yellow
    dotnet publish (Join-Path $scriptDir "QuickDictForICC.csproj") -c $configuration --self-contained false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

# Read manifest to obtain plugin ID and version
$manifestPath = Join-Path $publishDir "manifest.json"
if (-not (Test-Path $manifestPath)) { throw "manifest.json not found at $manifestPath. Please build the project first." }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$pluginId = $manifest.Id
$version = $manifest.Version

Write-Host "Packaging plugin: $pluginId v$version" -ForegroundColor Cyan
Write-Host "Source: $publishDir" -ForegroundColor Gray

# Ensure packages directory exists
if (-not (Test-Path $packagesDir)) {
    New-Item -ItemType Directory -Path $packagesDir | Out-Null
}

# Create temporary directory
$tempDir = Join-Path $env:TEMP "icpx_package_$pluginId"
if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
New-Item -ItemType Directory -Path $tempDir | Out-Null

# Copy all published files (plugin assembly, manifest, deps.json and all dependency DLLs)
$items = Get-ChildItem -Path $publishDir -Recurse
foreach ($item in $items) {
    $relativePath = $item.FullName.Substring($publishDir.Length + 1)
    $destPath = Join-Path $tempDir $relativePath
    if ($item.PSIsContainer) {
        New-Item -ItemType Directory -Path $destPath -Force | Out-Null
    } else {
        Copy-Item $item.FullName $destPath -Force
        Write-Host "  + $relativePath"
    }
}

# Package as .icpx (ZIP)
$icpxName = "$pluginId.icpx"
$zipPath = Join-Path $packagesDir ($pluginId + ".zip")
$icpxPath = Join-Path $packagesDir $icpxName

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $tempDir '*') -DestinationPath $zipPath -Force

if (Test-Path $icpxPath) { Remove-Item $icpxPath -Force }
Rename-Item $zipPath $icpxName

# Compute SHA256
$hash = (Get-FileHash $icpxPath -Algorithm SHA256).Hash.ToLower()
$fileSize = (Get-Item $icpxPath).Length
Write-Host ""
Write-Host "Package created: $icpxPath" -ForegroundColor Green
Write-Host "File size: $fileSize bytes"
Write-Host "SHA256:    $hash"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Create a GitHub Release for QuickDictForICC v$version"
Write-Host "  2. Upload $icpxName as a Release Asset"
Write-Host "  3. Update DownloadUrl and DownloadSha256 in PluginIndex/index.json"
Write-Host "  4. Push the PluginIndex repository"

# Cleanup
Remove-Item $tempDir -Recurse -Force
