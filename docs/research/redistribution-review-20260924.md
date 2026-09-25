# Redistribution review — 2026-09-24

Historical inventory: see [current remediation and verification](native-redistribution-remediation-20260924.md).
The native helpers and download fallback described below have since been replaced.

Status: **not cleared for public redistribution yet**. This is an engineering
inventory and conservative release hold, not a legal opinion.

**Follow-up:** [detailed findings and updater readiness](redistribution-followup-20260924.md).
The package-notice gaps listed below have now been collected for all 40 identified
packages. Native provenance, embedded-component obligations and the Unreal
distribution question remain open. The follow-up identifies an Oodle-download
fallback in the retoc dependency source requiring remediation.

## What was inspected

`artifacts/updater-clean-layout-final/next-publish`: 261 DLL/EXE files, 256 matched
byte-for-byte to resolved NuGet or self-contained runtime package files, covering
40 packages. Two are Batcomputer outputs; three require manual provenance review.
No Oodle runtime (`oo2core*.dll`) was present in that publish. A local development
copy exists and must stay out of public packages.

`Tools/Updater/Audit-Redistribution.ps1` writes SHA-256, relative path, matching
package/version and review status to `output/redistribution-audit/`. Its
`MissingLocalLicenseTexts` means missing in the NuGet package root, not necessarily
missing after upstream collection. `Collect-PinnedDependencyLicenses.ps1` records
the exact upstream commit/URL for additional collected texts. Package metadata
and a hash match establish provenance to that package, not all legal obligations.

The csproj now includes the recursive `licenses/dependencies/**/*.txt` tree.
Collected package texts include .NET, Windows Desktop, WebView2 SDK, SkiaSharp,
ImageSharp, Newtonsoft.Json, and others. Pinned upstream LICENSE/NOTICE texts
include CUE4Parse/Conversion, Fmod5Sharp, Serilog, AssetRipper and others.

## Blocking and unfinished items

1. **Prebuilt Unreal editor plugin.** Our source is independently authored, but
   `UnrealEditor-BatcomputerRegistryWriter.dll` is built against Unreal Engine.
   Epic's EULA sections 5(a) and 6(d) restrict public distribution of Engine Tools
   to specified Epic channels. Determine the actual linked content, applicable
   agreement and classification/distribution route before ordinary public GitHub
   redistribution. A filename alone does not settle that question. The packager
   currently rejects Unreal editor binaries as a conservative hold.
   Simply excluding this DLL is not a working fix: portable-layout validation
   requires it. A source-only/local-build alternative needs setup changes and an
   Unreal-compatible C++ toolchain on the user's machine.
2. **CUE4Parse-Natives.dll.** The local prebuilt helper lacks recorded exact
   source/build provenance. Upstream can optionally build additional components,
   including Oodle-related support. Record or reproduce an ACL-only build and
   include every actual component's license/notice. Do not infer its composition
   from the managed package license or absence of a recognizable string.
3. **retoc.exe.** MIT fork license is present, but the shipped local executable
   needs its exact source revision, Cargo.lock/dependency licensing and build
   provenance recorded. A wrapper's MIT license does not grant an Oodle license.
4. **Complete package notices.** Exact-version texts still need resolution for
   packages without pinned repository metadata, including InfraBlack.UE4Config,
   K4os LZ4/Streams/xxHash, LZMA-SDK, NAudio.Core, OggVorbisEncoder, SharpGLTF
   alpha0023, SubstreamSharp and VGAudio. Blake3's pinned-source text also needs
   checking. Review embedded components in native codecs and all Apache NOTICE
   obligations. Some packages provide only a third-party notice, so ensure the
   applicable primary license is retained too. Do not mark the audit complete
   merely because package metadata says MIT/Apache/BSD.

The packaging filename guard also rejects game runtime `LOTDKExpanded.dll` and
`oo2core*.dll`. It is not a complete binary-content/license scanner and does not
police arbitrary manually created ZIPs. Existing files were left intact. The
guard currently also stops new local updater fixtures containing the held editor
plugin; existing fixtures remain unchanged.

## Primary terms

- [Epic Unreal Engine EULA](https://www.unrealengine.com/eula/unreal), especially
  sections 5(a), 6(d) and attribution requirements. Check the agreement applicable
  to the actual engine/licensee, including any custom terms.
- [.NET runtime MIT license](https://github.com/dotnet/runtime/blob/v10.0.0/LICENSE.TXT).
  Exact shipped 10.0.12 runtime license and third-party texts were collected locally.
- [ImageSharp 3.1.12 upstream terms](https://github.com/SixLabors/ImageSharp/blob/v3.1.12/LICENSE).
  This MIT open-source application qualifies for Apache-2.0; downstream notices
  identify that granted license. Reassess if application licensing/use changes.
- [CUE4Parse native build configuration](https://github.com/FabianFG/CUE4Parse/blob/master/CUE4Parse-Natives/CMakeLists.txt)
  demonstrates why exact native build provenance matters; it is not proof of the
  contents of our local prebuilt binary.
- [Microsoft WebView2 distribution guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution).
  SDK package notices are preserved separately from browser runtime installation.

## Verification performed

Release build passed (one NU1900 warning: vulnerability-feed lookup unavailable).
All 24 updater regression groups passed, including one-time version-matched
completion notification gating and rejection of held runtime filenames. No GUI
automation, public release, commit or push was performed for this change.
