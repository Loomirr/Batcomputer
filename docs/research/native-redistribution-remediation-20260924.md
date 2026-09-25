# Native-helper remediation and GitHub test plan

This supersedes the technical blockers in the earlier redistribution reports.
It is not a legal opinion or blanket permission to distribute third-party code.

## Implemented

- Rebuilt retoc from pinned Loomirr fork `a3d3c84`, using the checked-in Cargo.lock
  and Batcomputer's local-only Oodle ABI adapter. There is no HTTP/download stack
  in the replacement adapter. No proprietary runtime or SDK source was fetched.
- Updated every application retoc launch path (including packaging subprocesses)
  to pass the configured existing local runtime via `BATCOMPUTER_OODLE_DLL` and
  remove stale inherited overrides. Missing local runtime allows raw output;
  reading compressed data fails with a clear local-runtime error.
- Rebuilt CUE4Parse-Natives from the managed package's pinned CUE revision with
  only pinned ACL and RTM submodules. Recorded tool/source/patch and binary hashes
  under `licenses/native/build-provenance.txt`.
- Collected license/notice sets for 165 resolved retoc build packages, including
  build/test dependencies, plus full CUE/ACL/RTM notices. Removed HTTP dependencies
  mean the old MPL-only webpki-roots dependency is absent. For crates explicitly
  offering Apache-2.0 but omitting its text, selected that offered license and
  retained original declaration/author/source records with its complete text.
- Kept the prebuilt authored registry writer as explicitly requested. The exact
  expected path is exempt from the conservative filename guard; Epic engine/editor
  DLLs, game DLLs and Oodle runtime filenames remain blocked. No new user compiler
  requirement. Prior shipping history does not establish legal permission; the
  distribution-terms classification question remains documented, not "cleared".

## Verification

- Native build recipe completed from fresh pinned checkouts:
  `Tools/Native/Build-NativeHelpers.ps1`. It never resets existing checkout paths.
  This provides repeatable source inputs, not bit-identical output across compiler
  versions/build paths. Run notice collection/acceptance before promoting outputs.
- Three Rust local-runtime validation tests passed.
- Converted the existing BodyOnly staged assets with `to-zen --version UE5_6`;
  verification of the resulting container passed. Original stages/paks were unchanged.
- Repacked a real four-chunk test container with and without Oodle; both unpacked
  payloads matched originals by SHA-256. The compressed UCAS was 105,013 bytes,
  compared with 251,610 uncompressed. Missing-runtime decompression failed with
  the expected error; no DLL was downloaded or created.
- Decoded `A_Idle_Batman_LEGOface` using the prior and new ACL binaries: both
  produced 59 tracks/386 frames and identical diagnostic pose output. This checks
  the probe's reported pose values, not a numeric comparison of every frame.
- Release publish succeeded; all **26 updater regression groups passed**,
  including local-runtime environment handling and the narrowed packaging guard.
- An internal full updater ZIP built successfully. The publish audit matched 256
  package binaries and two source-built helpers, with all 40 package notice sets
  present. No blocked runtime files were found. The authored writer remains the
  explicit manual terms-review item.
- Source-built helpers were promoted to the development source inputs. Previous
  binaries are retained in `output/NativeHelperAcceptance`. No installed game mod,
  user's running portable installation, GitHub release or git history changed.

Results: `output/NativeHelperAcceptance`, `output/native-remediation-audit` and
`output/NativeRemediationUpdaterChecks/updater-checks-618f15dbfd5a46e693d5805aecb6327a/results.txt`.
The internal ZIP is only an acceptance artifact, not a newly versioned release.
NuGet vulnerability auditing was not part of these offline build checks.

## A real GitHub updater test

We can test on this same PC in a disposable portable folder with isolated
settings/projects. No clean machine or game-mod installation is necessary.

1. Prepare two genuinely versioned builds: an older bridge-capable base and the
   candidate. Never publish fake `99.0.0-updatertest` versions to the real feed.
2. With the owner's explicit approval, publish candidate assets to a controlled
   GitHub test release: full ZIP and SHA, file catalog and SHA, and every referenced
   compressed file payload. A dedicated test repository is least disruptive but
   requires a deliberately test-only build pointing to that repository: production
   currently pins Loomirr/Batcomputer. Ordinary settings cannot redirect it.
3. Alternatively use an intentional prerelease in Loomirr/Batcomputer. It is
   visible to beta-enabled users; this is not a hidden test. The normal unauthenticated
   updater skips drafts, so a draft alone does not exercise the ordinary flow.
4. In the isolated older copy: Check, verify candidate/version/download estimate,
   Download, verify GitHub redirects and SHA-256 digests, Restart now, confirm the
   new version and one-time completion prompt, then verify sentinel settings and
   project files are unchanged. Close/reopen once more: no repeated success prompt.
5. Test backup recovery and the full-ZIP fallback in separate disposable copies.
   Confirm the stable channel does not offer a prerelease and the production feed
   did not accidentally expose an artificial test version.

GitHub's release API supplies asset metadata including digest and download URL:
https://docs.github.com/en/rest/releases/assets
Draft visibility and release behavior:
https://docs.github.com/en/rest/releases/releases

A portable-folder test validates updater behavior but does not prove a PC without
installed .NET/WebView2/VC++ components has everything needed. That optional
prerequisite test can use a VM or Windows Sandbox on this same PC where supported;
it does not require another physical computer. No GitHub upload was performed here.
