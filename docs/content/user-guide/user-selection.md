# User Selection

When creating a smart list, you must select one or more users. How user selection works differs significantly between **Playlists** and **Collections**.

## Playlists: Multi-User Support

For **Playlists**, you can select one or more users who will be the **owners** of the playlist:

### Single User
- When you select a single user, one Jellyfin playlist is created and owned by that user
- The playlist is filtered based on that user's data (watch status, favorites, play count, etc.)
- The playlist appears in that user's library

### Multiple Users
- When you select multiple users, a **separate Jellyfin playlist is created for each user**
- Each user gets their own personalized version of the same smart playlist
- Each playlist is filtered based on the respective user's own data
- This allows the same smart playlist configuration to show different content for each user

!!! example "Multi-User Playlist Example"
    If you create a "My Favorites" playlist and select three users (Alice, Bob, and Charlie):
    
    - Alice will see a playlist containing her favorite items
    - Bob will see a playlist containing his favorite items
    - Charlie will see a playlist containing his favorite items
    
    Each user sees only their own favorites, even though they're all using the same smart playlist configuration.

!!! note "User-Specific Rules Must Use 'Default' Target"
    For multi-user playlists to work correctly with personalized content, user-specific rules (like "Is Favorite", "Playback Status", "Play Count", etc.) must have their user target set to **"Default"**.
    
    - **Default**: The rule will be evaluated for each playlist owner individually, creating personalized content
    - **Specific User**: If you change the rule to target a specific user (e.g., "Is Favorite for Alice"), then all playlists will use Alice's data.
    
    When creating rules, the user target dropdown defaults to "Default" - keep it that way for multi-user personalization to work as expected.

### Visibility Settings

Playlists also have a **"Make playlist public"** option:

- **Private (unchecked)**: The playlist is only visible to the selected user(s)
- **Public (checked)**: The playlist is visible to all logged-in users on the server, but the content is still based on the owner's data

!!! note "Public Playlists with Multiple Users"
    When you select multiple users for a playlist, the "Make playlist public" option is automatically hidden and disabled. This is because each user gets their own separate playlist, and it wouldn't make sense for one user's personalized playlist to be visible to others.

!!! warning "Multi-User Playlists and User Page Access"
    Multi-user playlists are **only visible and editable by administrators**. Regular users accessing the SmartLists user page will not see multi-user playlists in their list, even if they are one of the selected users.
    
    This is a safety measure to prevent users from accidentally modifying playlists that would affect other users. If a user needs their own version of a multi-user playlist, an administrator should create separate single-user playlists instead (one for each user).

## Collections: Reference User

For **Collections**, the user selection works differently because collections don't have "owners":

### No Ownership
- Collections are **server-wide** and visible to all users
- There is no concept of a collection "owner"
- All users can see the same collection

### Reference User for Filtering
- The user you select is used as a **reference** when fetching and filtering media items
- This user's context is used for:
  - **Library access permissions**: Only items this user has access to will be included
  - **User-specific rules**: Rules like "Playback Status", "Is Favorite", "Play Count", etc. are evaluated based on this user's data (unless you specifically choose a different user in the rule)
  - **User-specific fields**: Any user-dependent filtering uses this user's context (unless you specifically choose a different user in the rule)

!!! example "Collection Reference User Example"
    If you create a "Recently Watched Movies" collection and select Bob as the list user:
    
    - The collection will only include movies Bob has access to in his libraries
    - The "recently watched" status is based on Bob's watch history
    - All users can see this collection, but the content is determined by Bob's data
    
    This means Alice and Charlie will see the same collection showing movies that Bob recently watched, not their own recently watched movies.

!!! warning "Choosing the Right Reference User"
    When creating collections with user-specific rules, carefully consider which user to select as the reference:
    
    - For collections based on user-specific data (favorites, watch status, etc.), select a user whose data represents what you want to show to everyone
    - For collections based only on metadata (genre, year, rating, etc.), the user selection matters less, but you should still select a user who has access to all the content you want to include

## Summary

| Feature | Playlists | Collections |
|---------|-----------|-------------|
| **User Selection** | One or more users | Single reference user |
| **Ownership** | Each selected user owns their playlist | No ownership (server-wide) |
| **Visibility** | Private or public | Always visible to all users |
| **Content Filtering** | Based on each owner's data | Based on reference user's data |
| **Multiple Instances** | One playlist per selected user | Single collection for all users |

## Selecting Users

In the SmartLists configuration interface:

### For Playlists
1. Click on the **Playlist User(s)** multi-select dropdown
2. Check the boxes for the users you want to create playlists for
3. At least one user must be selected
4. Each selected user will get their own personalized playlist

### For Collections
1. Select a single user from the **Collection User** dropdown
2. This user will be used as the reference for filtering and rule evaluation
3. The collection will be visible to all users on the server
