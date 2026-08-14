# Generic Ancestor Walk for Tags / Studios / Genres (issue #495) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A tag applied to a **season**, a **physical folder**, or a **library** must match the items beneath it when "Include parent tags" is enabled — not just a tag on the Series (episodes) or the Album (audio tracks). Same for Studios and Genres. The option becomes valid for **all** media types.

**Architecture:** Replace the six one-level extractors (`ExtractParentSeries{Tags,Studios,Genres}` + `ExtractParentAlbum{Tags,Studios,Genres}`, ~350 lines) with **one memoized ancestor walk** in a new file, `Core/QueryEngine/AncestorValueResolver.cs`, that collects Tags + Studios + Genres in a single pass. Runtime plumbing collapses (Operand 6 lists → 3, ExtractionGroup 6 bits → 3, RefreshCache 6 dicts → 1, Engine loses 2 parameters and 2 dead helpers, frontend loses ~135 lines of media-type label swapping). **Nothing is renamed or removed on disk** — `Expression` gains 3 flags plus 3 computed folds; the 9 existing flags stay untouched, so there is no migration, no shim, no deprecation window.

**Tech Stack:** C# (.NET 9/10 multi-target, Jellyfin plugin), xUnit test project (net10.0 only), vanilla JS (Jellyfin admin UI conventions), mkdocs.

---

## Amendments (decided after the plan was drafted — these OVERRIDE the body)

### Amendment 1 — No dual-write. The `10.11` stable line is retired.

Repo owner, 2026-08-14: *"I won't release any more stable to the 10.11 branch, only 12 from now on."*

Every instruction in this plan to **dual-write** the legacy flags is **cancelled**. It existed solely so a list edited on a `12.x` build survived a rollback to a `10.11.X.0` stable release; that rollback is now impossible.

| | Decision |
|---|---|
| **Write** legacy `IncludeParentSeries*` / `IncludeParentAlbum*` keys on save | **NO.** New saves emit `IncludeParentTags` / `OnlyParentTags` (+ Studios/Genres) only. |
| **Read** legacy keys | **YES, permanently.** Lists saved by older builds still carry them. C# folds via `IncludeParent*Effective`; JS edit-mode repopulation must still recognise them. |
| Legacy flag declarations on `Expression` | **Kept**, unchanged, read-only. |

Affected: **Major 5** (withdrawn), the "Written by" column of the mapping table, **Task 10 Steps 3-4**, **Task 12** grep gate, **Task 13 Step E**, **Open Question 1** (closed). Those sections have been corrected in place; any residual `dual-write` phrasing below is superseded by this amendment.

Task 12's gate becomes: legacy identifiers must still appear in JS on the **read/repopulate** path, and must **not** appear on the **write/serialize** path.

### Amendment 2 — Remaining open questions, resolved

| # | Question | Decision |
|---|---|---|
| 2 | Document `IncludeParentTags` / `OnlyParentTags` as public API? | **No.** Keep internal — the parent flags have never been documented and documenting makes them a supported contract. YAGNI. |
| 3 | Fold in the symlinked-library `GetCollectionFolders` gap (~20 lines)? | **No — follow-up issue.** Pre-existing and already documented (`LibraryManagerHelper.cs:98-101`); not introduced here. Out of scope. |
| 4 | Gate audio to stop at the album for one release? | **No.** Ship the generic walk as-is. Gating audio is a speculative special-case that contradicts the whole point. **Must appear in release notes** — `NotContains`/`NotEqual` audio rules will visibly shrink. |
| 5 | Confirm depth cap of 20 | **Keep 20.** Measured real depth is 4-5. |

---

## Problem Statement

GitHub issue **#495**: a user tagged a **season** with `seasontag02` and their **"Series" library** with a library-level tag. Their episodes did not match a `Tags` rule even with "Include parent series tags" enabled.

**Confirmed root cause:** `Jellyfin.Plugin.SmartLists/Core/QueryEngine/Factory.cs:2180-2236` (`ExtractParentSeriesTags`) resolves the episode's `SeriesId` via reflection (`TryGetEpisodeSeriesGuid`, Factory.cs:1923-1936), calls `libraryManager.GetItemById(seriesGuid)` (Factory.cs:2207) and reads **only that Series' `Tags`**. The Season sitting between episode and series is never touched, and nothing above the Series is either. The same shape is repeated in five siblings:

| Extractor | Location | Reads exactly one level |
|---|---|---|
| `ExtractParentSeriesTags` | Factory.cs:2180-2236 | Series `.Tags` |
| `ExtractParentSeriesStudios` | Factory.cs:2242-2298 | Series `.Studios` |
| `ExtractParentSeriesGenres` | Factory.cs:2304-2360 | Series `.Genres` |
| `ExtractParentAlbumGenres` | Factory.cs:2366-2415 | MusicAlbum `.Genres` |
| `ExtractParentAlbumTags` | Factory.cs:2421-2470 | MusicAlbum `.Tags` |
| `ExtractParentAlbumStudios` | Factory.cs:2476-2525 | MusicAlbum `.Studios` |

All six also hard-return for anything that is not an `Episode` (Factory.cs:2186) or not `Audio` (Factory.cs:2371), which is why the option is offered only when Episode or Audio is a selected media type.

---

## Load-Bearing Facts (verified against the codebase and the live dev DB)

1. **A `GetParent()` walk NEVER reaches the library.** Measured by recursive `ParentId` CTE on `dev/jellyfin-data/config/data/jellyfin.db`:
   - Episode `Moss and the German` `8F4F4464` → Season `3C34646F` → Series `CACA970C` → Folder `7CA8CCF8` (`shows`) → AggregateFolder `F27CAA37` (`root`) → null
   - Movie `3619F76A` → Folder `5B0D238E` (`movies`) → AggregateFolder `root`
   - Audio `04FAD2F9` → MusicAlbum `B5E5B661` → Folder `20A90C40` (`music`) → AggregateFolder `root`

   The tagged library `CollectionFolder BC3A7D1D` (`Serier`) has `ParentId = E9D5075A` (the **UserRootFolder**) — it is a sibling structure, never an ancestor. **The brief's "stop at the CollectionFolder inclusive" is unimplementable as written and would ship a half-fix** (season tag starts matching, library tag still never does).
   The walk must therefore be: **parent chain (stopping before AggregateFolder / UserRootFolder / UserView) UNION `libraryManager.GetCollectionFolders(chainTop)`** — structurally identical to what `BaseItem.GetInheritedTags()` and `GetAncestorIds()` do in core.

2. **Library tags are real and live on the CollectionFolder `BaseItem`.** `SELECT Id,Name,Type,Tags FROM BaseItems WHERE Tags<>''` returns `BC3A7D1D | Serier | ...CollectionFolder | seriestag01`, and `GET /Items?ids=BC3A7D1D&fields=Tags` returns `Tags: ['seriestag01']`. Tags are **not** in `LibraryOptions`.

3. **`BaseItem.TopParentId` does not exist.** It is a DB column and an `InternalItemsQuery` filter only. `GetTopParent()` returns the *physical* top folder, never the library.

4. **`SeasonId` / `SeriesId` drift from the real tree.** Episode `8F4F4464` has `ParentId=3C34646F` but `SeasonId=98C6594D`. Use `ParentId`/`GetParent()` exclusively; never `DisplayParentId` (`Episode.DisplayParentId => SeasonId`).

5. **`Audio` has no `AlbumId` property** in either ABI (only `MusicAlbum AlbumEntity => FindParent<MusicAlbum>()`), so `TryGetAudioAlbumGuid`'s reflected `"AlbumId"` lookup always returns null and the `ParentId` fallback is what has always run. The walk visits the same node for audio — no regression there.

6. **Tags/Studios/Genres are registered as CHEAP** (`FieldRegistry.cs:281-283`, `ExtractionGroup.ItemLists`, inside `CheapExtractionGroups`). `FieldRegistry.IsExpensiveField("Tags")` is **false**. The **only** thing promoting a parent-aware rule to the expensive tier is the hardcoded flag-name match in `SmartList.cs:66-71` (`IsParentAwareListExpression`). Miss it and the rule is evaluated in Phase 1 against an operand whose parent list `Factory` reset to `[]`, matching nothing, with **no exception and no log**.

7. **`Expression` property names ARE the on-disk JSON schema.** `SmartListFileSystem.cs:75-79` `SharedJsonOptions` sets no `UnmappedMemberHandling` (STJ default `Skip` → unknown keys silently discarded) and no `PropertyNameCaseInsensitive` (reads are case-sensitive). Every refresh re-serializes the whole DTO (`PlaylistStore.SaveAsync:124`, `CollectionStore.SaveAsync:104`, driven by `RefreshQueueService.cs:523/601`), so a removed property is **erased from disk**, not merely ignored.

8. **Extras have no parent.** Row `E173B706` (`Gag reel season 1`, `Video`, `ExtraType=0`) has an empty `ParentId` and `OwnerId` pointing at the `Modern Family` Series. Core's own `GetCollectionFolders` falls back to `GetOwner()`; so must the walk.

9. **The test stub throws on everything but `GetItemById`.** `Jellyfin.Plugin.SmartLists.Tests/Support/TestItems.cs:30-49`. A probe confirmed `GetInheritedTags()` and `GetAncestorIds()` both throw `NotSupportedException` there today. Existing fixtures never set `ParentId` and never register containers, so a naive walk test would pass vacuously.

---

## Adversarial Review Resolutions

Every blocker and major from the review is folded into the tasks below. Cross-reference:

### Blocker 1 — `CS0053` inconsistent accessibility
`RefreshQueueService` is `public class` (RefreshQueueService.cs:54) and `RefreshCache` is `public sealed class` (RefreshQueueService.cs:723), so a **public** property may not expose an **internal** type. Verified failure: `RefreshQueueService.cs(744,107): error CS0053`.
**Resolution:** `AncestorValues` is declared **`public sealed class`**; only `AncestorValueResolver` stays `internal static`. Verified to build clean (0 errors / 0 warnings) on both net9.0 and net10.0. → **Task 1, Task 2**.

### Blocker 2 — The grep gate contradicts the mandatory back-compat rule
"grep `IncludeParentSeries|IncludeParentAlbum` over the whole plugin must return hits ONLY in Expression.cs" would force deletion of the legacy reads in `config-rules.js:3202/3214/3226` and `config-lists.js:1658-1686`, which are exactly the back-compat hinge.
**Resolution:** the gate is restated as **two** greps — a negative one restricted to `--include='*.cs'`, and a **positive** one asserting the JS legacy reads still exist. → **Task 12, Step 1**.

