# Jellyfin SmartLists Plugin

<div align="center">
    <p>
        <img alt="Logo" src="https://raw.githubusercontent.com/jyourstone/jellyfin-smartlists-plugin/main/images/logo.jpg" style="height: 400px; width: auto;"/><br />
        <a href="https://github.com/jyourstone/jellyfin-smartlists-plugin/releases"><img alt="Total GitHub Downloads" src="https://img.shields.io/github/downloads/jyourstone/jellyfin-smartlists-plugin/total"/></a> 
        <a href="https://github.com/jyourstone/jellyfin-smartlists-plugin/issues"><img alt="GitHub Issues or Pull Requests" src="https://img.shields.io/github/issues/jyourstone/jellyfin-smartlists-plugin"/></a> 
        <a href="https://github.com/jyourstone/jellyfin-smartlists-plugin/releases"><img alt="Build and Release" src="https://github.com/jyourstone/jellyfin-smartlists-plugin/actions/workflows/release.yml/badge.svg"/></a> 
        <a href="https://jellyfin.org/"><img alt="Jellyfin Version" src="https://img.shields.io/badge/Jellyfin-10.11-blue.svg"/></a>
    </p>        
</div>

Create smart, rule-based playlists and collections in Jellyfin.

This plugin allows you to create dynamic playlists and collections based on a set of rules, which will automatically update as your library changes.

**Requires Jellyfin version `10.11.0` and newer.**

## Features

- 🚀 **Modern Jellyfin Support** - Built for newer Jellyfin versions with improved compatibility
- 🎨 **Modern Web Interface** - A full-featured UI to create, edit, view and delete smart playlists and collections
- ✏️ **Edit Lists** - Modify existing smart playlists and collections directly from the UI
- 👥 **Multi-User Playlists** - Create playlists for multiple users, with each user getting their own personalized version based on their playback data
- 🎯 **Flexible Rules** - Build simple or complex rules with an intuitive builder
- 🔄 **Automatic Updates** - Playlists and collections refresh automatically on library updates or via scheduled tasks
- 📦 **Export/Import** - Export all lists to a ZIP file for backup or transfer between Jellyfin instances
- 🎵 **Media Types** - Works with all Jellyfin media types

## Supported Media Types

SmartLists works with all media types supported by Jellyfin:

- **🎬 Movie** - Individual movie files
- **📺 Series** - TV shows as a whole (can only be used when creating a Collection)
- **📺 Episode** - Individual TV show episodes
- **🎵 Audio (Music)** - Music tracks and albums
- **🎬 Music Video** - Music video files
- **📹 Video (Home Video)** - Personal home videos and recordings
- **📸 Photo (Home Photo)** - Personal photos and images
- **📚 Book** - eBooks, comics, and other readable content
- **🎧 Audiobook** - Spoken word audio books

## Quick Start

1. **Install the Plugin**: See [Installation Guide](getting-started/installation.md)
2. **Access SmartLists**:
   - **Regular Users**: Click "SmartLists" in your home screen sidebar (requires administrator to configure allowed users in Dashboard → My Plugins → SmartLists → User Selection)
   - **Administrators**: Go to Dashboard → My Plugins → SmartLists
3. **Create Your First List**: Use the "Create List" tab
4. **Example**: Create a playlist or collection for "Unwatched Action Movies" with:
   - Media type: "Movie"
   - Genre contains "Action"
   - Playback Status = Unplayed

For more detailed instructions, see the [Quick Start Guide](getting-started/quick-start.md).

## Overview

This plugin creates smart playlists and collections that automatically update based on rules you define, such as:

- **Unplayed movies** from specific genres
- **Recently added** series or episodes
- **Next unwatched episodes** for "Continue Watching" playlists
- **High-rated** content from certain years
- **Music** from specific artists or albums
- **Tagged content** like "Christmas", "Kids", or "Documentaries"
- And much more!

The plugin features a modern web-based interface for easy list management - no technical knowledge required.