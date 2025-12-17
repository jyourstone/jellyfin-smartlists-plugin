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
- **Collections** contains "Movie Franchise" (includes all movies in the franchise)
- **Note**: For Playlists, this fetches all media items from within the collection. For Collections, you can optionally enable "Include collection only" to create a meta-collection that contains the collection object itself

### Meta-Collection (Collection of Collections)
- **Collections** is in "Marvel;DC;Star Wars" with "Include collection only" enabled
- **List Type**: Collection
- **Note**: When "Include collection only" is enabled, your selected media types are ignored, and the collection will contain the actual collection objects rather than the media items within them
- Creates a single collection that organizes multiple collections together (e.g., a "Superhero Universes" collection containing your Marvel, DC, and other superhero collections)
- **Important**: The smart collection will never include itself in the results, even if its name matches the rule. So you can safely name your meta-collection "Superhero Universes" and use rules that match "Marvel" without worrying about it including itself

### Combine Multiple Playlists
- **Playlists** is in "Favorites;Top Rated;Recent Additions"
- **List Type**: Playlist
- **Note**: For playlists, this fetches all media items from within the specified playlists and combines them into a single "super playlist"
- Creates a playlist that merges content from multiple existing playlists
- Perfect for creating aggregated playlists like "Best of All Time" that combines your various curated playlists
- **Important**: Only playlists you own or that are marked as public are accessible. The smart playlist will never include itself in the results.

### Playlist Organization Collection
- **Playlists** contains "workout" with "Include playlist only" enabled
- **List Type**: Collection
- **Note**: When "Include playlist only" is enabled, the collection contains the actual playlist objects (not the media items within them)
- Creates a collection that organizes your playlists by category (e.g., a "Workout Playlists" collection containing all your workout-related playlists)
- Useful for managing large numbers of playlists by grouping them into categories
- **Important**: The smart collection will never include itself, and only playlists you own or that are public are accessible

### Unplayed Sitcom Episodes
- **Tags** contains "Sitcom" (with parent series tags enabled) AND **Playback Status** = Unplayed

## Music

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

### Holiday Collection
- **Tags** contain "Christmas" OR "Holiday"
- **List Type**: Collection
- Groups all holiday-themed content (movies, TV shows, music) into one collection

### Director's Collection
- **People** contains "Christopher Nolan" (Director role)
- **List Type**: Collection
- Creates a collection of all movies by a specific director

