# List Templates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A "Start from a template" picker on the Create tab (admin + user pages) that prefills the create form from a curated catalog of 10 smart-list templates.

**Architecture:** Frontend-only. A new `config-templates.js` holds the catalog and picker logic. The population engine is a shared `populateFormFromDto(page, dto, opts)` extracted from the existing near-duplicate `editPlaylist`/`clonePlaylist` functions in `config-lists.js`. No backend changes — the prefilled form is the draft; the existing POST path validates and creates.

**Tech Stack:** Vanilla ES5-style JS (IIFE modules over shared `window.SmartLists` namespace), Jellyfin plugin embedded resources, C# only for resource registration.

**Spec:** `docs/superpowers/specs/2026-07-22-list-templates-design.md`

## Global Constraints

- No ES6 template literals in config-*.js — string concatenation only. No arrow functions — match existing `function () {}` style.
- Never `is="emby-input"`; `is="emby-select"` / `is="emby-checkbox"` are fine. Text inputs use `class="emby-input"`.
- User messages via `SmartLists.showNotification(message[, 'success'])`, never `Dashboard.alert()`.
- New JS file registered in FOUR places: `.csproj` `<EmbeddedResource>`, `Plugin.cs` `GetPages()`, `<script>` tag in `config.html` AND in `user-playlists.html`.
- Required inputs must never live inside `#advanced-options-body`. The picker is optional and sits above List Type — outside the fold.
- Build treats all warnings as errors (`AnalysisMode=Recommended`), multi-targets net9.0 + net10.0. Build check: `dotnet build Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj -c Release`.
- JS syntax check: `node --check <file>` on every changed JS file.
- No test suite exists. Verification = builds + `node --check` + `dev/validate-templates.js` (added by this plan) + end-to-end against the local dev Jellyfin container (Task 5).
- Work on branch `feature/list-templates` (already created, spec committed).
- All file paths below are relative to the repo root (worktree `.claude/worktrees/collection-group-by-round-robin`).

## Load-Bearing Facts (verified against the codebase)

- `editPlaylist` (config-lists.js:894-1112) and `clonePlaylist` (config-lists.js:1114-1309) are ~400 lines of near-duplicate DTO→form population. Differences (complete list):
  1. edit stashes `page._editingPlaylistCreatedByUserId` (920-922); clone doesn't (createPlaylist then assigns fresh, config-lists.js:486-491).
  2. clone sets `#playlistName` to `Name + ' (Copy)'` BEFORE `switchToTab` (1149-1152) to stop `populateFormDefaults` from firing (switchToTab populates defaults when NOT editMode AND `#playlistName` empty — config-init.js:828-839); edit sets name mid-flow (939) and calls switchToTab LATE (1081).
  3. clone clears edit state early (`setPageEditState(page, false, null)`, 1155); edit sets `setPageEditState(page, true, playlistId)` late (1061) plus edit-only UI: `#edit-mode-indicator` display flex (1064-1067), submitBtn text `'Update ' + listType` (1068-1072), create-tab button text `'Edit List'` (1075-1078).
  4. clone holds `page._skipMediaTypeChangeHandlers = true` from before `handleListTypeChange` (1161) to near the end (1277) and clears `page._mediaTypeUpdateTimer` (1280-1283); edit only wraps the `setSelectedItems` call (987-996).
  5. rules guard: edit requires `ExpressionSets.some(es => es.Expressions && es.Expressions.length > 0)` (1023-1024); clone only checks `length > 0` (1239). Use edit's (stricter, falls back to an initial empty group).
  6. similarity fields page key: edit `_editingPlaylistSimilarityFields` (1029), clone `_cloningPlaylistSimilarityFields` (1244); `populateRuleRow` reads `page._editingPlaylistSimilarityFields || page._cloningPlaylistSimilarityFields` (config-rules.js:3071). The shared helper uses `_editingPlaylistSimilarityFields` and clears both first.
  7. images: edit calls `loadExistingImages(page, playlistId).then(re-sync syncAdvancedSection)` plus an immediate `syncAdvancedSection` (1094-1101); clone never loads images and calls `syncAdvancedSection` once (1295).
  8. post-population update ordering differs slightly (edit: sorts → sort-visibility → field selects → per-field visibilities → rule buttons; clone: rule buttons → field selects → visibilities → sorts). Both work; the helper uses edit's order.
  9. edit `console.warn`s on missing MaxItems/MaxPlayTime elements; clone is silent. Helper keeps the warns.
  10. Both call `loadRandomGroupSelectionIntoUI` twice (963+1002 / 1188+1216) — existing quirk, keep it.
- Population is synchronous (rule rows built from cached `SmartLists.availableFields`, loaded at page init by `loadAndPopulateFields`, config-core.js:426-446). Async edges (people-subfield setTimeout, `loadUsersForRule` promises) are internal to `populateRuleRow` and need no handling.
- **Empty rule values do NOT block save.** `collectRulesFromForm` silently skips a rule when MemberName/Operator/TargetValue is empty (config-rules.js:2802); `createPlaylist` never checks ExpressionSets non-empty (config-lists.js:297-530); server-side `ValidateStringValue` accepts empty (InputValidator.cs:126-131). Placeholder templates therefore need an explicit client-side guard (Task 3). Exception: empty BUMPER rule values already block via `bumperResult.valid` (config-lists.js:417-420).
- `syncAdvancedSection(page, playlist)` (config-lists.js:716-759) computes chips + auto-expands the More-options fold FROM THE PASSED OBJECT, and is only ever called explicitly — template flow must call it with the template dto.
- Page context: `SmartLists.isUserPage(page)` (config-core.js), user page sets `window.SmartLists.IS_USER_PAGE = true` in an inline script before config-core.js loads (user-playlists.html:510-513). User capabilities live in `window.SmartLists.userCapabilities` (loaded via `Plugins/SmartLists/User/Capabilities`, user-playlists.html:516-534) — check the exact property name for collection permission there (expected `CanCreateCollections`) before using it in Task 3.
- ExternalList rule field IS available on the user page (same `SharedFieldDefinitions.GetAvailableFields()` payload from both fields endpoints; no user-page filtering in config-rules.js).
- `https://trakt.tv/movies/trending` is an explicitly supported provider URL (TraktListProvider.cs:17, chart regex :252) but needs the admin-configured Trakt Client ID to fetch.
- Delegated click handler pattern: single `page.addEventListener('click', ...)` in `setupEventListeners` (config-init.js:984-1316); add new button handling inside it via `target.closest(...)`.
- DTO value vocabulary used by the catalog (exact strings): see Task 2 catalog code — SortBy values from config-core.js:65-91; `GroupByField` ∈ 'SeriesName'|'Collections'|'AlbumName'|'Artist'|'Genres'|'Studios'; `WithinGroupOrder` only 'AirDate'; directionless sorts still carry `SortOrder: 'Ascending'`; relative dates `'30:days'`; booleans `'true'`/`'false'`; PlaybackStatus 'Played'|'InProgress'|'Unplayed'; schedule Time `'HH:MM:00'`, DayOfWeek integer 0=Sunday; RandomGroupSelection GroupBy 'Album' for whole albums.

## File Structure

