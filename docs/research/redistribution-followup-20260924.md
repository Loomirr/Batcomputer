# Redistribution follow-up and updater readiness — 2026-09-24

Historical findings: superseded by [native remediation and verification](native-redistribution-remediation-20260924.md).
That later work replaces the native helpers and removes retoc's runtime-download fallback.

Status: engineering review completed to the extent evidence permits; **not a
blanket legal clearance**. No working binaries, user installations or public
releases were changed.

## Package notices

All **40 used NuGet/runtime packages now have collected license/notice texts**.
The prior root-text gaps are resolved, including Blake3, K4os, NAudio, SharpGLTF,
SubstreamSharp, OggVorbisEncoder, LZMA-SDK and VGAudio. The collector records
upstream URLs/hashes. `Tools/Updater/DependencyLicensePins.json` records the
release tag/commit basis where NuGet omitted a commit. InfraBlack's v0.7.2 source
is identified, but its package's `.97` build suffix is not independently matched.
The primary .NET MIT text was also supplied for Bcl.Memory and System.IO.Hashing,
whose cache roots had third-party notices but no separate primary license file.

The audit now distinguishes missing texts inside the NuGet cache from texts
collected upstream. `notice-coverage.json` and `summary.json` report 40 covered
packages and zero packages without collected texts. This counts notice presence,
not compliance with every embedded component's terms.

## Unreal registry writer: hold remains

Read-only PE inspection confirmed imports of `UnrealEditor-Core.dll`,
`UnrealEditor-CoreUObject.dll`, `UnrealEditor-Engine.dll` and
`UnrealEditor-AssetRegistry.dll`. The `.uproject` declares an Editor module.
Build.cs lists Core/CoreUObject/Engine/AssetRegistry, **not UnrealEd**. Linking
editor-build runtime libraries is not proof that Epic's Editor-folder
implementation was copied into our plugin.

[Epic's EULA](https://www.unrealengine.com/eula/unreal), sections 5(a) and 6(d),
restricts Engine Tools distribution. The actual classification, applicable
agreement and allowed route need confirmation from Epic or qualified counsel.
The packaging hold remains conservative, not a declaration that every authored
editor plugin is prohibited. A separate download or source-only approach is not
automatically a legal workaround. Local builds also need setup changes and an
Unreal-compatible C++ toolchain; excluding the DLL breaks portable validation.

## CUE4Parse native helper

Shipped SHA-256:
`d84d61148556fe6f8d7d9c876a03d5517f7a18087a8bc998eb41e78273e1b813`.
Exports: `IsFeatureAvailable`, `nAllocate`, `nCompressedTracks_IsValid`,
`nDeallocate`, `nReadACLData`, `nReadCurveACLData`, `nTracksHeader_SetDefaultScale`.
No Oodle exports. This supports an ACL-only build, but does not establish the
binary's exact source revision or every embedded component.

The managed package pins CUE4Parse source to
`ecad882a3049df6f27e0c5c3a3531346305c010b`, which pins ACL to
`414689d5cff4286a7898487a46dc5e48005d38da` and RTM to
`d7982f2b2524feeb322f424b29cf43df30b5d5b7`. Native CMake adds Oodle only if
separately supplied source exists. A reproducible ABI-matched ACL-only build with
complete CUE/ACL/RTM notices closes this gap more reliably than inference.

## retoc: additional issue found

Shipped SHA-256:
`8bd3d3c1abb23c47ef8bed2f621e17df9595abac6e46ff9a6f916f0bdcf29cad`.
The local `retoc-oodle-research` checkout is clean at
`a3d3c84f030118c36896568e35e0dcea6305e2b9`, but its release executable has a
different hash. It is candidate source, not proven source for this binary.

Its locked Windows dependency graph includes `webpki-roots 0.26.7` (MPL-2.0),
`ring 0.17.8` (mixed license file), and MIT/Apache/BSD/ISC/Unicode components.
One retoc MIT notice does not cover the full executable. MPL does not automatically
relicense the whole app, but applicable covered-file source/notice obligations
must be met if that code is distributed.

The candidate `oodle_loader` has `fetch_oodle()`: when no adjacent DLL exists,
it downloads Oodle from a third-party GitHub repository. The same
`WorkingRobot/OodleUE` URL marker and hash-error marker are in the shipped EXE.
**No download was triggered in this audit.** Batcomputer adds the configured
runtime directory to PATH for Oodle packing; that does not by itself prove the
fallback is unreachable in this helper.

Next implementation: establish the exact source or rebuild a known source,
remove network fallback in favor of explicit local-runtime loading and a clear
missing-runtime error, record provenance/full dependency notices, then test
packing/unpacking before replacing the shipped helper. Leaving Oodle out of the
ZIP alone does not settle the fallback issue.

## Embedded codec notices

Blake3 2.2.1's pinned native Cargo.toml declares `blake3 1.8.2`, `libc 0.2.178`
and Rayon feature support; that revision has no Cargo.lock. Its BSD wrapper
text is not the complete native dependency notice set. ZstdSharp.Port 0.8.8
identifies Zstandard 1.5.7 as its basis, so preserve upstream obligations too.
Zlib-ng.NET is a wrapper; the publish has no separate zlib-ng native DLL. Do not
infer bundled components solely from wrapper support.

## Updater status

The planned beta flow is implemented: version display, channel/check, verified
full/file-level downloads, backup/recovery, install-on-exit, restart now,
animated page and one-time completion prompt. The latest normal-version run
passed all 24 regression groups. Prior disposable tests covered flat/organized
migration, rollback and private-helper execution. One file-update fixture fetched
6,486,560 bytes versus an 81,578,464-byte ZIP, reusing 372 files; that is not a
guaranteed size for every update.

**Feature-complete for the planned beta, not public-release-ready yet:**

1. Resolve the distribution issues above; packaging remains held.
2. Choose a flat bridge release or clean manual install for older clients that
   cannot consume the organized `app/` layout.
3. Verify real GitHub release assets/digests and run a final clean-machine
   end-to-end smoke test, including the new completion dialog's visual check.

No live GitHub release was created/exercised here. Independent manifest/code
signing remains future hardening; current trust is GitHub HTTPS and release API
digests. No additional UI redesign is needed to finish this beta.

Final follow-up verification: restored the missing win-x64 Release assets target
from the available packages, then built successfully (zero warnings/errors) and
reran all 24 updater checks with zero failures. Results are in
`output/RedistributionFollowupChecks/updater-checks-95ffec3932f84644bc548de53b2e764d/results.txt`.
All 27 upstream source-record files were copied into the build's notices tree.
NuGet vulnerability auditing was disabled for this offline build check; it was
not a dependency vulnerability scan and does not constitute security clearance.
