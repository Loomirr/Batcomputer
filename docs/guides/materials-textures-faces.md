# Materials, textures, and faces

Batcomputer starts with **working game materials**. It copies a cooked material instance whose
parent and parameters already work in-game, then changes the supported values. This avoids guessing
at a cooked material layout.

## Materials

1. Select the target component and material slot.
2. Open **Materials** and choose a compatible game material or a tested template.
3. Read the donor parameters.
4. Override only the textures or colors you intend to change.
5. Generate the material and apply it to the slot.

A blank override inherits the donor value. **Set None** writes an intentional null/disabled value
where the template supports it; it is different from leaving the override blank.

![Material template browser](../assets/screenshots/material-template-picker.jpg){ .bc-doc-shot loading=lazy }

## Material families

Use templates as compatibility rules, not just visual presets:

- Character body materials.
- Fixed-color and recolorable accessories.
- Metallic plastic and cloth/cape materials.
- Standard `SK_LEGOface` materials.
- Special face rigs such as SuperheroFace.

Batcomputer warns when a selected template targets another mesh family. Do not force a standard
LEGOface recipe onto SuperheroFace, or the reverse, simply because both assets are faces.

## Faces

The face mesh and material topology must agree. Face helpers group hard-to-read parameters into
eyes, brows, lids, lashes, mouth/lower-face, and related regions.

For a Batman cowl that should use the Joker '89 lower-face print without visible eyes:

1. Keep the standard Batman `SK_LEGOface` mesh.
2. Start from the **Joker '89 lower-face print — no eyes** template.
3. Replace only the lower-face BC and normal textures with the Joker '89 donor textures.
4. Keep the Batman no-eyes values for eye regions.
5. Generate a new material and apply it to the Face slot.

The Joker '89 face mesh itself is a different target and is not a safe substitute for the standard
Batman face in this recipe.

![Face-aware material editor helpers](../assets/screenshots/face-helper-material-editor.jpg){ .bc-doc-shot loading=lazy }

## Textures

Batcomputer records a cook profile with new texture entries. Select a profile that matches the use:

- Character/body color map.
- Color mask.
- Normal or packed material map.
- Native UI/suit icon.

Do not assume all images are color masks. The texture kind controls how the result is validated and
how materials consume it.

### UI icons

Use the current verified native suit-icon profile. Old experimental BC7/DXT5 outputs may look
plausible in an extractor while decoding incorrectly in-game. After cooking:

1. Confirm the texture is present in the staged build.
2. Confirm FModel can display it at the expected path.
3. Test hover and selection in-game.

### Existing legacy textures

An old project may contain cooked textures with no recorded profile. Batcomputer preserves complete
legacy output rather than guessing how to recook it. Use **Change cook profile** before intentionally
rebuilding that texture.

## Color masks and Red Brick previews

Playable characters and modded suits with a usable body Color Mask can preview the base game's
Red Brick colours in the 3D viewer. This changes only the preview. Batcomputer does not create,
register, unlock, or package custom Red Bricks in this beta.
