# Updating Batcomputer

The installed version appears under the Batcomputer logo, in the window title, and in Settings. Click the version, choose **Updates** from the main menu, or use **Settings → Updates**.

1. Choose whether to include beta releases, then **Check for updates**.
2. Review the release notes and download size. Choose **Download**.
3. After verification completes, save your work and choose **Restart now**, then confirm. Batcomputer requests normal shutdown and reopens after installation. Editor closing guards can cancel shutdown; no process is force-closed.
4. Alternatively, choose **Install when I exit** and close every Batcomputer window running from this folder within ten minutes. Closing just the Update center does not install the update. Other app instances must also close before either route can install.

If an editor cancels **Restart now**, finish/save your work and try again within ten minutes. The verified update remains scheduled for normal exit. Restart is unavailable until the download is verified.

After a successful update restart, a one-time **Update complete** prompt shows the installed version. It appears after startup has been confirmed, so waiting to dismiss it does not delay the installer's health check. Ordinary launches do not repeat it. This confirms installation/startup, not that every feature has been tested.

Startup checks are optional and off by default. They only show an update link when a newer compatible package exists; they do not download or install anything. The beta channel includes both beta and stable releases. A stable-only check will not downgrade an installed beta.

The Update center separates release notes from live activity and shows Check / Download / Verify / Restart progress. A verified download remains available if you close and reopen this menu during the same app session. Installation requires closing the whole application, not just the Update center. Startup confirmation refreshes automatically while the menu is open.

Settings, Data, Generated, Runtime, authoring projects, game files, UE4SS and installed mods are not update targets. The portable app stays in its existing directory, so relative workspace paths do not change. Existing application files replaced by an update are backed up. Files not listed in the update are left alone, not deleted.

## Recovery

**Updates → Backups / recovery** opens `.batcomputer-updates` beside the executable. Each transaction has a `status.txt`, verified payload and, after installation starts, a `backup` folder and recovery journal.

If the new app exits before its main window opens, the helper attempts to restore the previous application files. If the app stays running but startup is slow or blocked by a dialog, it is left alone, not killed. Startup confirmation only means the window opened; it is not a complete feature test.

For manual recovery after closing Batcomputer, open the relevant transaction's **helper** folder and double-click **Batcomputer.exe**. This private copy offers to restore that transaction's backup. It refuses to overwrite app files changed since that update. Settings and project data are never rolled back.

If power is lost midway through an update, use that same helper. Its undo journal is written before the first application file is replaced. Keep backups until you are happy with the new version. Cancelled downloads and old backups can be removed manually after all update helpers have exited; they are not automatically purged. Do not remove a transaction while installation or recovery is pending.

## Release author: build an updater package

**Distribution checks:** packaging rejects Oodle/game runtimes and Epic editor DLLs. The release owner chose to retain the authored prebuilt registry writer; only its exact expected tool path is exempted. This is not legal clearance of its distribution terms. Native retoc/ACL helpers are now source-pinned, tested builds with dependency notices. See [the remediation report](../research/native-redistribution-remediation-20260924.md). Do not ship local proprietary runtimes or infer permission from a wrapper's license.

Use a **clean publish folder**, not your working portable installation. Increase the project's `Version`, `InformationalVersion` and `FileVersion` for each published patch. The release tag and executable must describe the same semantic version; for example, `v1.0.0-beta.3` and `1.0.0-beta.3`. Never replace an existing version with different binaries.

```powershell
dotnet publish Batcomputer.csproj -c Release -o artifacts/publish-beta3
# Run an updater-capable Batcomputer, passing absolute paths if outside this directory:
& artifacts/publish-beta3/Batcomputer.exe --create-app-update-files artifacts/publish-beta3 artifacts/update-beta3
```

The command exits nonzero on failure and writes `package-error.txt` in its output folder. In a PowerShell terminal, use `Start-Process -Wait -PassThru` if you need to wait explicitly for this GUI executable's CLI operation. The output directory must be outside the publish folder and must not already contain a package with the same name.

Upload these release assets:

- `Batcomputer-update-win-x64.zip`
- `Batcomputer-update-win-x64.zip.sha256`
- `Batcomputer-update-win-x64.files.json` and its `.sha256`
- Every generated `bc-file-<hash>.gz` payload

The full ZIP supports initial/manual installs and older updaters. The authenticated file catalog lets newer clients select only the changed or missing file payloads. Upload **all** generated payloads for that release; do not upload only the files changed on your own machine, because users may be upgrading from different versions. Release uploads can therefore be larger than the full ZIP alone even though individual clients download much less. Cross-release hosting/deduplication of payloads is not implemented yet. `--create-app-update` still generates a full-ZIP-only package when needed.

The ZIP contains `Batcomputer.update.json` with every application file's size and SHA-256. A loose copy is also generated for inspection but is not required as an uploaded asset. The ZIP is also suitable for a fresh manual extraction. Do not upload local test builds with `99.0.0-updatertest.*` versions.

