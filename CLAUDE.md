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

### Multipart uploads break OpenAPI if bound wrong
Bind uploaded files as a bare `IFormFile` parameter — **never** `[FromForm] IFormFile`. MVC already
binds `IFormFile` from the multipart body, so the attribute is a runtime no-op, but it downgrades the
binding source to `BindingSource.Form`, which makes Swashbuckle throw and return **HTTP 500 for
`/api-docs/openapi.json` server-wide** — breaking API-doc generation for all of Jellyfin, not just this
plugin. Add `[Consumes("multipart/form-data")]` alongside so the schema declares the right shape.
`[FromForm]` on plain scalar parameters (e.g. `string imageType`) is fine.

### Media Type Constants
Use `MediaTypes.Episode` instead of `"Episode"` - see `Core/Constants/MediaTypes.cs`.

### Manual Service Construction
`RefreshQueueService` creates `PlaylistService`/`CollectionService` via `new`, not DI. New constructor dependencies for those services must be threaded through `RefreshQueueService` manually.

## Versioning & Releases

Releases are triggered by pushing a git tag matching `v*` (see `.github/workflows/release.yml`).

### Cutting releases

Releases are tagged with the `/release` skill; the project-specific flow (branch lines, RC-in-Revision numbering, tag-message format) lives in `.claude/skills/release/SKILL.md` — the personal `/release` skill reads it as a reference document, since personal skills shadow same-named project skills. The workflow publishes the annotated tag message body (`%(contents:body)` — everything after the first line) as both the GitHub release body and the plugin-manifest changelog. Tags without an annotated message fall back to GitHub auto-generated notes (PR titles + labels per `.github/release.yml`).

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

### Release Line — single `12.x` line (decided 2026-08-14)

**All version numbers are `v12.x.y.z`.** The old split scheme (RCs on `main`, `v10.11.X.0` stables on `10.11-release`) is **retired** — no further stable releases will be cut on the `10.11-release` branch. It is kept only as history; never tag from it.

Every tag still builds **both** ABIs (`TARGETS` in release.yml: 10.11.0/net9.0 and 12.0.0/net10.0) and writes both `targetAbi` entries to the manifest. The manifest branch (stable = main, unstable) is the release *channel*; git branches only anchor where tags are cut.

**Jellyfin 10.11 users keep receiving updates.** Support is unchanged — the plugin still multi-targets `net9.0` and every release still publishes an ABI-10.11 entry. Only the *version number* they see changed: updates now arrive as `12.x.y.z` instead of `10.11.x.0`. The plugin version no longer tracks the Jellyfin version line.

#### Branches

| Branch | Role |
|---|---|
| `main` | Trunk. All development lands here. **RCs are tagged here.** |
| `12-release` | Tracks the **last stable release**. Fast-forwarded to `main` at each stable. **Stables are tagged here.** The mkdocs Cloudflare Worker publishes from this branch, so the docs site shows released state rather than unreleased trunk. |
| `10.11-release` | Historical only. Do not tag, do not merge into. |

#### Cutting a release

- **RC** — on `main`: tag `v12.x.y.z-rc` → unstable manifest, GitHub prerelease. Revision *is* the RC number.
- **Stable** — fast-forward `12-release` up to `main`, then tag there:

  ```bash
  git checkout 12-release
  git fetch origin main
  git merge --ff-only origin/main   # origin/main, not local main — a stale local ref ships old code
  git push origin 12-release        # the docs Worker publishes from this branch
  git tag -a v12.0.X.0 -m "..."     # Build bumps; Revision resets to 0
  ```

  `--ff-only` is deliberate: `12-release` must never carry commits of its own, or it stops being a pointer at a `main` commit and the next fast-forward fails. This is the key difference from `10.11-release`, which did carry its own commits and needed merging both ways.

Bump the **Build** segment for stables; Revision is reserved exclusively for RC numbers. Ordering holds across the whole line, so RC users now roll straight into stables — the old trade-off where a `10.11.X.0` stable sorted *below* the `12.x` RCs and was never offered to RC users is gone with the split.

Smoke testing the 10.11 ABI before a stable is still worthwhile since it is still shipped: `JELLYFIN_ABI=10.11.0 ./build-local.sh`.

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
