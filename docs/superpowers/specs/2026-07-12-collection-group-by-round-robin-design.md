# Design: Collection Group By + Within-Group Order for Round Robin

**Date:** 2026-07-12
**Status:** Approved (design discussion 2026-07-12)
**Origin:** Follow-up request on Least Recently Watched Round Robin — "If a show/s is part of a collection, can you view those shows as 1 group for that collection, not as the individual shows, and sort by airdate within that group rather than by episode number? This would keep shows' timelines and crossovers in order." (NCIS/CSI/One Chicago-style franchise collections with crossover episodes.)

## Behavior

Two additions to the round-robin sort family:

### 1. Group By: Collection

A new Group By option, **Collection** (field value `"Collections"`), for **Episode and Movie** playlists. Each Jellyfin collection (BoxSet — manual, TMDB-auto-created, or a SmartLists smart collection; they are the same item type) becomes ONE rotation group:

- **Episodes** resolve membership through their parent series: TV collections contain Series items, so an episode belongs to the collection that contains its series (`episode.SeriesId` in the collection's member set).
- **Movies** resolve membership directly (`movie.Id` in the member set).
- **Items in no collection** fall back to today's grouping: episodes group by Series Name, everything else by its own name. Standalone shows rotate as themselves alongside franchise groups — mixed lists work naturally.
- **Multiple collections**: the alphabetically-first collection name wins (deterministic; consistent with the existing "first value" convention for Genres/Studios).
- **Nested collections**: direct members only (matches the Collections rule's default search depth). Deepening is a later knob if requested.

Works with all four round-robin variants. With **Least Recently Watched Round Robin** the whole franchise carries one recency — watch any NCIS episode and the entire NCIS-verse group moves to the back of the rotation.

### 2. Order Within Group

A new per-sort dropdown, **Order Within Group**, for round-robin sorts (hidden for Shuffled Round Robin, which shuffles within groups):

| Value | Meaning |
| --- | --- |
| `Natural` (default) | Current behavior: season/episode for episodes, disc/track for audio, name otherwise. Absent/null in old configs → byte-identical behavior. |
| `AirDate` | `PremiereDate` truncated to day, tie-broken episodes-first → season → episode (same semantics as the existing Release Date sort). Missing dates sort first. |

`AirDate` within a Collection group plays a franchise in airing order — crossover events spanning multiple shows interleave correctly, and a spinoff's episodes only start appearing once the timeline reaches its premiere (this satisfies the requester's "air-date proximity" idea with no extra logic). Available for every Group By value, not just Collection (e.g. Series Name + AirDate puts specials into the timeline).

## Data model

`Core/Models/SortOption.cs` gains one property (same pattern as `GroupByField`):

```csharp
/// <summary>
/// How items are ordered inside each Round Robin group.
/// "Natural" (default): season/episode, disc/track, or name.
/// "AirDate": premiere date (day precision), tie-broken by season/episode.
/// </summary>
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? WithinGroupOrder { get; set; }
```

No validation added — consistent with `SortBy`/`GroupByField`, which are unvalidated and degrade gracefully (unknown values behave as `Natural`).

## Backend

### Order classes (`Core/Orders/RoundRobinOrder.cs`)

- `RoundRobinBase` gains two injected/assigned members:
  - `public Dictionary<Guid, string>? CollectionGroupKeys { get; set; }` — itemId → collection name, set by SmartList (same injection pattern as `RoundRobinLeastRecentlyWatchedOrder.GroupRecency`). Only populated when `GroupByField == "Collections"`.
  - `public bool OrderWithinGroupsByAirDate { get; set; }` — set from the DTO in `InitializeFromDto`.
- `ExtractGroupKey` (currently `ExtractGroupKey(BaseItem, string?)`, RoundRobinOrder.cs ~168) gains a `Dictionary<Guid, string>? collectionGroupKeys` parameter and a `"Collections"` case: map hit → collection name; miss → existing SeriesName fallback logic (episode → `SeriesName`, else `Name`).
- `BuildInterleavedPositions` (~93) gains the map parameter and an `airDateWithinGroups` flag alongside the existing `shuffleWithinGroups`; group-internal sort picks `CompareWithinGroupByAirDate` when the flag is set (and shuffle still wins for the Shuffled variant).
- New `internal static int CompareWithinGroupByAirDate(BaseItem a, BaseItem b)`: compare `OrderUtilities.GetReleaseDate(x).Date`, then episodes-before-non-episodes, then `GetSeasonNumber`, then `GetEpisodeNumber` — mirroring `ReleaseDateOrder` so same-day crossover parts keep airing order. Missing dates are `DateTime.MinValue` (sort first), same sentinel as Release Date sort.
- `PreComputePositions` passes the instance state (`CollectionGroupKeys`, `OrderWithinGroupsByAirDate`) into `BuildInterleavedPositions`.
- `RoundRobinLeastRecentlyWatchedOrder.BuildGroupRecency` gains the same map parameter so recency keys agree with grouping keys (franchise = one recency).

### SmartList (`Core/SmartList.cs`)

1. **Map construction.** New private static helper `BuildCollectionGroupKeyMap(IReadOnlyList<BaseItem> pool, User user, ILibraryManager libraryManager, RefreshQueueService.RefreshCache? refreshCache, ILogger? logger)`:
   - Enumerate user-visible BoxSets (`InternalItemsQuery(user) { IncludeItemTypes = [BaseItemKind.BoxSet], Recursive = true }`), reusing `refreshCache.AllCollections` when populated (same cache the Collections rule extraction fills).
   - For each BoxSet, get direct members via the existing child-enumeration mechanism (`GetCollectionChildren` already in SmartList.cs ~1619; reuse rather than duplicate a fourth copy of the reflection pattern), reusing `refreshCache.CollectionDirectChildren` when present.
   - Build member-id → collection-name with **alphabetically-first-wins** (process collections in name order via the natural comparer; first assignment wins).
   - Map pool items: movie/other → own Id lookup; episode → `SeriesId` lookup. Only items that resolve to a collection enter the map (misses fall back inside `ExtractGroupKey`).
2. **Injection block** (extend the existing GroupRecency injection at ~760-769, which already runs on the unfiltered pool before filtering):
   - If any order is `RoundRobinBase` with `GroupByField == "Collections"`: build the map once, assign to those orders' `CollectionGroupKeys`.
   - Build the map **before** the LRW recency loop and pass it to `BuildGroupRecency` so recency is keyed by collection where applicable.
3. **`InitializeFromDto`** (~126-129, the existing `is RoundRobinBase rr` branch): additionally `rr.OrderWithinGroupsByAirDate = string.Equals(so.WithinGroupOrder, "AirDate", StringComparison.OrdinalIgnoreCase);`.
4. No changes to `PrepareRoundRobinPositions` or its three call sites — the map and flag are instance state, set once per `FilterPlaylistItems` run before any call site executes.

### Per-user and caching notes

- The map is built per `FilterPlaylistItems` run (per user for AllUsers playlists), user-scoped through `InternalItemsQuery(user)`/`GetChildren(user, true)` — same owner-visibility semantics as the Collections rule field.
- `refreshCache.CollectionDirectChildren` is not user-keyed (pre-existing single-owner assumption shared with the rule engine); this feature reuses it as-is and inherits that behavior.
- Collection membership changes (adding a series to a BoxSet) trigger refreshes through existing library-change handling; no AutoRefreshService changes.

## UI (`Configuration/config-core.js`, `config-sorts.js`)

- `ROUND_ROBIN_GROUP_FIELDS` (config-core.js ~113-119): add `{ value: 'Collections', label: 'Collection', mediaTypes: ['Episode', 'Movie'] }` — `getFilteredRoundRobinFields` handles media-type visibility automatically.
- New **Order Within Group** select in the sort box (config-sorts.js `createSortBox`, next to the Group By select): options `Natural` ("Season/Episode (default)") and `AirDate` ("Air Date"); visible when `isRoundRobinSort(sortBy) && sortBy !== 'Shuffled Round Robin'`; plain `populateSelectElement` like Group By (not searchable).
- Save path (`collectSortsFromForm` ~463-502): when round-robin and value is `AirDate`, set `sortEntry.WithinGroupOrder = 'AirDate'` (omit for Natural — keeps JSON clean and old-config-compatible). Load path: `sortData.WithinGroupOrder` → select value, mirroring `GroupByField` handling.
- Visibility sync in `syncSortOrderUI` (~126-145) alongside the Group By container toggle; re-filter on media-type change is not needed (options are static).
- Both HTML pages: no changes (sort boxes are fully JS-built in `#sorts-container`).
- `formatSortDisplay` shows neither `GroupByField` nor `WithinGroupOrder` today — unchanged (pre-existing display gap, out of scope).

## Docs

- `sorting-and-limits.md`: add `Collection` row to the "Available Group By fields" table (~126-134) — "Episode or Movie media type"; document multi-collection alphabetical-first and the series-based membership for episodes; add an "Order Within Group" subsection to the Round Robin section (applies to all variants except Shuffled); note the LRW composition (franchise recency).
- `common-use-cases.md`: new recipe **"Franchise TV Channel (crossovers in air-date order)"** — Group By Collection + Order Within Group Air Date + Playback Status = Unplayed + Least Recently Watched Round Robin + Auto Refresh On All Changes; mention the requester's NCIS/One Chicago scenario shape.

## Edge cases

- **Item in no collection** → falls back to Series Name / own name grouping (single-show groups rotate as themselves).
- **Missing PremiereDate with AirDate order** → sorts first within the group (Release Date sort convention).
- **Empty collections / collections with no pool items** → produce no groups (map only covers pool items).
- **Group By Collection on unsupported media types** (saved via API): map resolves by item Id; non-members fall back by name — graceful, undocumented.
- **`WithinGroupOrder` on Shuffled Round Robin** (via API): shuffle wins (flag ignored), matching the UI hiding the knob.
- **Collections rule + Collection grouping together**: independent — rules filter, grouping groups; no interaction.

## Out of scope

- Air-date **proximity gating** between shows (requester's second idea) — satisfied structurally by AirDate ordering within a collection group; no separate mechanism.
- Nested-collection depth knob for grouping (direct members only).
- Showing GroupByField/WithinGroupOrder in the list-properties sort display (pre-existing gap for GroupByField).
- Validation of the new DTO value (consistent with existing sort fields).

## Touched files

| Area | Files |
| --- | --- |
| Backend | `Core/Orders/RoundRobinOrder.cs`, `Core/SmartList.cs`, `Core/Models/SortOption.cs` |
| UI | `Configuration/config-core.js`, `Configuration/config-sorts.js` |
| Docs | `docs/content/user-guide/sorting-and-limits.md`, `docs/content/examples/common-use-cases.md` |

## Verification

No test suite; against local Jellyfin (build both targets, warnings are errors):

1. In the dev library, create a Jellyfin collection containing two series whose episode air dates interleave (e.g. Show Alpha + Show Beta; set PremiereDates via metadata edit if needed).
2. Playlist: Episode media type, Round Robin Ascending, Group By = Collection, Order Within Group = Air Date. Expect: the two collection shows occupy ONE rotation slot with episodes in air-date order across both shows; non-collection shows rotate as individual groups alphabetically.
3. Switch Order Within Group back to default (Natural): the collection still forms ONE group, but episodes inside it sort by season/episode number across both shows (Alpha S1E1 and Beta S1E1 adjacent) — verify grouping is unchanged and only the internal order differs.
4. Switch sort to Least Recently Watched Round Robin: watch/mark an episode of Show Alpha → whole collection group moves to the back on refresh; standalone shows unaffected.
5. Movie playlist: Group By = Collection with a TMDB-style movie collection → franchise movies form one group, Air Date = release order.
6. Regression: existing round-robin playlists (all four variants, Series Name/Album/Artist/Genres/Studios groupings) refresh byte-identical; playlist without WithinGroupOrder in JSON behaves exactly as before.
7. UI: Group By shows "Collection" only for Episode/Movie media types; Order Within Group hidden for Shuffled Round Robin and non-round-robin sorts; values round-trip through save/edit.
