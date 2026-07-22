# List Templates — Design

Date: 2026-07-22
Status: Approved (Johan, 2026-07-22)

## Problem

The plugin has grown to ~65 rule fields, 40+ sorts, bumpers, external lists,
round-robin interleaving, and more. New users face a blank create form with no
guidance. A full setup wizard is too complex for the Jellyfin plugin UI, so
instead: a curated set of templates the user can pick from, which prefills the
create form with a working configuration they review and save.

## Approach (decided)

Frontend-only template gallery that prefills the create form. No backend
changes. The prefilled form itself is the "draft" — nothing is created until
the user clicks Create, so there is no risk of junk lists and all existing
validation applies unchanged.

Rejected alternatives:

- **One-click backend create** (templates as API resources instantiating
  disabled lists): more API surface, and placeholder values would produce
  broken lists the user has to hunt down in the Manage tab.
- **Per-list JSON import/export with a docs gallery**: useful for community
  sharing later, but does not solve onboarding — new users will not paste JSON.

## Why this is cheap: existing machinery

Verified against the codebase (all claims adversarially confirmed):

- `SmartLists.clonePlaylist` (config-lists.js:1114–1309) already implements
  template instantiation: populate create form from a DTO, clear edit state
  (`setPageEditState(page, false, null)`), so submit POSTs a new list. It is a
  ~196-line near-duplicate of `editPlaylist` (config-lists.js:894–1112).
- The create endpoint (POST `Plugins/SmartLists`, SmartListController.cs:572)
  accepts a full `SmartListDto` and self-heals Id/FileName/Order/DateCreated;
  `InputValidator.ValidateSmartList` runs on everything. `DtoMapper.cs:19–158`
  is the authoritative checklist of copyable vs instance-specific fields.
- Prefill prior art: `populateFormDefaults`/`applyFormDefaults`
  (config-init.js:409–527) already prefill the create form from admin default
  settings.
- The docs Examples pages hold 66 worked configurations
  (examples/common-use-cases.md: 49, examples/advanced-examples.md: 17) —
  the template catalog is curation, not invention.

## Components

### 1. Template catalog — new `config-templates.js`

`SmartLists.TEMPLATES`: static array. Entry shape:

```js
{
  id: "tv-channel",
  name: "TV Channel",              // also prefills the list Name field
  category: "TV & Movies",         // optgroup label
  description: "...",              // shown under the picker
  adminOnly: false,                // hide on user page when true
  inputHint: null,                 // set when the template needs user input
  dto: { /* partial SmartListDto */ }
}
```

`dto` carries only definition fields: `Type`, `MediaTypes`, `ExpressionSets`,
`Order.SortOptions`, `MaxItems`, `MaxPlayTimeMinutes`, `Bumpers`,
`RandomGroupSelection`, `AutoRefresh`, `Schedules`, `VisibilitySchedules`,
`HideWhenEmpty`, `IncludeExtras`. Never instance fields (Id, FileName,
Jellyfin IDs, DateCreated, stats, CustomImages, UserPlaylists).

Rule field names must exist in the `FIELD_TYPES` arrays in config-core.js
(populateRuleRow consults them); enum values serialize as strings
(SmartListType, AutoRefreshMode, ScheduleTrigger, ScheduleAction).

### 2. Picker UI — both pages

Section at the top of `#playlistForm`, above List Type — never inside
`#advanced-options-body` (project rule). Native `emby-select` with
`<optgroup>` per category, a "Use" button, and a description div updated on
select change. Same markup in config.html and user-playlists.html;
`adminOnly` templates are filtered out on the user page. No ES6 template
literals; messages via `showNotification`.

### 3. Population engine — shared helper (refactor)

Extract `populateFormFromDto(page, dto, opts)` from the ~400 duplicated lines
in `editPlaylist` and `clonePlaylist`; both become thin wrappers. The
template path calls the same helper with edit state cleared — clone's proven
flow. The feature rides an already-tested code path and the duplication dies
as a side effect.

### 4. Placeholders

Templates needing user input (SimilarTo seed title, franchise name, external
list URL) ship those rule values **empty** plus an `inputHint`. After
populating: `showNotification(inputHint)`, scroll to and focus the first
empty rule value input. Empty values must block saving — verify during
implementation that existing validation rejects empty `TargetValue`; if not,
add a guard in `createPlaylist`.

### 5. Registration

`config-templates.js` needs: `.csproj` `<EmbeddedResource>`, `Plugin.cs`
`GetPages()` `PluginPageInfo`, and a script tag in **both** HTML pages
(user-playlists.html keeps its own script list, served by
UserPagesController).

## v1 template set (10)

| Template | Showcases | Needs input |
|---|---|---|
| TV Channel | Round Robin, group by Series Name | no |
| Franchise TV Channel | LRW Round Robin, group by Collections, AirDate within group, air-block window | no |
| Continue Watching | NextUnwatched, LRW Round Robin, AutoRefresh OnAllChanges | no |
| Weekly Jams (ListenBrainz) | External list feed, External List Order sort, weekly schedule | URL (username) |
| Saturday Cartoons + Bumpers | Bumper config, visibility schedules | bumper tag |
| Because You Watched… | SimilarTo, SimilarityComparisonFields, Similarity sort | seed title |
| Album Roulette | RandomGroupSelection (Album), TrackNumber sort, MaxPlayTime | no |
| Balanced Mix | Multi-block per-group MaxItems, Rule Block Order | no |
| Fresh & Unseen (everyone) | DateCreated + Unplayed, AllUsers | no |
| Trending Now (Trakt) | Collection type, external list, daily schedule, HideWhenEmpty | no |

External-list templates get `adminOnly: true` if the ExternalList field turns
out to be unavailable on the user page (verify during implementation).
"Fresh & Unseen": admin page sets AllUsers; on the user page the server
force-overrides ownership anyway, so it degrades to per-user cleanly.

## Docs impact

- user-guide/configuration.md — Create List tab section gains the picker.
- getting-started/quick-start.md — "or start from a template" in the
  first-list flow.
- index.md + README.md — feature bullet.
- Examples pages — mark entries that ship as built-in templates.

## Verification

- `dotnet build -c Release` both TFMs (warnings are errors), `node --check`
  on changed JS.
- E2E against local Jellyfin (dev container): instantiate TV Channel template
  → form filled correctly → Create → list materializes with expected rules
  and sort; placeholder template → save blocked until the value is filled;
  user page → adminOnly templates hidden; edit and clone still work
  (regression on the extracted helper).

## Deliberate simplifications

- No overwrite-confirm when "Use" is clicked on a partly-filled form —
  explicit button, explicit intent.
- Templates version with the plugin; no remote/community catalog (per-list
  import/export is the future path for that).
- No library-content-aware filtering — music templates are visible on
  movie-only servers; descriptions make the target obvious.
