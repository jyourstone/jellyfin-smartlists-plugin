# Sorting and Limits

## Multiple Sorting Levels

You can add up to **3 sorting options** for both playlists and collections to create cascading sorts. Items are first sorted by the first option, then items with equal values are sorted by the second option, and so on.

**Example**: Sort by "Production Year" descending, then "Community Rating" descending to group movies by year with highest-rated movies first within each year.

## Sort Fields

The **Sort By** dropdown is searchable and its options are grouped into sections: General, Dates, Ratings & Playback, TV, Music, Media Info, Rule-Based, and Round Robin.

### Default
Automatically selects the best sort order based on your rules. If your list uses an **External List** rule, items are sorted in the external list's order. If it uses a **Similar To** rule, items are sorted by similarity score. Otherwise, items are sorted alphabetically by name. This is the default sort for new lists.

### Name
Sort alphabetically by the item's title. Respects Jellyfin's **Sort Title** metadata field when set.

### Name (Ignore 'The')
Sort alphabetically by title, ignoring the leading article "The". Respects Jellyfin's **Sort Title** metadata field when set.

### Production Year
Sort by the year the content was produced or released.

### Release Date
Sort by the specific release date of the content.

### Last Episode Air Date
Sort TV series by the air date of their most recent episode. Only available when the **Series** media type is selected. Useful for finding which series are currently airing or were most recently updated with new episodes.

### Date Created
Sort by when the item was added to your Jellyfin library. When used with a secondary sort (e.g., Track Number), items added on the same day are grouped together so the secondary sort can determine order within that day. When Date Created is the only or final sort, full timestamp precision is used.

### Community Rating
Sort by user ratings. Higher ratings first when descending.

### Play Count (owner)
Sort by how many times the playlist/collection owner has played each item. Useful for finding least-played or most-played content.

### Last Played (owner)
Sort by when the playlist/collection owner last played each item. Great for rediscovering content or finding recently watched items. Like Date Created, uses day-level precision when combined with secondary sorts.

### Runtime
Sort by the duration or runtime of items in minutes.

### Series Name
Sort TV episodes by their series name. Respects Jellyfin's **Sort Title** metadata field when set.

### Season Number
Sort TV episodes by season number.

### Episode Number
Sort TV episodes by episode number.

### Album Name
Sort music and music videos by album name.

### Artist
Sort music and music videos by artist name.

### Track Number
Sort by album name, disc number, then track number. Designed specifically for music to maintain proper album order.

### Resolution
Sort by video resolution (e.g., 480p, 720p, 1080p, 4K). Available for movies, episodes, music videos, and home videos.

### Similarity
Sort by similarity score (highest first). Only available when using the "Similar To" filter field in your rules.

### Random
Randomize the order of items. Each refresh generates a new random order.

### Rule Block Order
Preserves the natural grouping from OR blocks by keeping items from each block together.

**How it works**:

- Items from OR Block 1 appear first
- Then items from OR Block 2
- Then OR Block 3, and so on

