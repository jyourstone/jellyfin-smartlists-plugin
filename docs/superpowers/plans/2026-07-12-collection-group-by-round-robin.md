# Collection Group By + Within-Group Order Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Round-robin sorts can group by Jellyfin collection (franchise = one rotation slot; episodes resolve membership via their series) and order items within each group by air date instead of season/episode.

**Architecture:** New `"Collections"` Group By value backed by an itemId→collection-name map that SmartList builds once per refresh from the unfiltered pool and injects into the round-robin orders — the exact pattern `GroupRecency` (LRW) already uses. A new nullable `WithinGroupOrder` DTO field selects a `CompareWithinGroupByAirDate` comparator inside `BuildInterleavedPositions`. LRW recency reuses the same map so a franchise carries one recency.

**Tech Stack:** C# (.NET multi-target net9.0/net10.0), Jellyfin plugin API, vanilla JS config pages.

**Spec:** `docs/superpowers/specs/2026-07-12-collection-group-by-round-robin-design.md`

## Global Constraints

- Build treats all warnings as errors (`AnalysisMode=Recommended`); both frameworks must build: `net10.0` and `net9.0`.
- Group By field value everywhere (backend + JS): exactly `Collections`. UI label: `Collection`. DTO property: `WithinGroupOrder`, values `Natural` (default, may be omitted/null) and `AirDate`.
- Config JS: no ES6 template literals; never `is="emby-input"`; user messages via `showNotification()`. NOTE: config-sorts.js uses `const`/arrow functions already — match the file's existing style.
- No test suite. Verification = build both targets + live checks against local Jellyfin (<http://localhost:8096>).
- Working tree: worktree at `.claude/worktrees/collection-group-by-round-robin`, branch `worktree-collection-group-by-round-robin`, based on main `0337526`.
- Line numbers below are anchors on that commit — locate by quoted code, not absolute number.
- Commit messages end with (blank line before):

  ```text
  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01RBFLcyWKuktDSu9acy68zg
  ```

---

### Task 1: DTO field + order-class mechanics (map consumption, air-date comparator)

**Files:**

- Modify: `Jellyfin.Plugin.SmartLists/Core/Models/SortOption.cs`
- Modify: `Jellyfin.Plugin.SmartLists/Core/Orders/RoundRobinOrder.cs`
- Modify: `Jellyfin.Plugin.SmartLists/Core/SmartList.cs` (only the `InitializeFromDto` round-robin branch, ~line 126)

**Interfaces:**

- Consumes: `OrderUtilities.GetReleaseDate/IsEpisode/GetSeasonNumber/GetEpisodeNumber/SharedNaturalComparer` (static class in SmartList.cs ~3318).
- Produces (Task 2 relies on these exact members): `RoundRobinBase.CollectionGroupKeys` (`Dictionary<Guid, string>?`), `RoundRobinBase.OrderWithinGroupsByAirDate` (`bool`), `ExtractGroupKey(BaseItem item, string? groupByField, Dictionary<Guid, string>? collectionGroupKeys = null)`, `BuildGroupRecency(..., Dictionary<Guid, string>? collectionGroupKeys = null)` (appended optional param), `SortOption.WithinGroupOrder` (`string?`).

- [ ] **Step 1: Add `WithinGroupOrder` to SortOption**

In `Core/Models/SortOption.cs`, after the `GroupByField` property (keep its `[JsonIgnore]` style):

```csharp
        /// <summary>
        /// How items are ordered inside each Round Robin group.
        /// "Natural" (default, also when null/unknown): season/episode, disc/track, or name.
        /// "AirDate": premiere date (day precision), tie-broken by season/episode.
        /// Ignored by Shuffled Round Robin (shuffle wins).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? WithinGroupOrder { get; set; }
```

- [ ] **Step 2: RoundRobinBase — new state + threaded parameters**

In `Core/Orders/RoundRobinOrder.cs`:

(a) After the `GroupByField` property (~line 27), add:

```csharp
        /// <summary>
        /// Item id → collection name, for GroupByField "Collections". Built by SmartList from the
        /// unfiltered media pool (episodes resolve membership through their parent series) and set
        /// before <see cref="PreComputePositions"/>. Items absent from the map fall back to
        /// series-name/own-name grouping in <see cref="ExtractGroupKey"/>.
        /// </summary>
        public Dictionary<Guid, string>? CollectionGroupKeys { get; set; }

        /// <summary>
        /// When true, items within each group are ordered by air date (premiere date, day precision)
        /// instead of natural season/episode order. Set from SortOption.WithinGroupOrder.
        /// Ignored when <see cref="ShuffleWithinGroups"/> is true.
        /// </summary>
        public bool OrderWithinGroupsByAirDate { get; set; }
```

(b) `PreComputePositions` (~51-54) passes the new state:

```csharp
        public void PreComputePositions(IEnumerable<BaseItem> items, ILogger? logger = null)
        {
            ItemPositions = BuildInterleavedPositions(items, GroupByField, OrderGroupKeys, Name, logger, ShuffleWithinGroups, CollectionGroupKeys, OrderWithinGroupsByAirDate);
        }
```

(c) `BuildInterleavedPositions` (~93-99) gains two trailing optional parameters:

```csharp
        internal static ConcurrentDictionary<Guid, int> BuildInterleavedPositions(
            IEnumerable<BaseItem> items,
            string? groupByField,
            Func<IEnumerable<string>, List<string>> orderGroupKeys,
            string logPrefix,
            ILogger? logger,
            bool shuffleWithinGroups = false,
            Dictionary<Guid, string>? collectionGroupKeys = null,
            bool airDateWithinGroups = false)
```

Inside it, the grouping loop (~117) becomes `var key = ExtractGroupKey(item, groupByField, collectionGroupKeys);` and the within-group ordering block (~130-140) becomes:

```csharp
            foreach (var kvp in groups)
            {
                if (shuffleWithinGroups)
                {
                    Shuffle(kvp.Value, Random.Shared);
                }
                else if (airDateWithinGroups)
                {
                    kvp.Value.Sort((a, b) => CompareWithinGroupByAirDate(a, b));
                }
                else
                {
                    kvp.Value.Sort((a, b) => CompareWithinGroup(a, b));
                }
            }
```

(d) `ExtractGroupKey` (~168) gains the map parameter and a `"Collections"` case (insert before the `default:` case; fallback mirrors the `"SeriesName"` case):

```csharp
        internal static string ExtractGroupKey(BaseItem item, string? groupByField, Dictionary<Guid, string>? collectionGroupKeys = null)
```

```csharp
                case "Collections":
                    if (collectionGroupKeys != null && collectionGroupKeys.TryGetValue(item.Id, out var collectionName))
                    {
                        return collectionName;
                    }

                    // Not in any collection: fall back to per-show grouping
                    if (item is Episode collectionEpisode)
                    {
                        return collectionEpisode.SeriesName ?? string.Empty;
                    }

                    return item.Name ?? string.Empty;
```

(e) New comparator, placed directly after `CompareWithinGroup` (~240) — same semantics as `ReleaseDateOrder` (day precision, episodes first on ties, then season/episode via the existing comparator):

```csharp
        /// <summary>
        /// Compares two items within the same group by air date (premiere date, day precision).
        /// Missing dates are DateTime.MinValue and sort first (Release Date sort convention).
        /// Same-day ties put episodes before non-episodes, then fall back to natural order,
        /// so multi-part crossovers airing the same day keep their episode order.
        /// </summary>
        internal static int CompareWithinGroupByAirDate(BaseItem a, BaseItem b)
        {
            var dateCompare = OrderUtilities.GetReleaseDate(a).Date.CompareTo(OrderUtilities.GetReleaseDate(b).Date);
            if (dateCompare != 0)
            {
                return dateCompare;
            }

            var episodeCompare = OrderUtilities.IsEpisode(b).CompareTo(OrderUtilities.IsEpisode(a));
            if (episodeCompare != 0)
            {
                return episodeCompare;
            }

            return CompareWithinGroup(a, b);
        }
```

(f) `RoundRobinLeastRecentlyWatchedOrder.BuildGroupRecency` (~345-351) gains a trailing optional parameter `Dictionary<Guid, string>? collectionGroupKeys = null`, and its key line (~364) becomes `var key = ExtractGroupKey(item, groupByField, collectionGroupKeys);`.

- [ ] **Step 3: InitializeFromDto assigns the flag**

In `Core/SmartList.cs` `InitializeFromDto`, the existing branch (~126-129):

```csharp
                        if (order is RoundRobinBase rr)
                        {
                            rr.GroupByField = so.GroupByField;
                            rr.OrderWithinGroupsByAirDate = string.Equals(so.WithinGroupOrder, "AirDate", StringComparison.OrdinalIgnoreCase);
                        }
```

- [ ] **Step 4: Build both targets**

```bash
dotnet build Jellyfin.Plugin.SmartLists --framework net10.0 --configuration Release
dotnet build Jellyfin.Plugin.SmartLists --framework net9.0 --configuration Release
```

Expected: both succeed, zero warnings.

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Core/Models/SortOption.cs Jellyfin.Plugin.SmartLists/Core/Orders/RoundRobinOrder.cs Jellyfin.Plugin.SmartLists/Core/SmartList.cs
git commit -m "Add WithinGroupOrder and Collections group-key mechanics to round robin"
```

---

### Task 2: SmartList — collection map builder + injection

**Files:**

- Modify: `Jellyfin.Plugin.SmartLists/Core/SmartList.cs` (injection block ~760-769; new private helper near `GetCollectionChildren` ~1619)

**Interfaces:**

- Consumes (from Task 1): `RoundRobinBase.CollectionGroupKeys`, `BuildGroupRecency(..., collectionGroupKeys)`. Also existing: `GetCollectionChildren(BaseItem, User, ILogger?)` (private static, SmartList.cs ~1619), `refreshCache.AllCollections` (`BaseItem[]?`), `refreshCache.CollectionDirectChildren` (`ConcurrentDictionary<Guid, BaseItem[]>`), `InternalItemsQuery`/`BaseItemKind` (already used at ~1499-1505 in `GetMatchingCollections` — copy that query shape exactly).
- Produces: `BuildCollectionGroupKeyMap(IReadOnlyList<BaseItem> pool, User user, ILibraryManager libraryManager, RefreshQueueService.RefreshCache? refreshCache, ILogger? logger)` returning `Dictionary<Guid, string>`.

- [ ] **Step 1: Add the map builder**

Insert after `GetCollectionChildren` (~1651):

```csharp
        /// <summary>
        /// Builds an item id → collection name map for Round Robin "Collections" grouping.
        /// TV collections contain Series items, so episodes resolve membership through their
        /// parent series; movies (and any direct members) resolve by their own id.
        /// When an item belongs to multiple collections, the alphabetically-first collection
        /// name wins (consistent with the Genres/Studios "first value" convention).
        /// Direct members only — nested collections are not flattened.
        /// </summary>
        private static Dictionary<Guid, string> BuildCollectionGroupKeyMap(
            IReadOnlyList<BaseItem> pool,
            User user,
            ILibraryManager libraryManager,
            RefreshQueueService.RefreshCache? refreshCache,
            ILogger? logger)
        {
            var map = new Dictionary<Guid, string>();

            try
            {
                BaseItem[] allCollections;
                if (refreshCache?.AllCollections != null)
                {
                    allCollections = refreshCache.AllCollections;
                }
                else
                {
                    var query = new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = [BaseItemKind.BoxSet],
                        Recursive = true,
                    };
                    allCollections = [.. libraryManager.GetItemsResult(query).Items];
                    if (refreshCache != null)
                    {
                        refreshCache.AllCollections = allCollections;
                    }
                }

                // memberId -> collection name; alphabetically-first collection wins
                var memberToCollection = new Dictionary<Guid, string>();
                foreach (var collection in allCollections.OrderBy(c => c.Name ?? string.Empty, OrderUtilities.SharedNaturalComparer))
                {
                    BaseItem[] children;
                    if (refreshCache != null && refreshCache.CollectionDirectChildren.TryGetValue(collection.Id, out var cachedChildren))
                    {
                        children = cachedChildren;
                    }
                    else
                    {
                        children = GetCollectionChildren(collection, user, logger);
                        refreshCache?.CollectionDirectChildren.TryAdd(collection.Id, children);
                    }

                    foreach (var child in children)
                    {
                        if (!memberToCollection.ContainsKey(child.Id))
                        {
                            memberToCollection[child.Id] = collection.Name ?? string.Empty;
                        }
                    }
                }

                foreach (var item in pool)
                {
                    if (memberToCollection.TryGetValue(item.Id, out var directName))
                    {
                        map[item.Id] = directName;
                    }
                    else if (item is Episode episode && episode.SeriesId != Guid.Empty &&
                             memberToCollection.TryGetValue(episode.SeriesId, out var seriesCollectionName))
                    {
                        map[item.Id] = seriesCollectionName;
                    }
                }

                logger?.LogDebug("Collection group map: {MappedCount} of {PoolCount} items belong to a collection ({CollectionCount} collections checked)",
                    map.Count, pool.Count, allCollections.Length);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Error building collection group map - items will fall back to series/name grouping");
            }

            return map;
        }
