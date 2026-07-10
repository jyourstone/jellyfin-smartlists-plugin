# Shuffled Round Robin + Bumpers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Shuffled Round Robin" sort (random show rotation + random episode order) and a "Bumpers" feature that weaves interstitial items — selected by their own rule set — between playlist items, repeating as needed.

**Architecture:** Part 1 is a new `RoundRobinBase` variant using the existing precomputed-position mechanism (zero sorting-pipeline changes). Part 2 keeps bumpers entirely outside the sorting pipeline: a `BumperConfigDto` on `SmartPlaylistDto` drives a second `FilterPlaylistItems` pass in `PlaylistService`, and the results are woven into the final ID list after sorting, limits, and dedupe. The rule-editor UI is refactored to be container-scoped (zero behavior change) before the bumper rule editor is added.

**Tech Stack:** C# (.NET, multi-targets net9.0/net10.0), Jellyfin plugin API, vanilla JS (Jellyfin admin UI — no ES6 template literals).

**Spec:** `docs/superpowers/specs/2026-07-10-shuffled-round-robin-and-bumpers-design.md`

## Global Constraints

- Build treats all warnings as errors (`AnalysisMode=Recommended`); CA1822 (make static), CA1305 (locale), CA5394 (insecure Random — suppress with pragma + comment as existing code does) will fail the build.
- No test suite exists. Every task verifies via `cd dev && ./build-local.sh` (builds both targets, restarts local Jellyfin at <http://localhost:8096>) plus manual exercise. Logs: `docker logs jellyfin 2>&1 | grep -i "Smart"`.
- Jellyfin UI JS: **no ES6 template literals** (string concatenation only); never `is="emby-input"` (use `class="emby-input"`); user messages via `showNotification()`.
- UI changes must be made in **both** `config.html` (admin) and `user-playlists.html` (user) — JS modules are shared.
- Bumpers are playlists-only. Collections must never see bumper UI or behavior.
- All file paths below are relative to `Jellyfin.Plugin.SmartLists/` unless prefixed with `docs/` or `dev/`.

---

### Task 1: Shuffled Round Robin — backend

**Files:**
- Modify: `Core/Orders/RoundRobinOrder.cs`
- Modify: `Core/SmartList.cs` (order registry ~line 3101; `InitializeFromDto` ~line 126)

**Interfaces:**
- Consumes: existing `RoundRobinBase.BuildInterleavedPositions`, `Shuffle`, `OrderFactory.OrderMap`.
- Produces: `RoundRobinShuffledOrder` class with `Name == "Shuffled Round Robin"`; registry key `"Shuffled Round Robin"`. Task 2's UI sends `SortBy: 'Shuffled Round Robin'` and relies on these exact strings.

- [ ] **Step 1: Add shuffle-within-groups hook to `RoundRobinBase`**

In `Core/Orders/RoundRobinOrder.cs`, add a virtual property to `RoundRobinBase` (below the `GroupByField` property):

```csharp
        /// <summary>
        /// When true, items within each group are shuffled instead of sorted in natural order.
        /// </summary>
        protected virtual bool ShuffleWithinGroups => false;
```

Update `PreComputePositions` to pass the flag:

```csharp
        public void PreComputePositions(IEnumerable<BaseItem> items, ILogger? logger = null)
        {
            ItemPositions = BuildInterleavedPositions(items, GroupByField, OrderGroupKeys, Name, logger, ShuffleWithinGroups);
        }
```

Update `BuildInterleavedPositions` signature (add trailing parameter):

```csharp
        internal static ConcurrentDictionary<Guid, int> BuildInterleavedPositions(
            IEnumerable<BaseItem> items,
            string? groupByField,
            Func<IEnumerable<string>, List<string>> orderGroupKeys,
            string logPrefix,
            ILogger? logger,
            bool shuffleWithinGroups = false)
```

Replace the within-group sort loop (currently `foreach (var kvp in groups) { kvp.Value.Sort((a, b) => CompareWithinGroup(a, b)); }`):

```csharp
            foreach (var kvp in groups)
            {
                if (shuffleWithinGroups)
                {
                    Shuffle(kvp.Value, Random.Shared);
                }
                else
                {
                    kvp.Value.Sort((a, b) => CompareWithinGroup(a, b));
                }
            }
```

- [ ] **Step 2: Add the `RoundRobinShuffledOrder` subclass**

At the end of `Core/Orders/RoundRobinOrder.cs`, after `RoundRobinRandomOrder`:

```csharp
    /// <summary>
    /// Round Robin sort with groups in random order AND items shuffled within each group.
    /// Each refresh produces a fully random rotation: random group interleaving and
    /// random order inside every group ("turning on a TV at a random time").
    /// </summary>
    public class RoundRobinShuffledOrder : RoundRobinBase
    {
        public override string Name => "Shuffled Round Robin";

        protected override bool ShuffleWithinGroups => true;

        protected override List<string> OrderGroupKeys(IEnumerable<string> keys)
        {
            var list = keys.ToList();
            Shuffle(list, Random.Shared);
            return list;
        }
    }
```

- [ ] **Step 3: Register the order in `SmartList.cs`**

In the `OrderMap` dictionary (~line 3101), after the `"Random Round Robin"` entry:

```csharp
            { "Shuffled Round Robin", () => new RoundRobinShuffledOrder() },
```

In `InitializeFromDto` (~line 126), extend the no-Asc/Desc special case:

```csharp
                        if (so.SortBy == "Random Round Robin" || so.SortBy == "Shuffled Round Robin")
                        {
                            var rrRandom = (RoundRobinBase)OrderFactory.CreateOrder(so.SortBy);
                            rrRandom.GroupByField = so.GroupByField;
                            return rrRandom;
                        }
```

- [ ] **Step 4: Build**

Run: `cd dev && ./build-local.sh`
Expected: build succeeds for both targets, container restarts.

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Core/Orders/RoundRobinOrder.cs Jellyfin.Plugin.SmartLists/Core/SmartList.cs
git commit -m "Add Shuffled Round Robin sort order (backend)"
```

---

### Task 2: Shuffled Round Robin — UI + docs

**Files:**
- Modify: `Configuration/config-core.js` (~line 86, `SORT_OPTIONS`)
- Modify: `Configuration/config-sorts.js` (lines ~122, ~135, ~293, ~428, ~441, ~580)
- Modify: `Configuration/config-formatters.js` (~line 438)
- Modify: docs sorting page (locate with `grep -rln 'Round Robin' docs/content/`)

**Interfaces:**
- Consumes: registry key `"Shuffled Round Robin"` from Task 1.
- Produces: sort dropdown option `'Shuffled Round Robin'` that saves `SortBy: 'Shuffled Round Robin'` with `GroupByField`, hides Sort Order, shows Group By — identical UX to 'Random Round Robin'.

- [ ] **Step 1: Add the sort option to `config-core.js`**

In `SmartLists.SORT_OPTIONS` (~line 86), after the `'Random Round Robin'` entry:

```javascript
        { value: 'Shuffled Round Robin', label: 'Shuffled Round Robin (Interleave)' },
```

- [ ] **Step 2: Extend every `'Random Round Robin'` condition in `config-sorts.js`**

There are exactly six sites; add the new name to each condition:

Line ~122 (hide Sort Order):
```javascript
        if (sortByValue === 'Random' || sortByValue === 'Random Round Robin' || sortByValue === 'Shuffled Round Robin' || sortByValue === 'NoOrder') {
```

Line ~135 (Group By visibility in `syncSortOrderUI`):
```javascript
            groupByContainer.style.display = (sortByValue === 'Round Robin' || sortByValue === 'Random Round Robin' || sortByValue === 'Shuffled Round Robin') ? '' : 'none';
```

Line ~293 (Group By visibility at sort-box creation):
```javascript
        groupByField.container.style.display = (actualSortBy === 'Round Robin' || actualSortBy === 'Random Round Robin' || actualSortBy === 'Shuffled Round Robin') ? '' : 'none';
```

Line ~428 (force Ascending on save):
```javascript
            const sortOrder = (sortBy === 'Random' || sortBy === 'Random Round Robin' || sortBy === 'Shuffled Round Robin' || sortBy === 'NoOrder') ? 'Ascending' : (sortOrderSelect ? sortOrderSelect.value : 'Ascending');
```

Line ~441 (include GroupByField on save):
```javascript
            if (sortBy === 'Round Robin' || sortBy === 'Random Round Robin' || sortBy === 'Shuffled Round Robin') {
```

Line ~580 (orderName normalization):
```javascript
            if (orderName === 'Random' || orderName === 'Random Round Robin' || orderName === 'Shuffled Round Robin' || orderName === 'NoOrder' || orderName === 'No Order' || orderName === 'Default') {
```

- [ ] **Step 3: Extend display formatting in `config-formatters.js`**

Line ~438 (no "Ascending" suffix for orderless sorts):

```javascript
                if (displaySortBy === 'Random' || displaySortBy === 'Random Round Robin' || displaySortBy === 'Shuffled Round Robin' || displaySortBy === 'NoOrder' || displaySortBy === 'No Order' || displaySortBy === 'Default') {
```

- [ ] **Step 4: Confirm no other JS sites reference the sibling sort**

Run: `grep -rn "Random Round Robin" Jellyfin.Plugin.SmartLists/Configuration/*.js`
Expected: every hit either already includes `'Shuffled Round Robin'` alongside it after Steps 1–3, or is a comment. If a code hit lacks the new name, add it with the same pattern.

- [ ] **Step 5: Update docs**

Run `grep -rln 'Round Robin' docs/content/` to find the sorting docs page. Add a "Shuffled Round Robin" entry next to "Random Round Robin" describing: groups interleaved in random order AND items within each group shuffled; new arrangement each refresh; supports the same Group By field. Add an example to the page's example section:

```text
Shuffled Round Robin (Group By: Series Name) over shows A, B, C might produce:
A·S02E03, C·S01E01, B·S03E02, A·S01E01, C·S02E04, B·S01E05, ...
Both the show rotation and the episode order within each show are random.
```

- [ ] **Step 6: Build and verify manually**

Run: `cd dev && ./build-local.sh`
Then at <http://localhost:8096> (Dashboard → Plugins → Smart Lists): create an Episode playlist with 3+ series, sort "Shuffled Round Robin (Interleave)", Group By "Series Name". Verify: Sort Order dropdown hidden, Group By visible; after refresh the playlist interleaves series in random order with episodes out of natural order; refreshing again changes the arrangement. Also verify on the user page (user-playlists.html route).

- [ ] **Step 7: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/config-core.js Jellyfin.Plugin.SmartLists/Configuration/config-sorts.js Jellyfin.Plugin.SmartLists/Configuration/config-formatters.js docs/content/
git commit -m "Add Shuffled Round Robin sort option to UI and docs"
```

---

### Task 3: Bumper DTO, validation, mapping

**Files:**
- Create: `Core/Models/BumperConfigDto.cs`
- Modify: `Core/Models/SmartPlaylistDto.cs`
- Modify: `Utilities/InputValidator.cs` (`ValidateSmartList`, after the ExpressionSets block ~line 300)
- Modify: `Utilities/DtoMapper.cs` (`ToPlaylistDto`, after `RandomGroupSelection` mapping ~line 50)

**Interfaces:**
- Consumes: `ExpressionSet` (`Core/Models/ExpressionSet.cs`, same namespace), `InputValidator.ValidateExpressionSets`, `MaxExpressionSetsCount`, `Core.Constants.MediaTypes.All`.
- Produces: `SmartPlaylistDto.Bumpers` (`BumperConfigDto?`) with properties `ExpressionSets` (`List<ExpressionSet>`), `MediaTypes` (`List<string>`), `BumperOrder` (`string`, `"Random"|"Name"|"ReleaseDate"`), `Interval` (`int`, >= 1). Tasks 4 and 6 rely on these exact names.

- [ ] **Step 1: Create `Core/Models/BumperConfigDto.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartLists.Core.Models
{
    /// <summary>
    /// Configuration for bumper items woven between a smart playlist's main items.
    /// The bumper pool is selected by its own rule sets and media types, ordered by
    /// <see cref="BumperOrder"/>, and one bumper is inserted after every
    /// <see cref="Interval"/> main items, cycling through the pool with wraparound.
    /// </summary>
    [Serializable]
    public class BumperConfigDto
    {
        public List<ExpressionSet> ExpressionSets { get; set; } = [];

        public List<string> MediaTypes { get; set; } = [];

        /// <summary>
        /// Order of the bumper pool: "Random" (reshuffled each refresh), "Name", or "ReleaseDate".
        /// </summary>
        public string BumperOrder { get; set; } = "Random";

        public int Interval { get; set; } = 1;
    }
}
```

- [ ] **Step 2: Add the property to `SmartPlaylistDto.cs`**

After the `UserPlaylists` property (before the nested `UserPlaylistMapping` class):

```csharp
        /// <summary>
        /// Optional bumper configuration: items matching these rules are woven between
        /// the playlist's main items at refresh time. Null = feature disabled.
        /// Bumpers do not count toward MaxItems/MaxPlayTimeMinutes.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BumperConfigDto? Bumpers { get; set; }
```

- [ ] **Step 3: Validate in `InputValidator.ValidateSmartList`**

Immediately after the existing ExpressionSets validation block (the one ending `return expressionResult;` ~line 300), add:

```csharp
            // Validate bumper configuration (playlists only)
            if (list is SmartPlaylistDto playlistDto && playlistDto.Bumpers != null)
            {
                var bumpers = playlistDto.Bumpers;

                if (bumpers.Interval < 1)
                {
                    return SmartListValidationResult.Failure("Bumper interval must be at least 1");
                }

                if (bumpers.MediaTypes != null && bumpers.MediaTypes.Count > 0)
                {
                    var validBumperMediaTypes = Core.Constants.MediaTypes.All;
                    var invalidBumperMediaTypes = bumpers.MediaTypes
                        .Where(mt => !validBumperMediaTypes.Contains(mt))
                        .ToList();

                    if (invalidBumperMediaTypes.Count > 0)
                    {
                        return SmartListValidationResult.Failure($"Invalid bumper media type(s): {string.Join(", ", invalidBumperMediaTypes)}");
                    }
                }

                if (bumpers.ExpressionSets != null)
                {
                    if (bumpers.ExpressionSets.Count > MaxExpressionSetsCount)
                    {
                        return SmartListValidationResult.Failure($"Bumpers cannot have more than {MaxExpressionSetsCount} rule groups");
                    }

                    var bumperExpressionResult = ValidateExpressionSets(bumpers.ExpressionSets);
                    if (!bumperExpressionResult.IsValid)
                    {
                        return bumperExpressionResult;
                    }
                }
            }
```

Add `using Jellyfin.Plugin.SmartLists.Core.Models;` to `InputValidator.cs` if not already present.

- [ ] **Step 4: Map in `DtoMapper.ToPlaylistDto`**

After the `RandomGroupSelection = source.RandomGroupSelection,` line (~line 50):

```csharp
                Bumpers = (source as SmartPlaylistDto)?.Bumpers,
```

Do NOT touch `ToCollectionDto`.

- [ ] **Step 5: Build**

Run: `cd dev && ./build-local.sh`
Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Core/Models/BumperConfigDto.cs Jellyfin.Plugin.SmartLists/Core/Models/SmartPlaylistDto.cs Jellyfin.Plugin.SmartLists/Utilities/InputValidator.cs Jellyfin.Plugin.SmartLists/Utilities/DtoMapper.cs
git commit -m "Add BumperConfigDto with validation and mapping"
```

---

### Task 4: Bumper weave — backend

**Files:**
- Modify: `Services/Playlists/PlaylistService.cs` (lines ~179–196 restructure; two new private methods)

**Interfaces:**
- Consumes: `dto.Bumpers` from Task 3; existing `SmartList(SmartPlaylistDto)` ctor, private `GetAllUserMedia(user, mediaTypes, dto, extraOwnerMap)`, `FilterPlaylistItems(...)`, `LinkedChildFactory.Create`.
- Produces: woven `LinkedChildren` written to the Jellyfin playlist. No public surface changes; `RefreshQueueService` needs no new constructor threading.

- [ ] **Step 1: Restructure the LinkedChildren build in `ProcessPlaylist`**

Replace lines ~179–187 (from `// Create a lookup dictionary` through the `newLinkedChildren` assignment):

```csharp
                // Create a lookup dictionary for O(1) access while preserving order from newItems
                var mediaLookup = allUserMedia
                    .GroupBy(m => m.Id)
                    .ToDictionary(g => g.Key, g => g.First());

                var mainItemIds = newItems
                    .Distinct()
                    .Where(itemId => mediaLookup.ContainsKey(itemId))
                    .ToList();

                // Weave bumper items between main items. Bumpers may legitimately repeat,
                // so the woven list must NOT be deduplicated.
                var finalItemIds = mainItemIds;
                if (dto.Bumpers != null && dto.Bumpers.ExpressionSets != null && dto.Bumpers.ExpressionSets.Count > 0 && mainItemIds.Count > 0)
                {
                    var bumperItemIds = GetBumperItemIds(dto, user, refreshCache, mainItemIds, mediaLookup, logger);
                    if (bumperItemIds.Count > 0)
                    {
                        finalItemIds = WeaveBumpers(mainItemIds, bumperItemIds, Math.Max(1, dto.Bumpers.Interval));
                        logger.LogDebug("Wove bumpers into playlist '{PlaylistName}' every {Interval} item(s): {MainCount} main + {BumperPool} pool -> {TotalCount} entries",
                            dto.Name, dto.Bumpers.Interval, mainItemIds.Count, bumperItemIds.Count, finalItemIds.Count);
                    }
                    else
                    {
                        logger.LogDebug("Bumper rules matched no items for playlist '{PlaylistName}'; writing playlist without bumpers", dto.Name);
                    }
                }

                var newLinkedChildren = finalItemIds
                    .Select(itemId => LinkedChildFactory.Create(itemId, mediaLookup[itemId]))
                    .ToArray();
```

The statistics lines that follow (`dto.ItemCount = ...`, `dto.TotalRuntimeMinutes = ...`) stay unchanged — they now naturally include bumpers, which is correct (they are real playlist content).

- [ ] **Step 2: Add `GetBumperItemIds` private method**

Add near the other private helpers in `PlaylistService`:

```csharp
        /// <summary>
        /// Builds the ordered bumper item pool by running the playlist's bumper rule sets
        /// through a synthetic SmartList. Items already in the main list are excluded so an
        /// item is never both content and bumper. Bumper media is added to
        /// <paramref name="mediaLookup"/> so LinkedChildren can be created for woven entries.
        /// </summary>
        private List<Guid> GetBumperItemIds(
            SmartPlaylistDto dto,
            User user,
            RefreshQueueService.RefreshCache refreshCache,
            List<Guid> mainItemIds,
            Dictionary<Guid, BaseItem> mediaLookup,
            ILogger logger)
        {
            try
            {
                var bumpers = dto.Bumpers!;
                var sortOption = bumpers.BumperOrder switch
                {
                    "Name" => new SortOption { SortBy = "Name", SortOrder = SortOrder.Ascending },
                    "ReleaseDate" => new SortOption { SortBy = "ReleaseDate", SortOrder = SortOrder.Ascending },
                    _ => new SortOption { SortBy = "Random", SortOrder = SortOrder.Ascending },
                };

                var bumperDto = new SmartPlaylistDto
                {
                    Name = dto.Name + " (Bumpers)",
                    ExpressionSets = bumpers.ExpressionSets,
                    MediaTypes = bumpers.MediaTypes ?? [],
                    Order = new OrderDto { SortOptions = [sortOption] },
                };

                var bumperList = new SmartList(bumperDto);
                var bumperMedia = GetAllUserMedia(user, bumperDto.MediaTypes, bumperDto).ToArray();
                var bumperIds = bumperList.FilterPlaylistItems(bumperMedia, _libraryManager, user, refreshCache, _userDataManager, logger, null);

                var mainSet = new HashSet<Guid>(mainItemIds);
                var bumperMediaLookup = bumperMedia
                    .GroupBy(m => m.Id)
                    .ToDictionary(g => g.Key, g => g.First());

                var result = new List<Guid>();
                foreach (var id in bumperIds)
                {
                    if (mainSet.Contains(id) || !bumperMediaLookup.TryGetValue(id, out var item))
                    {
                        continue;
                    }

                    result.Add(id);
                    mediaLookup.TryAdd(id, item);
                }

                return result;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to build bumper pool for playlist '{PlaylistName}'. Playlist will be written without bumpers.", dto.Name);
                return [];
            }
        }
```

Add usings if missing: `Jellyfin.Plugin.SmartLists.Core.Enums` (for `SortOrder`). `SortOption`/`OrderDto` are in `Core.Models` (likely already imported). Match the actual `GetAllUserMedia` overload — the 3-arg private form `GetAllUserMedia(user, mediaTypes, dto)` exists at ~line 968. If the compiler picks the wrong overload, call with named args. If CA analyzers demand a static method (CA1822 — it won't here since `_libraryManager` is used), leave as instance.

- [ ] **Step 3: Add `WeaveBumpers` private method**

```csharp
        /// <summary>
        /// Inserts one bumper after every <paramref name="interval"/> main items, cycling
        /// through the bumper pool with wraparound. No bumper follows the final main item.
        /// Main item order is never altered.
        /// </summary>
        internal static List<Guid> WeaveBumpers(List<Guid> mainItemIds, List<Guid> bumperItemIds, int interval)
        {
            var result = new List<Guid>(mainItemIds.Count + (mainItemIds.Count / interval) + 1);
            int bumperIndex = 0;
            for (int i = 0; i < mainItemIds.Count; i++)
            {
                result.Add(mainItemIds[i]);
                bool isLast = i == mainItemIds.Count - 1;
                if (!isLast && (i + 1) % interval == 0)
                {
                    result.Add(bumperItemIds[bumperIndex % bumperItemIds.Count]);
                    bumperIndex++;
                }
            }

            return result;
        }
```

- [ ] **Step 4: Build**

Run: `cd dev && ./build-local.sh`
Expected: build succeeds for both targets.

- [ ] **Step 5: Verify weave via API (UI not built yet)**

The UI does not exist yet, so exercise via the plugin API. Get an existing smart playlist's JSON (admin page network tab, or the store file under `dev/jellyfin-data/config/data/smartlists/`), add a `"Bumpers"` block by editing the playlist JSON file directly, e.g.:

```json
"Bumpers": {
    "ExpressionSets": [ { "Expressions": [ { "MemberName": "Name", "Operator": "Contains", "TargetValue": "bumper" } ] } ],
    "MediaTypes": ["Video"],
    "BumperOrder": "Random",
    "Interval": 1
}
```

(Adjust the rule to match real items in the dev library.) Restart the container or trigger a refresh from the admin page. Verify in Jellyfin: playlist alternates main items and bumpers; with fewer bumpers than gaps, bumpers repeat; log line `Wove bumpers into playlist` appears in `docker logs jellyfin 2>&1 | grep -i "Smart"`.

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Services/Playlists/PlaylistService.cs
git commit -m "Weave bumper items into playlists after sorting and limits"
```

---

### Task 5: Rule editor container-scoping refactor (zero behavior change)

**Files:**
- Modify: `Configuration/config-core.js` (`getSelectedMediaTypes` ~line 142; new `getRulesContainer` helper; `hasSimilarToRuleInForm` ~line 147)
- Modify: `Configuration/config-rules.js` (container sites at ~1056, 1415, 1668, 1737, 1773, 1819, 1843; `collectRulesFromForm` ~2733; `populateFieldSelect` ~2038; any page-wide `.rule-row` iteration)
- Modify: `Configuration/config-lists.js` (SimilarTo scan ~line 374)

**Interfaces:**
- Consumes: nothing new.
- Produces: `SmartLists.getRulesContainer(page, scope)`; `createInitialLogicGroup(page, scope)` / `addNewLogicGroup(page, scope)` stamping `data-rule-scope` on logic groups; `collectRulesFromForm(page, scope)`; `getSelectedMediaTypes(page, scope)` where `scope === 'bumper'` reads `#bumperMediaType`. Task 6 calls all of these with `'bumper'`. Omitted scope = `'main'` = today's behavior, byte-identical.

- [ ] **Step 1: Add the container helper to `config-core.js`**

Near `getSelectedMediaTypes`:

```javascript
    // Resolve the rules container for a scope: 'main' (default) or 'bumper'
    SmartLists.getRulesContainer = function (page, scope) {
        return page.querySelector(scope === 'bumper' ? '#bumper-rules-container' : '#rules-container');
    };
```

- [ ] **Step 2: Scope `getSelectedMediaTypes`**

Replace the existing function (~line 142):

```javascript
    SmartLists.getSelectedMediaTypes = function (page, scope) {
        if (scope === 'bumper') {
            var bumperSelect = page.querySelector('#bumperMediaType');
            return (bumperSelect && bumperSelect.value) ? [bumperSelect.value] : [];
        }
        return SmartLists.getSelectedItems(page, 'mediaTypesMultiSelect', 'media-type-multi-select-checkbox');
    };
```

(`#bumperMediaType` does not exist until Task 6 — `scope` is never `'bumper'` until then, so behavior is unchanged.)

- [ ] **Step 3: Scope the page-level entry points in `config-rules.js`**

`createInitialLogicGroup` (~line 1055) and `addNewLogicGroup` (~line 1414): add a `scope` parameter, resolve the container through the helper, and stamp the created logic group:

```javascript
    SmartLists.createInitialLogicGroup = function (page, scope) {
        const rulesContainer = SmartLists.getRulesContainer(page, scope);
        ...
        logicGroup.setAttribute('data-rule-scope', scope === 'bumper' ? 'bumper' : 'main');
```

(Same two-line pattern in `addNewLogicGroup`. The `setAttribute` goes right after the logic group element is created, before it is appended.)

- [ ] **Step 4: Derive container from the node in group-level functions**

In `cloneLogicGroup` (~1668), `moveLogicGroup` (~1737), `removeLogicGroup` (~1773), replace:

```javascript
        const rulesContainer = page.querySelector('#rules-container');
```

with:

```javascript
        const rulesContainer = logicGroup.closest('#rules-container, #bumper-rules-container') || page.querySelector('#rules-container');
```

In `cloneLogicGroup`, also copy the scope stamp onto the clone if the implementation creates a fresh element rather than `cloneNode` (with `cloneNode(true)` the attribute copies automatically).

- [ ] **Step 5: Make visibility updaters iterate both containers**

`updateLogicGroupMoveButtonVisibility` (~1819) and `updateRuleButtonVisibility` (~1843) currently operate on the single main container. Wrap each function's existing body in a per-container loop so first/last logic stays per-container:

```javascript
    SmartLists.updateLogicGroupMoveButtonVisibility = function (page) {
        ['#rules-container', '#bumper-rules-container'].forEach(function (sel) {
            const rulesContainer = page.querySelector(sel);
            if (!rulesContainer) { return; }
            // ... existing body, unchanged, using rulesContainer ...
        });
    };
```

(Behavior identical while `#bumper-rules-container` doesn't exist.)

- [ ] **Step 6: Scope `collectRulesFromForm`**

Change the signature to `(page, scope)`, use scoped media types, and scope the logic-group query:

```javascript
    SmartLists.collectRulesFromForm = function (page, scope) {
        const expressionSets = [];
        const container = SmartLists.getRulesContainer(page, scope);
        if (!container) { return expressionSets; }
        const selectedMediaTypes = SmartLists.getSelectedMediaTypes(page, scope);
        ...
        container.querySelectorAll('.logic-group').forEach(function (logicGroup) {
```

(The rest of the function body is unchanged. Today `.logic-group` elements only exist inside `#rules-container`, so scoping the query is behavior-neutral.)

- [ ] **Step 7: Scope field filtering in `populateFieldSelect` and rule-row creation**

`addRuleToGroup` (~1079): derive scope from the group and pass it down to every `populateFieldSelect` call it makes:

```javascript
        const scope = logicGroup.getAttribute('data-rule-scope') || 'main';
```

`populateFieldSelect` (~2038): add trailing `scope` parameter and use it:

```javascript
    SmartLists.populateFieldSelect = function (selectElement, fieldGroups, defaultValue, page, scope) {
        const selectedMediaTypes = page ? SmartLists.getSelectedMediaTypes(page, scope) : [];
```

For any other `populateFieldSelect` caller (find with `grep -n 'populateFieldSelect(' Jellyfin.Plugin.SmartLists/Configuration/*.js`): derive scope from the nearest rule row / logic group — `ruleRow.closest('.logic-group')` then `getAttribute('data-rule-scope') || 'main'` — and pass it. Callers with no DOM context pass nothing (defaults to main behavior).

- [ ] **Step 8: Pin page-wide `.rule-row` scans to the main editor**

These helpers drive main-form behavior (sort option visibility, similarity fields) and must not react to bumper rules:

- `config-core.js` `hasSimilarToRuleInForm` (~147): `page.querySelectorAll('.rule-row')` → `page.querySelectorAll('#rules-container .rule-row')`.
- `config-lists.js` SimilarTo scan (~374): same replacement.
- Find remaining page-wide scans: `grep -n "querySelectorAll('.rule-row')" Jellyfin.Plugin.SmartLists/Configuration/*.js`. For each: if it drives main-form state (similarity, external-list detection, sort visibility) pin to `#rules-container .rule-row`; if it refreshes per-row field options on media-type change (e.g. the update-all functions around config-rules.js ~2093 and ~2552), keep it page-wide but resolve each row's scope via `row.closest('.logic-group')` attribute and call `getSelectedMediaTypes(page, rowScope)` so bumper rows will use bumper media types.

- [ ] **Step 9: Build and regression-verify the editor**

Run: `cd dev && ./build-local.sh`
On BOTH the admin page and the user page, verify unchanged behavior: create a playlist with 2 rule groups and 3 rules; clone a rule; clone a group; move groups up/down; remove a rule and a group; switch media types and confirm field dropdowns refilter; save; re-open for edit and confirm rules populate; save again with no diff in behavior. Any deviation = fix before committing.

- [ ] **Step 10: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/config-core.js Jellyfin.Plugin.SmartLists/Configuration/config-rules.js Jellyfin.Plugin.SmartLists/Configuration/config-lists.js
git commit -m "Parameterize rule editor by container scope (no behavior change)"
```

---

### Task 6: Bumper UI

**Files:**
- Modify: `Configuration/config.html` (after the sorts section, ~line 149)
- Modify: `Configuration/user-playlists.html` (after the sorts section, ~line 135)
- Modify: `Configuration/config-lists.js` (save build ~line 393; edit-populate paths ~lines 860 and 1138; list-type visibility)
- Modify: `Configuration/config-rules.js` (new `collectBumperConfigFromForm`; wire "Add Bumper Rule Group" button)

**Interfaces:**
- Consumes: Task 5's scoped functions (`createInitialLogicGroup(page, 'bumper')`, `addNewLogicGroup(page, 'bumper')`, `collectRulesFromForm(page, 'bumper')`, `getSelectedMediaTypes(page, 'bumper')`); Task 3's DTO shape.
- Produces: `SmartLists.collectBumperConfigFromForm(page)` returning `BumperConfigDto`-shaped object or null; `#bumper-section`, `#bumperMediaType`, `#bumperOrder`, `#bumperInterval`, `#bumper-rules-container`, `#add-bumper-rule-group` element IDs in both HTML pages.

- [ ] **Step 1: Add the Bumpers section to BOTH HTML pages**

Insert directly after the sorting section's closing `</div>` (`#sorts-container`'s `inputContainer`, config.html ~line 149, user-playlists.html ~line 135). Copy the exact `<select>`/`<input>` attribute pattern from an existing select in the same file (e.g. the `#listType` or sort selects) — same `is=`/class attributes; remember never `is="emby-input"`:

```html
<div id="bumper-section" class="inputContainer" style="margin-bottom: 1em;">
    <label class="inputLabel">Bumpers (optional)</label>
    <div class="fieldDescription" style="margin-bottom: 0.5em;">
        Insert short "bumper" items (commercials, interstitials, idents) between playlist items.
        Bumpers are selected by their own rules, repeat if fewer than needed, and do not count toward Max Items.
    </div>
    <div style="display: flex; gap: 1em; flex-wrap: wrap; margin-bottom: 0.5em;">
        <div class="selectContainer" style="min-width: 180px;">
            <label class="selectLabel" for="bumperMediaType">Bumper media type</label>
            <select id="bumperMediaType" class="emby-select-withcolor emby-select">
                <option value="">None (disabled)</option>
                <option value="Movie">Movies</option>
                <option value="Video">Home Videos</option>
                <option value="Episode">Episodes</option>
                <option value="Audio">Music</option>
                <option value="MusicVideo">Music Videos</option>
            </select>
        </div>
        <div class="selectContainer bumper-detail" style="min-width: 160px;">
            <label class="selectLabel" for="bumperOrder">Bumper order</label>
            <select id="bumperOrder" class="emby-select-withcolor emby-select">
                <option value="Random">Random</option>
                <option value="Name">Name</option>
                <option value="ReleaseDate">Release Date</option>
            </select>
        </div>
        <div class="inputContainer bumper-detail" style="min-width: 120px;">
            <label class="inputLabel" for="bumperInterval">Every N items</label>
            <input type="number" class="emby-input" id="bumperInterval" min="1" value="1" />
        </div>
    </div>
    <div id="bumper-rules-container" class="bumper-detail"></div>
    <button type="button" id="add-bumper-rule-group" class="raised emby-button bumper-detail" style="margin-top: 0.5em;">
        <span>Add Bumper Rule Group</span>
    </button>
</div>
```

- [ ] **Step 2: Add `collectBumperConfigFromForm` to `config-rules.js`**

Near `collectRulesFromForm`:

```javascript
    // Collect the bumper configuration from the form. Returns null when disabled
    // (no media type selected or no bumper rules defined).
    SmartLists.collectBumperConfigFromForm = function (page) {
        var mediaTypeSelect = page.querySelector('#bumperMediaType');
        if (!mediaTypeSelect || !mediaTypeSelect.value) {
            return null;
        }
        var expressionSets = SmartLists.collectRulesFromForm(page, 'bumper');
        if (!expressionSets || expressionSets.length === 0) {
            return null;
        }
        var intervalInput = page.querySelector('#bumperInterval');
        var interval = intervalInput ? parseInt(intervalInput.value, 10) : 1;
        var orderSelect = page.querySelector('#bumperOrder');
        return {
            ExpressionSets: expressionSets,
            MediaTypes: [mediaTypeSelect.value],
            BumperOrder: (orderSelect && orderSelect.value) ? orderSelect.value : 'Random',
            Interval: (isNaN(interval) || interval < 1) ? 1 : interval
        };
    };
```

- [ ] **Step 3: Include bumpers on save in `config-lists.js`**

Next to `var randomGroupSelection = SmartLists.collectRandomGroupSelectionFromForm(page);` (~line 391):

```javascript
            var bumperConfig = isCollection ? null : SmartLists.collectBumperConfigFromForm(page);
```

And in the `playlistDto` object literal (~line 394), after `Order: { SortOptions: sortOptions },`:

```javascript
                Bumpers: bumperConfig,
```

- [ ] **Step 4: Populate bumpers on edit/clone in `config-lists.js`**

Both populate paths (edit ~line 1138, clone ~line 860) clear and rebuild `#rules-container` from `playlist.ExpressionSets`. Mirror that block immediately after it, for bumpers:

```javascript
                // Clear and populate bumper rules
                const bumperRulesContainer = page.querySelector('#bumper-rules-container');
                if (bumperRulesContainer) {
                    bumperRulesContainer.innerHTML = '';
                }
                const bumperMediaSelect = page.querySelector('#bumperMediaType');
                const bumperOrderSelect = page.querySelector('#bumperOrder');
                const bumperIntervalInput = page.querySelector('#bumperInterval');
                if (playlist.Bumpers && playlist.Bumpers.ExpressionSets && playlist.Bumpers.ExpressionSets.length > 0) {
                    if (bumperMediaSelect) {
                        bumperMediaSelect.value = (playlist.Bumpers.MediaTypes && playlist.Bumpers.MediaTypes.length > 0) ? playlist.Bumpers.MediaTypes[0] : '';
                    }
                    if (bumperOrderSelect) { bumperOrderSelect.value = playlist.Bumpers.BumperOrder || 'Random'; }
                    if (bumperIntervalInput) { bumperIntervalInput.value = playlist.Bumpers.Interval || 1; }
                    playlist.Bumpers.ExpressionSets.forEach(function (expressionSet, setIndex) {
                        const logicGroup = setIndex === 0 ? SmartLists.createInitialLogicGroup(page, 'bumper') : SmartLists.addNewLogicGroup(page, 'bumper');
                        // Populate each expression into the group exactly as the main-rules
                        // populate loop directly above does (first rule row reused, subsequent
                        // rows via addRuleToGroup, then populateRuleRow per expression).
                        SmartLists.populateLogicGroupExpressions(page, logicGroup, expressionSet);
                    });
                } else {
                    if (bumperMediaSelect) { bumperMediaSelect.value = ''; }
                    if (bumperOrderSelect) { bumperOrderSelect.value = 'Random'; }
                    if (bumperIntervalInput) { bumperIntervalInput.value = 1; }
                }
                SmartLists.updateBumperSectionVisibility(page);
```

If no `populateLogicGroupExpressions`-style helper exists, extract one from the main-rules populate loop first (both main and bumper paths then call it) — do not duplicate the ~20-line expression-populate loop.

- [ ] **Step 5: Wire visibility + buttons (init code shared by both pages)**

In the page-init code where other form listeners are attached (`config-init.js` for admin — find the block near its `#rules-container` reference at ~line 81 — and the user page's equivalent init; if a shared form-init function exists in `config-lists.js`, put it there once):

```javascript
    // Show bumper detail controls only when a bumper media type is chosen;
    // hide the whole section for collections.
    SmartLists.updateBumperSectionVisibility = function (page) {
        var section = page.querySelector('#bumper-section');
        if (!section) { return; }
        var listType = SmartLists.getElementValue(page, '#listType', 'Playlist');
        section.style.display = (listType === 'Collection') ? 'none' : '';
        var mediaTypeSelect = page.querySelector('#bumperMediaType');
        var enabled = !!(mediaTypeSelect && mediaTypeSelect.value);
        section.querySelectorAll('.bumper-detail').forEach(function (el) {
            el.style.display = enabled ? '' : 'none';
        });
    };
```

Listeners:

```javascript
        var bumperMediaTypeSelect = page.querySelector('#bumperMediaType');
        if (bumperMediaTypeSelect) {
            bumperMediaTypeSelect.addEventListener('change', function () {
                SmartLists.updateBumperSectionVisibility(page);
                // Create the first rule group on first enable; refilter fields on type change
                var container = page.querySelector('#bumper-rules-container');
                if (this.value && container && container.children.length === 0) {
                    SmartLists.createInitialLogicGroup(page, 'bumper');
                }
            });
        }
        var addBumperGroupBtn = page.querySelector('#add-bumper-rule-group');
        if (addBumperGroupBtn) {
            addBumperGroupBtn.addEventListener('click', function () {
                SmartLists.addNewLogicGroup(page, 'bumper');
            });
        }
```

Also call `SmartLists.updateBumperSectionVisibility(page)` wherever `#listType` changes are already handled (grep `#listType` change listeners) and once at form init/reset. The user page has no `#listType` (playlists only) — the `getElementValue` default `'Playlist'` keeps the section visible there.

- [ ] **Step 6: Build and verify end-to-end**

Run: `cd dev && ./build-local.sh`
Admin page: create an Episode playlist; pick bumper media type "Home Videos"; a bumper rule group appears; add rule `Name Contains bumper` (match dev-library items); order Random, interval 2; save; refresh playlist; verify in Jellyfin the playlist has a bumper after every 2 episodes, repeating. Re-open for edit: bumper config populates. Set media type back to "None (disabled)", save, verify `Bumpers` gone from stored JSON and playlist refreshes without bumpers. Switch `#listType` to Collection: section hides, saved collection has no `Bumpers`. Repeat create/edit on the user page.

- [ ] **Step 7: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/config.html Jellyfin.Plugin.SmartLists/Configuration/user-playlists.html Jellyfin.Plugin.SmartLists/Configuration/config-lists.js Jellyfin.Plugin.SmartLists/Configuration/config-rules.js Jellyfin.Plugin.SmartLists/Configuration/config-init.js
git commit -m "Add bumper configuration UI to playlist forms"
```

---

### Task 7: Docs + final regression

**Files:**
- Create: bumpers page under `docs/content/` (match the nav structure in `docs/mkdocs.yml` or equivalent — check how existing feature pages register)
- Modify: docs examples section (per repo convention: examples go in the example sections)

**Interfaces:**
- Consumes: everything shipped in Tasks 1–6.
- Produces: user-facing documentation; verified release-ready branch.

- [ ] **Step 1: Write the bumpers docs page**

New page describing: what bumpers are; the bumper rule editor (own media type + rules); order options (Random/Name/Release Date); "Every N items"; repeat-with-wraparound; bumpers excluded from Max Items/max play time but included in the playlist's item count and runtime stats; an item matching both main rules and bumper rules is treated as main content only. Add a worked example mirroring the feature request (3 shows + 4 bumpers, showing wraparound). Register the page in the mkdocs nav where other feature pages are registered.

- [ ] **Step 2: Full regression pass**

Run: `cd dev && ./build-local.sh`, then verify against local Jellyfin:
1. Shuffled Round Robin playlist reshuffles per refresh (shows AND episodes).
2. Bumper playlist weaves + repeats; `Random` bumper order changes per refresh.
3. Pre-existing playlists (Round Robin Asc/Desc, Random Round Robin, rule blocks with per-group MaxItems, IncludeCollectionOnly-only, MaxItems + MaxPlayTime) refresh with unchanged results.
4. Collections: create/edit/refresh unaffected; no bumper UI.
5. Rule editor (both pages): create/clone/move/remove groups and rules — no regressions.

- [ ] **Step 3: Commit**

```bash
git add docs/
git commit -m "Document bumpers feature and Shuffled Round Robin examples"
```
