# Media Types

When creating a smart list, you must select at least one **Media Type** to specify what kind of content should be included. Media types in SmartLists correspond directly to the **Content type** options you see when adding a new Media Library in Jellyfin.

## Available Media Types

| Media Type | Jellyfin Library | Description |
|------------|------------------|-------------|
| **Movies** | Movies | Feature films and movie content |
| **Episodes** | Shows | Individual TV show episodes |
| **Series** | Shows | Entire TV series (collections only, not individual episodes) |
| **Audio** | Music | Music tracks and songs |
| **Music Videos** | Music Videos | Music video content |
| **Video** | Home Videos and Photos | Personal video content and extras (behind the scenes, featurettes, etc.) |
| **Photo** | Home Videos and Photos | Photo content |
| **Books** | Books | E-book content |
| **AudioBooks** | Books | Audiobook content |
| **Trailer** | — | Trailers attached to movies/shows (requires **Include Extras**) |

## Extras (Special Features)

Jellyfin supports "extras" — behind the scenes, deleted scenes, featurettes, trailers, and other bonus content attached to movies and TV shows. By default, extras are **not included** in smart lists because they are owned by their parent items and excluded from standard library queries.

To include extras in a smart list, enable the **Include Extras** checkbox when creating or editing a list. This adds extras to the item pool alongside regular library items.

Once included, you can use the **Extra Type** rule field to filter by specific extra type. The field provides a dropdown with all available types: Behind the Scenes, Clip, Deleted Scene, Featurette, Interview, Sample, Scene, Short, Theme Song, Theme Video, Trailer, and Unknown.

Selecting an **Extra Type** rule will automatically enable the **Include Extras** checkbox.

!!! tip "Trailer Media Type"
    Jellyfin resolves trailers as a separate `Trailer` media type. If your list includes trailers, make sure to add **Trailer** to your selected media types. Other extras (behind the scenes, featurettes, etc.) are resolved as **Video** type.

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
