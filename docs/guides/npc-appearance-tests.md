# Character replacers (experimental)

Replacers use the **existing suit editor**, not a separate workshop.

1. Open **Replacers → New replacer** and select the exact original character variant to override.
2. Name the project. Edit its appearance using the usual **Materials**, **Faces**, **Textures** and **Parts** tools.
3. Save and add the project to a mod through the normal mod content picker. **Build mod** can also create a mod for a project that has none yet.
4. Build, install or ZIP through the usual controls. **View in 3D** remains available; there is no separate embedded replacer viewer.

The base identifies the original Blueprint, not a new playable donor. Playable, quest and cutscene variants are separate targets. Replacing one does not automatically replace its siblings. Two enabled projects overriding the same target are rejected.

## Appearance-only output

Authoring uses mod-local working copies; extracted game assets remain untouched. At build time the edited Blueprint is restored to the original package/class identity. Non-visual properties, import identities, export metadata and opaque data are checked before writing and again after cooked reload.

Replacers create no new PawnTags, suit-menu entries, DCMD/UIMD assets, voices, equipment loadouts or animation/ability overrides. The builder rejects those declarations even if a project file is edited manually. Original AI, dialogue, quest logic and gameplay stay attached to the original character.

Replacement-only mods do not generate an Asset Registry plugin. Mixed mods still register their ordinary characters, suits and vehicles.

## Current safety limits

- Concrete base-game `/Game/Characters` Blueprints only. DLC targets and inherited-only component authoring need more work.
- Material, compatible existing-mesh and visual placement changes use the shared tools. Structural edits that change the Blueprint export/component layout currently fail visual-only verification; adding new attachments is not yet certified.
- Custom visual dependencies come from the prepared project. Missing or ambiguous sources and non-visual custom dependencies block builds.
- In-game testing is required, especially for quest/cutscene variants and mesh proportions. Offline checks do not establish quest compatibility.

Installed mods override assets through game loading; they do not overwrite original game files. Removing the mod restores the base appearance. Other mods overriding the exact same Blueprint conflict. Back up saves before testing.

Older experimental `.replacement.json` files remain supported by the internal `--build-character-replacer` diagnostic, but are not normal suit-editor projects and are not automatically migrated. Those files are left untouched.
