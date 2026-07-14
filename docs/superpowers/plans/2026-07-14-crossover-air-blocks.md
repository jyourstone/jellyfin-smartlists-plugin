# Crossover Air Blocks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When round robin groups by Collections with Air Date order, interleave one *air block* (episodes that aired within N days of each other, different shows) per cycle instead of one episode, so same-night crossovers play back-to-back. Window configurable per sort option, default 3 days.

**Architecture:** Blocking lives entirely in `RoundRobinBase.BuildInterleavedPositions` (a new `ChunkIntoAirBlocks` helper chunks each air-date-sorted group; the interleave loop iterates blocks instead of items). A new nullable `AirBlockWindowDays` on `SortOption` threads through `SmartList.InitializeFromDto` onto the order instance. UI is one number input in the JS-generated sort box (`config-sorts.js` only — no HTML changes).

**Tech Stack:** C# (.NET multi-target net9.0/net10.0), vanilla ES5-style JS (Jellyfin plugin config page).

**Spec:** `docs/superpowers/specs/2026-07-14-crossover-air-blocks-design.md`

## Global Constraints

- Build treats all warnings as errors (`AnalysisMode=Recommended`); all new public/internal members need XML doc comments.
- Both target frameworks must build clean: `dotnet build` builds net9.0 + net10.0.
- No test suite exists — verification is a clean build per task plus the e2e pass in Task 5.
- JS: **no ES6 template literals**; **never** `is="emby-input"` (use `class="emby-input"`); string concatenation only.
- Every commit message ends with the two trailer lines shown in each commit step (Co-Authored-By + Claude-Session).
- Blocking gate: active only when collection grouping (`CollectionGroupKeys != null`) AND air-date within-group order AND not shuffled. Inactive → positions byte-identical to current behavior.
- Window semantics: null DTO value = default 3; clamp to [0, 30]; 0 = same-day only.

---

### Task 1: Backend blocking algorithm (`RoundRobinOrder.cs`)

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Core/Orders/RoundRobinOrder.cs`

**Interfaces:**
- Consumes: existing `OrderUtilities.GetReleaseDate(BaseItem)` (returns `DateTime`, `DateTime.MinValue` when unknown), existing group dictionaries inside `BuildInterleavedPositions`.
- Produces: `public const int DefaultAirBlockWindowDays = 3` and `public int AirBlockWindowDays { get; set; }` on `RoundRobinBase` (Task 2 threads the DTO value into this property); `internal static List<List<BaseItem>> ChunkIntoAirBlocks(List<BaseItem> group, int windowDays)`; `BuildInterleavedPositions` gains trailing parameter `int airBlockWindowDays = DefaultAirBlockWindowDays`.

- [ ] **Step 1: Add window constant + property to `RoundRobinBase`**

In `Jellyfin.Plugin.SmartLists/Core/Orders/RoundRobinOrder.cs`, directly after the `OrderWithinGroupsByAirDate` property (line ~42), add:

```csharp
        /// <summary>
        /// Default window (days) for chaining episodes into air blocks.
        /// </summary>
        public const int DefaultAirBlockWindowDays = 3;

        /// <summary>
        /// Window in days for chaining episodes of different shows into one interleave block,
        /// active only when grouping by Collections with air-date within-group order.
        /// 0 means same-day only. Set from SortOption.AirBlockWindowDays (null = default).
        /// </summary>
        public int AirBlockWindowDays { get; set; } = DefaultAirBlockWindowDays;
```

- [ ] **Step 2: Pass the window through `PreComputePositions`**

Replace (line ~68):

```csharp
            ItemPositions = BuildInterleavedPositions(items, GroupByField, OrderGroupKeys, Name, logger, ShuffleWithinGroups, CollectionGroupKeys, OrderWithinGroupsByAirDate);
```

with:

```csharp
            ItemPositions = BuildInterleavedPositions(items, GroupByField, OrderGroupKeys, Name, logger, ShuffleWithinGroups, CollectionGroupKeys, OrderWithinGroupsByAirDate, AirBlockWindowDays);
```

- [ ] **Step 3: Extend `BuildInterleavedPositions` signature and interleave over blocks**

Add the trailing parameter to the signature (line ~108):

```csharp
        internal static ConcurrentDictionary<Guid, int> BuildInterleavedPositions(
            IEnumerable<BaseItem> items,
            string? groupByField,
            Func<IEnumerable<string>, List<string>> orderGroupKeys,
            string logPrefix,
            ILogger? logger,
            bool shuffleWithinGroups = false,
            Dictionary<Guid, string>? collectionGroupKeys = null,
            bool airDateWithinGroups = false,
            int airBlockWindowDays = DefaultAirBlockWindowDays)
