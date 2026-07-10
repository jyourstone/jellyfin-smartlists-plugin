# Design: Least Recently Watched Round Robin

**Date:** 2026-07-10
**Status:** Draft — awaiting user review
**Origin:** User feedback on Round Robin — "if I watch 2 episodes today, they are Show A and B. Tomorrow ... it will be Shows A and B again and C and D will never get watched. If I open my list and see Show A, B, C, D, and I watch 2 episodes, I would like to open my list tomorrow and see Show C, D, A, B."

## Behavior

A new sort option, **"Least Recently Watched Round Robin"**, extends the round-robin family. Groups (shows) are ordered by how recently the user watched anything in them — least recently watched first, never-watched first of all — then interleaved round-robin with natural episode order within each group.

Watch an episode of Show A today → on the next refresh Show A's slot moves to the back of the rotation. The rotation "continues where you left off" without any persisted rotation state: it is derived entirely from Jellyfin's per-user `LastPlayedDate` data.

| Variant | Group order | Within-group order |
| --- | --- | --- |
| Round Robin Ascending | A→Z | Natural (season/episode, disc/track, name) |
| Round Robin Descending | Z→A | Natural |
| Random Round Robin | Shuffled | Natural |
| Shuffled Round Robin | Shuffled | Shuffled |
| **Least Recently Watched Round Robin (new)** | **Oldest watch date → newest; never-watched first** | **Natural** |

Directionless (no Asc/Desc variants — "most recently watched first" has no use case). Deterministic: same watch history → same order.

### Group recency definition

For each group (e.g. each series), recency = the **maximum per-user `LastPlayedDate` across all of the group's items in the playlist's full media pool** — *before* rule filtering. This is load-bearing: the intended companion rule is `Playback Status is Unwatched`, which removes watched episodes from the result list. If recency were computed from the filtered results, every group would look never-watched and the feature would degrade to alphabetical. Computing from the unfiltered pool means the watched-yesterday episode still stamps its show as recently watched even though it no longer appears in the playlist.

- Never-watched group → `DateTime.MinValue` → sorts first. Ties (including all-never-watched) break alphabetically by group key (natural comparer) — degrades gracefully to Round Robin Ascending.
- Partially watched episodes count: Jellyfin sets `LastPlayedDate` on playback, so starting an episode moves its show back. Desired ("I watched some of A today").
- Works for any Group By field (Series Name, Album, Artist, Genres, Studios): recency is per group key, aggregated over pool items, not series-specific.
- Container items (Series/Season/MusicAlbum media types): recency from the item's own user data, upgraded to the aggregate-over-children value when the refresh cache has children (same mechanism as `LastPlayedOrderBase.GetAggregateLastPlayedDate` — reuse it, promote from `private` if needed).

## Implementation

### Order class (`Core/Orders/RoundRobinOrder.cs`)

```csharp
public class RoundRobinLeastRecentlyWatchedOrder : RoundRobinBase
{
    public override string Name => "Least Recently Watched Round Robin";

    /// <summary>Group key → most recent LastPlayedDate. Set by SmartList before PreComputePositions.</summary>
    public Dictionary<string, DateTime> GroupRecency { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    protected override List<string> OrderGroupKeys(IEnumerable<string> keys)
    {
        return keys
            .OrderBy(k => GroupRecency.TryGetValue(k, out var d) ? d : DateTime.MinValue)
            .ThenBy(k => k, OrderUtilities.SharedNaturalComparer)
            .ToList();
    }
}
```

No change to the `OrderGroupKeys` hook signature — the recency map is instance state, mirroring how `GroupByField` is already injected by SmartList. `ItemPositions`/`GetSortKey` mechanics unchanged, so multi-sort cascades, early-return paths, per-group limits, and MaxItems compose exactly as for the other round-robin variants.

### SmartList (`Core/SmartList.cs`)

1. **Recency map construction.** `PrepareRoundRobinPositions` gains the context to build the map: the full unfiltered per-user pool, `User`, `IUserDataManager?`, and `RefreshQueueService.RefreshCache?`. For each `RoundRobinLeastRecentlyWatchedOrder` in `Orders`: one pass over the pool, `ExtractGroupKey(item, GroupByField)` → track max `LastPlayedDate` (via `UserDataCacheHelper.GetCachedUserData` when a refresh cache is present, falling back to `userDataManager.GetUserData`), assign the resulting map to `GroupRecency`, then `PreComputePositions` as today. When `userDataManager` or `user` is null, log a warning and leave the map empty (alphabetical fallback) — same convention as `LastPlayedOrderBase`.
2. **All three call sites** of `PrepareRoundRobinPositions` (main results, expanded results, per-rule-block group items) pass the full pool available in their scope plus the user context already flowing through `FilterPlaylistItems`. Verify at implementation time that the per-rule-block path has the pre-filter pool in scope; if only the block-filtered subset is available there, pass the widest set the scope has and note it (per-block lists are an edge case for this sort).
3. **Registry**: add `{ "Least Recently Watched Round Robin", () => new RoundRobinLeastRecentlyWatchedOrder() }` to the order map, add the name to `DirectionlessOrders`. The existing generic `is RoundRobinBase` branch in `InitializeFromDto` already assigns `GroupByField` — no special-casing.
4. `IsDescendingOrder`: not descending (no change needed if the default is false — verify).

