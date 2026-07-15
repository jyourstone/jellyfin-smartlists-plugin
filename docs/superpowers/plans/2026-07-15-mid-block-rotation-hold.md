# Mid-Block Rotation Hold Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Least Recently Watched Round Robin with Collections + Air Date holds a collection group at the front of the rotation while the user is mid-way through an air block, instead of rotating the whole group to the back after every watch.

**Architecture:** Backend-only. `BuildGroupRecency` gains an optional `int? airBlockWindowDays` (null = current behavior); when set it also collects per-group `(airDate, lastPlayed)` pairs and post-processes: a group whose earliest unwatched air date is within the window of its most-recently-watched item's air date is removed from the recency map, so it sorts with never-watched groups at the front. `SmartList.cs` passes the window only for Collections + Air Date LRW orders.

**Tech Stack:** C# (.NET multi-target net9.0/net10.0).

**Spec:** `docs/superpowers/specs/2026-07-15-mid-block-rotation-hold-design.md`

## Global Constraints

- Warnings are errors (CA analyzers); new/changed members keep XML docs accurate.
- Both TFMs build clean: `dotnet build Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj --no-incremental` → 0 warnings, 0 errors.
- No test suite — verification is the build per task plus the e2e pass in Task 3.
- Commits end with the two trailer lines shown in each commit step.
- Null `airBlockWindowDays` must produce byte-identical recency maps to today (all non-Collections callers).

---

### Task 1: Mid-block hold in BuildGroupRecency (`RoundRobinOrder.cs`, `SmartList.cs`)

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Core/Orders/RoundRobinOrder.cs:470-533`
- Modify: `Jellyfin.Plugin.SmartLists/Core/SmartList.cs:782-787`

**Interfaces:**
- Consumes: existing `OrderUtilities.GetReleaseDate(BaseItem)` (DateTime, MinValue when unknown), existing lastPlayed derivation inside the loop, `RoundRobinBase.AirBlockWindowDays` (int, default 3).
- Produces: `BuildGroupRecency(..., Dictionary<Guid, string>? collectionGroupKeys = null, int? airBlockWindowDays = null)` — single caller updated in the same task.

- [ ] **Step 1: Update the `GroupRecency` property XML doc**

Replace the property doc (line ~470):

```csharp
        /// <summary>
        /// Group key → most recent per-user LastPlayedDate, computed from the UNFILTERED media
        /// pool (rules like "Playback Status is Unwatched" remove watched items from the results,
        /// so recency derived from filtered items would see every group as never watched).
        /// Set by SmartList before <see cref="RoundRobinBase.PreComputePositions"/>.
        /// Groups absent from the map are treated as never watched and sort first — including
        /// groups held mid-block: with Collections grouping and air-date order, a group whose
        /// next unwatched episode aired within <see cref="RoundRobinBase.AirBlockWindowDays"/>
        /// days of its most recently watched one stays at the front until the block is finished.
        /// </summary>
        public Dictionary<string, DateTime> GroupRecency { get; set; } = new(StringComparer.OrdinalIgnoreCase);
