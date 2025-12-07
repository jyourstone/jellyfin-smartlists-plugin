# Fields and Operators

## Available Fields

The web interface provides access to all available fields for creating list rules.

### Content Fields

- **Audio Languages** - The audio language of the movie/TV show. You can optionally restrict this to only match the default audio language (excluding dub tracks) by checking the "Must be the default language" checkbox.
- **Audio Bitrate** - Audio bitrate in kbps (e.g., 128, 256, 320, 1411)
- **Audio Sample Rate** - Audio sample rate in Hz (e.g., 44100, 48000, 96000, 192000)
- **Audio Bit Depth** - Audio bit depth in bits (e.g., 16, 24)
- **Audio Codec** - Audio codec format (e.g., FLAC, MP3, AAC, ALAC)
- **Audio Profile** - Audio codec profile (e.g., Dolby TrueHD, Dolby Atmos)
- **Audio Channels** - Number of audio channels (e.g., 2 for stereo, 6 for 5.1)
- **Resolution** - Video resolution (480p, 720p, 1080p, 1440p, 4K, 8K)
- **Framerate** - Video framerate in frames per second (e.g., 23.976, 29.97, 59.94)
- **Video Codec** - Video codec format (e.g., HEVC, H264, AV1, VP9)
- **Video Profile** - Video codec profile (e.g., Main 10, High)
- **Video Range** - Video dynamic range (e.g., SDR, HDR)
- **Video Range Type** - Specific HDR format (e.g., HDR10, DOVIWithHDR10, HDR10Plus, HLG)
- **Name** - Title of the media item
- **Series Name** - Name of the parent series (for episodes only)
- **Similar To** - Find items similar to a reference item based on metadata
- **Parental Rating** - Age rating (G, PG, PG-13, R, etc.)
- **Overview** - Description/summary of the content
- **Production Year** - Original production year
- **Release Date** - Original release date of the media

### Ratings & Playback Fields

- **Community Rating** - User ratings (0-10)
- **Critic Rating** - Professional critic ratings
- **Is Favorite** - Whether the item is marked as a favorite
- **Playback Status** - The playback status of the item with three possible values:
  - **Played** - Fully watched/listened to (marked as played in Jellyfin)
  - **In Progress** - Partially watched/listened to (has playback position but not marked as played)
  - **Unplayed** - Not started (no playback position)
  
    !!! note "Series Playback Status"
        For TV series, the playback status is calculated based on episode watch counts:
        
        - **Played**: All episodes in the series have been watched
        - **In Progress**: At least one episode has been watched, but not all
        - **Unplayed**: No episodes have been watched

- **Last Played** - When the item was last played (user-specific)
  
    !!! note "Series Last Played Date"
        For TV series, the last played date is calculated based on the most recent episode watch date:
        
        - The series' last played date is set to the **most recent** `LastPlayedDate` among all episodes in the series
        - This ensures that series appear correctly in filters like "Last Played newer than X days/hours"
        - Only episodes from season 1 and above are considered (season 0 specials are excluded)
        
        **Example:** If you watched Episode 1 on January 1st and Episode 2 on January 5th, the series' last played date will be January 5th (the most recent episode).

- **Next Unwatched** - Shows only the next unwatched episode in chronological order for TV series
- **Play Count** - Number of times the item has been played
- **Runtime (Minutes)** - Duration of the content in minutes

### File Info Fields

- **Date Modified** - Last file modification date
- **File Name** - Name of the media file
- **Folder Path** - Location in your library

### Library Fields

- **Date Added to Library** - When added to your Jellyfin library
- **Last Metadata Refresh** - When Jellyfin last updated metadata from online sources
- **Last Database Save** - When the item's data was last saved to Jellyfin's database

### People Fields (Movies & TV Shows)

- **People (All)** - All cast and crew
- **Actors** - Actors in the movie or TV show
- **Directors** - Directors of the movie or TV show
- **Writers** - Writers/screenwriters
- **Producers** - Producers
- **Guest Stars** - Guest stars in TV show episodes

### Collection Fields

