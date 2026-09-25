# First GitHub updater test

Release: `v1.0.0-beta.3`, deliberately published as a prerelease on
`Loomirr/Batcomputer` with the owner's approval. Beta-enabled users can see it.
No fake `99.0.0` version is published. Only the full ZIP and SHA are uploaded for
this first network acceptance; file-catalog payloads are not part of this live test.

## Fixtures and scope

`Tools/Updater/Prepare-GitHubUpdateTest.ps1` creates a new folder with
base/candidate builds, automated/manual copies, isolated settings and KEEP-ME
sentinels. It does not launch windows, publish releases, or change installed mods.
The base is current updater code stamped beta.2, not the historical public beta.2.
This tests the new updater's live network/install path, not a nonexistent updater
in an earlier release. Older public builds require one manual installation first.

After publication, run `base/Batcomputer.exe --verify-updater-github-fixture
<absolute automated folder>`. This uses the fixed production GitHub feed, not a
local proxy, and tests stable filtering, download verification, installation,
execution of the updated bundled runtime, rollback and preservation. It does not
claim GUI restart/notification acceptance; that is the manual test below.

## Manual in-app test

1. Open **manual/Batcomputer.exe**, not your usual installation. It starts as beta.2.
2. If setup opens, leave game/tool paths unset and skip/dismiss it; no game is needed.
3. Open Updates, enable beta releases, Check, and confirm beta.3 is offered.
4. Download. This first live test is the full ZIP, not the smaller changed-file path.
5. Close and reopen Updates: the verified download should still be ready.
6. Restart now and confirm. Verify the app reopens as beta.3 and shows Update complete.
7. Check Generated/KEEP-ME.txt remains, then close/reopen: no repeated completion prompt.

## Release ZIP layout

```text
Batcomputer.exe              only top-level executable
app/                        managed/native DLLs and bundled .NET runtime
Tools/                      helpers and Blender rig-preparation Python script
gamedata/                   reference catalogs
Documentation/             offline guides
licenses/                  third-party license and notice texts
README.md, CHANGELOG.md, LICENSE, THIRD_PARTY_NOTICES.md
Batcomputer.update.json      verified application-file manifest
```

Extract the entire archive to a new folder; do not move only the EXE. Settings and
Generated are created locally, not copied from the developer. GitHub keeps the
version in the release tag, while the updater ZIP's asset name stays constant:
`Batcomputer-update-win-x64.zip` plus `.zip.sha256`.

Later changed-file releases also upload the authenticated `.files.json` catalog,
its SHA and every `bc-file-<hash>.gz` payload. Clients download changed/missing
files only; authors still upload all payloads. The full manual-install ZIP does
not become tiny just because updater downloads can be smaller.