- Modify `Jellyfin.Plugin.SmartLists/Configuration/config-lists.js` — Task 1 (extract `populateFormFromDto`, rewrite edit/clone as wrappers), Task 3 (placeholder guard in `createPlaylist`, flag reset in `clearForm`).
- Create `Jellyfin.Plugin.SmartLists/Configuration/config-templates.js` — Task 2 (catalog) + Task 3 (`initTemplatePicker`, `useTemplate`).
- Create `dev/validate-templates.js` — Task 2 (dev-only catalog validator, not shipped).
- Modify `Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj`, `Jellyfin.Plugin.SmartLists/Plugin.cs` — Task 2 (registration).
- Modify `Jellyfin.Plugin.SmartLists/Configuration/config.html`, `user-playlists.html` — Task 2 (script tag), Task 3 (picker markup).
- Modify `Jellyfin.Plugin.SmartLists/Configuration/config-init.js` — Task 3 (picker init + Use-button click wiring).
- Modify `docs/content/user-guide/configuration.md`, `docs/content/getting-started/quick-start.md`, `docs/content/index.md`, `README.md` — Task 4.

---

### Task 1: Extract shared `populateFormFromDto` (pure refactor)

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-lists.js:894-1309`

**Interfaces:**
- Consumes: existing `SmartLists.*` population helpers (stagePendingUserSelection, setElementValue/Checked, handleListTypeChange, loadSchedulesIntoUI, loadVisibilitySchedulesIntoUI, loadRandomGroupSelectionIntoUI, setSelectedItems, setUserIdValueWithRetry, applyPendingUserSelection, createInitialLogicGroup, addNewLogicGroup, populateLogicGroupExpressions, populateBumperConfigIntoForm, loadSortOptionsIntoUI, updateAll*Visibility, updateAllFieldSelects, updateRuleButtonVisibility, setPageEditState, switchToTab, initMetadataTagsInput, loadExistingImages, syncAdvancedSection).
- Produces: `SmartLists.populateFormFromDto(page, playlist, opts)` where `opts = { editMode?: boolean, playlistId?: string|null, name?: string }`. Behavior: populates the whole create form from a SmartListDto-shaped object; `opts.name` overrides `playlist.Name`; `editMode: true` additionally stashes CreatedByUserId, sets edit state/indicator/submit text/tab text and loads images. Throws on population errors (callers catch). Task 3 calls it with `{ name: template.name }`.

- [ ] **Step 1: Replace `editPlaylist` and `clonePlaylist` bodies with the shared helper + thin wrappers**

In `config-lists.js`, replace the entire block from line 894 (`SmartLists.editPlaylist = function (page, playlistId) {`) through line 1309 (end of `clonePlaylist`, the line before `SmartLists.cancelEdit = ...`) with:

```js
    // Shared DTO -> create-form population engine used by edit, clone, and
    // template flows. opts: { editMode, playlistId, name }.
    SmartLists.populateFormFromDto = function (page, playlist, opts) {
        opts = opts || {};
        const editMode = opts.editMode === true;
        const listType = playlist.Type || 'Playlist';
        const isCollection = listType === 'Collection';

        if (editMode && playlist.CreatedByUserId) {
            // Store CreatedByUserId to preserve it during updates
            page._editingPlaylistCreatedByUserId = playlist.CreatedByUserId;
        }

        // Extract userIds BEFORE calling handleListTypeChange (which triggers loadUsers)
        // This ensures pendingUserIds is set before loadUsers checks for it
        SmartLists.stagePendingUserSelection(page, playlist, isCollection);

        // Set playlist name FIRST (before switchToTab) to prevent populateFormDefaults from being called
        // switchToTab checks if name is empty and calls populateFormDefaults if so, which would regenerate checkboxes
        SmartLists.setElementValue(page, '#playlistName', opts.name !== undefined ? opts.name : (playlist.Name || ''));

        // Switch to Create tab
        SmartLists.switchToTab(page, 'create');

        // Clear any existing edit state (edit mode is set after population below)
        SmartLists.setPageEditState(page, false, null);

        // Set list type
        SmartLists.setElementValue(page, '#listType', listType);

        // Set flag to prevent media type change handlers from interfering during population
        page._skipMediaTypeChangeHandlers = true;

        // Trigger type change handler to show/hide fields
        SmartLists.handleListTypeChange(page);

        // Only set public for playlists
        if (!isCollection) {
            SmartLists.setElementChecked(page, '#playlistIsPublic', playlist.Public || false);
        }

        SmartLists.setElementChecked(page, '#playlistIsEnabled', playlist.Enabled !== false); // Default to true for backward compatibility
        SmartLists.setElementChecked(page, '#playlistIncludeExtras', playlist.IncludeExtras || false);
        SmartLists.setElementChecked(page, '#playlistHideWhenEmpty', playlist.HideWhenEmpty || false);

        // Handle AutoRefresh with backward compatibility
        const autoRefreshValue = playlist.AutoRefresh !== undefined ? playlist.AutoRefresh : 'Never';
        const autoRefreshElement = page.querySelector('#autoRefreshMode');
        if (autoRefreshElement) {
            autoRefreshElement.value = autoRefreshValue;
        }

        // Handle schedule settings with backward compatibility
        SmartLists.loadSchedulesIntoUI(page, playlist);

        // Handle visibility schedule settings
        SmartLists.loadVisibilitySchedulesIntoUI(page, playlist);

        SmartLists.loadRandomGroupSelectionIntoUI(page, playlist);

        // Handle MaxItems with backward compatibility
        // Default to 0 (unlimited) for lists that didn't have this setting
        const maxItemsValue = (playlist.MaxItems !== undefined && playlist.MaxItems !== null) ? playlist.MaxItems : 0;
        const maxItemsElement = page.querySelector('#playlistMaxItems');
        if (maxItemsElement) {
            maxItemsElement.value = maxItemsValue;
        } else {
            console.warn('Max Items element not found when trying to populate form');
        }

        // Handle MaxPlayTimeMinutes with backward compatibility
        const maxPlayTimeMinutesValue = (playlist.MaxPlayTimeMinutes !== undefined && playlist.MaxPlayTimeMinutes !== null) ? playlist.MaxPlayTimeMinutes : 0;
        const maxPlayTimeMinutesElement = page.querySelector('#playlistMaxPlayTimeMinutes');
        if (maxPlayTimeMinutesElement) {
            maxPlayTimeMinutesElement.value = maxPlayTimeMinutesValue;
        } else {
            console.warn('Max Playtime Minutes element not found when trying to populate form');
        }

        // Set media types
        if (playlist.MediaTypes && playlist.MediaTypes.length > 0) {
            SmartLists.setSelectedItems(page, 'mediaTypesMultiSelect', playlist.MediaTypes, 'media-type-multi-select-checkbox', 'Select media types...');
        } else {
            SmartLists.setSelectedItems(page, 'mediaTypesMultiSelect', [], 'media-type-multi-select-checkbox', 'Select media types...');
        }

        // Update Include Extras visibility based on loaded media types
        if (SmartLists.updateIncludeExtrasVisibility) {
            SmartLists.updateIncludeExtrasVisibility(page);
        }
        SmartLists.loadRandomGroupSelectionIntoUI(page, playlist);

        // Set the list owner (for both playlists and collections)
        if (isCollection) {
            // Collections always have single user
            const userIdString = playlist.UserId ? String(playlist.UserId) : null;
            if (userIdString) {
                SmartLists.setUserIdValueWithRetry(page, userIdString);
            }
        } else {
            // Playlists can have multiple users; selection was staged above
            SmartLists.applyPendingUserSelection(page);
            // If checkboxes don't exist yet, loadUsers will set them when it finishes
        }

        // Clear existing rules (applies to both playlists and collections)
        const rulesContainer = page.querySelector('#rules-container');
        if (rulesContainer) {
            rulesContainer.innerHTML = '';
        }

        // Clear stale similarity-field context from any previous population
        page._editingPlaylistSimilarityFields = null;
        page._cloningPlaylistSimilarityFields = null;

        // Populate logic groups and rules
        if (playlist.ExpressionSets && playlist.ExpressionSets.length > 0 &&
            playlist.ExpressionSets.some(function (es) { return es.Expressions && es.Expressions.length > 0; })) {
            playlist.ExpressionSets.forEach(function (expressionSet, groupIndex) {
                const logicGroup = groupIndex === 0 ? SmartLists.createInitialLogicGroup(page) : SmartLists.addNewLogicGroup(page);

                // Store similarity comparison fields on page for populateRuleRow to access
                page._editingPlaylistSimilarityFields = playlist.SimilarityComparisonFields;

                // Populate rules and per-group MaxItems into this logic group
                SmartLists.populateLogicGroupExpressions(page, logicGroup, expressionSet);
            });
        } else {
            // No rules exist - create an initial logic group with a placeholder rule
            SmartLists.createInitialLogicGroup(page);
        }

        // Clear and populate bumper rules
        SmartLists.populateBumperConfigIntoForm(page, playlist);

        // Set sort options AFTER rules are populated so hasSimilarToRuleInForm() can detect them
        SmartLists.loadSortOptionsIntoUI(page, playlist);
        // Update sort options visibility based on populated rules
        SmartLists.updateAllSortOptionsVisibility(page);

        // Update field selects first, then per-field options visibility based on selected media types
        SmartLists.updateAllFieldSelects(page);
        SmartLists.updateAllTagsOptionsVisibility(page);
        SmartLists.updateAllStudiosOptionsVisibility(page);
        SmartLists.updateAllGenresOptionsVisibility(page);
        SmartLists.updateAllAudioLanguagesOptionsVisibility(page);
        SmartLists.updateAllCollectionsOptionsVisibility(page);
        SmartLists.updateAllNextUnwatchedOptionsVisibility(page);

        // Update button visibility
        SmartLists.updateRuleButtonVisibility(page);

        // Clear flag to re-enable change event handlers
        page._skipMediaTypeChangeHandlers = false;

        // Clear any pending media type update timers just in case
        if (page._mediaTypeUpdateTimer) {
            clearTimeout(page._mediaTypeUpdateTimer);
            page._mediaTypeUpdateTimer = null;
        }

        if (editMode) {
            // Set edit mode state
            SmartLists.setPageEditState(page, true, opts.playlistId);

            // Update UI to show edit mode
            const editIndicator = page.querySelector('#edit-mode-indicator');
            if (editIndicator) {
                editIndicator.style.display = 'flex';
            }
            const submitBtn = page.querySelector('#submitBtn');
            if (submitBtn) {
                const currentListType = SmartLists.getElementValue(page, '#listType', 'Playlist');
                submitBtn.textContent = 'Update ' + currentListType;
            }

            // Update tab button text
            const createTabButton = page.querySelector('a[data-tab="create"]');
            if (createTabButton) {
                createTabButton.textContent = 'Edit List';
            }
        }

        // Populate metadata fields
        SmartLists.setElementValue(page, '#metadataSortTitle', playlist.SortTitle || '');
        SmartLists.setElementValue(page, '#metadataOverview', playlist.Overview || '');
        SmartLists.setElementValue(page, '#metadataFavorite', playlist.Favorite === true ? 'true' : (playlist.Favorite === false ? 'false' : ''));
        page._metadataTagsWasManaged = playlist.Tags !== undefined && playlist.Tags !== null;
        if (SmartLists.initMetadataTagsInput) {
            SmartLists.initMetadataTagsInput(page, playlist.Tags || []);
        }

        if (editMode) {
            // Load existing images for this list, then re-sync the
            // Advanced fold so an images-only list still expands.
            if (SmartLists.loadExistingImages) {
                SmartLists.loadExistingImages(page, opts.playlistId).then(function () {
                    SmartLists.syncAdvancedSection(page, playlist);
                });
            }
        }

        // Chips + auto-expand from the populated config (images re-sync above in edit mode)
        SmartLists.syncAdvancedSection(page, playlist);
    };

    SmartLists.editPlaylist = function (page, playlistId) {
        const apiClient = SmartLists.getApiClient();
        Dashboard.showLoadingMsg();

        // Always scroll to top when entering edit mode (auto for instant behavior)
        window.scrollTo({ top: 0, behavior: 'auto' });

        apiClient.ajax({
            type: 'GET',
            url: apiClient.getUrl(SmartLists.ENDPOINTS.base + '/' + playlistId),
            contentType: 'application/json'
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status + ': ' + response.statusText);
            }
            return response.json();
        }).then(function (playlist) {
            Dashboard.hideLoadingMsg();

            if (!playlist) {
                SmartLists.showNotification('No playlist data received from server.');
                return;
            }

            try {
                SmartLists.populateFormFromDto(page, playlist, { editMode: true, playlistId: playlistId });
            } catch (formError) {
                console.error('Error populating form for edit:', formError);
                SmartLists.showNotification('Error loading playlist data for editing: ' + formError.message);
            }
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            console.error('Error loading playlist for edit:', err);
            SmartLists.handleApiError(err, 'Failed to load playlist for editing');
        });
    };

    SmartLists.clonePlaylist = function (page, playlistId, playlistName) {
        const apiClient = SmartLists.getApiClient();
        Dashboard.showLoadingMsg();

        // Always scroll to top when entering clone mode (auto for instant behavior)
        window.scrollTo({ top: 0, behavior: 'auto' });

        apiClient.ajax({
            type: 'GET',
            url: apiClient.getUrl(SmartLists.ENDPOINTS.base + '/' + playlistId),
            contentType: 'application/json'
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status + ': ' + response.statusText);
            }
            return response.json();
        }).then(function (playlist) {
            Dashboard.hideLoadingMsg();

            if (!playlist) {
                SmartLists.showNotification('No playlist data received from server.');
                return;
            }

            try {
                SmartLists.populateFormFromDto(page, playlist, { name: (playlist.Name || '') + ' (Copy)' });

                // Show success message
                SmartLists.showNotification('List "' + playlistName + '" cloned successfully! You can now modify and create the new list.', 'success');
            } catch (formError) {
                console.error('Error populating form for clone:', formError);
                SmartLists.showNotification('Error loading list data for cloning: ' + formError.message);
            }
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            console.error('Error loading list for clone:', err);
            SmartLists.handleApiError(err, 'Failed to load list for cloning');
        });
    };
