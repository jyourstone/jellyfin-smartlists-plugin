# Design: Shuffled Round Robin + Bumpers

**Date:** 2026-07-10
**Status:** Approved
**Origin:** User feature request — "randomize not only what show is interleaved first, but also randomize the episode order as well … specify a collection or series name to be 'bumpers' or commercials that are played between each episode, repeating if needed."

A previous implementation attempt failed because bumper insertion was modeled inside the sorting pipeline (`Orders`/`GetSortKey`), where changing item counts and repeated items cannot be expressed as sort keys. This design keeps the sorting pipeline untouched: Part 1 is a pure sort variant using the existing precomputed-position mechanism, and Part 2 is a post-processing weave that runs after sorting and limits are complete.

---

## Part 1 — Shuffled Round Robin sort variant

### Behavior

A new sort option, **"Shuffled Round Robin"**, extends the existing round-robin family. Like "Random Round Robin" it randomizes the group interleave order, and additionally shuffles the item order *within* each group. For a playlist grouped by SeriesName this produces a random show rotation with episodes in random order — the "turning on a TV at a random time" feel. Each refresh produces a new shuffle.

Existing variants are unchanged:

| Variant | Group order | Within-group order |
|---|---|---|
| Round Robin Ascending | A→Z | Natural (season/episode, disc/track, name) |
| Round Robin Descending | Z→A | Natural |
| Random Round Robin | Shuffled | Natural |
| **Shuffled Round Robin (new)** | **Shuffled** | **Shuffled** |

### Implementation

`Core/Orders/RoundRobinOrder.cs`:

- `RoundRobinBase` gains `protected virtual bool ShuffleWithinGroups => false;`.
- `BuildInterleavedPositions` (already receives the group dictionary) shuffles each group's item list with the existing Fisher-Yates `Shuffle` helper instead of sorting via `CompareWithinGroup` when the flag is set. The flag is passed into the static method (or the method becomes instance-level).
- New subclass `RoundRobinShuffledOrder : RoundRobinBase` — group-key strategy identical to `RoundRobinRandomOrder` (shuffle), `ShuffleWithinGroups => true`, `Name => "Shuffled Round Robin"`.

Because round robin works via precomputed integer positions (`PreComputePositions` → `ItemPositions`, `GetSortKey` returns an int), the new variant composes with multi-sort cascades, early-return paths, per-group limits, and MaxItems with **no changes** to `ApplyMultipleOrders`, `ApplySortingCore`, `WrapOrdersWithChildAggregation`, or `IsDescendingOrder`.

Touch points:

- `Core/Orders/RoundRobinOrder.cs` — flag + subclass (~20 lines).
- `Core/SmartList.cs` — order registry entry (`"Shuffled Round Robin"`, near line 3099) and the `InitializeFromDto` special-case for sorts without Asc/Desc variants (alongside "Random Round Robin").
- `Configuration/config-sorts.js` — add `'Shuffled Round Robin'` to every existing `'Round Robin' || 'Random Round Robin'` condition: GroupBy dropdown visibility (2 sites), Sort Order dropdown hiding, GroupByField save path, and the sort-options list. No new UI controls; the existing Group By dropdown applies.
- Docs: sorting page in `/docs/content/`.

---

## Part 2 — Bumpers

### Concept

A playlist can define a second, independent rule set that selects "bumper" items (commercials/interstitials). After the main playlist content is filtered, sorted, and limited, bumper items are woven in: one bumper after every N main items, cycling through the bumper pool with wraparound so bumpers repeat as needed. Playlists only — collections have no play order and never show the feature.

### Data model

New nested DTO on `SmartPlaylistDto` (not `SmartListDto`; collections are excluded by construction):

```csharp
public class BumperConfigDto
{
    public List<ExpressionSet> ExpressionSets { get; set; } = [];  // full rule engine, same shape as main rules
    public List<string> MediaTypes { get; set; } = [];             // bumper pool's own media types (e.g. Video while main is Episode)
    public string BumperOrder { get; set; } = "Random";            // "Random" | "Name" | "Release Date"
    public int Interval { get; set; } = 1;                         // insert 1 bumper after every N main items
}

// on SmartPlaylistDto:
public BumperConfigDto? Bumpers { get; set; }   // null = feature off
```

No `Enabled` flag: `Bumpers` being null or having no expression sets means off. `BumperOrder` maps to existing `OrderFactory` names ("Random", "Name Ascending", "Release Date Ascending") — no new Order classes.

### Backend flow

In `PlaylistService.ProcessPlaylist`, after the main item list is produced (`FilterPlaylistItems` → which already applies sorting and MaxItems/MaxPlayTime limits) and after the existing `.Distinct()`:

