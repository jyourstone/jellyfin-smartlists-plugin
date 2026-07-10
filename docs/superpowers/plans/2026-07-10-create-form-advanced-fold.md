# Create/Edit Form "Advanced options" Fold — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collapse the 11 advanced field groups of the create/edit list form into a single "Advanced options" fold with sub-headings and summary chips, so simple lists are created without scrolling past advanced options.

**Architecture:** Static HTML wrapper (header `<button>` + display-toggled body div) cloned from the Manage-tab collapse pattern, in BOTH `config.html` and `user-playlists.html`. New JS functions live in existing modules (`config-lists.js`, `config-init.js`) — no new JS files. No DOM nodes move at runtime; all existing IDs/classes/conditional-visibility machinery untouched. Spec: `docs/superpowers/specs/2026-07-10-create-form-advanced-fold-design.md`.

**Tech Stack:** Jellyfin plugin config pages (embedded HTML/JS resources), vanilla JS (ES5-style, Jellyfin dashboard environment), .NET build via `dev/build-local.sh`.

## Global Constraints

- **No ES6 template literals** in any `config-*.js` — string concatenation only.
- **Never** `is="emby-input"`; **never** `is="emby-collapse"` (not registered on plugin config pages — verified against jellyfin-web source and shipped 12.x bundle).
- Every create-form HTML change applies to **both** `Jellyfin.Plugin.SmartLists/Configuration/config.html` and `Jellyfin.Plugin.SmartLists/Configuration/user-playlists.html`.
- **No new JS files** (avoids `.csproj` + `Plugin.cs` double registration).
- Use only main-bundle-safe classes: `emby-button`, `sectionTitle`, `inputContainer`, `selectContainer`, `checkboxList`, `paperList`, `fieldDescription`, `inputLabel`, `material-icons`. Colors inherited, no hardcoded palette values.
- Expand icons are plain text characters `▶` / `▼` (same as Manage tab, see `config-bulk-actions.js:893-918`).
- There is no test suite. Every task verifies via `cd dev && ./build-local.sh` (must succeed — warnings are errors) plus manual checks against http://localhost:8096. Admin page: Dashboard → Plugins → SmartLists. User page: requires "File Transformation" + "Plugin Pages" plugins (link at top of admin page).
- Line numbers below are as of commit `f6724ee`. Re-locate by the quoted anchors if drifted.

---

### Task 1: config.html restructure + minimal toggle JS

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config.html:150-462` (create-tab form)
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-lists.js` (add `toggleAdvancedOptions` near `clearForm`, line ~660)
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-init.js:1253` (click delegation)

**Interfaces:**
- Consumes: nothing new.
- Produces: DOM ids `#advanced-options-header`, `#advanced-expand-icon`, `#advanced-summary`, `#advanced-options-body`; function `SmartLists.toggleAdvancedOptions(page, force)` — `force` optional boolean (true=expand, false=collapse, undefined=toggle). Tasks 2–5 rely on these exact names.

- [x] **Step 1: Reorder and wrap the advanced blocks in config.html**

