# Jellyfin SmartLists Plugin
<div align="center">
    <p>
        <img alt="Logo" src="https://raw.githubusercontent.com/jyourstone/jellyfin-smartlists-plugin/main/images/logo.jpg" height="350"/><br />
        <a href="https://github.com/jyourstone/jellyfin-smartlists-plugin/releases"><img alt="Total GitHub Downloads" src="https://img.shields.io/github/downloads/jyourstone/jellyfin-smartlists-plugin/total"/></a> <a href="https://github.com/jyourstone/jellyfin-smartlists-plugin/issues"><img alt="GitHub Issues or Pull Requests" src="https://img.shields.io/github/issues/jyourstone/jellyfin-smartlists-plugin"/></a> <a href="https://github.com/jyourstone/jellyfin-smartlists-plugin/releases"><img alt="Build and Release" src="https://github.com/jyourstone/jellyfin-smartlists-plugin/actions/workflows/release.yml/badge.svg"/></a> <a href="https://jellyfin.org/"><img alt="Jellyfin Version" src="https://img.shields.io/badge/Jellyfin-10.11-blue.svg"/></a>
    </p>        
</div>

### ✨✨ SmartPlaylist is now SmartLists with support for Collections! ✨✨

Create smart, rule-based playlists and **collections** in Jellyfin.

This plugin allows you to create dynamic playlists and collections based on a set of rules, which will automatically update as your library changes.

**Requires Jellyfin version `10.11.0` and newer.**

## ✨ Features

- 🚀 **Modern Jellyfin Support** - Built for newer Jellyfin versions with improved compatibility
- 🎨 **Modern Web Interface** - A full-featured UI to create, manage and view status for smart playlists and collections
- ✏️ **Edit Lists** - Modify existing smart playlists and collections directly from the UI
- 👥 **User Selection** - Choose which user should own a playlist or collection with an intuitive dropdown
- 🎯 **Flexible Rules** - Build simple or complex rules with an intuitive builder
- 🔄 **Automatic Updates** - Playlists and collections refresh automatically on library updates or via scheduled tasks
- 📊 **Refresh Status & Statistics** - Monitor ongoing refresh operations with real-time progress, view refresh history, and track statistics for all your lists
- 📦 **Export/Import** - Export all lists to a ZIP file for backup or transfer between Jellyfin instances
- 🎵 **Media Types** - Works with all Jellyfin media types

## 🚀 Quick Start

1. **Install the Plugin**: [See installation instructions](#-how-to-install)
2. **Access Plugin Settings**: Go to Dashboard → My Plugins → SmartLists
3. **Create Your First List**: Use the "Create List" tab
4. **Example**: Create a playlist or collection for "Unwatched Action Movies" with:
   - Media type: "Movie"
   - Genre contains "Action"
   - Is Played = False

## ⚙️ Configuration Interface

SmartLists features a modern web-based configuration interface with four main tabs:

<div align="center">
    <p>
        <a href="https://raw.githubusercontent.com/jyourstone/jellyfin-smartlists-plugin/main/images/config_page_create.png" target="_blank" style="cursor: pointer;">
            <img alt="Create list page" src="https://raw.githubusercontent.com/jyourstone/jellyfin-smartlists-plugin/main/images/config_page_create_cropped.png" width="400" style="margin-right: 10px; margin-bottom: 10px;"/>
        </a>
        <a href="https://raw.githubusercontent.com/jyourstone/jellyfin-smartlists-plugin/main/images/config_page_manage.png" target="_blank" style="cursor: pointer;">
            <img alt="Manage lists page" src="https://raw.githubusercontent.com/jyourstone/jellyfin-smartlists-plugin/main/images/config_page_manage_cropped.png" width="400" style="margin-right: 10px; margin-bottom: 10px;"/>
        </a>
    </p>
    <p>
        <a href="https://raw.githubusercontent.com/jyourstone/jellyfin-smartlists-plugin/main/images/config_page_status.png" target="_blank" style="cursor: pointer;">
            <img alt="Status page" src="https://raw.githubusercontent.com/jyourstone/jellyfin-smartlists-plugin/main/images/config_page_status.png" width="400" style="margin-right: 10px;"/>
        </a>
        <a href="https://raw.githubusercontent.com/jyourstone/jellyfin-smartlists-plugin/main/images/config_page_settings.png" target="_blank" style="cursor: pointer;">
            <img alt="Settings page" src="https://raw.githubusercontent.com/jyourstone/jellyfin-smartlists-plugin/main/images/config_page_settings_cropped.png" width="400"/>
        </a>
    </p>
</div>

## 📚 Documentation

<div align="center">

### 📖 **[View Full Documentation →](https://jellyfin-smartlists-plugin.dinsten.se)**

Complete guide with detailed field descriptions, operators, examples, advanced configuration, and more!

</div>

## 💬 Support & Feedback

- **🐛 Bug Reports & Feature Requests**: Please use the [Issues tab](https://github.com/jyourstone/jellyfin-smartlists-plugin/issues) to report bugs or suggest new features.
- **❓ Community Support & General Help**: For support questions or general help, please use [Discussions](https://github.com/jyourstone/jellyfin-smartlists-plugin/discussions).

## 📦 How to Install

### From Repository
Add this repository URL to your Jellyfin plugin catalog:
```
https://raw.githubusercontent.com/jyourstone/jellyfin-plugin-manifest/main/manifest.json
```

### Manual Installation
Download the latest release from the [Releases page](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases) and extract it to a subfolder in your Jellyfin plugins directory (for example `/config/plugins/SmartLists`).

### Try RC Releases (Unstable)
Want to test the latest features before they're officially released? You can try release candidate (RC) versions using the unstable manifest:
```
https://raw.githubusercontent.com/jyourstone/jellyfin-plugin-manifest/unstable/manifest.json
```

**⚠️ Warning**: RC releases are pre-release versions that may contain bugs or incomplete features. Use at your own risk and consider backing up your smart list configurations before upgrading.

## 📋 Overview

This plugin creates smart playlists and collections that automatically update based on rules you define, such as:

- **Unplayed movies** from specific genres
- **Recently added** series or episodes
- **Next unwatched episodes** for "Continue Watching" playlists
- **High-rated** content from certain years
- **Music** from specific artists or albums
- **Tagged content** like "Christmas", "Kids", or "Documentaries"
- And much more!

The plugin features a modern web-based interface for easy list management - no technical knowledge required.

### Supported Media Types

SmartLists works with all media types supported by Jellyfin:

- **🎬 Movie** - Individual movie files
- **📺 Episode** - Individual TV show episodes
- **🎵 Audio (Music)** - Music tracks and albums
- **🎬 Music Video** - Music video files
- **📹 Video (Home Video)** - Personal home videos and recordings
- **📸 Photo (Home Photo)** - Personal photos and images
- **📚 Book** - eBooks, comics, and other readable content
- **🎧 Audiobook** - Spoken word audio books

## 🙏 Credits

This project is based on the original SmartPlaylist plugin created by **[ankenyr](https://github.com/ankenyr)**. You can find the original repository [here](https://github.com/ankenyr/jellyfin-smartplaylist-plugin). All credit for the foundational work and the core idea goes to him.

## ⚠️ Disclaimer

The vast majority of the recent features, including the entire web interface and the underlying API changes in this plugin, were developed by an AI assistant. While I do have some basic experience with C# from a long time ago, I'm essentially the project manager, guiding the AI, fixing its occasional goofs, and trying to keep it from becoming self-aware.
