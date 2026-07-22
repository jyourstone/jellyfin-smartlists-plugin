# Jellyfin SmartLists Plugin

<div align="center">
    <p>
        <img alt="Logo" src="../images/logo.jpg" style="height: 400px; width: auto;"/><br />
        <a href="https://github.com/jyourstone/jellyfin-smartlists-plugin/releases"><img alt="Total GitHub Downloads" src="https://img.shields.io/github/downloads/jyourstone/jellyfin-smartlists-plugin/total"/></a> 
        <a href="https://github.com/jyourstone/jellyfin-smartlists-plugin/issues"><img alt="GitHub Issues or Pull Requests" src="https://img.shields.io/github/issues/jyourstone/jellyfin-smartlists-plugin"/></a> 
        <a href="https://github.com/jyourstone/jellyfin-smartlists-plugin/releases"><img alt="Build and Release" src="https://github.com/jyourstone/jellyfin-smartlists-plugin/actions/workflows/release.yml/badge.svg"/></a> 
        <a href="https://jellyfin.org/"><img alt="Jellyfin Version" src="https://img.shields.io/badge/Jellyfin-10.11%20%7C%2012.x-blue.svg"/></a>
    </p>        
</div>

Official documentation for the Jellyfin SmartLists plugin.

SmartLists creates rule-based playlists and collections that refresh automatically as your Jellyfin library changes. The plugin is maintained in the open on GitHub and the documentation in this site tracks the current released behavior of the project.

**Requires Jellyfin version `10.11.0` and newer.**

## Project Links

- **Source code**: [github.com/jyourstone/jellyfin-smartlists-plugin](https://github.com/jyourstone/jellyfin-smartlists-plugin)
- **Releases**: [GitHub Releases](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases)
- **Bug reports and feature requests**: [GitHub Issues](https://github.com/jyourstone/jellyfin-smartlists-plugin/issues)
- **Questions and community help**: [GitHub Discussions](https://github.com/jyourstone/jellyfin-smartlists-plugin/discussions)
- **Plugin manifest**: [Stable manifest](https://raw.githubusercontent.com/jyourstone/jellyfin-plugin-manifest/main/manifest.json)

## What SmartLists Does

- Builds playlists and collections from rules instead of manual curation
- Refreshes lists automatically when library content or playback state changes
- Supports movies, shows, episodes, music, photos, books, and more
- Lets administrators and regular users manage lists through the Jellyfin web UI
- Supports external sources such as IMDb, TMDB, Trakt, MDBList, Letterboxd, and ListenBrainz

## Features

- **Modern Web Interface** - A full-featured UI to create, manage and view status for smart playlists and collections
- **External Lists** - Populate lists from [MDBList](https://mdblist.com), [IMDb](https://www.imdb.com), [Letterboxd](https://letterboxd.com), [Trakt](https://trakt.tv), [TMDB](https://www.themoviedb.org), and [ListenBrainz](https://listenbrainz.org)
- **User Selection** - Choose which users should own a playlist or collection with an intuitive dropdown
- **Flexible Rules** - Build simple or complex rules with an intuitive builder
- **Automatic Updates** - Playlists and collections refresh automatically on library updates, playback status changes, or via scheduled tasks
- **Refresh Status & Statistics** - Monitor ongoing refresh operations with real-time progress, view refresh history, and track statistics for all your lists
- **Media Types** - Works with all Jellyfin media types
- **End User Config Page** - Let regular users manage their own smart lists from the home screen (requires [Plugin Pages](https://github.com/IAmParadox27/jellyfin-plugin-pages) and [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugins)
- **Templates** - Start from built-in templates - TV channel round-robin, Continue Watching, external-list imports, album roulette, and more

## Screenshots

<div align="center" style="display: grid; grid-template-columns: repeat(4, 1fr); gap: 10px; max-width: 1000px; margin: 0 auto;">
    <a href="../images/config_page_create.png" target="_blank" style="cursor: pointer;">
        <img alt="Create list page" src="../images/config_page_create_cropped.png" width="240"/>
    </a>
    <a href="../images/config_page_manage.png" target="_blank" style="cursor: pointer;">
        <img alt="Manage lists page" src="../images/config_page_manage_cropped.png" width="240"/>
    </a>
    <a href="../images/config_page_status.png" target="_blank" style="cursor: pointer;">
        <img alt="Status page" src="../images/config_page_status.png" width="240"/>
    </a>
    <a href="../images/config_page_settings.png" target="_blank" style="cursor: pointer;">
        <img alt="Settings page" src="../images/config_page_settings_cropped.png" width="240"/>
    </a>
</div>

## Supported Media Types

SmartLists works with all media types supported by Jellyfin:

- **Movie** - Individual movie files
- **Series** - TV shows as a whole (can only be used when creating a Collection)
- **Season** - TV show seasons (can only be used when creating a Collection)
- **Episode** - Individual TV show episodes
- **Audio (Music)** - Individual music tracks and songs
- **Album (Music)** - Entire music albums (can only be used when creating a Collection)
- **Music Video** - Music video files
- **Video** - Personal home videos and recordings and extras (trailers, interviews etc.)
- **Photo (Home Photo)** - Personal photos and images
- **Book** - eBooks, comics, and other readable content
- **Audiobook** - Spoken word audio books

## Quick Start

1. **Install the Plugin**: See [Installation Guide](getting-started/installation.md)
2. **Access SmartLists**:
    - **Regular Users**: Click "SmartLists" in your home screen sidebar (requires administrator to configure allowed users in Dashboard → My Plugins → SmartLists → User Selection)
    - **Administrators**: Go to Dashboard → Plugins → SmartLists
3. **Create Your First List**: Use the "Create List" tab
4. **Example**: Create a playlist or collection for "Unwatched Action Movies" with:
    - Media type: "Movie"
    - Genre contains "Action"
    - Playback Status = Unplayed

## Maintenance Notes

This project uses AI-assisted development, but the plugin and docs are reviewed, tested, and maintained through the public GitHub repository. If you find something unclear or incorrect, please open an issue or discussion so it can be fixed.
