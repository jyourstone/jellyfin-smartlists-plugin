#!/bin/bash

# This script builds the SmartLists plugin and prepares it for local Docker-based testing.
# It will also restart the Jellyfin Docker container to apply the changes.

set -e # Exit immediately if a command exits with a non-zero status.

# Set the Jellyfin ABI for local testing. Defaults to Jellyfin 12 RC/dev.
JELLYFIN_ABI="${JELLYFIN_ABI:-12.0.0}"
VERSION="${VERSION:-${JELLYFIN_ABI}.0}"
case "$JELLYFIN_ABI" in
    10.11.*)
        TARGET_FRAMEWORK="${TARGET_FRAMEWORK:-net9.0}"
        ;;
    12.*)
        TARGET_FRAMEWORK="${TARGET_FRAMEWORK:-net10.0}"
        ;;
    *)
        echo "Unsupported JELLYFIN_ABI '$JELLYFIN_ABI'. Expected 10.11.x or 12.x."
        exit 1
        ;;
esac
OUTPUT_DIR="../build_output"

echo "Building SmartLists plugin for Jellyfin ABI $JELLYFIN_ABI ($TARGET_FRAMEWORK)..."

# Clean the previous build output
rm -rf $OUTPUT_DIR
mkdir -p $OUTPUT_DIR

# Build the project
dotnet build ../Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj --framework "$TARGET_FRAMEWORK" --configuration Release -o "$OUTPUT_DIR" /p:Version="$VERSION" /p:AssemblyVersion="$VERSION"

# Write the dev meta.json file, as it's required by Jellyfin to load the plugin
cat > "$OUTPUT_DIR/meta.json" <<EOF
{
  "guid": "A0A2A7B2-747A-4113-8B39-757A9D267C79",
  "version": "$VERSION",
  "targetAbi": "$JELLYFIN_ABI",
  "imagePath": "logo.jpg"
}
EOF

# Copy the logo image for local plugin display
cp ../images/logo.jpg $OUTPUT_DIR/logo.jpg

# Create the Configuration directory and copy the logging file for debug logs
mkdir -p $OUTPUT_DIR/Configuration
mkdir -p jellyfin-data/config/config
cp logging.json jellyfin-data/config/config/logging.json

echo ""
echo "Build complete."
echo "Restarting Jellyfin container to apply changes..."

# Stop the existing container (if any) and start a new one with the updated plugin files.
docker compose down
docker container prune -f
docker compose up --detach

echo ""
echo "Jellyfin container is up and running."
echo "You can access it at: http://localhost:8096"
