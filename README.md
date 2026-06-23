# Jellyfin SmartLists Plugin
<div align="center">
    <p>
        <img alt="Logo" src="https://raw.githubusercontent.com/jyourstone/jellyfin-smartlists-plugin/main/images/logo.jpg" height="350"/><br />
        <a href="https://github.com/jyourstone/jellyfin-smartlists-plugin/releases"><img alt="Total GitHub Downloads" src="https://img.shields.io/github/downloads/jyourstone/jellyfin-smartlists-plugin/total"/></a> <a href="https://github.com/jyourstone/jellyfin-smartlists-plugin/issues"><img alt="GitHub Issues or Pull Requests" src="https://img.shields.io/github/issues/jyourstone/jellyfin-smartlists-plugin"/></a> <a href="https://github.com/jyourstone/jellyfin-smartlists-plugin/releases"><img alt="Build and Release" src="https://github.com/jyourstone/jellyfin-smartlists-plugin/actions/workflows/release.yml/badge.svg"/></a> <a href="https://jellyfin.org/"><img alt="Jellyfin Version" src="https://img.shields.io/badge/Jellyfin-10.11%20%7C%2012.x-blue.svg"/></a>
    </p>        
</div>

Create smart, rule-based **playlists and collections** in Jellyfin.

This plugin allows you to create dynamic playlists and collections based on a set of rules, which will automatically update as your library changes. It works with all media types (movies, episodes, TV shows, music etc.) and features a modern web-based interface for easy list management - no technical knowledge required.

**Requires Jellyfin version `10.11.0` and newer.**

## ✨ Features

- **Modern Web Interface** - A full-featured UI to create, manage and view status for smart playlists and collections
- **External Lists** - Populate lists from [MDBList](https://mdblist.com), [IMDb](https://www.imdb.com), [Trakt](https://trakt.tv), and [TMDB](https://www.themoviedb.org) — trending charts, watchlists, top lists, and more
- **Flexible Rules** - Build simple or complex rules with an intuitive builder
- **Automatic Updates** - Playlists and collections refresh automatically on library updates, playback status changes, or via scheduled tasks
- **Refresh Status & Statistics** - Monitor ongoing refresh operations with real-time progress, view refresh history, and track statistics for all your lists
- **Media Types** - Works with all Jellyfin media types
- **End User Config Page** - Let regular users manage their own smart lists from the home screen (requires [Plugin Pages](https://github.com/IAmParadox27/jellyfin-plugin-pages) and [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugins)
- **And more** - [View the documentation](https://jellyfin-smartlists-plugin.dinsten.se) to see all features

## 🚀 Quick Start

1. **Install the Plugin**: [See installation instructions](#-how-to-install)
2. **Access Plugin Settings**: Click on "SmartLists" in the main sidebar under "Plugins" (or via Dashboard → My Plugins → SmartLists)
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

## 📖 Documentation

### **[View Full Documentation →](https://jellyfin-smartlists-plugin.dinsten.se)**

Complete guide with installation instructions, detailed field descriptions, operators, examples, advanced configuration, and more!

## 💬 Support & Feedback

- **Bug Reports & Feature Requests**: Please use the [Issues tab](https://github.com/jyourstone/jellyfin-smartlists-plugin/issues) to report bugs or suggest new features.
- **Community Support & General Help**: For support questions or general help, please use [Discussions](https://github.com/jyourstone/jellyfin-smartlists-plugin/discussions).

## 📦 How to Install

1. Add this repository URL to your Jellyfin plugin catalog:
```
https://raw.githubusercontent.com/jyourstone/jellyfin-plugin-manifest/main/manifest.json
```
2. Install the plugin
3. Restart Jellyfin

Complete installation instructions can be found [in the documentation](https://jellyfin-smartlists-plugin.dinsten.se/getting-started/installation/).

## 🙏 Credits

This project is based on the original SmartPlaylist plugin created by **[ankenyr](https://github.com/ankenyr)**. You can find the original repository [here](https://github.com/ankenyr/jellyfin-smartplaylist-plugin). All credit for the foundational work and the core idea goes to him.

## ⚠️ Disclaimer

Much of the recent work on this project has been AI-assisted, but the repository, releases, and documentation are reviewed and maintained by the project owner. If you run into a bug or spot something inaccurate, please open an issue or discussion.
