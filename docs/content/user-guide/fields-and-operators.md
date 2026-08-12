# Fields and Operators

This page documents all available fields and operators for creating smart list rules.

## Fields

Fields are organized into categories that match the dropdown menu in the UI. Some fields have additional options that appear when selected.

The **JSON name** column shows each field's internal identifier — the value used for `MemberName` when [editing list files directly](advanced-configuration.md#creating-lists-from-files).

### Content

| Field | JSON name | Description |
|-------|-----------|-------------|
| **Name** | `Name` | Title of the media item |
| **Series Name** | `SeriesName` | Name of the parent series (episodes only) |
| **Parental Rating** | `OfficialRating` | Age rating (G, PG, PG-13, R, etc.) |
| **Custom Rating** | `CustomRating` | Custom/user-defined rating string |
| **Overview** | `Overview` | Description/summary of the content |
| **Production Year** | `ProductionYear` | Original production year |
| **Release Date** | `ReleaseDate` | Original release date of the media |
| **Last Episode Air Date** | `LastEpisodeAirDate` | Air date of the most recent episode (TV series only). Useful for finding actively airing shows. |
| **Series Status** | `SeriesStatus` | Current status of a TV series — select from a dropdown: Continuing, Ended, or Unreleased. Only shown when the **Series** media type is selected and the list type is **Collection**. |
| **Production Locations** | `ProductionLocations` | Countries/regions where the content was produced (e.g., "United States", "Japan") |
| **Extra Type** | `ExtraType` | Type of extra — select from a dropdown (Behind the Scenes, Deleted Scene, Featurette, Trailer, etc.). Requires **Include Extras** enabled. |
| **IMDb ID** | `ImdbId` | IMDb identifier (e.g., `tt15574124`). Use **is in** with semicolons to match multiple IDs. |
| **TMDb ID** | `TmdbId` | TheMovieDb identifier (e.g., `875828`). Use **is in** with semicolons to match multiple IDs. |
| **TVDb ID** | `TvdbId` | TheTVDB identifier. Use **is in** with semicolons to match multiple IDs. |

#### Release Date / Last Episode Air Date

These dates come from metadata providers and can be missing — for example, an episode downloaded before its metadata is published. By default, items with an unknown date never match a date rule.

**Options:**

- **When the date is unknown** (default: Exclude) - Choose **Include items with an unknown date** to also match items whose date is missing. Useful for "new releases" lists so freshly added episodes appear even before their release date metadata arrives; once the metadata is filled in, the next refresh re-evaluates the rule normally.

#### Similar To

JSON name: `SimilarTo`

Find items similar to a reference item based on metadata.

**Options:**

- **Comparison fields** (default: Genre + Tags) - Select which metadata fields to use for similarity matching:
    - Genre, Tags, Actors, Actor Roles, Writers, Producers, Directors, Studios, Audio Languages, Name, Production Year, Parental Rating

The more fields selected, the more comprehensive but potentially stricter the matching.

### Video

| Field | JSON name | Description |
|-------|-----------|-------------|
| **Resolution** | `Resolution` | Video resolution (480p, 720p, 1080p, 1440p, 4K, 8K) |
| **Framerate** | `Framerate` | Video framerate in fps (e.g., 23.976, 29.97, 59.94) |
| **Video Codec** | `VideoCodec` | Codec format (e.g., HEVC, H264, AV1, VP9) |
| **Video Profile** | `VideoProfile` | Codec profile (e.g., Main 10, High) |
| **Video Range** | `VideoRange` | Dynamic range (e.g., SDR, HDR) |
| **Video Range Type** | `VideoRangeType` | Specific HDR format (e.g., HDR10, DOVIWithHDR10, HDR10Plus, HLG) |

### Audio

| Field | JSON name | Description |
|-------|-----------|-------------|
| **Subtitle Languages** | `SubtitleLanguages` | Available subtitle tracks (e.g., eng, spa, fra) |
| **Audio Bitrate (kbps)** | `AudioBitrate` | Audio bitrate (e.g., 128, 256, 320, 1411) |
| **Audio Sample Rate (Hz)** | `AudioSampleRate` | Sample rate (e.g., 44100, 48000, 96000) |
| **Audio Bit Depth** | `AudioBitDepth` | Bit depth (e.g., 16, 24) |
| **Audio Codec** | `AudioCodec` | Codec format (e.g., FLAC, MP3, AAC, ALAC) |
| **Audio Profile** | `AudioProfile` | Codec profile (e.g., Dolby TrueHD, Dolby Atmos) |
| **Audio Channels** | `AudioChannels` | Number of channels (e.g., 2 for stereo, 6 for 5.1) |

#### Audio Languages

JSON name: `AudioLanguages`

The audio language tracks available for the media item.

**Options:**

