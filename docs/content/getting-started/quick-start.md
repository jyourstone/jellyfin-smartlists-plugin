# Quick Start

## Accessing SmartLists

SmartLists can be accessed in two ways depending on your user permissions:

**For Regular Users:**
- Navigate to your Jellyfin home screen
- Click **"SmartLists"** in the main sidebar
- This gives you access to create and manage your own playlists and collections

!!! warning "Required Plugins for User Page"
    The user-facing SmartLists page requires two additional plugins to be installed:
    
    - **Plugin Pages**: [https://github.com/IAmParadox27/jellyfin-plugin-pages](https://github.com/IAmParadox27/jellyfin-plugin-pages)
    - **File Transformation**: [https://github.com/IAmParadox27/jellyfin-plugin-file-transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
    
    See the [Installation Guide](installation.md#required-plugins-for-user-page) for details.

**For Administrators:**
- Go to **Dashboard → My Plugins → SmartLists**
- Or click **"SmartLists"** in the sidebar under "Plugins"
- This gives you full access to all lists and global settings
- **No additional plugins required** for admin access

!!! tip "Collection Permissions"
    Regular users need the **"Manage Collections"** permission in Jellyfin to create and manage collections. Administrators can grant this in: Dashboard → Users → [User] → Profile.

## Creating Your First List

1. **Access SmartLists**: Use one of the methods above
2. **Navigate to Create List Tab**: Click on the "Create List" tab
3. **Configure Your List**:
   - Enter a name for your list
   - Choose whether to create a Playlist or Collection
   - Select the media type(s) you want to include
   - Add rules to filter your content
   - Choose sorting options
   - Set the list owner (for playlists) or reference user (for collections)
   - Configure other settings as needed

!!! tip "Playlists vs Collections"
    For a detailed explanation of the differences between Playlists and Collections, see the [Configuration Guide](../user-guide/configuration.md#playlists-vs-collections).

## Example: Unwatched Action Movies

Here's a simple example to get you started:

**List Name**: "Unwatched Action Movies"

**List Type**: Playlist

**Media Type**: Movie

**Rules**:
- Genre contains "Action"
- Playback Status = Unplayed

**Sort Order**: Production Year (Descending)

**Max Items**: 100

This will create a playlist of up to 100 unwatched action movies, sorted by production year with the newest first.

## Next Steps

- Learn about [Configuration](../user-guide/configuration.md) options
- Explore [Fields and Operators](../user-guide/fields-and-operators.md) for more complex rules
- Check out [Common Use Cases](../examples/common-use-cases.md) for inspiration

