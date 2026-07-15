# Mid-Block Rotation Hold for Least Recently Watched Round Robin — Design

**Date:** 2026-07-15
**Status:** Approved

## Summary

With Least Recently Watched Round Robin (LRW) grouped by **Collections** with
**Air Date** order, watching any episode gives the whole collection group a
fresh recency and rotates it to the back — even when the user is in the middle
of a crossover air block. Watch part 1 of a crossover night and parts 2–3 move
to the bottom of the playlist.

This feature adds a **mid-block hold**: a collection group whose watch position
is inside an unfinished air block stays at the front of the rotation until the
block is finished. No new configuration — the existing Air Window
(`AirBlockWindowDays`, default 3, clamp 0–30) defines "inside a block".

## Rule

Active only when ALL hold (the same gate as air blocks, plus LRW):

- The order is `RoundRobinLeastRecentlyWatchedOrder`
- `GroupByField == "Collections"`
- `OrderWithinGroupsByAirDate` is true

For each group, computed from the **unfiltered** media pool (same pool the
recency map already uses):

1. **Anchor**: the group's most recently played item (max per-user
   `LastPlayedDate` from the unfiltered pool, collection members only), ties
   broken by latest air date, then by item id, so bulk-marked histories
   resolve deterministically. No dated watch history → normal rotation.
2. **Topology**: union of the playlist's FILTERED items in the group and the
   user's watched items (watched = Played flag set or a LastPlayedDate —
   watched items anchor a block even when a "Playback Status" rule hides
   them), sorted and chunked with the interleave's own pipeline
   (`CompareWithinGroupByAirDate` + `ChunkIntoAirBlocks`, shared
   `MaxAirBlockWindowDays` clamp). Locate the block containing the anchor.
3. **Hold** iff that block still has an item that is both **visible in the
   playlist** and **unwatched** (Played flag unset — imported watch states
   without timestamps count as watched, in-progress items count as unwatched;
   folder items like Series count as watched once they have any aggregate
   watch activity, since Jellyfin does not reliably persist the folder Played
   flag): add the group to `HeldGroups`, which `OrderGroupKeys` sorts with the
   never-watched groups at the front (alphabetical tie-break). Items excluded
   by the playlist's other rules never keep a hold alive.
4. Otherwise the group keeps its normal recency and rotates as today.

The hold runs in `PreComputePositions` (which every sort path calls with the
filtered items) and **recomputes `HeldGroups` from scratch on every call** —
intermediate passes (per-group limits sort rule-group subsets before the
global sort) never leave a stale hold behind; the final pass over the real
item set wins. `BuildGroupRecencyAndHoldState` collects the per-group watch
state from the unfiltered pool beforehand, for collection members only, and
`GroupRecency` itself is never mutated by the hold.

> **Revision history:** v1 was an air-date-distance rule — it permanently
> front-pinned any group whose air cadence fit inside the window. v2 used
> block membership over the unfiltered pool — review found rule-excluded
> unwatched block-mates (e.g. a Tag or Series rule hiding one crossover show)
> and Played-without-date imports still pinned groups forever, date-only
> watched-ness released holds early for in-progress episodes, and timestamp
> ties made the anchor nondeterministic. v3 (this design) scopes the
> unwatched check to playlist-visible items, uses the Played flag for
> watched-ness, and tie-breaks the anchor by air date.

Consequences:

- Watch part 1 of a same-night crossover → parts 2–3 stay at the top next
  refresh.
- Watch the last part of the block → the group rotates to the back as before,
  even when the next block aired within the window.
- Never-watched groups and mid-block groups share the front (held groups keep
  their recency entry but sort with the never-watched key); alphabetical
  tie-break orders them.
- Watched item with no air date, or no unwatched item with an air date →
  normal rotation (hold never triggers on missing metadata).
- Rewatching an old crossover part whose partner is unwatched pins the group
  to the front — acceptable: the rewatcher is being set up to continue.

**Known trade-offs (accepted):**

- Blocks split same-show double-headers (`{A1,B1}`, `{A2}`): finishing
  `{A1,B1}` does not hold the group for same-day `A2` — accepted in exchange
  for never front-pinning cadence-matched groups.
- The interleave chunks blocks from the filtered items only, while the hold
  reasons over filtered ∪ watched; a held cycle can therefore emit an episode
  the hold considered part of the *next* block (cosmetic — one extra episode
  in the held cycle, never a pin).
- Two simultaneously held groups sort alphabetically (held groups share the
  never-watched front cluster), not by their pre-hold recency.

## Changes

### Backend only — no DTO, no UI, no new setting

- `Core/Orders/RoundRobinOrder.cs` — `RoundRobinBase`: `MaxAirBlockWindowDays`
  const (shared clamp), `UsesAirBlocks` computed property (single gate:
  Collections + air-date order + not shuffled), `PreComputePositions` made
  virtual. `RoundRobinLeastRecentlyWatchedOrder`:
  `BuildGroupRecencyAndHoldState` (instance method replacing the static
  `BuildGroupRecency`; identical recency behavior, plus `WatchedByGroup` /
  `UnwatchedCollectionItemIds` collection for collection members when
  `UsesAirBlocks`), `ApplyMidBlockHold(filteredItems, logger)` applying the
  rule above, and a `PreComputePositions` override that runs the hold before
  computing positions; logs at Debug when a group is held.
- `Core/SmartList.cs` (~line 782) — the LRW injection becomes
  `lrwOrder.BuildGroupRecencyAndHoldState(itemsArray, user, userDataManager, refreshCache, logger);`
  (the order reads its own injected `GroupByField`/`CollectionGroupKeys`).

### Docs

- `docs/content/user-guide/sorting-and-limits.md`: amend the LRW collection
  note ("the whole franchise carries one recency — watching any member sends
  the entire collection group to the back") with the mid-block exception.
- `docs/content/examples/common-use-cases.md`: one sentence in the Franchise
  TV Channel recipe result.

## Verification

No test suite; verify against local Jellyfin (both ABIs build clean, 0
warnings). Fixture: Show Alpha + Show Beta (3 episodes each, identical air
dates 2004-07-16/23/30) in one collection; playlist rules matching them plus
2–3 non-collection shows; sort **Least Recently Watched Round Robin**,
Group By Collection, Order Within Group Air Date.

1. All unwatched → collection group at front (never watched), air-date pairs
   blocked together.
2. Mark Alpha S1E1 played (`POST /UserPlayedItems/{id}?userId=`) → refresh →
   group STILL at front (Beta S1E1, same air date, unwatched); Beta S1E1 is
   the group's next episode.
3. Mark Beta S1E1 played → refresh → block finished (next unwatched pair aired
   7 days later) → group rotates to the back.
4. Regression: LRW with Group By Series Name — watch one episode → that series
   rotates to the back (no hold).
5. Unmark all played states and delete fixtures afterwards.

## Out of scope

- Persisting rotation state; block-membership-exact hold; holds for
  non-Collections grouping; any UI.