### Auto-refresh (`Services/Shared/AutoRefreshService.cs`)

Without this, the rotation only advances on library changes or schedule — watching an episode wouldn't reorder anything until some unrelated refresh. Playlists using this sort must be treated as playback-relevant:

- In `AddPlaylistToRuleCache`: when any sort option's `SortBy` is "Least Recently Watched Round Robin", register the playlist under the same `"{MediaType}+PlaybackStatus"`-style cache keys as if it had a `PlaybackStatus` rule (for each of the playlist's media types).
- In the `IsItemRelevantToPlaylist` fallback: also return true for playback-data changes when the playlist uses this sort.
- Docs note: per-play rotation advance requires Auto-refresh **On all changes** (playback events route through `OnUserDataSaved` → `HandleLibraryChangeAsync`, which filters to OnAllChanges playlists).

### UI (`Configuration/config-core.js`, `config-sorts.js`)

- Add `'Least Recently Watched Round Robin'` to `SORT_OPTIONS` (next to the other round-robin entries), `ROUND_ROBIN_SORTS` (Group By dropdown visibility + GroupByField save path), and `ORDERLESS_SORTS` (hide the Asc/Desc dropdown). The centralized predicates from the review-fixes pass (`isRoundRobinSort`/`isOrderlessSort`) mean no per-site condition chains need touching.
- `config-formatters.js` display formatting flows through the same predicates — verify the name renders in the properties panel sort row.
- Both HTML pages: nothing — sort options are JS-driven.

### Validation / DTO

None. The sort is selected by name like every other sort; no new DTO fields, no InputValidator changes.

## Recommended companion setup (docs)

The sort answers "which show comes first"; pairing it with these makes the full "continue watching my rotation" experience:

- Rule: `Playback Status` → `is` → `Unwatched` (watched episodes drop off; natural within-group order then surfaces each show's next unwatched episode).
- Auto-refresh: **On all changes** (rotation advances right after watching).
- Group By: `Series Name` for TV.

Document as an example on the sorting docs page (and cross-link from the round-robin section).

## Edge cases

- **No GroupByField**: existing warning + original order (unchanged base behavior).
- **All groups never-watched**: alphabetical rotation — identical to Round Robin Ascending until the first watch.
- **User watches something mid-refresh-cycle without auto-refresh**: rotation stale until next refresh; documented, not defended in code.
- **AllUsers playlists**: `ProcessPlaylist` runs per user; each user gets their own rotation from their own watch data. Collections use the reference user, consistent with other user-data sorts.
- **Bumpers**: unrelated — weave runs after sorting; composes with this sort like any other.

## Out of scope

- Persisted rotation state (pointer files, "resume position") — watch-data derivation covers the use case with zero storage.
- A "Most Recently Watched" descending variant.
- Changing existing round-robin variants.

## Touched files

| Area | Files |
| --- | --- |
| Backend | `Core/Orders/RoundRobinOrder.cs`, `Core/SmartList.cs`, `Services/Shared/AutoRefreshService.cs` |
| UI | `Configuration/config-core.js` (constants), `config-sorts.js`/`config-formatters.js` (verify predicates only) |
| Docs | `/docs/content/` sorting page + example |

## Verification

No test suite; verify against local Jellyfin (`cd dev && ./build-local.sh`, both target frameworks build clean):

1. Playlist: Episode media type, 3+ series, sort = Least Recently Watched Round Robin, Group By = Series Name, rule `Playback Status is Unwatched`, auto-refresh On all changes.
2. Initial refresh: never-watched shows rotate alphabetically (A, B, C, A, B, C, ...).
3. Mark an episode of Show A watched (via UI or API) → playlist auto-refreshes → rotation now B, C, A; Show A's watched episode gone (unwatched rule), its next episode appears in Show A's slots.
4. Watch Show B → rotation C, A, B.
5. Remove the unwatched rule, refresh: watched episodes reappear in natural order within their groups; rotation still least-recently-watched first.
6. Regression: Round Robin Ascending/Descending/Random/Shuffled playlists refresh unchanged; a playlist without this sort does not start refreshing on playback events (cache keys scoped to LRW playlists only).