```

- [ ] **Step 2: Extend `BuildGroupRecency`**

Replace the whole method (lines ~487-533) with:

```csharp
        /// <summary>
        /// Builds the group key → most recent LastPlayedDate map for one user across the given items.
        /// Container items (Series/Season/MusicAlbum) use the aggregate-over-children date when the
        /// refresh cache has their children, mirroring LastPlayedOrderBase.
        /// When <paramref name="airBlockWindowDays"/> is set (Collections grouping with air-date
        /// order), groups that are mid-way through an air block — the earliest unwatched air date
        /// lies within the window of the most recently watched item's air date — are omitted from
        /// the map so they hold their spot at the front of the rotation until the block is finished.
        /// </summary>
        internal static Dictionary<string, DateTime> BuildGroupRecency(
            IEnumerable<BaseItem> items,
            string? groupByField,
            User user,
            IUserDataManager? userDataManager,
            RefreshQueueService.RefreshCache? refreshCache,
            ILogger? logger,
            Dictionary<Guid, string>? collectionGroupKeys = null,
            int? airBlockWindowDays = null)
        {
            var recency = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(groupByField) || userDataManager == null || user == null)
            {
                logger?.LogWarning("Least Recently Watched Round Robin: missing GroupByField or user context - groups fall back to alphabetical order");
                return recency;
            }

            // Per-group (airDate, lastPlayed) pairs, collected only when the mid-block hold is active
            var groupItems = airBlockWindowDays.HasValue
                ? new Dictionary<string, List<(DateTime Air, DateTime LastPlayed)>>(StringComparer.OrdinalIgnoreCase)
                : null;

            foreach (var item in items)
            {
                try
                {
                    var key = ExtractGroupKey(item, groupByField, collectionGroupKeys);

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

                    if (groupItems != null)
                    {
                        if (!groupItems.TryGetValue(key, out var list))
                        {
                            list = new List<(DateTime Air, DateTime LastPlayed)>();
                            groupItems[key] = list;
                        }

                        list.Add((OrderUtilities.GetReleaseDate(item).Date, lastPlayed));
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Error reading last played date for item {ItemName}", item.Name);
                }
            }

            if (groupItems != null && airBlockWindowDays.HasValue)
            {
                ApplyMidBlockHold(recency, groupItems, Math.Clamp(airBlockWindowDays.Value, 0, 30), logger);
            }

            return recency;
        }

        /// <summary>
        /// Removes groups that are mid-way through an air block from the recency map: a group whose
        /// earliest unwatched air date is at or after — and within <paramref name="windowDays"/> days
        /// of — the air date of its most recently watched item sorts with the never-watched groups
        /// at the front, so the rest of the block plays before the group rotates to the back.
        /// Groups with no watch data, or without air dates on the relevant items, are left untouched.
        /// </summary>
        internal static void ApplyMidBlockHold(
            Dictionary<string, DateTime> recency,
            Dictionary<string, List<(DateTime Air, DateTime LastPlayed)>> groupItems,
            int windowDays,
            ILogger? logger)
        {
            foreach (var kvp in groupItems)
            {
                if (!recency.ContainsKey(kvp.Key))
                {
                    continue; // never watched - already at the front
                }

                // Air date of the most recently watched item in the group
                var watchedAir = DateTime.MinValue;
                var bestPlayed = DateTime.MinValue;
                foreach (var (air, played) in kvp.Value)
                {
                    if (played > bestPlayed)
                    {
                        bestPlayed = played;
                        watchedAir = air;
                    }
                }

                if (watchedAir == DateTime.MinValue)
                {
                    continue; // watched item has no air date - normal rotation
                }

                // Earliest unwatched air date at or after the watched one
                var nextUnwatchedAir = DateTime.MaxValue;
                foreach (var (air, played) in kvp.Value)
                {
                    if (played == DateTime.MinValue && air > DateTime.MinValue && air >= watchedAir && air < nextUnwatchedAir)
                    {
                        nextUnwatchedAir = air;
                    }
                }

                if (nextUnwatchedAir != DateTime.MaxValue && (nextUnwatchedAir - watchedAir).TotalDays <= windowDays)
                {
                    recency.Remove(kvp.Key);
                    logger?.LogDebug(
                        "Least Recently Watched Round Robin: holding group '{GroupKey}' at the front - next unwatched episode aired {Days} day(s) after the last watched one (window {Window})",
                        kvp.Key, (nextUnwatchedAir - watchedAir).TotalDays, windowDays);
                }
            }
        }
```

- [ ] **Step 3: Pass the window at the injection site**

In `Jellyfin.Plugin.SmartLists/Core/SmartList.cs` (~line 782), replace:

```csharp
                foreach (var lrwOrder in Orders.OfType<RoundRobinLeastRecentlyWatchedOrder>())
                {
                    lrwOrder.GroupRecency = RoundRobinLeastRecentlyWatchedOrder.BuildGroupRecency(
                        itemsArray, lrwOrder.GroupByField, user, userDataManager, refreshCache, logger,
                        lrwOrder.GroupByField == "Collections" ? collectionGroupKeys : null);
                }
```

with:

```csharp
                foreach (var lrwOrder in Orders.OfType<RoundRobinLeastRecentlyWatchedOrder>())
                {
                    lrwOrder.GroupRecency = RoundRobinLeastRecentlyWatchedOrder.BuildGroupRecency(
                        itemsArray, lrwOrder.GroupByField, user, userDataManager, refreshCache, logger,
                        lrwOrder.GroupByField == "Collections" ? collectionGroupKeys : null,
                        lrwOrder.GroupByField == "Collections" && lrwOrder.OrderWithinGroupsByAirDate
                            ? lrwOrder.AirBlockWindowDays
                            : (int?)null);
                }