### Blocker 3 — "one cache hit per episode" is false as originally designed
`var node = item.GetParent() ?? item.GetOwner();` executing **before** the memo lookup costs a full `ILibraryManager.GetItemById` round-trip per item. Today's code costs **zero** library calls on a memo hit (`TryGetEpisodeSeriesGuid` reads a property, and `Factory.cs:2196` consults the memo first). Measured: 10k episodes / 500 seasons goes from 50 `GetItemById` to ~10,550.
**Resolution:** the memo is keyed on `item.ParentId` — a plain `Guid` field needing no resolution — and consulted **before** any parent is materialized. Restores exact parity with today's memo-first pattern. → **Task 1, `Resolve` body**.

### Major 4 — `GenerateRuleSetHash` was left unspecified
There was no "dedicated cache-key section". Forgetting a flag here produces a stale-compiled-rule bug that only reproduces without a Jellyfin restart (`_ruleCache` is process-static, `SmartList.cs:50`, bounded at 1000 entries).
**Resolution:** the exact six replacement appends are spelled out verbatim. → **Task 8, Step 2**.

### Major 5 — Downgrade / cross-release-line reads — **WITHDRAWN, see Amendment 1**
The review assumed the two concurrent release lines in CLAUDE.md ("Release Lines") and proposed dual-writing the legacy flags so a list edited on a `12.x` build survived a rollback to the `10.11.X.0` stable line.
**Resolution: no dual-write.** Superseded by **Amendment 1** — the `10.11` stable line is retired, so the rollback this defended against cannot occur. Reading legacy flags stays mandatory; only the *writing* of legacy keys is dropped.

### Major 6 — Negative operators SHRINK, they do not grow
`Engine.cs:332-338`: `NotEqual|NotContains|IsNotIn` fold with `AndAlso`, so a larger ancestor value set can only **remove** items. Live DB: MusicArtist 50 rows / 42 with Genres, MusicAlbum 14 / 9 — an existing `Genres NotEqual X (include parent album)` audio rule changes results for most of that library.
**Resolution:** stated explicitly in docs **and** release notes, plus two dedicated e2e scenarios (G and H) that capture the item set **before** deploying and diff after. → **Task 11**, **Task 13 scenarios G/H**.

### Major 7 — Mutable static singleton reachable from every Operand
`AncestorValues.Empty` holding mutable `List<string>` assigned by reference into `Operand`'s public settable `List<string>` properties means a single stray mutation corrupts every item in every list for the remaining **process** lifetime.
**Resolution:** `AncestorValues` members are typed **`IReadOnlyList<string>`** (mutation is a compile error) and `Operand`'s three parent properties become `IReadOnlyList<string>` too, so assignment stays zero-copy. Every Engine consumer (`AnyItemEquals`, `AnyItemContains`, `AnyItemIsInList`, `AnyRegexMatch` — Engine.cs:1534/1554/1667/1729) takes `IEnumerable<string>`, so no call site changes. → **Task 1, Task 5**.

### Major 8 — "only runs on Phase-2 survivors" is false for the #495 shape
When the ONLY rule is a parent-aware Tags rule, `hasNonExpensiveRules` is false and `SmartList.cs:3026` takes the expensive-only branch, extracting full operands for **every** candidate item. A rule set with zero cheap rules also sets `hasExpensiveOnlyRuleSets = true` (SmartList.cs:3193-3195), promoting every item into `phase1Survivors` (SmartList.cs:3214-3217).
**Resolution:** the real bound is stated in the code comment and here — **worst case is one walk attempt per candidate item, not per Phase-2 survivor**. Combined with the Blocker 3 fix that is one dictionary lookup per item plus one walk per distinct container. Task 13 adds the single-rule-over-a-full-library scenario and records refresh wall-clock. → **Task 1 comment, Task 13 scenario I**.

### Major 9 — No invalidation; staleness blast radius grows
`_refreshCaches` is keyed **per user** (RefreshQueueService.cs:78, :636) and cleared only when the whole queue drains (:200-203) or on Dispose (:715). Each `AncestorValuesById` entry embeds the union of everything above it, so retagging one library invalidates ~550 descendant entries, versus today where retagging a Series invalidated exactly one and retagging a Season invalidated nothing.
**Resolution:** `AncestorValuesById` is cleared **per queue item** in a new `finally` block on `ProcessQueueItemAsync` (RefreshQueueService.cs:224-298, which currently has no `finally`). It is cheap to rebuild (one walk per distinct container) and this bounds staleness to a single list refresh. → **Task 2, Step 2**.

### Minor A — Truncated walk silently loses the library contribution
The original `seed = truncated || chain.Count == 0 ? Empty : GetLibraryValues(...)` short-circuits away the library values on the depth-cap path — the exact failure #495 is about.
**Resolution:** on truncation, **still** resolve library values from the deepest node reached (`GetCollectionFolders` walks independently of the chain and is unaffected by the cap). Keep the no-memo-write guard. The dead `chain.Count == 0` arm is dropped. → **Task 1**.

### Minor B — Cold-miss cost is 10x, not parity
Scenario A goes from 50 `RetrieveItem` calls (one per series) to ~550 (500 seasons + 50 series + folders). Jellyfin's LRU is `Environment.ProcessorCount * 100` (200 on a 2-core NAS), below the 500 distinct seasons, and the candidate query sets no `OrderBy` (`PlaylistService.cs:1271-1279`) so touch order is not guaranteed contiguous.
**Resolution:** documented honestly as `O(distinct containers)` DB reads + `O(items)` dictionary lookups. Task 13 compares refresh wall-clock against a **pre-change baseline** rather than assuming parity.

---

## Global Constraints

- Build treats all warnings as errors (`AnalysisMode=Recommended`); CA1822 (make static) and CA1305 (locale) **will** fail the build.
- Per-task verification: `cd dev && ./build-local.sh` (net10.0) plus `dotnet test`. Task 12 additionally verifies `JELLYFIN_ABI=10.11.0 ./build-local.sh` (net9.0).
- Frontend: **no ES6 template literals** (`grep -c '\`' config-rules.js` must stay `0`), string concatenation only, no arrow functions in these files. `is="emby-select"` is the existing and correct pattern (the `is=` ban is specific to `emby-input`).
- **No HTML changes.** `config.html` and `user-playlists.html` contain zero matches for `parent`/`tags-options`/`studios-options`/`genres-options` — all markup is generated at runtime by `config-rules.js`. No new JS files → no `.csproj` `EmbeddedResource` and no `Plugin.cs` `GetPages()` registration.
- Line numbers were captured on `main` at `80d68a1`. Treat them as anchors: locate the quoted code, don't trust raw numbers blindly. **Never** read `.claude/worktrees/**` (stale duplicates) or `docs/site/**` (generated).
- Work on branch `fix/495-ancestor-value-walk`. Commit after each task; commit messages end with:

```text
Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S9FFji6MP136o3bq298BWJ
```

---

## Back-Compat: exact old-flag → new-flag mapping

**Strategy: additive on disk, fold on read.** (The plan originally added "dual-write from the UI for one release"; **cancelled by Amendment 1** — new saves write only the new keys.) No migration, no deserialize-only shim, no restore-path hook, no deprecation window. `SmartListDto.MigrateLegacyFields` (SmartListDto.cs:157-180) and `StorageMigrationHostedService` are **not** touched.

### Persisted flags (all nine keep their exact names and behaviour)

| On-disk JSON key | Status after change | Read by | Written by |
|---|---|---|---|
| `IncludeParentSeriesTags` | **kept**, legacy | `IncludeParentTagsEffective` fold | **not written** (Amendment 1) |
| `IncludeParentAlbumTags` | **kept**, legacy | `IncludeParentTagsEffective` fold | **not written** (Amendment 1) |
| `IncludeParentTags` | **new** | `IncludeParentTagsEffective` fold | JS |
| `OnlyParentTags` | **unchanged** | read directly, `== true` | JS |
| `IncludeParentSeriesStudios` | **kept**, legacy | `IncludeParentStudiosEffective` fold | **not written** (Amendment 1) |
| `IncludeParentAlbumStudios` | **kept**, legacy | `IncludeParentStudiosEffective` fold | **not written** (Amendment 1) |
| `IncludeParentStudios` | **new** | `IncludeParentStudiosEffective` fold | JS |
| `OnlyParentStudios` | **unchanged** | read directly, `== true` | JS |
| `IncludeParentSeriesGenres` | **kept**, legacy | `IncludeParentGenresEffective` fold | **not written** (Amendment 1) |
| `IncludeParentAlbumGenres` | **kept**, legacy | `IncludeParentGenresEffective` fold | **not written** (Amendment 1) |
| `IncludeParentGenres` | **new** | `IncludeParentGenresEffective` fold | JS |
| `OnlyParentGenres` | **unchanged** | read directly, `== true` | JS |

### C# read-side fold (the ONE place new + legacy combine)

```
IncludeParentTags == true    OR IncludeParentSeriesTags == true    OR IncludeParentAlbumTags == true    -> IncludeParentTagsEffective
IncludeParentStudios == true OR IncludeParentSeriesStudios == true OR IncludeParentAlbumStudios == true -> IncludeParentStudiosEffective
IncludeParentGenres == true  OR IncludeParentSeriesGenres == true  OR IncludeParentAlbumGenres == true  -> IncludeParentGenresEffective
```

There are exactly **four** consumers, and all four read the fold, never the raw flags:

1. `Engine.BuildParentAwareListExpression` (Engine.cs:225/236/247)
2. `SmartList.IsParentAwareListExpression` (SmartList.cs:66-71)
3. `SmartList.GenerateRuleSetHash` (SmartList.cs:477-493)
4. `SmartList.FieldRequirements.Analyze` (SmartList.cs:3764-3769)

### Runtime-only renames (never serialized, safe to change)

| Old | New |
|---|---|
| `Operand.ParentSeriesTags` + `Operand.ParentAlbumTags` | `Operand.ParentTags` |
| `Operand.ParentSeriesStudios` + `Operand.ParentAlbumStudios` | `Operand.ParentStudios` |
| `Operand.ParentSeriesGenres` + `Operand.ParentAlbumGenres` | `Operand.ParentGenres` |
| `ExtractionGroup.ParentSeriesTags` (1<<8) + `ParentAlbumTags` (1<<22) | `ExtractionGroup.ParentTags` (1<<8) |
| `ExtractionGroup.ParentSeriesStudios` (1<<9) + `ParentAlbumStudios` (1<<23) | `ExtractionGroup.ParentStudios` (1<<9) |
| `ExtractionGroup.ParentSeriesGenres` (1<<10) + `ParentAlbumGenres` (1<<21) | `ExtractionGroup.ParentGenres` (1<<10) |
| `RefreshCache.{Series,Album}{Tags,Studios,Genres}ById` (6 dicts) | `RefreshCache.AncestorValuesById` (1 dict) |

### Semantics explicitly preserved