- **Must be the default language** (default: No) - When enabled, only matches items where the specified language is the default audio track. This excludes items that merely have dubs in that language.

### Ratings & Playback

| Field | JSON name | Description |
|-------|-----------|-------------|
| **Community Rating** | `CommunityRating` | User ratings (0-10) |
| **Critic Rating** | `CriticRating` | Professional critic ratings |
| **Runtime** | `RuntimeMinutes` | Duration of the content. Runtime rules can use minutes or seconds. |

#### User-Specific Fields

The following fields track per-user data and support an optional **user selector**:

| Field | JSON name | Description |
|-------|-----------|-------------|
| **Is Favorite** | `IsFavorite` | Whether the item is marked as a favorite |
| **Play Count** | `PlayCount` | Number of times the item has been played |
| **Last Played** | `LastPlayedDate` | When the item was last played |
| **Playback Status** | `PlaybackStatus` | Played, In Progress, or Unplayed |
| **Next Unwatched** | `NextUnwatched` | Shows only the next unwatched episode for each series |

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

| Field | JSON name | Description |
|-------|-----------|-------------|
| **Library Name** | `LibraryName` | The Jellyfin library the item belongs to |
| **Date Added to Library** | `DateCreated` | When added to your Jellyfin library |
| **Last Metadata Refresh** | `DateLastRefreshed` | When Jellyfin last updated metadata from online sources |
| **Last Database Save** | `DateLastSaved` | When the item's data was last saved to the database |

### File Info

| Field | JSON name | Description |
|-------|-----------|-------------|
| **File Name** | `FileName` | Name of the media file |
| **Folder Path** | `FolderPath` | File location in your library |
| **Date Modified** | `DateModified` | Last file modification date |

### People

Filter by cast and crew members. Select "People" in the field dropdown, then choose a specific role type.

**General roles:**

| Field | JSON name | Description |
|-------|-----------|-------------|
| **People (All)** | `People` | Any cast or crew member |
| **Actors** | `Actors` | Actors |
| **Actor Roles (Character Names)** | `ActorRoles` | Character names played by actors |
| **Directors** | `Directors` | Directors |
| **Writers** | `Writers` | Writers/screenwriters |
| **Producers** | `Producers` | Producers |
| **Guest Stars** | `GuestStars` | Guest stars (TV episodes) |
| **Creators** | `Creators` | General content creators |

**Music-related roles:**

| Field | JSON name | Description |
|-------|-----------|-------------|
| **Composers** | `Composers` | Music composers |
| **Conductors** | `Conductors` | Orchestra/music conductors |
| **Lyricists** | `Lyricists` | Song lyricists |
| **Arrangers** | `Arrangers` | Music arrangers |
| **Sound Engineers** | `SoundEngineers` | Audio/sound engineers |
| **Mixers** | `Mixers` | Audio mixers |
| **Remixers** | `Remixers` | Remix artists |
| **Artists (Person Role)** | `PersonArtists` | Track-level artists (person metadata) |
| **Album Artists (Person Role)** | `PersonAlbumArtists` | Album-level artists (person metadata) |

**Books & Comics roles:**

| Field | JSON name | Description |
|-------|-----------|-------------|
| **Authors** | `Authors` | Book authors |
| **Illustrators** | `Illustrators` | Illustrators |
| **Pencilers** | `Pencilers` | Comic book pencil artists |
| **Inkers** | `Inkers` | Comic book inkers |
| **Colorists** | `Colorists` | Comic book colorists |
| **Letterers** | `Letterers` | Comic book letterers |
| **Cover Artists** | `CoverArtists` | Cover artwork artists |
| **Editors** | `Editors` | Editors |
| **Translators** | `Translators` | Translators |

### Membership

