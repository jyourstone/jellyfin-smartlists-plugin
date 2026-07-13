---
name: verify
description: Drive the SmartLists plugin against the local dev Jellyfin container to verify a change end-to-end. Use after building any plugin change that has runtime behavior.
---

# Verifying SmartLists changes against the dev container

## Build + deploy (from repo root; works from worktrees too)

```bash
# ALWAYS --no-incremental: incremental deploy builds have served stale DLLs
dotnet build Jellyfin.Plugin.SmartLists/Jellyfin.Plugin.SmartLists.csproj \
  --framework net10.0 --configuration Release --no-incremental \
  -o <MAIN_CHECKOUT>/build_output /p:Version=12.0.0.0 /p:AssemblyVersion=12.0.0.0
docker restart jellyfin
```

`<MAIN_CHECKOUT>/build_output` is bind-mounted to `/config/plugins/SmartLists` (see dev/docker-compose.yml). Alternatively `cd dev && ./build-local.sh` does the full cycle from the main checkout.

After a code change, confirm the deployed DLL actually contains it — .NET string literals are UTF-16, ASCII `strings` misses them:

```js
const buf = require('fs').readFileSync('.../build_output/Jellyfin.Plugin.SmartLists.dll');
buf.toString('utf16le').includes('some new log message')  // check odd offset too: buf.slice(1)
```

## Drive the API

- Wait ready: `until curl -s -o /dev/null -w "%{http_code}" http://localhost:8096/health | grep -q 200; do sleep 2; done`
- API key: `sqlite3 "file:<MAIN>/dev/jellyfin-data/config/data/jellyfin.db?mode=ro" "SELECT AccessToken FROM ApiKeys"` (dev-only key)
- Auth header (query-param api_key returns 401): `Authorization: MediaBrowser Token="<key>"`
- Endpoints: `GET /Plugins/SmartLists` (list), `POST /Plugins/SmartLists/{id}/refresh|enable|disable`
- Refresh is queued; wait for `docker logs jellyfin | grep "Completed Refresh operation for list <id>"`

## Observable state

- Plugin DTOs: `<MAIN>/dev/jellyfin-data/config/data/smartlists/{listId}/config.json` (edit only while container stopped — plugin caches DTOs and writes back)
- Jellyfin playlists on disk: `<MAIN>/dev/jellyfin-data/config/data/playlists/<Name> [Smart]*/playlist.xml`
- DB (read-only!): `sqlite3 "file:<MAIN>/dev/jellyfin-data/config/data/jellyfin.db?mode=ro"` — tables `BaseItems`, `BaseItemProviders` (provider-ID tether rows: `ProviderId='SmartLists'`)
- Logs: `docker logs jellyfin 2>&1 | grep -i smart` (debug logging enabled via dev/logging.json)

## Gotchas

- Item IDs are deterministic (type+path hash): deleting and recreating a playlist at the same folder path yields the same item GUID.
- Simulating a stale `JellyfinPlaylistId`: stop container, edit config.json mapping to a bogus GUID, start, trigger refresh.
- Jellyfin core appends `1` to the playlist folder name per collision (`GetTargetPath`) — folder names with trailing 1s are normal on multi-user lists.