- `OnlyParentX = true` **with** a source → ancestors only. Unchanged.
- `OnlyParentX = true` **without** any source → still compiles to `Expression.Constant(false)` (Engine.cs:289-292). Both UI writers now always emit the Include flag alongside `OnlyParent`, so the state is unreachable from the UI; the branch survives as a safety net for hand-edited JSON and API consumers. `EngineOperatorTests.cs:947-954` survives with only a property rename.
- The six non-regex operators are provably identical across the 2-lists → 1-list collapse: `AnyItemEquals`/`AnyItemContains`/`AnyItemIsInList` are `list.Any(pred)` with null/empty → false, so `P(A) OR P(B) == P(A ∪ B)` and by De Morgan `¬P(A) AND ¬P(B) == ¬P(A ∪ B)`. **Keeping `BuildCombinedStringEnumerableExpression` (Engine.cs:316-346) completely unchanged is what guarantees this.**

### Two intended behaviour changes (release notes, not just docs)

1. Existing episode/audio lists with the option enabled **change contents on first refresh**. Positive operators (`Equal`, `Contains`, `IsIn`, `MatchRegex`) **gain** items (Season, physical-folder, library, and for audio MusicArtist-folder values now count). Negative operators (`NotEqual`, `NotContains`, `IsNotIn`) **lose** items, because they AND-fold. Also, a saved `IncludeParentSeriesTags` on an Audio-only list yields `[]` today and starts matching after the change.
2. `MatchRegex` is the one operator whose result can change on the *fold* itself. `AnyRegexMatch` (Engine.cs:1685-1692) is not a pure existential — for an **empty** list it tests the pattern against `string.Empty`. Today an episode rule OR-folds three terms and the permanently-empty `ParentAlbumTags` term makes any pattern matching `""` (`^$`, `.*`, negative lookaheads) match everything. Two terms shrink that surface: `Tags MatchRegex ^$ (include parent)` now matches only when both the item's own and its ancestors' tags are empty.

---

## File Structure

```
Jellyfin.Plugin.SmartLists/
├── Core/QueryEngine/
│   ├── AncestorValueResolver.cs      NEW  (AncestorValues + AncestorValueResolver)
│   ├── Expression.cs                 +3 flags, +3 folds, comment-only edits on 6 legacy
│   ├── Operand.cs                    6 lists -> 3, typed IReadOnlyList<string>
│   ├── Engine.cs                     builders lose 2 params; 2 dead helpers deleted
│   ├── Factory.cs                    6 extractors + 2 helpers + 2 caches deleted; 1 added
│   └── FieldRegistry.cs              6 bits -> 3, doc header collapsed
├── Core/SmartList.cs                 4 consumption sites repointed
├── Services/Shared/RefreshQueueService.cs   6 dicts -> 1; per-queue-item clear
└── Configuration/
    ├── config-rules.js               markup strings, updaters gutted, serialize, deserialize
    ├── config-lists.js               rule summary suffixes
    └── config-init.js                stale comment only

Jellyfin.Plugin.SmartLists.Tests/
├── Support/TestItems.cs              stub gains GetCollectionFolders; chained builders
├── Core/QueryEngine/EngineOperatorTests.cs   5 tests renamed to new members
└── Core/QueryEngine/AncestorWalkTests.cs     NEW
```

---

## Task 1: New file — `AncestorValues` + `AncestorValueResolver`

**Files:**
- Create: `Jellyfin.Plugin.SmartLists/Core/QueryEngine/AncestorValueResolver.cs`

**Interfaces:**
- Consumes: `MediaBrowser.Controller.Entities.BaseItem`, `MediaBrowser.Controller.Library.ILibraryManager`, `Microsoft.Extensions.Logging.ILogger`.
- Produces: `public sealed class AncestorValues` (Task 2 exposes it on a public property; Task 4 assigns it to `Operand`) and `internal static class AncestorValueResolver` with the single entry point `Resolve`.

- [ ] **Step 1: Write `AncestorValues`**

```csharp
/// <summary>
/// Ancestor-inherited Tags/Studios/Genres for one node of the item tree.
/// IMMUTABLE: instances are shared across operands via the per-refresh memo and are
/// assigned to Operand properties BY REFERENCE. Members are IReadOnlyList so that
/// mutation is a compile error rather than a comment-enforced convention — Empty is a
/// process-lifetime singleton and a stray mutation would corrupt every list, for every
/// user, for the rest of the process.
/// </summary>
public sealed class AncestorValues
{
    public static readonly AncestorValues Empty = new([], [], []);

    public IReadOnlyList<string> Tags { get; }
    public IReadOnlyList<string> Studios { get; }
    public IReadOnlyList<string> Genres { get; }

    private AncestorValues(IReadOnlyList<string> tags, IReadOnlyList<string> studios, IReadOnlyList<string> genres)
    {
        Tags = tags;
        Studios = studios;
        Genres = genres;
    }

    /// <summary>
    /// Returns a NEW instance holding this instance's values plus the node's own
    /// Tags/Studios/Genres. Returns <c>this</c> unchanged when the node contributes
    /// nothing, so a chain of value-less folders allocates nothing.
    /// </summary>
    public AncestorValues Union(BaseItem node) { /* see step 2 */ }
}
```

Requirements for the public `Empty` singleton: `IReadOnlyList<string> x = []` target-types to an empty collection — that is a non-null empty list, which matters because `AnyRegexMatch`'s empty-list branch runs for empty but **not** for null.

- [ ] **Step 2: Write `Union`**

`BaseItem` is nullable-**oblivious** in both ABIs (no `NullableContextAttribute`), so `node.Tags` is declared `string[]` yet can be null and **nothing warns** under `Nullable=enable` + `TreatWarningsAsErrors`. Null-guard each of the three arrays individually.

De-duplicate with **`StringComparer.Ordinal`**, not `OrdinalIgnoreCase`. Put this comment on the line:

```csharp
// Ordinal, deliberately NOT OrdinalIgnoreCase (which is what core's GetInheritedTags uses).
// Six of the seven operators are OrdinalIgnoreCase so dedup strength is invisible to them,
// but MatchRegex is case-SENSITIVE (GetOrCreateRegex passes RegexOptions.None) — case-insensitive
// dedup could discard the only casing a pattern would have matched.
```

Return `this` when all three of the node's arrays are null/empty.

- [ ] **Step 3: Write `AncestorValueResolver`**

```csharp
internal static class AncestorValueResolver
{
    // Runaway/cycle insurance ONLY; it must never bind. Measured real depth in the dev
    // library is 4 (episode->season->series->folder), 2 (movie), 3 (audio);
    // /music/Artist/Album/Disc/track reaches 5 and deep genre trees maybe 7.
    // Do NOT "tighten" this to 10 — a binding cap produces truncated results.
    private const int MaxAncestorDepth = 20;

    /// <summary>
    /// Ancestor-inherited values for <paramref name="item"/>, EXCLUDING the item's own
    /// values (Engine unions the item's own field separately).
    ///
    /// The walk is: parent chain (stopping BEFORE AggregateFolder/UserRootFolder/UserView)
    /// UNION libraryManager.GetCollectionFolders(chainTop). The second half is NOT optional:
    /// a CollectionFolder (a Jellyfin library) is never in the ParentId chain — it hangs off
    /// the UserRootFolder as a sibling structure — so a parents-only walk finds season tags
    /// but never library tags. Core's BaseItem.GetInheritedTags()/GetAncestorIds() have the
    /// same two-part shape.
    ///
    /// COST: the memo is keyed on the IMMEDIATE PARENT id and is consulted BEFORE any parent
    /// is materialized, so a hit costs one dictionary lookup and ZERO ILibraryManager calls —
    /// parity with the code this replaces. A miss costs O(distinct containers) GetItemById
    /// plus one GetCollectionFolders path-scan per top physical folder.
    /// NOTE: when a list's ONLY rule is parent-aware, SmartList takes the expensive-only path
    /// (SmartList.cs:3026) and this runs once per CANDIDATE item, not per Phase-2 survivor.
    /// </summary>
    internal static AncestorValues Resolve(
        BaseItem item,
        ILibraryManager libraryManager,
        ConcurrentDictionary<Guid, AncestorValues> memo,
        ILogger? logger)
    {
        // MEMO-FIRST on the raw ParentId Guid — do NOT call GetParent() before this line.
        // GetParent() is `ParentId.IsEmpty() ? null : LibraryManager.GetItemById(ParentId)`,
        // so materializing the parent first would cost a library round-trip per ITEM.
        var parentId = item.ParentId;
        if (!parentId.IsEmpty() && memo.TryGetValue(parentId, out var cached))
        {
            return cached;
        }

        var node = item.GetParent() ?? item.GetOwner();   // GetOwner() covers extras (empty ParentId, set OwnerId)
        if (node is null || IsWalkBoundary(node))
        {
            return AncestorValues.Empty;
        }

        var chain = new List<BaseItem>();
        var visited = new HashSet<Guid>();
        AncestorValues? seed = null;
        var truncated = false;

        while (node is not null && !IsWalkBoundary(node))
        {
            if (memo.TryGetValue(node.Id, out var hit)) { seed = hit; break; }
            if (!visited.Add(node.Id) || chain.Count >= MaxAncestorDepth)
            {
                logger?.LogWarning(
                    "SmartLists ancestor walk stopped early at '{Name}' ({Id}) - cycle or depth cap",
                    node.Name, node.Id);
                truncated = true;
                break;
            }
            chain.Add(node);
            node = node.GetParent() ?? node.GetOwner();
        }

        if (seed is null)
        {
            // Library values are resolved from the deepest node reached. GetCollectionFolders
            // walks independently of our chain, so it is valid (and required) even when the
            // chain was truncated — dropping it on truncation would silently reproduce #495.
            seed = GetLibraryValues(chain[^1], libraryManager, logger);
        }

        var acc = seed;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            acc = acc.Union(chain[i]);

            // NEVER memoize a truncated (partial) result. A truncated walk is missing the TOP
            // of the chain, so caching it would make later walks return a value that depends on
            // WHICH ITEM WARMED THE CACHE FIRST (an episode 4 levels down vs a series 1 level down).
            if (!truncated) { memo[chain[i].Id] = acc; }
        }

        return acc;
    }

    private static bool IsWalkBoundary(BaseItem node)
        => node is AggregateFolder || node is UserRootFolder || node is UserView;

    private static AncestorValues GetLibraryValues(BaseItem anchor, ILibraryManager libraryManager, ILogger? logger)
    {
        try
        {
            var acc = AncestorValues.Empty;
            foreach (var folder in libraryManager.GetCollectionFolders(anchor)) { acc = acc.Union(folder); }
            return acc;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "SmartLists failed to resolve library values for '{Name}'", anchor.Name);
            return AncestorValues.Empty;
        }
    }
}
```