```

Known intentional unifications (all no-op or strictly-safer):
- Edit mode now sets the name before switching tabs and clears edit state before re-setting it — safe because the name is always non-empty for an existing list, so `populateFormDefaults` never fires.
- Edit mode now gets clone's wider `_skipMediaTypeChangeHandlers` scope and the `_mediaTypeUpdateTimer` clear — strictly more defensive.
- Clone now gets edit's stricter empty-ExpressionSets guard and the missing-element `console.warn`s.
- Clone keeps NOT hiding `#edit-mode-indicator` / resetting submit text (matches today's behavior; `cancelEdit`/`clearForm` own that).

- [ ] **Step 2: Syntax check**

Run: `node --check Jellyfin.Plugin.SmartLists/Configuration/config-lists.js`
Expected: no output, exit 0.

- [ ] **Step 3: Build**

Run: `dotnet build Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj -c Release`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).` for both TFMs.

- [ ] **Step 4: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/config-lists.js
git commit -m "refactor: extract shared populateFormFromDto from edit/clone duplication"
```

---

### Task 2: Template catalog + registration + validator

**Files:**
- Create: `Jellyfin.Plugin.SmartLists/Configuration/config-templates.js`
- Create: `dev/validate-templates.js`
- Modify: `Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj` (after the config-lists.js entry, ~line 48)
- Modify: `Jellyfin.Plugin.SmartLists/Plugin.cs` (GetPages, after the config-lists.js entry, ~line 148)
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config.html` (script list, after config-lists.js at ~line 1260)
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/user-playlists.html` (script list, after config-lists.js at ~line 635)

