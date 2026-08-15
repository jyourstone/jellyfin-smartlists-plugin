# Changelog

Every released version, newest first. Release candidates are marked **RC** — they ship
from the [unstable manifest](getting-started/installation.md#try-rc-releases-unstable)
and their documentation lives on the
[preview site](https://jellyfin-smartlists-plugin-preview.dinsten.se/).

Version numbers are .NET four-part versions (`Major.Minor.Build.Revision`), not SemVer.
A non-zero final segment means a release candidate; stable releases end in `.0`.


## Unreleased

*Not yet released — ships in the next version.*

**Improvements**

- Rules that check parent tags, studios or genres now look at **every** level above an item — season, series, album, artist folder, the folders they sit in, and the library itself — instead of stopping at the parent series or album. A tag set on a season or on a whole library now applies to everything inside it. The option is also no longer limited to episodes and music, so it works for movies and every other media type ([#495](https://github.com/jyourstone/jellyfin-smartlists-plugin/issues/495)).
- Library-level tags, studios and genres are now also found for symlinked and plugin-created libraries.

**Existing lists may change**

- Lists that already had "also check parent" enabled will change contents on their first refresh, because more levels now count. Rules that look for a match (equals, contains, is in) will match **more** items. Rules that exclude (not equals, not contains, is not in) will match **fewer**.
- Music lists are the most visible case: values from the artist folder and the library now count alongside the album.

## v12.0.0.15-rc

*2026-08-14 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.15-rc)*

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

*2026-08-13 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.14-rc)*

**Features**

- New User-Agent setting for external lists: choose Default, Auto Clone, Clone, or a custom value if a list provider starts blocking requests (Settings > External Lists)
- New default media types setting: pre-select which media types new smart lists start with (Settings > Default Settings)

**Bug Fixes**

- Rule groups mixing collection/playlist rules with other rules now correctly apply AND logic
- Per-group limits no longer drop items from include-only collection/playlist rules
- Nested collections shared by several rule groups now count toward every matching group

## v12.0.0.13-rc

*2026-08-08 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.13-rc)*

**Features**

- New option for Release Date and Last Episode Air Date rules: include items whose date is missing from metadata, so brand-new episodes show up in "newer than" lists even before their release date arrives

**Bug Fixes**

- Items with an unknown release date no longer incorrectly match "before", "older than" and "not equals" date rules (they previously counted as released in 1970)

## v12.0.0.12-rc

*2026-08-05 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.12-rc)*

**Bug Fixes**

- Fixed the configuration pages loading blank when Jellyfin is served through Cloudflare with Rocket Loader enabled
- Trakt now explains that a "Forbidden" error means a rejected or missing client ID, instead of showing a raw status that looked like a problem with the list's privacy setting

## v12.0.0.11-rc

*2026-07-31 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.11-rc)*

**Bug Fixes**

- Fixed IMDb lists, charts and awards failing to load with a "Forbidden" error after a change on IMDb's side

## v12.0.0.10-rc

*2026-07-28 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.10-rc)*

**Features**

- New setting to control whether newly created lists hide themselves when empty

**Bug Fixes**

- Fixed the configuration page loading blank when Jellyfin runs behind a reverse proxy using the recommended Nginx security headers

## v12.0.0.9-rc

*2026-07-23 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.9-rc)*

**Features**

- List templates: one-click starting points on the Create tab

**Improvements**

- External music lists now add one library item per matched track, with more accurate MusicBrainz-based matching

**Bug Fixes**

- Playlists again show total duration, genres, studios and parental rating (broken since the cover badge update)

## v12.0.0.8-rc

*2026-07-22 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.8-rc)*

**Features**

- New external list provider: ListenBrainz. Build smart music playlists from any public ListenBrainz playlist, or from auto-updating recommendation feeds like Weekly Jams. Tracks are matched by MusicBrainz tags, with title + artist fallback for untagged libraries. An optional user token enables private playlists.

**Bug Fixes**

- Fixed the "All Users" checkbox showing the wrong state when editing or cloning a playlist, which could silently change which users a playlist applies to on save.

## v12.0.0.7-rc

*2026-07-17 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.7-rc)*

**Improvements**

- Crossover episodes airing on the same day now play in the order of each show's Sort Title, so you can control the order within a crossover night by editing the Sort Title on the series
- The weekly cleanup task now also removes leftover Jellyfin playlists and collections whose smart list configuration no longer exists

## v12.0.0.6-rc

*2026-07-15 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.6-rc)*

**Features**

- Smart list badge: playlist and collection covers created by the plugin now show a smart list badge, with a new admin setting to turn it off ("Cover Images" section). Playlist covers are generated by the plugin and crop cleanly to Jellyfin's tiles.
- Least Recently Watched Round Robin holds a collection at the front of the rotation while you are mid-way through a crossover air block - watching part 1 of a crossover night no longer pushes parts 2 and 3 to the bottom of the playlist.

