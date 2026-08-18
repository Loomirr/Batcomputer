# Report a problem

Use the repository's [bug report form](https://github.com/Loomirr/Batcomputer/issues/new/choose).

## Include

- Batcomputer version.
- Current game build and mappings version/source date.
- Whether LOTDK Expanded works with another known-good custom suit.
- Exact steps from launch to failure.
- Expected result and actual result.
- The relevant **Copy log** output from Batcomputer.
- Relevant LOTDK Expanded or UE4SS log lines for in-game failures.
- A screenshot when the problem is visual.
- Whether the problem reproduces after a cold game restart.

## For build failures

Also include the release-preflight findings and the final build section. If possible, say whether the
same suit builds without the newest texture, material, part, or custom mesh.

## Protect your machine and game data

- Do not upload `.pak`, `.ucas`, `.utoc`, `.uasset`, `.uexp`, `.ubulk`, or extracted game assets.
- Redact your Windows username and other personal folder names.
- Project JSON and logs may contain absolute paths; inspect them before posting.
- Package paths such as `/Game/Characters/...` and `/Game/Mods/...` are usually the most useful
  identifiers and do not require uploading the asset itself.

## A good minimal report

```text
Batcomputer: 0.9.0-beta.1
Game/mappings: <build and date>
Action: Build Mod
Expected: release installs
Actual: preflight blocks the UIMD icon
Reproduces: yes, after restart

Relevant diagnostics:
<paste only the relevant lines>
```
