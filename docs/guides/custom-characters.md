# Custom characters

The **Characters** tab creates independent playable roster entries, not additional suits under Batman or another native character. Custom characters and their suit variants have been tested in-game. The editor is still in beta, so back up your save before testing.

## Create a character

1. Open **Characters → New character**, or **Copy a saved suit** to start with an existing design.
2. Enter a display name and a unique permanent ID, such as `Ragman`. The menu suggests an ID and previews the exact character/variant tag before creation. IDs use letters and numbers, starting with a letter; invalid entries are explained inline.
3. For a blank character, choose its native visual/gameplay base. For a copied design, its donor and authoring choices are retained.
4. Use the familiar Base, Parts, Materials, Faces, Textures, Animations, Abilities, Equipment and Gliders categories. The same existing-rig restrictions apply to skinned meshes.
5. Set the portraits/suit icon in **Base → Set icons**. Review the owner in **Character identity**.
6. Add the character to a mod in Home, then Build Mod.

The default suit is unlocked and receives `Pawns.Playable.Ragman.Ragman`, plus its own progress tag. The native donor still supplies gameplay machinery; it does **not** own the new roster entry. Display names and descriptions can change, but IDs stay fixed to protect saved selections and dependent suits.

Copying preserves the original project. Project-owned geometry sources, textures and materials get independent outputs. Native game assets and references to other tool-library assets remain shared references; editing a shared library asset still has the normal tool-wide meaning.

Copying rebuilds and verifies the new textures from the saved PNGs **before** copying material dependencies, so an outdated source cook no longer blocks character creation. If a PNG or donor template is genuinely missing, the error names it: restore/reimport that source in the original suit, then retry. Packaging still rejects incomplete or uncertified cooks; it does not silently substitute older archived textures.

Saved-design and character pickers include searchable lists and selection details. The base picker separates **Native characters** from **Your characters**; the latter creates a new suit, while the gameplay-donor picker only accepts native playables.

## Add another suit

Open the character and choose **Add a suit**, or use **Suits → Use your character** / **Base → Your custom characters**. Saved characters also appear in the visual-base picker, separately from native gameplay donors. Pick the saved character and enter a new variant ID, such as `NoHood`.

The new project opens in **Suits**, with a copied design and `Pawns.Playable.Ragman.NoHood`. Change its parts or abilities independently. The default character project is automatically included when building a mod containing this child suit. A missing or explicitly disabled parent blocks the build with a dependency message. Characters with saved child suits cannot be deleted from the tool until those suits are removed.

## What ships

Home keeps **Characters** and **Suits** in separate sections; a character includes its default suit, while additional variants remain suit projects. **Manage content** controls which saved projects are included in a mod. The release screen and top bar count characters and suits separately. Copying a design does not remove the original suit from an existing mod.

Build preparation repairs outdated or missing texture cooks from each project's saved PNG recipes before checking material dependencies. You should not need to wait or repeatedly reimport valid sources. If repair fails, open the named project's **Textures** category and restore its source with **Reimport image** or **Replace image**, then retry **Build Mod**. Missing templates still require a valid cook profile; uncertified packages remain blocked.

Build Mod generates the character's native group, unlocked/saveable progression entries, variant metadata, localized names, gameplay tags, primary-asset registry entries and additive roster configuration. Keep the entire generated install/release layout: the trio alone is not sufficient. No runtime DLL changes are required by this workflow.

Both **Full character extraction** and **DeveloperResearch** include `PROG_Characters`. Older developer extractions could report success while omitting it; update Batcomputer before refreshing. Donor availability is now checked before building, and incomplete new extracts do not replace the active dump.

There is no fixed character-count cap imposed by this workflow, but game-side roster, save and performance limits have not been stress-tested. Characters use new group/progression packages and additive roster configuration rather than replacing native characters or `PROG_Characters`. Keep character IDs unique across installed mods: different Mod IDs alone do not make two identical pawn/character IDs independent.

Do not install a proof/test mod and a new mod using the same character/pawn identities together. Remove the earlier proof first, or choose a different character ID.

## Current boundaries

- Scripted story cutscene casting is **not supported** for new characters. Compatible cutscene Blueprint assets are still generated as part of the normal metadata/loading structure; this does not grant a story role.
- New voices, dialogue, mission permissions, new skeletons, and unlock challenges aren't supported yet.
- Variants start unlocked. The roster group currently inherits Batman's emblem/default vehicle and native upgrade-menu flag; per-character roster presentation controls are future work. Portraits and suit icons are editable normally.
- Abilities and equipment work the same way as they do for suits. The extra VFX/status-effect experiments are still on hold because of crashes.
