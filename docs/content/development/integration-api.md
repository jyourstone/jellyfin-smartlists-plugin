# Integration API

SmartLists exposes a REST API on your Jellyfin server. It is the same API the plugin's own
configuration pages use, and it is the supported way for external scripts, automations and other
Jellyfin plugins to create, refresh and inspect smart lists.

Typical uses:

- Provisioning lists from a script or config-management tool instead of clicking through the UI
- Triggering a refresh from an external trigger (a download client finishing, a cron job, a webhook)
- Reading refresh progress and history into a dashboard
- Another Jellyfin plugin creating lists on the user's behalf

## Stability

!!! important "What is and isn't covered"
    The endpoints listed under [Endpoint reference](#endpoint-reference) are the plugin's public
    integration contract as of the **12.0.x** release line. They will not change shape without a
    note in the release changelog.

    **Everything else is UI plumbing and is not part of the contract.** That includes
    `/Plugins/SmartLists/currentuser`, every `/Plugins/SmartLists/User/*` route, the
    `/Plugins/SmartLists/backups*` routes, `/Plugins/SmartLists/Timer/*`, the image upload and
    delete routes, and `/Plugins/SmartLists/Pages/UserPlaylists`. They exist to serve the admin and
    user configuration pages, they change whenever those pages change, and they may be removed or
    reshaped in any release. Don't build against them.

To check which plugin version a server is running, call Jellyfin's own `GET /Plugins` endpoint and
look for the SmartLists entry.

## Authentication

Every endpoint requires authentication. The simplest credential for an integration is a Jellyfin
API key.

### Creating an API key

1. In Jellyfin, go to **Dashboard → Advanced → API Keys**
2. Click **+** and give the key a name (e.g. `smartlists-automation`)
3. Copy the generated key

### Sending the key

Jellyfin expects the key in an `Authorization` header:

```
Authorization: MediaBrowser Token="YOUR_API_KEY"
```

The quotes around the token are part of the header value.

!!! warning "`?api_key=` no longer works"
    The legacy `?api_key=YOUR_KEY` query parameter returns **401 Unauthorized** on Jellyfin 12.
    Use the `Authorization` header.

### Elevation and user context

All endpoints in the reference below are declared `[Authorize(Policy = "RequiresElevation")]` —
they require an **administrator**. An API key satisfies that policy, so an API key is enough for
everything documented here.

There is one important difference between an API key and a logged-in admin's access token: an API
key carries **no user identity**. Jellyfin issues no `Jellyfin-UserId` claim for it, so the plugin
cannot infer "the current user" from the request.

!!! warning "Collections need an explicit owner when you use an API key"
    A smart collection has an owner whose watch state, favourites and play counts are used to
    evaluate user-specific rules. With a logged-in admin token the plugin defaults that owner to
    the caller. With an API key there is no caller to default to.

    **Creating:** send **`UserId`** (preferred) or **`CreatedByUserId`** in the request body.
    There is no existing owner to fall back on, so one of them is required.

    **Updating:** both are optional. Omitting `UserId` means "leave ownership alone" — the stored
    owner is kept. Send `UserId` only when you actually intend to reassign the collection. An owner
    is required again only if the stored one is missing or points at a deleted user.

    When no owner can be determined you get:

    ```json
    {
      "title": "Validation Error",
      "status": 400,
      "detail": "Collection owner could not be determined. Supply 'UserId' (or 'CreatedByUserId') in the request body when calling with an API key."
    }
    ```

    Get valid user IDs from [`GET /Plugins/SmartLists/users`](#reference-data).

Smart **playlists** are unaffected — they carry their owners explicitly in `UserPlaylists`, so
they work identically with either credential.

## OpenAPI

Jellyfin publishes a machine-readable schema at:

```
/api-docs/openapi.json
```

The SmartLists routes appear in that document alongside the core Jellyfin API, so you can generate
a typed client:

```bash
openapi-generator-cli generate \
  -i http://localhost:8096/api-docs/openapi.json \
  -g python \
  -o ./jellyfin-client
```

!!! note "Fixed in this release"
    Earlier plugin versions broke the whole document: four multipart upload actions declared their
    `IFormFile` parameter as `[FromForm] IFormFile`, which Swashbuckle rejects. The schema generator
    threw and returned HTTP 500 for `/api-docs/openapi.json` **server-wide**, not just for the
    plugin's routes. That is fixed. If you get a 500 from that URL, you are on an older build —
    upgrade the plugin.

    Contributors adding a multipart endpoint: bind the file as a bare `IFormFile` parameter, never
    `[FromForm] IFormFile`. MVC already binds `IFormFile` from the multipart body via
    `BindingSourceMetadataProvider`, so the attribute changes nothing at runtime — it only downgrades
    the binding source from `BindingSource.FormFile` to `BindingSource.Form`, which is the exact
    combination the schema generator throws on. Add `[Consumes("multipart/form-data")]` too, so the
    generated schema declares the right request shape.

The generated schema is thin on error bodies (most error statuses are declared without a schema).
Treat the [Error format](#error-format) section below as authoritative for those.

## Endpoint reference

All routes are relative to your Jellyfin server root, e.g.
`http://localhost:8096/Plugins/SmartLists`.

### Lists

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/Plugins/SmartLists` | All smart lists. Optional `?type=Playlist` or `?type=Collection` (case-insensitive). |
| `GET` | `/Plugins/SmartLists/{id}` | A single smart list by ID. Looks in playlists first, then collections. |
| `POST` | `/Plugins/SmartLists` | Create a list. Returns `201` with the stored list. Optional `?skipRefresh=true`. |
| `PUT` | `/Plugins/SmartLists/{id}` | Replace a list. Returns `200` with the stored list. Optional `?skipRefresh=true`. |
| `DELETE` | `/Plugins/SmartLists/{id}` | Delete a list. Returns `204`. Optional `?deleteJellyfinList=false`. |

!!! warning "Query and body gotchas"
    - **`?type=` is not validated.** Any value that is not exactly `Playlist` or `Collection`
      returns `200` with an **empty array** rather than a `400`. `?type=playlists` (plural)
      silently returns nothing.
    - **`Type` is required** in the body on both `POST` and `PUT`. Send `"Playlist"` or
      `"Collection"` (the numeric forms `0` and `1` also work). Omitting it fails deserialization
      and returns a `400` with `errors["$"]`.
    - **`PUT` with a different `Type` converts the list.** Sending `"Type": "Collection"` for an ID
      that is currently a playlist deletes the Jellyfin playlist and rebuilds it as a collection
      under the same smart-list ID. This is a destructive operation, not a validation error.
    - **`DELETE` defaults to `deleteJellyfinList=true`**, which removes the real Jellyfin
      playlist/collection as well as the smart-list configuration and any uploaded images. Pass
      `?deleteJellyfinList=false` to keep the Jellyfin object — it stays behind, minus the
      `[Smart]` suffix.
    - **`skipRefresh=true`** stores the configuration without queueing a refresh. Useful when you
      are about to make several writes and want a single refresh at the end.

### Actions

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/Plugins/SmartLists/{id}/enable` | Enable a list, then queue a refresh. |
| `POST` | `/Plugins/SmartLists/{id}/disable` | Disable a list and remove its Jellyfin playlist/collection. |
| `POST` | `/Plugins/SmartLists/{id}/refresh` | Refresh one list. **Synchronous** — returns when done. |
| `POST` | `/Plugins/SmartLists/refresh` | Refresh all **playlists**. |
| `POST` | `/Plugins/SmartLists/refresh-direct` | Refresh all playlists **and** collections. |

All five return `200` with `{ "message": "..." }`.

!!! note "Refresh behaviour"
    - `POST /{id}/refresh` blocks until the refresh completes. Expect a long-running request for a
      large library, and set your client timeout accordingly.
    - `POST /refresh` and `POST /refresh-direct` return **409 Conflict** when a refresh is already
      running. Use **`refresh-direct`** for "refresh everything" — plain `refresh` skips
      collections.
    - Refreshing a disabled list returns `400`. Enable it first.

### Reference data

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/Plugins/SmartLists/fields` | Every rule field, operator, field→operator map and sort option. |
| `GET` | `/Plugins/SmartLists/users` | `[{ "Id", "Name" }]` — Jellyfin users, for `UserId` values. |
| `GET` | `/Plugins/SmartLists/libraries` | `[{ "Id", "Name", "CollectionType" }]` — libraries, for `LibraryName` rules. |

`GET /fields` returns an object with these keys, each a `[{ "Value", "Label" }]` array unless noted:

`ContentFields`, `VideoFields`, `AudioFields`, `RatingsPlaybackFields`, `FileFields`,
`LibraryFields`, `PeopleFields`, `PeopleSubFields`, `CollectionFields`,
`SimilarityComparisonFields`, `Operators`, `FieldOperators`
(field name → allowed operator names), `OrderOptions`.

`Operators` is the full operator catalogue, in the same `[{ "Value", "Label" }]` shape as the field
lists — `Value` is what you put in a rule's `Operator`, `Label` is the UI wording.

`FieldOperators` is the authoritative answer to "which operators may I use with this field".

### Status

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/Plugins/SmartLists/Status` | `{ ongoingOperations, history, statistics }`. |
| `GET` | `/Plugins/SmartLists/Status/History` | The last refresh for each list. |
| `GET` | `/Plugins/SmartLists/Status/Ongoing` | Refreshes in flight right now. |

An entry in `ongoingOperations` (and in `Status/Ongoing`) contains:

`listId`, `listName`, `listType`, `triggerType`, `startTime` (ISO 8601),
`totalItems`, `processedItems`, `estimatedTimeRemaining` (seconds), `elapsedTime` (seconds),
`errorMessage`, `batchCurrentIndex`, `batchTotalCount`.

A `history` entry contains `listId`, `listName`, `listType`, `triggerType`, `startTime`,
`endTime`, `duration` (seconds), `success`, `errorMessage`.

`statistics` contains `totalLists`, `ongoingOperationsCount`, `queuedOperationsCount`,
`lastRefreshTime`, `averageRefreshDuration` (seconds), `successfulRefreshes`, `failedRefreshes`.

!!! tip "Where refresh failures surface"
    A list can be created successfully and still never produce any items — for example if
    `MediaTypes` is empty. The API returns `201` either way; the failure only appears in the
    Jellyfin log and in `GET /Plugins/SmartLists/Status/History`. Check history after your first
    refresh.

## Worked examples

The examples use these shell variables:

```bash
JF="http://localhost:8096"
KEY="your-api-key"
AUTH="Authorization: MediaBrowser Token=\"$KEY\""
```

### 1. Find a user ID

Both playlists and collections need at least one Jellyfin user.

```bash
curl -sS "$JF/Plugins/SmartLists/users" -H "$AUTH"
```

```json
[
  { "Id": "d7fb16f13aea4aa79942dcc7cf22c655", "Name": "alice" }
]
```

!!! note "GUID formats differ across the API"
    User IDs come back **without dashes**. Smart-list IDs are returned **with** dashes. Both forms
    parse on input, but string-comparing an ID from one endpoint against an ID from another will
    fail — normalize before comparing.

### 2. Create a smart playlist

`Type`, `Name` and at least one owner in `UserPlaylists` are enforced by the API. `MediaTypes` is
not enforced, but a list without it can never refresh — treat it as required:

```bash
curl -sS -X POST "$JF/Plugins/SmartLists" \
  -H "$AUTH" -H "Content-Type: application/json" \
  -d '{
    "Type": "Playlist",
    "Name": "My Smart Playlist",
    "MediaTypes": ["Movie"],
    "UserPlaylists": [{ "UserId": "d7fb16f13aea4aa79942dcc7cf22c655" }]
  }'
```

A more realistic body, with rules, sorting and a cap:

```json
{
  "Type": "Playlist",
  "Name": "Recent Highly Rated Movies",
  "MediaTypes": ["Movie"],
  "UserPlaylists": [{ "UserId": "d7fb16f13aea4aa79942dcc7cf22c655" }],
  "ExpressionSets": [
    {
      "Expressions": [
        { "MemberName": "ProductionYear", "Operator": "GreaterThanOrEqual", "TargetValue": "2020" },
        { "MemberName": "CommunityRating", "Operator": "GreaterThan", "TargetValue": "7.5" }
      ]
    }
  ],
  "Order": { "SortOptions": [{ "SortBy": "ReleaseDate", "SortOrder": "Descending" }] },
  "MaxItems": 50,
  "AutoRefresh": "OnLibraryChanges"
}
```

The response is `201 Created` with the stored list, including the server-generated `Id`.

### 3. Create a smart collection

Collections take a single `UserId` instead of `UserPlaylists`. **With an API key this field is
required** — see [Elevation and user context](#elevation-and-user-context).

```bash
curl -sS -X POST "$JF/Plugins/SmartLists" \
  -H "$AUTH" -H "Content-Type: application/json" \
  -d '{
    "Type": "Collection",
    "Name": "My Smart Collection",
    "MediaTypes": ["Movie"],
    "UserId": "d7fb16f13aea4aa79942dcc7cf22c655"
  }'
```

!!! warning "Collection names must be unique"
    Jellyfin keys collections by name. Creating a second collection whose formatted name collides
    with an existing one returns `400`. Playlists have no such restriction.

### 4. Trigger a refresh and poll for progress

Single list, synchronous — this call returns only once the refresh has finished:

```bash
LIST_ID="3fa85f64-5717-4562-b3fc-2c963f66afa6"
curl -sS -X POST "$JF/Plugins/SmartLists/$LIST_ID/refresh" -H "$AUTH"
```

Everything, playlists and collections:

```bash
curl -sS -X POST "$JF/Plugins/SmartLists/refresh-direct" -H "$AUTH"
```

Because `refresh-direct` can take a while, poll progress from a second connection:

```bash
curl -sS "$JF/Plugins/SmartLists/Status/Ongoing" -H "$AUTH"
```

```json
[
  {
    "listId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "listName": "Recent Highly Rated Movies",
    "listType": "Playlist",
    "triggerType": "Manual",
    "startTime": "2026-01-01T12:00:00.0000000Z",
    "totalItems": 1200,
    "processedItems": 640,
    "estimatedTimeRemaining": 4.2,
    "elapsedTime": 7.9
  }
]
```

When the array is empty, nothing is running. Confirm the outcome in history:

```bash
curl -sS "$JF/Plugins/SmartLists/Status/History" -H "$AUTH"
```

### 5. Delete

Remove the smart list **and** the Jellyfin playlist/collection it manages (the default):

```bash
curl -sS -X DELETE "$JF/Plugins/SmartLists/$LIST_ID" -H "$AUTH"
```

Remove only the SmartLists configuration, leaving the Jellyfin playlist/collection in place as a
plain, no longer managed list:

```bash
curl -sS -X DELETE "$JF/Plugins/SmartLists/$LIST_ID?deleteJellyfinList=false" -H "$AUTH"
```

Both return `204 No Content`.

## Rules, fields and operators

Rules are not documented again here. The field names, their allowed operators and their value
formats are in **[Fields and Operators](../user-guide/fields-and-operators.md)** — use the
**JSON name** column for `MemberName`.

For the live, authoritative list as your server actually has it, call
`GET /Plugins/SmartLists/fields` (see [Reference data](#reference-data)).

A few rule facts that bite integrators:

- **Rule logic is structural, not a field.** Expressions inside one `ExpressionSet` are ANDed;
  separate `ExpressionSets` are ORed. There is no `RuleLogic` property to send.
- **`TargetValue` is always a string**, including numbers and booleans (`"2020"`, `"true"`).
- **`IsIn` / `IsNotIn` take a semicolon-separated string**, not a JSON array:
  `"TargetValue": "Action;Comedy"`. Matching is case-insensitive and by substring, so `"Act"`
  matches `"Action"`. Commas are not separators.
- **`NewerThan` / `OlderThan` need `number:unit`**, with unit one of `hours`, `days`, `weeks`,
  `months`, `years` — e.g. `"3:days"`. `Weekday` takes an integer `0`–`6` (`0` = Sunday).
- **Unknown media types are silently dropped.** `"MediaTypes": ["Film"]` returns `201` with
  `MediaTypes: []`, and the list can then never refresh. Validate against
  [Media Types](../user-guide/media-types.md) before sending.
- **`SortBy` uses a different vocabulary from `OrderOptions`.** `OrderOptions` from `/fields` are
  legacy combined strings like `"ReleaseDate Descending"`; `SortOptions` splits that into
  `"SortBy": "ReleaseDate"` plus `"SortOrder": "Descending"`. An unrecognised `SortBy` silently
  falls back to no sorting rather than erroring.
- **Legacy fields are migrated on read.** A stored `IsPlayed` rule is returned as a
  `PlaybackStatus` rule, so a rule you wrote may not be the rule you read back.
- **Null values are omitted from responses.** An absent field means "unset", not `false`/`0`/`[]`.

## Error format

Every error body the plugin's controllers produce is
[RFC 7807](https://datatracker.ietf.org/doc/html/rfc7807) `ProblemDetails`:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Bad Request",
  "status": 400,
  "detail": "Invalid list ID format",
  "traceId": "00-..."
}
```

**Read `detail` for the human-readable message.** The HTTP status code always matches `status`.

Request-body validation failures additionally carry an `errors` object
(`ValidationProblemDetails`) with per-field messages. A missing `Type` discriminator, for example,
comes back as:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "$": ["Smart list JSON is missing required Type property."]
  }
}
```

!!! note "Three responses this does not cover"
    Normalization happens inside the plugin's controllers, so responses produced before or outside
    them keep their own shape. A robust client should also tolerate:

    - a bare **`401`/`403`** with an empty body, from Jellyfin's authorization layer
    - a plain-text **`500`** (`Error processing request.`) from Jellyfin's exception middleware
    - Jellyfin's own core error bodies on core routes

Success responses are unaffected: action endpoints still return `{ "message": "..." }` on `200`.

## For Jellyfin plugin developers

If you are writing another Jellyfin plugin and want to create or refresh smart lists, the answer
is **call the REST API over loopback**. Referencing SmartLists' types directly does not work, and
the reason is worth understanding before you try.

### Why you cannot share types with this plugin

Jellyfin loads each plugin into its **own `AssemblyLoadContext`**.
`Emby.Server.Implementations/Plugins/PluginManager.cs` creates
`new PluginLoadContext(plugin.Path)` once per plugin, and `PluginLoadContext` resolves assemblies
through an `AssemblyDependencyResolver` scoped to that plugin's own folder, returning `null` — and
so deferring to the Default context, which holds Jellyfin core and the BCL — when the assembly is
not found there.

The consequence for cross-plugin type sharing:

- **Ship `Jellyfin.Plugin.SmartLists.dll` alongside your plugin** and your load context resolves it
  from *your* folder. You get a second, independent copy of every type. `SmartPlaylistDto` from
  your copy is a different `Type` from `SmartPlaylistDto` in ours, so a cast fails and
  `GetRequiredService<ISmartListService>()` will not match our DI registration.
- **Don't ship it**, and your context finds it in neither your folder nor the Default context, so
  the reference fails to resolve at all.

The DI container *is* shared across plugins. Assembly type identity is not, and DI resolution is
keyed on type identity. There is no arrangement of references that makes this work.

### Self-provisioning an API key

A plugin does not have to ask the user to paste in a key. `IAuthenticationManager` from
`MediaBrowser.Controller.Security` is injectable, and can create one at startup:

```csharp
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Security;

public sealed class SmartListsClientFactory(
    IAuthenticationManager authenticationManager,
    IServerApplicationHost appHost)
{
    private const string KeyName = "My Plugin (SmartLists integration)";

    public async Task<HttpClient> CreateAsync()
    {
        // CreateApiKey returns Task, not the key itself - read it back by name.
        var keys = await authenticationManager.GetApiKeys().ConfigureAwait(false);
        var key = keys.FirstOrDefault(k => string.Equals(k.AppName, KeyName, StringComparison.Ordinal));

        if (key is null)
        {
            await authenticationManager.CreateApiKey(KeyName).ConfigureAwait(false);
            keys = await authenticationManager.GetApiKeys().ConfigureAwait(false);
            key = keys.First(k => string.Equals(k.AppName, KeyName, StringComparison.Ordinal));
        }

        // Honours a configured base URL path; no trailing slash.
        var baseUrl = appHost.GetApiUrlForLocalAccess(IPAddress.Loopback, allowHttps: false);

        var client = new HttpClient { BaseAddress = new Uri(baseUrl + "/") };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("MediaBrowser", $"Token=\"{key.AccessToken}\"");

        return client;
    }
}
```

From there, `client.PostAsJsonAsync("Plugins/SmartLists", body)` behaves exactly like the curl
examples above — including the requirement to send an explicit `UserId` when creating a
collection, since an API key has no user identity.

!!! tip "Be a good citizen"
    Name the key after your plugin so the server owner can see what it is in **Dashboard →
    Advanced → API Keys**, and delete it with `IAuthenticationManager.DeleteApiKey(accessToken)`
    when your plugin is uninstalled.

### An in-process API?

An in-process typed facade — a small interface-only package both plugins could reference, loaded
from the Default context — is possible, but it is only worth building if people actually want it.
If you have a use case the REST API handles badly, open an issue on
[GitHub](https://github.com/jyourstone/jellyfin-smartlists-plugin/issues) describing it.
