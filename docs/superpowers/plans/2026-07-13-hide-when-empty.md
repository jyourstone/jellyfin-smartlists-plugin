# Hide When Empty Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in "Hide when empty" option so a smart list's Jellyfin playlist/collection is deleted/never created while its rules match zero items, and recreated automatically when items match again.

**Architecture:** One new bool `HideWhenEmpty` on the shared `SmartListDto` base class, a short-circuit branch in each service's refresh flow (the single choke point where the final item list and the existing Jellyfin entity are both known), and standard form wiring in the shared config JS + both HTML pages. Spec: `docs/superpowers/specs/2026-07-13-hide-when-empty-design.md`.

**Tech Stack:** C# (.NET 9/10 multi-target, Jellyfin plugin), vanilla JS (Jellyfin admin UI conventions), mkdocs.

## Global Constraints

- Build treats all warnings as errors (`AnalysisMode=Recommended`); CA1305 means use `CultureInfo.InvariantCulture` or invariant-safe APIs where flagged.
- There is NO test suite. Per-task verification = `cd dev && ./build-local.sh` compiles clean (defaults to net10.0 target). Final task verifies end-to-end against local Jellyfin at <http://localhost:8096>.
- Frontend: **no ES6 template literals** (string concatenation only). Checkboxes use the existing `is="emby-checkbox"` + `data-embycheckbox="true" class="emby-checkbox"` pattern (the `is=` ban applies to `emby-input` only).
- UI changes go in **both** `config.html` (admin) and `user-playlists.html` (user); the JS modules are shared.
- Line numbers below were captured on branch `feature/hide-when-empty` at commit `24a2297`. Treat them as anchors: locate the quoted code, don't trust raw numbers blindly.
- Work on branch `feature/hide-when-empty`. Commit after each task; commit messages end with:

```text
Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RBFLcyWKuktDSu9acy68zg
```

---

### Task 1: DTO property + mapper

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Core/Models/SmartListDto.cs` (~line 75)
- Modify: `Jellyfin.Plugin.SmartLists/Utilities/DtoMapper.cs` (~lines 44 and 118)

**Interfaces:**
- Consumes: nothing.
- Produces: `public bool HideWhenEmpty { get; set; }` on `SmartListDto` (base class, so both `SmartPlaylistDto` and `SmartCollectionDto` inherit it). Tasks 2, 3, 5 rely on this exact name.

- [ ] **Step 1: Add the property to SmartListDto**

In `Core/Models/SmartListDto.cs`, find:

```csharp
        public bool IncludeExtras { get; set; } = false;
```

Insert directly after it:

```csharp
        /// <summary>
        /// When true, the Jellyfin playlist/collection is not created (and an existing one is
        /// removed) while the list's rules match zero items. It is recreated automatically
        /// once items match again. The smart list configuration itself is never deleted.
        /// </summary>
        public bool HideWhenEmpty { get; set; } = false;
```

- [ ] **Step 2: Thread through DtoMapper (both mapping sites)**

In `Utilities/DtoMapper.cs` there are two object initializers (`ToPlaylistDto` → `new SmartPlaylistDto {...}` and `ToCollectionDto` → `new SmartCollectionDto {...}`), each containing:

```csharp
                IncludeExtras = source.IncludeExtras,
```

After **each** of the two occurrences, add:

```csharp
                HideWhenEmpty = source.HideWhenEmpty,
```

- [ ] **Step 3: Build**

Run: `cd dev && ./build-local.sh`
Expected: build succeeds, container restarts. (Warnings-as-errors means any CA violation fails here.)

- [ ] **Step 4: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Core/Models/SmartListDto.cs Jellyfin.Plugin.SmartLists/Utilities/DtoMapper.cs
git commit -m "Add HideWhenEmpty property to SmartListDto"
```

---

