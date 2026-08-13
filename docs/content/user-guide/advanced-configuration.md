# Advanced Configuration

For advanced users who prefer direct file editing or need to perform bulk operations, SmartLists stores all list configurations as JSON files.

## File Location

Smart lists are stored in the Jellyfin data directory with the following structure:

```
{DataPath}/smartlists/
  {listId}/
    config.json       # List configuration
    primary.jpg       # Custom Primary image (optional)
    backdrop.png      # Custom Backdrop image (optional)
    thumb.jpg         # Custom Thumb image (optional)
    ...               # Other custom images
```

Where `{DataPath}` is your Jellyfin data path (typically `/config/data` on Linux, `C:\ProgramData\Jellyfin\Server\data` on Windows, or `~/Library/Application Support/Jellyfin/Server/data` on macOS).

Each smart list has its own folder named with its unique GUID identifier. The configuration is stored in `config.json`, and any custom images uploaded through SmartLists are stored alongside it.

### Legacy Locations (Deprecated)

For backward compatibility, the plugin also checks these legacy locations when reading configurations:

- `{DataPath}/smartlists/{listId}.json` (flat file format from older versions)
- `{DataPath}/smartplaylists/{listId}.json` (very old format)

Lists in legacy locations are automatically migrated to the new folder structure on plugin startup.

## File Format

List files use JSON format with the following structure:

- **Indented JSON** - Files are formatted with indentation for readability
- **UTF-8 encoding** - All files use UTF-8 character encoding
- **GUID-based filenames** - Each file is named using the list's unique identifier

## Manual Editing

You can manually edit these JSON files if needed, but please be aware:

!!! warning "Edit at Your Own Risk"
    - **No validation safeguards**: The plugin may not have safeguards in place for misconfigured JSON files
    - **Backup first**: Always backup your list files before editing
    - **Syntax errors**: Invalid JSON syntax will prevent the list from loading
    - **Data corruption**: Incorrect field values or types may cause unexpected behavior or errors

### Best Practices

1. **Always backup** your `smartlists` directory before making changes
2. **Validate JSON syntax** using a JSON validator before saving
3. **Test thoroughly** after making changes to ensure lists still work correctly
4. **Use the web interface** when possible - it's safer and includes validation

## Creating Lists from Files

New lists don't have to be created through the web interface. The plugin keeps no separate registry — it re-reads the `smartlists` directory whenever lists are needed — so a valid `config.json` placed in the right folder is picked up automatically on the next page load or refresh. No restart required.

### Creating a New List

1. Generate a new GUID (e.g. with `uuidgen` or an online generator).
2. Create the folder `{DataPath}/smartlists/{new-guid}/`.
3. Create a `config.json` inside it. Minimal example:

```json
{
  "Id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "Name": "Recent Comedies",
  "Type": "Playlist",
  "UserId": "your-jellyfin-user-guid",
  "MediaTypes": ["Movie"],
  "ExpressionSets": [
    {
      "Expressions": [
        { "MemberName": "Genres", "Operator": "Contains", "TargetValue": "Comedy" },
        { "MemberName": "ProductionYear", "Operator": "GreaterThanOrEqual", "TargetValue": "2000" }
      ]
    }
  ]
}
```

Requirements:

- **`Id` must match the folder name** — the plugin looks lists up by folder name and saves them to the folder matching `Id`. A mismatch creates a duplicate the next time the list is saved.
- **`Name` is required** — a file without it fails to load entirely.
- **`Type`** is `"Playlist"` or `"Collection"` (defaults to `"Playlist"` if omitted).
- **Ownership (playlists only)**: set `UserId` to a Jellyfin user GUID (copy one from an existing list's file), or set `"AllUsers": true` to create a personalized playlist for every user. Collections don't need an owner.
- **`MediaTypes`** values must match the types listed in [Media Types](media-types.md) — unknown values are silently dropped.

Everything else is optional with sensible defaults (`Enabled: true`, `AutoRefresh: "Never"`, no item limits). The Jellyfin playlist or collection itself is created automatically on the first refresh, and the plugin writes its ID back into `config.json`. The easiest way to fine-tune the result is to open the list in the web interface once it appears.

### Copying an Existing List

Copying a list's folder is a quick way to create variants:

1. Copy the folder to a new folder named with a freshly generated GUID.
2. In the copy's `config.json`, set `Id` to the new GUID and change `Name`.
3. **Remove the link to the original's Jellyfin playlist**: delete the top-level `JellyfinPlaylistId` and the `JellyfinPlaylistId` inside every `UserPlaylists` entry (for collections: `JellyfinCollectionId`). If you skip this, both smart lists point at the same Jellyfin playlist and overwrite each other's contents on refresh.
4. Optionally remove the `LastRefreshed`, `ItemCount` and `TotalRuntimeMinutes` fields — they are statistics and are regenerated on refresh.

### Field and Operator Names

Rules in JSON use internal names, not the labels shown in the web interface:

- `MemberName` — the field's JSON name, e.g. `OfficialRating` for "Parental Rating". See the **JSON name** columns in [Fields and Operators](fields-and-operators.md).
- `Operator` — e.g. `Equal`, `Contains`, `IsIn`, `GreaterThanOrEqual`, `MatchRegex`. Listed per operator type in [Fields and Operators](fields-and-operators.md#operators).
- `TargetValue` — always a string, even for numbers (`"TargetValue": "2000"`).

Administrators can also fetch the authoritative field list from the API — see
[Integration API](../development/integration-api.md#reference-data).

### Gotchas

!!! warning
    - **Errors are only visible in the logs.** A file with invalid JSON or an unknown enum value is silently skipped — the list simply doesn't appear. Check the Jellyfin log for `Skipping invalid smart list file`.
    - **Field names in rules are validated at refresh time**, not at load time. A typo in `MemberName` loads fine but fails when the list refreshes.
    - **The plugin rewrites `config.json` on every refresh** (statistics, playlist IDs). Don't hand-edit a file while a refresh is running, or your changes may be overwritten.

## Example Use Cases

Manual editing can be useful for:

- **Bulk operations**: Making the same change to multiple lists
- **Advanced configurations**: Settings not available in the web interface
- **Migration**: Copying lists between Jellyfin instances
- **Backup/restore**: Manual backup and restoration of list configurations
- **Scripted creation**: Generating new lists from templates or scripts (see [Creating Lists from Files](#creating-lists-from-files))

## File Structure Reference

For a reference of the JSON file structure, you can:

1. **Create a backup** using the web interface (Settings → Create Backup Now) to see the format
2. **Examine existing files** in your `smartlists` directory
3. **Check the repository** for example files (if available)

The JSON structure follows the `SmartListDto` format, which includes fields for:
- List metadata (name, ID, owner, list type, etc.)
- Rules and logic groups
- Sort options
- Refresh settings
- Limits (max items, max playtime)
- And more

## Troubleshooting

If a list file becomes corrupted or invalid:

1. **Check JSON syntax** - Use a JSON validator to find syntax errors
2. **Restore from backup** - If you have a backup, restore the file
3. **Recreate via UI** - Delete the corrupted file and recreate the list using the web interface
4. **Check logs** - Review Jellyfin logs for specific error messages about the list

!!! tip "Prefer the Web Interface"
    While manual editing is possible, the web interface is the recommended method for creating and editing lists. It includes validation, error checking, and is much safer than manual file editing.

