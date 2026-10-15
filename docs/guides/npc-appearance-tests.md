# Experimental NPC material replacers

In the **3D viewer**, select a base-game **Quest** character and choose **NPC test…**. Choose a material directly referenced by that Blueprint, then a different extracted native material instance. Build creates a test PAK and displays its path; it does not install it.

This prototype changes material references, not the character's identity. It is separate from Characters, which creates playable characters. All uses of the selected material in the selected Blueprint change together. Every instance of that NPC Blueprint is affected. Other replacers for the same Blueprint conflict.

Supported in this first pass: concrete `/Game` Quest character Blueprints with directly referenced native material instances. Not supported: inherited-only materials, DLC targets, new attachments, mesh replacement, arbitrary NPCs or changes to AI, dialogue and quests. Material compatibility still needs visual testing.

The builder requires exact source round-trip compatibility, checks export payloads remain unchanged, checks the edited name map, preserves original extracted files, and verifies packaged bytes through extraction. These checks do not establish in-game quest compatibility. Test a backed-up save before sharing. Close the game before adding or removing the test PAK; install one conflicting test at a time.

For repeatable internal tests:

```text
Batcomputer --build-npc-appearance-test recipe.json extractedContent mappings.usmap UnrealPak.exe newOutputDirectory
```

The JSON recipe contains `Target`, `Material` and `Replacement` Unreal package paths. Existing output directories are rejected. No automatic installer or NPC registration is involved.
