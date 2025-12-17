# Advanced Examples

Here are some more complex playlist and collection examples:

## Weekend Binge Queue
- **Next Unwatched** = True (excluding unwatched series) for started shows only

## Kids' Shows Progress
- **Next Unwatched** = True AND **Tags** contain "Kids" (with parent series tags enabled)

## Foreign Language Practice
- **Audio Languages** match `(?i)(ger|fra|spa)` AND **Playback Status** = Unplayed

## Tagged Series Marathon
- **Tags** is in "Drama;Thriller" (with parent series tags enabled) AND **Runtime** < 50 minutes

## High-Quality FLAC Music
- **Audio Codec** = "FLAC" AND **Audio Bit Depth** >= 24 AND **Audio Sample Rate** >= 96000

## Lossless Audio Collection
- **Audio Codec** is in "FLAC;ALAC" (lossless formats)

## High Bitrate Music
- **Audio Bitrate** >= 320 (high-quality MP3 or lossless)

## Surround Sound Movies
- **Audio Channels** >= 6 (5.1 or higher)

## Dynamic Playlist from User Favorites
- **Playlists** contains "favorites" (match any user-created favorites playlists)
- **Playback Status** = Unplayed (combine with unplayed filter)
- Creates a dynamic playlist that pulls unplayed items from any existing favorites playlists
- Automatically updates as playlists are added/removed or items are marked as played

## Genre-Based Playlist Mixer
- **Rule Group 1**: **Playlists** contains "Rock" AND **Community Rating** >= 8
- **Rule Group 2**: **Playlists** contains "Electronic" AND **Play Count** = 0
- Combines highly-rated rock tracks with unplayed electronic music
- Uses OR logic between rule groups to pull from multiple playlist categories

## Smart Collection of Music Playlists
- **Playlists** matches regex `(?i)(workout|running|gym)` with "Include playlist only" enabled
- **List Type**: Collection
- Creates a collection that contains your exercise-related playlist objects
- Great for organizing themed playlists without duplicating content
- Use regex for flexible pattern matching (e.g., case-insensitive matching of multiple keywords)