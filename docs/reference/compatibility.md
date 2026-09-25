# Compatibility and limits

Batcomputer 1.0 is the first stable application release. Some authoring workflows are still
experimental; stable packaging does not make every donor combination compatible.

## Available in 1.0

| Area | Supported workflow | Important limit |
| --- | --- | --- |
| Suits and characters | Native-character suits, independent characters and their extra suits | Unique identities and a compatible playable donor are required |
| Game modes | Normal, Mayhem or Both for custom characters | Requires a compatible installed framework with integrated mode support |
| Visual bases | Indexed playable, cutscene and supported quest visuals, including installed DLC | NPC visuals do not create NPC powers or player controls |
| Body profiles | Nine shipped Minifig/Smallfig body variants on the shared LEGOfig rig | Shared bones do not guarantee matching takedown proportions |
| Parts and materials | Indexed native parts, compatible material templates, textures, face maps and icons | Face/UV families and native shader contracts still matter |
| Mesh imports | Rigid OBJ attachments and experimental weighted FBX on existing native rigs | No arbitrary skeleton transfer, auto-rigging, cloth or new morph targets |
| Abilities and held items | Native loadouts, fighting-style adapters, independently configured hand props | A decorative item alone does not grant weapon attacks |
| Equipment | Supported native gadget derivatives, model/icon edits and experimental native-rig FBX components | Support varies by donor; NPC gear can lack player-compatible logic |
| Vehicles | Native driving donors, body/part/material/light edits and separate assembly-model imports | Experimental; donor physics and rig constraints remain |
| Animation | Suit-local compatible sequence/montage/layer/locomotion overrides and a cooked import library | Not an arbitrary FBX animation importer or retargeter |
| 3D workshop | Assembly inspection, supported material previews, custom static-part placement and reference GLB export | Not a full Unreal renderer or runtime simulation |
| Sharing | Player release ZIPs and supported editable creator archives | Imported animation libraries are not transferred in creator archives |
| Application updates | Verified download, restart, completion prompt and application-file recovery | 1.0.0 uses a full ZIP; smaller downloads require a release file catalog |

The supported body profiles are standard Minifig, Minifig 08, headless Minifig, armless Minifig,
Minifig without its left hand, Minifig without its upper body, standard Smallfig, Smallfig 08 and
armless Smallfig. They keep the gameplay donor and use native `SKEL_LEGOfig`.

## Not provided

- New scripted story roles, dialogue/voice authoring or arbitrary unlock challenges.
- Arbitrary gameplay code, enemy AI-to-player conversion or universal new powers.
- New skeletons, cloth systems, morph targets or automatically fitted collision.
- New Red Bricks or physical collectible placement. Red Brick colors in the viewer are preview-only.
- Exact game shaders, procedural animation, cloth or physics in the offline viewer.
- A safe combination of any cape and any glider controller. Use a supported **Glide cape** preset
  and its matching regular cape from the same native variant, or remove the regular cape for a glide-only visual.

## Experimental combinations

Test custom skinned bodies/attachments, skeletal equipment, cross-family fighting styles,
unusual body sizes, vehicle driving/assembly models and multi-character/co-op behavior separately.
A successful cook and build check cannot prove the runtime behavior of all those systems.

!!! warning "Held-item effects and on-hit status"
    Expanded effect/status experiments have caused suit-hover crashes. Treat those controls as
    on hold, not a supported release feature. A working native baton trail does not prove an
    arbitrary set of particles or statuses is safe.

For vehicles, test the driving and parked/summon appearances separately. A working driving body
does not establish that the separate assembly model is correct for every donor.

## After a game update

Use mappings for the installed game, refresh all character assets, refresh the part index, and
repair/rebase only projects that need it. Then check, rebuild and cold-test affected mods.
An asset being readable in an extractor is not proof of game compatibility.

See [Update or repair a suit](../guides/update-repair-suit.md) for the recovery order.
