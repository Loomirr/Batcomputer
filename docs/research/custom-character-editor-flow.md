# Character projects and suit projects

User requirements recorded 6 September 2026. The Moon Knight proof and both suits were confirmed working in-game. Initial editor integration is now implemented; see [Custom characters](../guides/custom-characters.md). Scripted cutscene casting is explicitly out of scope. Permanent owner/variant IDs and initially unlocked progression are the first supported identity model; group emblem/default-vehicle and unlock-rule editing remain future work.

- **Characters** is a separate main tab, alongside **Suits**. A character is an independent roster owner, not a suit disguised as one.
- Mods can contain character projects and suit projects. Adding a character includes its default suit and its required group, registry, localization, tag and progress assets.
- Character editing should reuse the suit editor's categories (parts, materials, textures, animations, abilities, equipment and previews). Shared controls should not be duplicated into a second independent implementation.
- Character identity replaces suit-only identity controls: character name, unique owner tag, group/emblem, default variant and unlock settings. Gameplay/rig donor remains separate from roster identity.
- A created character becomes available as a **suit base**. New suits inherit that character's owner, group and compatible gameplay defaults, but receive unique child pawn/progress tags, metadata and asset paths. Never silently put them back under the donor's original owner.
- The default suit is a real variant owned by the character. Moon Knight's first proof uses `Pawns.Playable.MoonKnight.MoonKnight`, unlocked by default. Its second variant is `Pawns.Playable.MoonKnight.NoHood`, identical except for the removed head attachment in playable and cutscene assets.
- Reusing a saved user suit for character creation must copy its recipe and dependencies without moving or altering the original.

Before editor integration, prove native roster discovery, both suits, independent progression, save/restart, character switching and cinematic loading. Keep the existing suit-only donor-owner checks until explicit character ownership is modeled; do not remove those checks globally.