```

Then replace the interleave section — everything from `var orderedKeys = orderGroupKeys(groups.Keys);` through the end of the `for (int level = ...)` loop (currently lines ~163-178):

```csharp
            var orderedKeys = orderGroupKeys(groups.Keys);

            int position = 0;
            int maxGroupSize = groups.Values.Max(g => g.Count);

            for (int level = 0; level < maxGroupSize; level++)
            {
                foreach (var groupKey in orderedKeys)
                {
                    var group = groups[groupKey];
                    if (level < group.Count)
                    {
                        positions[group[level].Id] = position++;
                    }
                }
            }
```

with:

```csharp
            var orderedKeys = orderGroupKeys(groups.Keys);

            // Chunk each group into "air blocks" when collection grouping uses air-date order;
            // otherwise every item is its own block, which reproduces the plain per-item interleave.
            bool useAirBlocks = collectionGroupKeys != null && airDateWithinGroups && !shuffleWithinGroups;
            int windowDays = Math.Clamp(airBlockWindowDays, 0, 30);
            var groupBlocks = new Dictionary<string, List<List<BaseItem>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in groups)
            {
                groupBlocks[kvp.Key] = useAirBlocks
                    ? ChunkIntoAirBlocks(kvp.Value, windowDays)
                    : kvp.Value.Select(i => new List<BaseItem> { i }).ToList();
            }

            int position = 0;
            int maxBlockCount = groupBlocks.Values.Max(b => b.Count);

            for (int level = 0; level < maxBlockCount; level++)
            {
                foreach (var groupKey in orderedKeys)
                {
                    var blocks = groupBlocks[groupKey];
                    if (level < blocks.Count)
                    {
                        foreach (var item in blocks[level])
                        {
                            positions[item.Id] = position++;
                        }
                    }
                }
            }
```

- [ ] **Step 4: Add `ChunkIntoAirBlocks` helper**

Add directly after the `BuildInterleavedPositions` method body (before `ExtractGroupKey`):

```csharp
        /// <summary>
        /// Chunks an air-date-sorted group into "air blocks": a block extends while the next
        /// item aired within <paramref name="windowDays"/> days of the previous one AND belongs
        /// to a show not already in the block. Same-night crossovers and franchise weeks stay
        /// together; solo-era episodes and items with no air date form blocks of one.
        /// </summary>
        internal static List<List<BaseItem>> ChunkIntoAirBlocks(List<BaseItem> group, int windowDays)
        {
            var blocks = new List<List<BaseItem>>();
            List<BaseItem>? current = null;
            HashSet<string>? currentShows = null;
            var prevDate = DateTime.MinValue;

            foreach (var item in group)
            {
                var date = OrderUtilities.GetReleaseDate(item).Date;
                var show = item is Episode blockEpisode
                    ? blockEpisode.SeriesName ?? string.Empty
                    : item.Name ?? string.Empty;

                var chains = current != null
                    && date > DateTime.MinValue
                    && prevDate > DateTime.MinValue
                    && (date - prevDate).TotalDays <= windowDays
                    && !currentShows!.Contains(show);

                if (!chains)
                {
                    current = new List<BaseItem>();
                    currentShows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    blocks.Add(current);
                }

                current!.Add(item);
                currentShows!.Add(show);
                prevDate = date;
            }

            return blocks;
        }
```

- [ ] **Step 5: Build both target frameworks**

Run from the repo root:

```bash
dotnet build Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj --no-incremental
```

Expected: `Build succeeded.` with `0 Warning(s)` and `0 Error(s)` for both net9.0 and net10.0.

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Core/Orders/RoundRobinOrder.cs
git commit -m "$(cat <<'EOF'
Interleave air blocks for collection round robin

When grouping by Collections with air-date within-group order, chunk each
group into blocks of episodes that aired within the window (default 3 days)
across different shows, and emit one block per rotation cycle. Same-night
crossovers now play back-to-back instead of a full cycle apart.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RBFLcyWKuktDSu9acy68zg
EOF
)"
```

---

