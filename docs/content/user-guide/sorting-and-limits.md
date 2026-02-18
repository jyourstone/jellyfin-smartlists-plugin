# Sorting and Limits

## Multiple Sorting Levels

You can add up to **3 sorting options** for both playlists and collections to create cascading sorts. Items are first sorted by the first option, then items with equal values are sorted by the second option, and so on.

**Example**: Sort by "Production Year" descending, then "Community Rating" descending to group movies by year with highest-rated movies first within each year.

## Sort Fields

### No Order
Items appear in library order without any specific sorting applied.

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
Sort by when the item was added to your Jellyfin library.

### Community Rating
Sort by user ratings. Higher ratings first when descending.

### Play Count (owner)
Sort by how many times the playlist/collection owner has played each item. Useful for finding least-played or most-played content.

### Last Played (owner)
Sort by when the playlist/collection owner last played each item. Great for rediscovering content or finding recently watched items.

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