```

Adaptation notes for the implementer: if `refreshCache.AllCollections`/`CollectionDirectChildren` member names or types differ, check `RefreshQueueService.cs` ~725-772 (`AllCollections` is `BaseItem[]?` at ~732, `CollectionDirectChildren` is `ConcurrentDictionary<Guid, BaseItem[]>` at ~762) and adapt; if `GetItemsResult(query).Items` needs a different materialization, copy the exact shape used by `GetMatchingCollections` (~1499-1505). `Episode` (`MediaBrowser.Controller.Entities.TV`) is already imported by SmartList.cs.

- [ ] **Step 2: Extend the injection block**

Replace the existing block at ~760-769 (currently the LRW-only injection starting with the comment `// Least Recently Watched Round Robin needs per-group watch recency...`) with:

```csharp
                // Round Robin sorts may need maps computed from the UNFILTERED pool before filtering
                // (same injection pattern as SimilarityOrder.Scores / RuleBlockOrder.GroupMappings):
                // - "Collections" grouping: item id -> collection name (episodes resolve via their series).
                // - Least Recently Watched: per-group watch recency. Rules like "Playback Status is
                //   Unwatched" remove watched items from the results, so recency derived from filtered
                //   items would see every group as unwatched.
                Dictionary<Guid, string>? collectionGroupKeys = null;
                if (Orders.OfType<RoundRobinBase>().Any(o => o.GroupByField == "Collections"))
                {
                    collectionGroupKeys = BuildCollectionGroupKeyMap(itemsArray, user, libraryManager, refreshCache, logger);
                    foreach (var rrOrder in Orders.OfType<RoundRobinBase>())
                    {
                        if (rrOrder.GroupByField == "Collections")
                        {
                            rrOrder.CollectionGroupKeys = collectionGroupKeys;
                        }
                    }
                }

                foreach (var lrwOrder in Orders.OfType<RoundRobinLeastRecentlyWatchedOrder>())
                {
                    lrwOrder.GroupRecency = RoundRobinLeastRecentlyWatchedOrder.BuildGroupRecency(
                        itemsArray, lrwOrder.GroupByField, user, userDataManager, refreshCache, logger,
                        lrwOrder.GroupByField == "Collections" ? collectionGroupKeys : null);
                }
```