### Task 2: DTO field + threading (`SortOption.cs`, `SmartList.cs`)

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Core/Models/SortOption.cs`
- Modify: `Jellyfin.Plugin.SmartLists/Core/SmartList.cs:126-130`

**Interfaces:**
- Consumes: `RoundRobinBase.AirBlockWindowDays` (int property) and `RoundRobinBase.DefaultAirBlockWindowDays` (const int, 3) from Task 1.
- Produces: `public int? AirBlockWindowDays { get; set; }` on `SortOption` — the JSON property name the frontend (Task 3) reads/writes is `AirBlockWindowDays`.

- [ ] **Step 1: Add nullable DTO property**

In `Jellyfin.Plugin.SmartLists/Core/Models/SortOption.cs`, after the `WithinGroupOrder` property, add:

```csharp
        /// <summary>
        /// Window in days for chaining episodes of different shows into one round-robin
        /// interleave block. Only used with GroupByField "Collections" and WithinGroupOrder
        /// "AirDate". Null = default (3). 0 = same-day only. Clamped to [0, 30] at use.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? AirBlockWindowDays { get; set; }
```

- [ ] **Step 2: Thread onto the order instance**

In `Jellyfin.Plugin.SmartLists/Core/SmartList.cs` (`InitializeFromDto`), replace:

```csharp
                        if (order is RoundRobinBase rr)
                        {
                            rr.GroupByField = so.GroupByField;
                            rr.OrderWithinGroupsByAirDate = string.Equals(so.WithinGroupOrder, "AirDate", StringComparison.OrdinalIgnoreCase);
                        }
```

with:

```csharp
                        if (order is RoundRobinBase rr)
                        {
                            rr.GroupByField = so.GroupByField;
                            rr.OrderWithinGroupsByAirDate = string.Equals(so.WithinGroupOrder, "AirDate", StringComparison.OrdinalIgnoreCase);
                            rr.AirBlockWindowDays = so.AirBlockWindowDays ?? RoundRobinBase.DefaultAirBlockWindowDays;
                        }
```

- [ ] **Step 3: Build**

```bash
dotnet build Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj --no-incremental
```

Expected: `Build succeeded.`, `0 Warning(s)`, both TFMs.

- [ ] **Step 4: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Core/Models/SortOption.cs Jellyfin.Plugin.SmartLists/Core/SmartList.cs
git commit -m "$(cat <<'EOF'
Add configurable AirBlockWindowDays to sort options

Nullable per-sort setting threaded onto round-robin orders; null keeps the
default 3-day window.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RBFLcyWKuktDSu9acy68zg
EOF
)"
```

---

### Task 3: Frontend number input (`config-sorts.js`)

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-sorts.js` (createSortBox ~lines 348-388, collectSortsFromForm ~lines 508-518)

**Interfaces:**
- Consumes: `sortData.AirBlockWindowDays` (int or absent) from the stored DTO (Task 2); existing `SmartLists.isRoundRobinSort`, `SmartLists.createStyledElement`, `SmartLists.STYLES.sortField`, `SmartLists.STYLES.sortFieldLabel`.
- Produces: `sortEntry.AirBlockWindowDays` (int, 0-30) in the JSON sent to the API, omitted when the input is blank or not applicable.

The sort UI is fully JS-generated — **no changes to `config.html` / `user-playlists.html`**. No new JS file, so no `.csproj` / `Plugin.cs` registration.

- [ ] **Step 1: Create the field in `createSortBox`**

In `createSortBox`, directly after `fieldsContainer.appendChild(withinGroupField.container);` (line ~358), add:

```javascript
        // Air window field (Collections grouping with Air Date order only)
        const airWindowContainer = SmartLists.createStyledElement('div', 'sort-field-container', SmartLists.STYLES.sortField);
        airWindowContainer.style.minWidth = '140px';
        airWindowContainer.style.maxWidth = '140px';
        const airWindowLabel = SmartLists.createStyledElement('label', '', SmartLists.STYLES.sortFieldLabel);
        airWindowLabel.textContent = 'Air Window (days)';
        airWindowLabel.setAttribute('for', 'sort-airwindow-' + sortId);
        airWindowContainer.appendChild(airWindowLabel);
        const airWindowInput = document.createElement('input');
        airWindowInput.type = 'number';
        airWindowInput.className = 'emby-input';
        airWindowInput.id = 'sort-airwindow-' + sortId;
        airWindowInput.min = '0';
        airWindowInput.max = '30';
        airWindowInput.step = '1';
        airWindowInput.placeholder = '3';
        airWindowInput.title = 'Episodes from different shows in the same collection that aired within this many days of each other play back-to-back. 0 = same day only.';
        if (sortData && sortData.AirBlockWindowDays !== undefined && sortData.AirBlockWindowDays !== null) {
            airWindowInput.value = sortData.AirBlockWindowDays;
        }
        airWindowContainer.appendChild(airWindowInput);
        fieldsContainer.appendChild(airWindowContainer);

        function updateAirWindowVisibility() {
            var showAirWindow = SmartLists.isRoundRobinSort(sortByField.input.value)
                && sortByField.input.value !== 'Shuffled Round Robin'
                && groupByField.input.value === 'Collections'
                && withinGroupField.input.value === 'AirDate';
            airWindowContainer.style.display = showAirWindow ? '' : 'none';
        }

        groupByField.input.addEventListener('change', updateAirWindowVisibility);
        withinGroupField.input.addEventListener('change', updateAirWindowVisibility);
