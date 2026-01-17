# Installation

## Required Plugins for User Page

For regular users to access SmartLists from their home screen, the following plugins are **required**:

1. **Plugin Pages** - Enables custom pages in the Jellyfin sidebar
   - Repository: [https://github.com/IAmParadox27/jellyfin-plugin-pages](https://github.com/IAmParadox27/jellyfin-plugin-pages)
   
2. **File Transformation** - Required dependency for Plugin Pages
   - Repository: [https://github.com/IAmParadox27/jellyfin-plugin-file-transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)

!!! info "Not Required for Admin Access"
    These plugins are **only needed** if you want regular users to access SmartLists from their home screen. Administrators can always access SmartLists through Dashboard → My Plugins → SmartLists without these plugins.
    
    If these plugins are not installed, the SmartLists plugin will still function normally, but the user-facing page will not be available.

## From Repository

1. **Add the Repository**:
   - Go to **Dashboard** → **Plugins** → **Repositories** (or **Plugins** → **Manage Repositories** → **New Repository**)
   - Click **New Repository** (or the **+** button)
   - Enter the repository URL:
     ```
     https://raw.githubusercontent.com/jyourstone/jellyfin-plugin-manifest/main/manifest.json
     ```
   - Click **Save**

2. **Install Required Plugins** (for user page access):
   - First, install **File Transformation** plugin from its repository
   - Then, install **Plugin Pages** plugin (depends on File Transformation)
   - Installation order matters: File Transformation must be installed before Plugin Pages
   - See repository URLs in the "Required Plugins for User Page" section above
   - Restart Jellyfin after installing both plugins
   - Both Plugin Pages and File Transformation must be installed

3. **Install SmartLists Plugin**:
   - Go to **Dashboard** → **Plugins** → **All/Available**
   - Click **SmartLists** in the list of available plugins
   - Click **Install**
   - Restart Jellyfin after the plugin installation completes

## Manual Installation

Download the latest release from the [Releases page](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases) and extract it to a subfolder in your Jellyfin plugins directory (for example `/config/plugins/SmartLists`) and restart Jellyfin.

## Try RC Releases (Unstable)

Want to test the latest features before they're officially released? You can try release candidate (RC) versions using the unstable manifest:

```
https://raw.githubusercontent.com/jyourstone/jellyfin-plugin-manifest/unstable/manifest.json
```

!!! warning "RC Releases"
    RC releases are pre-release versions that may contain bugs or incomplete features. Use at your own risk and consider backing up your smart list configurations before upgrading.