(`itemsArray` and `libraryManager` are already in scope — `itemsArray` is materialized at ~748, `libraryManager` is a method parameter.)

- [ ] **Step 3: Build both targets**

```bash
dotnet build Jellyfin.Plugin.SmartLists --framework net10.0 --configuration Release
dotnet build Jellyfin.Plugin.SmartLists --framework net9.0 --configuration Release
```

Expected: both succeed, zero warnings.

- [ ] **Step 4: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Core/SmartList.cs
git commit -m "Build and inject collection group-key map for Round Robin Collections grouping"
```

---

### Task 3: UI — Group By entry + Order Within Group dropdown

**Files:**

- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-core.js` (~113-119)
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-sorts.js` (syncSortOrderUI ~126-145; createSortBox ~329-367; collectSortsFromForm ~485-496; updateAllSortOptionsVisibility ~540-555)

**Interfaces:**

- Consumes: Task 1's DTO contract — save `WithinGroupOrder: 'AirDate'` (omit for Natural), Group By value `'Collections'`.
- Produces: nothing downstream.

- [ ] **Step 1: config-core.js — Group By entry**

In `SmartLists.ROUND_ROBIN_GROUP_FIELDS` (~113-119), add after the `SeriesName` entry:

```javascript
        { value: 'Collections', label: 'Collection', mediaTypes: ['Episode', 'Movie'] },
