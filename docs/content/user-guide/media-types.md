# Media Types

When creating a smart list, you must select at least one **Media Type** to specify what kind of content should be included. Media types in SmartLists correspond directly to the **Content type** options you see when adding a new Media Library in Jellyfin.

## Available Media Types

| Media Type | Jellyfin Library | Description |
|------------|------------------|-------------|
| **Movies** | Movies | Feature films and movie content |
| **Episodes** | Shows | Individual TV show episodes |
| **Series** | Shows | Entire TV series (collections only, not individual episodes) |
| **Audio** | Music | Music tracks and songs |
| **Music Videos** | Music Videos | Music video content |
| **Video** | Home Videos and Photos | Personal video content |
| **Photo** | Home Videos and Photos | Photo content |
| **Books** | Books | E-book content |
| **AudioBooks** | Books | Audiobook content |
| **LiveTvChannel** | Live TV | Live TV channels from IPTV/tuner sources (collections only) |

## Important Notes

!!! warning "Library Content Type Matters"
    The media type you select must match the content type of your Jellyfin libraries. For example:
    
    - If you select **Movies**, the list will only include items from libraries configured with the "Movies" content type
    - If you select **Episodes**, the list will only include items from libraries configured with the "Shows" content type
    - If you select **Audio**, the list will only include items from libraries configured with the "Music" content type

!!! tip "Multiple Media Types"
    You can select multiple media types for a single list. For example, you could create a list that includes both **Movies** and **Episodes** to create a mixed content list.

!!! info "Collections-Only Media Types"
    Some media types can only be used with **Collections**, not Playlists:

    - **Series**
    - **LiveTvChannel**

    If you need to group these media types, create a Smart Collection instead of a Smart Playlist.

## Selecting Media Types

In the SmartLists configuration interface, media types are presented as a multi-select dropdown:

1. Click on the **Media Types** field
2. Check the boxes for the media types you want to include
3. At least one media type must be selected
4. The selected types will be displayed in the field

The available fields and operators for filtering will vary depending on which media types you select. See the [Fields and Operators](fields-and-operators.md) guide for details on what filtering options are available for each media type.
