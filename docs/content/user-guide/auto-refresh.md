# Auto-Refresh

Smart playlists and collections can update automatically in multiple ways.

## Real-Time Auto-Refresh

Configure lists to refresh automatically when your library changes:

- **Per-List Setting**: Each list can be set to `Never`, `On Library Changes`, or `On All Changes`
- **Global Default**: Set the default auto-refresh mode for new lists in Settings
- **Unified Batching**: All changes use intelligent 3-second batching to prevent spam during bulk operations
- **Performance Optimized**: Uses advanced caching to only refresh lists that are actually affected by changes
- **Automatic Deduplication**: Multiple events for the same item are combined into a single refresh

### Auto-Refresh Modes

- **Never**: Scheduled and manual refresh only (original behavior)
- **On Library Changes**: Refresh only when new items are added to your library
- **On All Changes**: Refresh for library additions AND all updates (metadata changes, playback status, favorites, etc.)

## Custom List Scheduling

Configure individual lists with their own refresh schedules:

- **Per-list scheduling**: Each list can have its own schedule
- **Schedule types**: Daily, Weekly, Monthly, Yearly, or Interval
- **Flexible intervals**: 15 min, 30 min, 1 h, 2 h, 3 h, 4 h, 6 h, 8 h, 12 h, or 24 h

### Schedule Options

- **Daily**: Refresh at a specific time each day (e.g., 3:00 AM)
- **Weekly**: Refresh on a specific day and time each week (e.g., Sunday at 8:00 PM)
- **Monthly**: Refresh on a specific day and time each month (e.g., 1st at 2:00 AM)
- **Yearly**: Refresh on a specific month, day and time each year (e.g., January 1st at midnight)
- **Interval**: Refresh at regular intervals (e.g., every 2 hours, every 30 minutes)
- **No schedule**: Disable all scheduled refreshes (auto-refresh and manual only)

!!! tip "Multiple Schedules"
    You can add multiple schedules to a single list. For example, you could set both a Daily schedule at 6:00 AM and an Interval schedule every 4 hours to refresh the list both at a specific time and at regular intervals throughout the day.

## Visibility Scheduling

Control when lists appear and disappear in Jellyfin automatically:

- **Seasonal Lists**: Show Christmas movies only in December, Halloween collections only in October
- **Event-Based**: Display special collections for specific dates or events
- **Time-Based Visibility**: Make playlists visible only on certain days of the week or times of year

### How It Works

Visibility schedules use Enable and Disable actions to automatically show or hide lists:

- **Enable**: Makes the list visible in Jellyfin at the scheduled time
- **Disable**: Hides the list from Jellyfin at the scheduled time
- **Schedule Types**: Same as refresh schedules (Daily, Weekly, Monthly, Yearly, Interval)
- **Automatic Refresh**: When a list is enabled by schedule, it automatically refreshes its content
- **Multiple Schedules**: Add multiple visibility schedules to control complex visibility patterns

### Visibility Schedule Examples

- **Christmas Movies**: 
    - Enable: Yearly on December 1st at 00:00
    - Disable: Yearly on January 2nd at 00:00
- **Halloween Collection**:
    - Enable: Yearly on October 25th at 00:00
    - Disable: Yearly on November 1st at 06:00
- **Weekend Playlist**:
    - Enable: Weekly on Friday at 18:00
    - Disable: Weekly on Sunday at 23:59
- **Summer Movies**:
    - Enable: Yearly on June 1st at 00:00
    - Disable: Yearly on September 1st at 00:00

### Combining Refresh and Visibility Schedules

You can use both types of schedules together:

- **Visibility Schedule**: Controls when the list appears/disappears
- **Refresh Schedule**: Updates content while the list is visible

**Example**: A Christmas movies collection could have:
- Visibility: Enable December 1st, Disable January 2nd
- Refresh: Daily at 3:00 AM (keeps content fresh during December)

!!! tip "Enable Takes Priority"
    If multiple visibility schedules trigger at the same time with conflicting actions (e.g., one Enable and one Disable), the Enable action takes priority. This ensures lists are shown when you want them visible.

## Legacy Scheduled Tasks

!!! warning "Deprecated and Removed"
    Legacy scheduled tasks have been deprecated and removed. The original Jellyfin scheduled tasks (Audio Smart Playlist and Media Smart Playlist) are no longer used. All lists now use the custom scheduling system described above, or rely on auto-refresh and manual refresh only.

## Example Use Cases

### Custom Scheduling Examples

- **Daily Random Mix**: Random-sorted playlist with a Daily schedule at 6:00 AM → fresh random order every morning
- **Weekly Discoveries**: New-content playlist with a Weekly schedule on Sunday at 8:00 PM → weekly refresh for weekend planning
- **Monthly Archive**: Year-based movie playlist with a Monthly schedule on the 1st at 2:00 AM → monthly refresh for archival content
- **Background Refresh**: Mood-based music playlist with 4-hour intervals → regular updates without being intrusive

### Auto-Refresh Examples

- **Continue Watching**: NextUnwatched playlist with auto-refresh on all changes → updates when episodes are watched (batched)
- **New Releases**: Date-based list with auto-refresh on library changes → updates when new content is added
- **Favorites Collection**: Favorite-based collection with auto-refresh on all changes → updates when items are favorited/unfavorited (batched)
- **Multi-User Playlists**: When a playlist is associated with multiple users, it will auto-refresh when any of those users' playback data changes (watched status, favorites, etc.). Each user's personalized playlist is updated independently based on their own data changes.

### Mixed Approach

Combine both systems for optimal performance:

- Use **custom scheduling** for lists that benefit from regular refresh (random order, time-based rules)
- Use **auto-refresh** for lists that need immediate updates (playback status, new additions)

## Scheduled Refresh Control

Perfect for randomized lists:

- Enable scheduled refresh for randomized lists to get fresh random order daily/hourly
- Disable for rule-based lists that rely on real-time auto-refresh instead
- Mix and match: some lists on schedule, others auto-refresh only

## Manual Refresh

- Use the **"Refresh All Lists"** button in the Settings tab to trigger a refresh of all lists
- Use the **"Refresh"** button next to each list in the Manage Lists tab to refresh individual lists

!!! note "Refresh Time"
    A full refresh of all lists can take some time depending on how many media items and lists you have. Large libraries with many lists may take several minutes or even hours to complete, depending on the hardware. Individual list refreshes are typically faster.

!!! tip "Monitor Refresh Progress"
    When you click "Refresh All Lists", you'll be automatically redirected to the **Status** page where you can monitor the progress of all refresh operations in real-time. The Status page shows ongoing operations with progress bars, estimated time remaining, and detailed refresh history. See the [Configuration](configuration.md#3-status) guide for more details.

## Performance Considerations

### Auto-Refresh Settings

- **`Never`**: No automatic refreshes
- **`On Library Changes`**: Good performance, refreshes only for library additions
- **`On All Changes`**: Refreshes for additions AND updates (metadata, playback status, etc.)

### Large Library Recommendations

- All changes are automatically batched with a 3-second delay to prevent spam during bulk operations
- Even `On All Changes` is efficient thanks to intelligent batching during large library scans
- **Removed items** are automatically handled by Jellyfin (no refresh needed)
- Consider limiting the number of lists with auto-refresh enabled to optimize server performance

### Third-Party Plugin Compatibility

- Plugins that sync watched status may trigger many simultaneous updates
- If you experience performance issues during bulk sync operations, temporarily set lists to `Never` or `On Library Changes`