```

- [ ] **Step 2: config-sorts.js — syncSortOrderUI gains the new container**

Replace the function (~126-145) with (new 5th parameter + visibility rule):

```javascript
    // Helper function to sync Sort Order UI based on Sort By value
    SmartLists.syncSortOrderUI = function(sortByValue, sortOrderContainer, sortOrderSelect, groupByContainer, withinGroupContainer) {
        if (!sortOrderContainer || !sortOrderSelect) return;
        
        // Hide Sort Order for Random, Random Round Robin, and Default (they don't use ordering)
        if (SmartLists.isOrderlessSort(sortByValue)) {
            sortOrderContainer.style.display = 'none';
        } else {
            sortOrderContainer.style.display = '';
            
            // Auto-set to Descending when Similarity is selected (most similar first)
            if (sortByValue === 'Similarity') {
                sortOrderSelect.value = 'Descending';
            }
        }

        // Show/hide GroupBy dropdown for Round Robin and Random Round Robin
        if (groupByContainer) {
            groupByContainer.style.display = SmartLists.isRoundRobinSort(sortByValue) ? '' : 'none';
        }

        // Show/hide Order Within Group for round robin sorts (not Shuffled - shuffle wins)
        if (withinGroupContainer) {
            var showWithinGroup = SmartLists.isRoundRobinSort(sortByValue) && sortByValue !== 'Shuffled Round Robin';
            withinGroupContainer.style.display = showWithinGroup ? '' : 'none';
        }
    };
