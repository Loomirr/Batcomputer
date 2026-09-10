# How characters and suits fit together

The first custom character and both of its suits worked in-game. The [Characters tab](../guides/custom-characters.md) now uses that same setup.

## Character projects

A character project creates a separate roster entry and includes its default suit. It has a permanent character ID, its own pawn tag, and the group and progression data needed by the game.

The editor shares the suit tools: parts, materials, textures, animations, abilities, equipment, gliders, and previews. The gameplay donor supplies compatible gameplay and rig data; it doesn't own the new character.

## Additional suits

Once saved, a character appears under **Your characters** in the base picker. Creating a suit from it keeps the character owner and gives the new suit its own variant ID and asset paths.

For example:

- Default: `Pawns.Playable.CustomCharacter.CustomCharacter`
- Extra suit: `Pawns.Playable.CustomCharacter.NoHood`

Copying an existing suit into a character leaves the original project in place. A mod can include characters and ordinary suit projects together.

## Limits

Variants start unlocked. Scripted story roles aren't supported. Group emblems, default vehicles, and custom unlock rules aren't editable yet.

Multiple installed character mods, saved selections and co-op still need broader testing.
