# Workflow refresh and updater design

Status: workflow implementation and updater follow-up notes, 24 September 2026. Full-ZIP and changed-file updates plus local test feeds are implemented; see [app updates](../guides/app-updates.md). Startup checking is opt-in and never installs automatically. The original planning sections below are historical; the current implementation uses a same-folder self-contained distribution, not a version-directory launcher, and GitHub asset digests rather than independent signatures.

## Current pass

- Separate the permanent character/project ID from the editable runtime pawn-tag family. Preserve asset paths, display names and progress IDs. Back up and update the definition and saved child suits together. Rebuild/reinstall the whole mod afterwards; old runtime selections are not migrated.
- Reject both native group filename conventions, including `DA_Character_Group_PoisonIvy` and `DA_Character_Group_MrFreeze`.
- Fix skeletal GLB generation: exported vertices already use metres. Skinned nodes cannot carry spatial transforms. Convert native bone translations only and preserve bind space.
- Add Home → Import/Export for editable creator archives and installable release ZIPs. These sharing actions move out of Build mod. Finished cooked releases are not editable projects.
- Review: detailed list/full-text panel, explicit Copy/Remove actions, status filtering, searchable targets/details, category sorting, timestamps, counts and explicit distinction between recorded edits and successful builds/in-game tests. Compact cards remain configurable.
- Settings: resolve automatic paths, recognize on-demand output folders, check input structure rather than treating every existing folder as valid. Indicators are path checks, not runtime certificates.

## Wider audit: recommended next improvements

This is a code/workflow review, not a claim that every screen has had a complete interactive audit.

| Area | Follow-up | Acceptance check |
|---|---|---|
| Identity | Test migrated Ivy/Freeze in Normal/Mayhem and after restart; test reinstall over an older release | No duplicate groups; all child variants still belong to the definition |
| Review | Add navigation from the new list/detail panel to the edited component; high-DPI audit | Long changes remain readable at 125–200% DPI; no removal silently leaves obsolete cooked intent |
| Preview | Surface per-part export failures, separate texture fallback from geometry failures, add per-character memory budgets | A missing part reports its actual cause; cancelling conversion releases provider resources |
| Import | Pre-import summary of dependencies, name collisions and disk requirements | No partial archive import or overwrite on conflict; missing DLC/mappings explained |
| Settings | Validation summary with explicit manual recheck; debounce network paths | No UI hangs on unreachable network folders; automatic paths reflect the draft workspace |
| Builds | Disk-space estimates, cancellable phases, one-click diagnostic export | No interrupted build replaces the last working release |
| Documentation | Interactive screenshot pass at minimum window sizes and high DPI | Labels, warnings and screenshots match shipped UI |

Useful additional settings to implement with their corresponding behaviour (not inert toggles): preview texture resolution, character preview memory budget, retained preview count, backup retention with a user-confirmed cleanup preview, default archive export folder, import conflict policy (default: stop), default build-output folder, log verbosity and bounded log retention. Never automatically delete source models, saves, or projects to satisfy a cache budget.

## GitHub updater

