# External Lists

External Lists let you populate smart lists from external services like MDBList, IMDb, Trakt, and TMDB. Use them to create collections based on trending lists, watchlists, top charts, and curated lists from these services.

## How It Works

1. Configure API key(s) in the plugin **Settings** tab under **External Lists**
2. Create a rule with the `External List` field, `equals` operator, and the list URL as the value
3. On refresh, the plugin fetches the external list and matches items by provider IDs (IMDb, TMDB, TVDB) against your Jellyfin library

Items are matched by comparing provider IDs between the external list and your library metadata. For episodes, the parent series IDs are also checked. If any provider ID matches, the item is included.

## Supported Providers

| Provider | API key required | Matches by |
|----------|-----------------|------------|
| **MDBList** | Yes | IMDb, TMDB, TVDB |
| **IMDb** | No | IMDb |
| **Trakt** | Yes (client ID) | IMDb, TMDB, TVDB |
| **TMDB** | Yes | TMDB |

---

### MDBList

[MDBList](https://mdblist.com) aggregates lists from multiple sources and provides a unified API.

**Setup:**

1. Go to [mdblist.com/preferences](https://mdblist.com/preferences/)
2. Sign in or create a free account
3. Your API key is displayed on the preferences page
4. Enter the API key in the plugin **Settings** tab under **External Lists > MDBList API Key**

**Supported URLs:**

| Type | URL format |
|------|-----------|
| User list | `https://mdblist.com/lists/{username}/{listname}` |

**Example:**

```
External List / equals / https://mdblist.com/lists/linaspurinis/top-watched-movies-of-the-week
```

!!! note "Rate Limits"
    The free tier allows 1,000 API requests per day. Each list URL uses one or more requests depending on list size.

---

### IMDb

[IMDb](https://www.imdb.com) public lists and chart pages can be used directly without an API key. The plugin scrapes the HTML page to extract IMDb title IDs.

**Setup:**

No API key required. Just use a public IMDb list or chart URL.

**Supported URLs:**

| Type | URL format |
|------|-----------|
| User list | `https://www.imdb.com/list/ls123456789/` |
| Top 250 Movies | `https://www.imdb.com/chart/top/` |
| Top 250 TV Shows | `https://www.imdb.com/chart/toptv/` |
| Box Office | `https://www.imdb.com/chart/boxoffice/` |

**Example:**

```
External List / equals / https://www.imdb.com/chart/top/
```

!!! warning "IMDb Limitations"
    IMDb lists must be public. Private lists cannot be accessed. Only IMDb IDs are extracted, so items in your library must have IMDb metadata to match.

---

### Trakt

[Trakt](https://trakt.tv) is a popular tracking service with user lists, watchlists, and curated charts. The plugin uses the Trakt API which requires a client ID.

**Setup:**

1. Go to [trakt.tv/oauth/applications](https://trakt.tv/oauth/applications)
2. Sign in to your Trakt account
3. Click **New Application**
4. Fill in the required fields:
    - **Name**: `Jellyfin SmartLists` (or any name you prefer)
    - **Redirect uri**: `urn:ietf:wg:oauth:2.0:oob`
    - All other fields can be left blank
5. Click **Save App**
6. Copy the **Client ID** from your new application
7. Enter the client ID in the plugin **Settings** tab under **External Lists > Trakt Client ID**

**Supported URLs:**

| Type | URL format |
|------|-----------|
| User list | `https://trakt.tv/users/{user}/lists/{list}` |
| Watchlist | `https://trakt.tv/users/{user}/watchlist` |
| Trending movies | `https://trakt.tv/movies/trending` |
| Popular movies | `https://trakt.tv/movies/popular` |
| Most watched movies | `https://trakt.tv/movies/watched` |
| Most played movies | `https://trakt.tv/movies/played` |
| Most collected movies | `https://trakt.tv/movies/collected` |
| Anticipated movies | `https://trakt.tv/movies/anticipated` |
| Box office | `https://trakt.tv/movies/boxoffice` |
| Trending shows | `https://trakt.tv/shows/trending` |
| Popular shows | `https://trakt.tv/shows/popular` |
| Most watched shows | `https://trakt.tv/shows/watched` |
| Most played shows | `https://trakt.tv/shows/played` |
| Most collected shows | `https://trakt.tv/shows/collected` |
| Anticipated shows | `https://trakt.tv/shows/anticipated` |

**Examples:**

```
External List / equals / https://trakt.tv/users/justin/lists/imdb-top-rated-movies
External List / equals / https://trakt.tv/movies/trending
External List / equals / https://trakt.tv/users/me/watchlist
```

!!! note "Rate Limits"
    Trakt allows 1,000 API requests per 5 minutes. Use your smart list's **Max Items** setting to control how many items end up in the final list.

!!! note "Public Lists Only"
    The plugin uses API-key authentication (not OAuth), so only public user lists and watchlists are accessible. Private lists require the list owner to make them public.

---

### TMDB

[The Movie Database (TMDB)](https://www.themoviedb.org) provides a comprehensive API with user lists, popular/top-rated charts, and trending endpoints.

**Setup:**

1. Go to [themoviedb.org/settings/api](https://www.themoviedb.org/settings/api)
2. Sign in or create a free account
3. Click **Create** or **Request an API Key** and select **Developer**
4. Accept the terms of use
5. Fill in the application form:
    - **Application Name**: `Jellyfin SmartLists` (or any name you prefer)
    - **Application URL**: `https://github.com/jyourstone/jellyfin-smartlists-plugin` (or your Jellyfin server URL)
    - **Type of Use**: `Desktop Application`
    - **Application Summary**: e.g. `Jellyfin plugin that creates smart playlists from TMDB lists and charts`
    - Fill in the remaining contact fields with your information
6. Submit the form — the API key is generated immediately
7. Copy the **API Key (v3 auth)** — not the read access token
8. Enter the API key in the plugin **Settings** tab under **External Lists > TMDB API Key**

**Supported URLs:**

| Type | URL format |
|------|-----------|
| User list | `https://www.themoviedb.org/list/{id}` |
| Popular movies | `https://www.themoviedb.org/movie` |
| Top rated movies | `https://www.themoviedb.org/movie/top-rated` |
| Now playing movies | `https://www.themoviedb.org/movie/now-playing` |
| Upcoming movies | `https://www.themoviedb.org/movie/upcoming` |
| Popular TV | `https://www.themoviedb.org/tv` |
| Top rated TV | `https://www.themoviedb.org/tv/top-rated` |
| Airing today TV | `https://www.themoviedb.org/tv/airing-today` |
| On the air TV | `https://www.themoviedb.org/tv/on-the-air` |
| Trending (daily) | `https://www.themoviedb.org/trending/movie/day` |
| Trending (weekly) | `https://www.themoviedb.org/trending/movie/week` |
| Trending TV (daily) | `https://www.themoviedb.org/trending/tv/day` |
| Trending TV (weekly) | `https://www.themoviedb.org/trending/tv/week` |
| Trending all (weekly) | `https://www.themoviedb.org/trending/all/week` |

**Examples:**

```
External List / equals / https://www.themoviedb.org/movie
External List / equals / https://www.themoviedb.org/movie/top-rated
External List / equals / https://www.themoviedb.org/list/8136
```

!!! note "TMDB ID Matching"
    TMDB only returns TMDB IDs. Items in your Jellyfin library must have TMDB metadata to match. Since Jellyfin uses TMDB as a primary metadata source, most items should match.

!!! note "TMDB Rate Limits"
    TMDB is lenient with rate limits but chart/trending endpoints can return thousands of items. Use your smart list's **Max Items** setting to control the final list size.

!!! note "Trending URLs"
    The trending URLs (e.g. `/trending/movie/week`) are not browsable pages on TMDB's website — they map directly to TMDB API endpoints. Use the exact URL formats shown in the table above.

---

## Tips

!!! tip "Combining with other rules"
    External list rules work well combined with other filters. For example:

    - `External List equals <trending-list>` AND `Genre equals Action` — trending action movies
    - `External List equals <top-250>` AND `Playback Status equals Unplayed` — unwatched top-rated movies
    - `External List not equals <watched-list>` — items not in a specific list

!!! tip "Multiple external lists"
    You can use multiple External List rules in different rule groups (OR logic) to combine items from several lists into one smart list.