```

- [ ] **Step 3: config-sorts.js — createSortBox: new field + wired calls**

(a) After the Group By field block (ends `fieldsContainer.appendChild(groupByField.container);` ~340), add:

```javascript
        // Order Within Group field (round robin sorts except Shuffled)
        const withinGroupField = SmartLists.createSortField('Order Within Group', 'sort-withingroup-' + sortId, 'select');
        withinGroupField.container.style.minWidth = '200px';
        withinGroupField.container.style.maxWidth = '200px';
        var savedWithinGroup = (sortData && sortData.WithinGroupOrder) ? sortData.WithinGroupOrder : 'Natural';
        SmartLists.populateSelectElement(withinGroupField.input, [
            { value: 'Natural', label: 'Season/Episode (default)', selected: savedWithinGroup !== 'AirDate' },
            { value: 'AirDate', label: 'Air Date', selected: savedWithinGroup === 'AirDate' }
        ]);
        withinGroupField.container.style.display = (SmartLists.isRoundRobinSort(actualSortBy) && actualSortBy !== 'Shuffled Round Robin') ? '' : 'none';
        fieldsContainer.appendChild(withinGroupField.container);
```

(b) The change listener (~356) and the init call (~367) pass the new container:

```javascript
            SmartLists.syncSortOrderUI(this.value, sortOrderField.container, sortOrderField.input, groupByField.container, withinGroupField.container);
