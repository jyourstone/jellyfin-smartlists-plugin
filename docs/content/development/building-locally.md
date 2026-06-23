# Building Locally

This guide explains how to set up a local development environment for the SmartLists plugin.

For the source code and repository, see: [jellyfin-smartlists-plugin](https://github.com/jyourstone/jellyfin-smartlists-plugin)

## Prerequisites

Make sure you have the following installed on your system:

- [Docker](https://www.docker.com/)
- [.NET SDK](https://dotnet.microsoft.com/)

## Development Environment

The development environment is contained in the [`/dev` directory](https://github.com/jyourstone/jellyfin-smartlists-plugin/tree/main/dev). This folder contains all the files needed for local development and testing of the SmartLists plugin.

### Files in the Dev Folder

- **`build-local.sh`** – Bash script to build the plugin and restart the Docker container (Linux/macOS/WSL)
- **`build-local.ps1`** – PowerShell script to build the plugin and restart the Docker container (Windows)
- **`docker-compose.yml`** – Docker Compose configuration for local Jellyfin testing  
- **`meta-dev.json`** – Development version of the plugin metadata  
- **`jellyfin-data/`** – Jellyfin data, gets created when built (persistent across restarts)  
- **`media/`** – Media directories for testing (place your test media files here)

!!! important "Important"
    For local testing, don't modify files outside `/dev` to prevent accidental changes. However, if you want to contribute improvements, you can edit any files and create a pull request. See the [Contributing](contributing.md) guide for details.

## How to Use

### 1. Build and Run the Plugin Locally

**Linux/macOS/WSL:**
```bash
cd dev
./build-local.sh
```

**Windows PowerShell:**
```powershell
cd dev
.\build-local.ps1
```

The build scripts automatically:
- Build the plugin for the configured Jellyfin ABI
- Restart the Jellyfin Docker container
- Make the plugin available in your local Jellyfin instance

By default, the scripts build for Jellyfin ABI `12.0.0` using `net10.0`, which matches the Jellyfin 12 RC development container. To test against Jellyfin 10.11, set `JELLYFIN_ABI=10.11.0` before running the script:

```bash
cd dev
JELLYFIN_ABI=10.11.0 ./build-local.sh
```

```powershell
cd dev
$env:JELLYFIN_ABI = "10.11.0"
.\build-local.ps1
```

### 2. Access Jellyfin

- Open [http://localhost:8096](http://localhost:8096) in your browser  
- Complete the initial setup if it's your first time  
- The plugin will appear as **"SmartLists"** in the plugin list

### 3. Add Test Media

Place your test media files in the appropriate directories:

- **Movies**: `dev/media/movies/`  
- **TV Shows**: `dev/media/shows/`
- **Music**: `dev/media/music/`  
- **Music Videos**: `dev/media/musicvideos/`

After adding media, you may need to trigger a library scan in Jellyfin.

## Important Notes

### Jellyfin Data Directory

The **`jellyfin-data`** directory stores Jellyfin's configuration and data, including:
- Logs
- Playlists
- User information
- Plugin configurations

This directory is mounted into the container so your data persists across restarts. The `smartlists` folder inside `jellyfin-data/config/data` is where your created playlists are stored.

### Logs

Logs can be accessed in:
```
dev/jellyfin-data/config/log/
```

### Development Metadata

**`meta-dev.json`** is a development-specific plugin manifest for the default Jellyfin 12 development setup. The build scripts generate `build_output/meta.json` from the selected `JELLYFIN_ABI` value during local builds.
