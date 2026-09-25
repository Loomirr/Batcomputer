# Updater polish and distribution review — 2026-09-24

## Implemented

- Centered the complete animated model with a separate parent group; original lever animation targets stay intact.
- Reduced key/fill/rim lighting and exposure to retain panel colors and contrast.
- Aligned the model, actions and disclosure sections to a shared content column; reduced the animation control to 24px high.
- Added Restart now after verification, including the native fallback. Confirmation requires the user to save work first. Normal Application.Exit(CancelEventArgs) honors FormClosing cancellation. The existing verified installer handles restart; no forced exit or second relaunch mechanism.
- Install when I exit remains available. A cancelled normal shutdown leaves installation scheduled with the existing ten-minute timeout.

## Manual acceptance (not automated desktop control)

The headless page checks pass: real GLB/shader loading, pause, 360px overflow, shared column alignment, 24px control, restart visibility only when verified/scheduled, restart bridge dispatch and static fallback. Source Release build passes (NU1900 vulnerability-feed-unavailable warning; not a security scan).

Run --verify-app-updater on the normal version build, not the 99.0.0-updatertest fixture. Its release-ordering test intentionally uses 98.0.0 and 99.0.0-beta fixture releases; a 99.0.0-updatertest build correctly filters those as downgrades and that one assertion fails. The production-version run passed all 21 groups. This is a test-harness version assumption, not a restart/download failure.

Use a fresh disposable local test, never the real installation.

1. Open installed/Batcomputer.exe. Check alignment, panel colors, lever motion and Pause/Play animation.
2. Check and download. Restart now must not appear before verification.
3. Choose Restart now, then Cancel the confirmation. The app stays open and nothing is installed.
4. Choose Restart now again, confirm after saving. The full test app should close and reopen as updatertest.2; Updates should report startup confirmed.
5. In a fresh fixture, test Install when I exit, then Restart now. Only one installer should be scheduled.
6. With an editor operation that blocks normal closing, try Restart now: shutdown should be cancelled and the updater should say Restart paused. Finish the operation and retry within ten minutes.

## Cleaner install — proposed, not implemented

Use a small root launcher with application/runtime DLLs kept together under an app subfolder, while settings, Data, Generated and other user state retain their existing paths. Do not manually relocate DLLs in the current build.

This requires explicit application/data roots, launcher argument and exit handling, updater path allowlists, helper layout, version detection, health confirmation, and an old-layout migration/rollback test. Current code resolves these from the app directory. Moving DLLs now would break those assumptions. File-level updates already avoid downloading unchanged runtime files; changing folders is organization, not an additional download-size saving by itself.

## DLL licensing review — preliminary, not legal clearance

Inspected resolved NuGet package metadata in obj/project.assets.json and package-cache licenses, plus the existing test publish. Most declare MIT, Apache-2.0 or BSD licenses. UAssetAPI 1.1.0 and FixedMathSharp 4.0.1 contain MIT license files; the WebView2 SDK package contains redistribution conditions requiring its notices. Bundling rather than leaving DLLs loose does not remove license obligations.

.NET permits redistribution subject to its license/notice requirements:
https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

ImageSharp 3.1.12 is not simply MIT: its terms grant Apache-2.0 for qualifying uses, including transitive dependencies and open-source/source-available software. It is currently a transitive dependency in Batcomputer. Preserve the applicable Apache license and attribution; reassess if dependency usage/distribution changes:
https://github.com/SixLabors/ImageSharp/blob/v3.1.12/LICENSE

WebView2 SDK DLLs are not the browser runtime; runtime deployment has separate guidance:
https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution

The existing THIRD_PARTY_NOTICES.md is incomplete for the full dependency tree. Before public packaging, collect exact-version copyright/license/NOTICE texts for shipped managed and native dependencies and .NET runtime packs, including embedded native third-party code. Package SPDX declarations alone do not complete that work. This review does not certify all binaries for redistribution.

The test installation includes MIT-declared Oodle.NET/OodleSharp wrappers, not the proprietary oo2core runtime; no oo2core files were found in that test installation. Continue excluding proprietary Oodle, Unreal Engine and game binaries. Review the actual final archive, not just its filenames or package declarations.
