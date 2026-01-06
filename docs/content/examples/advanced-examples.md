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

## Advanced Per-Group Limit Techniques

### Curated Discovery Playlist
Combine different content discovery strategies with precise control:

- **OR Block 1** (Recent Additions):
  - **Date Created** after 7:Days
  - **Playback Status** = Unplayed
  - **Max Items for this OR block**: 10
- **OR Block 2** (Rediscover Old Favorites):
  - **Last Played** before 6:Months
  - **Community Rating** >= 8
  - **Max Items for this OR block**: 5
- **OR Block 3** (Highly Rated Unwatched):
  - **Community Rating** >= 9
  - **Playback Status** = Unplayed
  - **Max Items for this OR block**: 5
- **Global Max Items**: 15
- **Sort by**: Random
- Result: Up to 10 recent additions, 5 older high-rated items, and 5 top-rated unwatched items, then randomly select 15 from this pool

### Perfectly Balanced Genre Workout Mix
Create a music playlist with exact genre proportions:

- **OR Block 1** (High Energy):
  - **Genre** is in "Electronic;Dance;EDM"
  - **BPM** >= 140 (if using music metadata)
  - **Max Items for this OR block**: 20
- **OR Block 2** (Rock Energy):
  - **Genre** is in "Rock;Metal;Punk"
  - **Max Items for this OR block**: 15
- **OR Block 3** (Hip Hop):
  - **Genre** is in "Hip Hop;Rap"
  - **Max Items for this OR block**: 10
- **OR Block 4** (Recovery):
  - **Genre** is in "Ambient;Chill"
  - **Max Items for this OR block**: 5
- **Sort by**: Random
- **Max Playtime**: 45 minutes
- Result: Mix of 20 electronic, 15 rock, 10 hip hop, and 5 chill tracks (50 total), then trimmed to fit 45 minutes

### Multi-User Family Content Balance
Create a balanced playlist from different family members' preferences:

- **OR Block 1** (Dad's Action):
  - **Genre** contains "Action"
  - **Playback Status** = Unplayed for user "Dad"
  - **Max Items for this OR block**: 3
- **OR Block 2** (Mom's Drama):
  - **Genre** contains "Drama"
  - **Playback Status** = Unplayed for user "Mom"
  - **Max Items for this OR block**: 3
- **OR Block 3** (Kids' Content):
  - **Parental Rating** is in "G;PG"
  - **Max Items for this OR block**: 4
- **Sort by**: Random
- Result: 3 unwatched action movies for dad, 3 unwatched dramas for mom, and 4 kid-friendly items (10 total)

### Temporal Content Balance
Create a playlist that balances content across release eras and rating tiers:

- **OR Block 1** (Modern Masterpieces):
  - **Production Year** >= 2020
  - **Community Rating** >= 9
  - **Max Items for this OR block**: 8
- **OR Block 2** (Recent Quality):
  - **Production Year** >= 2020
  - **Community Rating** between 7-8.9
  - **Max Items for this OR block**: 12
- **OR Block 3** (Classic Gems):
  - **Production Year** < 2020
  - **Community Rating** >= 8.5
  - **Max Items for this OR block**: 10
- **Sort by**: Random
- **Global Max Items**: 25
- Result: Balanced mix prioritizing modern masterpieces, recent quality content, and classic gems, randomly selecting 25 items from the pool

### Structured Multi-Section Playlist
Create a playlist with distinct sections that remain grouped:

- **OR Block 1** (Opening: Recent Blockbusters):
  - **Date Created** after 30:Days
  - **Community Rating** >= 8
  - **Max Items for this OR block**: 5
- **OR Block 2** (Main Content: Unwatched Classics):
  - **Playback Status** = Unplayed
  - **Production Year** < 2000
  - **Community Rating** >= 8.5
  - **Max Items for this OR block**: 15
- **OR Block 3** (Closing: Fan Favorites):
  - **Is Favorite** = True
  - **Play Count** >= 3
  - **Max Items for this OR block**: 5
- **Sort by**: 
  - **Primary**: Rule Block Order (keeps sections intact)
  - **Secondary**: Community Rating descending (orders within each section)
- Result: A structured 25-item playlist with three distinct sections:
  1. Opening section: 5 recent highly-rated new releases
  2. Main section: 15 highly-rated unwatched classics
  3. Closing section: 5 of your most-watched favorites