- **Collections** - All Jellyfin collections that contain the media item
- **Genres** - Content genres
- **Studios** - Production studios
- **Tags** - Custom tags assigned to media items
- **Album** - Album name (for music)
- **Artists** - Track-level artists (for music)
- **Album Artists** - Album-level primary artists (for music)

## Optional Field Options

Some fields have additional optional settings that appear when you select them. These options allow you to fine-tune how the field is evaluated:

### User-Specific Fields

The following fields support an optional **user selector** that allows you to check playback data for a specific user:

- **Playback Status**
- **Is Favorite**
- **Play Count**
- **Next Unwatched**
- **Last Played**

#### How User Selection Works

**For Playlists:**
- **Default behavior**: When you select users for a playlist (in the "Users" field), user-specific fields without an explicit user selection will automatically use the playlist user being processed. This means if you create a playlist with users "Alice" and "Bob", each user gets their own personalized playlist based on their own data.
- **Explicit user selection**: You can optionally select a specific user from the dropdown in the rule itself to check that user's data, even if they're not one of the playlist users. This is useful for creating rules like "Is Favorite for Alice" in a playlist that belongs to Bob.

**For Collections:**
- **Default behavior**: User-specific fields use the collection's reference user (the user you selected when creating the collection).
- **Explicit user selection**: You can optionally select a different user from the dropdown to check their playback status instead.

**Examples:**
- **Multi-user playlist**: Create a playlist with users "Alice" and "Bob", add rule "Is Favorite = True" (no user selected). Result: Alice sees her favorites, Bob sees his favorites.
- **Cross-user rule**: Create a playlist for "Bob", add rule "Is Favorite = True" with user "Alice" selected. Result: Bob's playlist shows Alice's favorites.
- **Collection**: Create a collection with reference user "Alice", add rule "Playback Status = Unplayed" (no user selected). Result: Shows unwatched items from Alice's perspective.

### Next Unwatched Options

When using the **Next Unwatched** field, you can configure:

- **Include unwatched series** (default: Yes) - When enabled, includes the first episode of series that haven't been started yet. When disabled, only shows the next episode from series that have been partially watched.

### Collections Options

The **Collections** field allows you to filter items based on which Jellyfin collections they belong to. The behavior differs depending on whether you're creating a Playlist or a Collection:

**For Playlists:**
- Items *from within* the specified collections are always fetched and added to the playlist
- Playlists cannot contain collection objects themselves (Jellyfin limitation)
- Example: A playlist with "Collections contains Marvel" will include all movies/episodes from your Marvel collection

**For Collections:**
- By default, items *from within* the specified collections are fetched (same as playlists)
- Optionally, you can include the collection objects themselves instead (see options below)
- Example: A collection with "Collections contains Marvel" can either contain the movies from Marvel collection, or the Marvel collection object itself

**Available Options:**

- **Include collection only** (Collections only, default: No) - When enabled, the collection object itself is included instead of its contents. This allows you to create "collections of collections" (meta-collections). **Important:** When this option is enabled, your selected media types are ignored for this rule, since you're fetching collection objects rather than media items.
- **Include episodes within series** (Playlists with Episode media type, default: No) - When enabled, individual episodes from series in collections are included. When disabled, only the series themselves are included in the collection match. This option is hidden when "Include collection only" is enabled.

!!! important "Self-Reference Prevention"
    A smart collection will **never include itself** in its results, even if it matches the rule criteria. This prevents circular references and infinite loops.
    
    **Example:** If you create a smart collection called "Marvel Collection" (or "My Marvel Collection - Smart" with a prefix/suffix) and use the rule "Collections contains Marvel", the system will:
    - ✅ Include other collections that match "Marvel" (e.g., a regular Jellyfin collection named "Marvel")
    - ❌ **Exclude itself** from the results, even though it technically matches the pattern
    
    The system compares the base names (after removing any configured prefix/suffix) to detect and prevent self-reference. This means you can safely create smart collections with names that match your collection rules without worrying about them including themselves.

### Episode-Specific Collection Field Options

When using **Tags**, **Studios**, or **Genres** fields with episodes selected as a media type, you can configure whether to also check the parent series:

