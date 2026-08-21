# Changelog

## 0.9.0-beta.4

- Fixed oversized and multi-monitor startup layouts, including the Diagnostics drawer consuming
  most of the window.
- Added the character-supporting `Content/Models/Gadgets` assets to normal extraction and made an
  incomplete refresh leave the previous extraction selected.
- Added a private installed-engine compatibility retry for missing NetFxSDK metadata and clearer
  first-error reporting for writer compilation failures.
- Separated normal cape attachments from glide-only cape/wingsuit components and retained the
  glider donor's traversal animation data.

## 0.9.0-beta.3

- Missing shared-registry errors now list the exact absent files.
- Completed suit builds can be installed after updating Loomirr's LOTDK UE4SS without rebuilding.
- Added a verified prebuilt registry writer for Epic's UE 5.6 `BuildId`, avoiding first-run compiler
  failures on otherwise valid author machines.
- Added precise fallback guidance for missing .NET Framework SDK and Visual Studio C++ components.

## 0.9.0-beta.2

- Fixed Asset Registry writer builds failing with UnrealBuildTool exit code 6 when Batcomputer was
  located in a deeply nested folder.
- Writer source now builds from a short local cache and remains fingerprint-checked before reuse.

## 0.9.0-beta.1

- First public beta.
- One-suit and multi-suit mods for Loomirr's LOTDK UE4SS.
- Playable/cutscene donor separation and broad character-part indexing.
- Game-material templates, face helpers, texture cooking, equipment, gliders, and compatible
  animation data.
- Custom static-mesh OBJ attachments with 3D placement and baking.
- Identity, menu metadata, StringTable, tags, registry, packaging, installation, and ZIP creation.
- Read-only Red Brick colour previews for compatible playable bodies.
- Notes saved with each mod and clearer build diagnostics.
- Guided setup and full UI layout audit.

See the repository's [`CHANGELOG.md`](https://github.com/Loomirr/Batcomputer/blob/main/CHANGELOG.md)
for the source-distribution copy.