Use the fixed repository `Loomirr/Batcomputer`, not a URL taken from a mod archive. GitHub provides release metadata, assets and asset digests through its [releases API](https://docs.github.com/en/rest/releases/releases) and [release-assets API](https://docs.github.com/en/rest/releases/assets).

### Safe first version

The original target architecture below remains a longer-term checklist. The implemented first pass uses GitHub API SHA-256 digests (not independent signatures), same-directory transactional replacement with retained backups (not a version-switching launcher), no automatic retries/caching, and no data migration. This preserves existing portable workspace paths. Checks are bounded and cancellable; the helper waits for app instances, retains recovery data and checks startup. Signed manifests, a launcher, automatic retention and incremental payloads remain follow-up work.

1. Manual **Check for updates**; optional startup checks off until chosen by the user. Add Stable/Beta channel choice, download confirmation and release notes. The latest-release endpoint is not sufficient for beta selection: list releases and filter draft/prerelease status deliberately.
2. Download to a staging directory with cancellation, maximum sizes, free-space checks, timeouts, retry limits and conditional HTTP caching. Never put a GitHub token in the client.
3. Verify a signed manifest with an embedded public key, file sizes and SHA-256 hashes. A colocated checksum alone detects corruption, not a compromised release account. Restrict repository/asset origin and validate redirects, archive paths, duplicate entries and symlinks before extraction.
4. Ask the user to save work and close Batcomputer. A small, separately versioned helper waits for the exact process to exit. Do not replace running binaries or force-kill the editor.
5. Install to a new version directory, validate its contents, atomically switch the launcher target, and retain the previous version. Run a startup health handshake; failed startup offers rollback. An interrupted download/install leaves the old version runnable.
6. Keep settings, Generated, authoring libraries, imported models and user-selected workspaces outside replaceable version directories. The current `AppSettings.ToolRoot` assumes data lives beside the executable: change this deliberately and migrate once with backup, rather than accidentally opening an empty workspace after update.
7. Keep the full portable ZIP + SHA available for offline/manual installs. Never update the game, UE4SS, or installed mods as part of the editor updater.

### Smaller downloads

The current project publishes a compressed self-contained single executable. It combines code with runtime/dependencies, so even an application-only change replaces the bundle. [.NET supports both folder and single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).

Recommended: keep a self-contained **folder** distribution behind a small launcher. Publish a manifest of independently hashed files; download only changed app assemblies, web assets, tools or dependencies. Keep unchanged runtime files across application updates. A full first-install download remains necessary. The inspected development build's Batcomputer.dll is about 10.7 MB before compression; this is not a promised release/update size.

Measure representative releases before adding binary deltas. If file-level updates are still large, generate a delta only against an exact prior hash, verify the reconstructed target hash, and fall back to the complete changed file. Do not delta-patch arbitrary user-modified binaries. Runtime security updates still require downloading affected runtime files.

Do not enable trimming/AOT merely for size: reflection-heavy asset libraries, JSON and WinForms need separate compatibility work. A framework-dependent alternative reduces first-install size but introduces a .NET Desktop Runtime prerequisite; keep it optional rather than silently changing requirements.

### Updater release gates

- Stable/Beta selection and downgrade prevention.
- Offline, rate-limited, cancelled, truncated and corrupted downloads.
- Malicious archive paths, duplicate entries, unsigned/wrong-key manifests.
- Locked files, permission failures, low disk space and power interruption at each install phase.
- Rollback without changing project data; old portable-install migration.
- Full/delta reconstruction equivalence and exact published hash verification.

The manual verified full-update flow is implemented. File-level incremental updates and independent release signing remain separate follow-up work.

### Concrete next migration (after animated UI acceptance)

Implementation follow-up: folder publishing, authenticated compressed-file catalogs, local hash reuse, complete staging validation and changed-only install/rollback are now implemented. Loose artwork extraction, obsolete-file cleanup, cross-release payload hosting and binary deltas remain deferred. The original proposal below is retained for context; it is not the current implementation checklist.

The inspected existing self-contained single-file release executable is 67.37 MiB. The current Debug application assembly including embedded UI/model assets is 11.20 MiB before compression; this is a development measurement, not a promised incremental release size.

1. Keep `SelfContained=true`, but publish a **folder** (`PublishSingleFile=false`): a small apphost EXE, the app DLL, dependencies and .NET runtime. Users still do not need a separate .NET install. Keep paths beside the existing portable app to avoid moving their data.
2. Introduce a versioned update format whose authenticated manifest lists each file's exact hash, size and download payload. Reuse unchanged files only after verifying their local hash. Download changed/missing files in compressed payloads, then validate the complete staged application before installation.
3. Retain the existing full ZIP as the initial-install, old-updater migration and recovery option. Old clients continue to receive the full ZIP. The first single-file → folder transition is a full update; later supported clients use file-level updates.
4. Move sizeable stable artwork/web resources outside the application assembly if measurements justify it, so a code-only DLL update doesn't resend those resources. Bundle the actual 0.55 MiB GLB only once; the editable Blender source stays out of releases.
5. Extend transactional install/recovery to account for changed, missing and explicitly obsolete app-owned files. Never prune unknown files, settings, projects, Runtime caches or game data. The previous complete runnable app must be recoverable.
6. Acceptance: old-layout migration, unchanged-runtime reuse, changed-runtime security update, locally modified/missing dependency, interrupted payload download, complete-manifest verification, rollback, and cold startup on a machine without .NET installed.

Do not just turn off `SelfContained`: that would require the user to install the matching .NET Desktop Runtime and does not by itself enable changed-file downloads. Do not promise a fixed small update size until representative Release builds have been measured. No packaging mode or update protocol was changed by the animated-model work.
