# Crossover Air Blocks for Collection Round Robin — Design

**Date:** 2026-07-14
**Status:** Approved

## Summary

When a round-robin sort groups by **Collections** with **Air Date** within-group
order, the interleave currently emits one episode per collection per cycle. Episodes
that aired together — same-night crossovers, franchise weeks (Tue/Wed/Thu) — end up
a full cycle apart in the playlist.

This feature chunks each air-date-sorted collection group into **air blocks** and
interleaves one *block* per cycle instead of one item. Episodes inside a block get
sequential positions, so crossover episodes play back-to-back.

The window is configurable per sort option (default **3 days**).

## Block rule

Within a group already sorted by air date (`CompareWithinGroupByAirDate`):

- Start a new block with the next item.
- Extend the current block while **both** hold:
  1. The next item aired **≤ N days** after the previous item (day precision,
     pairwise chain), where N = the configured window.
  2. The next item's series is **not already in the block** (episodes resolve via
     `SeriesName`; non-episodes via `Name`).
- Items with a missing air date (`DateTime.MinValue`) never chain — always a
  block of 1 (otherwise all undated items would glue together). A dated item
  never chains onto an undated predecessor either.

Properties this guarantees:

- Same-night crossover (Show A E2.1 + Show B E1.1) → one block, adjacent output.
- Solo era (only Show A airing) → same-show rule breaks every chain → blocks of 1,
  identical to current one-per-cycle behavior.
- Spinoff premiere years after parent finale → date gap breaks the chain (fixes
  the naive "different title → together" heuristic).
- Daily shows can't chain runaway (same-show rule).
- Chain length capped by the number of distinct shows in the collection.
- Window `0` = same-day only.

Known edge (accept + document): a premiere night with two episodes of Show A plus
one of Show B splits as `{A E1.1}`, `{A E1.2, B E1.1}` — the same-show rule breaks
the chain at the second A episode.

## Activation gate

Blocking is active **only** when both:

- `CollectionGroupKeys != null` (GroupByField = "Collections"), and
- `OrderWithinGroupsByAirDate` is true.

When inactive, every item is its own block → positions are byte-identical to
current behavior. Not offered for SeriesName grouping (same-show rule makes all
blocks singletons anyway) or Genres/Studios (would serialize whole weeks of a
genre). Applies automatically to every round-robin variant (Ascending, Descending,
Random, Least Recently Watched) because group ordering is orthogonal to blocking.
Shuffled Round Robin is unaffected (shuffle already suppresses air-date order).

## Backend changes

- `Core/Models/SortOption.cs`: new nullable property
  `public int? AirBlockWindowDays { get; set; }` with
  `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`.
  Null → default 3.
- `Core/Orders/RoundRobinOrder.cs` (`RoundRobinBase`):
  - `public const int DefaultAirBlockWindowDays = 3;`
  - `public int AirBlockWindowDays { get; set; } = DefaultAirBlockWindowDays;`
  - `BuildInterleavedPositions`: build `List<List<BaseItem>>` blocks per group
    (chunking helper above when the gate is active; one item per block otherwise),
    interleave over blocks, assign sequential positions within a block.
  - Clamp the window to `[0, 30]` at the use site.
- `Core/SmartList.cs` (`InitializeFromDto`, ~line 126): thread
  `so.AirBlockWindowDays` onto the `RoundRobinBase` instance next to
  `GroupByField` / `OrderWithinGroupsByAirDate`.

No `InputValidator` / `DtoMapper` changes — `SortOption` fields are pass-through
serialization today; the backend clamp covers bad values.

## Frontend changes (`config-sorts.js` only)

The sort UI is fully JS-generated — **no HTML file changes**.

- New number input field "Air window (days)" in `createSortBox`, next to the
  "Order Within Group" select. Min 0, max 30, placeholder/default 3. Help/title
  text: episodes from different shows in the same collection that aired within
  this many days of each other play back-to-back.
- Visible only when GroupBy = `Collections` AND Order Within Group = `AirDate`
  (and the sort is a non-Shuffled round robin). Visibility must react to changes
  of the Sort By select, the Group By select, and the Order Within Group select.
- `collectSortsFromForm`: when the field is applicable (Collections + AirDate,
  non-Shuffled round robin) and contains a valid number, include
  `AirBlockWindowDays` (parsed int, clamped 0–30). Blank input → omit the
  property (null = default 3). An explicit 3 is stored as 3 — no
  equals-default stripping, so a future default change never silently alters
  saved lists.
- Edit mode: populate the input from `sortData.AirBlockWindowDays` (blank when
  null → shows placeholder 3).
- No ES6 template literals; `class="emby-input"` conventions per project rules.

## Docs

- `docs/content/user-guide/sorting-and-limits.md`: extend the collection
  grouping / air-date section with the air-block behavior and the window setting.
- `docs/content/examples/common-use-cases.md`: update the franchise/crossover
  recipe to mention crossover nights now play together.

## Trade-offs (document, don't solve)

- A cycle may deliver 2–3 episodes from one franchise before moving on — that is
  the point of the feature.
- Global `MaxItems` counts episodes, not blocks — a block can be cut mid-way at
  the list size limit.

## Verification

No test suite; verify against local Jellyfin (`cd dev && ./build-local.sh`, both
ABIs build clean). Use a collection with staggered air dates:

1. Collection with two shows sharing air dates (crossover night) + other
   non-collection shows, Round Robin + GroupBy Collections + Air Date order:
   crossover episodes appear adjacent; other groups still one-per-cycle.
2. Same list with window input set to 0: only same-day episodes pair.
3. Solo-era episodes (before second show premieres) still emit one per cycle.
4. Non-collection round robin (GroupBy SeriesName): output identical to before
   the change.
5. Edit the list: window value round-trips (shows saved value, blank = default).

## Out of scope

- Persisted rotation state, per-group MaxItems, block-aware MaxItems.
- Windows measured from block anchor instead of pairwise chain.
- Exposing blocking for non-Collections group fields.