- **Include parent series tags** (Tags field only, default: No) - When enabled, episodes will match if either the episode or its parent series has the specified tag.
- **Include parent series studios** (Studios field only, default: No) - When enabled, episodes will match if either the episode or its parent series has the specified studio.
- **Include parent series genres** (Genres field only, default: No) - When enabled, episodes will match if either the episode or its parent series has the specified genre.

These options are useful when series-level metadata is more complete than episode-level metadata, or when you want to match episodes based on series characteristics.

### Audio Languages Options

When using the **Audio Languages** field with any audio-capable media type (Movie, Episode, Audio, AudioBook, MusicVideo, Video), you can configure whether to match only the default audio language:

- **Must be the default language** (default: No) - When enabled, the filter will only match items where the specified language is marked as the default audio track (IsDefault=true). This excludes items that merely have additional audio tracks (dubs) in that language. When disabled, the filter matches any item with an audio track in the specified language, regardless of whether it's the default track.

**Example Use Cases:**
- **French films only**: Use "Audio Languages contains fra" with "Must be the default language" enabled to find films originally produced in French, excluding English films with French dubs.
- **Any French audio**: Use "Audio Languages contains fra" with "Must be the default language" disabled to find any content with French audio tracks, including dubbed content.

### Similar To Options

When using the **Similar To** field, you can configure which metadata fields to use for similarity comparison:

**Default fields**: Genre + Tags

You can optionally select additional fields to include in the similarity calculation:
- Genre
- Tags
- Actors
- Writers
- Producers
- Directors
- Studios
- Audio Languages
- Name
- Production Year
- Parental Rating

The more fields you select, the more comprehensive the similarity matching becomes. However, using too many fields may make matches less likely.

### People Field Options

When using the **People** field, you can select a specific person type to filter by:

- **People (All)** - Matches any cast or crew member (default)
- **Actors** - Only actors
- **Directors** - Only directors
- **Writers** - Only writers/screenwriters
- **Producers** - Only producers
- **Guest Stars** - Only guest stars (TV episodes)
- **Composers** - Only composers
- **Conductors** - Only conductors
- **Lyricists** - Only lyricists
- And many more specialized roles...

This allows you to create more specific rules, such as "Movies directed by Christopher Nolan" instead of "Movies with Christopher Nolan in any role."

## Available Operators

- **equals** / **not equals** - Exact matches
- **contains** / **not contains** - Partial text matching
- **is in** / **is not in** - Check if value contains any item (partial matching)
  - **Tip**: Use this instead of creating multiple OR rule groups! For example, instead of creating separate rule groups for "Action", "Comedy", and "Drama", you can use a single rule: `Genre is in "Action;Comedy;Drama"`
- **greater than** / **less than** - Numeric comparisons
- **greater than or equal** / **less than or equal** - Numeric comparisons
- **after** / **before** - Date comparisons
- **newer than** / **older than** - Relative date comparisons (days, weeks, months, years)
- **weekday** - Day of week matching (Monday, Tuesday, etc.)
- **matches regex** - Advanced pattern matching using .NET regex syntax

### Using "Is In" to Simplify Lists

The **"is in"** and **"is not in"** operators are powerful tools that can help you simplify your lists. Instead of creating multiple OR rule groups, you can combine multiple values in a single rule using semicolons.

**Example: Instead of this (multiple OR rule groups):**
```
Rule Group 1:
  - Genre contains "Action"
  - Playback Status = Unplayed

Rule Group 2:
  - Genre contains "Comedy"
  - Playback Status = Unplayed

Rule Group 3:
  - Genre contains "Drama"
  - Playback Status = Unplayed
```

**You can use this (single rule with "is in"):**
```
Rule Group 1:
  - Genre is in "Action;Comedy;Drama"
  - Playback Status = Unplayed
```

Both approaches produce the same result, but the second is much simpler and easier to maintain! The "is in" operator checks if the field value contains any of the semicolon-separated items.

**Syntax**: Separate multiple values with semicolons: `value1; value2; value3`

### Using the Weekday Operator

The **weekday** operator allows you to filter items based on the day of week for any date field. This is particularly useful for:

- Filtering TV shows that originally aired on specific weekdays (e.g., "Release Date weekday Monday")
- Finding items created or modified on specific days of the week
- Combining with other date operators for more complex filters

