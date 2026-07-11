# Least Recently Watched Round Robin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new round-robin sort variant that orders show rotation by how recently the user watched each show — least recently watched first, never-watched first of all — so the rotation "continues where you left off" with zero persisted state.

**Architecture:** New `RoundRobinLeastRecentlyWatchedOrder` subclass of `RoundRobinBase` whose group ordering reads a `GroupRecency` map (group key → most recent per-user `LastPlayedDate`). SmartList injects that map at the top of `FilterPlaylistItems`, computed from the **unfiltered** media pool — this is load-bearing: the intended companion rule `Playback Status is Unwatched` removes watched episodes from results, so recency derived from filtered items would see every group as never-watched. Auto-refresh needs **no changes** (verified: playback events select playlists by media type + membership, not rules).

**Tech Stack:** C# (.NET, multi-target net9.0/net10.0), Jellyfin plugin API, vanilla JS config pages (no ES6 template literals).

**Spec:** `docs/superpowers/specs/2026-07-10-least-recently-watched-round-robin-design.md`

## Global Constraints

- Build treats all warnings as errors (`AnalysisMode=Recommended`): CA1822 (make static), CA1305 (locale), CA5394 (insecure Random — suppress with pragma like existing round-robin code if needed).
- Both frameworks must build: `net10.0` and `net9.0`.
- Sort display name everywhere (backend registry, JS constants, docs): exactly `Least Recently Watched Round Robin`. UI dropdown label: `Least Recently Watched Round Robin (Interleave)`.
- Config JS: no ES6 template literals, string concatenation only; never `is="emby-input"`; user messages via `showNotification()`.
- No test suite exists. Verification = build both targets + live checks against local Jellyfin (<http://localhost:8096>) via `cd dev && ./build-local.sh`.
- Working tree is the worktree at `.claude/worktrees/least-recently-watched-round-robin`, branch `worktree-least-recently-watched-round-robin`, based on main `60ab82f`.
- Commit messages end with:

  ```text
  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01RBFLcyWKuktDSu9acy68zg
  ```

---

### Task 1: Order class, helper promotion, backend registry

**Files:**

- Modify: `Jellyfin.Plugin.SmartLists/Core/Orders/RoundRobinOrder.cs` (append new class at end, after `RoundRobinShuffledOrder`, ~line 305)
- Modify: `Jellyfin.Plugin.SmartLists/Core/Orders/LastPlayedOrderBase.cs` (promote two private static helpers to internal, ~lines 129 and 170)
- Modify: `Jellyfin.Plugin.SmartLists/Core/SmartList.cs` (`OrderMap` ~line 3093, `DirectionlessOrders` ~line 3104)

**Interfaces:**

- Consumes: `RoundRobinBase` (`GroupByField`, `ExtractGroupKey`, `OrderGroupKeys` hook), `OrderUtilities.SharedNaturalComparer`, `UserDataCacheHelper.GetCachedUserData(User, BaseItem, RefreshCache, IUserDataManager)` (returns `UserItemData?`), `LastPlayedOrderBase.GetAggregateLastPlayedDate` / `GetLastPlayedDateFromUserData`.
- Produces: `RoundRobinLeastRecentlyWatchedOrder` with `public Dictionary<string, DateTime> GroupRecency { get; set; }` and `internal static Dictionary<string, DateTime> BuildGroupRecency(IEnumerable<BaseItem> items, string? groupByField, User user, IUserDataManager? userDataManager, RefreshQueueService.RefreshCache? refreshCache, ILogger? logger)`. Task 2 calls both. Registry name `"Least Recently Watched Round Robin"`.

- [ ] **Step 1: Promote the two `LastPlayedOrderBase` helpers**

In `Jellyfin.Plugin.SmartLists/Core/Orders/LastPlayedOrderBase.cs`, change the access modifier of both existing methods (bodies unchanged):

```csharp
// line ~129: was "private static DateTime? GetAggregateLastPlayedDate("
internal static DateTime? GetAggregateLastPlayedDate(

// line ~170: was "private static DateTime GetLastPlayedDateFromUserData(object? userData)"
internal static DateTime GetLastPlayedDateFromUserData(object? userData)
```

- [ ] **Step 2: Add the order class**

Append to `Jellyfin.Plugin.SmartLists/Core/Orders/RoundRobinOrder.cs`, inside the namespace, after `RoundRobinShuffledOrder`:

```csharp
    /// <summary>
    /// Round Robin sort with groups ordered by how recently the user watched anything in them:
    /// least recently watched first, never-watched groups first of all (alphabetical tie-break).
    /// The rotation "continues where the user left off" with no persisted state - it is derived
    /// entirely from per-user LastPlayedDate data, so it is deterministic for a given watch history.
    /// </summary>
    public class RoundRobinLeastRecentlyWatchedOrder : RoundRobinBase
    {
        public override string Name => "Least Recently Watched Round Robin";

        /// <summary>
        /// Group key → most recent per-user LastPlayedDate, computed from the UNFILTERED media
        /// pool (rules like "Playback Status is Unwatched" remove watched items from the results,
        /// so recency derived from filtered items would see every group as never watched).
        /// Set by SmartList before <see cref="RoundRobinBase.PreComputePositions"/>.
        /// Groups absent from the map are treated as never watched and sort first.
        /// </summary>
        public Dictionary<string, DateTime> GroupRecency { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        protected override List<string> OrderGroupKeys(IEnumerable<string> keys)
        {
            return keys
                .OrderBy(k => GroupRecency.TryGetValue(k, out var d) ? d : DateTime.MinValue)
                .ThenBy(k => k, OrderUtilities.SharedNaturalComparer)
                .ToList();
        }

        /// <summary>
        /// Builds the group key → most recent LastPlayedDate map for one user across the given items.
        /// Container items (Series/Season/MusicAlbum) use the aggregate-over-children date when the
        /// refresh cache has their children, mirroring LastPlayedOrderBase.
        /// </summary>
        internal static Dictionary<string, DateTime> BuildGroupRecency(
            IEnumerable<BaseItem> items,
            string? groupByField,
            User user,
            IUserDataManager? userDataManager,
            RefreshQueueService.RefreshCache? refreshCache,
            ILogger? logger)
        {
            var recency = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(groupByField) || userDataManager == null || user == null)
            {
                logger?.LogWarning("Least Recently Watched Round Robin: missing GroupByField or user context - groups fall back to alphabetical order");
                return recency;
            }

            foreach (var item in items)
            {
                try
                {
                    var key = ExtractGroupKey(item, groupByField);

                    var lastPlayed = LastPlayedOrderBase.GetAggregateLastPlayedDate(item, user, userDataManager, refreshCache)
                        ?? LastPlayedOrderBase.GetLastPlayedDateFromUserData(
                            refreshCache != null
                                ? UserDataCacheHelper.GetCachedUserData(user, item, refreshCache, userDataManager)
                                : userDataManager.GetUserData(user, item));

                    if (lastPlayed > DateTime.MinValue &&
                        (!recency.TryGetValue(key, out var existing) || lastPlayed > existing))
                    {
                        recency[key] = lastPlayed;
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Error reading last played date for item {ItemName}", item.Name);
                }
            }

            return recency;
        }
    }
```

Add `using Jellyfin.Plugin.SmartLists.Utilities;` to the file's usings if not already present (needed for `UserDataCacheHelper`; `OrderUtilities` lives in the same namespace as the orders — check existing usings and only add what's missing).

