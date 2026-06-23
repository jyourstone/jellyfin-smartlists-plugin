#!/usr/bin/env pwsh

# This script builds the SmartLists plugin and prepares it for local Docker-based testing.
# It will also restart the Jellyfin Docker container to apply the changes.

$ErrorActionPreference = "Stop" # Exit immediately if a command fails

# Set the Jellyfin ABI for local testing. Defaults to Jellyfin 12 RC/dev.
$JellyfinAbi = $env:JELLYFIN_ABI
if ([string]::IsNullOrWhiteSpace($JellyfinAbi)) {
    $JellyfinAbi = "12.0.0"
}

$VERSION = $env:VERSION
if ([string]::IsNullOrWhiteSpace($VERSION)) {
    $VERSION = "$JellyfinAbi.0"
}

$TargetFramework = $env:TARGET_FRAMEWORK
if ([string]::IsNullOrWhiteSpace($TargetFramework)) {
    if ($JellyfinAbi.StartsWith("10.11.")) {
        $TargetFramework = "net9.0"
    } elseif ($JellyfinAbi.StartsWith("12.")) {
        $TargetFramework = "net10.0"
    } else {
        throw "Unsupported JELLYFIN_ABI '$JellyfinAbi'. Expected 10.11.x or 12.x."
    }
}

$OUTPUT_DIR = "..\build_output"

Write-Host "Building SmartLists plugin for Jellyfin ABI $JellyfinAbi ($TargetFramework)..."

# Clean the previous build output
if (Test-Path $OUTPUT_DIR) {
    Remove-Item -Path $OUTPUT_DIR -Recurse -Force
}
New-Item -ItemType Directory -Path $OUTPUT_DIR -Force | Out-Null

# Build the project
dotnet build ..\Jellyfin.Plugin.SmartLists\Jellyfin.Plugin.SmartLists.csproj --framework $TargetFramework --configuration Release -o $OUTPUT_DIR /p:Version=$VERSION /p:AssemblyVersion=$VERSION

# Write the dev meta.json file, as it's required by Jellyfin to load the plugin
$meta = [ordered]@{
    guid = "A0A2A7B2-747A-4113-8B39-757A9D267C79"
    version = $VERSION
    targetAbi = $JellyfinAbi
    imagePath = "logo.jpg"
}
$meta | ConvertTo-Json | Set-Content -Path "$OUTPUT_DIR\meta.json" -Encoding UTF8

# Copy the logo image for local plugin display
Copy-Item -Path "..\images\logo.jpg" -Destination "$OUTPUT_DIR\logo.jpg"

# Create the Configuration directory and copy the logging file for debug logs
New-Item -ItemType Directory -Path "$OUTPUT_DIR\Configuration" -Force | Out-Null
New-Item -ItemType Directory -Path "jellyfin-data\config\config" -Force | Out-Null
Copy-Item -Path "logging.json" -Destination "jellyfin-data\config\config\logging.json"

Write-Host ""
Write-Host "Build complete."
Write-Host "Restarting Jellyfin container to apply changes..."

# Stop the existing container (if any) and start a new one with the updated plugin files.
docker compose down
docker container prune -f
docker compose up --detach

Write-Host ""
Write-Host "Jellyfin container is up and running."
Write-Host "You can access it at: http://localhost:8096"