**Example Use Cases**:
- "Release Date weekday Friday" - Shows that premiered on Fridays
- "Release Date weekday Monday AND Release Date newer than 6 months" - Recent Monday releases
- "DateCreated weekday Sunday" - Items added to your library on Sundays

**Important Notes**:
- Weekday matching uses UTC timezone, consistent with all other date operations in the plugin
- You can combine weekday with other date operators (After, Before, NewerThan, OlderThan) using AND logic

## Rule Logic

Understanding how rule groups work is key to creating effective lists. The plugin uses two types of logic:

### Within a Rule Group (AND Logic)

**All rules within the same group must be true** for an item to match. This means you're looking for items that meet ALL the conditions in that group.

**Example:**
```
Rule Group 1:
  - Genre contains "Action"
  - Playback Status = Unplayed
  - Production Year > 2010
```

This matches items that are:
- **Action** movies **AND**
- **Unwatched** **AND**
- **Released after 2010**

All three conditions must be true!

### Between Rule Groups (OR Logic)

**Different rule groups are separated with OR logic**. An item matches if it satisfies ANY of the rule groups.

**Example:**
```
Rule Group 1:
  - Genre contains "Action"
  - Playback Status = Unplayed

Rule Group 2:
  - Genre contains "Comedy"
  - Playback Status = Unplayed
```

This matches items that are:
- **(Action AND Unwatched)** **OR**
- **(Comedy AND Unwatched)**

An item matches if it's either an unwatched action movie OR an unwatched comedy.

### Complex Example

Here's a more complex example to illustrate both concepts:

```
Rule Group 1:
  - Genre contains "Action"
  - Production Year > 2010
  - Community Rating > 7

Rule Group 2:
  - Genre contains "Sci-Fi"
  - Is Favorite = True
```

This list will include items that are:
- **(Action AND After 2010 AND Rating > 7)** **OR**
- **(Sci-Fi AND Favorite)**

So you'll get highly-rated recent action movies, plus any sci-fi movies you've marked as favorites, regardless of when they were made or their rating.

### Using Regex for Advanced Pattern Matching

The **matches regex** operator allows you to create complex pattern matching rules using .NET regular expression syntax.

!!! important "Important: .NET Syntax Required"
    SmartLists uses **.NET regex syntax**, not JavaScript-style regex. Do not use JavaScript-style patterns like `/pattern/flags`.

**Common Examples:**

- **Case-insensitive matching**: `(?i)swe` - Matches "swe", "Swe", "SWE", etc.
- **Multiple options**: `(?i)(eng|en)` - Matches "eng", "EN", "en", etc. (case-insensitive)
- **Starts with**: `^Action` - Matches items that start with "Action" (e.g., "Action Movie", "Action Hero")
- **Ends with**: `Movie$` - Matches items that end with "Movie" (e.g., "Action Movie", "Comedy Movie")
- **Contains word**: `\bAction\b` - Matches the word "Action" as a whole word (not "ActionMovie" or "InAction")

**Testing Your Patterns:**

You can test your regex patterns using [Regex101.com](https://regex101.com/) - make sure to select the **.NET** flavor when testing.

!!! tip "Regex Tips"
    - Use `(?i)` at the start of your pattern for case-insensitive matching
    - Use `^` to match the start of a string
    - Use `$` to match the end of a string
    - Use `|` for "OR" logic (e.g., `(eng|en|english)`)
    - Use `\b` to match word boundaries

## Managing Rules

The web interface provides several tools to help you manage and organize your rules efficiently:

### Cloning Rules

You can quickly duplicate individual rules or entire OR rule groups to save time when creating similar rules.

#### Cloning Individual Rules

To clone a single rule:

1. Click the **clone button** (copy icon) on the rule you want to duplicate
2. A new rule will be created last, in the same rule group
3. The cloned rule will have all the same settings as the original

#### Cloning OR Rule Groups

To clone an entire OR rule group (all rules within a group):

1. Click the **clone button** (copy icon) in the rule group header
2. A complete copy of the entire rule group will be created last
3. The cloned rule group will have all the same individual rule fields as the original