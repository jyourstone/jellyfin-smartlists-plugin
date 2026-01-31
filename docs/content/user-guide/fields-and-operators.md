# Fields and Operators

This page documents all available fields and operators for creating smart list rules.

## Fields

Fields are organized into categories that match the dropdown menu in the UI. Some fields have additional options that appear when selected.

### Content

| Field | Description |
|-------|-------------|
| **Name** | Title of the media item |
| **Series Name** | Name of the parent series (episodes only) |
| **Parental Rating** | Age rating (G, PG, PG-13, R, etc.) |
| **Custom Rating** | Custom/user-defined rating string |
| **Overview** | Description/summary of the content |
| **Production Year** | Original production year |
| **Release Date** | Original release date of the media |
| **Last Episode Air Date** | Air date of the most recent episode (TV series only). Useful for finding actively airing shows. |
| **Production Locations** | Countries/regions where the content was produced (e.g., "United States", "Japan") |

#### Similar To

Find items similar to a reference item based on metadata.

**Options:**

- **Comparison fields** (default: Genre + Tags) - Select which metadata fields to use for similarity matching:
    - Genre, Tags, Actors, Actor Roles, Writers, Producers, Directors, Studios, Audio Languages, Name, Production Year, Parental Rating

The more fields selected, the more comprehensive but potentially stricter the matching.

### Video

| Field | Description |
|-------|-------------|
| **Resolution** | Video resolution (480p, 720p, 1080p, 1440p, 4K, 8K) - for Movies, Episodes, Music Videos, Home Videos |
| **Channel Resolution** | Live TV channel resolution (SD, HD, Full HD, UHD) - for Live TV channels only |
| **Framerate** | Video framerate in fps (e.g., 23.976, 29.97, 59.94) |
| **Video Codec** | Codec format (e.g., HEVC, H264, AV1, VP9) |
| **Video Profile** | Codec profile (e.g., Main 10, High) |
| **Video Range** | Dynamic range (e.g., SDR, HDR) |
| **Video Range Type** | Specific HDR format (e.g., HDR10, DOVIWithHDR10, HDR10Plus, HLG) |

!!! note "Resolution vs Channel Resolution"
    **Resolution** extracts from actual video stream data and works with Movies, Episodes, Music Videos, and Home Videos. It uses numeric height comparison.

    **Channel Resolution** reads IPTV metadata text and only works with Live TV Channels. Values are: SD, SD (PAL), HD, Full HD, UHD. It supports numeric comparison operators (greater than, less than, etc.) for filtering by quality level - for example, "Channel Resolution greater than HD" will match Full HD and UHD channels.

### Audio

| Field | Description |
|-------|-------------|
| **Subtitle Languages** | Available subtitle tracks (e.g., eng, spa, fra) |
| **Audio Bitrate (kbps)** | Audio bitrate (e.g., 128, 256, 320, 1411) |
| **Audio Sample Rate (Hz)** | Sample rate (e.g., 44100, 48000, 96000) |
| **Audio Bit Depth** | Bit depth (e.g., 16, 24) |
| **Audio Codec** | Codec format (e.g., FLAC, MP3, AAC, ALAC) |
| **Audio Profile** | Codec profile (e.g., Dolby TrueHD, Dolby Atmos) |
| **Audio Channels** | Number of channels (e.g., 2 for stereo, 6 for 5.1) |

#### Audio Languages

The audio language tracks available for the media item.

**Options:**

- **Must be the default language** (default: No) - When enabled, only matches items where the specified language is the default audio track. This excludes items that merely have dubs in that language.

### Ratings & Playback

| Field | Description |
|-------|-------------|
| **Community Rating** | User ratings (0-10) |
| **Critic Rating** | Professional critic ratings |
| **Runtime (Minutes)** | Duration of the content |

#### User-Specific Fields

The following fields track per-user data and support an optional **user selector**:

| Field | Description |
|-------|-------------|
| **Is Favorite** | Whether the item is marked as a favorite |
| **Play Count** | Number of times the item has been played |
| **Last Played** | When the item was last played |
| **Playback Status** | Played, In Progress, or Unplayed |
| **Next Unwatched** | Shows only the next unwatched episode for each series |

**How user selection works:**

- **Playlists**: By default, uses each playlist user's own data (personalized per user). You can optionally select a specific user to check their data instead.
- **Collections**: By default, uses the collection's reference user. You can optionally select a different user.

**Playback Status values:**