**Improvements**

- Only fully watched episodes advance the Least Recently Watched rotation - stopping half-way through an episode no longer sends the show to the back.

## v12.0.0.5-rc

*2026-07-14 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.5-rc)*

**Bug Fixes**

- Fixed smart collections disappearing when the TMDbBoxSets plugin is installed. Jellyfin's metadata matching could tag smart collections with an incorrect TMDB ID, causing TMDbBoxSets to delete them as orphaned. Smart collections are now locked against metadata matching, and already-affected collections heal automatically on their next refresh.
- Fixed "Error in metadata saver" errors caused by multiple refreshes writing collection metadata at the same time.

## v12.0.0.4-rc

*2026-07-14 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.4-rc)*

**Features**

- Crossover air blocks for Round Robin with collection grouping: episodes from different shows that aired close together (same-night crossovers, franchise weeks) now play back-to-back instead of a full rotation apart
- New "Air Window (days)" setting on the sort controls how close together episodes must have aired (default 3 days, 0 = same day only)

## v12.0.0.3-rc

*2026-07-13 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.3-rc)*

**Features**

- New "Hide when empty" option: hides a playlist or collection while its rules match no items, and brings it back automatically once items match again
- Round Robin sorting can now group by Collection and control the order of items within each group

**Bug Fixes**

- Fixed duplicate smart playlists and collections that could appear after failed deletions; leftover orphans are now detected and cleaned up automatically

## v12.0.0.2-rc

*2026-07-11 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.2-rc)*

**New Features**

