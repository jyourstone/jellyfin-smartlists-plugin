# Jellyfin Smart Lists Plugin

A Jellyfin plugin that creates dynamic playlists and collections based on user-defined rules (genres, ratings, years, etc.) with automatic refresh capabilities.

## Development Commands

```bash
# Build + restart local Jellyfin Docker container (from /dev directory)
./build-local.sh                        # defaults to Jellyfin 12.x (net10.0)
JELLYFIN_ABI=10.11.0 ./build-local.sh   # build for Jellyfin 10.11 (net9.0)

# View logs
docker logs jellyfin 2>&1 | grep -i "Smart"
# or
tail -f dev/jellyfin-data/config/log/log_*.log | grep "Smart"
```

The project multi-targets `net9.0` (Jellyfin 10.11) and `net10.0` (Jellyfin 12.x). The build treats all warnings as errors with `AnalysisMode=Recommended` — CA analyzer warnings (e.g. CA1822 make-static, CA1305 locale) fail the build.

There is no test suite; verification is done by building and exercising the plugin against the local Jellyfin instance (<http://localhost:8096>).

## Project Structure

```text
Jellyfin.Plugin.SmartLists/
├── Core/                    # Business logic
│   ├── Constants/           # MediaTypes, Operators, ResolutionTypes
│   ├── Enums/               # SmartListType, RuleLogic, AutoRefreshMode, etc.
│   ├── Models/              # DTOs: SmartListDto, SmartPlaylistDto, SmartCollectionDto
│   ├── Orders/              # 25+ sort implementations (NameOrder, RandomOrder, etc.)
│   ├── QueryEngine/         # Rule evaluation: Engine, Expression, Factory, Operand, FieldRegistry
│   └── SmartList.cs         # Main filtering logic
├── Api/Controllers/         # SmartListController, UserSmartListController
├── Services/
│   ├── Abstractions/        # ISmartListService, ISmartListStore
│   ├── Playlists/           # PlaylistService, PlaylistStore
│   ├── Collections/         # CollectionService, CollectionStore
│   ├── ExternalList/        # External list providers: MDBList, IMDb, Trakt, TMDB
│   ├── Users/               # User resolution/lookup services
│   └── Shared/              # AutoRefreshService, RefreshQueueService, etc.
├── Configuration/           # Two HTML pages + shared config-*.js modules
│   ├── config.html          # Admin configuration page
│   └── user-playlists.html  # User configuration page
└── Utilities/               # DtoMapper, InputValidator, LibraryManagerHelper, etc.
```

## Key Principles

### DRY (Don't Repeat Yourself)
Extract duplicated code into helpers. Check `Utilities/` and existing helpers before creating new functionality.

### Thread Safety
List item processing is sequential (enforced by `SemaphoreSlim(1,1)` in `RefreshQueueService`), but background task scheduling and cache access use concurrent collections (`ConcurrentDictionary`, `ConcurrentQueue`). Use thread-safe collections for shared caches accessed across the background refresh task and API layer.

### Two-Phase Filtering
Expensive fields (People, AudioLanguages, Collections, etc.) use two-phase filtering in `SmartList.cs`:
1. Phase 1: Evaluate cheap rules first
2. Phase 2: Only extract expensive data for items passing Phase 1

Expensive fields are defined in `FieldRegistry.cs` via `ExtractionGroup` flags. Use `FieldRegistry.IsExpensiveField(fieldName)` to check if a field is expensive.

### Adding New Rule Fields
`FieldRegistry.cs` is the single source of truth for field definitions. Adding a new field requires updates in: `FieldRegistry.cs` (definition), `Operand.cs` (property), and `Factory.cs` (extraction logic). The field dropdown in the UI is populated from the API, but `config-core.js` has hardcoded `FIELD_TYPES` arrays (e.g., `STRING_FIELDS`, `LIST_FIELDS`, `NUMERIC_FIELDS`) that control which input controls and operators are shown. New fields must be added to the appropriate array in `config-core.js`.

## Critical Gotchas

### Sorting Architecture
Sorting uses `Order` classes in `Core/Orders/`. Each order must implement:
- `GetSortKey()` - Returns `IComparable` for multi-sort scenarios
- `OrderBy()` - Single-sort optimization path

**Multi-sort flow**: `ApplyMultipleOrders()` → `WrapOrdersWithChildAggregation()` → `ApplySortingCore()`

**Early return paths**: `FilterPlaylistItems()` has multiple early returns (e.g., when all rules use `IncludeCollectionOnly`). These must still apply sorting - check that `ApplyMultipleOrders()` is called before returning.

Adding new sort options requires updates in: `Core/Orders/`, `OrderFactory.cs`, `IsDescendingOrder()` in SmartList.cs, and frontend `config-sorts.js`.

### Jellyfin UI (config-*.js)
- **No ES6 template literals** - use string concatenation
- **Never use `is="emby-input"`** - causes htmlFor errors, use `class="emby-input"` instead
- Use `showNotification()` for user messages, not `Dashboard.alert()`
- **New JS files must be registered in TWO places**: `.csproj` (as `<EmbeddedResource>`) AND `Plugin.cs` (in `GetPages()` as `PluginPageInfo`)

### Media Type Constants
Use `MediaTypes.Episode` instead of `"Episode"` - see `Core/Constants/MediaTypes.cs`.

### Manual Service Construction
`RefreshQueueService` creates `PlaylistService`/`CollectionService` via `new`, not DI. New constructor dependencies for those services must be threaded through `RefreshQueueService` manually.

## Versioning & Releases

Releases are triggered by pushing a git tag matching `v*` (see `.github/workflows/release.yml`).

### Version Format

Jellyfin plugins use .NET `System.Version` (`Major.Minor.Build.Revision` — four integers). Unlike SemVer, there are **no pre-release labels** (`-rc.1`, `-alpha`, etc.) and comparison is purely numeric left-to-right. The convention below encodes RC status into the four-part version instead:

- **Revision > 0** → Release Candidate (the revision number is the RC number)
- **Revision = 0** → Stable release

The `-rc` suffix on the git tag is only a workflow marker — it routes the build to the **unstable** manifest branch and marks the GitHub release as a prerelease. It is stripped before building (the .NET version is the four-part number).

### Tag Examples

| Git Tag | Manifest Version | Manifest Branch | Notes |
|---|---|---|---|
| `v12.0.0.1-rc` | `12.0.0.1` | unstable | First RC for 12.0 |
| `v12.0.0.2-rc` | `12.0.0.2` | unstable | Second RC |
| `v12.0.1.0` | `12.0.1.0` | stable (main) | Final stable release |
| `v12.0.2.0` | `12.0.2.0` | stable (main) | Hotfix (no RC) |
| `v12.1.0.1-rc` | `12.1.0.1` | unstable | RC for next minor |
| `v12.1.1.0` | `12.1.1.0` | stable (main) | Stable for next minor |

Ordering always holds: `12.0.0.1 < 12.0.0.2 < 12.0.1.0` — so RC users auto-update through RCs and into the final stable release. Because Revision is reserved for RC numbers, stable releases always bump the **Build** component (never use Revision for stable).

### Release Lines (current, while Jellyfin 12 is in RC)

Every tag builds **both** ABIs (`TARGETS` in release.yml: 10.11.0/net9.0 and 12.0.0/net10.0) and writes both `targetAbi` entries to the manifest. The manifest branch (stable = main, unstable) is the release *channel*; git branches only anchor where tags are cut.

- **RCs** are tagged on `main` and MUST use `v12.x.y.z-rc` numbering. The unstable manifest already carries ABI-10.11 entries at version `12.x` from past RCs, so a lower-numbered RC tag (e.g. `v10.11.x.y-rc`) would sort below them and never be offered to existing RC users.
- **Stable releases** (until Jellyfin 12 final) are tagged on `10.11-release` as `v10.11.X.0`, keeping the plugin-version-follows-Jellyfin convention prod users expect. Promotion flow:
  1. Merge `main` into `10.11-release` (never fast-forward assumptions — the branch carries its own commits), and merge back so the trees converge.
  2. Smoke test against a real 10.11 container: `JELLYFIN_ABI=10.11.0 ./build-local.sh`.
  3. Tag `v10.11.X.0` on `10.11-release`.
- Known cosmetic trade-off: RC users don't auto-roll into a `10.11.X.0` stable (it sorts below `12.x` RC versions); they stay on the byte-identical RC build until the next RC.
- **When Jellyfin 12 final ships**: cut the first `v12.0.X.0` stable from `main` — it sorts above both lines, all users converge, and the split-line scheme ends.

## When Making Changes


- Update the mkdocs `/docs/content/` when adding user-facing features. Put any examples in the example sections.
- **UI changes must update both HTML files**: `config.html` (admin) and `user-playlists.html` (user) - the JS modules are shared
- Form fields need updates in: HTML (both pages), JS (create/edit/display), and backend DTOs
- **Create-form fields and the "More options" fold**: required inputs must never
  be placed inside `#advanced-options-body` (collapsed `display:none` hides native
  validation). New advanced fields go under the matching sub-heading inside the fold
  (Limits / Bumpers / Automation / Sharing / Presentation); new core fields go above
  it. If a new advanced field has an unambiguous non-default state, add a signal for
  it in `syncAdvancedSection` (config-lists.js) so edit mode surfaces it as a chip
  and auto-expands.