- **Played** - Fully watched/listened to
- **In Progress** - Partially watched (has playback position but not marked complete)
- **Unplayed** - Not started

!!! note "Series Behavior"
    For TV series:

    - **Playback Status**: Played = all episodes watched, In Progress = some watched, Unplayed = none watched
    - **Last Played**: Uses the most recent episode watch date (excludes season 0 specials)

**Next Unwatched options:**

- **Include unwatched series** (default: Yes) - When enabled, includes the first episode of series that haven't been started. When disabled, only shows next episodes from partially watched series.

### Library

| Field | Description |
|-------|-------------|
| **Library Name** | The Jellyfin library the item belongs to |
| **Date Added to Library** | When added to your Jellyfin library |
| **Last Metadata Refresh** | When Jellyfin last updated metadata from online sources |
| **Last Database Save** | When the item's data was last saved to the database |

### File Info

| Field | Description |
|-------|-------------|
| **File Name** | Name of the media file |
| **Folder Path** | File location in your library |
| **Date Modified** | Last file modification date |

### People

Filter by cast and crew members. Select "People" in the field dropdown, then choose a specific role type.

**General roles:**

| Field | Description |
|-------|-------------|
| **People (All)** | Any cast or crew member |
| **Actors** | Actors |
| **Actor Roles (Character Names)** | Character names played by actors |
| **Directors** | Directors |
| **Writers** | Writers/screenwriters |
| **Producers** | Producers |
| **Guest Stars** | Guest stars (TV episodes) |
| **Creators** | General content creators |

**Music-related roles:**

| Field | Description |
|-------|-------------|
| **Composers** | Music composers |
| **Conductors** | Orchestra/music conductors |
| **Lyricists** | Song lyricists |
| **Arrangers** | Music arrangers |
| **Sound Engineers** | Audio/sound engineers |
| **Mixers** | Audio mixers |
| **Remixers** | Remix artists |
| **Artists (Person Role)** | Track-level artists (person metadata) |
| **Album Artists (Person Role)** | Album-level artists (person metadata) |

**Books & Comics roles:**

| Field | Description |
|-------|-------------|
| **Authors** | Book authors |
| **Illustrators** | Illustrators |
| **Pencilers** | Comic book pencil artists |
| **Inkers** | Comic book inkers |
| **Colorists** | Comic book colorists |
| **Letterers** | Comic book letterers |
| **Cover Artists** | Cover artwork artists |
| **Editors** | Editors |
| **Translators** | Translators |

### Membership

| Field | Description |
|-------|-------------|
| **Genres** | Content genres |
| **Studios** | Production studios |
| **Tags** | Custom tags assigned to media items |
| **Album** | Album name (music) |
| **Artists** | Track-level artists (music) |
| **Album Artists** | Album-level primary artists (music) |

**Episode-specific options** for Tags, Studios, and Genres:

- **Include parent series [tags/studios/genres]** (default: No) - When enabled, episodes match if either the episode or its parent series has the specified value. Useful when series-level metadata is more complete.

#### Collection Name

Filter items based on Jellyfin collection membership.

**Behavior:**

- **Playlists**: Fetches items *from within* matching collections
- **Collections**: By default fetches items from within collections. Optionally can include collection objects themselves.

**Options:**

- **Include collections only** (Collections only, default: No) - Include the collection object instead of its contents. Creates "collections of collections" (meta-collections). Media type selection is ignored when enabled.
- **Include episodes within series** (Playlists with Episodes, default: No) - Include individual episodes from series in collections.
- **Collection Search Depth** (default: 0) - How deep to traverse nested collections:
    - 0 = Only items directly in the collection
    - 1 = Items in collection + one level of sub-collections
    - 2+ = Continue traversing nested collections

!!! warning "Performance"
    Higher search depths require more database queries. Start with depth 0 and increase only if needed.

!!! note "Self-Reference Prevention"
    Smart collections never include themselves in results, even if they match the rule criteria.

#### Playlist Name

Filter items based on Jellyfin playlist membership.

**Behavior:**

- **Playlists**: Fetches items *from within* matching playlists (create "super playlists")
- **Collections**: By default fetches items from playlists. Optionally can include playlist objects.

**Options:**

- **Include playlist only** (Collections only, default: No) - Include the playlist object instead of its contents. Media type selection is ignored when enabled.

!!! note "Permissions"
    Only playlists you own or that are marked as public are accessible.

!!! note "Self-Reference Prevention"
    Smart playlists never include themselves in results, even if they match the rule criteria.

---

## Operators