Hard constraints for the implementer:
- `AncestorValues` **must** be `public sealed class` (Task 2 exposes it on a public property — `internal` produces `CS0053`).
- Use the **1-arg** `GetCollectionFolders(BaseItem)` overload. It is already used at `Factory.cs:3925`, so it is proven to compile on both ABIs.
- Never call `BaseItem.GetAncestorIds()` or `GetInheritedTags()` — both reach `GetCollectionFolders` in a way the test stub throws on, `GetInheritedTags` has no Studios/Genres counterpart, and it unions the item's own tags in.
- Never reuse `Factory._knownUnsupportedTypes` (Factory.cs:219-222) as the boundary test — it is a log-noise filter used at exactly one site (:3504), it omits `UserView` and `PlaylistsFolder`, and it includes plain `Folder`, which the walk **must** traverse.
- **No negative caching.** A `GetItemById` miss simply makes `GetParent()` return null, ending the chain, which still yields the library values via the seed. Do not write `Empty` into the memo on failure (the six extractors did that at Factory.cs:2223/2285/2347 and 2019; with recursive memoization that would poison a whole subtree).

- [ ] **Step 4: Build**

Run: `cd dev && ./build-local.sh`
Expected: succeeds with the file compiling standalone (nothing references it yet). Watch CA1822 and CA1305 on the new logging calls.

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Core/QueryEngine/AncestorValueResolver.cs
git commit -m "Add memoized ancestor value walk (parents + library folders)"
```

---

## Task 2: RefreshCache — one memo, cleared per queue item

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Services/Shared/RefreshQueueService.cs` (~lines 738-743, and `ProcessQueueItemAsync` at ~224-298)

**Interfaces:**
- Consumes: `AncestorValues` (Task 1).
- Produces: `RefreshCache.AncestorValuesById`. Task 4 writes and reads it.

- [ ] **Step 1: Swap the six dicts for one**

Delete these six lines (RefreshQueueService.cs:738-743):

```csharp
            public ConcurrentDictionary<Guid, List<string>> SeriesTagsById { get; } = new();
            public ConcurrentDictionary<Guid, List<string>> SeriesStudiosById { get; } = new();
            public ConcurrentDictionary<Guid, List<string>> SeriesGenresById { get; } = new();
            public ConcurrentDictionary<Guid, List<string>> AlbumGenresById { get; } = new();
            public ConcurrentDictionary<Guid, List<string>> AlbumTagsById { get; } = new();
            public ConcurrentDictionary<Guid, List<string>> AlbumStudiosById { get; } = new();
```

Replace with:

```csharp
            /// <summary>
            /// Ancestor-inherited Tags/Studios/Genres, keyed by the ANCESTOR NODE id (season id,
            /// album id, folder id) — never the item id — so every episode of a season is a single
            /// lookup and the walk itself runs once per container. Each entry is the COMPLETE union
            /// for that node and everything above it INCLUDING the library CollectionFolder, so a
            /// memo hit needs no further work. Cleared per queue item (see ProcessQueueItemAsync)
            /// rather than per queue drain: entries embed everything above them, so a stale entry
            /// after a retag would otherwise affect a whole subtree until the queue emptied.
            /// </summary>
            public ConcurrentDictionary<Guid, AncestorValues> AncestorValuesById { get; } = new();
```

Leave `SeriesNameById` (:736) and `SeriesSortNameById` (:737) **untouched** — they belong to `ExtractSeriesName` and to sorting. Add `using Jellyfin.Plugin.SmartLists.Core.QueryEngine;` if absent (verified: no name collision).

- [ ] **Step 2: Clear the memo per queue item**

`ProcessQueueItemAsync` (~line 224) currently has `try` / `catch(OperationCanceledException)` / `catch(Exception)` and **no** `finally`. Add one after the last catch block (~line 297):

```csharp
            finally
            {
                // Bound ancestor-memo staleness to a single list refresh. RefreshCache itself is
                // per-user and survives until the whole queue drains, but each AncestorValuesById
                // entry embeds every value above it, so a season/library retag would otherwise
                // stay invisible across an entire batch. Rebuilding costs one walk per container.
                foreach (var cache in _refreshCaches.Values)
                {
                    cache.AncestorValuesById.Clear();
                }
            }
```

- [ ] **Step 3: Build and commit**

```bash
cd dev && ./build-local.sh
git add Jellyfin.Plugin.SmartLists/Services/Shared/RefreshQueueService.cs
git commit -m "Replace six parent-value caches with one ancestor memo, cleared per queue item"
```

(The build will fail until Task 4 removes the extractors that reference the deleted dicts. If executing tasks in isolation, run `dotnet build` and accept only errors naming `SeriesTagsById`/`AlbumTagsById`/etc. in `Factory.cs`; verify no other file references them: `grep -rn 'SeriesTagsById\|SeriesStudiosById\|SeriesGenresById\|AlbumTagsById\|AlbumStudiosById\|AlbumGenresById' --include='*.cs' .` should hit only `Factory.cs`.)

---

## Task 3: FieldRegistry — six extraction bits become three

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Core/QueryEngine/FieldRegistry.cs` (~lines 50-55, 74-82)

**Interfaces:**
- Produces: `ExtractionGroup.ParentTags`, `ParentStudios`, `ParentGenres`. Tasks 4 and 8 consume them.

- [ ] **Step 1: Collapse the enum members**

Delete lines 74-76 (`ParentSeriesTags = 1 << 8`, `ParentSeriesStudios = 1 << 9`, `ParentSeriesGenres = 1 << 10`) and lines 80-82 (`ParentAlbumGenres = 1 << 21`, `ParentAlbumTags = 1 << 22`, `ParentAlbumStudios = 1 << 23`). At the `1 << 8` position, **inside the expensive block** (above the `// Cheap extraction groups` comment at line 84), insert:

```csharp
        ParentTags = 1 << 8,          // Fields: Tags (with IncludeParentTags) | Cache: AncestorValuesById
        ParentStudios = 1 << 9,       // Fields: Studios (with IncludeParentStudios) | Cache: AncestorValuesById
        ParentGenres = 1 << 10,       // Fields: Genres (with IncludeParentGenres) | Cache: AncestorValuesById
```

Add `// (1 << 21 .. 1 << 23 free)` next to `ExternalLists = 1 << 20`. Renumbering is safe: `ExtractionGroup` is never serialized (only `RandomGroupSelectionDto` maps a string to a group).

- [ ] **Step 2: Collapse the doc header**

Replace the six `CACHING REQUIREMENTS BY GROUP` lines at 50-55 with three naming `AncestorValuesById`, and fix their stray 8-space indentation to the 4-space of their neighbours.

- [ ] **Step 3: Do NOT touch two things**

- Do **not** add the new bits to `CheapExtractionGroups` (:168-171). If they leak in, `phase1Groups` (SmartList.cs:3174) stops deferring and the walk runs for every candidate item unconditionally.
- Do **not** touch the `Genres` / `Studios` / `Tags` field registrations at :281-283 — they stay `ExtractionGroup.ItemLists` (cheap), which is what keeps `FieldRegistryInvariantTests.cs:468-469` green with no edits.

- [ ] **Step 4: Verify and commit**

```bash
dotnet test --filter FullyQualifiedName~FieldRegistryInvariantTests
```
Expected: `IsExpensiveField_PinsTheKnownTierOfEachExtractionGroup` and `ExtractionGroupMembership_MatchesEachFieldsDeclaredFlags` pass with **zero** edits to that test file. If either needed editing, the tier boundary was moved by mistake.

```bash
git add Jellyfin.Plugin.SmartLists/Core/QueryEngine/FieldRegistry.cs
git commit -m "Collapse six parent extraction groups into ParentTags/ParentStudios/ParentGenres"
```

---

## Task 4: Factory — delete six extractors, add one

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Core/QueryEngine/Factory.cs`

**Interfaces:**
- Consumes: `AncestorValueResolver.Resolve` (Task 1), `RefreshCache.AncestorValuesById` (Task 2), `ExtractionGroup.Parent*` (Task 3).
- Produces: `MediaTypeExtractionOptions.ExtractParentTags/ExtractParentStudios/ExtractParentGenres`; writes `Operand.ParentTags/ParentStudios/ParentGenres` (Task 5).

- [ ] **Step 1: Delete**

| What | Location |
|---|---|
| `ExtractParentSeriesTags` | 2180-2236 |
| `ExtractParentSeriesStudios` | 2242-2298 |
| `ExtractParentSeriesGenres` | 2304-2360 |
| `ExtractParentAlbumGenres` | 2366-2415 |
| `ExtractParentAlbumTags` | 2421-2470 |
| `ExtractParentAlbumStudios` | 2476-2525 |
| `TryGetAudioAlbumGuid` | 1942-1957 (no other caller) |
| `GetOrFetchParentValues` + `TryResolveParentKey` delegate | 1987-2022 (the album trio was its only user) |
| `_albumIdPropertyCache`, `_parentIdPropertyCache` | 321-322 (orphaned by the above) |

**Do NOT delete** `TryGetEpisodeSeriesGuid` (1923-1936), `TryExtractGuid` (1959-1985) or `_seriesIdPropertyCache` (320) — `ExtractSeriesName` (2035) and the NextUnwatched path (3412) still use them, and reusing `SeriesId` in the walk would re-introduce the exact Season-skip that **is** bug #495. Leave `ExtractSeriesName`/`ResolveAndCacheSeriesName`/`ExtractSeriesNameFromExtra` (2029-2107) and `ExtractLibraryNames` (3909-3957) entirely untouched.

- [ ] **Step 2: Collapse the options bools** (Factory.cs:97-131)

Replace the six get/set-over-a-bit properties with three:

```csharp
        public bool ExtractParentTags
        {
            get => RequiredGroups.HasFlag(ExtractionGroup.ParentTags);
            set => RequiredGroups = value ? RequiredGroups | ExtractionGroup.ParentTags : RequiredGroups & ~ExtractionGroup.ParentTags;
        }
```

…and the `ParentStudios` / `ParentGenres` equivalents.

- [ ] **Step 3: Add the single extractor**

```csharp
        /// <summary>
        /// Resolves ancestor-inherited Tags/Studios/Genres in ONE walk and assigns only the
        /// requested lists. Assignment is BY REFERENCE — AncestorValues is immutable and its
        /// members are IReadOnlyList, so sharing across operands is safe and allocation-free.
        /// </summary>
        private static void ExtractAncestorValues(
            Operand operand,
            BaseItem baseItem,
            ILibraryManager libraryManager,
            RefreshQueueService.RefreshCache cache,
            ILogger? logger,
            bool wantTags,
            bool wantStudios,
            bool wantGenres)
        {
            try
            {
                var values = AncestorValueResolver.Resolve(baseItem, libraryManager, cache.AncestorValuesById, logger);
                if (wantTags) { operand.ParentTags = values.Tags; }
                if (wantStudios) { operand.ParentStudios = values.Studios; }
                if (wantGenres) { operand.ParentGenres = values.Genres; }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "SmartLists failed to resolve ancestor values for '{Name}'", baseItem.Name);
            }
        }
