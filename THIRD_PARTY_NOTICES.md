# Batcomputer Third-Party Notices

Batcomputer's MIT license does not replace the licenses of its dependencies.
Preserve this file and the entire `licenses` directory when sharing the app.
License and NOTICE texts collected from the resolved packages and pinned upstream
commits are included under `licenses/dependencies/<package>-<version>/`.
That collection is still undergoing review; it is not a certification that every
binary or embedded component is cleared for redistribution.
The audit has collected texts for all 40 identified NuGet/runtime packages.
Source-built helper provenance and 165 resolved native-build dependency notice
sets are now recorded under `licenses/native`. See
`docs/research/native-redistribution-remediation-20260924.md` for verification
and the separate Unreal distribution-terms question.

## .NET and managed/native package dependencies

The self-contained distribution includes Microsoft .NET and Windows Desktop
runtime files. Their MIT licenses and applicable third-party notices are retained
under the corresponding `microsoft.*.runtime.win-x64-*` directories. Moving these
files into `app/` does not alter their licensing obligations.

Other collected notices include CUE4Parse/Conversion (Apache-2.0), SkiaSharp and
its native components, WebView2 SDK, Newtonsoft.Json, Serilog and supporting
packages. Native libraries can include additional components: retain both LICENSE
and NOTICE/THIRD-PARTY-NOTICES files, not just the top-level package license.
The WebView2 SDK files are not a bundled Microsoft Edge browser runtime.

### ImageSharp 3.1.12

Copyright (c) Six Labors. Batcomputer consumes ImageSharp under Apache License
2.0 as an open-source MIT-licensed application. The applicable license text is in
`licenses/dependencies/sixlabors.imagesharp-3.1.12/Apache-2.0.txt`.
Reassess eligibility if the application's licensing or use changes.

## retoc-oodle

Batcomputer includes `Tools\retoc-oodle\retoc.exe` in its portable release.
That helper is built from the [retoc-oodle source fork](https://github.com/Loomirr/retoc-oodle)
of [`trumank/retoc`](https://github.com/trumank/retoc), with Batcomputer's local-only
runtime adapter. Its own source is MIT-licensed; dependencies retain their own
licenses, collected under `licenses/native/retoc`.
Its complete license text is in `licenses/retoc-oodle-MIT.txt`.

The helper can load an Oodle runtime from a user-selected local Unreal Engine
installation. The inspected publish does not include `oo2core_9_win64.dll`.
Do not redistribute this proprietary runtime or game binaries merely because an
MIT-licensed wrapper can load them. The current helper has no Oodle download
fallback. Batcomputer supplies an explicit local `BATCOMPUTER_OODLE_DLL` path.
Without it, compressed reads report a missing-runtime error; uncompressed output
can still be written. Build inputs/hashes are in `licenses/native/build-provenance.txt`.

## Other notices

The existing notices for the CUE4Parse native helper, three.js and D-DIN font are
kept in the `licenses` folder alongside this file. The source-built
`CUE4Parse-Natives.dll` is recorded in `licenses/native/build-provenance.txt`,
with CUE4Parse, ACL and RTM license/notice texts alongside it.

## Public release review pending

The current development publish includes our prebuilt
`UnrealEditor-BatcomputerRegistryWriter.dll`. Its Unreal Engine dependency and
the applicable distribution terms must be reviewed before public distribution.
The release owner has chosen to retain this authored prebuilt module. Packaging
exempts only its exact expected tool path while continuing to reject Epic engine
and editor DLLs. This choice is not legal clearance or a conclusion that every
independently authored plugin is prohibited. Users do not need a compiler for it.

See `docs/research/native-redistribution-remediation-20260924.md` in the source
repository for the current remediation, verification and remaining terms-review
item. Earlier redistribution reports are historical inventories. Do not treat
this development notice collection as blanket legal clearance.