```

```javascript
        SmartLists.syncSortOrderUI(actualSortBy, sortOrderField.container, sortOrderField.input, groupByField.container, withinGroupField.container);
```

- [ ] **Step 4: config-sorts.js — save path**

In `collectSortsFromForm`, inside the existing `if (SmartLists.isRoundRobinSort(sortBy))` block (~491-496), after the GroupByField lines add:

```javascript
                var withinGroupSelect = box.querySelector('[id^="sort-withingroup-"]');
                if (withinGroupSelect && withinGroupSelect.value === 'AirDate' && sortBy !== 'Shuffled Round Robin') {
                    sortEntry.WithinGroupOrder = 'AirDate';
                }
```

(Natural is deliberately omitted from the JSON — old configs stay byte-identical.)

- [ ] **Step 5: config-sorts.js — updateAllSortOptionsVisibility**

At ~540-542, the sync call gains the new container (resolve it the same way as groupByContainer):

```javascript
            var groupByContainer = box.querySelector('[id^="sort-groupby-"]');
            groupByContainer = groupByContainer ? groupByContainer.closest('.sort-field-container') : null;
            var withinGroupContainer = box.querySelector('[id^="sort-withingroup-"]');
            withinGroupContainer = withinGroupContainer ? withinGroupContainer.closest('.sort-field-container') : null;
            SmartLists.syncSortOrderUI(effectiveSortValue, sortOrderContainer, sortOrderSelect, groupByContainer, withinGroupContainer);
```

Note: verify the container-resolution idiom matches what the file actually uses at ~540 (`closest('.sort-field-container')`) — if `createSortField` uses a different wrapper class, mirror whatever the Group By resolution uses.

(No repopulation-on-media-type-change needed for the new select — its options are static. The Group By select's existing repopulation at ~544-555 automatically picks up the Collections entry via `getFilteredRoundRobinFields`.)

- [ ] **Step 6: Syntax check**

```bash
node --check Jellyfin.Plugin.SmartLists/Configuration/config-core.js
node --check Jellyfin.Plugin.SmartLists/Configuration/config-sorts.js
```

Expected: no output.

- [ ] **Step 7: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/config-core.js Jellyfin.Plugin.SmartLists/Configuration/config-sorts.js
git commit -m "Add Collection group-by option and Order Within Group dropdown to sort UI"
```

---

### Task 4: Docs

**Files:**

- Modify: `docs/content/user-guide/sorting-and-limits.md` (Group By table ~126-134; Round Robin section; variant notes)
- Modify: `docs/content/examples/common-use-cases.md` (round-robin recipes ~76-135)

**Interfaces:** none — prose. Match the existing docs style exactly (read the sections first; the Group By table lives in the "Round Robin (Interleave)" section and other variants cross-reference it).

- [ ] **Step 1: sorting-and-limits.md**

Content to convey (adapt to existing format):

1. Add a row to the "Available Group By fields" table: `Collection` | `Episode or Movie media type`.
2. Below the table, extend the multi-value note: episodes belong to a collection through their **series** (TV collections contain series); an item in several collections groups under the alphabetically-first one; items in no collection group by Series Name (episodes) or their own name; direct collection members only (nested collections are not flattened). Collections are Jellyfin's regular collections — manual, auto-created movie collections, or SmartLists smart collections all work.
3. New subsection **"Order Within Group"** in the Round Robin section: applies to all round-robin variants except Shuffled; `Season/Episode (default)` = current behavior; `Air Date` = premiere date (day precision, same-day ties keep episode order; missing dates first). Highlight the franchise use: Group By Collection + Air Date plays crossovers in airing order, and a spinoff only enters the rotation once the timeline reaches its premiere.
4. One line in the Least Recently Watched section: with Group By Collection, the whole franchise carries one recency — watching any member sends the collection group to the back.