**Interfaces:**
- Consumes: nothing (data module; IIFE over `window.SmartLists`).
- Produces: `SmartLists.TEMPLATES` — array of `{ id, name, category, description, adminOnly, inputHint, dto }`. `dto` is a partial SmartListDto (always has `Type`, `MediaTypes`, `ExpressionSets`, `Order`). Task 3 reads this array.

- [ ] **Step 1: Create `config-templates.js` with the full catalog**

```js
// Template catalog for the "Start from a template" picker on the Create tab.
// Each dto is a partial SmartListDto containing only definition fields —
// never instance fields (Id, FileName, Jellyfin IDs, dates, stats, images).
// Rule MemberName/Operator/TargetValue strings and sort names must match the
// backend vocabulary (FieldRegistry.cs / OrderFactory) — dev/validate-templates.js
// checks the catalog against config-core.js.
(function (SmartLists) {
    'use strict';

    SmartLists.TEMPLATES = [
        {
            id: 'tv-channel',
            name: 'TV Channel',
            category: 'TV',
            description: 'Interleaves unwatched episodes from all your shows like a TV channel: one episode per series, round-robin, in order.',
            adminOnly: false,
            inputHint: null,
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Episode'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'PlaybackStatus', Operator: 'Equal', TargetValue: 'Unplayed' }
                    ]
                }],
                Order: { SortOptions: [{ SortBy: 'Round Robin', SortOrder: 'Ascending', GroupByField: 'SeriesName' }] },
                AutoRefresh: 'OnLibraryChanges'
            }
        },
        {
            id: 'franchise-tv-channel',
            name: 'Franchise TV Channel',
            category: 'TV',
            description: 'Rotates through your collections (franchises) in broadcast order, resuming the least recently watched one. Crossover episodes that aired within 3 days stay together.',
            adminOnly: false,
            inputHint: null,
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Episode'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'PlaybackStatus', Operator: 'Equal', TargetValue: 'Unplayed' }
                    ]
                }],
                Order: {
                    SortOptions: [{
                        SortBy: 'Least Recently Watched Round Robin',
                        SortOrder: 'Ascending',
                        GroupByField: 'Collections',
                        WithinGroupOrder: 'AirDate',
                        AirBlockWindowDays: 3
                    }]
                },
                AutoRefresh: 'OnAllChanges'
            }
        },
        {
            id: 'continue-watching',
            name: 'Continue Watching',
            category: 'TV',
            description: 'The next unwatched episode of every show you are in the middle of, with the show you have not touched longest surfacing first. Updates itself as you watch.',
            adminOnly: false,
            inputHint: null,
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Episode'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'NextUnwatched', Operator: 'Equal', TargetValue: 'true' }
                    ]
                }],
                Order: { SortOptions: [{ SortBy: 'Least Recently Watched Round Robin', SortOrder: 'Ascending', GroupByField: 'SeriesName' }] },
                AutoRefresh: 'OnAllChanges'
            }
        },
        {
            id: 'saturday-cartoons',
            name: 'Saturday Morning Cartoons',
            category: 'TV',
            description: 'Animated episodes with bumper clips woven in every 2 episodes, visible only on weekend mornings. Tag your bumper videos and enter that tag in the bumper rule.',
            adminOnly: false,
            inputHint: 'Enter the tag of your bumper videos in the empty bumper rule value (tag some short videos first).',
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Episode'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'Genres', Operator: 'Contains', TargetValue: 'Animation' },
                        { MemberName: 'PlaybackStatus', Operator: 'Equal', TargetValue: 'Unplayed' }
                    ]
                }],
                Order: { SortOptions: [{ SortBy: 'Round Robin', SortOrder: 'Ascending', GroupByField: 'SeriesName' }] },
                Bumpers: {
                    ExpressionSets: [{
                        Expressions: [
                            { MemberName: 'Tags', Operator: 'Contains', TargetValue: '' }
                        ]
                    }],
                    MediaTypes: ['Video'],
                    BumperOrder: 'Random',
                    Interval: 2
                },
                VisibilitySchedules: [
                    { Action: 'Enable', Trigger: 'Weekly', DayOfWeek: 6, Time: '06:00:00' },
                    { Action: 'Disable', Trigger: 'Weekly', DayOfWeek: 6, Time: '12:00:00' },
                    { Action: 'Enable', Trigger: 'Weekly', DayOfWeek: 0, Time: '06:00:00' },
                    { Action: 'Disable', Trigger: 'Weekly', DayOfWeek: 0, Time: '12:00:00' }
                ]
            }
        },
        {
            id: 'because-you-watched',
            name: 'Because You Watched…',
            category: 'Movies',
            description: 'Movies similar to a title you pick, best matches first. Compares genres, tags, actors and directors.',
            adminOnly: false,
            inputHint: 'Type the title to find similar movies for in the empty rule value.',
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Movie'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'SimilarTo', Operator: 'Equal', TargetValue: '' }
                    ]
                }],
                SimilarityComparisonFields: ['Genre', 'Tags', 'Actors', 'Directors'],
                Order: { SortOptions: [{ SortBy: 'Similarity', SortOrder: 'Descending' }] },
                MaxItems: 25
            }
        },
        {
            id: 'balanced-mix',
            name: 'Balanced Genre Mix',
            category: 'Movies',
            description: 'Up to 15 movies each of Action, Comedy and Drama, kept in that block order. Change the genres to taste — each rule block has its own item limit.',
            adminOnly: false,
            inputHint: null,
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Movie'],
                ExpressionSets: [
                    { Expressions: [{ MemberName: 'Genres', Operator: 'Contains', TargetValue: 'Action' }], MaxItems: 15 },
                    { Expressions: [{ MemberName: 'Genres', Operator: 'Contains', TargetValue: 'Comedy' }], MaxItems: 15 },
                    { Expressions: [{ MemberName: 'Genres', Operator: 'Contains', TargetValue: 'Drama' }], MaxItems: 15 }
                ],
                Order: { SortOptions: [{ SortBy: 'Rule Block Order', SortOrder: 'Ascending' }] }
            }
        },
        {
            id: 'fresh-and-unseen',
            name: 'Fresh & Unseen',
            category: 'Movies',
            description: 'Movies added in the last 30 days that you have not watched yet, newest first. Kept current automatically.',
            adminOnly: false,
            inputHint: null,
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Movie'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'DateCreated', Operator: 'NewerThan', TargetValue: '30:days' },
                        { MemberName: 'PlaybackStatus', Operator: 'Equal', TargetValue: 'Unplayed' }
                    ]
                }],
                Order: { SortOptions: [{ SortBy: 'DateCreated', SortOrder: 'Descending' }] },
                AutoRefresh: 'OnAllChanges'
            }
        },
        {
            id: 'trakt-trending',
            name: 'Trending Now (Trakt)',
            category: 'Movies',
            description: 'A collection of the movies trending on Trakt right now, refreshed daily. Requires a Trakt Client ID in the plugin settings (admin, Settings tab). Hidden while empty.',
            adminOnly: false,
            inputHint: null,
            dto: {
                Type: 'Collection',
                MediaTypes: ['Movie'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'ExternalList', Operator: 'Equal', TargetValue: 'https://trakt.tv/movies/trending' }
                    ]
                }],
                Order: { SortOptions: [{ SortBy: 'External List Order', SortOrder: 'Ascending' }] },
                Schedules: [{ Trigger: 'Daily', Time: '06:00:00' }],
                HideWhenEmpty: true,
                MaxItems: 50
            }
        },
        {
            id: 'weekly-jams',
            name: 'Weekly Jams (ListenBrainz)',
            category: 'Music',
            description: 'Your personalized ListenBrainz Weekly Jams as a playlist, in list order, refreshed weekly. No API key needed — just your ListenBrainz username in the feed URL.',
            adminOnly: false,
            inputHint: 'Paste your feed URL in the empty rule value: https://listenbrainz.org/syndication-feed/user/YOUR_USERNAME/recommendations?recommendation_type=weekly-jams',
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Audio'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'ExternalList', Operator: 'Equal', TargetValue: '' }
                    ]
                }],
                Order: { SortOptions: [{ SortBy: 'External List Order', SortOrder: 'Ascending' }] },
                Schedules: [{ Trigger: 'Weekly', DayOfWeek: 1, Time: '08:00:00' }]
            }
        },
        {
            id: 'album-roulette',
            name: 'Album Roulette',
            category: 'Music',
            description: 'A few random complete albums (at least 5 tracks each), tracks in album order, capped at 3 hours. Re-roll by refreshing the list.',
            adminOnly: false,
            inputHint: null,
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Audio'],
                ExpressionSets: [],
                RandomGroupSelection: { Enabled: true, GroupBy: 'Album', MinimumItems: 5 },
                Order: {
                    SortOptions: [
                        { SortBy: 'AlbumName', SortOrder: 'Ascending' },
                        { SortBy: 'TrackNumber', SortOrder: 'Ascending' }
                    ]
                },
                MaxPlayTimeMinutes: 180
            }
        }
    ];
})(window.SmartLists = window.SmartLists || {});
```

