# Organized updater and studio preview acceptance

## Implemented

- Native .NET SDK apphost at the top level; bundled managed/native dependencies under app/. Same process, app-local runtime only. Host trace confirms app/hostfxr.dll and app/coreclr.dll, not the system .NET installation.
- Root-relative settings, projects, caches, tools, catalogs and profile loading preserved.
- Organized helper/recovery copy; flat-to-organized migration archives recognized legacy files in a durable recovery journal. Unknown or modified third-party DLLs remain untouched.
- Matching legacy DLLs can be reused across the folder move, with staged hashes checked again.
- Neutral startup surface instead of flashing the native fallback. Fallback appears on initialization failure or missing page-ready acknowledgement after navigation.
- Page-level fading studio glow, not a rectangular stage background. Shadow alpha fades at the canvas edge.
- Original model preserved; generated derivative has rounded keycaps, beveled edges, per-part materials, softbox reflections, a soft shadow and animated indicators/lever. Editable derivative: output/updater-model/studio-v3/Batcomputer-Updater.blend.

## Passed checks

- Release build: zero compile errors. NU1900 means the NuGet vulnerability feed was unavailable; no security-scan success is claimed.
- 22 updater regression groups, including interrupted layout migration and rollback:
  output/OrganizedFinalChecks/updater-checks-3b00b0e7636c4d76b581dd05e4489665/results.txt
- Real flat -> organized -> flat round trip: staged/installed manifest verification, executed updated runtime without UI, restored old files and preserved fixture settings/project sentinel:
  artifacts/organized-migration-acceptance/updater-fixture-result.txt
- That migration reused 372 files and downloaded 6,485,711 bytes (two changed files), not the full runtime.
- Real organized -> organized -> original round trip:
  artifacts/organized-folder-acceptance/updater-fixture-result.txt
- Native launcher/private helper execution with their own bundled runtime and correct data root:
  artifacts/updater-clean-layout-final/installed/updater-helper-result.txt
- Headless authored-page checks: shader compilation, model loading, pause, fallback, restart state gating and bridge message, narrow layout. Across 49 animation samples and both hover-yaw extremes at 946x600, 360x600 and 720x760, projected mesh bounds stayed within 0.835 of the normalized viewport half-height; no model crossed the canvas boundary.
- Final local render: output/updater-model/web-ready.png. The old background-glow seam is absent.

## User flow still to confirm

artifacts/updater-clean-layout-final/TEST-INSTRUCTIONS.md

This fresh app has one top-level EXE and no top-level DLLs. Feed: 127.0.0.1:8764. Expected changed-file download: 6,486,560 bytes, reusing 372 files. No desktop/computer control, public release, commit, push, or game installation was performed. The exact in-app open/close transition, visual preference and restart click remain user acceptance.

## Public rollout constraint

Older updater clients that do not allow app/ reject organized packages. Ship a flat bridge release containing the new updater first, or require a clean-folder manual install. Keep this migration compatibility distinct from the older full-ZIP/file-catalog fallback. The test flat base uses the new bridge-capable code; it does not imply that every already-distributed updater supports this layout.
