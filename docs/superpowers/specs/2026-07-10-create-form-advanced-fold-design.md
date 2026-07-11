# Create/Edit Form: "Advanced options" Fold — Design

**Date:** 2026-07-10
**Status:** Approved direction (hybrid: minimal-diff fold + sub-headings, create-mode summary chips, pre-submit invalid guard)

## Problem

The Create List form (shared by create and edit, on both `config.html` and `user-playlists.html`) is a single-column wall of ~17 field groups. Creating a simple list (name + media type + a rule) drowns in advanced options: bumpers, random group selection, schedules, custom images, metadata overrides, etc. The form grows with every release.

## Goals

- A simple list is creatable without scrolling past advanced fields.
- Advanced configuration stays discoverable and is never invisible when it is in effect (especially in edit mode).
- Near-zero blast radius: no new JS files, no DOM nodes moved at runtime, no backend/DTO changes, reuse the proven Manage-tab collapse pattern.

## Non-goals

- No redesign of the Rules builder.
- No per-section collapsible IA (deliberate stepping stone: the fold's sub-headings make a later migration to per-section folds mechanical if ever needed).
- No localStorage persistence of fold state in v1 (cheap follow-up if requested; avoids any race with async `applyFormDefaults`).

## Design

### Visible form (fold collapsed — the default)

In today's order, with one move (Users block moves up, from after Random Group Selection to directly after Sort Options, making the advanced fields contiguous):

1. Edit-mode indicator (edit only)
2. List Type
3. List Name
4. Media Types (+ conditional Include Extras)
5. Rules builder
6. Sort Options
7. Users (admin page: playlist multi-select / collection single select; user page: hidden input)
8. **▶ Advanced options** — collapsed fold header
9. Create/Update + Clear Form buttons

### Inside the fold (all existing IDs, labels, descriptions, and conditional-visibility classes unchanged), with static `h3.sectionTitle` sub-headings

- **Limits** — Max Items, Max Playtime (playlist-only), Random Group Selection
- **Bumpers** — the whole `#bumper-section` (keeps playlist-only gating and two-stage detail visibility)
- **Automation** — Enable list, Auto-refresh mode, Refresh Schedules, Visibility Schedules
- **Sharing** — Make playlist public (keeps `updatePublicCheckboxVisibility` gating)
- **Presentation** — Custom Images; the existing **Metadata** `h3` is demoted to an `inputLabel` beneath it (Sort Title, Overview, Tags, Favorite) so it doesn't compete with the fold's sub-headings

All three required inputs (`#playlistName`, `#listType`, conditional collection `#playlistUser`) live **outside** the fold — display:none never swallows native validation.

### Fold header anatomy

Clones the Manage-tab collapse pattern (display-toggled body, plain-text ▶/▼ icon, no animation — `emby-collapse` is NOT registered on plugin config pages and must not be used):

```html
<button type="button" id="advanced-options-header" class="emby-button"
        style="width:100%; display:flex; align-items:center; text-align:left;">
    <span id="advanced-expand-icon">&#9654;</span>
    <span>Advanced options</span>
    <span id="advanced-summary" class="fieldDescription" style="margin-left:auto;"></span>
</button>
<div id="advanced-options-body" style="display: none;">
    <!-- existing advanced field groups, unmoved at runtime -->
</div>
```

A real `<button class="emby-button">` (main-bundle safe) preserves keyboard/gamepad focus handling. Only main-bundle-verified classes are used (`emby-button`, `sectionTitle`, `fieldDescription`, `inputContainer`, …); colors inherited, per repo convention.

### Summary chips (create AND edit mode)

`#advanced-summary` on the collapsed header always shows the applied value chips (Max Items, auto-refresh — read from the populated form via `getAdvancedValueChips`), so hidden-but-applied configuration stays visible in every mode:

- **Create mode:** value chips only, i.e. the effective defaults, e.g. `500 items max · refresh on library changes` (rendered after `applyFormDefaults`/`applyFallbackDefaults` resolves; tolerates defaults arriving a tick after form reset).
- **Edit/clone mode:** feature signals first (counts over booleans), then the value chips — `Bumpers · 2 schedules · 3 images · Metadata · Disabled · Playtime limit · Random groups · 100 items max · refresh on library changes`. `IsPublic` is the one applied setting without a chip (its visibility already depends on user selection).

### Expand/collapse behavior

- Create: always starts collapsed. `clearForm` re-collapses and re-renders default chips.
- Edit: `SmartLists.syncAdvancedSection(page, playlist)` is appended at the very END of `editPlaylist` (population sequence untouched — it is order-fragile). It renders chips and **auto-expands** the fold if any unambiguous signal fires: bumper media type set, any refresh/visibility schedule, random group enabled, `MaxPlayTimeMinutes > 0`, `Enabled === false`, any metadata field set, any custom image.
- Fields whose non-defaultness is ambiguous (defaults load async: `AutoRefreshMode`, `MaxItems`, `IsPublic`) never trigger auto-expand — but `AutoRefreshMode` and `MaxItems` remain visible as value chips on the collapsed header, and all values round-trip on save regardless of visibility.
- Custom images arrive from a separate async endpoint: `loadExistingImages`' success path re-invokes `syncAdvancedSection`, so an images-only list still expands when the response lands.
- `clonePlaylist` calls the same sync after populating. Update-failure re-invokes `editPlaylist` (sync re-runs). `cancelEdit`/update-success flow through `clearForm` (re-collapse).

### Pre-submit invalid guard

Before submit: if `#advanced-options-body` contains an `:invalid` control while collapsed, expand the fold first. One-liner; permanently defuses the hidden-required-field validation trap for future fields.

## Implementation notes

- **Both HTML files** (`config.html`, `user-playlists.html`) get the same wrapper + header + sub-headings; user page's Users block is the hidden input (position irrelevant).
- **JS in existing modules only** (no `.csproj`/`Plugin.cs` registration):
  - `config-lists.js`: `SmartLists.toggleAdvancedOptions(page, force)` (flip body display, swap ▶/▼; string concatenation, no template literals), `SmartLists.syncAdvancedSection(page, playlist)`; one line in `clearForm`; trailing sync call in `editPlaylist`/`clonePlaylist`.
  - `config-images.js`: re-invoke sync from `loadExistingImages` success path.
  - `config-init.js`: add `target.closest('#advanced-options-header')` to the existing page-level click delegation (~line 1254); one line in the `pageshow` handler (~2511) to re-sync ▶/▼ icon from the body's actual display (bfcache back-nav).
- Wrapper is static HTML; no nodes move at runtime, so module-held references and delegated listeners survive; `handleListTypeChange`, `updateBumperSectionVisibility`, `updatePublicCheckboxVisibility`, and all `initialize*`/`populate*` functions work unchanged.
- Estimated diff: ~30 lines HTML per page (mostly re-indentation), ~70 lines JS across existing modules.
- **Docs:** short "Advanced options" note on the configuration page in `docs/content/`.

## Rules for future fields (codify in CLAUDE.md)

1. Required inputs must never live inside the fold.
2. New advanced fields go under the matching sub-heading inside the fold; new core fields go above it.
3. Edit-mode chips/auto-expand: add a signal to `syncAdvancedSection` when the new field has an unambiguous non-default state.

## Known trade-offs (accepted)

- Open fold is still a long column (mitigated by sub-headings).
- Max Items / Make public / Enable list / Auto-refresh land in Advanced — defensible since all have Settings-page defaults; habitual per-list tweakers pay one extra click.
- Edit of a list whose only non-default value is an async-default field (e.g. MaxItems) opens with the fold closed — the value stays visible as a chip on the collapsed header and round-trips on save.
- Users block moves above Bumpers — small muscle-memory cost for a contiguous fold.
- No animation, text ▶/▼ icon — consistent with Manage tab.

## Verification

No test suite; verify against local Jellyfin (`cd dev && ./build-local.sh`, http://localhost:8096), both admin and user pages:

1. Create simple playlist with fold untouched → saved with defaults (Max Items, auto-refresh) intact.
2. Edit a list with bumpers + schedules → fold auto-expanded, chips show counts.
3. Edit a list with only custom images → fold expands when images load.
4. Clone a configured list → chips/expand state match.
5. Cancel edit / successful update → fold re-collapses, default chips return.
6. Switch List Type Playlist↔Collection with fold open → bumpers/playlist-only fields gate correctly inside fold.
7. Browser back-nav to the page → icon matches body state, no ghost-expanded fold.
8. Keyboard: header reachable by Tab, toggles on Enter/Space.
