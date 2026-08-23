# Changelog

## 0.9.0-beta.5

- Cutscene visuals now always open the gameplay-donor picker, and Catwoman playables resolve their
  Catwoman archetype instead of falling back to Batman.
- Custom OBJ meshes can be edited or removed and are replayed with native parts, removals, and
  materials after changing the base.
- Custom-mesh transforms baked in the 3D viewer now remain in the live suit recipe when an unrelated
  part removal triggers a clean rebuild.
- Generated-file sharing violations receive bounded retries; incomplete playable/cutscene grafts
  stop the rebuild and cannot be packaged as a partial suit.
- Fixed the first declarative rebuild on a fresh suit when no graft-stage folder exists yet.
- Fixed registry-writer launches from spaced Unreal paths, structured registry verification, and
  punctuation leaking from display names into Unreal identifiers.
- Incompatible remote-controller families and unsupported regular-cape/glider combinations are
  blocked with actionable guidance. Replacement gliders must use a verified paired-cape controller,
  while wingsuits and other glide-only visuals require the regular `Cape` to be removed; older saved
  projects receive the same package-time check.
- Batman and Batgirl glide-cape controllers remain supported, Batman glide capes are called out in
  the Gliders browser, and the selected donor's controller identity is preserved.
- Suit and mod outputs are prepared away from certified authoring stages, and only a fresh, complete
  IoStore trio can be published or installed; failed installs restore the previous trio.

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