```

- [ ] **Step 4: Replace the four hoisted locals** (Factory.cs:2877-2880)

Replace `extractParentSeriesTags` / `extractParentSeriesStudios` / `extractParentSeriesGenres` / `extractParentAlbumGenres` with three: `extractParentTags`, `extractParentStudios`, `extractParentGenres`. Do **not** reproduce the existing asymmetry where `ParentAlbumTags`/`ParentAlbumStudios` were read as `options.*` directly at 3354/3365.

- [ ] **Step 5: Replace the six dispatch blocks with one** (Factory.cs:3308-3372)

```csharp
            // Ancestor-inherited Tags/Studios/Genres (season/series/album/artist/folder/library).
            // Expensive (tree walk + library lookup), so gated on the requirement flags and
            // memoized per ancestor node. The else-branch reset is load-bearing: Phase 1 builds
            // its operand with the parent groups masked off, and must never see stale values.
            if (extractParentTags || extractParentStudios || extractParentGenres)
            {
                ExtractAncestorValues(operand, baseItem, libraryManager, cache, logger,
                    extractParentTags, extractParentStudios, extractParentGenres);
            }

            if (!extractParentTags) { operand.ParentTags = []; }
            if (!extractParentStudios) { operand.ParentStudios = []; }
            if (!extractParentGenres) { operand.ParentGenres = []; }
```

There are **no** `is not Episode` / `is not Audio` guards — dropping them **is** the all-media-types broadening.

- [ ] **Step 6: Verify and commit**

```bash
cd dev && ./build-local.sh
grep -n 'ParentSeriesTags\|ParentAlbumTags\|ParentSeriesStudios\|ParentAlbumStudios\|ParentSeriesGenres\|ParentAlbumGenres\|GetOrFetchParentValues\|TryGetAudioAlbumGuid\|_albumIdPropertyCache\|_parentIdPropertyCache' Jellyfin.Plugin.SmartLists/Core/QueryEngine/Factory.cs
grep -n 'TryGetEpisodeSeriesGuid' Jellyfin.Plugin.SmartLists/Core/QueryEngine/Factory.cs
```
Expected: the first grep returns **nothing**; the second still returns its definition plus the `ExtractSeriesName` and NextUnwatched call sites.

```bash
git add Jellyfin.Plugin.SmartLists/Core/QueryEngine/Factory.cs
git commit -m "Replace six one-level parent extractors with one ancestor walk"
```

---

## Task 5: Operand — six parent lists become three

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Core/QueryEngine/Operand.cs` (lines 28-33)

- [ ] **Step 1: Replace**

```csharp
        public IReadOnlyList<string> ParentTags { get; set; } = [];
        public IReadOnlyList<string> ParentStudios { get; set; } = [];
        public IReadOnlyList<string> ParentGenres { get; set; } = [];
```

Notes:
- `IReadOnlyList<string>` (not `List<string>`) so the shared, process-lifetime `AncestorValues.Empty` singleton cannot be mutated through an operand. Every Engine consumer takes `IEnumerable<string>` (`AnyItemEquals`/`AnyItemContains`/`AnyItemIsInList`/`AnyRegexMatch`, Engine.cs:1534/1554/1667/1729), and `IReadOnlyList<string>` is reference-assignable to `IEnumerable<string>`, so `Expression.Call` argument validation succeeds unchanged.
- The `= []` initializers are **load-bearing**, not cosmetic: `AnyRegexMatch`'s empty-list branch tests the pattern against `string.Empty`, whereas a null list returns false — null and empty are observably different for `MatchRegex`.
- Engine reaches these **only** via `Expression.PropertyOrField(param, "<literal>")`, so a spelling mismatch with Engine's string literals compiles clean and throws `ArgumentException` at rule-compile time (per refresh), never at build time. Task 9 adds the test that catches it.

- [ ] **Step 2: Build**

The build now fails **only** in `EngineOperatorTests` object initializers. That compile failure is the intended guard rail; Task 9 fixes it.

- [ ] **Step 3: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Core/QueryEngine/Operand.cs
git commit -m "Collapse six Operand parent lists into ParentTags/ParentStudios/ParentGenres"
```

---

## Task 6: Expression — three new flags plus three folds (purely additive)

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Core/QueryEngine/Expression.cs`

**Interfaces:**
- Produces: `IncludeParentTags`/`IncludeParentStudios`/`IncludeParentGenres` (persisted) and `IncludeParentTagsEffective`/`IncludeParentStudiosEffective`/`IncludeParentGenresEffective` (computed). Tasks 7 and 8 read only the `*Effective` folds.

- [ ] **Step 1: Insert the three new flags**

Immediately after `OnlyParentGenres` (~line 65), matching the surrounding style exactly:

```csharp
        // Tags-specific option: also match values inherited from ancestors (season, series, album, folder, library) - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeParentTags { get; set; } = null;

        // Studios-specific option: also match values inherited from ancestors - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeParentStudios { get; set; } = null;

        // Genres-specific option: also match values inherited from ancestors - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeParentGenres { get; set; } = null;
```

- [ ] **Step 2: Insert the three folds directly after them**

```csharp
        // Legacy parent flags fold into these. This is the ONLY place new + legacy are combined —
        // Engine, IsParentAwareListExpression, GenerateRuleSetHash and FieldRequirements.Analyze
        // all read these, never the raw flags.
        [JsonIgnore]
        public bool IncludeParentTagsEffective => IncludeParentTags == true || IncludeParentSeriesTags == true || IncludeParentAlbumTags == true;

        [JsonIgnore]
        public bool IncludeParentStudiosEffective => IncludeParentStudios == true || IncludeParentSeriesStudios == true || IncludeParentAlbumStudios == true;

        [JsonIgnore]
        public bool IncludeParentGenresEffective => IncludeParentGenres == true || IncludeParentSeriesGenres == true || IncludeParentAlbumGenres == true;
```

Use plain `[JsonIgnore]`, **not** `WhenWritingDefault` — these are get-only so STJ never binds them, and they must never be written.

- [ ] **Step 3: Comment-only edits on the six legacy flags**

Change **only** the `//` comments above `IncludeParentSeriesTags` (33), `IncludeParentAlbumTags` (37), `IncludeParentSeriesStudios` (45), `IncludeParentAlbumStudios` (49), `IncludeParentSeriesGenres` (57), `IncludeParentAlbumGenres` (61) to:

```csharp
        // Legacy - read via IncludeParent*Effective, still written by the UI for downgrade
        // compatibility. Do NOT remove: this is an on-disk JSON key.
```

Touch **nothing** about `OnlyParentTags` (41), `OnlyParentStudios` (53), `OnlyParentGenres` (65).

- [ ] **Step 4: Build and commit**

```bash
cd dev && ./build-local.sh
git add Jellyfin.Plugin.SmartLists/Core/QueryEngine/Expression.cs
git commit -m "Add IncludeParent{Tags,Studios,Genres} plus legacy-folding Effective properties"
```

---

## Task 7: Engine — repoint the builders, delete two dead helpers

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Core/QueryEngine/Engine.cs`

- [ ] **Step 1: `BuildParentAwareListExpression`** (223-259)

Keep the three `if` blocks and the `ModelExpression r` parameter name. Each now passes a single parent field and a single include flag:

```csharp
            if (r.MemberName == "Tags")
            {
                return BuildParentAwareFieldExpression(r, param, logger,
                    baseField: "Tags",
                    parentField: "ParentTags",
                    onlyParent: r.OnlyParentTags == true,
                    includeParent: r.IncludeParentTagsEffective);
            }
```

…and the `Studios` → `"ParentStudios"` / `r.OnlyParentStudios` / `r.IncludeParentStudiosEffective` and `Genres` → `"ParentGenres"` / `r.OnlyParentGenres` / `r.IncludeParentGenresEffective` equivalents. Keep the `== true` coercion on `OnlyParentX` so null and false stay identical.

- [ ] **Step 2: `BuildParentAwareFieldExpression`** (266-305)

Drop the `parentSeriesField`/`parentAlbumField` pair for a single `parentField`, and drop `includeParentAlbum`. The field list becomes `[parentField]` for only-parent and `[baseField, parentField]` for include.

**KEEP verbatim**: the `System.Linq.Expressions.Expression.Constant(false)` branch at 289-292 and its XML doc at 262-265. `OnlyParentX = true` with no source still matches nothing.

- [ ] **Step 3: Leave three things COMPLETELY unchanged**

- `BuildCombinedStringEnumerableExpression` (316-346). Its per-field OR/AND fold with `isNegativeOperator = NotEqual|NotContains|IsNotIn` is precisely what makes the collapse provably safe for the six non-regex operators. `Aggregate` over a one-element list returns that element, so the only-parent case needs nothing special.
- `BuildStringEnumerableExpression` (1272-1360), including its `MemberExpression` parameter type — folding two terms needs no widening.
- The `BuildExpr` hook at line 144.

- [ ] **Step 4: Delete two dead helpers**

`OnlyCombinedItemEquals` (1643-1653) and `AnyCombinedItemEquals` (1660-1665). Zero callers, not resolved by any `GetMethod` string — **and their XML docs falsely claim to implement this exact parent path**, so leaving them will mislead the next reader into thinking the old two-list design is still live. Say that in the commit message.

Do **not** touch `OnlyItemEquals`/`OnlyCollectionEquals`/`OnlyPlaylistEquals` (1595/1603/1611) — production-unreferenced but exercised by `EngineInternalsTests`, pre-existing and unrelated.

- [ ] **Step 5: Build and commit**

```bash
cd dev && ./build-local.sh
git add Jellyfin.Plugin.SmartLists/Core/QueryEngine/Engine.cs
git commit -m "Point parent-aware builders at the single ancestor field; drop two dead combined helpers whose docs misdescribed this path"
```

---

## Task 8: SmartList — the four consumption sites

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Core/SmartList.cs`

> This task carries the highest silent-breakage risk in the change. All four edits are compile-clean if wrong; only the tests in Task 9 catch them.

- [ ] **Step 1: `IsParentAwareListExpression`** (66-71) — THE critical edit

```csharp
        private static bool IsParentAwareListExpression(Expression expr)
        {
            return (expr.MemberName == "Tags" && (expr.IncludeParentTagsEffective || expr.OnlyParentTags == true)) ||
                   (expr.MemberName == "Studios" && (expr.IncludeParentStudiosEffective || expr.OnlyParentStudios == true)) ||
                   (expr.MemberName == "Genres" && (expr.IncludeParentGenresEffective || expr.OnlyParentGenres == true));
        }
```

