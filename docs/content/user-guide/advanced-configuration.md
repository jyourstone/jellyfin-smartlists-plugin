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

## Example Use Cases

Manual editing can be useful for:

- **Bulk operations**: Making the same change to multiple lists
- **Advanced configurations**: Settings not available in the web interface
- **Migration**: Copying lists between Jellyfin instances
- **Backup/restore**: Manual backup and restoration of list configurations

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

