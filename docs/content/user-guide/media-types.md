# Media Types

When creating a smart list, you must select at least one **Media Type** to specify what kind of content should be included. Media types in SmartLists correspond directly to the **Content type** options you see when adding a new Media Library in Jellyfin — plus **Collection** and **Playlist**, which target Jellyfin's built-in collections and playlists rather than a library (see [below](#container-media-types)).

!!! tip "Default Media Types"
    If you usually create lists with the same media types, admins can set **Default Media Types** in the Settings tab of the admin configuration page. New lists created there will start with those types pre-selected (types only available for collections are skipped when the new list is a playlist).

## Available Media Types

| Media Type | Jellyfin Library | Description |
|------------|------------------|-------------|
| **Movies** | Movies | Feature films and movie content |
| **Series** | Shows | Entire TV series (collections only, not individual episodes) |
| **Season** | Shows | Entire TV show seasons (collections only) |
| **Episodes** | Shows | Individual TV show episodes |
| **Audio** | Music | Music tracks and songs |
| **Album** | Music | Entire music albums (collections only, not individual tracks) |
| **Music Videos** | Music Videos | Music video content |
| **Video** | Home Videos and Photos | Personal video content and extras (behind the scenes, featurettes, trailers, etc.) |
| **Photo** | Home Videos and Photos | Photo content |
| **Books** | Books | E-book content |
| **AudioBooks** | Books | Audiobook content |
| **Collection** | Collections | Jellyfin collections themselves (collections only, see [below](#container-media-types)) |
| **Playlist** | Playlists | Jellyfin playlists themselves (collections only, see [below](#container-media-types)) |

## Collection & Playlist Media Types {#container-media-types}

Smart **collections** can also select **Collection** and **Playlist** as media types, to build lists *of* collections or playlists — a "Superhero Universes" collection containing your Marvel and DC collections, or a collection that groups all your workout playlists. These types are not offered for smart playlists, because Jellyfin playlists can only contain media items.

Collection and Playlist mix freely with the other media types: `Movie + Collection` is a valid selection, and — just like selecting `Movie + Series` — every rule group applies to every selected media type.

### Match by members

When a Collection or Playlist media type is selected, a list-level toggle appears: **"Match collections/playlists by the items inside them"**.

**Toggle off (default)** — rules are checked against the collection or playlist **itself**. When *only* container types are selected, the rule field dropdown is limited to the fields a collection or playlist actually carries:

- **Name**
- **Genres** and **Studios** (Jellyfin aggregates these from a collection's items, and SmartLists writes them for its own smart collections and playlists)
- **Production Year**, **Release Date**, **Parental Rating**
- **Date Created**, **Last Metadata Refresh**, **Last Database Save**
- **Collection Name** (membership in another collection)

**Toggle on** — rules are checked against the **items inside** each collection or playlist, exactly as they would be for an item list. A collection or playlist is included when **at least one of its items passes all rules in a rule group** (rule groups still combine with OR). Every item field works: actors, tags, playback status, resolution, audio languages, and so on.

For example, with the **Collection** media type and the toggle on:

```text
Actors contains "Arnold Schwarzenegger"
AND Production Year less than 1990
```

matches the collections that contain a pre-1990 Arnold Schwarzenegger movie. A **single item** must satisfy the whole rule group — a collection holding one 2015 Arnold movie and one 1985 movie without him does not match. Use separate rule groups (OR) when each condition may be satisfied by different items.

In a mixed selection like `Movie + Collection`, item candidates are still evaluated directly: the example above produces the pre-1990 Arnold movies *and* the collections containing one.

!!! note "Nested collections"
    A matched collection still pulls in its nested collections up to the configured [Collection search depth](fields-and-operators.md#collection-search-depth), in both toggle modes.

!!! note "Self-reference prevention"
    A smart list never includes its own collection or playlist in the results, in either toggle mode, even when it matches the rules.

!!! info "Replaces the old include-only checkboxes"
    Older versions used per-rule **"Include collections only"** / **"Include playlist only"** checkboxes on the **Collection name** and **Playlist name** fields. Existing lists are migrated automatically on load: the include-only rule becomes a **Name** rule, the matching media type is selected, and the toggle stays off. See the [changelog](../changelog/rc.md) for details on lists that mixed include-only rules with normal item rules.

### Group results into collections {#group-into-collections}

Smart collections have a second list-level toggle, **"Group results into collections"**, under **More options → Presentation**. Where [Match by members](#match-by-members) checks a collection by the items *inside* it, this one works the other way round — it turns an ordinary item search *into* collections. It is available on every smart collection and, unlike Match by members, needs no container media type.

With the toggle on, every matched item that is a direct member of one or more Jellyfin collections is replaced by those collections. Items that are in no collection are kept as they are, and results that already *are* collections or playlists pass through unchanged. Results are de-duplicated, so ten matched Marvel movies produce a single Marvel collection rather than ten copies of it — while an item sitting in several collections is replaced by all of them.

For example, on a smart collection with the **Movie** media type and the toggle on:

```text
Genres contains "Action"
AND Community Rating greater than 8
```

produces a collection of every collection holding a highly-rated action movie, instead of a collection of the movies themselves.

!!! note "Direct membership only"
    Only the collections that contain a matched item **directly** are used — nested collections are not traversed, and the [Collection search depth](fields-and-operators.md#collection-search-depth) setting has no say in which collections are emitted (it still affects how they are *sorted*, see below). Episodes and seasons are never direct collection members themselves, so they are grouped through their **series**: an episode is replaced by the collections holding its show.

!!! note "Which collections are used"
    Grouping uses the Jellyfin collections the [reference user](user-selection.md#collections-reference-user) can see: manually created ones and the collections Jellyfin creates automatically for movie franchises. **Smart collections are never emitted** — neither the list's own (the self-reference prevention described above) nor any other one this plugin manages. They are regenerated on every refresh, so grouping into one would make a list's contents depend on refresh order, and any smart collection that happened to hold a matched item would crowd out the real collections. To build a list *of* smart collections, use the [Collection media type](#container-media-types) instead.

!!! note "Grouping runs before sorting and limits"
    Items are grouped before the list is sorted and before **Max Items** is applied, so that limit — global and [per rule group](sorting-and-limits.md#per-group-max-items) — counts collections, not the items that collapsed into them. A grouped collection inherits the rule groups of the items it replaced, so it counts toward those groups' limits. Sorting by **Round Robin** grouped by *Collection* adds nothing here: every result is already a collection, so each one forms a group of its own.

!!! tip "Sorting grouped results by a member value"
    Grouped results are collections, and a collection usually carries no community rating, year or release date of its own — so sorting by one of those fields leaves every entry at zero unless [Collection search depth](fields-and-operators.md#collection-search-depth) is set to **1 or higher**, which is what switches those sorts over to [aggregating the values of a collection's children](sorting-and-limits.md#child-item-sorting). The depth setting never changes *which* collections grouping emits.

## Extras (Special Features)

Jellyfin supports "extras" — behind the scenes, deleted scenes, featurettes, trailers, and other bonus content attached to movies and TV shows. By default, extras are **not included** in smart lists because they are owned by their parent items and excluded from standard library queries.

To include extras in a smart list, enable the **Include Extras** checkbox when creating or editing a list. This adds extras to the item pool alongside regular library items.

Once included, you can use the **Extra Type** rule field to filter by specific extra type. The field provides a dropdown with all available types: Behind the Scenes, Clip, Deleted Scene, Featurette, Interview, Sample, Scene, Short, Theme Song, Theme Video, Trailer, and Unknown.

Selecting an **Extra Type** rule will automatically enable the **Include Extras** checkbox.

!!! tip "Including Trailers"
    Trailers are extras attached to movies and shows. To include them, select the **Video** media type, enable **Include Extras**, and optionally add an **Extra Type** = Trailer rule to filter for trailers specifically.

## Important Notes

!!! warning "Library Content Type Matters"
    The media type you select must match the content type of your Jellyfin libraries. For example:
    
    - If you select **Movies**, the list will only include items from libraries configured with the "Movies" content type
    - If you select **Episodes**, the list will only include items from libraries configured with the "Shows" content type
    - If you select **Audio**, the list will only include items from libraries configured with the "Music" content type

!!! tip "Multiple Media Types"
    You can select multiple media types for a single list. For example, you could create a list that includes both **Movies** and **Episodes** to create a mixed content list.

## Selecting Media Types

In the SmartLists configuration interface, media types are presented as a multi-select dropdown:

1. Click on the **Media Types** field
2. Check the boxes for the media types you want to include
3. At least one media type must be selected
4. The selected types will be displayed in the field

The available fields and operators for filtering will vary depending on which media types you select. See the [Fields and Operators](fields-and-operators.md) guide for details on what filtering options are available for each media type.