### Task 2: PlaylistService hide-when-empty branch

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Services/Playlists/PlaylistService.cs` (~line 262, inside `ProcessPlaylistRefreshAsync`)

**Interfaces:**
- Consumes: `dto.HideWhenEmpty` from Task 1. In-scope locals at the insertion point: `dto`, `user`, `logger`, `saveCallback`, `newLinkedChildren`, `existingPlaylist`, `smartPlaylistName`.
- Produces: refresh returns `(true, "...hidden...", string.Empty)` when the flag is set and the list is empty.

**Context:** `ProcessPlaylistRefreshAsync` computes `newLinkedChildren` (final items, bumpers already handled — bumper weaving only runs when there is at least one main item, so `newLinkedChildren.Length == 0` is exactly "zero main items"), then looks up `existingPlaylist` by the per-user `JellyfinPlaylistId`, then branches update-vs-create. The hide branch goes between the lookup and that branch. This method runs once per user for multi-user playlists (each user has their own mapping in `dto.UserPlaylists`), so per-user hiding falls out naturally. The wrapper `ProcessPlaylistRefreshWithCachedMediaAsync` calls `saveCallback` again on success, but the branch saves explicitly too — mirroring the create path — so the cleared ID persists even on the direct `RefreshAsync` path where the wrapper isn't involved.

- [ ] **Step 1: Insert the hide branch**

Find (~line 261):

```csharp
                // Now that we've found the existing playlist (or not), apply the new naming format
                var smartPlaylistName = NameFormatter.FormatPlaylistName(dto.Name);

                if (existingPlaylist != null)
```

Insert between `var smartPlaylistName = ...;` and `if (existingPlaylist != null)`:

```csharp
                // Hide when empty: don't keep a Jellyfin playlist around while no items match
                if (dto.HideWhenEmpty && newLinkedChildren.Length == 0)
                {
                    if (existingPlaylist != null)
                    {
                        logger.LogInformation("Smart playlist '{PlaylistName}' matched no items - deleting Jellyfin playlist (hide when empty)", dto.Name);
                        _libraryManager.DeleteItem(existingPlaylist, new DeleteOptions { DeleteFileLocation = true }, true);
                    }

                    // Clear the stored Jellyfin playlist ID so a later refresh with items recreates it
                    if (dto.UserPlaylists != null && dto.UserPlaylists.Count > 0)
                    {
                        var emptyUserMapping = dto.UserPlaylists.FirstOrDefault(m => string.Equals(m.UserId, user.Id.ToString("N"), StringComparison.OrdinalIgnoreCase));
                        if (emptyUserMapping != null)
                        {
                            emptyUserMapping.JellyfinPlaylistId = null;
                        }

                        // Update backwards compatibility field (first user's playlist)
                        dto.JellyfinPlaylistId = dto.UserPlaylists[0].JellyfinPlaylistId;
                    }
                    else
                    {
                        dto.JellyfinPlaylistId = null;
                    }

                    if (saveCallback != null)
                    {
                        await saveCallback(dto);
                    }

                    return (true, $"Playlist '{smartPlaylistName}' has no items - hidden (hide when empty)", string.Empty);
                }
```

Notes for the implementer:
- `DeleteOptions` is already used in this file (`DeleteAsync`, ~line 498) — no new using needed.
- If `JellyfinPlaylistId` on `UserPlaylistMapping` or the DTO is non-nullable `string`, use `string.Empty` instead of `null` and match how `DisableAsync`/`DeleteAsync` treat "no id" (`string.IsNullOrEmpty` checks guard all lookups, so either works — prefer whatever compiles without nullability warnings).

- [ ] **Step 2: Build**

Run: `cd dev && ./build-local.sh`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Services/Playlists/PlaylistService.cs
git commit -m "Skip/delete Jellyfin playlist when HideWhenEmpty and no items match"
```

---