Why this matters: Tags/Studios/Genres are registered `ItemLists` (cheap), so `FieldRegistry.IsExpensiveField("Tags")` is **false**, and this hardcoded predicate is the **only** thing promoting a parent-aware rule to the expensive tier. Miss it and the rule lands in `cheapCompiledRules` (:3003), is evaluated in Phase 1 against an operand whose `ParentTags` Factory reset to `[]` (phase1Groups masking at :3174), and matches **nothing** — no exception, no log, a fresh instance of #495.

- [ ] **Step 2: `GenerateRuleSetHash`** (477-493) — exact replacement

Delete the nine `Append` pairs and replace with **exactly** these six, keeping the surrounding `hashBuilder.Append(':');` separators and the position between `UserId` (:475) and `IncludeCollectionOnly` (:495):

```csharp
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludeParentTagsEffective);
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.OnlyParentTags?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludeParentStudiosEffective);
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.OnlyParentStudios?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludeParentGenresEffective);
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.OnlyParentGenres?.ToString() ?? "null");
```

Notes:
- Folding to a non-nullable `bool` is safe here **because the fold is a total function of behaviour**: two expressions that fold identically compile identically, so they may share a cache entry.
- Keep the surviving `OnlyParentX` in their existing `?.ToString() ?? "null"` form.
- `Id` is already in the key (SmartList.cs:423), so there is no cross-list collision.
- Forgetting any of these produces a stale-compiled-rule bug that only reproduces **without** a Jellyfin restart: `_ruleCache` (SmartList.cs:50) is process-static, holds up to 1000 entries and cleans up no more often than every 5 minutes, so it will not self-heal.

- [ ] **Step 3: `FieldRequirements` accessors** (3696-3701)

Six become three:

```csharp
            public bool NeedsParentTags => RequiredGroups.HasFlag(ExtractionGroup.ParentTags);
            public bool NeedsParentStudios => RequiredGroups.HasFlag(ExtractionGroup.ParentStudios);
            public bool NeedsParentGenres => RequiredGroups.HasFlag(ExtractionGroup.ParentGenres);
```

(Nothing reads any of the six today; they are kept only because every other group in that block follows this convention.)

- [ ] **Step 4: `FieldRequirements.Analyze`** (3764-3769)

Six `AddParentGroupIfIncluded` calls become three:

```csharp
                    AddParentGroupIfIncluded(requirements, expr.MemberName, "Tags", expr.IncludeParentTagsEffective, ExtractionGroup.ParentTags);
                    AddParentGroupIfIncluded(requirements, expr.MemberName, "Studios", expr.IncludeParentStudiosEffective, ExtractionGroup.ParentStudios);
                    AddParentGroupIfIncluded(requirements, expr.MemberName, "Genres", expr.IncludeParentGenresEffective, ExtractionGroup.ParentGenres);
```

The helper at 3721-3730 takes `bool? includeFlag`, so the plain `bool` converts implicitly — **no signature change**. Reword the comment at 3761-3763 (`"OnlyParent* alone does NOT trigger extraction of both groups — use IncludeParent* to decide which"`) since there is no longer a "which"; the behaviour is unchanged and deliberate — `OnlyParent`-with-no-source compiles to constant-false and correctly requests no extraction.

- [ ] **Step 5: Leave these untouched (verify only)**

Two-phase entry decision (2929-2935), Phase-1 masking (3172-3181), per-rule split (2985-3006), `hasNonExpensiveRules` (1005-1019), and all four `FromRequirements` call sites (2533/3047/3258/3424) — all bit-count-agnostic. Confirm the two hand-built options objects (`DoesSeriesMatchCollectionsRules` 2330-2342, MaxPlayTime fallback 2874-2885) still leave the new flags off.

- [ ] **Step 6: Build and commit**

```bash
cd dev && ./build-local.sh
git add Jellyfin.Plugin.SmartLists/Core/SmartList.cs
git commit -m "Point expensiveness gate, rule-cache hash and requirement analysis at the folded parent flags"
```

---

## Task 9: Tests

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists.Tests/Support/TestItems.cs`
- Modify: `Jellyfin.Plugin.SmartLists.Tests/Core/QueryEngine/EngineOperatorTests.cs`
- Create: `Jellyfin.Plugin.SmartLists.Tests/Core/QueryEngine/AncestorWalkTests.cs`

**Interfaces:**
- Consumes everything from Tasks 1-8. `AncestorValueResolver` is `internal` and `InternalsVisibleTo` is already wired (`Jellyfin.Plugin.SmartLists.csproj:25`), so `Resolve` is directly reachable without stubbing `IUserManager`/`IUserDataManager`.

- [ ] **Step 1: Extend `TestLibraryManager`** (TestItems.cs:30-49)

The stub implements only `GetItemById` and throws `NotSupportedException` on everything else — a probe confirmed `GetInheritedTags()` and `GetAncestorIds()` both throw there today. Add **one** deliberate arm:

```csharp
        // Registered by CHAIN-TOP folder id — that is the anchor the resolver passes to
        // GetCollectionFolders, and it is deliberately not the item id.
        internal static readonly ConcurrentDictionary<Guid, List<Folder>> CollectionFolders = new();
```

…returning `CollectionFolders.TryGetValue(id, out var f) ? f : new List<Folder>()` for `GetCollectionFolders(BaseItem)`. Keep throw-by-default for everything else; that guard is deliberate ("if production code under test ever starts depending on more of the library manager, these tests must fail loudly").

- [ ] **Step 2: Add chained fixture builders** (TestItems.cs, near 106-167)

Nothing in `TestItems` currently produces an item whose `GetParent()` returns non-null — `Ep` (119-147) sets `SeriesId` but never `ParentId`, and `SeasonOf`/`Album` (151-165) are never registered in `TestLibraryManager.Items`. Add:

```csharp
        public static T Under<T>(T child, BaseItem parent) where T : BaseItem   // sets child.ParentId, registers BOTH in Items
        public static Folder PhysicalFolder(string name, params string[] tags)
```

Constraints: use fresh `Guid.NewGuid()` (Items is process-wide and never cleared), set `Name` **before** `SortName` (reading an unset `SortName` throws NRE), terminate chains with a null parent rather than constructing an `AggregateFolder`, and return plain `Folder` instances with `Tags` from the `CollectionFolders` stub rather than a real `CollectionFolder`.

- [ ] **Step 3: Rewrite the five existing tests** (EngineOperatorTests.cs:893-966)

Rename members only; **assertions stay verbatim**.

| Test | Change |
|---|---|
| `CompileRule_TagsIncludingParentSeries_OrsItemAndParentForPositiveOperators` | `IncludeParentSeriesTags` → `IncludeParentTags`; `Operand.ParentSeriesTags` → `ParentTags`. Rename to `..._TagsIncludingParent_...` |
| `CompileRule_TagsIncludingParentSeries_AndsItemAndParentForNegativeOperators` | same rename. **Assertions must not change** — the positive-OR / negative-AND asymmetry is the one non-obvious semantic and must survive |
| `CompileRule_TagsOnlyParentSeries_IgnoresTheItemsOwnTags` | `IncludeParentSeriesTags` + `OnlyParentTags` → `IncludeParentTags` + `OnlyParentTags` |
| `CompileRule_TagsOnlyParentWithNoParentSource_MatchesNothing` | **KEPT AS-IS** apart from the member rename — that semantic is deliberately preserved |
| `CompileRule_GenresIncludingParentAlbum_OrsItemAndParent` | `IncludeParentAlbumGenres` → `IncludeParentGenres`; `ParentAlbumGenres` → `ParentGenres` |

- [ ] **Step 4: Add `AncestorWalkTests`** — named cases

| # | Test name | What it proves |
|---|---|---|
| 1 | `Resolve_FindsTagSetOnTheSeason_TheLevelTheOldCodeSkipped` | episode → season → series → folder; the **season's** tag is returned. This is #495 proper. **Must FAIL if the walk is reverted to `SeriesId`.** |
| 2 | `Resolve_UnionsLibraryFolderValues_NotReachableViaParentChain` | same chain, a tag registered on the stubbed `GetCollectionFolders(chainTop)` is returned. **Must FAIL if `GetCollectionFolders` is dropped from the resolver** — this is the half a parents-only walk would miss. |
| 3 | `Resolve_CollectsTagsStudiosAndGenresInOneWalk` | all three kinds come back from a single call |
| 4 | `Resolve_MemoHitCostsNoLibraryCalls` | second episode of the same season resolves from the memo; assert the stub's `GetItemById` call counter did not increase (proves the **`ParentId` memo-first** fix from Blocker 3) |
| 5 | `Resolve_MemoizesOneEntryPerAncestorNode` | two episodes of one season → exactly one memo entry per ancestor, not per item |
| 6 | `Resolve_CycleTerminatesAndWritesNoMemoEntries` | `A.ParentId=B`, `B.ParentId=A` → returns without hanging and `memo.Count == 0` |
| 7 | `Resolve_TruncatedWalkStillReturnsLibraryValues` | depth-cap path still unions `GetCollectionFolders` (Minor A) |
| 8 | `Resolve_ExtraWithNoParentResolvesViaOwner` | empty `ParentId`, set `OwnerId` → the owner's chain is walked |
| 9 | `Resolve_StopsAtWalkBoundary_DoesNotClimbIntoAggregateFolder` | boundary type test |
| 10 | `Resolve_DedupesOrdinallyAcrossLevels` | `Series01` on the library and `series01` on the season both survive (Ordinal, not OrdinalIgnoreCase) |
| 11 | `CompileRule_TagsNotEqual_AcrossTwoLevelWalk_ExcludesWhenAnyAncestorMatches` | negated operator across a 2-level walk |
| 12 | `CompileRule_TagsMatchRegexEmptyPattern_WithAndWithoutAncestorValues` | locks the `^$` empty-list semantics both ways |
| 13 | `IsNonExpensiveExpression_ClassifiesParentAwareTagsRuleAsExpensive` | **new coverage for the #1 silent-failure mode** — nothing exercises `IsParentAwareListExpression` today |
| 14 | `GenerateRuleSetHash_DiffersWhenIncludeParentTagsToggles` | **new coverage** — nothing exercises the rule-cache key today |
| 15 | `Expression_LegacyAlbumFlagFoldsIntoIncludeParentTagsEffective` | round-trip a rule carrying only `"IncludeParentAlbumTags": true` and assert the fold is true and no `*Effective` property is serialized |
| 16 | `CompileRule_UsingNewParentFields_DoesNotThrowArgumentException` | catches an Engine-literal ↔ Operand-property spelling mismatch, which otherwise only fails at refresh time |

- [ ] **Step 5: Verify and commit**

```bash
dotnet test
```
Expected: all green. Note the test project is net10.0-only by design; the walk uses no 12-only API (`GetParent`/`GetOwner`/`GetCollectionFolders` are byte-identical in both ABIs).

```bash
git add Jellyfin.Plugin.SmartLists.Tests/
git commit -m "Test ancestor walk, library union, memoization, expensiveness gate and rule-cache key"
```

---

## Task 10: Frontend

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-rules.js`
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-lists.js`
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-init.js`