- [ ] **Step 2: Register in `.csproj`**

In `Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj`, after the `config-lists.js` entry (lines 47-48):

```xml
    <!-- Template catalog and picker -->
    <EmbeddedResource Include="Configuration\config-templates.js" />
```

- [ ] **Step 3: Register in `Plugin.cs`**

In `GetPages()` (Plugin.cs:74-180), after the `config-lists.js` PluginPageInfo entry (~line 148):

```csharp
                // Template catalog and picker
                new PluginPageInfo
                {
                    Name = "config-templates.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config-templates.js",
                },
```

- [ ] **Step 4: Add script tags in both HTML pages**

`config.html` after the config-lists.js script line (~1260):

```html
        <!-- Template catalog and picker -->
        <script src="configurationpage?name=config-templates.js"></script>
```

`user-playlists.html` after the config-lists.js script line (~635): same two lines.

- [ ] **Step 5: Create `dev/validate-templates.js`**

Dev-only guard against typos in the canned DTOs. Loads config-core.js and config-templates.js in a Node vm with browser stubs, then checks every template's vocabulary against the live `SmartLists` constants plus the file sources.

```js
#!/usr/bin/env node
// Validates the template catalog (config-templates.js) against the frontend
// vocabulary in config-core.js. Run: node dev/validate-templates.js
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const cfgDir = path.join(__dirname, '..', 'Jellyfin.Plugin.SmartLists', 'Configuration');
const coreSrc = fs.readFileSync(path.join(cfgDir, 'config-core.js'), 'utf8');
const rulesSrc = fs.readFileSync(path.join(cfgDir, 'config-rules.js'), 'utf8');
const templatesSrc = fs.readFileSync(path.join(cfgDir, 'config-templates.js'), 'utf8');

const stubEl = { classList: { contains: function () { return false; } } };
const sandbox = {
    window: {},
    document: {
        querySelector: function () { return null; },
        querySelectorAll: function () { return []; },
        addEventListener: function () {},
        createElement: function () { return stubEl; }
    },
    console: console,
    setTimeout: setTimeout,
    clearTimeout: clearTimeout
};
sandbox.window.SmartLists = {};
vm.createContext(sandbox);
vm.runInContext(coreSrc, sandbox, { filename: 'config-core.js' });
vm.runInContext(templatesSrc, sandbox, { filename: 'config-templates.js' });

const SL = sandbox.window.SmartLists;
const errors = [];
function check(cond, msg) { if (!cond) { errors.push(msg); } }

check(Array.isArray(SL.TEMPLATES) && SL.TEMPLATES.length > 0, 'SmartLists.TEMPLATES missing or empty');

const sortValues = (SL.SORT_OPTIONS || []).map(function (o) { return typeof o === 'string' ? o : o.value; });
const groupFields = (SL.ROUND_ROBIN_GROUP_FIELDS || []).map(function (o) { return typeof o === 'string' ? o : o.value; });
const OPERATORS = ['Equal', 'NotEqual', 'Contains', 'NotContains', 'IsIn', 'IsNotIn', 'MatchRegex',
    'GreaterThan', 'LessThan', 'GreaterThanOrEqual', 'LessThanOrEqual',
    'After', 'Before', 'NewerThan', 'OlderThan', 'Weekday'];
const MEDIA_TYPES = ['Episode', 'Series', 'Season', 'Movie', 'Audio', 'MusicAlbum', 'MusicVideo', 'Video', 'Photo', 'Book', 'AudioBook'];
const AUTO_REFRESH = ['Never', 'OnLibraryChanges', 'OnAllChanges'];
const TRIGGERS = ['None', 'Daily', 'Weekly', 'Monthly', 'Interval', 'Yearly'];
const seenIds = {};

(SL.TEMPLATES || []).forEach(function (t) {
    const p = 'template "' + t.id + '": ';
    check(t.id && t.name && t.category && t.description, p + 'id/name/category/description required');
    check(!seenIds[t.id], p + 'duplicate id');
    seenIds[t.id] = true;
    check(t.dto && typeof t.dto === 'object', p + 'dto required');
    if (!t.dto) { return; }
    const dto = t.dto;
    check(dto.Type === 'Playlist' || dto.Type === 'Collection', p + 'Type must be Playlist|Collection');
    check(Array.isArray(dto.MediaTypes) && dto.MediaTypes.length > 0, p + 'MediaTypes required');
    (dto.MediaTypes || []).forEach(function (m) {
        check(MEDIA_TYPES.indexOf(m) !== -1, p + 'unknown MediaType ' + m);
    });
    check(Array.isArray(dto.ExpressionSets), p + 'ExpressionSets must be an array');
    ['Id', 'FileName', 'JellyfinPlaylistId', 'JellyfinCollectionId', 'DateCreated', 'LastRefreshed',
        'ItemCount', 'TotalRuntimeMinutes', 'CustomImages', 'UserPlaylists', 'UserId', 'CreatedByUserId'
    ].forEach(function (k) {
        check(!(k in dto), p + 'instance field ' + k + ' must not be in a template dto');
    });
    function checkExpressionSets(sets, where) {
        (sets || []).forEach(function (set) {
            check(Array.isArray(set.Expressions), p + where + ' set missing Expressions array');
            (set.Expressions || []).forEach(function (e) {
                check(typeof e.MemberName === 'string' && e.MemberName.length > 0, p + where + ' rule missing MemberName');
                check(OPERATORS.indexOf(e.Operator) !== -1, p + where + ' unknown Operator ' + e.Operator);
                check(typeof e.TargetValue === 'string', p + where + ' TargetValue must be a string');
                // Every valid field name appears as a quoted string in the frontend sources
                const quoted = "'" + e.MemberName + "'";
                check(coreSrc.indexOf(quoted) !== -1 || rulesSrc.indexOf(quoted) !== -1,
                    p + where + ' MemberName not found in config-core.js/config-rules.js: ' + e.MemberName);
            });
        });
    }
    checkExpressionSets(dto.ExpressionSets, 'rule');
    if (dto.Bumpers) {
        checkExpressionSets(dto.Bumpers.ExpressionSets, 'bumper');
        check(['Random', 'Name', 'ReleaseDate'].indexOf(dto.Bumpers.BumperOrder) !== -1, p + 'bad BumperOrder');
        check(dto.Bumpers.Interval >= 1, p + 'bumper Interval must be >= 1');
        check(dto.Type === 'Playlist', p + 'Bumpers are playlist-only');
    }
    ((dto.Order || {}).SortOptions || []).forEach(function (s) {
        check(sortValues.indexOf(s.SortBy) !== -1, p + 'unknown SortBy ' + s.SortBy);
        check(s.SortOrder === 'Ascending' || s.SortOrder === 'Descending', p + 'bad SortOrder for ' + s.SortBy);
        if (s.GroupByField) {
            check(groupFields.indexOf(s.GroupByField) !== -1, p + 'unknown GroupByField ' + s.GroupByField);
        }
        if (s.WithinGroupOrder) {
            check(s.WithinGroupOrder === 'AirDate', p + 'WithinGroupOrder must be AirDate');
        }
        if (s.AirBlockWindowDays !== undefined) {
            check(s.GroupByField === 'Collections' && s.WithinGroupOrder === 'AirDate',
                p + 'AirBlockWindowDays needs GroupByField Collections + WithinGroupOrder AirDate');
            check(s.AirBlockWindowDays >= 0 && s.AirBlockWindowDays <= 30, p + 'AirBlockWindowDays out of range');
        }
    });
    if (dto.AutoRefresh !== undefined) {
        check(AUTO_REFRESH.indexOf(dto.AutoRefresh) !== -1, p + 'bad AutoRefresh ' + dto.AutoRefresh);
    }
    (dto.Schedules || []).concat(dto.VisibilitySchedules || []).forEach(function (s) {
        check(TRIGGERS.indexOf(s.Trigger) !== -1, p + 'bad schedule Trigger ' + s.Trigger);
        if (s.Time !== undefined) {
            check(/^\d{2}:\d{2}:00$/.test(s.Time), p + 'schedule Time must be HH:MM:00, got ' + s.Time);
        }
        if (s.DayOfWeek !== undefined) {
            check(typeof s.DayOfWeek === 'number' && s.DayOfWeek >= 0 && s.DayOfWeek <= 6, p + 'DayOfWeek must be integer 0-6');
        }
    });
    (dto.VisibilitySchedules || []).forEach(function (s) {
        check(s.Action === 'Enable' || s.Action === 'Disable', p + 'visibility schedule needs Action Enable|Disable');
    });
    if (dto.RandomGroupSelection) {
        check(['Artists', 'AlbumArtists', 'Album', 'SeriesName', 'Genres', 'Studios', 'Tags'].indexOf(dto.RandomGroupSelection.GroupBy) !== -1,
            p + 'bad RandomGroupSelection.GroupBy');
    }
    if (t.inputHint) {
        // Placeholder templates must actually contain an empty value to fill
        const allSets = (dto.ExpressionSets || []).concat(dto.Bumpers ? dto.Bumpers.ExpressionSets : []);
        const hasEmpty = allSets.some(function (set) {
            return (set.Expressions || []).some(function (e) { return e.TargetValue === ''; });
        });
        check(hasEmpty, p + 'inputHint set but no empty TargetValue');
    }
});

if (errors.length) {
    console.error('TEMPLATE VALIDATION FAILED (' + errors.length + '):');
    errors.forEach(function (e) { console.error('  - ' + e); });
    process.exit(1);
}
console.log('All ' + SL.TEMPLATES.length + ' templates valid.');
```

