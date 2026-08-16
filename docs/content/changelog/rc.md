# Release Candidates

Every release candidate, newest first. RCs ship from the
[unstable manifest](../getting-started/installation.md#try-rc-releases-unstable) and their
documentation lives on the
[preview site](https://jellyfin-smartlists-plugin-preview.dinsten.se/).

Each entry covers what changed since the previous RC. Everything here also reaches the next
[stable release](stable.md), whose entry restates the whole cycle in one place.

Version numbers are .NET four-part versions (`Major.Minor.Build.Revision`), not SemVer.
A non-zero final segment is the RC number — older entries instead number the RC in the tag
itself (`v10.10.10.0-rc3`), which is the scheme used before the number moved into the version.


## Unreleased

**Bug Fixes**

- Smart collections no longer see themselves when a rule checks **Collection name**. Previously a collection whose own name matched its rule — which the default `[Smart]` suffix makes easy, for example a "not contains smart" rule meant to list uncollected items — flipped between full and empty on every refresh, because its own contents fed back into its own rule. Smart playlists already had this protection; collections did not ([#499](https://github.com/jyourstone/jellyfin-smartlists-plugin/issues/499)).
- A list is now recognised by identity rather than by name, so a separate collection or playlist that merely shares the name is matched normally, and renaming the Jellyfin list does not break the exclusion.
- Fixed **Collection name** and **Playlist name** rules returning wrong results when several lists were refreshed in one batch. The first list's self-exclusion leaked into the lists refreshed after it, making them blind to that list.

**Existing lists may change**

- Collections with a **Collection name** rule that matched their own name will settle on the correct contents instead of alternating. Lists affected by the batch-refresh problem above may gain items they were previously missing.


## v12.0.0.16-rc

*2026-08-15 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.16-rc)*

**Improvements**

- Rules that check parent tags, studios or genres now look at **every** level above an item — season, series, album, artist folder, the folders they sit in, and the library itself — instead of stopping at the parent series or album. A tag set on a season or on a whole library now applies to everything inside it. The option is also no longer limited to episodes and music, so it works for movies and every other media type ([#495](https://github.com/jyourstone/jellyfin-smartlists-plugin/issues/495)).
- Library-level tags, studios and genres are now also found for symlinked and plugin-created libraries.

**Existing lists may change**

- Lists that already had "also check parent" enabled will change contents on their first refresh, because more levels now count. Rules that look for a match (equals, contains, is in) will match **more** items. Rules that exclude (not equals, not contains, is not in) will match **fewer**.
- Music lists are the most visible case: values from the artist folder and the library now count alongside the album.
## v12.0.0.15-rc

*2026-08-14 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.15-rc)*

**Features**

- New external list provider: Scrob
- New integration API so other tools can create and manage smart lists

**Bug Fixes**

- Titles containing numbers now sort in the expected order - "Season 2" before "Season 10" instead of after it. Affects music tracks and episodes sorted by Name, and round robin group order. Works with non-Latin digits too.
- The Resolution sort option did nothing and quietly left lists sorted by name
- Some sorts produced a different order depending on how many sort options you had configured
- Episodes came back unordered when Season/Episode was combined with another sort
- Lists sorted by Play Count ordered equally-played items inconsistently
- A series showed a play count of 0 even when all of its episodes were watched
- "Last Played is <date>" matched nothing unless playback happened exactly at midnight UTC
- A complex regex rule could tie up a CPU core for an entire refresh
- Saving the User-Agent setting could overwrite another setting saved at the same moment
- External lists from Scrob with no items are now handled cleanly

## v12.0.0.14-rc

*2026-08-13 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.14-rc)*

**Features**

- New User-Agent setting for external lists: choose Default, Auto Clone, Clone, or a custom value if a list provider starts blocking requests (Settings > External Lists)
- New default media types setting: pre-select which media types new smart lists start with (Settings > Default Settings)

**Bug Fixes**

- Rule groups mixing collection/playlist rules with other rules now correctly apply AND logic
- Per-group limits no longer drop items from include-only collection/playlist rules
- Nested collections shared by several rule groups now count toward every matching group

## v12.0.0.13-rc

*2026-08-08 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.13-rc)*

**Features**

- New option for Release Date and Last Episode Air Date rules: include items whose date is missing from metadata, so brand-new episodes show up in "newer than" lists even before their release date arrives

**Bug Fixes**

- Items with an unknown release date no longer incorrectly match "before", "older than" and "not equals" date rules (they previously counted as released in 1970)

## v12.0.0.12-rc

*2026-08-05 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.12-rc)*

**Bug Fixes**

- Fixed the configuration pages loading blank when Jellyfin is served through Cloudflare with Rocket Loader enabled
- Trakt now explains that a "Forbidden" error means a rejected or missing client ID, instead of showing a raw status that looked like a problem with the list's privacy setting

## v12.0.0.11-rc

*2026-07-31 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.11-rc)*