**When to use**: Perfect for creating playlists with distinct sections from different rule groups, especially when combined with [Per-Group Max Items](#per-group-max-items).

**Works with secondary sorts**: Items within each block are sorted by your secondary sort options.

!!! note "Requires Multiple OR Blocks"
    This sort option requires multiple OR blocks to be meaningful. With only one OR block, it behaves like regular sorting.

### Round Robin (Interleave)
Interleaves items across groups defined by a field you choose (e.g., Series Name, Album Name, Artist, Genre, or Studio). This creates a "TV channel" effect where episodes cycle through shows one at a time.

**How it works**:

1. Items are grouped by the **Group By** field you select (e.g., Series Name groups episodes by their show)
2. Within each group, items are sorted in natural order (episodes by season/episode number, audio by disc/track number, other items by name)
3. Groups are sorted alphabetically (or reverse for descending)
4. Items are interleaved round-robin style: first item from each group, then second item from each group, and so on
5. When a group runs out of items, it is skipped

**Example** — TV channel playlist grouped by Series Name:

| Position | Item |
|----------|------|
| 1 | Show A - S01E01 |
| 2 | Show B - S01E01 |
| 3 | Show C - S01E01 |
| 4 | Show A - S01E02 |
| 5 | Show B - S01E02 |
| 6 | Show C - S01E02 |
| 7 | Show A - S01E03 |
| 8 | Show C - S01E03 |
| 9 | Show C - S01E04 |

(Show B only had 2 episodes, so it's skipped from round 3 onward.)

**Configuration**:

1. Add rules that match the episodes you want (e.g., `Playback Status = Unplayed`)
2. Select **Round Robin (Interleave)** as your sort
3. Choose the **Group By** field (e.g., Series Name for TV episodes)
4. Optionally set Sort Order to control group ordering (Ascending = A→Z, Descending = Z→A)

!!! tip "Keep it simple"
    You do **not** need separate OR blocks per show/album. A single rule block (e.g., `Playback Status = Unplayed`) is enough — the Round Robin sort automatically groups and interleaves items by the Group By field you choose.

**Available Group By fields**:

| Field | Available when |
|-------|---------------|
| Series Name | Episode media type |
| Collection | Episode or Movie media type |
| Album Name | Audio or Music Video media type |
| Artist | Audio or Music Video media type |
| Genre (first) | All media types |
| Studio (first) | All media types |

!!! tip "Combining with limits"
    Use **Max Items** to create a fixed-length "channel" playlist.

!!! note "Multi-value fields"
    For Genre and Studio, items are grouped by their **first** value. For example, a movie tagged "Action, Comedy" would be placed in the "Action" group.

!!! note "Grouping by Collection"
    Collections are Jellyfin's regular collections — manually created ones, auto-created movie collections, and SmartLists smart collections all work. Episodes belong to a collection through their **series** (TV collections contain series). If an item is in several collections, it's grouped under the alphabetically-first one. Items that aren't in any collection fall back to grouping by Series Name (episodes) or their own name. Only direct collection members count — nested collections are not flattened.

**Order Within Group**:

All round robin variants except Shuffled Round Robin (where shuffling always wins) let you choose how items are ordered inside each group:

- **Season/Episode (default)**: Natural order — episodes by season/episode number, audio by disc/track number, other items by name.
- **Air Date**: Premiere date order (day precision). Items airing on the same day keep their episode order, and items with no date come first.

Air Date is made for franchise viewing: combine **Group By: Collection** with **Order Within Group: Air Date** and crossover episodes that span multiple shows play in airing order, while a spinoff only enters the rotation once the timeline reaches its premiere.

When grouping by Collection with Air Date order, episodes that aired close together are also pulled into the rotation **as one block**: episodes from *different* shows in the collection that aired within the **Air Window** (default 3 days, 0 = same day only) play back-to-back instead of a full rotation cycle apart. Same-night crossovers stay together, and franchise weeks (one show Tuesday, the next Wednesday, the third Thursday) come as one run. Episodes of the same show never chain, so solo-era episodes still rotate one per cycle, and a block never contains more episodes than the collection has shows. Note that **Max Items** counts episodes, not blocks — a tight limit can cut the list off in the middle of a block.

!!! warning "NextUnwatched vs Playback Status"
    If you want to interleave **all** unwatched episodes, use `Playback Status = Unplayed`. The `Next Unwatched = Yes` filter only returns 1 episode per series (the very next one to watch), which limits Round Robin to at most one item per show.

### Random Round Robin (Interleave)
Works exactly like Round Robin, but **shuffles the group order randomly** on each refresh. This gives you the interleaved "TV channel" effect while varying which show appears first each time.

**How it differs from Round Robin**:

- **Round Robin**: Groups are always in alphabetical order (A→Z or Z→A). Show A always comes first.
- **Random Round Robin**: Group order is randomized each refresh. One time Show C might come first, the next time Show B.

Within each group, items stay in natural order (episodes by season/episode number), or air-date order if you set **Order Within Group** to Air Date.

**Example** — Same 3 shows, different refreshes:

First refresh:

| Position | Item |
|----------|------|
| 1 | Show B - S01E01 |
| 2 | Show A - S01E01 |
| 3 | Show C - S01E01 |
| 4 | Show B - S01E02 |
| 5 | Show A - S01E02 |
| 6 | Show C - S01E02 |

Next refresh:

| Position | Item |
|----------|------|
| 1 | Show C - S01E01 |
| 2 | Show A - S01E01 |
| 3 | Show B - S01E01 |
| 4 | Show C - S01E02 |
| 5 | Show A - S01E02 |
| 6 | Show B - S01E02 |

**Configuration**:

1. Add rules that match the episodes you want (e.g., `Playback Status = Unplayed`)
2. Select **Random Round Robin (Interleave)** as your sort
3. Choose the **Group By** field (e.g., Series Name for TV episodes)
4. No Sort Order is needed — group order is always randomized

!!! tip "Simulating a TV channel"
    Combine with **Auto Refresh** to get a different "channel lineup" on a schedule. Each refresh reshuffles which show appears first while keeping episodes in order within each show.

### Shuffled Round Robin (Interleave)
Like Random Round Robin, groups are interleaved in random order — and additionally, the items **within each group are shuffled** too. Every refresh produces a completely new arrangement: a random show rotation with episodes in random order, like turning on a TV at a random time.

**How the round robin variants compare**:

- **Round Robin**: Groups in alphabetical order, items within each group in natural order.
- **Random Round Robin**: Group order randomized each refresh, items within each group in natural order.
- **Shuffled Round Robin**: Group order randomized each refresh AND items within each group shuffled.
- **Least Recently Watched Round Robin**: Group order follows your watch history (least recently watched first), items within each group in natural order.

**Example** — Shuffled Round Robin (Group By: Series Name) over shows A, B, C might produce:

| Position | Item |
|----------|------|
| 1 | Show A - S02E03 |
| 2 | Show C - S01E01 |
| 3 | Show B - S03E02 |
| 4 | Show A - S01E01 |
| 5 | Show C - S02E04 |
| 6 | Show B - S01E05 |

Both the show rotation and the episode order within each show are random, and refreshing again produces a different arrangement.

**Configuration**:

1. Add rules that match the episodes you want (e.g., `Playback Status = Unplayed`)
2. Select **Shuffled Round Robin (Interleave)** as your sort
3. Choose the **Group By** field (e.g., Series Name for TV episodes) — the same Group By fields as Round Robin are supported
4. No Sort Order is needed — both group order and item order are always randomized

### Least Recently Watched Round Robin (Interleave)
Rotates through your shows starting with the one you watched **least** recently — shows you have never watched come first, and the show you watched most recently goes to the back of the rotation. Watch an episode of Show A today, and on the next refresh Show A moves to the end of the rotation while the other shows shift forward. Within each group, items stay in natural order (episodes by season/episode number), or air-date order if you set **Order Within Group** to Air Date.

Unlike Random Round Robin, the rotation is not shuffled — it is derived entirely from your watch history, so it "continues where you left off" across refreshes, and refreshing without watching anything produces the same order.

**Example** — 3 shows: you have never watched Show C, watched Show A last week, and watched Show B yesterday:

| Position | Item |
|----------|------|
| 1 | Show C - S01E01 |
| 2 | Show A - S01E01 |
| 3 | Show B - S01E01 |
| 4 | Show C - S01E02 |
| 5 | Show A - S01E02 |
| 6 | Show B - S01E02 |

**Recommended setup for a fair TV rotation**:

1. Add the rule `Playback Status = Unplayed` — watched episodes drop off, and each show's next unwatched episode surfaces automatically
2. Select **Least Recently Watched Round Robin (Interleave)** as your sort
3. Choose the **Group By** field (e.g., Series Name for TV episodes)
4. No Sort Order is needed — group order always follows watch recency (never watched first, most recently watched last, alphabetical tie-break)
5. Set [Auto Refresh](auto-refresh.md) to **On All Changes** so the rotation advances right after you finish watching something

With other auto-refresh modes the rotation still advances, but only at the next refresh (scheduled or on library changes).

With **Group By: Collection**, the whole franchise carries one recency — watching any member sends the entire collection group to the back of the rotation. The exception is an unfinished air block: with **Order Within Group: Air Date**, a collection stays at the front while the air block you just watched from still has unwatched episodes **in the playlist**, so watching part 1 of a crossover night never pushes parts 2 and 3 to the bottom. Once the block is finished, the collection rotates to the back as usual — episodes hidden by the playlist's other rules never keep a collection at the front.

!!! note "Per-user rotation"
    For playlists shared with multiple users, each user gets their own rotation based on their own watch history. Collections use the [reference user's](user-selection.md#collections-reference-user) watch history, so all users see the same rotation.

## Random Group Selection

Random Group Selection chooses one random group from the items that already matched your rules, then continues with your normal sort and limit settings.

**How it works**:

1. Your rules create the eligible item pool
2. One group is selected at random
3. Items outside that group are removed
4. Sort options and limits are applied to the selected group

**Supported groups**:

- Artist
- Album Artist
- Album
- Series Name
- Genre
- Studio
- Tag

For fields with multiple values, such as Artists, Genres, Studios, and Tags, an item can belong to every value it has.

**Example: 50 random tracks from one random artist**

1. Select **Audio (Music)** as the media type
2. Enable **Random Group Selection**
3. Set **Group By** to **Artist**
4. Set **Minimum Items in Group** to `50`
5. Set **Sort By** to **Random**
6. Set **Max Items** to `50`

One refresh might choose Eminem and return 50 Eminem tracks. The next refresh might choose Katy Perry and return 50 Katy Perry tracks.

**Example: least-listened tracks from one random artist**

Use the same settings as above, but set **Sort By** to **Play Count (owner)** and **Sort Order** to **Ascending**.

**Example: most-listened tracks from one random artist**

Use the same settings as above, but set **Sort By** to **Play Count (owner)** and **Sort Order** to **Descending**.

**Other examples**:

- Random series, then episodes from that series
- Random genre, then movies from that genre
- Random studio, then movies from that studio
- Random tag, then items with that tag

!!! tip "Minimum Items"
    Set **Minimum Items in Group** when you need a full list. For example, set it to `50` if you only want artists with at least 50 matching tracks.

### External List Order
Sort items in the same order as the external list they were matched from. Only available when using the "External List" filter field in your rules.

## Sorting Collections by Child Item Values {#child-item-sorting}

When creating a **smart collection that contains other collections**, you can sort those collections by aggregated values from the items within them.

### How It Works

When you have a "collection of collections" (a smart collection containing child collections), you may want to sort by the earliest, most recent, or highest-rated item **within** each child collection, rather than by the collection's own metadata.

This feature is **automatically enabled** when your Collection name rule has a **Collection search depth** greater than 0 and you're sorting by one of the supported fields.

### Supported Sort Fields

This feature is available for the following sort fields:

| Sort Field | Ascending | Descending |
|------------|-----------|------------|
| Production Year | Minimum (earliest year) | Maximum (most recent year) |
| Community Rating | Minimum (lowest rating) | Maximum (highest rating) |
| Date Created | Minimum (oldest) | Maximum (most recently added) |
| Release Date | Minimum (earliest release) | Maximum (most recently released) |

The aggregation method depends on sort direction to ensure consistent, intuitive ordering.

### Configuration

1. Set the output type to **Collection**
2. Add a rule using the **Collection name** field
3. Set **"Include collections only"** to **"Yes"** (to include the collection objects themselves)
4. Set the **Collection search depth** field to 1 or higher (this controls how deep to look for items to aggregate)
5. Select a supported sort field (e.g., Production Year)

!!! info "See Also"
    - [Sorting Collections by Child Content](../examples/advanced-examples.md#sorting-collections-by-child-content) - Full example with configuration details
    - [Collection Search Depth](fields-and-operators.md#collection-search-depth) - Details on the depth setting

## Limits

### Max Items

Set a maximum number of items for your smart list. The limit applies **after sorting** is applied.

**Common uses**:

- Create "Top 10" or "Best of" style lists
- Limit large lists to a manageable size
- Improve performance for very large libraries

!!! warning "Performance"
    Setting this to unlimited (0) might cause performance issues for very large lists.

### Per-Group Max Items

When using multiple OR blocks, you can set a **Max Items limit for each individual OR block**. This allows precise control over your smart list composition.

**How it works**:

1. Items matching each OR block's rules are collected separately
2. Each group is sorted using the playlist's sort options
3. The MaxItems limit is applied to each group independently
4. The limited results from all groups are combined
5. The global Max Items limit (if set) is applied to the final combined result

**Configuration**:

1. Create your rule(s) within an OR block
2. At the bottom of each OR block, enter a value in **"Max Items for this OR block"**
3. Leave empty or set to 0 for unlimited items from that group

**Example**: Create a balanced playlist with 50 trailers and 50 episodes by setting MaxItems to 50 on each OR block.

!!! tip "Combining with Global Limits"
    Per-group limits are applied first, then the global Max Items limit. Example: 3 blocks × 50 items each = 150 total, then global limit of 100 = final result of 100 items.

!!! info "See Examples"
    For detailed examples using per-group limits, see [Common Use Cases](../examples/common-use-cases.md#balanced-mix-with-per-group-limits) and [Advanced Examples](../examples/advanced-examples.md#advanced-per-group-limit-techniques).

### Max Playtime

Set a maximum playtime in minutes for your smart playlist (playlists only, not collections).

**How it works**: The plugin calculates the total runtime and stops adding items when the time limit is reached. The last item that would exceed the limit is excluded.

**Common uses**:

- Workout playlists matching your exercise duration
- Pomodoro-style work sessions with music
- Playlists that fit within specific time constraints

Works with all media types (movies, TV shows, music) using the actual runtime of each item.
