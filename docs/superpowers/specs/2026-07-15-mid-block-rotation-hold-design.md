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

1. Find the most recently watched item (max per-user `LastPlayedDate`) and its
   air date `wAir` (`OrderUtilities.GetReleaseDate(...).Date`); no air date →
   normal rotation.
2. Rebuild the group's air blocks exactly as the interleave does
   (`CompareWithinGroupByAirDate` sort + `ChunkIntoAirBlocks`, which chains
   only across *different* shows) and locate the block containing the watched
   item.
3. If **that block** still has an unwatched item (per-user `LastPlayedDate`
   missing/`MinValue`) with a known air date at or after `wAir`, the group is
   **mid-block**: omit it from the recency map, so it sorts with the
   never-watched groups at the front (existing alphabetical tie-break).
4. Otherwise (block finished, no unwatched left in it, or no air-date data):
   the group gets its normal recency (max LastPlayedDate) and rotates as today.

> **Revised during review** from an air-date-distance rule
> (`nextUnwatchedAir - wAir <= window`; block membership was originally out of
> scope). The distance rule permanently front-pinned any group whose air
> cadence fits inside the window: single-show fallback groups with daily
> episodes, and whole collections whenever the window ≥ the shows' weekly
> cadence. Block membership fixes both: single-show groups form blocks of one
> and rotate after every watch, and a finished block rotates even when the
> next block aired within the window.

Consequences:

- Watch part 1 of a same-night crossover → parts 2–3 stay at the top next
  refresh.
- Watch the last part of the block → the group rotates to the back as before,
  even when the next block aired within the window.
- Never-watched groups and mid-block groups share the front (both absent from
  the recency map); alphabetical tie-break orders them.
- Watched item with no air date, or no unwatched item with an air date →
  normal rotation (hold never triggers on missing metadata).
- Rewatching an old crossover part whose partner is unwatched pins the group
  to the front — acceptable: the rewatcher is being set up to continue.

**Known trade-off:** because blocks split same-show double-headers
(`{A1,B1}`, `{A2}`), finishing `{A1,B1}` does not hold the group for same-day
`A2` — the group rotates, and `A2` plays when the rotation returns. Accepted
in exchange for never front-pinning cadence-matched groups.

## Changes

### Backend only — no DTO, no UI, no new setting

- `Core/Orders/RoundRobinOrder.cs` — `RoundRobinLeastRecentlyWatchedOrder.BuildGroupRecency`:
  new optional parameter `int? airBlockWindowDays = null`. Null → exact current
  single-pass behavior (all non-collection callers). When set: collect per-group
  `(airDate, lastPlayed)` pairs in the existing loop, then post-process each
  group with the rule above; log at Debug when a group is held mid-block.
  Update the `GroupRecency` XML doc to mention the hold.
- `Core/SmartList.cs` (~line 784) — the LRW injection passes the window when
  the gate matches:
  `lrwOrder.GroupByField == "Collections" && lrwOrder.OrderWithinGroupsByAirDate ? Math.Clamp(lrwOrder.AirBlockWindowDays, 0, 30) : (int?)null`.

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