**Bug Fixes**

- Fixed IMDb lists, charts and awards failing to load with a "Forbidden" error after a change on IMDb's side

## v12.0.0.10-rc

*2026-07-28 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.10-rc)*

**Features**

- New setting to control whether newly created lists hide themselves when empty

**Bug Fixes**

- Fixed the configuration page loading blank when Jellyfin runs behind a reverse proxy using the recommended Nginx security headers

## v12.0.0.9-rc

*2026-07-23 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.9-rc)*

**Features**

- List templates: one-click starting points on the Create tab

**Improvements**

- External music lists now add one library item per matched track, with more accurate MusicBrainz-based matching

**Bug Fixes**

- Playlists again show total duration, genres, studios and parental rating (broken since the cover badge update)

## v12.0.0.8-rc

*2026-07-22 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.8-rc)*

**Features**

- New external list provider: ListenBrainz. Build smart music playlists from any public ListenBrainz playlist, or from auto-updating recommendation feeds like Weekly Jams. Tracks are matched by MusicBrainz tags, with title + artist fallback for untagged libraries. An optional user token enables private playlists.

**Bug Fixes**

- Fixed the "All Users" checkbox showing the wrong state when editing or cloning a playlist, which could silently change which users a playlist applies to on save.

## v12.0.0.7-rc

*2026-07-17 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.7-rc)*

**Improvements**

- Crossover episodes airing on the same day now play in the order of each show's Sort Title, so you can control the order within a crossover night by editing the Sort Title on the series
- The weekly cleanup task now also removes leftover Jellyfin playlists and collections whose smart list configuration no longer exists

## v12.0.0.6-rc

*2026-07-15 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.6-rc)*

**Features**

- Smart list badge: playlist and collection covers created by the plugin now show a smart list badge, with a new admin setting to turn it off ("Cover Images" section). Playlist covers are generated by the plugin and crop cleanly to Jellyfin's tiles.
- Least Recently Watched Round Robin holds a collection at the front of the rotation while you are mid-way through a crossover air block - watching part 1 of a crossover night no longer pushes parts 2 and 3 to the bottom of the playlist.

**Improvements**

- Only fully watched episodes advance the Least Recently Watched rotation - stopping half-way through an episode no longer sends the show to the back.

## v12.0.0.5-rc

*2026-07-14 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.5-rc)*

**Bug Fixes**

- Fixed smart collections disappearing when the TMDbBoxSets plugin is installed. Jellyfin's metadata matching could tag smart collections with an incorrect TMDB ID, causing TMDbBoxSets to delete them as orphaned. Smart collections are now locked against metadata matching, and already-affected collections heal automatically on their next refresh.
- Fixed "Error in metadata saver" errors caused by multiple refreshes writing collection metadata at the same time.

## v12.0.0.4-rc

*2026-07-14 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.4-rc)*

**Features**

- Crossover air blocks for Round Robin with collection grouping: episodes from different shows that aired close together (same-night crossovers, franchise weeks) now play back-to-back instead of a full rotation apart
- New "Air Window (days)" setting on the sort controls how close together episodes must have aired (default 3 days, 0 = same day only)

## v12.0.0.3-rc

*2026-07-13 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.3-rc)*

**Features**

- New "Hide when empty" option: hides a playlist or collection while its rules match no items, and brings it back automatically once items match again
- Round Robin sorting can now group by Collection and control the order of items within each group

**Bug Fixes**

- Fixed duplicate smart playlists and collections that could appear after failed deletions; leftover orphans are now detected and cleaned up automatically

## v12.0.0.2-rc

*2026-07-11 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.2-rc)*

**New Features**

