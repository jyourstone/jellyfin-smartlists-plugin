# Jellyfin Smart Lists Plugin

A Jellyfin plugin that creates dynamic playlists and collections based on user-defined rules (genres, ratings, years, etc.) with automatic refresh capabilities.

## Development Commands

```bash
# Build locally (from /dev directory)
./build-local.sh

# View logs
tail -f dev/jellyfin-data/config/log/log_*.log | grep "Smart"

## Project Structure

```
Jellyfin.Plugin.SmartLists/
├── Core/                    # Business logic
│   ├── Constants/           # MediaTypes, Operators, ResolutionTypes
│   ├── Enums/               # SmartListType, RuleLogic, AutoRefreshMode, etc.
│   ├── Models/              # DTOs: SmartListDto, SmartPlaylistDto, SmartCollectionDto
│   ├── Orders/              # 25+ sort implementations (NameOrder, RandomOrder, etc.)
│   ├── QueryEngine/         # Rule evaluation: Engine, Expression, Factory, Operand
│   └── SmartList.cs         # Main filtering logic
├── Api/Controllers/         # SmartListController, UserSmartListController
├── Services/
│   ├── Abstractions/        # ISmartListService, ISmartListStore
│   ├── Playlists/           # PlaylistService, PlaylistStore
│   ├── Collections/         # CollectionService, CollectionStore
│   └── Shared/              # AutoRefreshService, RefreshQueueService, etc.
├── Configuration/           # Two HTML pages + shared JS modules (11 files)
│   ├── config.html          # Admin configuration page
│   └── user-playlists.html  # User configuration page
└── Utilities/               # DtoMapper, InputValidator, LibraryManagerHelper, etc.
```

## Key Principles

### DRY (Don't Repeat Yourself)
Extract duplicated code into helpers. Check `Utilities/` and existing helpers before creating new functionality.

### Thread Safety
Lists are processed with threading. Use thread-safe collections (`ConcurrentDictionary`, `ConcurrentBag`) and proper locking for shared state.

### Two-Phase Filtering
Expensive fields (People, AudioLanguages, Collections) use two-phase filtering in `SmartList.cs`:
1. Phase 1: Evaluate cheap rules first
2. Phase 2: Only extract expensive data for items passing Phase 1

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

### Media Type Constants
Use `MediaTypes.Episode` instead of `"Episode"` - see `Core/Constants/MediaTypes.cs`.

## When Making Changes

- Update the mkdocs `/docs/content/` when adding user-facing features. Put any examples in the example sections.
- **UI changes must update both HTML files**: `config.html` (admin) and `user-playlists.html` (user) - the JS modules are shared
- Form fields need updates in: HTML (both pages), JS (create/edit/display), and backend DTOs