- Added a new random group selection feature ([#437](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/437))
- Added Shuffled Round Robin sorting and Bumpers ([#438](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/438))
- Added Least Recently Watched Round Robin sort ([#441](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/441))
**Other Changes**

- SmartLists no longer does unconditional metadata saves just to re-set the same collection name/display order ([#436](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/436))
- Added "More options" collapsable section when creating/editing lists. ([#442](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/442))
- Added search and section groups to the Sort By dropdown ([#443](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/443))

## v12.0.0.1-rc

*2026-06-28 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.1-rc)*

**Bug Fixes**

- Improved image handling to prevent conflicts between jpg and jpeg ([#428](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/428))

## v10.11.30.2

*2026-06-28 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.30.2)*

_No release notes were recorded for this version._

## v12.0.0.0-rc

*2026-06-23 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v12.0.0.0-rc)*

**New Features**

- Added support for Jellyfin 12.0-rc1 ([#426](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/426))

## v10.11.30.1

*2026-06-23 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.30.1)*

**Other Changes**

- Enhanced support for virtual folders in movie libraries ([#423](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/423))

## v10.11.30.0

*2026-06-15 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.30.0)*

**New Features**

- Added support for virtual movie libraries ([#419](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/419))

## v10.11.29.0

*2026-06-07 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.29.0)*

**New Features**

- Added support for seconds with the Runtime filter ([#409](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/409))
**Bug Fixes**

- Fixed an issue with max items for external lists ([#415](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/415))

## v10.11.28.0

*2026-05-26 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.28.0)*

**New Features**

- Added support for season playback status ([#401](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/401))
- Added "only parent" option for tags, studios, and genres rules ([#403](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/403))
- You can now reorder rule blocks with up/down arrows ([#408](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/408))

## v10.11.27.0

*2026-05-21 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.27.0)*

**New Features**

- You can now choose to include parent album genres for music ([#389](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/389))
**Bug Fixes**

- Fixed an issue with release date sorting ([#387](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/387))
- Added compability with Jellyfin 10.11.9 ([#393](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/393))

## v10.11.26.1

*2026-05-17 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.26.1)*

**Bug Fixes**

- Fixed an issue with populating incorrect items for external lists ([#383](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/383))

## v10.11.26.0

*2026-05-09 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.26.0)*

**New Features**

- Added support for metadata tags and favorite state ([#374](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/374))
- Added support for all-user playlists ([#378](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/378))

## v10.11.25.1

*2026-04-13 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.25.1)*

_No release notes were recorded for this version._

## v10.11.25.0

*2026-04-04 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.25.0)*

**New Features**

- Added support for Series Season media type for collections ([#344](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/344))
- Add Round Robin sorting feature ([#346](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/346))
- Added new Series Status field ([#349](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/349))
- Added Random Round Robin sorting option ([#350](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/350))
- Added support for TMDB Collections ([#356](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/356))
**Bug Fixes**

- Fixed issue with NotEqual operator ([#352](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/352))
**Other Changes**

- Create configuration directory for logging.json ([#345](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/345))

**New Contributors**

- @adripo made their first contribution in [#345](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/345)

## v10.11.24.102-rc

*2026-04-01 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.24.102-rc)*

**New Features**

- Added Random Round Robin sorting option ([#350](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/350))

## v10.11.24.101-rc

*2026-03-31 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.24.101-rc)*

**New Features**

- Added new Series Status field ([#349](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/349))

## v10.11.24.100-rc

*2026-03-30 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.24.100-rc)*

**New Features**

- Added support for Series Season media type for collections ([#344](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/344))
- Add Round Robin sorting feature ([#346](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/346))
**Other Changes**

- Create configuration directory for logging.json ([#345](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/345))

**New Contributors**

- @adripo made their first contribution in [#345](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/345)

## v10.11.24.0

*2026-03-23 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.24.0)*

**New Features**

- Added support for IMDb, TMDb, and TVDb IDs ([#338](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/338))
**Other Changes**

- Added support for IMDb award lists ([#337](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/337))
- Changed field dropdown to a custom dropdown with a search function plus some other minor styling changes. ([#339](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/339))

## v10.11.23.4

*2026-03-21 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.23.4)*

**Other Changes**

- Changed IMDb integration to use GraphQL API instead of scraping ([#332](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/332))

## v10.11.23.3

*2026-03-14 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.23.3)*

**New Features**

- Renamed sort option "No Order" to "Default". When using Default, it can change automatically depending on the rule field selected, for example External List will by default sort by the extern list order. ([#327](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/327))
**Bug Fixes**

- Playback status now works correctly with albums in collections ([#330](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/330))

## v10.11.23.2

*2026-03-05 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.23.2)*

**Bug Fixes**

- Enhanced error handling in backup restore process and increased upload size limit ([#324](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/324))

## v10.11.23.1

*2026-03-04 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.23.1)*

**Bug Fixes**

- Fixed an issue with ReleaseDate sorting using multiple sort orders ([#320](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/320))
- Add support for episode-level sorting in Trakt integration and update documentation ([#322](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/322))

## v10.11.23.0

*2026-03-01 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.23.0)*

**New Features**

- Added support for 'Music Album' media type in Collections ([#309](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/309))
- Added support for configuring Sort Title and Overview. IMPORTANT NOTE: Any manual edits for Sort Title or Overview directly in Jellyfin playlists/collections will be overwritten by SmartLists. ([#312](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/312))
- Added Letterboxd support for external lists and optimized fetching logic ([#316](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/316))
**Bug Fixes**

- Fixed an issue when using multiple external lists ([#318](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/318))
**Other Changes**

- Modified sorting precision for DateCreated ([#315](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/315))

## v10.11.22.103-rc

*2026-02-27 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.22.103-rc)*

**Bug Fixes**

- Fixed an issue when using multiple external lists ([#318](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/318))

## v10.11.22.102-rc

*2026-02-26 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.22.102-rc)*

**New Features**

- Added Letterboxd support for external lists and optimized fetching logic ([#316](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/316))
**Other Changes**

- Modified sorting precision for DateCreated ([#315](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/315))

## v10.11.22.101-rc

*2026-02-22 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.22.101-rc)*

**New Features**

- Added support for configuring Sort Title and Overview. ([#312](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/312))

## v10.11.22.100-rc

*2026-02-22 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.22.100-rc)*

**New Features**

- Added support for 'Music Album' media type in Collections ([#309](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/309))

## v10.11.22.0

*2026-02-20 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.22.0)*

**New Features**

- Added support for grabbing external lists from MDBList, IMDb, Trakt, and TMDB ([#296](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/296))
- Added support for sorting by External List Order ([#298](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/298))
- Added support for including extras ([#300](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/300))
- Added Last Episode Air Date sorting option ([#306](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/306))
**Bug Fixes**

- The equal/not equal operators now work properly with list fields, such as Studios. ([#304](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/304))
**Other Changes**

- Improved music collection cover generation ([#292](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/292))

## v10.11.21.102-rc

*2026-02-18 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.21.102-rc)*

**New Features**

- Added Last Episode Air Date sorting option ([#306](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/306))
**Bug Fixes**

- The equal/not equal operators now work properly with list fields, such as Studios. ([#304](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/304))

## v10.11.21.101-rc

*2026-02-13 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.21.101-rc)*

**New Features**

- Added support for sorting by External List Order ([#298](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/298))
- Added support for including extras ([#300](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/300))

## v10.11.21.100-rc

*2026-02-11 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.21.100-rc)*

**New Features**

- Added support for grabbing external lists from MDBList, IMDb, Trakt, and TMDB ([#296](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/296))
**Other Changes**

- Improved music collection cover generation ([#292](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/292))

## v10.11.21.0

*2026-01-29 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.21.0)*

**New Features**

- Added new 'Library Name' field ([#277](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/277))
- You can now bulk convert playlists/collections ([#285](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/285))
- Added support for automated backups in settings ([#288](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/288))
**Other Changes**

- Optimized rule field extraction logic - Major increase in performance ([#279](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/279))
- Minor UX fixes ([#282](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/282))

## v10.11.20.102-rc

*2026-01-28 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.20.102-rc)*

**New Features**

- Added support for automated backups in settings ([#288](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/288))

## v10.11.20.101-rc

*2026-01-27 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.20.101-rc)*

**New Features**

- You can now bulk convert playlists/collections ([#285](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/285))

## v10.11.20.100-rc

*2026-01-26 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.20.100-rc)*

**New Features**

- Added new 'Library Name' field ([#277](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/277))
**Other Changes**

- Refactored field definitions and extraction logic ([#278](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/278))
- Refactored rule field extraction to increase performance ([#279](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/279))
- Minor UX fixes ([#282](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/282))

## v10.11.20.0

*2026-01-24 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.20.0)*

**New Features**

- Users can now create and manage their own smart lists (requires the plugins 'File Transformation' and 'Plugin Pages'). Read more in the documentation: https://jellyfin-smartlists-plugin.dinsten.se/getting-started/quick-start/ ([#257](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/257))
- Added support for sorting collections by aggregated child item values in collections/playlists ([#266](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/266))
- Added support for uploading images ([#268](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/268))
- Support nested collection searches with customizable search depth property ([#272](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/272))
- Added support for production locations, custom rating, subtitle languages, and last episode air date. ([#273](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/273))
**Bug Fixes**

- Fixed an issue where the new user config page would not load properly in some cases. ([#258](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/258))
- Fixed an issue with clone logic ([#264](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/264))
- Fixed two issues when switching between collection/playlist, wrong user and unknown creation date ([#275](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/275))
**Other Changes**

- Prevent refresh of disabled lists ([#259](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/259))
- Changed styling for compability with other Jellyfin themes ([#265](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/265))
- Changed file storage structure ([#270](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/270))
- Some minor styling and UI changes ([#274](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/274))
- Added quick menu actions for lists in collapsed view ([#276](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/276))

## v10.11.16.105-rc

*2026-01-21 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.16.105-rc)*

**New Features**

- Added support for uploading images ([#268](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/268))
- Support nested collection searches with customizable search depth property ([#272](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/272))
- Added support for custom rating, subtitle languages, and last episode air date. ([#273](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/273))
**Other Changes**

- Changed file storage structure ([#270](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/270))

## v10.11.16.104-rc

*2026-01-18 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.16.104-rc)*

**New Features**

- Added support for sorting collections by aggregated child item values in collections/playlists ([#266](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/266))

## v10.11.16.103-rc

*2026-01-17 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.16.103-rc)*

**Other Changes**

- Changed styling for compability with other Jellyfin themes ([#265](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/265))

## v10.11.16.102-rc

*2026-01-16 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.16.102-rc)*

**Bug Fixes**

- Fixed an issue with clone logic ([#264](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/264))

## v10.11.16.101-rc

*2026-01-12 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.16.101-rc)*

**Bug Fixes**

- Fixed an issue where the new user config page would not load properly in some cases. ([#258](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/258))
**Other Changes**

- Prevent refresh of disabled lists ([#259](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/259))

## v10.11.16.100-rc

*2026-01-12 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.16.100-rc)*

**New Features**

- Users can now create and manage their own smart lists (requires the plugins 'File Transformation' and 'Plugin Pages'). Read more in the documentation: https://jellyfin-smartlists-plugin.dinsten.se/getting-started/quick-start/ ([#257](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/257))

## v10.11.16.0

*2026-01-09 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.16.0)*

**New Features**

- You can now sort by rule groups (including interleaved sorting) ([#251](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/251))
- Added support for Actor Roles in people metadata. ([#256](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/256))
**Bug Fixes**

- Fixed a bug where negated operators did not work with playlist/collection rules ([#255](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/255))
**Other Changes**

- Removed interleaved sorting and fixed some issues with rule group sorting ([#253](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/253))

## v10.11.15.101-rc

*2026-01-06 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.15.101-rc)*

**Other Changes**

- Removed interleaved sorting and fixed some issues with rule group sorting ([#253](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/253))

## v10.11.15.100-rc

*2026-01-04 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.15.100-rc)*

**New Features**

- You can now sort by rule groups (including interleaved sorting) ([#251](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/251))

## v10.11.15.0

*2025-12-30 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.15.0)*

**New Features**

- Collection thumbnails are now generated automatically ([#245](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/245))
- You can now schedule list visibility (enable/disable) ([#248](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/248))

## v10.11.14.0

*2025-12-20 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.14.0)*

**New Features**

- Added support for Playlists in rule fields ([#239](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/239))
**Bug Fixes**

- Fix API doc generation error ([#242](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/242))
**Other Changes**

- Added plugin shortcut in the main sidebar. ([#237](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/237))

**New Contributors**

- @RedStylzZ made their first contribution in [#242](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/242)

## v10.11.13.0

*2025-12-15 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.13.0)*

**New Features**

- You can now clone rule fields as well as clone and delete whole ruleblocks. ([#229](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/229))
- You can now sort collections! (requires Jellyfin version 10.11.5) ([#235](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/235))
**Bug Fixes**

- Fixed an issue where the wrong user would be pre-selected when creating and cloning collections. ([#233](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/233))
**Other Changes**

- You can now close the notification popups ([#234](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/234))

## v10.11.12.1

*2025-12-07 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.12.1)*

**New Features**

- The LastPlayedDate rule now works with Collections as well. ([#225](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/225))
**Bug Fixes**

- Fixed an issue where audio playlists couldn't be sorted by name (track title). ([#227](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/227))
- Fixed an issue where playback status wasn't working properly in somecases. ([#228](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/228))

## v10.11.12.0

*2025-12-01 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.12.0)*

**New Features**

- Refactored playback status rule field. You can now also choose 'In Progress' for media items. ([#222](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/222))
**Bug Fixes**

- Fixed an issue where collections containing series would not get automatically updated. ([#220](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/220))
- Fixed an issue where Jellyfin playlists wouldn't get deleted when disabled or converted into Collections. ([#223](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/223))
**Other Changes**

- Poster images in collections with episodes are now fetched from the parent series. ([#221](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/221))

## v10.11.11.0

*2025-11-28 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.11.0)*

**New Features**

- Added support for multiple playlist users. ([#214](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/214))
- Added support for choosing only Audio Languages marked as default ([#215](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/215))
**Bug Fixes**

- Fixed issue where user names could show up twice in the list filters. ([#211](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/211))
- Fixed an issue where lists wouldn't update automatically when a media item was marked as unwatched. ([#217](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/217))
**Other Changes**

- Added a multi-select dropdown for media types. ([#216](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/216))

## v10.11.10.101-rc

*2025-11-25 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.10.101-rc)*

**Bug Fixes**

- Fixed an issue where lists wouldn't update automatically when a media item was marked as unwatched. ([#217](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/217))

## v10.11.10.100-rc

*2025-11-25 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.10.100-rc)*

**New Features**

- Added support for multiple playlist users. ([#214](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/214))
- Added support for choosing only Audio Languages marked as default ([#215](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/215))
**Bug Fixes**

- Fixed issue where user names could show up twice in the list filters. ([#211](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/211))
**Other Changes**

- Added a multi-select dropdown for media types. ([#216](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/216))

## v10.11.10.0

*2025-11-24 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.10.0)*

**New Features**

- Added the following new rule fields: Audio Codec, Video Codec, VideoProfile, Video Range, and Video Range Type ([#186](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/186))

## v10.11.9.104-rc

*2025-11-23 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.9.104-rc)*

**Bug Fixes**

- Fixed an issue where the status page wouldn't load properly in some cases ([#205](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/205))
- Fixed an issue where you could not leave Suffix Text blank. ([#207](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/207))

## v10.11.9.103-rc

*2025-11-20 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.9.103-rc)*

**New Features**

- Added weekday operator support for date fields ([#202](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/202))
- Added support for bulk refreshing lists ([#204](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/204))
**Other Changes**

- Minor UI improvements ([#203](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/203))

## v10.11.9.102-rc

*2025-11-19 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.9.102-rc)*

**Bug Fixes**

- Fixed issue where Collections would not get properly deleted ([#197](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/197))
- Fixed issues when sorting by series name and episode name ([#199](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/199))
**Other Changes**

- Implemented a new queue system along with a new cache system for increased performance when multiple lists are refreshed ([#196](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/196))
- Sorting by name and series name now sorts by SortTitle metadata first ([#198](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/198))

## v10.11.9.101-rc

*2025-11-18 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.9.101-rc)*

**New Features**

- Notification message changes ([#193](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/193))
**Other Changes**

- Fixed formatting for JellyfinPlaylistId ([#194](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/194))

## v10.11.9.100-rc

*2025-11-17 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.9.100-rc)*

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

## v10.11.2.0

*2025-11-11 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.2.0)*

**New Features**

- Added support for multiple sorting options. Added Episode and Season as sorting options. ([#175](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/175))
- Add sorting options for Last Played, Runtime, Series Name, Album Name and Artist. ([#180](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/180))
- The rule fields Studios and Genres can now search parent series for episodes. ([#181](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/181))
**Bug Fixes**

- Fixed a bug where the UI would incorrectly always show "Sunday" for weekly schedules. ([#179](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/179))
**Other Changes**

- Only show sort options related to the media type(s) selected. ([#177](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/177))
- Improved performance by using parallel processing for more fields. ([#183](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/183))

## v10.11.1.101-rc

*2025-11-10 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.1.101-rc)*

**Other Changes**

- Improved performance by using parallel processing for more fields. ([#183](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/183))

## v10.11.1.100-rc

*2025-11-09 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.1.100-rc)*

**New Features**

- Added support for multiple sorting options. Added Episode and Season as sorting options. ([#175](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/175))
- Add sorting options for Last Played, Runtime, Series Name, Album Name and Artist. ([#180](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/180))
- The rule fields Studios and Genres can now search parent series for episodes. ([#181](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/181))
**Bug Fixes**

- Fixed a bug where the UI would incorrectly always show "Sunday" for weekly schedules. ([#179](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/179))
**Other Changes**

- Only show sort options related to the media type(s) selected. ([#177](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/177))

## v10.11.1.0

*2025-11-06 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.1.0)*

**New Features**

- You can now add multiple schedules. ([#155](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/155))
- Added rule field support for: Actors, Directors, Producers, Writers, Guest Stars ([#157](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/157))
- You can now choose which metadata properties to include when selecting the 'Similar To' rule field. ([#158](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/158))
- Added support for the following audio metadata: Bitrate, Sample Rate, Bit Depth, Codec, Channels. ([#160](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/160))
- Added conditional rule field visibility logic, only displaying the rule fields related to the chosen media type. ([#167](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/167))
- Added support for all Jellyfin people fields. ([#168](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/168))
**Bug Fixes**

- Fixed issue where equals/not equals operators were missing for some rule fields. ([#148](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/148))
- Fixed issue with the Collections rule field. ([#174](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/174))
**Other Changes**

- Simplified and optimized the AutoRefresh setting logic ([#161](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/161))

## v10.11.0.102-rc

*2025-11-03 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.0.102-rc)*

**New Features**

- Added conditional rule field visibility logic, only displaying the rule fields related to the chosen media type. ([#167](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/167))
- Added support for all Jellyfin people fields. ([#168](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/168))

## v10.11.0.101-rc

*2025-10-29 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.0.101-rc)*

_No release notes were recorded for this version._

## v10.11.0.100-rc

*2025-10-28 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.0.100-rc)*

**New Features**

- You can now add multiple schedules. ([#155](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/155))
- Added rule field support for: Actors, Directors, Producers, Writers, Guest Stars ([#157](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/157))
- You can now choose which metadata properties to include when selecting the 'Similar To' rule field. ([#158](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/158))
- Added support for the following audio metadata: Bitrate, Sample Rate, Bit Depth, Codec, Channels. ([#160](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/160))
**Bug Fixes**

- Fixed issue where equals/not equals operators were missing for some rule fields. ([#148](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/148))
**Other Changes**

- Simplified and optimized the AutoRefresh setting logic ([#161](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/161))

## v10.11.0.1

*2025-10-20 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.0.1)*

**New Features**

- Added option to update playlists automatically based on library updates. ([#98](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/98))
- You can now set individual playlist refresh schedules ([#103](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/103))
- You can now sort by play count. ([#106](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/106))
- Major improvements to the 'Manage Playlists' page (filter, bulk select, clone playlists, etc). ([#113](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/113))
- Added "hours" option to the Newer Than/Older than operators and allow zero as value. ([#123](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/123))
- Added option to ignore leading article 'The' when sorting by name ([#127](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/127))
- You can now choose to include tags from parent Series for episodes. ([#130](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/130))
- Sorting by name now takes into account if the name begins with numbers and sorts them properly. ([#133](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/133))
- Added playlist statistics ([#134](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/134))
- Added a link to the Jellyfin playlist in the properties table. ([#137](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/137))
- Use parallel scans under specific conditions, added "Parallel Concurrency Limit" setting. ([#140](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/140))
- You can now sort by track number ([#143](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/143))
- Added "Similar To" rule field which finds items similar to a reference item based on shared metadata. ([#146](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/146))
**Bug Fixes**

- Fixed issue where the scheduled time for refresh would show wrong in the UI ([#111](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/111))
- Some minor UI fixes and tweaks ([#115](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/115))
**Other Changes**

- Separated "Refresh All Playlists" logic from Jellyfin tasks. ([#105](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/105))
- Minor sorting UI changes. ([#107](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/107))
- Styling adjustments ([#108](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/108))
- Various minor fixes and tweaks. ([#117](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/117))
- Removed 'Series' media type due to Jellyfin limitations ([#120](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/120))
- Implement time buffer checks for playlist refresh scheduling ([#125](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/125))

## v10.11.0.0-rc8

*2025-10-10 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.11.0.0-rc8)*

**New Features**

- Added a link to the Jellyfin playlist in the properties table. ([#137](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/137))
- Use parallel scans under specific conditions, added "Parallel Concurrency Limit" setting. ([#140](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/140))

## v10.10.10.0-rc8

*2025-10-06 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc8)*

**New Features**

- Sorting by name now takes into account if the name begins with numbers and sorts them properly. ([#133](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/133))
- Added playlist statistics ([#134](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/134))

## v10.10.10.0-rc7

*2025-10-02 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc7)*

**New Features**

- Added option to ignore leading article 'The' when sorting by name ([#127](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/127))
- You can now choose to include tags from parent Series for episodes. ([#130](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/130))

## v10.10.10.0-rc6

*2025-09-30 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc6)*

_No release notes were recorded for this version._

## v10.10.10.0-rc5

*2025-09-29 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc5)*

**Other Changes**

- Implement time buffer checks for playlist refresh scheduling ([#125](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/125))

## v10.10.10.0-rc4

*2025-09-28 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc4)*

**New Features**

- Added "hours" option to the Newer Than/Older than operators and allow zero as value. ([#123](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/123))
**Other Changes**

- Removed 'Series' media type due to Jellyfin limitations ([#120](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/120))

## v10.10.10.0-rc3

*2025-09-10 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc3)*

**Bug Fixes**

- Some minor UI fixes and tweaks ([#115](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/115))
**Other Changes**

- Various minor fixes and tweaks. ([#117](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/117))

## v10.10.10.0-rc2

*2025-09-08 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc2)*

**New Features**

- Major improvements to the 'Manage Playlists' page (filter, bulk select, clone playlists, etc). ([#113](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/113))
**Bug Fixes**

- Fixed issue where the scheduled time for refresh would show wrong in the UI ([#111](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/111))

## v10.10.10.0-rc1

*2025-09-06 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.10.0-rc1)*

**New Features**

- Added option to update playlists automatically based on library updates. ([#98](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/98))
- You can now set individual playlist refresh schedules ([#103](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/103))
- You can now sort by play count. ([#106](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/106))
**Other Changes**

- Separated "Refresh All Playlists" logic from Jellyfin tasks. ([#105](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/105))
- Minor sorting UI changes. ([#107](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/107))
- Styling adjustments ([#108](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/108))

## v10.10.9.1

*2025-09-02 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.9.1)*

**Bug Fixes**

- Fixed rare race condition that could affect the media types of smart playlists ([#100](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/100))

## v10.10.9.0

*2025-09-01 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.9.0)*

**New Features**

- Added support for media types Home Videos and Photos. ([#94](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/94))
- Added support for Books and Audio Books. ([#96](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/96))
**Other Changes**

- Performance optimizations ([#97](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/97))

## v10.10.8.1

*2025-08-25 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.8.1)*

**Bug Fixes**

- Fixed issue with user matching on playlist import ([#90](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/90))

## v10.10.8.0

*2025-08-25 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.8.0)*

**New Features**

- Added new Export/Import functionality in Settings. ([#88](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/88))
- Some styling changes to mimic Jellyfin layout. ([#89](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/89))

## v10.10.7.0

*2025-08-24 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.7.0)*

**New Features**

- Added new Resolution rule field ([#83](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/83))
- Added new Framerate rule field ([#85](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/85))
- Added Series Name rule field ([#86](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/86))

## v10.10.6.1

*2025-08-21 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.6.1)*

**New Features**

- You can now include individual episodes from series within the Collections rule field ([#74](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/74))
- Improvements to release date sorting ([#77](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/77))
**Other Changes**

- Code optimizations ([#78](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/78))

## v10.10.6.1-rc4

*2025-08-20 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.6.1-rc4)*

**New Features**

- Improvements to release date sorting ([#77](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/77))
**Other Changes**

- Code optimizations ([#78](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/78))

## v10.10.6.1-rc3

*2025-08-19 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.6.1-rc3)*

_No release notes were recorded for this version._

## v10.10.6.1-rc2

*2025-08-19 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.6.1-rc2)*

_No release notes were recorded for this version._

## v10.10.6.1-rc

*2025-08-18 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.6.1-rc)*

**New Features**

- You can now include individual episodes from series within the Collections rule field ([#74](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/74))

## v10.10.6.0

*2025-08-16 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.6.0)*

**New Features**

- Added new IsIn/IsNotIn operator for matching multiple words ([#68](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/68))
- Added new "Collections" rule field that searches for items belonging to a specific collection. ([#72](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/72))

## v10.10.5.3

*2025-08-09 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.5.3)*

**Bug Fixes**

- Fixed caching issue which could cause wrong contents showing up in playlists ([#64](https://github.com/jyourstone/jellyfin-smartlists-plugin/pull/64))

## v10.10.5.2

*2025-08-07 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.5.2)*

- PlaylistMediaType is now set to Video when the playlist only contains video files.

## v10.10.5.1

*2025-08-05 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.5.1)*

- Added support for Music Videos, thank you guluarte!
- Prevent search while playlists are loading
- Code optimizations

## v10.10.5.0

*2025-07-31 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.5.0)*

Added new rule field properties:
- 'Overview': For the media item description
- 'Last Played': When the media item was last played
- 'Next Unwatched': The next unwatched episode

Other changes:
- Changed the rule field categories to make a bit more sense
- Media type pre-filtering now uses API to increase performance
- Restructured code to make it easier to add new features

## v10.10.4.1

*2025-07-27 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.4.1)*

- You can now set a max play time for playlists
- Split scheduled tasks into two, one for video (hourly) and one for audio (daily)
- Fixed issue with config page causing styling errors

## v10.10.4.0

*2025-07-26 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.4.0)*

- You can now set your own suffix/prefix or even remove them completely
- Playlists are now connected with ID instead of name

NOTE: This change comes with a lot of backend changes. Make sure your playlists have refreshed once before changing name and/or owner to avoid duplicate Jellyfin playlists

## v10.10.3.3

*2025-07-25 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.3.3)*

New features:
- You can now choose an item limit when playlists are generated
- Added a 'Random' sort option for playlists

Fix:
- Now works properly with all Jellyfin 10.10.x versions

## v10.10.3.2

*2025-07-19 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.3.2)*

- Fix: Artists fields now work properly

## v10.10.3.1

*2025-07-18 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.3.1)*

Added support for Artists and Album Artists

## v10.10.3.0

*2025-07-15 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.3.0)*

New features:
- You can now target specific users for playback fields, such as IsPlayed.
- Added support for relative date comparisons ('Newer Than' and 'Older Than')
- Added 'After' and 'Before' operators for date fields (removed 'LessThan' and 'GreaterThan')
- Added support for Release Date

Fixes:
- Fixed bug for config page event listeners when using back/forward in browser.
- Date fields now work properly

## v10.10.2.3

*2025-07-12 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.2.3)*

- Added Series media type and renamed TV Shows to Episodes for clarity
- You can now search for users as well
- More styling changes to better match Jellyfin

## v10.10.2.2

*2025-07-10 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.2.2)*

- You can now search for playlists under Manage Playlists
- Styling changes to better match Jellyfin
- Various other tweaks and fixes

## v10.10.2.1

*2025-07-09 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.2.1)*

- Fix: Playlist covers are now regenerated properly (metadata refresh) when editing and refreshing playlists.
- Changed refresh task to trigger every hour instead instead of every 30 minutes.
- Various other enhancements and stability improvements.

## v10.10.2.0

*2025-07-06 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v10.10.2.0)*

New features and optimizations:

- You can now choose and mix AND/OR logic when creating rules
- Option to select media type first to go well in hand with the new rules logic
- Added flexible deletion options (config only vs config + playlist)
- Added regex validation
- Added option to enable/disable playlists
- Various performance optimizations

Also switched to Jellyfin version semantics.

## v2.2.0

*2025-07-04 · **RC** · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v2.2.0)*

Jellyfin 10.11 support and new features:

- You can now choose and mix AND/OR logic when creating rules
- Option to select media type first to go well in hand with the new rules logic
- Added flexible deletion options (config only vs config + playlist)
- Works with Jellyfin 10.11.0.0-rc2

## v2.1.2

*2025-07-02 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v2.1.2)*

Fixes:
- Prevent duplicate names when renaming smart playlists
- Delete old playlist when renaming a smart playlist

Optimizations:
- When creating and editing smart playlists, only refresh that specific list.
- Optimized and cleaned up code

## v2.1.1

*2025-07-02 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v2.1.1)*

Fix: Multiple playlists could get deleted after cancellation

## v2.1.0

*2025-07-01 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v2.1.0)*

New features:
- Edit existing playlists directly in the UI!
- Choose playlist owner from dropdown of all Jellyfin users
- User ID Migration: Automatic migration from usernames to User IDs for reliability

Plus some other fixes, enhancements and performance tweaks.

## v2.0.8

*2025-06-30 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v2.0.8)*

Changed release pipeline version history back to tag messages

## v2.0.7

*2025-06-30 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v2.0.7)*

Added support for People metadata.

## v2.0.6

*2025-06-29 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v2.0.6)*

Refactor item type rule evaluation to check all rules within each rule set, improving filtering logic for pre-filtered items.

## v2.0.5

*2025-06-29 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v2.0.5)*

Fixes:
- Playlist images are now populating again.
- Proper fix for null exception.

## v2.0.4

*2025-06-29 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v2.0.4)*

Fixes + audio language support (#4)

## v2.0.3

*2025-06-28 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v2.0.3)*

- Merge pull request #1 from jyourstone/dev
- Added contribution guidelines

## v2.0.2

*2025-06-27 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v2.0.2)*

- Added support for field rules: Is Played, Is Favorite, Play Count, Runtime, Parental Rating

## v2.0.1

*2025-06-27 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v2.0.1)*

- Added support for "Tags" field in rules.

## v2.0.0

*2025-06-27 · [release notes](https://github.com/jyourstone/jellyfin-smartlists-plugin/releases/tag/v2.0.0)*

_No release notes were recorded for this version._