### Task 3: CollectionService hide-when-empty branch

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Services/Collections/CollectionService.cs` (~line 329, inside the collection refresh core method)

**Interfaces:**
- Consumes: `dto.HideWhenEmpty` from Task 1. In-scope locals at the insertion point: `dto`, `_logger`, `newLinkedChildren`, `existingCollectionItem`.
- Produces: refresh returns `(true, "...hidden...", string.Empty)` when the flag is set and the collection is empty.

**Context:** The collection refresh core (called by `ProcessPlaylistRefreshWithCachedMediaAsync`, which invokes `saveCallback` on success — that persists the cleared ID; the inner method has no saveCallback parameter) computes `newLinkedChildren`, looks up `existingCollectionItem` by `dto.JellyfinCollectionId`, then branches update-vs-create.

- [ ] **Step 1: Insert the hide branch**

Find (~line 328):

```csharp
                var collectionName = dto.Name;

                if (existingCollectionItem != null && existingCollectionItem.GetBaseItemKind() == BaseItemKind.BoxSet)
```

Insert **before** `var collectionName = dto.Name;`:

```csharp
                // Hide when empty: don't keep a Jellyfin collection around while no items match
                if (dto.HideWhenEmpty && newLinkedChildren.Length == 0)
                {
                    if (existingCollectionItem != null)
                    {
                        _logger.LogInformation("Smart collection '{CollectionName}' matched no items - deleting Jellyfin collection (hide when empty)", dto.Name);
                        _libraryManager.DeleteItem(existingCollectionItem, new DeleteOptions { DeleteFileLocation = true }, true);
                    }

                    // Clear the stored Jellyfin collection ID so a later refresh with items recreates it
                    dto.JellyfinCollectionId = null;

                    return (true, $"Collection '{dto.Name}' has no items - hidden (hide when empty)", string.Empty);
                }
```

Same nullability note as Task 2: if `JellyfinCollectionId` is non-nullable, use `string.Empty` (all lookups guard with `string.IsNullOrEmpty`).
`DeleteOptions` is already used in this file (`DeleteAsync`, ~line 434).

- [ ] **Step 2: Build**

Run: `cd dev && ./build-local.sh`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Services/Collections/CollectionService.cs
git commit -m "Skip/delete Jellyfin collection when HideWhenEmpty and no items match"
```

---

### Task 4: Checkbox in both HTML pages

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config.html` (Presentation heading at ~line 411)
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/user-playlists.html` (Presentation heading at ~line 345)

