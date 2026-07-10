# Bumpers

Bumpers let a smart **playlist** weave short interstitial items — commercials, channel idents, "coming up next" clips — between its main items, recreating the feel of a classic TV channel.

Bumpers are selected by their **own rules and media type**, completely independent of the playlist's main rules. After the main content has been filtered, sorted, and limited, one bumper is inserted after every N main items, cycling through the bumper pool and repeating as needed.

!!! note "Playlists only"
    Bumpers are available for playlists only. Collections have no play order, so the bumper section is hidden when creating or editing a collection.

## How It Works

1. The playlist's main rules, sorting, and limits are applied as usual
2. The bumper rules select the bumper pool from the bumper media type
3. The pool is ordered by the **Bumper order** setting
4. One bumper is inserted after every **N** main items, cycling through the pool with wraparound — when the pool runs out, it starts over from the beginning, so bumpers repeat as often as needed
5. No bumper is added after the final item

The order of your main items is never changed — bumpers are only inserted between them.

## Configuration

The **Bumpers (optional)** section appears below the sorting options in the playlist form (on both the admin and user pages).

| Setting | Description |
|---------|-------------|
| **Bumper media type** | The media type of the bumper pool — it can differ from the playlist's main media types (e.g., Video items as bumpers between Episodes). Only playlist-supported types are available: Series, Season, and Album are collection-only and cannot be used as bumpers, since bumpers are woven into playlists. Set to **None (disabled)** to turn bumpers off. |
| **Bumper order** | How the bumper pool is cycled: **Random** (reshuffled on every refresh), **Name**, or **Release Date**. |
| **Every N items** | How many main items play between bumpers. `1` inserts a bumper after every item, `2` after every second item, and so on. |
| **Bumper rules** | A full rule editor, exactly like the main rules — including multiple rule groups via **Add Bumper Rule Group**. Only items matching these rules are used as bumpers. |

**Steps**:

1. Create or edit a playlist
2. Choose a **Bumper media type** — the remaining bumper controls appear, along with the first bumper rule group
3. Add one or more bumper rules (e.g., `Name contains "bumper"` or `Tags contains "ident"`)
4. Pick a **Bumper order** and set **Every N items**
5. Save — bumpers are woven in on the next refresh

!!! warning "Complete rules required"
    Once a bumper media type is chosen, at least one **complete** bumper rule is required — saving is blocked with a message otherwise. To save without bumpers, set the bumper media type back to **None (disabled)**.

## Worked Example

A "TV channel" playlist over three shows (Round Robin, Group By: Series Name) with a bumper pool of **4** home videos, **Every N items** set to `1`:

| Position | Item |
|----------|------|
| 1 | Show A - S01E01 |
| 2 | Bumper 1 |
| 3 | Show B - S01E01 |
| 4 | Bumper 2 |
| 5 | Show C - S01E01 |
| 6 | Bumper 3 |
| 7 | Show A - S01E02 |
| 8 | Bumper 4 |
| 9 | Show B - S01E02 |
| 10 | Bumper 1 *(pool wraps around)* |
| 11 | Show C - S01E02 |

There are more gaps (5) than bumpers (4), so the pool wraps around and Bumper 1 plays again. No bumper follows the final episode.

## Behavior Notes

- **Limits**: Bumpers do **not** count toward [Max Items or Max Playtime](sorting-and-limits.md#limits) — those limits apply to the main content only. A playlist with Max Items 50 contains 50 main items *plus* bumpers. The playlist's displayed item count and total runtime do include bumpers, since they are real playlist content.
- **Main content wins**: An item that matches both the main rules and the bumper rules is treated as main content only — it is never also inserted as a bumper.
- **Empty pool**: If the bumper rules match no items (or every match was claimed by the main rules — see below), the playlist refreshes normally without bumpers. The server log records the reason (`grep -i bumper` in the Jellyfin log).
- **Extras always included**: The bumper pool always includes [extras](media-types.md#extras-special-features) (trailers, interstitials, theme videos, etc.) — trailers are classic bumper material. This is independent of the playlist's own **Include Extras** setting, and no checkbox is needed for the bumper rules. Use a bumper rule like `Extra Type` = Trailer to build the pool from extras, or narrow it with any other rules.
- **Random order**: With **Bumper order** set to Random, the pool is reshuffled on every refresh, so the bumper rotation changes each time.

!!! warning "Keep the bumper rules and main rules disjoint"
    Because main content wins, bumpers silently disappear when your main rules also match your bumper items — each overlapping item becomes ordinary playlist content and is removed from the bumper pool. This is easy to hit when the bumper media type is the **same** as the playlist's (e.g., an Episode playlist with broad genre rules and Episode bumpers): the main rules swallow the whole pool and the playlist refreshes with no bumpers at all.

    Two easy ways to avoid it:

    - Use a bumper media type the playlist doesn't include (the classic setup: **Video** clips as bumpers between **Episodes**) — overlap becomes impossible.
    - Or explicitly exclude the bumper content from the main rules, e.g. main rule `Series Name` `does not contain` `"bumper"`.

!!! tip "Building a bumper pool"
    Put your bumper clips (station idents, retro commercials, intros) in a Home Videos library and tag them or give them a common name prefix, then match them with a single rule like `Name contains "bumper"`.

!!! info "See Also"
    - [TV Channel with Commercials (Bumpers)](../examples/common-use-cases.md#tv-channel-with-commercials-bumpers) — full example configuration
    - [Round Robin sorting](sorting-and-limits.md#round-robin-interleave) — interleaved "TV channel" episode ordering that pairs well with bumpers