The client checks only **Loomirr/Batcomputer**. It uses the ZIP digest returned by GitHub's HTTPS release API, then verifies the file manifest and embedded executable version. Redirects are restricted to the repository's release-download path and GitHub's release-asset host. A missing API digest blocks installation. This authenticates against the GitHub release source, **not an independent offline signing key**; compromise of the release account remains a risk. Separately signed manifests are a future hardening step. See [GitHub's release-assets API](https://docs.github.com/en/rest/releases/assets).

Older releases without the exact updater asset name are manual-only. Users need to install an updater-capable build once before in-app updates work.

All versions before 1.0 (including 0.9 betas) are legacy and cannot be offered or downloaded by the updater. 1.0 betas are eligible when beta updates are enabled. Legacy ZIPs remain available manually on GitHub; there is no testing bypass for this rule.

## Test without publishing a GitHub release

From the repository, run:

```powershell
./Tools/Updater/Start-LocalUpdateTest.ps1
```

This builds **two genuine self-contained versions** in a new `artifacts/updater-test-*` folder, creates a local release feed, starts a loopback-only server and opens the first version's Updates window. It does not create tags, publish releases, use your real settings, or install game mods.

For the actual workspace flow instead of the standalone update dialog, use:

```powershell
./Tools/Updater/Start-LocalUpdateTest.ps1 -FullApp -Scenario Slow -Port 8756
```

`-FullApp` opens the normal workshop with disposable settings and dummy game paths. `-Scenario Slow` throttles the ZIP for cancellation testing; `-Scenario Corrupt` advertises a deliberately incorrect checksum and must block installation. Use a fresh output directory and unused port for each fixture. See the [in-app checklist](../research/updater-in-app-checklist.md).

In that window: **Check for updates → Download → Install on exit**, then close the test window. It should restart showing `99.0.0-updatertest.2`. `Generated/KEEP-ME.txt` and the fixture settings must remain unchanged. Close the updated test window and use its transaction's recovery helper to restore `.1`; then reopen the test app to confirm its version.

The local server process ID is printed and saved in `server-pid.txt`. Stop that specific test server when finished. An occupied port causes the local server to fail; select another port with `-Port 8755`. Local HTTP feeds are accepted only at `127.0.0.1`, on the chosen port, in an explicitly marked disposable app folder. They cannot be selected in ordinary settings or supplied by a mod.

For repeatable automated checks:

```powershell
& path/to/Batcomputer.exe --verify-app-updater artifacts/updater-checks
```

Inspect the unique test folder's `results.txt`. Checks cover version/channel selection, local HTTP download, archive and manifest verification, unsafe paths, corrupt data, file locks, interrupted installation, rollback and preservation of fixture settings/projects. The local test folder's executable also supports `--updater-test-install` to exercise staging, helper handoff and restart automatically; this command refuses an ordinary unmarked installation.

## Download sizes

Release publishes now use an **organized self-contained folder**: one top-level `Batcomputer.exe`, with managed/native DLLs, .NET, dependency metadata and native runtime subfolders under `app/`. The native launcher loads `app/Batcomputer.dll` in the same process, using only the bundled app-local runtime. A separate .NET installation is not required. Keep the whole folder together; do not move the EXE alone.

Settings, Data, Runtime cache, Generated, tools, documentation and game-data catalogs remain rooted beside the launcher. Existing paths are not shifted into `app/`. Ordinary debug/build output stays flat; use `-p:BatcomputerOrganizedLayout=false` for an explicit flat publish.

The organized-layout-aware updater can migrate a flat installation: exact-hash matching files are reused from the old location, recognized legacy application files are archived in the transaction backup, and rollback restores their original locations. Unknown or modified third-party DLLs are retained, not swept away. New publishes keep the optional crash-dump CLI under `app/` too, never as a second top-level EXE.

**Compatibility:** updater builds predating `app/` support reject these packages. The first public transition needs a flat bridge build containing this updater, or a manual clean-folder install. Do not advertise an organized package as directly compatible with older updater clients. This differs from the earlier flat full-ZIP/file-catalog compatibility.

When a release offers a file catalog, the updater shows the compressed changed-file download estimate and number of files it can reuse. It verifies local hashes, copies matching files into a complete staging tree, downloads compressed changed/missing files, then verifies every staged file. A file modified or removed after the check is downloaded instead of trusted. This can change the final download total. Unchanged installed files are not rewritten or backed up; rollback retains originals for the changed files only. Unknown/obsolete files are not automatically deleted.

Without a file catalog, it uses the full ZIP. Clients that support the destination layout can use either route. The first single-EXE-to-folder transition generally downloads most or all of the runtime; flat-folder-to-organized and later folder-to-folder updates can reuse matching runtime files. Runtime security updates and changed dependencies still need downloading. File-level updates are not binary deltas: a changed DLL is downloaded in full, compressed.

For a local small-download test, run `./Tools/Updater/Start-LocalUpdateTest.ps1 -FullApp -FileUpdates -Scenario Slow -NoLaunch -Port 8761` with a new output directory. `expected-download.json` records the measured baseline. After downloading, the transaction's `file-update-summary.json` records actual bytes and reused file counts. Add `-FlatBase` to test a bridge flat-to-organized transition, or `-SingleFileBase` for a single-file base; omit `-FileUpdates` to exercise the full-ZIP route. No test needs a GitHub release. The marked test installation also supports `--updater-test-helper` to copy and execute the private helper without installing anything or showing UI.
