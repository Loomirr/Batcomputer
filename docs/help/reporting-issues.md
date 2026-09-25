# Report a problem

Use the repository's [bug report form](https://github.com/Loomirr/Batcomputer/issues/new/choose).

## Include

- Batcomputer version.
- Current game build and mappings version/source date.
- Whether Loomirr's LOTDK UE4SS works with another known-good custom suit.
- Exact steps from launch to failure.
- Expected result and actual result.
- The relevant **Copy log** output from Batcomputer.
- Relevant Loomirr's LOTDK UE4SS log lines for in-game failures.
- A screenshot when the problem is visual.
- Whether the problem reproduces after a cold game restart.
- For an older project, whether you refreshed game assets, refreshed the part index, or rebased the
  suit before the failure.

## For build failures

Also include the build-check findings and the final build section. If possible, say whether the
same suit builds without the newest texture, material, part, or custom mesh.

## For meshes or missing characters

- **Skinned mesh:** give the native donor/component, Blender version and export settings. Include
  the relevant `rig-comparison.json`, `import.log` and `cook.log` text. Say whether the problem
  occurs at import, in the preview, or only under an in-game animation.
- **Viewer:** identify native versus custom geometry, what is hidden in the assembly list,
  and whether another character previews correctly. For vehicles, mention safe-preview settings.
- **Character discovery:** include the character ID, pawn-tag family, selected game modes,
  gameplay donor and installed framework version. State whether the default character or only an
  extra suit is missing, and whether the complete registry/tag/trio bundle was installed.
- **Updater:** include old/new app versions and the transaction's status/error text. Do not post
  a whole workspace or recovery archive when a short log excerpt will do.

## Protect your machine and game data

- Do not upload `.pak`, `.ucas`, `.utoc`, `.uasset`, `.uexp`, `.ubulk`, or extracted game assets.
- Redact your Windows username and other personal folder names.
- Project JSON and logs may contain absolute paths; inspect them before posting.
- Package paths such as `/Game/Characters/...` and `/Game/Mods/...` are usually the most useful
  identifiers and do not require uploading the asset itself.

## A good minimal report

```text
Batcomputer: 1.0.0
Game/mappings: <build and date>
Action: Build Mod
Expected: release installs
Actual: build check blocks the UIMD icon
Reproduces: yes, after restart
Asset refresh / rebase: current dump, part index refreshed, no rebase needed

Relevant diagnostics:
<paste only the relevant lines>
```
