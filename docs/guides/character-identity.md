# Change a character's identity

Your character can be called **Poison Ivy** or **Mr Freeze** without borrowing the game's reserved
NPC pawn-tag family. The display name, project ID and pawn-tag family have different jobs.

| Field | Purpose | Change after creation? |
| --- | --- | --- |
| Display name | The readable name | Yes |
| Character/project ID | Stable saved-project and generated-asset identity | No |
| Pawn-tag family | Runtime grouping for the character and its suits | Yes, on the character definition |
| Gameplay donor | Native playable behavior used as the starting point | Separate from the character's name and tag family |

Changing the family does not require remaking the character or renaming its display name.

## Example: give Poison Ivy a unique family

Suppose the character ID is `PoisonIvy` and the display name is **Poison Ivy**. A unique family such
as `MMPPoisonIvy` produces the default tag `Pawns.Playable.MMPPoisonIvy.PoisonIvy`.
The display name stays **Poison Ivy**. Use your own distinctive prefix instead of copying an
example already used by another mod.

1. Back up the mod and its character/suit projects.
2. Open **Characters** and load the character's **default definition**, not one of its extra suits.
3. Choose **Character identity & modes**.
4. Change the pawn-tag family to a unique value using letters and numbers, starting with a letter. Leave the project ID unchanged.
5. Check the display name and mode choice, then save. Batcomputer updates the saved child-suit tags with the parent family.
6. Reopen an extra suit and check that it belongs to the updated family.
7. Run **Check mod** and rebuild the whole mod, including the default character and its enabled suits.
8. Replace the old installed release completely and cold-launch the game. Do not keep a second old build with the previous identities installed.

Native families such as `PoisonIvy` and `MrFreeze` are reserved. Reusing them for an independent
character can collide with native discovery even when the cooked assets themselves are readable.
An extra suit inherits its custom parent's family; edit the parent rather than trying to split a
child into a new roster entry.

## What stays unchanged?

The character/project ID and generated asset identity stay stable. The family edit is not a
progression reset or a save-game migration. Existing saved selections that refer to an old tag may
need to be reselected in-game. Keep the old release and a save backup when testing an already
published character.

Keep the default character and its child suits in the same mod. Shipping the same default
definition in two separately installed releases creates another identity collision.

## If the character still does not appear

A unique tag solves identity collisions, not every discovery problem. Check the character's
[mode choice and installed framework](character-modes.md), install the full release bundle
including registry/tag files, and test after a complete game restart. Do not reset the game save
as the first troubleshooting step.