Different operators are available depending on the field type.

### Text Operators

| Operator | Description |
|----------|-------------|
| **equals** / **not equals** | Exact match |
| **contains** / **not contains** | Partial text match |
| **is in** / **is not in** | Match any of multiple values (semicolon-separated) |
| **matches regex** | Pattern matching using .NET regex syntax |

### Numeric Operators

| Operator | Description |
|----------|-------------|
| **equals** / **not equals** | Exact match |
| **greater than** / **less than** | Comparison |
| **greater than or equal** / **less than or equal** | Comparison |

### Date Operators

| Operator | Description |
|----------|-------------|
| **equals** / **not equals** | Exact date match |
| **after** / **before** | Absolute date comparison |
| **newer than** / **older than** | Relative date (days, weeks, months, years) |
| **weekday** | Day of week (Monday, Tuesday, etc.) |

### Boolean Operators

| Operator | Description |
|----------|-------------|
| **equals** / **not equals** | True or False |

### Using "Is In" for Multiple Values

The **is in** operator lets you match multiple values in a single rule using semicolons, instead of creating multiple OR rule groups.

**Syntax:** `value1;value2;value3`

See [Common Use Cases](../examples/common-use-cases.md/#using-is-in-for-multiple-values) for examples.

### Using Regex

The **matches regex** operator uses .NET regular expression syntax (not JavaScript-style `/pattern/flags`).

**Quick reference:**

| Pattern | Description |
|---------|-------------|
| `(?i)text` | Case-insensitive match |
| `^text` | Starts with |
| `text$` | Ends with |
| `\bword\b` | Whole word match |
| `(a\|b\|c)` | Match any of a, b, or c |

Test patterns at [Regex101.com](https://regex101.com/) using the **.NET** flavor.

---

## Rule Logic

Rules are organized into groups with two types of logic:

- **Within a group**: AND logic - all rules must match
- **Between groups**: OR logic - any group can match

**Example:** A list with two rule groups:

```
Group 1: Genre contains "Action" AND Playback Status = Unplayed
Group 2: Genre contains "Comedy" AND Playback Status = Unplayed
```

Matches: (Action AND Unplayed) OR (Comedy AND Unplayed)

!!! tip "Per-Group Limits"
    Each OR group can have its own **Max Items** limit. See [Per-Group Max Items](sorting-and-limits.md#per-group-max-items).

For more examples, see [Common Use Cases](../examples/common-use-cases.md).

---

## Media Type-Specific Notes

### Live TV Channels

Live TV channels have limited metadata available for filtering. The following fields are supported:

**Content Fields:**

- **Name** - Channel name
- **OfficialRating** - Parental rating (if available)
- **Tags** - Custom tags

**Video Fields:**

- **Channel Resolution** - Live TV-specific resolution (SD, SD (PAL), HD, Full HD, UHD)

!!! info "Channel Resolution vs Resolution"
    Live TV channels use **Channel Resolution** instead of the standard **Resolution** field. This is because Live TV resolution is stored as text metadata from IPTV sources, while standard Resolution is calculated from video stream data.

    Available Channel Resolution values:

    - **SD** - Standard Definition
    - **SD (PAL)** - PAL Standard Definition
    - **HD** - High Definition (720p)
    - **Full HD** - Full HD (1080p)
    - **UHD** - Ultra HD (4K)

**Metadata Fields:**

- **Collections** - Collection membership
- **DateCreated**, **DateLastRefreshed**, **DateLastSaved** - Timestamp fields

**User Data Fields (useful for DVR recordings):**

- **IsFavorite** - Whether the channel is favorited
- **PlayCount**, **LastPlayedDate**, **PlaybackStatus** - Playback tracking

Most other fields are not available for Live TV channels:

- **Resolution** - Standard resolution field (uses video stream data, not available for Live TV)
- **LibraryName** - Live TV is not part of a traditional library
- **Genre, Year, People, audio/video codecs** - Not populated

!!! tip "Organizing Channels"
    Use **Name** rules with contains/regex to group channels by category (e.g., `Name contains "News"` or `Name contains "Sports"`), or manually tag channels in Jellyfin and filter by **Tags**.

!!! tip "Sorting by Resolution"
    Use the **Channel Resolution** sort option (available only for Live TV) to organize channels by quality. See [Sorting and Limits](sorting-and-limits.md#channel-resolution) for details.

!!! note "Collections Only"
    Live TV channels can only be added to **Collections**, not Playlists. This is a Jellyfin limitation.