**Interfaces:**
- Consumes: nothing.
- Produces: checkbox with id `#playlistHideWhenEmpty` on both pages (Task 5's JS reads/writes this exact id).

- [ ] **Step 1: Insert the checkbox block in config.html**

Find in `config.html`:

```html
                            <h3 class="sectionTitle" style="margin-top: 1.5em;">Presentation</h3>
```

Insert directly after it (before the Custom Images `inputContainer`):

```html
                            <div class="checkboxList paperList"
                                style="padding: 0.5em 1em; margin-bottom: 1em; margin-top: 0.5em;">
                                <label class="emby-checkbox-label">
                                    <input type="checkbox" is="emby-checkbox" id="playlistHideWhenEmpty"
                                        data-embycheckbox="true" class="emby-checkbox">
                                    <span class="checkboxLabel">Hide when empty</span>
                                    <span class="checkboxOutline">
                                        <span class="material-icons checkboxIcon checkboxIcon-checked check"
                                            aria-hidden="true"></span>
                                        <span class="material-icons checkboxIcon checkboxIcon-unchecked"
                                            aria-hidden="true"></span>
                                    </span>
                                </label>
                                <div class="fieldDescription">Don't create the playlist or collection while it has no matching items. If a refresh finds no items, the existing Jellyfin playlist/collection is removed and recreated automatically once items match again. The smart list configuration is kept.</div>
                            </div>
```

(Indentation: match the sibling blocks in each file — config.html's Presentation section content sits one level shallower than the `<h3>`; copy whatever the adjacent Custom Images container uses.)

- [ ] **Step 2: Insert the identical block in user-playlists.html**

Find the same `<h3 ...>Presentation</h3>` line in `user-playlists.html` (~line 345) and insert the identical block directly after it.

- [ ] **Step 3: Commit**

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/config.html Jellyfin.Plugin.SmartLists/Configuration/user-playlists.html
git commit -m "Add Hide when empty checkbox to Presentation section on both pages"
```

---

### Task 5: JS wiring (save, populate, chips, display, resets)

**Files:**
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-lists.js` (~lines 334, 429, 733, 903, 1160, 2125)
- Modify: `Jellyfin.Plugin.SmartLists/Configuration/config-init.js` (~lines 426, 476)

No new JS files → no `.csproj`/`Plugin.cs` registration. Mirror the `IncludeExtras` wiring exactly; every site below is adjacent to an existing `IncludeExtras`/`playlistIncludeExtras` line found by grep.

**Interfaces:**
- Consumes: `#playlistHideWhenEmpty` (Task 4), `HideWhenEmpty` DTO property (Task 1; both admin and user pages POST/PUT the full DTO wholesale, so no controller changes are needed).
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Read the checkbox in the save path** (config-lists.js ~line 334)

Find:

```js
            const includeExtras = SmartLists.getElementChecked(page, '#playlistIncludeExtras', false);
```

Add after it:

```js
            const hideWhenEmpty = SmartLists.getElementChecked(page, '#playlistHideWhenEmpty', false);
```

- [ ] **Step 2: Send it in the DTO** (config-lists.js ~line 429)

Find inside the `playlistDto` object literal:

```js
                IncludeExtras: includeExtras,
```

Add after it:

```js
                HideWhenEmpty: hideWhenEmpty,
```

- [ ] **Step 3: Populate on edit** (config-lists.js ~line 903)

Find:

```js
                SmartLists.setElementChecked(page, '#playlistIncludeExtras', playlist.IncludeExtras || false);
```

Add after it:

```js
                SmartLists.setElementChecked(page, '#playlistHideWhenEmpty', playlist.HideWhenEmpty || false);
```

- [ ] **Step 4: Populate on clone** (config-lists.js ~line 1160)

Same pattern: find the second `setElementChecked(page, '#playlistIncludeExtras', playlist.IncludeExtras || false);` occurrence and add the same `HideWhenEmpty` line after it.

- [ ] **Step 5: Chip + auto-expand signal** (config-lists.js, `SmartLists.syncAdvancedSection`, ~line 733)

Find:

```js
        if (playlist.Enabled === false) {
            chips.push('Disabled');
        }
```

Add after it:

```js
        if (playlist.HideWhenEmpty) {
            chips.push('Hide when empty');
        }
```

(Checked is an unambiguous non-default, so it belongs in the feature-signal chips that also auto-expand the fold — that happens automatically via the existing `if (chips.length > 0)` at the end of the function.)

- [ ] **Step 6: Display row in the list-details table** (config-lists.js ~line 2125)

Find the conditional `Include Extras` row:

```js
            (playlist.IncludeExtras ?
                '<tr style="border-bottom: 1px solid var(--jf-palette-divider);">' +
                '<td style="padding: 0.5em 0.75em; font-weight: bold; opacity: 0.8; width: 40%; border-right: 1px solid var(--jf-palette-divider);">Include Extras</td>' +
                '<td style="padding: 0.5em 0.75em; ">Yes</td>' +
                '</tr>' :
                ''
            ) +
```

Add directly after it:

```js
            (playlist.HideWhenEmpty ?
                '<tr style="border-bottom: 1px solid var(--jf-palette-divider);">' +
                '<td style="padding: 0.5em 0.75em; font-weight: bold; opacity: 0.8; width: 40%; border-right: 1px solid var(--jf-palette-divider);">Hide When Empty</td>' +
                '<td style="padding: 0.5em 0.75em; ">Yes</td>' +
                '</tr>' :
                ''
            ) +
```

- [ ] **Step 7: Form resets** (config-init.js ~lines 426 and 476)

There are two reset sites, each containing:

```js
        SmartLists.setElementChecked(page, '#playlistIncludeExtras', false);
```

After **each** of the two occurrences, add:

```js
        SmartLists.setElementChecked(page, '#playlistHideWhenEmpty', false);
```

- [ ] **Step 8: Build (embeds the JS/HTML as resources) and commit**

Run: `cd dev && ./build-local.sh`
Expected: build succeeds.

```bash
git add Jellyfin.Plugin.SmartLists/Configuration/config-lists.js Jellyfin.Plugin.SmartLists/Configuration/config-init.js
git commit -m "Wire Hide when empty through create/edit/display JS"
```

---

### Task 6: Docs

**Files:**
- Modify: `docs/content/user-guide/configuration.md`

**Interfaces:** none.

- [ ] **Step 1: Add a section**

In `docs/content/user-guide/configuration.md`, insert a new section immediately **before** the `## Custom List Naming` heading (~line 281), parallel to the existing `## Enable List` section:

```markdown
## Hide When Empty

By default, a smart list's Jellyfin playlist or collection exists even when its rules match no items. Enable **Hide when empty** (in the **Presentation** group under **More options** when creating or editing a list) to change that:

- If a refresh finds **no matching items**, the Jellyfin playlist/collection is removed (or never created in the first place).
- As soon as a later refresh finds matching items again, it is recreated automatically — including any custom images and metadata you configured.
- The smart list configuration itself is never deleted; it stays visible in the Smart Lists interface.

This is useful for seasonal or rotating lists (e.g. "Halloween movies" driven by a schedule or an external list) that would otherwise linger as empty entries in your library. For multi-user playlists, hiding applies per user: a user with no matching items has their playlist hidden while other users keep theirs.
```

- [ ] **Step 2: Check the Create List section for an options list**

Skim the `### 1. Create List` section (~lines 142–181). If it enumerates the "More options" groups or individual advanced options, add a one-line bullet for **Hide when empty** in the Presentation group following the existing bullet style. If it doesn't enumerate options, skip this step.

- [ ] **Step 3: Commit**

```bash
git add docs/content/user-guide/configuration.md
git commit -m "Document Hide when empty option"
```

---

### Task 7: End-to-end verification

**Files:** none (verification only).

- [ ] **Step 1: Build and deploy both ABIs**

```bash
cd dev && ./build-local.sh
cd dev && JELLYFIN_ABI=10.11.0 ./build-local.sh   # compile check for net9.0; redeploys 10.11 container config
cd dev && ./build-local.sh                        # leave the 12.x build running for the manual pass
```

Expected: both builds succeed (warnings-as-errors clean).

- [ ] **Step 2: Manual scenario pass against <http://localhost:8096>**

1. **Create-empty:** Create a playlist with an impossible rule (e.g. Name contains `zzzznonexistent`), open More options → Presentation, check **Hide when empty**, save. Expected: refresh succeeds; NO playlist appears in Jellyfin. Log check: `docker logs jellyfin 2>&1 | grep -i "hide when empty"` shows the hidden message.
2. **Appear:** Edit the list, relax the rule so items match, refresh. Expected: playlist appears with items.
3. **Disappear:** Edit back to the impossible rule, refresh. Expected: playlist is deleted from Jellyfin; the smart list is still listed in the plugin UI, edit mode shows the "Hide when empty" chip on the collapsed More options fold and auto-expands it.
4. **Custom image survives:** Upload a custom image to the list, make it empty (hidden), then make it non-empty again. Expected: recreated playlist has the custom image.
5. **Collection:** Repeat scenarios 1–3 with a collection.
6. **Default off:** Create a list with an impossible rule and the flag UNCHECKED. Expected: empty playlist/collection is created (today's behavior unchanged).
7. **User page:** Create a hidden-when-empty playlist from the user page (`user-playlists.html`) and verify scenario 1 works there too.
8. **Multi-user partial match:** Create a multi-user playlist (two users) with the flag on and a rule that matches items for only one user (e.g. an `IsPlayed` rule). Expected: the matching user gets a playlist, the other user's playlist is absent/hidden; both remain configured in the plugin UI.

- [ ] **Step 3: Update primer.md** (session-continuity file at repo root; rewrite per its existing structure) and report results.
