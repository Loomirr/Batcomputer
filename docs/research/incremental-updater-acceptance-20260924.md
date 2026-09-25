# Changed-file updater acceptance — 24 September 2026

## Implemented

- Self-contained folder publishing (runtime included; not framework-dependent), full-ZIP compatibility and optional authenticated compressed-file catalogs.
- Local file size/hash comparison; matching files copied and reverified in a complete staging tree. Changed/missing files are downloaded as bounded gzip payloads, with compressed and expanded hashes/sizes verified. Files modified after Check are fetched instead of trusted.
- Only changed installed files are replaced/backed up. Recovery journals support a subset of application files. No unknown/obsolete files are pruned.
- Long-path-safe Windows version-resource checks. GitHub release listing uses smaller pages because file catalogs require many assets.
- Release uploads include the complete catalog and all referenced payloads, plus full ZIP and SHA. Cross-release upload deduplication and binary deltas are not implemented. Independent cryptographic release signatures are not implemented; trust remains restricted HTTPS GitHub metadata/digests.

## Measured fixture

`artifacts/updater-smaller-test`, real self-contained folder builds `99.0.0-updatertest.1` and `.2`, loopback port 8761.

| Metric | Actual |
|---|---:|
| Full ZIP | 81,498,015 bytes (77.72 MiB) |
| Changed-file download | 6,450,055 bytes (6.15 MiB) |
| Reused files | 369 |
| Changed files | Batcomputer.dll, Batcomputer.exe |
| Payload download reduction versus full ZIP | 92.1% |

The small catalog/HTTP metadata is additional to the payload total. Future runtime, dependency or asset changes can increase download sizes. This fixture demonstrates real versioned builds, not a synthetic reduction estimate.

## Checks and evidence

- Source build: zero errors, NU1900 vulnerability-feed warning. An unavailable vulnerability audit is not a successful security audit.
- All 21 regression groups pass: `output/IncrementalFinalChecks/updater-checks-c8672feac92b490389265276eca9007e/results.txt`.
- Selective install -> updated runtime execution -> rollback: `artifacts/incremental-acceptance/updater-fixture-result.txt`. Transaction `e27c22403c2c4fabaafabac1ce0dd87f` records the actual bytes, 369 reused files and a two-file undo journal.
- Single-file .1 -> folder .2 via the **full ZIP** -> runtime execution -> rollback to the single-file .1: `artifacts/migration-acceptance/updater-fixture-result.txt`, transaction `217b78a649d04ddaacfab2d7b6c62d78`.
- Both acceptance runs verify the complete staged/installed manifests and preserve fixture settings and `Generated/KEEP-ME.txt` hashes. The test runtime command starts/exits without UI; no computer-control tools or app-window manipulation were used.
- The user's `artifacts/updater-smaller-test/installed` folder remains at `.1` for a real manual in-app test; see its `TEST-INSTRUCTIONS.md`.

## Limits of these checks

The new runtime starts successfully using the folder distribution. These headless checks do not establish full main-window restart acceptance or arbitrary workshop feature compatibility; the user's manual test covers the real install-on-exit flow. The migration test uses the current installer with the legacy full-ZIP format, not a replay of every historical updater binary. Deeply nested paths in an older client may still require manual extraction to get the fixed updater. Actual power loss, all antivirus configurations and live GitHub publication have not been tested. No release/tag was published.
