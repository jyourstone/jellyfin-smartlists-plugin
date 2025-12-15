# Sorting and Limits

## Multiple Sorting Levels

You can add up to **3 sorting options** for both playlists and collections to create cascading sorts. Items are first sorted by the first option, then items with equal values are sorted by the second option, and so on.

### Example Use Cases

- **Best Movies by Year**: Sort by "Production Year" descending, then "Community Rating" descending - Groups movies by year, with highest-rated movies first within each year
- **Least Played Mix**: Sort by "Play Count (owner)" ascending, then "Random" - Prioritizes less-played items, while shuffling tracks with the same play count to prevent album grouping

## Available Sort Fields

- **No Order** - Items appear in library order
- **Name** - Sort by title
- **Name (Ignore 'The')** - Sort by name while ignoring leading article 'The'
- **Release Date** - Sort by release date
- **Production Year** - Sort by production year
- **Season Number** - Sort by TV season number
- **Episode Number** - Sort by TV episode number
- **Series Name** - Sort by series name (for TV episodes)
- **Community Rating** - Sort by user ratings
- **Date Created** - Sort by when added to library
- **Play Count (owner)** - Sort by how many times the list owner has played each item
- **Last Played (owner)** - Sort by when the list owner last played each item
- **Runtime** - Sort by duration/runtime in minutes
- **Album Name** - Sort by album name (for music and music videos)
- **Artist** - Sort by artist name (for music and music videos)
- **Track Number** - Sort by album name, disc number, then track number (designed for music)
- **Similarity** - Sort by similarity score (highest first) - only available when using the "Similar To" field
- **Random** - Randomize the order of items

!!! tip "Sort Title Metadata Support"
    All **Name** and **Series Name** sort options (including "Ignore Articles" variants) automatically respect Jellyfin's **Sort Title** metadata field. When you set a custom Sort Title for a media item in Jellyfin's metadata editor:
    
    - The plugin will use the Sort Title **as-is** for sorting (without any modifications)
    - This applies to both regular and "Ignore Articles" sorting options
    - If Sort Title is not set, the plugin falls back to the regular title (and strips "The" for "Ignore Articles" options)
    
    This allows you to control the exact sort order without changing the displayed title.

## Max Items

You can optionally set a maximum number of items for your smart list. This is useful for:

- Limiting large lists to a manageable size
- Creating "Top 10" or "Best of" style playlists or collections
- Improving performance for very large libraries

!!! note "Collections and Sorting"
    For both playlists and collections, the max items limit applies after sorting is applied.

!!! warning "Performance"
    Setting this to unlimited (0) might cause performance issues or even crashes for very large lists.

## Max Playtime

You can optionally set a maximum playtime in minutes for your smart playlist (this option is only available for playlists, not collections). This is useful for:

- Creating workout playlists that match your exercise duration
- Setting up Pomodoro-style work sessions with music
- Ensuring playlists fit within specific time constraints

**How it works**: The plugin calculates the total runtime of items in the playlist and stops adding items when the time limit is reached. The last item that would exceed the limit is not included, ensuring the playlist stays within your specified duration.

This feature works with all media types (movies, TV shows, music) and uses the actual runtime of each item.