```

- [ ] **Step 2: Hook Sort By changes and initial state**

In the existing `sortByField.input.addEventListener('change', ...)` handler (line ~373, now shifted down), add `updateAirWindowVisibility();` as the last line of the handler body:

```javascript
        sortByField.input.addEventListener('change', function() {
            SmartLists.syncSortOrderUI(this.value, sortOrderField.container, sortOrderField.input, groupByField.container, withinGroupField.container);
            // Show/hide ignore articles checkbox based on Sort By value
            const showIgnoreArticles = (this.value === 'Name' || this.value === 'SeriesName');
            ignoreArticlesField.container.style.display = showIgnoreArticles ? '' : 'none';
            // Reset checkbox when switching away from Name/SeriesName
            if (!showIgnoreArticles) {
                ignoreArticlesField.checkbox.checked = false;
            }
            updateAirWindowVisibility();
        });
```

And directly after the existing initialization call `SmartLists.syncSortOrderUI(actualSortBy, ...)` (line ~385), add:

```javascript
        updateAirWindowVisibility();
```

- [ ] **Step 3: Collect the value in `collectSortsFromForm`**

Replace the round-robin branch:

```javascript
            // Include GroupByField for Round Robin and Random Round Robin sorts
            if (SmartLists.isRoundRobinSort(sortBy)) {
                var groupBySelect = box.querySelector('[id^="sort-groupby-"]');
                if (groupBySelect && groupBySelect.value) {
                    sortEntry.GroupByField = groupBySelect.value;
                }
                var withinGroupSelect = box.querySelector('[id^="sort-withingroup-"]');
                if (withinGroupSelect && withinGroupSelect.value === 'AirDate' && sortBy !== 'Shuffled Round Robin') {
                    sortEntry.WithinGroupOrder = 'AirDate';
                }
            }
```

with:

```javascript
            // Include GroupByField for Round Robin and Random Round Robin sorts
            if (SmartLists.isRoundRobinSort(sortBy)) {
                var groupBySelect = box.querySelector('[id^="sort-groupby-"]');
                if (groupBySelect && groupBySelect.value) {
                    sortEntry.GroupByField = groupBySelect.value;
                }
                var withinGroupSelect = box.querySelector('[id^="sort-withingroup-"]');
                if (withinGroupSelect && withinGroupSelect.value === 'AirDate' && sortBy !== 'Shuffled Round Robin') {
                    sortEntry.WithinGroupOrder = 'AirDate';

                    var airWindowInput = box.querySelector('[id^="sort-airwindow-"]');
                    if (sortEntry.GroupByField === 'Collections' && airWindowInput && airWindowInput.value !== '') {
                        var airWindowDays = parseInt(airWindowInput.value, 10);
                        if (!isNaN(airWindowDays)) {
                            sortEntry.AirBlockWindowDays = Math.min(30, Math.max(0, airWindowDays));
                        }
                    }
                }
            }
```

- [ ] **Step 4: Build (JS is embedded — build validates packaging)**

```bash
dotnet build Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj --no-incremental
```

Expected: `Build succeeded.`, `0 Warning(s)`.

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/config-sorts.js
git commit -m "$(cat <<'EOF'
Add Air Window (days) input to round robin sort options

Visible only for Collections grouping with Air Date order. Blank input
keeps the default 3-day window; value is clamped to 0-30 on collect.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RBFLcyWKuktDSu9acy68zg
EOF
)"
```