**Non-goals:** no HTML changes, no `config-core.js` changes (`shouldShowField` at config-rules.js:2226-2266 already offers Tags/Studios/Genres for every media type), no `config-templates.js` changes, no `syncAdvancedSection` changes (the dropdowns live in the rule row, not in `#advanced-options-body`).

**Keep every function name.** `updateTagsOptionsVisibility` / `updateStudiosOptionsVisibility` / `updateGenresOptionsVisibility` and the three `updateAll*OptionsVisibility` wrappers keep their exact names and the exact `(ruleRow, fieldValue, page)` signature — `page` becomes unused but **must** stay, because `updateAllRules` (2778-2790) hard-codes `updateFunction(ruleRow, fieldSelect.value, page)`. Do **not** merge the three into one: that would force edits at six external call sites, two of which sit behind `if (SmartLists.updateAllXxx)` existence guards that fail **silently** on a rename (the divs would simply never appear, with no console error). Keeping the names means zero edits at those six sites and zero edits at the seven internal call sites (1312-1319, 1352-1354, 1944-1946, 1975-1977, 2171-2173, 2188-2190, 3111-3113).

- [ ] **Step 1: Rewrite the twelve dropdown strings** (config-rules.js:1204-1233)

Keep container divs, inline styles, class names and `is="emby-select" class="emby-select ..."` byte-identical. Keep option **values** `'false'` / `'true'` / `'only'` — they are the wire contract shared by `extractRuleConfig`, `collectRulesFromForm` and `populateRuleRow`. Since the runtime label-swapping is deleted, this static text becomes the final user-visible text.

| Block | Label | value="false" | value="true" | value="only" |
|---|---|---|---|---|
| Tags | `Include parent tags:` | `No - Only check item tags` | `Yes - Also check parent tags (season, series, album, folder, library)` | `Yes - Only check parent tags (season, series, album, folder, library)` |
| Studios | `Include parent studios:` | `No - Only check item studios` | `Yes - Also check parent studios (season, series, album, folder, library)` | `Yes - Only check parent studios (season, series, album, folder, library)` |
| Genres | `Include parent genres:` | `No - Only check item genres` | `Yes - Also check parent genres (season, series, album, folder, library)` | `Yes - Only check parent genres (season, series, album, folder, library)` |

- [ ] **Step 2: Gut the three visibility updaters** (2451-2491, 2498-2543, 2545-2590)

Delete the entire three-way label/option-text swap block **including** its `select.options.length >= 3` guard (a dangling guard would silently skip a future block), and delete the `getRowScope`/`getSelectedMediaTypes`/`hasEpisode`/`hasAudio` lookups. Each function becomes:

```js
    SmartLists.updateTagsOptionsVisibility = function (ruleRow, fieldValue, page) {
        const tagsOptionsDiv = ruleRow.querySelector('.rule-tags-options');
        if (tagsOptionsDiv) {
            tagsOptionsDiv.style.display = fieldValue === 'Tags' ? 'block' : 'none';
        }
    };
```

Identical surgery for `.rule-studios-options` / `'Studios'` and `.rule-genres-options` / `'Genres'`. ~135 lines become ~18.

- [ ] **Step 3: Serialize — `collectRulesFromForm`** (2929-2942, 2944-2957, 2959-2972)

Drop `&& (hasEpisode || hasAudio)` from all three guards. Write **only the new keys** — the dual-write this step originally specified is cancelled by Amendment 1:

```js
                    const tagsSelect = rule.querySelector('.rule-tags-select');
                    if (tagsSelect && memberName === 'Tags') {
                        const tagsSelectValue = tagsSelect.value;
                        if (tagsSelectValue === 'only') {
                            expression.OnlyParentTags = true;
                            expression.IncludeParentTags = true;
                        } else if (tagsSelectValue === 'true') {
                            expression.IncludeParentTags = true;
                        }
                        // If 'false' (default), don't include the parameter to save space
                    }
```

Same shape for Studios (`OnlyParentStudios`/`IncludeParentStudios`) and Genres. Emitting the Include flag alongside `'only'` is what keeps the incoherent `OnlyParent`-with-no-source state unreachable from the UI.

**Keep** `hasEpisode`/`hasAudio` computed at 2796-2805 — NextUnwatched (2859) and IncludeEpisodesWithinSeries (2891) still use them.

- [ ] **Step 4: Serialize — `extractRuleConfig`** (1625-1645, the clone path)

Give it the **byte-identical** emission shape, keyed off `fieldValue`/`tagsValue`. Today these two writers disagree (`extractRuleConfig` emits `OnlyParentTags` alone and never the Album flags) and round-trip only by accident. After this step, cloning a rule and saving a rule must produce identical JSON.

- [ ] **Step 5: Deserialize — `populateRuleRow`** (3196-3231)

**EXTEND the OR-chain, never replace it.** This is the single highest-risk line in the frontend change: if it reads only the new flag, every pre-upgrade rule repopulates as `'No'`, the user hits Save, and `collectRulesFromForm` emits nothing — the setting is destroyed with no warning.

```js
        if (expression.MemberName === 'Tags') {
            const tagsSelect = ruleRow.querySelector('.rule-tags-select');
            if (tagsSelect) {
                let includeValue = 'false';
                if (expression.OnlyParentTags === true) {
                    includeValue = 'only';
                } else if (expression.IncludeParentTags === true || expression.IncludeParentSeriesTags === true || expression.IncludeParentAlbumTags === true) {
                    includeValue = 'true';
                }
                tagsSelect.value = includeValue;
            }
        }
```

Same for Studios (3208-3219, add `IncludeParentStudios` to the OR) and Genres (3220-3231, add `IncludeParentGenres`). **Preserve** the ordering contract documented at 3149-3150: visibility updaters run first, select values are restored after.

- [ ] **Step 6: Rule summary suffixes** (config-lists.js:1653-1695)

Collapse each field's 4-way branch to 2 cases that **still read the legacy flags**, or pre-existing lists lose their suffix on the cards and look broken:

```js
            if (rule.OnlyParentTags === true) {
                tagsInfo = ' (only parent tags)';
            } else if (rule.IncludeParentTags === true || rule.IncludeParentSeriesTags === true || rule.IncludeParentAlbumTags === true) {
                tagsInfo = ' (including parent tags)';
            }
```

Same for `studiosInfo` and `genresInfo`. Delete the two hoisted locals `includesParentSeriesGenres`/`includesParentAlbumGenres` at 1682-1683. The locals `tagsInfo`/`studiosInfo`/`genresInfo` **must** keep their names and default to `''` — line 1714 concatenates them unconditionally.

- [ ] **Step 7: One comment in config-init.js** (2942)

`// Update visibility of parent series options based on media types` → `// Update visibility of parent metadata options`. No other config-init.js change.

- [ ] **Step 8: Verify and commit**

```bash
grep -c '`' Jellyfin.Plugin.SmartLists/Configuration/config-rules.js       # must be 0
grep -c '=>' Jellyfin.Plugin.SmartLists/Configuration/config-rules.js      # must not increase
grep -c 'IncludeParentSeriesTags' Jellyfin.Plugin.SmartLists/Configuration/config-rules.js   # >= 1 (legacy read + dual write)
grep -c 'IncludeParentSeriesTags' Jellyfin.Plugin.SmartLists/Configuration/config-lists.js   # >= 1 (legacy read)
cd dev && ./build-local.sh
```

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/
git commit -m "Offer parent metadata options for all media types"
```

---

## Task 11: Docs

**Files:**
- Modify: `docs/content/user-guide/fields-and-operators.md`
- Modify: `docs/content/examples/common-use-cases.md`
- Modify: `docs/content/examples/advanced-examples.md`

Only `docs/content/**` is source — never edit `docs/site/**`.

- [ ] **Step 1: Rewrite `fields-and-operators.md:199-207`**

- Drop the `(shown when Episode or Audio media type is selected)` gate at line 199 — the option now applies to **all** media types.
- Replace `parent series/album` with the real chains: `episode → season → series → folder → library`; `track → album → artist folder → library`; `movie → folder → library`.
- **Delete line 207 entirely** (`The label and option text adapts based on the selected media type…`) — there is no more media-type-dependent label swapping.
- Add an explicit sentence that **season-level and library-level** tags now count — that is what #495 readers will search for.
- Add a note that **BoxSet/collection and playlist membership do NOT contribute**, because membership is `LinkedChildren` rather than `ParentId`. Now that the option is offered for movies, users will otherwise expect collection tags to flow.
- Add the operator-direction warning: positive operators (`Equal`, `Contains`, `IsIn`, `MatchRegex`) **match more** items than before; negative operators (`NotEqual`, `NotContains`, `IsNotIn`) **match fewer**, because they require *every* checked source to not match.

- [ ] **Step 2: Pointer on the Membership table** (187-197)

Add a short `see Parent metadata options below` note on the Genres / Studios / Tags rows, since readers of the table for Movies will no longer skip the section.

- [ ] **Step 3: Reword the three examples**

- `advanced-examples.md:31` and `:37`: `(with parent series tags enabled)` → `(with parent tags enabled, so a tag on the season or series applies to its episodes)`.
- `common-use-cases.md:68`: same rewording.
- Add **one new example** exercising the capability gain — a Movies list matching a tag applied to the whole library.

- [ ] **Step 4: Verify and commit**

```bash
grep -rn 'parent series/album\|Episode or Audio media type' docs/content/    # must return nothing
```
Build mkdocs to confirm it renders.

```bash
git add docs/content/
git commit -m "Document the ancestor walk, all-media-type support and the negative-operator direction"
```

---

## Task 12: Both-ABI build + review gate

**Files:** none (verification only).

- [ ] **Step 1: The two-part grep gate**

```bash
# NEGATIVE: C# must read only the folded flags. Restricted to *.cs on purpose —
# the JS legacy reads are MANDATORY back-compat and must not be swept up.
grep -rn 'IncludeParentSeries\|IncludeParentAlbum' --include='*.cs' Jellyfin.Plugin.SmartLists/
#   -> expected: ONLY Expression.cs (six legacy declarations + three folds)

# POSITIVE: the back-compat hinge must still exist in JS.
grep -c 'IncludeParentSeriesTags' Jellyfin.Plugin.SmartLists/Configuration/config-rules.js   # >= 1
grep -c 'IncludeParentSeriesTags' Jellyfin.Plugin.SmartLists/Configuration/config-lists.js   # >= 1
```