Note: if `vm.runInContext(coreSrc, ...)` throws because config-core.js touches a browser API the stub lacks, extend the `sandbox.document`/`sandbox.window` stubs (no-op functions) until it loads — do NOT weaken the assertions.

- [ ] **Step 6: Run validator + syntax checks**

Run: `node dev/validate-templates.js`
Expected: `All 10 templates valid.`
Run: `node --check Jellyfin.Plugin.SmartLists/Configuration/config-templates.js`
Expected: exit 0.

- [ ] **Step 7: Build (verifies csproj/Plugin.cs registration compiles)**

Run: `dotnet build Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj -c Release`
Expected: 0 warnings, 0 errors, both TFMs.

- [ ] **Step 8: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/config-templates.js dev/validate-templates.js \
  Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj Jellyfin.Plugin.SmartLists/Plugin.cs \
  Jellyfin.Plugin.SmartLists/Configuration/config.html Jellyfin.Plugin.SmartLists/Configuration/user-playlists.html
git commit -m "feat: add template catalog with dev validator and resource registration"
```

---

### Task 3: Picker UI + apply flow + placeholder guard

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config.html` (~line 52, after `#edit-mode-indicator` closes, before the List Type `inputContainer`)
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/user-playlists.html` (~line 36, same position)
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-templates.js` (add picker logic below the catalog, inside the same IIFE)
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-init.js` (init call + Use-button click wiring)
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-lists.js` (placeholder guard in `createPlaylist`, flag reset in `clearForm`)

**Interfaces:**
- Consumes: `SmartLists.TEMPLATES` (Task 2), `SmartLists.populateFormFromDto(page, dto, opts)` (Task 1), `SmartLists.isUserPage(page)`, `SmartLists.escapeHtml` (config-core.js — verify exact helper name near config-core.js:214, it may be `escapeHtml` or similar; use whatever the escaper there is called), `SmartLists.showNotification`, `SmartLists.syncAdvancedSection`.
- Produces: `SmartLists.initTemplatePicker(page)` (idempotent; populates `#templateSelect`, binds change handler) and `SmartLists.useTemplate(page, templateId)`. Page flag `page._templatePlaceholderPending` consumed by `createPlaylist`.

- [ ] **Step 1: Add picker markup to `config.html`**

