# Updater acceptance — 24 September 2026

**Latest follow-up:** changed-file updates and folder publishing are now implemented and tested. See [incremental acceptance](incremental-updater-acceptance-20260924.md) for measurements, migration/rollback evidence and remaining limits. The full-ZIP-only limits below describe the earlier implementation.

## Implemented and checked

- The normal Debug source build completes with zero errors. NuGet emitted NU1900 because its vulnerability-data service was unreachable; that is not a successful dependency-security audit.
- The updater-specific command passes all 14 check groups in the final source build: semantic versions/channels, path protection, duplicate entries, install/rollback, interrupted installs, corrupt staged files, locked files, user-modified files during recovery, shared/exclusive app locking, local HTTP metadata, ZIP download/verification (including cancellation, truncation and wrong versions), remote-origin/missing-digest rejection, low-space rejection, and ZIP traversal.
- Existing release regression checks pass. Some game-extraction-dependent fixtures report their usual explicit skips in the isolated build.
- Two self-contained test publications, `99.0.0-updatertest.1` and `.2`, exercise the actual archive, private helper, application replacement and restart path over a loopback HTTP server. The restarted `.2` app confirms startup. The recovery helper restores `.1`.
- The fixture settings and `Generated/KEEP-ME.txt` have identical SHA-256 values before update, after update, and after rollback. No real project settings, game files or installed mods were used in the test.
- The actual desktop Updates window successfully checks the local feed, shows its release notes/size, downloads the package and reaches “Verified and ready”. The local test window was left at that point for user testing. A separate native-window review prompted shorter startup-check wording; rendering was also inspected at normal and minimum dialog widths.

## Evidence locations (local, not shipped)

## Update center polish follow-up

- The redesigned source build completes with zero errors (NU1900 vulnerability-feed warning remains).
- All 15 updater check groups pass, including live pending/confirmed/rollback status interpretation.
- The form harness passes verified-download retention across menu close/reopen, live startup-status refresh without reopening, and retry availability after a scheduled update fails. Normal, minimum, ready, complete and retry layouts are rendered under `output/UpdaterModernFlowUi/`.
- A full-workshop local fixture opens on the user desktop as `.1 · LOCAL TEST`, with isolated settings and dummy game paths. Its feed offers `.2` with a throttled download. User-driven in-app cancellation, install/restart and visual acceptance are pending; see [the checklist](updater-in-app-checklist.md).
- Follow-up regression evidence: `output/UpdaterModernTests/updater-checks-6e040056654f4b32898ecece393fab01/results.txt`.

## Earlier evidence locations (local, not shipped)

- `output/UpdaterTestsFinal/updater-checks-6cf12ee5e15442c8b955ecfe6886a150/results.txt`
- `output/updater-release-regressions.log`
- `artifacts/updater-local-20260924-v2/installed/.batcomputer-updates/b3018bbf754e48bda1e3bb25efa57436/`
- `output/UpdaterUiAudit/`

The automated launch runs on the command environment's desktop. Its hidden fixture process was stopped for cleanup before recovery; the application updater itself never kills the editor. A separate test instance was then launched on the user desktop for the visible download check.

## Still outside this acceptance

### Actual-model update screen follow-up

- The supplied FBX and adjacent texture were imported in Blender without modifying the originals. The 577,060-byte GLB retains all four meshes, one embedded texture, `LeverPivot`, and `LeverCycle`. The editable scene and poster are under `output/updater-model/prepared/`.
- Source build and all 15 updater regression groups pass (`output/UpdaterModelChecks/`). NU1900 remains an unavailable vulnerability-feed warning, not a security audit pass.
- Browser rendering checks pass: real GLB/texture loading, lever clip presence, emissive material shader compilation, download/verification/ready states, pause control, 360px layout, and still-image fallback after a blocked model load. Screenshots: `output/updater-model/web-*.png`.
- Fresh disposable full-app test pair: `artifacts/updater-actual-model`, slow loopback feed on port 8758. Both versions contain the actual-model screen. This is separate from the previous `updater-full-app-polish` fixture.
- Animation and its WebView do not own installer logic. Native handlers still verify/stage/install, release notes remain text, navigation/downloads are restricted, and the original native view remains a fallback for WebView startup failure.
- Full user-driven install/restart acceptance of the new presentation remains separate from the earlier successful old-layout test. No incremental update protocol or packaging-layout migration was enabled.

## Remaining release limits

- No GitHub tag or release was created. Live GitHub publication/download acceptance requires uploading the updater asset to a future real release. Users first need a manual installation of an updater-capable build.
- Trust is HTTPS GitHub release metadata plus GitHub's archive digest and the verified internal manifest; independent release signing is not implemented.
- Updates still use full ZIPs. No incremental downloads, launcher migration, automatic retention, scheduled installs or runtime/mod/game updates are implemented.
- Interrupted-install exceptions were injected and recovery rerun, but actual machine power loss and all antivirus/permissions configurations have not been tested. Recovery data is retained rather than claiming interruption can never affect an install.
- Startup confirmation proves the main window opened, not that every feature of a new release works.

See the [updating guide](../guides/app-updates.md) for packaging, local test setup and recovery instructions.