1. **Build bumper pool.** Construct a synthetic `SmartPlaylistDto` from `dto.Bumpers` (bumper ExpressionSets, bumper MediaTypes, Order from `BumperOrder`, no MaxItems), wrap in `SmartList`, fetch media via the existing `GetAllUserMedia(user, bumperMediaTypes, dto)`, and run `FilterPlaylistItems` with the same `refreshCache` and same user. Per-user fields (playback status) in bumper rules work naturally; `AllUsers` playlists weave per user because `ProcessPlaylist` already runs per user.
2. **Dedupe against main.** Remove bumper ids that already appear in the main list — an item cannot be both content and bumper.
3. **Weave.** After every `Interval` main items, insert the next bumper, cycling with wraparound (`bumpers[i % bumpers.Count]`). No bumper after the final main item. Main item order is never altered.
4. **Write.** Build `LinkedChildren` from the woven sequence **without a second `Distinct()`** — bumpers legitimately repeat. Jellyfin's `Playlist.LinkedChildren` is a plain array set directly by the plugin (bypasses the server-side duplicate setting), so duplicate entries are supported.

Consequences and decisions:

- **Limits:** bumpers do not count toward MaxItems/MaxPlayTimeMinutes (weave happens after limits). MaxItems=50 means 50 real episodes plus bumpers.
- **Stats:** `dto.ItemCount` and `TotalRuntimeMinutes` are computed from the woven list, since that is the actual playlist content.
- **Randomness:** `BumperOrder = "Random"` reshuffles the pool each refresh.
- **Refresh change detection:** the rule-hash used for auto-refresh must include `Bumpers` so config changes trigger a refresh.
- **Sorting pipeline isolation:** the weave never enters `Orders`, `GetSortKey`, or any of the three sort call sites. Any sort combination on the main list works with bumpers.

Edge handling:

- Bumper rules match nothing → playlist written without bumpers, debug log.
- Bumper config invalid → rejected at save by `InputValidator` (same expression validation as main rules, plus `Interval >= 1`).
- Media lookup for bumper items: the LinkedChildren build requires `BaseItem` instances; the bumper media fetched in step 1 is added to the `mediaLookup` used at write time.

### UI

The rule editor in `config-rules.js` (3115 lines) hardcodes `#rules-container` in ~10 functions and reads media types via `getSelectedMediaTypes(page)`. It is refactored to be container-scoped:

- New helper `SmartLists.getRulesContainer(page, scope)`; all `page.querySelector('#rules-container')` sites route through it. Scope omitted → main container — existing behavior byte-identical.
- `getRulesFromForm`, the populate/edit path, and field-dropdown media-type filtering gain a scope parameter; bumper scope reads the bumper section's own media-type select.

New collapsible **"Bumpers"** section in the playlist form (below sorting): media-type select, the scoped rule editor, bumper order dropdown, interval number input. Added to **both** `config.html` and `user-playlists.html`. Hidden entirely when editing collections. Standard Jellyfin UI constraints apply (no ES6 template literals, `class="emby-input"`, `showNotification`).

**Risk mitigation:** the container-scoping refactor lands as its own commit with zero behavior change, verified against the existing editor (create/edit/clone/move/remove rules on both pages) before any bumper UI is added on top. This isolates the highest-regression-risk step.

### Out of scope

- Bumpers for collections.
- Multiple bumpers per gap ("commercial break" mode) — `Interval` covers spacing; N-per-gap can be added later if requested.
- Bumper-specific MaxItems or limits.
- Embedded second sort-option editor for bumpers (the `BumperOrder` dropdown is deliberately minimal).

---

## Touched files (summary)

| Area | Files |
|---|---|
| Part 1 backend | `Core/Orders/RoundRobinOrder.cs`, `Core/SmartList.cs` |
| Part 1 UI | `Configuration/config-sorts.js` |
| Part 2 DTO | `Core/Models/SmartPlaylistDto.cs` (+ new `BumperConfigDto`), `Utilities/DtoMapper.cs`, `Utilities/InputValidator.cs` |
| Part 2 backend | `Services/Playlists/PlaylistService.cs` |
| Part 2 UI | `Configuration/config-rules.js` (scoping refactor), `Configuration/config-lists.js` (save/load), `config.html`, `user-playlists.html` |
| Docs | `/docs/content/` sorting page + new bumpers page with examples |

`RefreshQueueService` constructs `PlaylistService` manually — new constructor dependencies, if any, must be threaded through; current design adds none.

## Verification

No test suite exists; verification is against the local Jellyfin instance:

1. `cd dev && ./build-local.sh` — build must pass (warnings are errors, both `net9.0` and `net10.0` targets).
2. Shuffled Round Robin playlist (3+ series): shows rotate randomly, episodes within each show out of order, different arrangement each refresh, GroupBy dropdown works.
3. Bumper playlist: bumpers appear after every N items, repeat when pool is smaller than needed, `Random` bumper order changes per refresh, bumper items never duplicated out of the main content set.
4. Regression: existing playlists (Round Robin, Random Round Robin, rule blocks with per-group MaxItems, IncludeCollectionOnly-only lists, MaxItems/MaxPlayTime) refresh with unchanged behavior; rule editor works identically on both admin and user pages before the bumper section is added (refactor commit) and after.
5. Collections: no bumper UI, no behavior change.
