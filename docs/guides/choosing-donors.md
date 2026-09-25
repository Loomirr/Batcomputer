# Choose visual and gameplay donors

The **visual base** is the look. The **gameplay donor** is the playable setup. They can be different,
but choosing a visual does not adapt all of that NPC's powers, animations or scripted behavior.

| You want to change… | Start here |
| --- | --- |
| The character's overall appearance | Base → Change visual base |
| Its native playable setup | Gameplay donor selection during base setup |
| A shipped body variant | Parts → Native body profiles |
| Just hair, a cowl, cape or another part | Parts |
| A material or print without changing geometry | Materials / Textures / Faces |
| A wholly custom weighted body | The skinned-mesh workshop and its native reference rig |

## Start from a known-good base

1. Create a test suit in a mod, then open **Base**.
2. Choose the desired indexed visual. A supported cutscene or `_Quest` visual can be used even if it has no playable version.
3. Choose a compatible **playable** gameplay donor when prompted. NPC appearance is not proof that the NPC has player controls.
4. Confirm the resulting visual and gameplay names in the Base summary.
5. Open **View in 3D** and inspect the assembly, then check and build before adding custom edits.

If the summary unexpectedly says Batman 2025 after you chose Joker, stop there. Update Batcomputer,
refresh the current assets and part index, and select the exact visual and gameplay donor again.
Do not keep adding materials to an unintended base.

## Joker, Harley and DLC characters

Joker and Harley's installed playable variants can be used as bases. DLC has to be installed on
the authoring PC, and players need the relevant content too. After installing a pack or changing
mappings, run **Refresh game assets → Refresh all character assets**, then **Refresh part index**.
A catalog entry alone cannot supply files from an uninstalled DLC pack.

If an older Joker/Harley project still has retired unlock references, rebuild it with the current
tool. Reinstall the whole mod and restart the game, rather than reusing an old packaged trio.

## Ivy, Freeze and other NPC visuals

Use the indexed NPC/cutscene visual with a compatible playable donor. Choosing Harley or Joker
for gameplay does not automatically make every Ivy or Freeze rig, face or gadget compatible.
Keep native face/material families matched, and treat unusual body/controller combinations as
experimental. Missing or incompatible native data can still block a base.

For an independent roster entry, create a **custom character**, not only a renamed suit. Keep its
display name and give it a [unique pawn-tag family](character-identity.md). Then set
[Normal, Mayhem or Both](character-modes.md) independently of the visual's name.

## Body size is not rig import scale

Supported Minifig/Smallfig profiles use the shared native LEGOfig skeleton but have different body
proportions and animation compatibility. Pick the body before fitting replacement parts.
Do not use FBX import scale to turn a minifig into a different body type.

Check takedowns, traversal and face animation after a body change. Sharing a skeleton does not
guarantee every synchronized animation fits every body.