- [ ] **Step 2: Build both ABIs**

```bash
cd dev && ./build-local.sh                        # net10.0 / Jellyfin 12
cd dev && JELLYFIN_ABI=10.11.0 ./build-local.sh   # net9.0 / Jellyfin 10.11
cd dev && ./build-local.sh                        # leave the 12.x build deployed for Task 13
dotnet test
```

- [ ] **Step 3: Verify the deployed DLL**

The dev container is shared and parallel sessions clobber each other's deployed plugin. Confirm the running build is yours by searching the deployed DLL for the UTF-16 string `IncludeParentTags` before trusting any e2e result.

---

## Task 13: End-to-end verification against the local dev Jellyfin

**Files:** none (verification only). Target: <http://localhost:8096>.

> **FIXTURE HAZARD — read this first.** All three children of the tagged season `3C34646F` (`Moss and the German` `8F4F4464`, `The Work Outing` `AAF2D212`, `Return of the Golden Child` `F07FCC7F`) **already carry `seasontag02` on themselves**. A `Tags contains seasontag02` rule matches them with the walk completely broken. Do **not** use them to prove anything.

- [ ] **Step 0: Confirm the fixture values**

```bash
sqlite3 dev/jellyfin-data/config/data/jellyfin.db \
  "SELECT Id, Name, Type, Tags FROM BaseItems WHERE Tags <> '';"
```
Expect a `CollectionFolder` row for the Series library carrying the library tag and the `Season` row `3C34646F` carrying `seasontag02`.

`<LIBTAG>` is **`seriestag01`** — confirmed directly against the live DB (`BC3A7D1D | Serier | CollectionFolder | seriestag01`). The issue text's `series01` is the reporter's paraphrase, not the stored value.

> Copy `jellyfin.db-wal` alongside `jellyfin.db` before querying a snapshot — Jellyfin runs in WAL mode and recent tag edits are invisible in the `.db` file alone.

- [ ] **Step A — Season tag (the #495 headline)**

Tag the clean season `814B5C54` (`Season 1`, path `/shows/The IT Crowd (2006)/Season 1`) with `seasontag99`; its children `1359EDA6`, `145405B5`, `4FB59619` all have **empty** own `Tags`.
Rule: media type `Episode`, `Tags` **contains** `seasontag99`, parent option = **`Yes - Also check parent tags`**.
Expected: exactly those three episodes. **Before the fix this returns zero.**

- [ ] **Step B — Library tag (the half a parents-only walk would miss)**

`<LIBTAG>` is carried **only** by the `Serier` CollectionFolder `BC3A7D1D` and by no item at all.
Rule: media type `Episode`, `Tags` **contains** `<LIBTAG>`, parent option = **`Yes - Also check parent tags`**.
Expected: **every** episode in that library. This is the clean proof the `GetCollectionFolders` union works.

- [ ] **Step C — All-media-type broadening**

Tag the `Filmer` CollectionFolder. Rule: media type `Movie`, `Tags` contains that tag, parent option = `Yes`.
Expected: all movies in that library. **Today the dropdown is not even shown for Movies** — confirm the div actually appears (the `config-init.js` existence guards fail silently, so "no console error" is not evidence).

- [ ] **Step D — Only parent tags**

Same rule as Step A but parent option = **`Yes - Only check parent tags`**.
Expected: the same three episodes. Then add `seasontag99` to **one episode's own** tags and re-refresh: the result must be **unchanged** (the item's own tags are ignored).

- [ ] **Step E — Legacy flag back-compat**

Grep confirms **zero** of the 20 saved lists under `dev/jellyfin-data/config/data/smartlists/` currently exercise any parent flag, so nothing existing covers this path. **Before deploying**, hand-write into one list's `config.json` a rule carrying only:

```json
{ "MemberName": "Tags", "Operator": "Contains", "TargetValue": "seasontag99", "IncludeParentAlbumTags": true }
```

After upgrade: (1) the rule must still match via the fold, and (2) opening the list in the edit form must show the dropdown reading **`Yes`**, not `No`. Save it and confirm the saved JSON now carries `IncludeParentTags` and **no** legacy keys — the legacy keys are read, never written (Amendment 1).

- [ ] **Step F — Negative case (must NOT match)**

Rule: media type `Episode`, `Tags` **NotEqual** `seasontag99`, parent option = `Yes - Also check parent tags`.
Expected: the three Season 1 episodes are **excluded**; every other episode in the library is present. This is the AND-fold direction.

- [ ] **Step G — Existing audio list shrinks (Major 6, regression watch)**

**Capture first, deploy second.** On the pre-change build, create/refresh an Audio list with `Genres NotContains <a genre that exists on a MusicArtist folder but not on the album>` plus the album parent option enabled, and record the exact item set. After deploying, refresh and diff. Expected: the item set **shrinks** (artist-folder and library genres now count). Confirm the shrink is explainable, not arbitrary.

- [ ] **Step H — Existing episode list shrinks**

Same capture-then-diff for an Episode list with `Tags NotContains <LIBTAG>` and the parent option enabled. Expected: goes from "everything" to "nothing in that library", because the library tag now counts.

- [ ] **Step I — Cost profile on the #495 shape**

Create a list whose **only** rule is `Tags contains <LIBTAG> (include parent)` over the full Series library — this takes the expensive-only path (SmartList.cs:3026), so the walk runs on every candidate item. Record refresh wall-clock and compare against a pre-change baseline for the same-sized list. Expect roughly parity per item (memo hits are dictionary lookups) with a modest one-off cost for the containers.

- [ ] **Step J — Log check**

```bash
docker logs jellyfin 2>&1 | grep -i "Smart"
```
Expected during a full refresh: **no** `NotSupportedException`, **no** `ArgumentException` (that would mean an Engine-literal ↔ Operand-property mismatch), and **no** `ancestor walk stopped early` warning.

- [ ] **Step K: Update `primer.md`** (session-continuity file at repo root; rewrite per its existing structure) and report results.

---

## Docs Update Checklist

| File | Passage | Change |
|---|---|---|
| `docs/content/user-guide/fields-and-operators.md` | line 199 | Delete `(shown when Episode or Audio media type is selected)` |
| | lines 201-205 | Replace `parent series/album` with the real ancestor chains; state that season-level and library-level values count |
| | line 207 | **Delete** (`The label and option text adapts based on the selected media type…`) |
| | new paragraph after 205 | Positive operators match more, negative operators match fewer; BoxSet/playlist membership does not contribute |
| | lines 187-197 (Membership table) | Add `see Parent metadata options below` on the Genres / Studios / Tags rows |
| `docs/content/examples/advanced-examples.md` | line 31 | `(with parent series tags enabled)` → new wording |
| | line 37 | same |
| `docs/content/examples/common-use-cases.md` | line 68 | same |
| | new section | One Movies-library-tag example (the capability gain) |
| Release notes (tag message body) | — | Existing lists change contents on first refresh: positive operators gain items, **negative operators lose items**; audio lists with the album option now also see artist-folder and library values. **Draft text parked below — carry it into the `/release` tag message.** |

### Drafted release-note text (for the `/release` tag message body)

Parked here so it survives to tag time. The docs version in `fields-and-operators.md` is deliberately
abbreviated; this is the only place the audio-specific shrinkage is spelled out for users.

> **Parent tags, studios, and genres now walk the whole ancestor chain**
>
> The "also check parent" option on **Tags**, **Studios**, and **Genres** used to stop at the parent
> series (episodes) or the parent album (audio). It now walks every ancestor up to and including the
> Jellyfin library: season → series → the folders they sit in → library for episodes, album → artist
> folder → the folders they sit in → library for tracks, and the folders → library for everything
> else. The option is also no longer limited to episodes and audio — it is available for every media
> type, so a tag on a movie library or on a plain folder now applies to everything inside it.
>
> **Existing lists that already had the option enabled will change contents on their first refresh.**
> Positive operators (equals, contains, is in, matches regex) match **more** items, because the item
> matches if it *or any ancestor* matches. Negative operators (not equals, not contains, is not in)
> match **fewer** items, because every checked source has to not match — a value sitting on a season,
> a folder, or the library is now enough to exclude the item. Music lists move the most: a
> not-contains or not-equals rule with the album option enabled now also sees values from the artist
> folder and the library, not just the album, so those playlists can visibly shrink.
>
> Collection and playlist membership still does not count — those are links, not folders, so their
> tags, studios, and genres never flow down to the items inside them.

Not touched, deliberately: `docs/content/user-guide/advanced-configuration.md` (parent flags were never documented for file editors — see Open Question 2) and `docs/content/development/integration-api.md` (see Open Question 2).

---

## Open Questions for the repo owner

> **ALL CLOSED — see [Amendments](#amendments-decided-after-the-plan-was-drafted--these-override-the-body) at the top.** Retained below for the reasoning that produced each decision.

1. ~~**When does the dual-write stop?**~~ **CLOSED — there is no dual-write.** The `10.11` stable line is retired (Amendment 1), so the rollback scenario cannot occur. New saves write only the new keys; legacy keys stay readable forever.

2. **Should the new flag be documented as public API?** `docs/content/user-guide/advanced-configuration.md:105-112` documents `MemberName`/`Operator`/`TargetValue` for hand-editors, and `docs/content/development/integration-api.md:275-290` documents the `ExpressionSets` wire shape. Neither has ever mentioned the parent flags. Documenting `IncludeParentTags`/`OnlyParentTags` makes them a supported contract; leaving them undocumented keeps them internal. Which?

3. **Symlinked / plugin-created virtual libraries are knowingly not covered.** `LibraryManagerHelper.cs:98-101` already documents that `GetCollectionFolders` is unreliable for them and works around it for library *names* via `GetVirtualFolders` path matching (`VirtualFolderInfo.ItemId` is the CollectionFolder id); the dev DB contains one under `/config/plugins/LocalRecs/virtual-libraries/`. Library **tags** will therefore be inconsistent with the existing `LibraryName` field on exactly those libraries. ~20 lines would close it. File as a follow-up issue, or fold into this change?

4. **Is the audio behaviour change acceptable as-is?** Walking past the MusicAlbum picks up MusicArtist-folder and library values that music lists have never seen. For positive operators that is a gain; for `NotContains`/`NotEqual` it will visibly shrink existing playlists (Step G). Ship as-is with release notes, or gate audio to stop at the album for one release?

5. **Depth cap of 20** is set so it never binds (measured real depth 4-5; deep artist/genre trees maybe 7). Confirm 20 is acceptable, or name a different number — but note that a *binding* cap produces truncated results, and truncated walks deliberately skip memo writes to avoid order-dependent caching.