- Added a new random group selection feature ([#437](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/437))
- Added Shuffled Round Robin sorting and Bumpers ([#438](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/438))
- Added Least Recently Watched Round Robin sort ([#441](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/441))
**Other Changes**

- SmartLists no longer does unconditional metadata saves just to re-set the same collection name/display order ([#436](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/436))
- Added "More options" collapsable section when creating/editing lists. ([#442](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/442))
- Added search and section groups to the Sort By dropdown ([#443](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/443))

## v12.0.0.1-rc

*2026-06-28 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.1-rc)*

**Bug Fixes**

- Improved image handling to prevent conflicts between jpg and jpeg ([#428](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/428))

## v12.0.0.0-rc

*2026-06-23 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.0-rc)*

**New Features**

- Added support for Jellyfin 12.0-rc1 ([#426](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/426))

## v10.11.24.102-rc

*2026-04-01 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.24.102-rc)*

**New Features**

- Added Random Round Robin sorting option ([#350](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/350))

## v10.11.24.101-rc

*2026-03-31 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.24.101-rc)*

**New Features**

- Added new Series Status field ([#349](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/349))

## v10.11.24.100-rc

*2026-03-30 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.24.100-rc)*

**New Features**

- Added support for Series Season media type for collections ([#344](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/344))
- Add Round Robin sorting feature ([#346](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/346))
**Other Changes**

- Create configuration directory for logging.json ([#345](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/345))

**New Contributors**

- @adripo made their first contribution in [#345](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/345)

## v10.11.22.103-rc

*2026-02-27 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.22.103-rc)*

**Bug Fixes**

- Fixed an issue when using multiple external lists ([#318](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/318))

## v10.11.22.102-rc

*2026-02-26 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.22.102-rc)*

**New Features**

- Added Letterboxd support for external lists and optimized fetching logic ([#316](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/316))
**Other Changes**

- Modified sorting precision for DateCreated ([#315](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/315))

## v10.11.22.101-rc

*2026-02-22 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.22.101-rc)*

**New Features**

- Added support for configuring Sort Title and Overview. ([#312](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/312))

## v10.11.22.100-rc

*2026-02-22 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.22.100-rc)*

**New Features**

- Added support for 'Music Album' media type in Collections ([#309](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/309))

## v10.11.21.102-rc

*2026-02-18 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.21.102-rc)*

**New Features**

- Added Last Episode Air Date sorting option ([#306](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/306))
**Bug Fixes**

- The equal/not equal operators now work properly with list fields, such as Studios. ([#304](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/304))

## v10.11.21.101-rc

*2026-02-13 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.21.101-rc)*

**New Features**

- Added support for sorting by External List Order ([#298](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/298))
- Added support for including extras ([#300](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/300))

## v10.11.21.100-rc

*2026-02-11 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.21.100-rc)*

**New Features**

- Added support for grabbing external lists from MDBList, IMDb, Trakt, and TMDB ([#296](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/296))
**Other Changes**

- Improved music collection cover generation ([#292](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/292))

## v10.11.20.102-rc

*2026-01-28 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.20.102-rc)*

**New Features**

- Added support for automated backups in settings ([#288](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/288))

## v10.11.20.101-rc

*2026-01-27 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.20.101-rc)*

**New Features**

- You can now bulk convert playlists/collections ([#285](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/285))

## v10.11.20.100-rc

*2026-01-26 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.20.100-rc)*

**New Features**

- Added new 'Library Name' field ([#277](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/277))
**Other Changes**

- Refactored field definitions and extraction logic ([#278](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/278))
- Refactored rule field extraction to increase performance ([#279](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/279))
- Minor UX fixes ([#282](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/282))

## v10.11.16.105-rc

*2026-01-21 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.16.105-rc)*

**New Features**

- Added support for uploading images ([#268](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/268))
- Support nested collection searches with customizable search depth property ([#272](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/272))
- Added support for custom rating, subtitle languages, and last episode air date. ([#273](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/273))
**Other Changes**

- Changed file storage structure ([#270](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/270))

## v10.11.16.104-rc

*2026-01-18 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.16.104-rc)*

**New Features**

- Added support for sorting collections by aggregated child item values in collections/playlists ([#266](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/266))

## v10.11.16.103-rc

*2026-01-17 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.16.103-rc)*

**Other Changes**

- Changed styling for compability with other Jellyfin themes ([#265](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/265))

## v10.11.16.102-rc

*2026-01-16 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.16.102-rc)*

**Bug Fixes**

- Fixed an issue with clone logic ([#264](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/264))

## v10.11.16.101-rc

*2026-01-12 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.16.101-rc)*

**Bug Fixes**

- Fixed an issue where the new user config page would not load properly in some cases. ([#258](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/258))
**Other Changes**

- Prevent refresh of disabled lists ([#259](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/259))

## v10.11.16.100-rc

*2026-01-12 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.16.100-rc)*

**New Features**

- Users can now create and manage their own smart lists (requires the plugins 'File Transformation' and 'Plugin Pages'). Read more in the documentation: https://jellyfin-smartlists-plugin.dinsten.se/getting-started/quick-start/ ([#257](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/257))

## v10.11.15.101-rc

*2026-01-06 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.15.101-rc)*

**Other Changes**

- Removed interleaved sorting and fixed some issues with rule group sorting ([#253](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/253))

## v10.11.15.100-rc

*2026-01-04 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.15.100-rc)*

**New Features**

- You can now sort by rule groups (including interleaved sorting) ([#251](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/251))

## v10.11.10.101-rc

*2025-11-25 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.10.101-rc)*

**Bug Fixes**

- Fixed an issue where lists wouldn't update automatically when a media item was marked as unwatched. ([#217](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/217))

## v10.11.10.100-rc

*2025-11-25 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.10.100-rc)*

**New Features**

- Added support for multiple playlist users. ([#214](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/214))
- Added support for choosing only Audio Languages marked as default ([#215](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/215))
**Bug Fixes**

- Fixed issue where user names could show up twice in the list filters. ([#211](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/211))
**Other Changes**

- Added a multi-select dropdown for media types. ([#216](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/216))

## v10.11.9.104-rc

*2025-11-23 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.9.104-rc)*

**Bug Fixes**

- Fixed an issue where the status page wouldn't load properly in some cases ([#205](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/205))
- Fixed an issue where you could not leave Suffix Text blank. ([#207](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/207))

## v10.11.9.103-rc

*2025-11-20 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.9.103-rc)*

**New Features**

- Added weekday operator support for date fields ([#202](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/202))
- Added support for bulk refreshing lists ([#204](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/204))
**Other Changes**

- Minor UI improvements ([#203](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/203))

## v10.11.9.102-rc

*2025-11-19 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.9.102-rc)*

**Bug Fixes**

- Fixed issue where Collections would not get properly deleted ([#197](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/197))
- Fixed issues when sorting by series name and episode name ([#199](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/199))
**Other Changes**

- Implemented a new queue system along with a new cache system for increased performance when multiple lists are refreshed ([#196](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/196))
- Sorting by name and series name now sorts by SortTitle metadata first ([#198](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/198))

## v10.11.9.101-rc

*2025-11-18 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.9.101-rc)*

**New Features**

- Notification message changes ([#193](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/193))
**Other Changes**

- Fixed formatting for JellyfinPlaylistId ([#194](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/194))

## v10.11.9.100-rc

*2025-11-17 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.9.100-rc)*

**Major update with support for Collections!**

- Name changed from SmartPlaylist to **SmartLists**
- The entire codebase has been **refactored**
- Support for **Collections** has been added.
- A **status page** has been added
- And **many** other backend changes

**Breaking changes**

- Migration logic from older versions has been removed, you must be running **Jellyfin 10.11.2.0** and have **refreshed all playlists** prior to updating — skipping this may result in broken playlists.
- The legacy Jellyfin tasks have been removed. Make sure no playlists are using these, and if so, set a custom schedule for them instead.

If you find any bugs, please report them here: https://github.com/jyourstone/jellyfin-smartlists-plugin/issues

## v10.11.1.101-rc

*2025-11-10 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.1.101-rc)*

**Other Changes**

- Improved performance by using parallel processing for more fields. ([#183](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/183))

## v10.11.1.100-rc

*2025-11-09 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.1.100-rc)*

**New Features**

- Added support for multiple sorting options. Added Episode and Season as sorting options. ([#175](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/175))
- Add sorting options for Last Played, Runtime, Series Name, Album Name and Artist. ([#180](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/180))
- The rule fields Studios and Genres can now search parent series for episodes. ([#181](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/181))
**Bug Fixes**

- Fixed a bug where the UI would incorrectly always show "Sunday" for weekly schedules. ([#179](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/179))
**Other Changes**

- Only show sort options related to the media type(s) selected. ([#177](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/177))

## v10.11.0.102-rc

*2025-11-03 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.0.102-rc)*

**New Features**

- Added conditional rule field visibility logic, only displaying the rule fields related to the chosen media type. ([#167](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/167))
- Added support for all Jellyfin people fields. ([#168](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/168))

## v10.11.0.101-rc

*2025-10-29 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.0.101-rc)*

_No release notes were recorded for this version._

## v10.11.0.100-rc

*2025-10-28 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.0.100-rc)*

**New Features**

- You can now add multiple schedules. ([#155](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/155))
- Added rule field support for: Actors, Directors, Producers, Writers, Guest Stars ([#157](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/157))
- You can now choose which metadata properties to include when selecting the 'Similar To' rule field. ([#158](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/158))
- Added support for the following audio metadata: Bitrate, Sample Rate, Bit Depth, Codec, Channels. ([#160](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/160))
**Bug Fixes**

- Fixed issue where equals/not equals operators were missing for some rule fields. ([#148](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/148))
**Other Changes**

- Simplified and optimized the AutoRefresh setting logic ([#161](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/161))

## v10.11.0.0-rc8

*2025-10-10 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.0.0-rc8)*

**New Features**

- Added a link to the Jellyfin playlist in the properties table. ([#137](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/137))
- Use parallel scans under specific conditions, added "Parallel Concurrency Limit" setting. ([#140](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/140))

## v10.10.10.0-rc8

*2025-10-06 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc8)*

**New Features**

- Sorting by name now takes into account if the name begins with numbers and sorts them properly. ([#133](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/133))
- Added playlist statistics ([#134](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/134))

## v10.10.10.0-rc7

*2025-10-02 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc7)*

**New Features**

- Added option to ignore leading article 'The' when sorting by name ([#127](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/127))
- You can now choose to include tags from parent Series for episodes. ([#130](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/130))

## v10.10.10.0-rc6

*2025-09-30 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc6)*

_No release notes were recorded for this version._

## v10.10.10.0-rc5

*2025-09-29 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc5)*

**Other Changes**

- Implement time buffer checks for playlist refresh scheduling ([#125](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/125))

## v10.10.10.0-rc4

*2025-09-28 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc4)*

**New Features**

- Added "hours" option to the Newer Than/Older than operators and allow zero as value. ([#123](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/123))
**Other Changes**

- Removed 'Series' media type due to Jellyfin limitations ([#120](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/120))

## v10.10.10.0-rc3

*2025-09-10 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc3)*

**Bug Fixes**

- Some minor UI fixes and tweaks ([#115](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/115))
**Other Changes**

- Various minor fixes and tweaks. ([#117](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/117))

## v10.10.10.0-rc2

*2025-09-08 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc2)*

**New Features**

- Major improvements to the 'Manage Playlists' page (filter, bulk select, clone playlists, etc). ([#113](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/113))
**Bug Fixes**

- Fixed issue where the scheduled time for refresh would show wrong in the UI ([#111](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/111))

## v10.10.10.0-rc1

*2025-09-06 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc1)*

**New Features**

- Added option to update playlists automatically based on library updates. ([#98](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/98))
- You can now set individual playlist refresh schedules ([#103](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/103))
- You can now sort by play count. ([#106](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/106))
**Other Changes**

- Separated "Refresh All Playlists" logic from Jellyfin tasks. ([#105](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/105))
- Minor sorting UI changes. ([#107](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/107))
- Styling adjustments ([#108](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/108))

## v10.10.6.1-rc4

*2025-08-20 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.6.1-rc4)*

**New Features**

- Improvements to release date sorting ([#77](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/77))
**Other Changes**

- Code optimizations ([#78](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/78))

## v10.10.6.1-rc3

*2025-08-19 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.6.1-rc3)*

_No release notes were recorded for this version._

## v10.10.6.1-rc2

*2025-08-19 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.6.1-rc2)*

_No release notes were recorded for this version._

## v10.10.6.1-rc

*2025-08-18 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.6.1-rc)*

**New Features**

- You can now include individual episodes from series within the Collections rule field ([#74](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/74))

## v2.2.0

*2025-07-04 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v2.2.0)*

Jellyfin 10.11 support and new features:

- You can now choose and mix AND/OR logic when creating rules
- Option to select media type first to go well in hand with the new rules logic
- Added flexible deletion options (config only vs config + playlist)
- Works with Jellyfin 10.11.0.0-rc2