Between the closing `</div>` of `#edit-mode-indicator` (line 52) and the List Type `inputContainer` (line 53):

```html
                        <div class="inputContainer" id="templatePickerContainer" style="margin-bottom: 1em;">
                            <label class="inputLabel" for="templateSelect">Start from a template (optional)</label>
                            <div style="display: flex; gap: 0.5em; align-items: center;">
                                <select is="emby-select" id="templateSelect" class="emby-select" style="flex: 1;">
                                </select>
                                <button type="button" id="useTemplateBtn" class="emby-button raised" disabled>Use</button>
                            </div>
                            <div id="templateDescription" class="fieldDescription" style="display: none; margin-top: 0.4em;"></div>
                        </div>
```

`type="button"` is required — the button sits inside `#playlistForm` and must not submit it.

- [ ] **Step 2: Add identical markup to `user-playlists.html`**

Same block, inserted between the `#edit-mode-indicator` closing `</div>` (line 36) and the `<!-- List Type ... -->` comment (line 37).

- [ ] **Step 3: Add picker logic to `config-templates.js`**

Inside the IIFE, after the `SmartLists.TEMPLATES` array:

```js
    function visibleTemplates(page) {
        const isUserPage = SmartLists.isUserPage(page);
        const caps = SmartLists.userCapabilities || {};
        return SmartLists.TEMPLATES.filter(function (t) {
            if (isUserPage && t.adminOnly) {
                return false;
            }
            // Hide collection templates from users who cannot create collections
            if (isUserPage && t.dto.Type === 'Collection' && !caps.CanCreateCollections) {
                return false;
            }
            return true;
        });
    }

    function findTemplate(templateId) {
        for (let i = 0; i < SmartLists.TEMPLATES.length; i++) {
            if (SmartLists.TEMPLATES[i].id === templateId) {
                return SmartLists.TEMPLATES[i];
            }
        }
        return null;
    }

    SmartLists.initTemplatePicker = function (page) {
        const select = page.querySelector('#templateSelect');
        if (!select) {
            return;
        }

        const templates = visibleTemplates(page);
        let html = '<option value="">Select a template...</option>';
        let currentCategory = null;
        templates.forEach(function (t) {
            if (t.category !== currentCategory) {
                if (currentCategory !== null) {
                    html += '</optgroup>';
                }
                html += '<optgroup label="' + SmartLists.escapeHtml(t.category) + '">';
                currentCategory = t.category;
            }
            html += '<option value="' + SmartLists.escapeHtml(t.id) + '">' + SmartLists.escapeHtml(t.name) + '</option>';
        });
        if (currentCategory !== null) {
            html += '</optgroup>';
        }
        select.innerHTML = html;

        if (!select._templatePickerBound) {
            select._templatePickerBound = true;
            select.addEventListener('change', function () {
                const template = findTemplate(select.value);
                const descriptionDiv = page.querySelector('#templateDescription');
                const useBtn = page.querySelector('#useTemplateBtn');
                if (descriptionDiv) {
                    descriptionDiv.textContent = template ? template.description : '';
                    descriptionDiv.style.display = template ? 'block' : 'none';
                }
                if (useBtn) {
                    useBtn.disabled = !template;
                }
            });
        }
    };

    SmartLists.useTemplate = function (page, templateId) {
        const template = findTemplate(templateId);
        if (!template) {
            return;
        }

        // Deep-copy so form population can never mutate the catalog
        const dto = JSON.parse(JSON.stringify(template.dto));

        try {
            SmartLists.populateFormFromDto(page, dto, { name: template.name });
        } catch (formError) {
            console.error('Error applying template:', formError);
            SmartLists.showNotification('Error applying template: ' + formError.message);
            return;
        }

        page._templatePlaceholderPending = !!template.inputHint;

        if (template.inputHint) {
            SmartLists.showNotification(template.inputHint);
            // Focus the first empty rule value so the user sees what to fill in
            const inputs = page.querySelectorAll('#rules-container .rule-value-input');
            for (let i = 0; i < inputs.length; i++) {
                if (!inputs[i].value) {
                    inputs[i].focus();
                    break;
                }
            }
        } else {
            SmartLists.showNotification('Template applied - review the settings and click Create.', 'success');
        }
    };
```

Style note: `const`/`let` are fine (used throughout the existing modules); arrow functions and template literals are not.

- [ ] **Step 4: Wire init + click in `config-init.js`**

(a) In the page-init path where other populate calls run (near `loadAndPopulateFields` usage in the init/pageshow sequence around config-init.js:97-118), add:

```js
        if (SmartLists.initTemplatePicker) {
            SmartLists.initTemplatePicker(page);
        }
```

(b) Inside the page-level delegated click listener (config-init.js:984-1316), following the existing `closest` pattern (e.g. the `.clone-playlist-btn` block at 1131-1136), add:

```js
            if (target.closest('#useTemplateBtn')) {
                const templateSelect = page.querySelector('#templateSelect');
                if (templateSelect && templateSelect.value && SmartLists.useTemplate) {
                    SmartLists.useTemplate(page, templateSelect.value);
                }
            }
```

Before relying on `caps.CanCreateCollections` (Step 3), confirm the exact property name in the capabilities object loaded at user-playlists.html:516-534 / the `Plugins/SmartLists/User/Capabilities` response, and adjust if it differs.

- [ ] **Step 5: Placeholder guard in `createPlaylist` + flag reset in `clearForm`**

In `config-lists.js`, inside `SmartLists.createPlaylist` (starts line 297), after the media-types check (`selectedMediaTypes.length === 0` block, ~lines 322-325) add:

```js
        // Guard for template placeholders: collectRulesFromForm silently drops
        // rules with empty values, so an unfilled template rule would otherwise
        // create a much broader list than the user expects.
        if (page._templatePlaceholderPending) {
            const ruleValueInputs = page.querySelectorAll('#rules-container .rule-value-input');
            let firstEmptyInput = null;
            for (let i = 0; i < ruleValueInputs.length; i++) {
                if (!ruleValueInputs[i].value) {
                    firstEmptyInput = ruleValueInputs[i];
                    break;
                }
            }
            if (firstEmptyInput) {
                SmartLists.showNotification('This template needs a value - fill in the empty rule value first.');
                firstEmptyInput.focus();
                return;
            }
            page._templatePlaceholderPending = false;
        }
```

(Empty BUMPER placeholder values are already blocked by the existing `bumperResult.valid` check at config-lists.js:417-420.)

In `SmartLists.clearForm` (config-lists.js:~832), alongside the existing state resets, add:

```js
        page._templatePlaceholderPending = false;
```

Also reset the picker there so a cleared form doesn't keep a stale selection:

```js
        const templateSelect = page.querySelector('#templateSelect');
        if (templateSelect) {
            templateSelect.value = '';
            const templateDescription = page.querySelector('#templateDescription');
            if (templateDescription) {
                templateDescription.style.display = 'none';
            }
            const useTemplateBtn = page.querySelector('#useTemplateBtn');
            if (useTemplateBtn) {
                useTemplateBtn.disabled = true;
            }
        }
```

- [ ] **Step 6: Syntax checks**

Run: `node --check` on `config-templates.js`, `config-init.js`, `config-lists.js`.
Expected: exit 0 each.
Run: `node dev/validate-templates.js`
Expected: `All 10 templates valid.` (config-templates.js now contains functions too — the validator only reads `SmartLists.TEMPLATES`, unaffected.)

