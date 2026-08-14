---
name: release
description: Create and push a release tag for the SmartLists plugin with a changelog in the annotated tag message. Project-specific version scheme — RC number lives in the Revision segment with a bare -rc suffix (e.g. v12.0.0.3-rc); stable releases bump the Build segment and reset Revision to 0 (e.g. v12.0.1.0). Use when the user wants to tag a release or create an RC.
argument-hint: stable|rc
allowed-tools: Bash
---

# Release Skill

Create a new git version tag with a changelog generated from commits and PR titles since the last relevant tag, then push it to the remote. This plugin uses .NET's four-part `Major.Minor.Build.Revision` version scheme, not SemVer, and ships a single `12.x` release line: RCs are tagged on `main`, stables on `12-release`.

## Steps

### 1. Validate input

`$ARGUMENTS` must be `stable` or `rc`. `patch` and `minor` are accepted as synonyms for `stable` — the four-part scheme doesn't distinguish bump sizes, so every stable is a Build bump regardless of how large the change is.

If `$ARGUMENTS` is `major`, stop and explain: the Major/Minor segments are pinned to the `12.x` line and only move as a deliberate manual decision, not something this skill infers from a bump type.

**If `$ARGUMENTS` is empty**, present this prompt and wait for the user's answer before proceeding:

```
What type of release do you want to create?

  stable — Stable release (tagged on main, e.g. v12.0.1.0)
  rc     — Release candidate (tagged on main, e.g. v12.0.0.3-rc)
```

Use the user's answer as `$ARGUMENTS` and continue. If `$ARGUMENTS` is present but doesn't match `stable`, `patch`, `minor`, or `rc`, stop and tell the user: "Usage: /release stable|rc".

### 2. Sync and check working tree

First check the current branch:

```bash
git branch --show-current
```

There is a single `12.x` release line (see "Release Line" in `CLAUDE.md`). The correct branch depends on release type:

- `rc` → must be on `main`.
- `stable` → must be on `12-release`, which tracks the last stable and is fast-forwarded to `main` at release time.

The old `10.11-release` branch is retired; never tag from it.

If on the wrong branch, warn and ask for confirmation before continuing:

```
You are on branch '<current>' but <rc/stable> releases are tagged from '<expected>'.
Continue anyway? (yes / no)
```