```

- [ ] **Step 4: Build both target frameworks**

Run from the repo root:

```bash
dotnet build Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj --no-incremental
```

Expected: `Build succeeded.`, `0 Warning(s)`, `0 Error(s)` for net9.0 and net10.0.

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Core/Orders/RoundRobinOrder.cs Jellyfin.Plugin.SmartLists/Core/SmartList.cs
git commit -m "$(cat <<'EOF'
Hold mid-block collection groups at the front of the LRW rotation

Watching part 1 of a crossover air block no longer rotates the whole
collection group to the back: with Collections grouping and air-date
order, a group whose next unwatched episode aired within the Air Window
of the last watched one is treated as least-recent until the block is
finished.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01TsYUGfTYy8xkQeUiNbjNQ5
EOF
)"
```

---

### Task 2: Docs

**Files:**
- Modify: `docs/content/user-guide/sorting-and-limits.md` (LRW collection-recency sentence, ~line 260)
- Modify: `docs/content/examples/common-use-cases.md` (Franchise TV Channel result, ~line 124)

**Interfaces:**
- Consumes: user-facing terms from Task 1: "Air Window", default 3.
- Produces: nothing downstream.

- [ ] **Step 1: Amend the LRW collection note**

In `docs/content/user-guide/sorting-and-limits.md`, find the sentence:

```markdown
With **Group By: Collection**, the whole franchise carries one recency — watching any member sends the entire collection group to the back of the rotation.
```

Replace with:

```markdown
With **Group By: Collection**, the whole franchise carries one recency — watching any member sends the entire collection group to the back of the rotation. The exception is an unfinished air block: with **Order Within Group: Air Date**, a collection whose next unwatched episode aired within the **Air Window** of the one you just watched stays at the front until that block is finished, so watching part 1 of a crossover night never pushes parts 2 and 3 to the bottom.
```

- [ ] **Step 2: Amend the franchise recipe**

In `docs/content/examples/common-use-cases.md`, find the sentence (end of the Franchise TV Channel result paragraph):

```markdown
Watch anything in a franchise and the whole franchise moves to the back of the rotation.
```

Replace with:

```markdown
Watch anything in a franchise and the whole franchise moves to the back of the rotation — unless you stopped mid-way through a crossover block, in which case the collection stays at the front until you finish it.
```

- [ ] **Step 3: Commit**

```bash
git add docs/content/user-guide/sorting-and-limits.md docs/content/examples/common-use-cases.md
git commit -m "$(cat <<'EOF'
Document the mid-block rotation hold

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01TsYUGfTYy8xkQeUiNbjNQ5
EOF
)"
```

---

### Task 3: E2E verification against local Jellyfin

**Files:** none (verification only). Deploy the worktree build into the MAIN checkout's `build_output` and verify the DLL before testing (shared-container gotcha):

```bash
dotnet build <worktree>/Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj --no-incremental -f net10.0 -o /Users/johan.yourstone/Git/jellyfin-smartplaylist-plugin/build_output
python3 -c "d=open('/Users/johan.yourstone/Git/jellyfin-smartplaylist-plugin/build_output/Jellyfin.Plugin.SmartLists.dll','rb').read(); print(d.count('holding group'.encode('utf-16-le')))"
docker restart jellyfin
```

Expected: python prints ≥ 1 (the new log literal).

Fixture: collection over Show Alpha + Show Beta (identical air dates 2004-07-16/23/30); playlist rules matching Alpha, Beta, Modern Family, The Simpsons, Breaking Bad; sort **Least Recently Watched Round Robin**, Group By Collection, Order Within Group Air Date. Mark played: `POST /UserPlayedItems/{itemId}?userId=` (DELETE to unmark). Refresh via `POST /Plugins/SmartLists/{id}/refresh`, poll `LastRefreshed`.

- [ ] **Step 1: Baseline** — all unwatched: collection group at the front (alphabetical among never-watched), pairs blocked together.
- [ ] **Step 2: Mid-block hold** — mark Alpha S1E1 played, refresh: collection group STILL at the front; its first unwatched episode (Beta S1E1) leads the group.
- [ ] **Step 3: Block finished** — mark Beta S1E1 played, refresh: collection group rotates to the BACK (next unwatched pair aired 7 days later than the watched ones).
- [ ] **Step 4: Regression** — same playlist with Group By Series Name: watching one episode rotates that series to the back (no hold).
- [ ] **Step 5: Cleanup** — unmark played states, delete test playlist and collection.