- [ ] **Step 7: Build**

Run: `dotnet build Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj -c Release`
Expected: 0 warnings, 0 errors.

- [ ] **Step 8: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/config.html Jellyfin.Plugin.SmartLists/Configuration/user-playlists.html \
  Jellyfin.Plugin.SmartLists/Configuration/config-templates.js Jellyfin.Plugin.SmartLists/Configuration/config-init.js \
  Jellyfin.Plugin.SmartLists/Configuration/config-lists.js
git commit -m "feat: add template picker with prefill flow and placeholder guard"
```

---

### Task 4: Docs

**Files:**
- Modify: `docs/content/user-guide/configuration.md` (Create List tab section, ~lines 165-203)
- Modify: `docs/content/getting-started/quick-start.md` (before/inside 'Creating Your First List', ~line 31)
- Modify: `docs/content/index.md` (Features list, lines 37-44)
- Modify: `README.md` (Features list, lines 17-24)

**Interfaces:** none (prose).

- [ ] **Step 1: configuration.md — document the picker**

In the Create List tab section, before the existing core-fields description, add:

```markdown
### Start from a Template

At the top of the Create tab you can pick one of the built-in templates — ready-made
configurations like **TV Channel** (round-robin episode interleaving), **Continue
Watching**, **Weekly Jams (ListenBrainz)**, or **Album Roulette**. Selecting a
template and clicking **Use** fills the whole create form with a working setup.
Nothing is created yet: review the settings, adjust them to taste, and click
**Create** as usual.

Some templates need one value from you (for example a title for *Because You
Watched…* or your ListenBrainz feed URL for *Weekly Jams*) — a notification tells
you which rule to fill in, and the form won't save until you do.
```

- [ ] **Step 2: quick-start.md — offer the shortcut**

At the start of 'Creating Your First List' (~line 31), add:

```markdown
!!! tip "Shortcut: start from a template"
    The quickest first list: pick a built-in template from **Start from a template**
    at the top of the Create tab and click **Use** — the form is filled in for you,
    ready to review and create. The steps below build the same thing by hand.
```

- [ ] **Step 3: index.md + README.md feature bullet**

Add to both feature lists (index.md ~line 44, README.md ~line 24), matching the surrounding bullet style:

```markdown
- **Templates**: Start from built-in templates - TV channel round-robin, Continue Watching, external-list imports, album roulette, and more
```

- [ ] **Step 4: Verify docs build**

Run: `cd docs && mkdocs build --strict` (use the venv/requirements.txt setup if mkdocs isn't on PATH: `pip install -r requirements.txt`).
Expected: build completes with zero warnings.

- [ ] **Step 5: Commit**

```bash
git add docs/content/user-guide/configuration.md docs/content/getting-started/quick-start.md docs/content/index.md README.md
git commit -m "docs: document the template picker"
```

---

### Task 5: End-to-end verification

**Files:** none (verification only).

- [ ] **Step 1: Deploy to the local dev Jellyfin**

Use the project's `/verify` skill flow. CRITICAL (from prior-session incident): `./build-local.sh` / docker compose must run from the MAIN checkout's `dev/` directory, never from this worktree's — running compose from a worktree remounts an empty `jellyfin-data`. Either check out `feature/list-templates` in the main checkout and run `cd dev && ./build-local.sh` there, or build in the worktree and copy the built plugin DLL into the main checkout's deployed plugin directory, then `docker restart jellyfin`. Verify the deployed DLL actually contains the change (config-templates.js is an embedded resource — check with the UTF-16 string search trick from memory: stale-msbuild-deploy-verification).

- [ ] **Step 2: Verify checklist (admin page, http://localhost:8096 → Dashboard → Smart Lists)**

1. Create tab shows "Start from a template" above List Type; dropdown has 10 templates in 3 optgroups (TV / Movies / Music); Use disabled until a selection is made; description appears on selection.
2. Select **TV Channel** → Use → form fills: type Playlist, name "TV Channel", media type Episode, one rule PlaybackStatus = Unplayed, sort Round Robin grouped by Series Name, auto-refresh On library changes; success notification shown; More-options fold state consistent (auto-expands only when the template sets advanced chips — e.g. schedules/bumpers templates).
3. Click Create → list is created and materializes (Manage tab shows it; Jellyfin playlist appears).
4. Select **Because You Watched…** → Use → notification with the hint; SimilarTo rule value empty and focused; click Create without filling → blocked with notification + focus; fill a title → Create succeeds.
5. Select **Saturday Morning Cartoons** → Use → bumper section populated (interval 2, Random), bumper Tags rule empty; Create blocked by the existing bumper-incomplete message until filled; visibility schedules present (all four transitions: Enable Sat 06:00, Disable Sat 12:00, Enable Sun 06:00, Disable Sun 12:00) and Advanced fold auto-expanded.
6. **Franchise TV Channel** → Use → sort shows Least Recently Watched Round Robin, group by Collections, within-group Air Date, air-block window 3.
7. **Album Roulette** → Use → no rules (single empty rule row), Random Group Selection enabled/Album/min 5, two sorts (Album Name, Track Number), max playtime 180 — creating works (empty rule row is allowed for non-placeholder templates).
8. Regression: edit an existing list → form populates correctly, Update works; clone an existing list → " (Copy)" name, create works; cancel edit resets.
9. User page (sign in as non-admin → user Smart Lists page): picker present; **Trending Now (Trakt)** hidden when the user cannot create collections; applying **TV Channel** works and creates a list owned by the user.

- [ ] **Step 3: Fix anything found, re-run the failing checklist item, commit fixes**

```bash
git add -A
git commit -m "fix: address e2e findings for template picker"
```

(Skip the commit if nothing was found.)

---

## Self-Review Notes

- Spec coverage: catalog (Task 2), picker both pages (Task 3 Steps 1-2), population refactor (Task 1), placeholders blank+notification+guard (Tasks 2/3), registration (Task 2 Steps 2-4), docs including Examples cross-marking — **deliberately dropped**: marking Examples-page entries as shipped templates adds churn with no user value; quick-start/configuration/index/README cover discovery (deviation from spec, flagged for review). v1 set (Task 2) matches the spec's 10 with one substitution: "Trakt Trending" ships with the verified-supported URL hardcoded (no placeholder needed — spec's open verification item resolved: TraktListProvider.cs:17,252 explicitly supports `trakt.tv/movies/trending`); ExternalList confirmed available on the user page, so no `adminOnly` flags are needed in v1 (field kept in the entry shape for future use).
- "Fresh & Unseen" ships WITHOUT `AllUsers: true` (spec said admin sets AllUsers): `stagePendingUserSelection`'s handling of a dto with no `UserPlaylists` but `AllUsers: true` is unverified, and defaulting to the creating user is the safer, simpler v1 — the description no longer promises all-users. Flagged as a deviation for review; enabling All Users remains a one-checkbox manual step.
- Type consistency: `populateFormFromDto(page, dto|playlist, opts)` signature identical across Tasks 1-3; `page._templatePlaceholderPending` set in Task 3 Step 3, consumed/cleared in Task 3 Step 5; `SmartLists.TEMPLATES` shape defined in Task 2, consumed in Task 3.
- Placeholder scan: two open verification points are explicit implementer checks with fallbacks (escaper helper name near config-core.js:214; `CanCreateCollections` property name), not TBDs.
