# Common Use Cases

Here are some popular playlist and collection types you can create:

## TV Shows & Movies

### Continue Watching
- **Next Unwatched** = True
- Shows next episodes to watch for each series

### Family Continue Watching (Multi-User)
- **Next Unwatched** = True (no user selected - uses each playlist user's data)
- **Users**: Select multiple family members
- Each family member gets their own personalized "Continue Watching" playlist showing their next unwatched episodes
- Auto-refreshes when any family member watches an episode

### Family Movie Night
- **Next Unwatched** = True AND **Parental Rating** = "PG" or "G"

### Unwatched Action Movies
- **Playback Status** = Unplayed AND **Genre** contains "Action"

### Continue Watching (In Progress)
- **Playback Status** = In Progress
- Shows all movies and episodes that have been started but not finished
- Perfect for picking up where you left off

### Recent Additions
- **Date Created** newer than "2 weeks"

### Holiday Classics
- **Tags** contain "Christmas" AND **Production Year** before "2000"

### Complete Franchise Collection
- **Collection name** contains "Movie Franchise" (includes all movies in the franchise)
- **Note**: For Playlists, this fetches all media items from within the collection. For Collections, you can optionally enable "Include collections only" to create a meta-collection that contains the collection object itself

### Meta-Collection (Collection of Collections)
- **Collection name** is in "Marvel;DC;Star Wars" with "Include collections only" enabled
- **List Type**: Collection
- **Note**: When "Include collections only" is enabled, your selected media types are ignored, and the collection will contain the actual collection objects rather than the media items within them
- Creates a single collection that organizes multiple collections together (e.g., a "Superhero Universes" collection containing your Marvel, DC, and other superhero collections)
- **Important**: The smart collection will never include itself in the results, even if its name matches the rule. So you can safely name your meta-collection "Superhero Universes" and use rules that match "Marvel" without worrying about it including itself

### Combine Multiple Playlists
- **Playlist name** is in "Favorites;Top Rated;Recent Additions"
- **List Type**: Playlist
- **Note**: For playlists, this fetches all media items from within the specified playlists and combines them into a single "super playlist"
- Creates a playlist that merges content from multiple existing playlists
- Perfect for creating aggregated playlists like "Best of All Time" that combines your various curated playlists
- **Important**: Only playlists you own or that are marked as public are accessible. The smart playlist will never include itself in the results.

### Playlist Organization Collection
- **Playlist name** contains "workout" with "Include playlist only" enabled
- **List Type**: Collection
- **Note**: When "Include playlist only" is enabled, the collection contains the actual playlist objects (not the media items within them)
- Creates a collection that organizes your playlists by category (e.g., a "Workout Playlists" collection containing all your workout-related playlists)
- Useful for managing large numbers of playlists by grouping them into categories
- **Important**: The smart collection will never include itself, and only playlists you own or that are public are accessible

### Unplayed Sitcom Episodes
- **Tags** contains "Sitcom" (with parent series tags enabled) AND **Playback Status** = Unplayed

## Balanced Mix with Per-Group Limits

These examples use the **Max Items for this OR block** feature to create perfectly balanced playlists:

### Movie Trailer & Episode Mix
Create a playlist with exactly 50% trailers and 50% episodes:

- **Include Extras**: Enabled
- **Media Types**: Video, Episode
- **OR Block 1**:
    - **Extra Type** = Trailer
    - **Max Items for this OR block**: 50
- **OR Block 2**:
    - **Media Type** = Episode
    - **Max Items for this OR block**: 50
- **Sort by**: Random
- Result: 50 random trailers mixed with 50 random episodes (100 items total)

### Multi-Genre Top Hits
Get the top-rated content from different genres equally:

- **OR Block 1**: 
    - **Genre** is in "Action;Thriller"
    - **Max Items for this OR block**: 20
- **OR Block 2**: 
    - **Genre** is in "Comedy;Romance"
    - **Max Items for this OR block**: 20
- **OR Block 3**: 
    - **Genre** is in "Sci-Fi;Fantasy"
    - **Max Items for this OR block**: 20
- **Sort by**: Community Rating (descending)
- Result: Top 20 highest-rated items from each genre group (60 items total)

### Balanced Continue Watching
Mix next episodes from different show types:

- **OR Block 1**: 
    - **Next Unwatched** = True
    - **Genre** contains "Drama"
    - **Max Items for this OR block**: 5
- **OR Block 2**: 
    - **Next Unwatched** = True
    - **Genre** contains "Comedy"
    - **Max Items for this OR block**: 5
- **OR Block 3**: 
    - **Next Unwatched** = True
    - **Genre** contains "Documentary"
    - **Max Items for this OR block**: 3
- Result: Your next 5 drama episodes, 5 comedy episodes, and 3 documentaries to watch

### Mixed Era Playlist
Create a time-traveling playlist with content from different decades:

- **OR Block 1**: 
    - **Production Year** between 1980-1989
    - **Max Items for this OR block**: 15
- **OR Block 2**: 
    - **Production Year** between 1990-1999
    - **Max Items for this OR block**: 15
- **OR Block 3**: 
    - **Production Year** between 2000-2009
    - **Max Items for this OR block**: 15
- **OR Block 4**: 
    - **Production Year** after 2010
    - **Max Items for this OR block**: 15
- **Sort by**: Random
- Result: 15 items from each decade, shuffled together

### Genre-Grouped Movie Night
Create distinct sections for different genres in one playlist:

- **OR Block 1**: 
    - **Genre** contains "Action"
    - **Max Items for this OR block**: 10
- **OR Block 2**: 
    - **Genre** contains "Comedy"
    - **Max Items for this OR block**: 10
- **OR Block 3**: 
    - **Genre** contains "Horror"
    - **Max Items for this OR block**: 10
- **Sort by**: Rule Block Order (primary), Community Rating descending (secondary)
- Result: 10 top-rated action movies, then 10 top-rated comedies, then 10 top-rated horror movies - all in separate sections

## Music

### Album Collection by Genre
- **Genre** contains "Rock"
- **Media Type**: Album (Music)
- **List Type**: Collection
- Creates a collection of all rock albums in your library
- Albums can only be added to Collections, not Playlists

### Workout Mix
- **Genre** contains "Electronic" OR "Rock" AND **Max Playtime** 45 minutes

### Discover New Music
- **Play Count** = 0 AND **Date Created** newer than "1 month"

### Top Rated Favorites
- **Is Favorite** = True AND **Community Rating** greater than 8

### Rediscover Music
- **Last Played** older than 6 months

### Family Favorites Playlist (Multi-User)
- **Is Favorite** = True (no user selected - uses each playlist user's data)
- **Users**: Select multiple family members (e.g., "Mom", "Dad", "Alice", "Bob")
- Each family member gets their own personalized playlist showing their favorites
- Auto-refreshes when any family member marks/unmarks items as favorites

## Home Videos & Photos

### Recent Family Memories
- **Date Created** newer than "3 months" (both videos and photos)

### Vacation Videos Only
- **Tags** contain "Vacation" (select Home Videos media type)

### Photo Slideshow
- **Production Year** = 2024 (select Home Photos media type)

### Birthday Memories
- **File Name** contains "birthday" OR **Tags** contain "Birthday"

## Collections

Collections are great for organizing related content that you want to browse together:

### Action Movie Collection
- **Genre** contains "Action"
- **Media Type**: Movie
- **List Type**: Collection
- Creates a collection that groups all action movies together for easy browsing

### Solo Studio Collection
- **Studios** equals "A24"
- **Media Type**: Movie
- Creates a collection of movies produced exclusively by A24 (excludes co-productions with other studios)
- Use **contains** instead if you want to include co-productions

### Holiday Collection
- **Tags** contain "Christmas" OR "Holiday"
- **List Type**: Collection
- Groups all holiday-themed content (movies, TV shows, music) into one collection

### Director's Collection
- **People** contains "Christopher Nolan" (Director role)
- **List Type**: Collection
- Creates a collection of all movies by a specific director

### Library-Specific Playlist
- **Library Name** equals "4K Movies"
- **Playback Status** = Unplayed
- Creates a playlist of only unwatched content from your "4K Movies" library
- Useful when you have multiple libraries of the same type (e.g., "Movies" and "4K Movies")

### Exclude Library from Playlist
- **Library Name** not equals "Kids Movies"
- **Genre** contains "Animation"
- Creates a playlist of animated content, but excludes anything from your "Kids Movies" library

## Operator Examples

### Using "Is In" for Multiple Values

Instead of creating multiple OR rule groups, use semicolons to match multiple values in one rule:

**Before (verbose):**
```
Rule Group 1: Genre contains "Action", Playback Status = Unplayed
Rule Group 2: Genre contains "Comedy", Playback Status = Unplayed
Rule Group 3: Genre contains "Drama", Playback Status = Unplayed
```

**After (simplified):**
```
Rule Group 1: Genre is in "Action;Comedy;Drama", Playback Status = Unplayed
```

Both produce the same result, but the second is simpler to maintain.

### Filtering by Weekday

Use the **weekday** operator to filter by day of week:

- **Friday Premieres**: Release Date weekday Friday
- **Recent Monday Releases**: Release Date weekday Monday AND Release Date newer than 6 months
- **Weekend Additions**: Date Added to Library weekday Saturday OR Date Added to Library weekday Sunday

### Regex Pattern Examples

Use **matches regex** with .NET syntax for advanced matching:

| Goal | Pattern | Matches |
|------|---------|---------|
| Case-insensitive | `(?i)swe` | "swe", "Swe", "SWE" |
| Multiple options | `(?i)(eng\|en)` | "eng", "EN", "en" |
| Starts with | `^Action` | "Action Movie", "Action Hero" |
| Ends with | `Movie$` | "Action Movie", "Comedy Movie" |
| Whole word | `\bAction\b` | "Action" but not "ActionMovie" |

**Finding original French films:**

- **Audio Languages** matches regex `(?i)fra?` with "Must be default language" enabled
- Matches "fr" or "fra" as default audio, excluding English films with French dubs

