# Hide When Empty — Design

**Date:** 2026-07-13
**Status:** Approved

## Summary

Add an opt-in per-list option, "Hide when empty", that prevents a smart list's
Jellyfin playlist/collection from existing while the list's rules match zero
media items. When the rules match items again, the entity is recreated
automatically. Default is off (current behavior: empty entities are created and
kept).

## Behavior

- New bool property `HideWhenEmpty` on `SmartListDto` (base class), default
  `false`. Applies to both playlists and collections.
- The check runs during refresh, after the final item ID list is computed (the
  create API enqueues a refresh, so initial creation is covered by the same
  path):
  - **0 items + flag set + entity exists** → delete the Jellyfin entity
    (`_libraryManager.DeleteItem` with `DeleteFileLocation = true`, same
    pattern as PlaylistService.cs:498 / CollectionService.cs:434) and clear
    the stored Jellyfin entity ID.
  - **0 items + flag set + no entity** → skip entity creation.
  - Either way the refresh reports success with a message indicating the list
    is hidden because it is empty.
- **Recreation:** a later refresh that yields ≥1 item goes through the normal
  create path. Custom images are re-applied by the existing
  `ApplyCustomImagesToPlaylistAsync` / `ApplyCustomImagesToCollectionAsync`
  (images are stored per smart list in plugin data, not on the entity —
  verify during implementation).
- **Per-user playlists:** the check applies per user instance. A user with 0
  matching items has their playlist hidden; other users' instances are
  unaffected.
- **Definition of empty:** 0 main items, evaluated before bumper weaving. A
  list that would contain only bumpers counts as empty.
- The smart list configuration (DTO/JSON) is never deleted — only the Jellyfin
  entity. Disabled-list handling is unchanged.

## Backend changes

- `Core/Models/SmartListDto.cs`: add `HideWhenEmpty` (bool, default false).
- `Services/Playlists/PlaylistService.cs`: empty-check branch in the refresh
  flow where `linkedChildren` is known.
- `Services/Collections/CollectionService.cs`: same for collections.
- `Utilities/DtoMapper.cs` / input validation: thread the property through if
  mapping/validation touches per-property (verify during planning).

## Frontend changes

- Checkbox under the **Presentation** sub-heading inside the "More options"
  fold, label **"Hide when empty"**, with help text explaining the
  delete/recreate mechanics. Added to **both** `config.html` (admin) and
  `user-playlists.html` (user).
- `config-lists.js`: read/write the field in create, edit, and display paths.
- `syncAdvancedSection` (config-lists.js): checked state is an unambiguous
  non-default → add a chip signal so edit mode surfaces it and auto-expands
  the fold.
- No new JS file, so no `.csproj` / `Plugin.cs` registration needed.
- No ES6 template literals; `class="emby-checkbox"` per project conventions.

## Docs

- Add the option to the mkdocs user guide under `/docs/content/`, in the
  section covering list options/presentation.

## Verification

No test suite; verify against local Jellyfin (`./build-local.sh`, both ABIs
build clean):

1. Create a list with an impossible rule and the flag on → no
   playlist/collection appears in Jellyfin; refresh reports success.
2. Relax the rule, refresh → entity appears with items (and custom images, if
   any were set).
3. Tighten the rule back, refresh → entity disappears; smart list config still
   listed in plugin UI with the "Hide when empty" chip.
4. Same flow for the other list type (playlist vs collection), and a
   multi-user playlist where only one user matches items.
5. Flag off (default) → behavior identical to today (empty entity created).

## Out of scope

- Hiding via privacy/visibility mechanisms instead of deletion.
- Any change to disabled-list or delete-list flows.
- Retroactively hiding existing empty lists that don't opt in.