Work only inside `<form id="playlistForm">` (lines 47–463). Three operations, all static cut/paste of existing blocks (do NOT edit the blocks' inner content except where shown):

**(a) Move the Users block up.** Cut the whole Users `inputContainer` — starts line 226 (`<label class="inputLabel" for="playlistUser"` inside) and ends line 279 with its closing `</div>` — and paste it directly after the Sort Options `inputContainer` closes (line 150, the `</div>` after `<div id="sorts-container"></div>`).

**(b) Insert the fold header + body wrapper.** Directly after the moved Users block, insert:

```html
                        <button type="button" id="advanced-options-header" class="emby-button"
                            aria-expanded="false" aria-controls="advanced-options-body"
                            style="width: 100%; display: flex; align-items: center; gap: 0.5em; text-align: left; padding: 0.75em 1em; margin-bottom: 1em; background: rgba(128, 128, 128, 0.1); border-radius: 4px;">
                            <span id="advanced-expand-icon" aria-hidden="true">&#9654;</span>
                            <span style="font-weight: 500;">Advanced options</span>
                            <span id="advanced-summary" class="fieldDescription" style="margin-left: auto; margin-top: 0; text-align: right;"></span>
                        </button>
                        <div id="advanced-options-body" style="display: none;">
```

and close the wrapper with `</div>` immediately BEFORE the submit-button block (`<div style="margin-top: 2em;">` containing `#submitBtn`, line 459).

**(c) Reorder blocks inside the wrapper and add sub-headings.** Final order inside `#advanced-options-body` (all blocks are existing HTML moved verbatim unless noted):

```html
<!-- inside #advanced-options-body -->
<h3 class="sectionTitle" style="margin-top: 0.5em;">Limits</h3>
[Max Items inputContainer            — currently lines 282-296]
[Max Playtime inputContainer         — currently lines 298-312, keeps class playlist-only-field]
[Random Group Selection checkboxList — currently lines 187-223]

[#bumper-section                     — currently lines 152-185, ONE inner edit, see below]

<h3 class="sectionTitle" style="margin-top: 1.5em;">Automation</h3>
[Enable list checkboxList            — currently lines 330-353]
[Auto-refresh selectContainer        — currently lines 355-371]
[Refresh Schedules inputContainer    — currently lines 373-386]
[Visibility Schedules inputContainer — currently lines 388-400]

<h3 class="sectionTitle playlist-only-field" style="margin-top: 1.5em;">Sharing</h3>
[#publicCheckboxContainer            — currently lines 314-328]

<h3 class="sectionTitle" style="margin-top: 1.5em;">Presentation</h3>
[Custom Images inputContainer        — currently lines 402-418]
[Metadata inputContainer             — currently lines 420-457, ONE inner edit, see below]
```

Inner edit 1 — inside `#bumper-section`, replace its label line

```html
                            <label class="inputLabel">Bumpers (optional)</label>
```

with a sub-heading matching the others (the h3 lives INSIDE `#bumper-section` so the existing id-targeted collection-mode hiding covers it):

```html
                            <h3 class="sectionTitle" style="margin-top: 1.5em;">Bumpers</h3>
```

Inner edit 2 — inside the Metadata block, demote the existing `<h3 class="sectionTitle" ...>Metadata ...</h3>` (lines 421-429) to a label so it doesn't compete with the new sub-headings (keep the doc link span exactly as is):

```html
                            <label class="inputLabel" style="display: flex; align-items: center;">
                                Metadata
                                <a href="https://jellyfin-smartlists-plugin.dinsten.se/user-guide/configuration/#metadata"
                                    target="_blank" rel="noopener noreferrer" title="Documentation"
                                    style="margin-left: 0.5em; text-decoration: none; color: inherit; display: inline-flex; align-items: center;">
                                    <span class="material-icons" aria-hidden="true"
                                        style="font-size: 1.1em; line-height: 0;">info_outline</span>
                                </a>
                            </label>
```

Notes:
- The `Sharing` h3 carries `playlist-only-field` so `handleListTypeChange` hides it together with the public checkbox in Collection mode (and the `pageshow` cleanup already whitelists that class).
- Do NOT add a class to the Bumpers h3 — it is inside `#bumper-section`, which `updateBumperSectionVisibility` already hides for Collections.
- All ids, classes, `style` attributes, labels, descriptions, and doc links inside moved blocks stay byte-identical (except the two inner edits above).

- [x] **Step 2: Add `SmartLists.toggleAdvancedOptions` to config-lists.js**

Insert directly ABOVE `SmartLists.clearForm = function (page) {` (line 664):

```javascript
    SmartLists.toggleAdvancedOptions = function (page, force) {
        const body = page.querySelector('#advanced-options-body');
        if (!body) {
            return;
        }
        const expand = force !== undefined ? force : body.style.display === 'none';
        body.style.display = expand ? 'block' : 'none';
        const icon = page.querySelector('#advanced-expand-icon');
        if (icon) {
            icon.textContent = expand ? '▼' : '▶';
        }
        const header = page.querySelector('#advanced-options-header');
        if (header) {
            header.setAttribute('aria-expanded', expand ? 'true' : 'false');
        }
    };
```

- [x] **Step 3: Wire the header click via the existing page-level delegation**

In `config-init.js`, inside the big click listener, directly BEFORE the `.playlist-header` block (line 1253, anchor: `if (target.closest('.playlist-header')) {`), insert:

```javascript
            if (target.closest('#advanced-options-header')) {
                if (SmartLists.toggleAdvancedOptions) {
                    SmartLists.toggleAdvancedOptions(page);
                }
                return;
            }
```

- [ ] **Step 4: Build and verify on the admin page**

Run: `cd /Users/johan.yourstone/Git/jellyfin-smartplaylist-plugin/dev && ./build-local.sh`
Expected: build succeeds, Jellyfin container restarts.

Manual checks at http://localhost:8096 → Dashboard → Plugins → SmartLists (hard-refresh; embedded resources cache aggressively — use devtools "Disable cache" or bump-refresh):
1. Create tab shows: List Type, Name, Media Types, Rules, Sort Options, Users, collapsed "▶ Advanced options" bar, Create/Clear buttons. Nothing else.
2. Click the bar → expands, icon flips to ▼; sub-headings read Limits / Bumpers / Automation / Sharing / Presentation; all advanced fields present.
3. Switch List Type to Collection with fold open → Bumpers section, Sharing h3 + public checkbox, Max Playtime disappear; switch back → they return.
4. Keyboard: Tab reaches the bar, Enter toggles it.
5. Create a simple playlist (name + media type + one rule) without touching the fold → saves successfully.

- [x] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/config.html Jellyfin.Plugin.SmartLists/Configuration/config-lists.js Jellyfin.Plugin.SmartLists/Configuration/config-init.js
git commit -m "Add collapsed Advanced options fold to admin create form"
```

---

### Task 2: Mirror the restructure in user-playlists.html

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/user-playlists.html:135-396` (create form)

**Interfaces:**
- Consumes: nothing (pure HTML; JS from Task 1 is shared and picks up the new ids automatically).
- Produces: identical fold markup/ids on the user page.

- [x] **Step 1: Apply the same restructure**

Same three operations as Task 1 Step 1, with these user-page differences:
- There is no visible Users block. The hidden `<input type="hidden" id="playlistUser" value="">` (line 213) must stay OUTSIDE the fold — leave it directly after the Sort Options container (line 135 area).
- Anchors on this page: `#bumper-section` line 138, Random Group checkbox line ~173, Max Items label line 216, `#publicCheckboxContainer` line 247, Enable list line ~261, Auto-refresh line ~285, `#schedules-container` line 316, `#visibility-schedules-container` line 331, `#custom-images-container` line 345, Metadata h3 line ~353, submit block lines 393-396.
- Fold header/body markup byte-identical to Task 1 Step 1(b). Sub-heading order and the two inner edits (Bumpers label → h3, Metadata h3 → label) byte-identical to Task 1 Step 1(c).

- [ ] **Step 2: Build and verify on the user page**

Run: `cd /Users/johan.yourstone/Git/jellyfin-smartplaylist-plugin/dev && ./build-local.sh`
Expected: build succeeds.

Manual checks on the user config page (link "Open User Config Page" at top of admin page):
1. Same collapsed-by-default fold; toggle works (shared delegation listener).
2. Create a simple playlist as a non-admin user without opening the fold → saves.
3. List Type switch gating works inside the fold (user page still has both list types).

- [x] **Step 3: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/user-playlists.html
git commit -m "Mirror Advanced options fold on user config page"
```

---

### Task 3: Create-mode summary chips (effective defaults)

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-lists.js` (add `renderAdvancedSummaryFromForm` next to `toggleAdvancedOptions`)
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-init.js:462,493` (tail of `applyFormDefaults` and `applyFallbackDefaults`)

**Interfaces:**
- Consumes: `#advanced-summary` (Task 1), `SmartLists.getElementValue` (existing helper).
- Produces: `SmartLists.renderAdvancedSummaryFromForm(page)` — reads current form values, writes the summary line; no-ops in edit mode (`page._editMode`). Task 4 relies on the `page._editMode` guard so edit chips are not overwritten by late-arriving defaults.

- [x] **Step 1: Add `renderAdvancedSummaryFromForm` to config-lists.js**

Insert directly AFTER the `toggleAdvancedOptions` function from Task 1:

```javascript
    SmartLists.renderAdvancedSummaryFromForm = function (page) {
        // Edit mode owns the summary via syncAdvancedSection; don't let
        // late-arriving async defaults overwrite its chips.
        if (page._editMode) {
            return;
        }
        const summaryEl = page.querySelector('#advanced-summary');
        if (!summaryEl) {
            return;
        }
        const parts = [];
        const maxItems = parseInt(SmartLists.getElementValue(page, '#playlistMaxItems', '0'), 10);
        if (!isNaN(maxItems)) {
            parts.push(maxItems > 0 ? maxItems + ' items max' : 'unlimited items');
        }
        const refreshLabels = {
            'Never': 'no auto-refresh',
            'OnLibraryChanges': 'refresh on library changes',
            'OnAllChanges': 'refresh on all changes'
        };
        const refreshValue = SmartLists.getElementValue(page, '#autoRefreshMode', '');
        if (refreshValue) {
            parts.push(refreshLabels[refreshValue] || refreshValue);
        }
        summaryEl.textContent = parts.join(' · ');
    };
```

(Verify the option values first: `grep -n "OnLibraryChanges\|OnAllChanges\|'Never'" Jellyfin.Plugin.SmartLists/Configuration/config-init.js` — the map keys must match the values used when `#autoRefreshMode` options are populated. Add any missing value to `refreshLabels`.)

- [x] **Step 2: Invoke it whenever defaults are applied**

In `config-init.js`, add as the LAST line inside `SmartLists.applyFormDefaults` (before its closing `};` at line 462) and inside `SmartLists.applyFallbackDefaults` (before its closing `};` at line 493):

```javascript
        if (SmartLists.renderAdvancedSummaryFromForm) {
            SmartLists.renderAdvancedSummaryFromForm(page);
        }
```

- [ ] **Step 3: Build and verify**

Run: `cd /Users/johan.yourstone/Git/jellyfin-smartplaylist-plugin/dev && ./build-local.sh`

Manual checks:
1. Admin create tab, fold collapsed: header right side reads e.g. `500 items max · refresh on library changes` (matches Settings-tab defaults).
2. Change Settings default Max Items → save → back to Create tab (Clear Form) → chip updates.
3. User page shows the fallback defaults chip (`500 items max · refresh on library changes`).

- [x] **Step 4: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/config-lists.js Jellyfin.Plugin.SmartLists/Configuration/config-init.js
git commit -m "Show effective defaults on collapsed Advanced options header"
```

---

### Task 4: Edit/clone chips + auto-expand + collapse on reset

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-lists.js` (add `syncAdvancedSection`; wire `clearForm:733`, `editPlaylist:966-969`, `clonePlaylist:1191`)

**Interfaces:**
- Consumes: `toggleAdvancedOptions` (Task 1), `renderAdvancedSummaryFromForm`'s `_editMode` guard (Task 3), `SmartLists.loadExistingImages(page, id)` which returns a Promise (`config-images.js:545`).
- Produces: `SmartLists.syncAdvancedSection(page, playlist)` — renders edit chips from the DTO + DOM image rows, expands the fold when any chip fires; idempotent, expand-only.

- [x] **Step 1: Add `syncAdvancedSection` to config-lists.js**

Insert directly AFTER `renderAdvancedSummaryFromForm`:

```javascript
    SmartLists.syncAdvancedSection = function (page, playlist) {
        const summaryEl = page.querySelector('#advanced-summary');
        if (!summaryEl || !playlist) {
            return;
        }
        const chips = [];
        if (playlist.Bumpers && playlist.Bumpers.MediaTypes && playlist.Bumpers.MediaTypes.length > 0) {
            chips.push('Bumpers');
        }
        const scheduleCount = (playlist.Schedules || []).length + (playlist.VisibilitySchedules || []).length;
        if (scheduleCount > 0) {
            chips.push(scheduleCount + (scheduleCount === 1 ? ' schedule' : ' schedules'));
        }
        if (playlist.RandomGroupSelection && playlist.RandomGroupSelection.Enabled) {
            chips.push('Random groups');
        }
        if (playlist.MaxPlayTimeMinutes > 0) {
            chips.push('Playtime limit');
        }
        if (playlist.Enabled === false) {
            chips.push('Disabled');
        }
        if (playlist.SortTitle || playlist.Overview || playlist.Favorite === true || playlist.Favorite === false ||
            (playlist.Tags && playlist.Tags.length > 0)) {
            chips.push('Metadata');
        }
        // Images arrive via a separate async endpoint; count what's in the DOM.
        const imageRows = page.querySelectorAll('#custom-images-container [id^="image-row-"]').length;
        if (imageRows > 0) {
            chips.push(imageRows === 1 ? '1 image' : imageRows + ' images');
        }
        summaryEl.textContent = chips.join(' · ');
        if (chips.length > 0) {
            SmartLists.toggleAdvancedOptions(page, true);
        }
    };
```

(Signals deliberately exclude async-default fields — MaxItems, AutoRefresh, IsPublic — per the spec: their non-defaultness is ambiguous and they round-trip regardless of fold state.)

- [x] **Step 2: Collapse the fold on every form reset**

In `SmartLists.clearForm`, directly before the closing `};` (line 733, after the `initCustomImagesContainer` block):

```javascript
        // Reset the Advanced options fold to collapsed
        if (SmartLists.toggleAdvancedOptions) {
            SmartLists.toggleAdvancedOptions(page, false);
        }
```

(The default chips re-render via the async `applyFormDefaults`/`applyFallbackDefaults` calls already inside `clearForm`; `cancelEdit` runs `setPageEditState(false)` before `clearForm`, so the `_editMode` guard has already been lifted.)

- [x] **Step 3: Wire editPlaylist**

In `SmartLists.editPlaylist`, replace the existing images call (lines 966-969):

```javascript
                // Load existing images for this playlist
                if (SmartLists.loadExistingImages) {
                    SmartLists.loadExistingImages(page, playlistId);
                }
```

with:

```javascript
                // Load existing images for this playlist, then re-sync the
                // Advanced fold so an images-only list still expands.
                if (SmartLists.loadExistingImages) {
                    SmartLists.loadExistingImages(page, playlistId).then(function () {
                        SmartLists.syncAdvancedSection(page, playlist);
                    });
                }

                // Chips + auto-expand from the loaded list (images re-sync above)
                SmartLists.syncAdvancedSection(page, playlist);
```

- [x] **Step 4: Wire clonePlaylist**

In `SmartLists.clonePlaylist`, directly after the metadata tags population (line 1191-1192, anchor `SmartLists.initMetadataTagsInput(page, playlist.Tags || []);` and its closing `}`), insert:

```javascript
                // Chips + auto-expand for cloned advanced config
                SmartLists.syncAdvancedSection(page, playlist);
```

(Clone does not copy images, so no promise chaining needed here.)

- [ ] **Step 5: Build and verify**

Run: `cd /Users/johan.yourstone/Git/jellyfin-smartplaylist-plugin/dev && ./build-local.sh`

Manual checks (admin page; create test lists first if needed):
1. Edit a list with bumpers + a refresh schedule → form opens with fold EXPANDED, header reads `Bumpers · 1 schedule`.
2. Edit a simple list (defaults only) → fold stays COLLAPSED, header shows default chips.
3. Edit a list whose only extra is a custom image → fold expands when the image loads, chip `1 image`.
4. Clone the bumper list → fold expanded, chips shown, submit still says Create.
5. Cancel Edit → back on Manage tab; return to Create tab → fold collapsed, default chips restored.
6. Update a configured list successfully → form clears, fold collapsed.
7. Repeat check 1 on the user page.

- [x] **Step 6: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/config-lists.js
git commit -m "Auto-expand Advanced options with summary chips in edit and clone"
```

---

### Task 5: Robustness guards — hidden-invalid expand + bfcache icon re-sync

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-init.js:1287-1295` (form listeners), `config-init.js:2539-2548` (pageshow handler)

**Interfaces:**
- Consumes: `toggleAdvancedOptions` (Task 1), `#advanced-options-body`, `#advanced-expand-icon`.
- Produces: nothing new — behavior only.

- [x] **Step 1: Expand the fold when native validation trips inside it**

In `config-init.js`, directly after the existing `playlistForm.addEventListener('submit', ...)` block (lines 1287-1295), still inside the `if (playlistForm) {` guard:

```javascript
            // If native validation flags a control hidden inside the collapsed
            // Advanced fold, expand the fold so the browser can focus it.
            // ('invalid' doesn't bubble — use capture.)
            playlistForm.addEventListener('invalid', function (e) {
                const body = page.querySelector('#advanced-options-body');
                if (body && body.style.display === 'none' && body.contains(e.target)) {
                    SmartLists.toggleAdvancedOptions(page, true);
                }
            }, { capture: true, signal: pageSignal });
```

(Match the options argument style used elsewhere in this listener block: if the file passes `SmartLists.getEventListenerOptions(pageSignal)`, extend with capture via `{ capture: true, signal: pageSignal }` only if `getEventListenerOptions` returns `{ signal: pageSignal }`; otherwise mirror whatever that helper returns plus `capture: true`. Check `getEventListenerOptions`' definition first: `grep -n "getEventListenerOptions = " Jellyfin.Plugin.SmartLists/Configuration/*.js`.)

- [x] **Step 2: Re-sync the expand icon on pageshow**

In the `pageshow` handler, inside the `if (page._pageInitialized) {` block (line 2542, next to `SmartLists.handleListTypeChange(page);`), add:

```javascript
                // bfcache can restore the fold body/icon out of sync
                const advBody = page.querySelector('#advanced-options-body');
                if (advBody && SmartLists.toggleAdvancedOptions) {
                    SmartLists.toggleAdvancedOptions(page, advBody.style.display !== 'none');
                }
```

- [ ] **Step 3: Build and verify**

Run: `cd /Users/johan.yourstone/Git/jellyfin-smartplaylist-plugin/dev && ./build-local.sh`

Manual checks:
1. Temporarily add `required` to `#playlistMaxItems` via devtools, clear its value, fold collapsed, hit Create → fold expands and browser focuses the field (no console error "not focusable"). Remove the attribute after.
2. Expand fold → navigate to user page → browser Back → icon matches body state.

- [x] **Step 4: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/config-init.js
git commit -m "Guard Advanced fold against hidden invalid controls and bfcache desync"
```

---

### Task 6: Docs + future-field rules

**Files:**
- Modify: `docs/content/user-guide/configuration.md` (verify exact path first: `ls docs/content/user-guide/`)
- Modify: `CLAUDE.md` (repo root, "When Making Changes" section)

**Interfaces:** none.

- [ ] **Step 1: Update user docs**

In the configuration page of the mkdocs docs, where the create-form fields are described, add a short note near the top of the field list (adapt heading level to the page):

```markdown
!!! note "Advanced options"
    Fields beyond List Type, Name, Media Types, Rules, Sort Options, and Users live
    in a collapsed **Advanced options** section on the create/edit form. The collapsed
    header always shows a summary of what applies (e.g. `500 items max · refresh on
    library changes`, or `Bumpers · 2 schedules` when editing a configured list), and
    the section expands automatically when you edit a list that uses advanced features.
```

Also reorder/regroup the field documentation if it mirrors the old on-screen order (check whether the page lists fields in form order; if it does, note the new grouping: Limits / Bumpers / Automation / Sharing / Presentation).

- [ ] **Step 2: Add future-field rules to CLAUDE.md**

In root `CLAUDE.md`, under "When Making Changes", add:

```markdown
- **Create-form fields and the Advanced options fold**: required inputs must never
  be placed inside `#advanced-options-body` (collapsed `display:none` hides native
  validation). New advanced fields go under the matching sub-heading inside the fold
  (Limits / Bumpers / Automation / Sharing / Presentation); new core fields go above
  it. If a new advanced field has an unambiguous non-default state, add a signal for
  it in `syncAdvancedSection` (config-lists.js) so edit mode surfaces it as a chip
  and auto-expands.
```

- [ ] **Step 3: Verify docs build (optional, if mkdocs installed) and commit**

```bash
git add docs/content CLAUDE.md
git commit -m "Document Advanced options fold and future-field rules"
```

---

## Final verification sweep (after all tasks)

1. `cd /Users/johan.yourstone/Git/jellyfin-smartplaylist-plugin/dev && ./build-local.sh` — clean build.
2. Full spec checklist (spec §Verification), both pages: simple create untouched fold; edit configured list auto-expands with chips; images-only edit expands late; clone matches; cancel/update re-collapse; Playlist↔Collection gating inside open fold; back-nav icon sync; keyboard toggle.
3. `git log --oneline main..` — 6 commits, one per task.