- [ ] **Step 2: common-use-cases.md — new recipe**

Add after the "Continue-Where-You-Left-Off TV Channel" recipe, matching the established recipe format:

```markdown
### Franchise TV Channel (crossovers in air-date order)

Have franchise collections like NCIS or One Chicago where crossover episodes span several shows? Group the rotation by collection and play each franchise in airing order:

- **Media Type:** Episode
- **Rules:** Playback Status = Unplayed
- **Sort By:** Least Recently Watched Round Robin, **Group By:** Collection, **Order Within Group:** Air Date
- **Auto Refresh:** On All Changes

Each collection becomes one slot in the rotation and plays chronologically across its shows — crossovers stay in order, and a spinoff only starts appearing once the timeline reaches its premiere. Shows not in any collection rotate as themselves. Watch anything in a franchise and the whole franchise moves to the back of the rotation.
```

- [ ] **Step 3: Commit**

```bash
git add docs/content/
git commit -m "Document Collection group-by and Order Within Group"
```

---

### Task 5: End-to-end verification against local Jellyfin

**Files:** none — verification only, per spec "Verification". Driver (main session) runs this; steps recorded here for completeness.

- [ ] **Step 1:** Build worktree plugin into the container-mounted output and restart: `dotnet build Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj --framework net10.0 --configuration Release -o /Users/johan.yourstone/Git/jellyfin-smartplaylist-plugin/build_output /p:Version=12.0.0.0 /p:AssemblyVersion=12.0.0.0 && docker restart jellyfin`.
- [ ] **Step 2:** Create a Jellyfin collection containing two series with interleaved episode air dates (via API: create BoxSet, add Show Alpha + Show Beta series; adjust PremiereDates via metadata if needed).
- [ ] **Step 3:** Episode playlist, Round Robin Ascending + Group By Collection + Order Within Group Air Date: collection shows occupy ONE slot, episodes across both shows in air-date order; non-collection shows rotate individually.
- [ ] **Step 4:** Switch Order Within Group to default: same single group, internal order by season/episode across both shows.
- [ ] **Step 5:** Least Recently Watched Round Robin + Group By Collection: mark a collection episode played → whole collection group moves to the back on refresh.
- [ ] **Step 6:** Movie playlist + Group By Collection with a movie collection → one group, Air Date = release order.
- [ ] **Step 7:** Regression: existing round-robin playlists (all variants, all existing Group By values, no WithinGroupOrder in JSON) refresh identically; UI hides Order Within Group for Shuffled and non-round-robin sorts; Collection option hidden for Audio-only playlists; values round-trip through save/edit.
- [ ] **Step 8:** Logs clean: `docker logs jellyfin 2>&1 | grep -iE "\[ERR\]|\[WRN\]" | grep -i smart` → no new errors/warnings. Delete test playlists/collection.

---

## Self-review notes

- Spec coverage: DTO (T1), order mechanics + comparator + LRW map param (T1), map builder + injection (T2), UI (T3), docs (T4), verification incl. movie + regression (T5). Alphabetical-first: `OrderBy(SharedNaturalComparer)` + first-assignment-wins in T2. Direct-members-only: `GetCollectionChildren`, no recursion. Shuffled exclusion: UI hides (T3) + shuffle branch precedes airDate branch (T1).
- Type consistency: `CollectionGroupKeys`/`OrderWithinGroupsByAirDate`/`WithinGroupOrder`/`BuildCollectionGroupKeyMap` names match across tasks; `ExtractGroupKey`/`BuildGroupRecency` optional-param signatures consistent between T1 and T2 call sites.
- `Episode.SeriesId` is a non-nullable `Guid` on Jellyfin's Episode entity; the `!= Guid.Empty` guard covers orphaned episodes.
