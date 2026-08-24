# Create your first suit

This tutorial starts with the game's existing assets. Test one simple suit in-game before adding
custom textures or meshes.

## 1. Create the mod

From **Home**, create a mod and set:

- **Display name:** the player-facing release name.
- **Mod ID:** a stable technical identifier with no spaces.
- **Description:** a short summary of the collection.

A mod can contain one suit or many suits. Shared files such as its StringTable are generated once
for the whole mod.

!!! danger "Do not casually rename a published Mod ID"
    The Mod ID controls pak, registry, StringTable, runtime-manifest, and install paths. Batcomputer
    can migrate a local project, but changing it after release is a breaking identity change.

## 2. Add a suit

Create a suit inside the active mod. Give it a unique suit ID and display name.

## 3. Choose the bases

Batcomputer separates two jobs that the game often stores in different assets:

- **Visual base:** supplies the visible character assembly. This can be a playable or cutscene
  character.
- **Gameplay donor:** supplies gameplay-facing playable behavior and metadata.

For the first test, choose a playable donor close to the character family you are making. Select
**Use as base** and wait for the generated playable and cutscene assets to complete.

![Base character browser](../assets/screenshots/base-character-picker.jpg){ .bc-doc-shot loading=lazy }

![Suit base workspace](../assets/screenshots/suit-base-workspace.jpg){ .bc-doc-shot loading=lazy }

## 4. Customize the character

Use the left navigation:

- **Parts** — graft compatible hair, hats, capes, torsos, accessories, and other indexed components.
- **Materials** — copy working game materials and apply them to mesh slots.
- **Faces** — choose only recipes compatible with the selected face mesh family.
- **Textures** — import and cook body maps, masks, and UI icons.
- **Equipment / Gliders / Animations** — apply compatible data from another character, then test it
  in-game.

The right inspector shows the playable and cutscene component trees and their material slots.

### Give a glide-only character a regular cape

Use this order when the gameplay donor has a wingsuit or another glide-only visual, but the finished
suit should use a normal cape and a matching glide cape:

1. Refresh the native part index from the main menu if the donor parts were extracted recently.
2. Open **Gliders**, choose **Glider presets**, and filter to one native **Glide cape** donor.
3. Open that preset and choose **Use preset**. Batcomputer records the donor's complete glide
   component, including its animation Blueprint, materials, visibility tags, and matching body pose.
4. Open **Parts** and apply the regular cosmetic `Cape` from the exact same character variant as the
   glide preset. Do not use a custom OBJ cape or a cape from another donor pair.
5. Batcomputer certifies the two parts as one dynamic adapter. It preserves the selected gameplay
   donor's normal movement, combat, equipment, and appearance, but replaces its glide-only animation
   categories with the cape donor's matching traversal and montage blocks.
6. Run **Check mod**, build, and cold-launch the game. Test standing cape visibility, glide opening,
   sustained flight, landing, and the character's normal combat/movement set.

The glide preset must be applied before the regular cape. If the exact playable/cutscene donor pair
or the verified paired-cape controller cannot be resolved, Batcomputer blocks the combination rather
than producing a double-cape or crash-prone suit.

## 5. Set identity and icons

Open **Native identity** and review:

- The unique PawnTag.
- The character family represented by the tag.
- The DCMD and UIMD identities.
- Display name and description StringTable entries.
- Menu, suit, left, and right icon paths.

Use unique PawnTags and package paths. Duplicate identities are blocked by release validation.

## 6. Inspect in 3D

Open **3D viewer** and check the assembled body, face, materials, and attachments. The viewer is a
close preview, not the game's renderer.

For custom static meshes, transform edits save to the project and **Bake to game** rebuilds the
generated mesh. Placement controls for normal game parts affect only the preview.

## 7. Check and build

Return to the mod's Home page:

1. Choose **Check mod**.
2. Resolve every error. Read warnings instead of dismissing them automatically.
3. Close the game and tools that may hold output files open.
4. Choose **Build mod**.

A successful build creates the cooked release and installs it for local testing. A failed build
does not install an older trio in its place.

## 8. Cold-launch test

Fully exit and restart the game. Confirm:

- The suit appears under the intended character.
- Hovering it shows the correct icon, name, description, and preview.
- Selecting it swaps to the generated playable.
- Returning to the frontend and reloading gameplay preserves the selected suit.
- Materials, textures, equipment, glider, and animations behave as expected.

Once this works, continue with [Build, test, and share](build-test-share.md).