Run `git pull` to bring the local branch up to date, then `git fetch --tags --prune-tags --force` so the local tag list matches the remote (a plain `git pull` doesn't reliably sync new or deleted tags, and a stale tag list would corrupt the version calculation). If the pull fails, stop and show the error — do not proceed with a stale or diverged branch.

#### For `stable` releases: work out what will ship

`12-release` sits at the previous stable and has to be fast-forwarded to `main` so the release actually contains the new work. **Do not fast-forward yet** — compute the delta here, show it, and only move the branch after the user confirms in step 7. Nothing destructive happens before that gate.

`git pull` while standing on `12-release` updates `12-release`, **not** `origin/main`, so fetch main explicitly or the delta is computed against a stale ref:

```bash
git fetch origin main
```

Check a fast-forward is even possible:

```bash
git merge-base --is-ancestor HEAD origin/main && echo FF_OK || echo DIVERGED
```

`DIVERGED` means `12-release` has commits of its own and is no longer a pointer at a `main` commit. **Stop.** Do not merge, do not force. Report it — recovery means resetting `12-release` back onto a `main` commit, which is the user's call.

Then compute what the release will newly contain:

```bash
git log HEAD..origin/main --oneline --no-merges
git rev-parse --short HEAD origin/main
```

- **Non-empty** → this is the set of changes the stable will ship. Carry it into the step-7 summary. Because `12-release` sits at the previous stable tag, this range is the same one the changelog is generated from in step 3 — compute it once, show it once, don't prompt twice.
- **Empty** → `12-release` already equals `main`; there is nothing new to release. Warn and ask before continuing, since re-tagging an identical tree is almost always a mistake:

  ```
  12-release is already up to date with main — this release would contain no new commits.
  Re-tag the same tree anyway? (yes / no)
  ```

**Releasing a subset of `main`:** if the user wants to ship only part of what's on `main` (e.g. a feature merged but not yet smoke-tested should wait), fast-forward to a specific commit rather than main's tip — `git merge --ff-only <sha>` in step 8, with the delta and changelog computed against that `<sha>` instead of `origin/main` throughout. Ask for the target commit and confirm the reduced set.

Then check for uncommitted changes:

```bash
git status --porcelain
```

If there are uncommitted changes, show the user a summary and ask:

```
You have uncommitted changes:
  <list of changed files>

Auto-generate a commit message and commit? (yes / no)
```

- **yes** → analyze the diff (`git diff` and `git diff --staged`), generate a concise commit message, then stage, commit, and push:
  ```bash
  git add -A
  git commit -m "<auto-generated message>"
  git push
  ```
  Confirm: "✓ Committed and pushed changes: <commit message>"
- **no** → abort, inform the user no release was created.

### 3. Gather context

Determine the changelog base tag depending on release type:

```bash
# rc: highest v12.* tag of any kind (stable or RC)
git tag --list 'v12.*' --sort=-v:refname | head -1

# stable: highest stable v12.*.0 tag — exclude RCs, since the changelog for a
# stable covers everything since the previous STABLE, not since the last RC
git tag --list 'v12.*.0' --sort=-v:refname | grep -v -- '-rc$' | head -1
```

Then collect commits and PR titles since that base:

```bash
CHANGELOG_BASE="<tag from above, may be empty>"

# Commits since changelog base (or all commits if no previous tag)
if [ -z "$CHANGELOG_BASE" ]; then
  git log --oneline --no-merges
else
  git log ${CHANGELOG_BASE}..HEAD --oneline --no-merges
fi

# PR titles merged since changelog base (requires gh CLI — skip gracefully if unavailable)
# Note: %cI gives strict ISO 8601 with no spaces — required, because the default
# %ci format contains spaces that break the gh search query.
if command -v gh &>/dev/null; then
  CHANGELOG_BASE_DATE=$(git log -1 --format=%cI "${CHANGELOG_BASE}" 2>/dev/null)
  if [ -n "$CHANGELOG_BASE_DATE" ]; then
    gh pr list --state merged --search "merged:>${CHANGELOG_BASE_DATE}" --json number,title,mergedAt --limit 50
  fi
fi
```

If there are no commits since the changelog base, stop and tell the user: "Nothing to release."

### 4. Calculate next version

The version scheme is .NET's four-part `Major.Minor.Build.Revision`. Ordering is purely numeric, left to right — there are no pre-release labels baked into the .NET version itself.

- **Revision > 0** with a bare `-rc` git-tag suffix marks a release candidate; the Revision number *is* the RC number. The `-rc` suffix exists only as a workflow routing marker (it sends the build to the unstable manifest branch and marks the GitHub release as a prerelease) — it is never part of the actual .NET version.
- **Never** produce `-rc.N`-style suffixes (e.g. `v12.0.0.1-rc.2`). Two such tags would strip to the identical manifest version `12.0.0.1` and break plugin auto-update ordering. Each RC increments the Revision segment itself.

**`rc`**: base = highest `v12.*` tag (from step 3).
- If the base is itself an RC (ends in `-rc`), increment Revision: `v12.0.0.2-rc` → `v12.0.0.3-rc`.
- If the base is a stable v12 tag (a future state, once Jellyfin 12 has shipped and this line has stable releases), start a new RC cycle: bump Build, set Revision to 1: `v12.0.1.0` → `v12.0.2.1-rc`.
- If there is no `v12.*` tag yet, ask the user for the starting Major.Minor before proceeding — this skill has no basis to guess it.

**`stable`**: base = highest stable `v12.*.0` tag (RCs excluded — see step 3).
- Bump Build, Revision resets to 0. From the highest `v12.*` tag *of any kind*: `v12.0.0.3-rc` → `v12.0.1.0`; `v12.0.1.0` → `v12.0.2.0`.
- Never put a hotfix counter in Revision — Revision is reserved exclusively for RC numbers.
- If no stable `v12.*.0` tag exists yet, the first stable closes out the current RC cycle: take the highest `v12.*-rc` tag, bump Build, set Revision to 0.

### 5. Generate changelog

Write a concise, human-readable changelog using the commits and PR titles collected in step 3.

Guidelines:
- Group changes into categories where it makes sense: **Features**, **Bug Fixes**, **Improvements** — but only include categories that have entries.
- Prefer PR titles over raw commit messages when both refer to the same change (PRs are usually better worded).
- Skip noise: merge commits, version bumps, "fix typo", "WIP", etc.
- **Only include application-facing changes**: omit commits that only touch repo infrastructure — `.github/` workflows, `.gitignore`, linter configs, docs-only changes (`docs/`, `README`, `CLAUDE.md`), or other housekeeping that doesn't affect the running plugin.
- **For stable releases**: omit bug fixes that were fixing issues in the new features added during that same RC cycle. Those are internal RC iteration details, not user-facing changes. Only include the features, improvements, and unrelated bug fixes that matter to users upgrading from the previous stable release.
- Keep each line short and punchy — this is a tag message, not a novel.
- Use plain text, not markdown (git tag messages render as plain text).
- The changelog is shown to end users (GitHub release + Jellyfin plugin catalog), so keep the wording non-technical and focused on what changed for them.

Format:
```
Features:
- ...

Bug Fixes:
- ...

Improvements:
- ...
```

Do not include a title line here — that's assembled separately in step 6.

### 6. Tag message format — critical

The release workflow reads the changelog with `git tag -l --format='%(contents:body)'`, which returns everything **after the first blank line** of the tag message. That body is published verbatim as both the GitHub release body and the Jellyfin plugin-manifest changelog entry.

This means the tag message must be structured as:

```
Release <version>

<changelog body from step 5>
```

For RCs, use "Release candidate <version>" instead of "Release <version>" as the first line.

The first line and the blank line beneath it are stripped before publishing — so the changelog body from step 5 must stand alone and make sense without that first line. Do not fold the title into the body, and do not omit the blank line separator.

### 7. Show summary and ask for confirmation

Display to the user:

```
New tag:    <new-version>
Previous:   <changelog-base-tag> (or "none")
Commits:    <count> commit(s) since previous tag

Changelog:
-----------
<full tag message text, including "Release <version>" header>
-----------

Ready to create and push this tag? (yes / no / edit)
```

**For `stable` releases**, insert the branch move above the changelog — this is the substantive part of the confirmation, since advancing `12-release` is what decides the release contents *and* what the docs site will serve:

```
Branch:     12-release <old-sha> -> <new-sha> (fast-forward to main, <count> commits)
            will be pushed, so the mkdocs Cloudflare Worker picks up the new docs
```

- **yes** → proceed to step 8
- **no** → abort, inform the user no tag was created
- **edit** → ask the user to provide the revised changelog, then re-show the summary and ask again

### 8. Create and push the tag

**For `stable` releases, advance and push `12-release` first**, so the tag lands on the released commit and the docs site follows:

```bash
git merge --ff-only origin/main     # or the specific <sha> if releasing a subset
git push origin 12-release
```

Pushing the branch is **not optional**. The mkdocs Cloudflare Worker publishes from `12-release`; if the branch moves only locally, the tag ships but the docs site keeps serving the previous release's content with no visible error. If either command fails, stop before tagging — a tag on an unadvanced branch would point at the old tree.

Then write the changelog text to a temporary file and use `-F` — passing a multi-line message with embedded quotes/backticks through `-m "..."` is a shell-quoting accident waiting to happen:

```bash
# Write tag message to a temp file
CHANGELOG_FILE=$(mktemp)
cat > "$CHANGELOG_FILE" <<'CHANGELOG_EOF'
Release <version>

<changelog body>
CHANGELOG_EOF

# Create annotated tag with the message
git tag -a <new-version> -F "$CHANGELOG_FILE"
rm -f "$CHANGELOG_FILE"

# Push tag to remote
git push origin <new-version>
```

Confirm success: "✓ Tagged and pushed <new-version>"

If any command fails, show the error output and stop — do not attempt to clean up automatically. If the tag was created locally but the push failed, tell the user the tag exists locally and can be removed with `git tag -d <new-version>` or pushed manually once the issue is resolved.

For `stable`, if the branch push succeeded but the tag push failed, say so explicitly: `12-release` is already published at the new commit, so the docs site has updated but no release was cut. Re-pushing the tag is all that is needed — do not roll the branch back.