- [ ] **Step 3: Register the order**

In `Jellyfin.Plugin.SmartLists/Core/SmartList.cs`:

`OrderMap` (~line 3093), after the Shuffled Round Robin entry:

```csharp
            { "Shuffled Round Robin", () => new RoundRobinShuffledOrder() },
            { "Least Recently Watched Round Robin", () => new RoundRobinLeastRecentlyWatchedOrder() },
```

`DirectionlessOrders` (~line 3104), after "Shuffled Round Robin":

```csharp
            "Random",
            "Random Round Robin",
            "Shuffled Round Robin",
            "Least Recently Watched Round Robin",
            "NoOrder",
```

No `InitializeFromDto` change: the existing `is RoundRobinBase` branch assigns `GroupByField` generically. No `IsDescendingOrder` change: it is a type list of `*Desc` orders; the new class is not in it, so it correctly reports ascending.

- [ ] **Step 4: Build both targets**

```bash
dotnet build Jellyfin.Plugin.SmartLists --framework net10.0 --configuration Release
dotnet build Jellyfin.Plugin.SmartLists --framework net9.0 --configuration Release
```

Expected: both succeed, zero warnings (warnings are errors).

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Core/Orders/RoundRobinOrder.cs Jellyfin.Plugin.SmartLists/Core/Orders/LastPlayedOrderBase.cs Jellyfin.Plugin.SmartLists/Core/SmartList.cs
git commit -m "Add Least Recently Watched Round Robin order and registry entries"
```

---

### Task 2: SmartList recency injection

**Files:**

- Modify: `Jellyfin.Plugin.SmartLists/Core/SmartList.cs` (top of `FilterPlaylistItems`, ~line 715-722)

**Interfaces:**

- Consumes: `RoundRobinLeastRecentlyWatchedOrder.GroupRecency` (property) and `RoundRobinLeastRecentlyWatchedOrder.BuildGroupRecency(...)` from Task 1.
- Produces: every `RoundRobinLeastRecentlyWatchedOrder` in `Orders` has `GroupRecency` populated before any of the three `PrepareRoundRobinPositions` call sites (lines ~894, ~1106, ~1364) runs. No signature changes anywhere.

- [ ] **Step 1: Inject the recency map at the top of `FilterPlaylistItems`**

Current code at ~line 715:

```csharp
        public IEnumerable<Guid> FilterPlaylistItems(IEnumerable<BaseItem> items, ILibraryManager libraryManager,
            User user, RefreshQueueService.RefreshCache refreshCache, IUserDataManager? userDataManager = null, ILogger? logger = null, Action<int, int>? progressCallback = null)
        {
            var stopwatch = Stopwatch.StartNew();

            // Clear similarity scores from any previous runs
            _similarityScores.Clear();
```

Insert directly after the `_similarityScores.Clear();` line:

```csharp
            // Least Recently Watched Round Robin needs per-group watch recency computed from the
            // UNFILTERED pool: rules like "Playback Status is Unwatched" remove watched items from
            // the results, so recency derived from filtered items would see every group as unwatched.
            // Same injection pattern as SimilarityOrder.Scores / RuleBlockOrder.GroupMappings.
            if (Orders != null && Orders.Any(o => o is RoundRobinLeastRecentlyWatchedOrder))
            {
                var recencyPool = items as IList<BaseItem> ?? items.ToList();
                items = recencyPool;
                foreach (var lrwOrder in Orders.OfType<RoundRobinLeastRecentlyWatchedOrder>())
                {
                    lrwOrder.GroupRecency = RoundRobinLeastRecentlyWatchedOrder.BuildGroupRecency(
                        recencyPool, lrwOrder.GroupByField, user, userDataManager, refreshCache, logger);
                }
            }
```

Notes for the implementer:

- The `items = recencyPool;` reassignment prevents double enumeration of a lazy `IEnumerable` (the pool is enumerated here and again by the filtering pipeline below).
- This runs before all three `PrepareRoundRobinPositions` call sites, including the per-rule-block path and the include-only early-return path, so no call-site changes are needed.
- If the file needs a using for `System.Linq`, it already has one (the method uses `.Select` below).

- [ ] **Step 2: Build both targets**

```bash
dotnet build Jellyfin.Plugin.SmartLists --framework net10.0 --configuration Release
dotnet build Jellyfin.Plugin.SmartLists --framework net9.0 --configuration Release
```

Expected: both succeed, zero warnings.

- [ ] **Step 3: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Core/SmartList.cs
git commit -m "Inject watch-recency map into Least Recently Watched Round Robin before sorting"
```

---

### Task 3: UI sort constants

**Files:**

- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-core.js` (~lines 65-96)

**Interfaces:**

- Consumes: nothing from other tasks (value string must match Task 1's registry name exactly).
- Produces: `'Least Recently Watched Round Robin'` present in `SORT_OPTIONS`, `ORDERLESS_SORTS`, `ROUND_ROBIN_SORTS`. All dropdown visibility (Group By shown, Asc/Desc hidden), save paths, and display formatting flow through the existing `isOrderlessSort`/`isRoundRobinSort` predicates — no other JS files change.

- [ ] **Step 1: Add the three entries in `config-core.js`**

In `SmartLists.SORT_OPTIONS` (~line 87), after the Shuffled Round Robin entry:

```javascript
        { value: 'Shuffled Round Robin', label: 'Shuffled Round Robin (Interleave)' },
        { value: 'Least Recently Watched Round Robin', label: 'Least Recently Watched Round Robin (Interleave)' },
```

Replace the two constant arrays (~lines 93 and 96):

```javascript
    // Sorts that have no Ascending/Descending direction
    SmartLists.ORDERLESS_SORTS = ['Random', 'Random Round Robin', 'Shuffled Round Robin', 'Least Recently Watched Round Robin', 'NoOrder'];

    // Round Robin sort variants (all use a GroupBy field)
    SmartLists.ROUND_ROBIN_SORTS = ['Round Robin', 'Random Round Robin', 'Shuffled Round Robin', 'Least Recently Watched Round Robin'];
```

- [ ] **Step 2: Verify no hardcoded round-robin chains remain that would miss the new sort**

```bash
grep -n "Shuffled Round Robin" Jellyfin.Plugin.SmartLists/Configuration/*.js
```

Expected: hits ONLY in `config-core.js` (the SORT_OPTIONS entry and the two arrays). If any other file hardcodes the round-robin list, add the new name there the same way and note it as a deviation.

- [ ] **Step 3: Syntax-check**

```bash
node --check Jellyfin.Plugin.SmartLists/Configuration/config-core.js
```

Expected: no output (success).

- [ ] **Step 4: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/config-core.js
git commit -m "Add Least Recently Watched Round Robin to UI sort constants"
```

---

### Task 4: Docs

**Files:**

- Modify: `docs/content/user-guide/sorting-and-limits.md` (round robin section — locate with `grep -n "Round Robin" docs/content/user-guide/sorting-and-limits.md`)

**Interfaces:** none — prose only. Match the page's existing tone/format for the other round-robin variants (read the section first, follow its structure: if variants are a table, add a row; if subsections, add a subsection).

- [ ] **Step 1: Document the variant**

Content to convey (adapt to the page's existing format, don't paste verbatim if the section is a table):

```markdown
#### Least Recently Watched Round Robin

Rotates through your shows starting with the one you watched *least* recently — shows you have
never watched come first, and the show you watched most recently goes to the back of the rotation.
Watch an episode of Show A today, and on the next refresh Show A moves to the end while the others
shift forward. Episodes within each show stay in their natural order (season/episode).

Unlike Random Round Robin, the rotation is not shuffled — it is derived from your watch history,
so it "continues where you left off" across refreshes.

**Recommended setup for a fair TV rotation:**

- **Sort By:** Least Recently Watched Round Robin, **Group By:** Series Name
- **Rule:** Playback Status → is → Unwatched (watched episodes drop off; each show's next unwatched
  episode surfaces automatically)
- **Auto-refresh:** On all changes (the rotation advances right after you finish watching something)

With other auto-refresh modes the rotation still advances, but only at the next refresh
(scheduled or library change).

!!! note
    For playlists shared by multiple users, each user gets their own rotation based on their own
    watch history.
```

- [ ] **Step 2: Check for other docs mentioning the round-robin family**

```bash
grep -rn "Random Round Robin" docs/content/ | grep -v sorting-and-limits
```

If example pages enumerate the variants (e.g. `docs/content/examples/common-use-cases.md`), add the new variant consistently there too.

- [ ] **Step 3: Commit**

```bash
git add docs/content/
git commit -m "Document Least Recently Watched Round Robin"
```

---

### Task 5: End-to-end verification against local Jellyfin

**Files:** none modified — verification only. Follow spec section "Verification".

- [ ] **Step 1: Build and deploy to the local container**

```bash
cd dev && ./build-local.sh
```

Expected: build succeeds, container restarts.

- [ ] **Step 2: Create a test playlist via the admin API**

Use the pattern from previous sessions: base URL `http://localhost:8096`, admin API key from `dev/` config (check `dev/` scripts or ask the driver). Create a playlist with: MediaTypes `["Episode"]`, sort `SortBy: "Least Recently Watched Round Robin"` + `GroupByField: "SeriesName"` (SortOptions format), rule `Playback Status is Unwatched` for the owner user, `AutoRefresh: OnAllChanges`. POST to the SmartLists controller endpoint (same endpoint used when testing bumpers: `/Plugins/SmartLists/...` — discover exact route via `grep -n "Route" Jellyfin.Plugin.SmartLists/Api/Controllers/SmartListController.cs`).

- [ ] **Step 3: Verify initial rotation**

Refresh the playlist, then fetch its items. Expected: series interleave alphabetically (all never-watched), episodes in season/episode order within each series slot.

- [ ] **Step 4: Watch something and verify rotation advance**

Mark the first episode played via Jellyfin API (`POST /Users/{userId}/PlayedItems/{itemId}` with the API key). Wait for the batched auto-refresh (up to ~30s; watch `docker logs jellyfin 2>&1 | grep -i "Smart"`). Fetch items again. Expected: the watched episode's series now rotates LAST; its slot shows that series' next unwatched episode; other series shifted forward.

- [ ] **Step 5: Verify determinism**

Refresh the playlist twice more without watching anything. Expected: identical order both times (no shuffle).

- [ ] **Step 6: Regression check**

Refresh an existing Round Robin / Random Round Robin / Shuffled Round Robin playlist (create one if none exists). Expected: behavior unchanged; no new warnings in logs.

- [ ] **Step 7: Clean up test playlists, verify logs clean**

Delete test playlists via API. `docker logs jellyfin 2>&1 | grep -iE "smart.*(error|warn)" | tail -20` — expected: no new errors from this feature.

- [ ] **Step 8: Commit any fixes found; otherwise nothing to commit**

---

## Self-review notes

- Spec coverage: order class + recency semantics (Task 1), unfiltered-pool injection (Task 2), UI constants (Task 3), docs incl. companion recipe + multi-user note (Task 4), verification incl. determinism + regression (Task 5). Auto-refresh: spec says verified no-op — no task, docs cover the mode note.
- Names consistent: `RoundRobinLeastRecentlyWatchedOrder`, `GroupRecency`, `BuildGroupRecency`, registry/JS string `"Least Recently Watched Round Robin"`.
- `UserDataCacheHelper.GetCachedUserData` returns `UserItemData?`; passing it to `GetLastPlayedDateFromUserData(object?)` is fine (reflection helper handles any shape across both target ABIs).
