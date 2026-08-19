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