---

### Task 4: Docs

**Files:**
- Modify: `docs/content/user-guide/sorting-and-limits.md:153`
- Modify: `docs/content/examples/common-use-cases.md:124`

**Interfaces:**
- Consumes: user-facing names from Task 3: setting label "Air Window (days)", default 3, 0 = same day only.
- Produces: nothing downstream.

- [ ] **Step 1: Extend the Order Within Group section**

In `docs/content/user-guide/sorting-and-limits.md`, after the paragraph ending "...once the timeline reaches its premiere." (line 153), add a new paragraph:

```markdown
When grouping by Collection with Air Date order, episodes that aired close together are also pulled into the rotation **as one block**: episodes from *different* shows in the collection that aired within the **Air Window** (default 3 days, 0 = same day only) play back-to-back instead of a full rotation cycle apart. Same-night crossovers stay together, and franchise weeks (one show Tuesday, the next Wednesday, the third Thursday) come as one run. Episodes of the same show never chain, so solo-era episodes still rotate one per cycle, and a block never contains more episodes than the collection has shows.
```

- [ ] **Step 2: Update the franchise recipe**

In `docs/content/examples/common-use-cases.md`, in the "Franchise TV Channel (crossovers in air-date order)" section, replace the Result paragraph (line ~124):

```markdown
Result: Each collection becomes one slot in the rotation and plays chronologically across its shows — crossovers stay in order, and a spinoff only starts appearing once the timeline reaches its premiere. Shows not in any collection rotate as themselves. Watch anything in a franchise and the whole franchise moves to the back of the rotation.
```

with:

```markdown
Result: Each collection becomes one slot in the rotation and plays chronologically across its shows — crossovers stay in order, and a spinoff only starts appearing once the timeline reaches its premiere. Episodes from different shows that aired within the **Air Window** (default 3 days) play back-to-back, so a crossover night comes as one run instead of being spread across rotation cycles. Shows not in any collection rotate as themselves. Watch anything in a franchise and the whole franchise moves to the back of the rotation.
```

- [ ] **Step 3: Commit**

```bash
git add docs/content/user-guide/sorting-and-limits.md docs/content/examples/common-use-cases.md
git commit -m "$(cat <<'EOF'
Document air blocks and the Air Window setting

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RBFLcyWKuktDSu9acy68zg
EOF
)"
```

---

### Task 5: E2E verification against local Jellyfin

**Files:** none (verification only). Run from the MAIN checkout's `dev/` directory after deploying the worktree build (see gotcha below).

**Deploy gotcha:** the Jellyfin container mounts the MAIN checkout's `../build_output`. Build the worktree code into that directory, then restart. Before testing, verify the deployed DLL contains the feature (macOS `strings` misses .NET UTF-16 literals):

```bash
dotnet build <worktree>/Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj --no-incremental -f net10.0 -o /Users/johan.yourstone/Git/jellyfin-smartplaylist-plugin/build_output
python3 -c "d=open('/Users/johan.yourstone/Git/jellyfin-smartplaylist-plugin/build_output/Jellyfin.Plugin.SmartLists.dll','rb').read(); print(d.count('Air Window (days)'.encode('utf-8')))"
docker restart jellyfin
```

Expected: python prints ≥ 1 (the label lives in the embedded `config-sorts.js` resource, which is UTF-8 inside the DLL; method names are not reliable markers).

- [ ] **Step 1: Crossover night pairs up.** Using the dev collection with staggered air dates (create one via API if absent: a collection containing two series where one episode of each shares a premiere date), create/refresh a playlist with Round Robin + Group By Collection + Order Within Group Air Date + other non-collection shows. Fetch the playlist items and confirm the same-date episodes from different shows are adjacent, while other groups still contribute one item per cycle.
- [ ] **Step 2: Window 0 = same-day only.** Edit the sort's Air Window to 0, refresh, confirm only exact same-date episodes pair; episodes 1-3 days apart no longer chain.
- [ ] **Step 3: Solo era unchanged.** Episodes of a single show (no same-window neighbor from another show) still appear one per cycle.
- [ ] **Step 4: Non-collection regression.** A Round Robin playlist with Group By Series Name produces the same order as before the change.
- [ ] **Step 5: Round-trip.** Edit the list in the UI (or GET the DTO): `AirBlockWindowDays` persists; blank input stores no property (null → default 3).