| Field | JSON name | Description |
|-------|-----------|-------------|
| **Genres** | `Genres` | Content genres |
| **Studios** | `Studios` | Production studios |
| **Tags** | `Tags` | Custom tags assigned to media items |
| **Album** | `Album` | Album name (music) |
| **Artists** | `Artists` | Track-level artists (music) |
| **Album Artists** | `AlbumArtists` | Album-level primary artists (music) |
| **External List** | `ExternalList` | Match items from an external list (MDBList, IMDb, Letterboxd, Trakt, TMDB, ListenBrainz). [See details below.](#external-list) |

**Parent metadata options** for Tags, Studios, and Genres (shown when Episode or Audio media type is selected):

Each of these fields has three options:

- **No - Only check item [tags/studios/genres]** (default) - Only checks the item's own metadata.
- **Yes - Also check [tags/studios/genres] from parent series/album** - Matches if either the item or its parent series/album has the specified value. Useful when parent-level metadata is more complete.
- **Yes - Only check [tags/studios/genres] from parent series/album** - Skips the item's own metadata entirely and only checks the parent series/album. Useful when you want to filter purely by series/album-level metadata.

The label and option text adapts based on the selected media type (e.g., "parent series" for episodes, "parent album" for audio tracks).

#### Collection Name

JSON name: `Collections`

Filter items based on Jellyfin collection membership.

**Behavior:**

- **Playlists**: Fetches items *from within* matching collections
- **Collections**: By default fetches items from within collections. Optionally can include collection objects themselves.

**Options:**

- **Include collections only** (Collections only, default: No) - Include the collection object instead of its contents. Creates "collections of collections" (meta-collections). Media type selection is ignored when enabled.
- **Include episodes within series** (Playlists with Episodes, default: No) - Include individual episodes from series in collections.

##### Collection Search Depth {#collection-search-depth}

How deep to traverse nested collections (default: 0):

- 0 = Only items directly in the collection
- 1 = Items in collection + one level of sub-collections
- 2+ = Continue traversing nested collections

!!! warning "Performance"
    Higher search depths require more database queries. Start with depth 0 and increase only if needed.

!!! note "Self-Reference Prevention"
    Smart collections never include themselves in results, even if they match the rule criteria.

#### Playlist Name

JSON name: `Playlists`

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

#### External List

Filter items based on membership in an external list. Supports [MDBList](https://mdblist.com), [IMDb](https://www.imdb.com), [Letterboxd](https://letterboxd.com), [Trakt](https://trakt.tv), [TMDB](https://www.themoviedb.org), and [ListenBrainz](https://listenbrainz.org) — including user lists, watchlists, charts/trending, and music playlists.

| Provider | API key required | Matches by |
|----------|-----------------|------------|
| **MDBList** | Yes | IMDb, TMDB, TVDB |
| **IMDb** | No | IMDb |
| **Letterboxd** | No | TMDB |
| **ListenBrainz** | No (optional user token) | MusicBrainz recording ID (title + artist fallback) |
| **Trakt** | Yes (client ID) | IMDb, TMDB, TVDB |
| **TMDB** | Yes | TMDB |

Use `equals` to include items from a list, or `not equals` to exclude them.

For setup instructions, supported URL formats, and examples, see the [External Lists](external-lists.md) page.

---

## Operators

Different operators are available depending on the field type.

The **JSON name** column shows the value used for `Operator` when [editing list files directly](advanced-configuration.md#creating-lists-from-files).

### Text Operators

| Operator | JSON name | Description |
|----------|-----------|-------------|
| **equals** / **not equals** | `Equal` / `NotEqual` | Exact match |
| **contains** / **not contains** | `Contains` / `NotContains` | Partial text match |
| **is in** / **is not in** | `IsIn` / `IsNotIn` | Match any of multiple values (semicolon-separated) |
| **matches regex** | `MatchRegex` | Pattern matching using .NET regex syntax |

### List Operators

For list fields (Genres, Studios, Tags, Actors, Directors, Collections, Playlists, etc.), operators work against the individual entries in the list:

| Operator | JSON name | Description |
|----------|-----------|-------------|
| **equals** | `Equal` | The list contains **only** this value and nothing else |
| **not equals** | `NotEqual` | The list does **not** contain only this value |
| **contains** / **not contains** | `Contains` / `NotContains` | Any entry in the list contains the text (partial match) |
| **is in** / **is not in** | `IsIn` / `IsNotIn` | Any entry in the list matches one of the semicolon-separated values |
| **matches regex** | `MatchRegex` | Any entry in the list matches the regex pattern |

!!! tip "Equals vs Contains on list fields"
    - **Studios equals "Marvel Studios"** — matches items where Marvel Studios is the *only* studio
    - **Studios contains "Marvel Studios"** — matches items that *include* Marvel Studios (even if other studios are also listed)

### Numeric Operators

| Operator | JSON name | Description |
|----------|-----------|-------------|
| **equals** / **not equals** | `Equal` / `NotEqual` | Exact match |
| **greater than** / **less than** | `GreaterThan` / `LessThan` | Comparison |
| **greater than or equal** / **less than or equal** | `GreaterThanOrEqual` / `LessThanOrEqual` | Comparison |

### Date Operators

| Operator | JSON name | Description |
|----------|-----------|-------------|
| **equals** / **not equals** | `Equal` / `NotEqual` | Exact date match |
| **after** / **before** | `After` / `Before` | Absolute date comparison |
| **newer than** / **older than** | `NewerThan` / `OlderThan` | Relative date (days, weeks, months, years) |
| **weekday** | `Weekday` | Day of week (Monday, Tuesday, etc.) |

### Boolean Operators

| Operator | JSON name | Description |
|----------|-----------|-------------|
| **equals** / **not equals** | `Equal` / `NotEqual` | True or False |

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
