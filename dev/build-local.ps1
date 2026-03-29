#!/usr/bin/env pwsh

# This script builds the SmartLists plugin and prepares it for local Docker-based testing.
# It will also restart the Jellyfin Docker container to apply the changes.

$ErrorActionPreference = "Stop" # Exit immediately if a command fails

# Set the version for the build. For local testing, this can be a static string.
$VERSION = "10.11.0.0"
$OUTPUT_DIR = "..\build_output"

Write-Host "Building SmartLists plugin version for local development..."

# Clean the previous build output
if (Test-Path $OUTPUT_DIR) {
    Remove-Item -Path $OUTPUT_DIR -Recurse -Force
}
New-Item -ItemType Directory -Path $OUTPUT_DIR -Force | Out-Null

# Build the project
dotnet build ..\Jellyfin.Plugin.SmartLists\Jellyfin.Plugin.SmartLists.csproj --configuration Release -o $OUTPUT_DIR /p:Version=$VERSION /p:AssemblyVersion=$VERSION

# Copy the dev meta.json file, as it's required by Jellyfin to load the plugin
Copy-Item -Path "meta-dev.json" -Destination "$OUTPUT_DIR\meta.json"

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
