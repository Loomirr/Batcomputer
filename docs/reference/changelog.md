# Changelog

## 0.9.0-beta.8

- Added the exact native Minifig and Smallfig body choices without changing the selected gameplay
  donor's playstyle or shared skeleton.
- Tool-created materials can be found and reused from **All tool materials** in another suit, and
  older assignment-only materials are recovered into the workspace library automatically.
- Native parts can be inspected in 3D with their attachment and resolved material recipes before
  being applied.
- Saved bases move from a retired absolute extract path to the active Content folder when the exact
  `/Game` package still exists.
- Diagnostics keeps its reading position when new lines arrive, and the 3D viewer's material panel
  is smaller and starts in the lower-right corner.
- Generated textures now include the complete mip chain through 1 pixel instead of leaving the
  lower-quality inline levels from the donor.
- The native 2K DXT1 MMR profile is proven in game on Electric at every texture-quality setting. It
  preserves linear packed-map sampling and recognizes older MMR names previously saved as
  Character textures.
- Cooked texture files are hashed and checked together before staging, so an interrupted or changed
  output cannot reuse an old successful report.
- Older assignment-only tool materials are recovered from the workspace library and included in a
  fresh mod build instead of being left behind.
- Registry writer failures now explain an unavailable Win64 SDK directly, and Diagnostics records
  why the bundled prebuilt was rejected before trying a source build.
- Custom OBJ components keep the cooked Blueprint's original class schema, preventing the known
  load, hover, and startup crashes caused by adding a reflected field to an opaque class-default object.
- Packaging now verifies every custom mesh's source OBJ, cooked files, live construction-script
  binding, socket, and gameplay/cutscene material instead of trusting a partial stage.
- A paired custom-cowl template keeps the game's LEGO surface detail without inheriting the donor
  cowl's geometry-specific maps, metallic switch, or extreme roughness offset.
- Material warnings flag suspicious metalness, roughness, packed-channel, and duplicate-normal
  choices while leaving specialized game material families alone.
- Shared materials bring their mod-local textures and parents into another suit. Renames, deletions,
  viewer placement saves, and release staging restore the last working state if a rebuild fails.

## 0.9.0-beta.7

- A failed base selection, reselection, or saved-edit replay now restores the previous project and
  generated files instead of leaving the suit empty.
- The part index follows the active extracted Content folder and has a dedicated **Refresh part
  index** command in the main menu.
- Older saved parts recover their full native donor only from an exact package, mesh, and
  playable/cutscene match. If that match is missing, the working generated files are left alone.
- Base changes preserve project-owned OBJ sources, including when a rebuild cannot finish.
- Batmite `_Quest` visuals can be paired with a Robin playable donor without returning to the base
  picker or losing the selected appearance.
- Glide-only gameplay donors such as Nightwing can use a supported native regular-cape/glide-cape
  pair while keeping their appearance and normal playset. The matching cape donor supplies the
  glide animation, and competing glide controllers are blocked before packaging.

## 0.9.0-beta.6

- All top-level windows and dialogs are resizable and constrained to usable single-monitor bounds,
  with responsive layouts for smaller and high-DPI displays.
- Extracted `_Quest` Blueprints, including Smallfig characters such as Batmite, can be selected as
  visual bases and are paired with an explicit playable gameplay donor.
- The native part index now scans Minifig and Smallfig recipes and invalidates older Minifig-only
  caches so they are rebuilt when needed.
- Every extraction profile includes the shared transparent cape material dependency.
- Role-specific playable and cutscene recipes are preserved for extra parts such as Mr. Freeze's,
  and appended components receive the generated-class and construction-script links needed in-game.
- Unsafe synthetic cape/glider layouts are blocked; native paired-cape bases retain the supported
  visibility behavior.
- Third-party build-machine debug paths are removed from the portable single-file executable.

## 0.9.0-beta.5

- Cutscene visuals now always open the gameplay-donor picker, and Catwoman playables resolve their
  Catwoman archetype instead of falling back to Batman.
- Custom OBJ meshes can be edited or removed and are replayed with native parts, removals, and
  materials after changing the base.
- Custom-mesh transforms baked in the 3D viewer now remain in the live suit recipe when an unrelated
  part removal triggers a clean rebuild.
- Generated-file sharing violations receive bounded retries; incomplete playable/cutscene grafts
  stop the rebuild and cannot be packaged as a partial suit.
- Fixed the first saved-edit replay on a fresh suit when no part stage exists yet.
- Fixed registry-writer launches from spaced Unreal paths, structured registry verification, and
  punctuation leaking from display names into Unreal identifiers.
- Incompatible remote-controller families and unsupported regular-cape/glider combinations are
  blocked with actionable guidance. Replacement gliders must use a verified paired-cape controller,
  while wingsuits and other glide-only visuals require the regular `Cape` to be removed; older saved
  projects receive the same package-time check.
- Batman and Batgirl glide-cape controllers remain supported, Batman glide capes are called out in
  the Gliders browser, and the selected donor's controller identity is preserved.
- Suit and mod outputs are prepared in separate build attempts, and only a fresh, complete IoStore
  trio can be published or installed. Failed installs restore the previous trio.

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
