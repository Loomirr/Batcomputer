# Summon and parked vehicle models

A vehicle can have more than one visible model. Importing the driving body does not automatically create a LEGO assembly animation.

| Appearance | Asset used by the current Forever donor |
| --- | --- |
| Driving | Skeletal body on the 13-bone driving rig |
| Vehicle selection model | A separate menu actor that can reuse the driving body |
| Summoning/dismissing | Skeletal mesh on a separate 42-bone summon rig |
| Active car parked in the Batcave | An equipped actor with a summon component and its own mesh reference |

This covers the active parked car, not every display stand or thumbnail in the game.

!!! warning "Experimental assembly importer"
    Use **Body & setup → Assembly model** for a summon FBX. Finish the [normal driving-model workflow](vehicles.md) first. The assembly import requires the chosen donor's separate summon skeleton; do not put it in the driving-body slot. The build-up animation and parked appearance still need an in-game check.

**Use matching body materials** is on by default and matches slots by their original names, not their order. Turn it off to keep materials assigned in the assembly importer. **Use native assembly** removes the replacement from the vehicle recipe but keeps its imported source files. Custom bone-count build-up rigs are not supported by this importer yet.

## Keep a second model copy

Keep the driving model and its rig unchanged. Duplicate the finished geometry into a separate collection for summon authoring. Both versions should match in their final assembled position, size, UVs and material slots so the transition does not visibly jump.

Preserve separate bricks or logical rigid subassemblies in the source file. A joined mesh can still contain disconnected pieces; that is fine. A fully welded shell is less useful if it must break into convincing individual assemblies. Separating a single continuous panel randomly can create visible cuts.

A useful source-file layout is:

```text
Reference
  Native driving mesh + rig
  Native summon mesh + rig
Driving
  Custom driving mesh + driving armature
Summon
  Custom rigid pieces + summon armature
```

Export one intended mesh/armature pair at a time. Do not export both rigs into the same FBX.

## Use the summon rig, not the wheel rig

The Forever summon reference is:

```text
/Game/Models/Vehicles/Summon/SK_VEH_Batmobile1995_BatmanForever_Summon
```

It binds to `SKEL_VEH_Batmobile1995_BatmanForever_Summon`. Its bones are not interchangeable with `Body`, `Wheel_FL` and the other driving bones.

Keep all 42 native bones, their names, hierarchy and rest transforms. Assign each whole piece **weight 1.0 to one summon bone**. Several pieces can share a bone and move as a group; a new bone for every brick is not required or supported by this native-rig experiment.

| Example group | Intended region in the Forever reference |
| --- | --- |
| `P05_Wheel_BR_PosY` | Back-right wheel assembly |
| `P06_Wheel_FR_PosY` | Front-right wheel assembly |
| `P07_Wheel_BL_NegY` | Back-left wheel assembly |
| `P08_Wheel_FL_NegY` | Front-left wheel assembly |
| `P01_Body_AxisNegZ` and other `P##_Body_…` bones | Rigid body groups; compare their geometry in the native reference |
| `Root` | Keep the hierarchy root; the native reference has no geometry weighted to it |

Do not rename the directional suffixes to adjust the look. The native summon system exposes bone-directed movement and per-part timing. This experiment retains its component, configuration and animation class rather than creating a new Blender animation clip. The final paths and timing still need in-game checks.

![Rigid groups on an assembled custom vehicle](../assets/vehicles/summon-groups.png){ .bc-doc-shot loading=lazy }

*Blender illustration: colors identify rigid animation groups, not final materials. This example has 296 source pieces assigned to 32 of the 41 available non-root groups. All 42 bones remain in the exported rig.*

## Check the grouped copy

Move one group at a time in Blender Pose Mode. Whole pieces should move together without stretching. Check that wheels stay separate from body panels and that moving a canopy group does not pull neighboring geometry. Clear all temporary poses before export.

![Exploded illustration of the grouped vehicle](../assets/vehicles/summon-exploded.png){ .bc-doc-shot loading=lazy }

*Illustrative offsets rendered in Blender. This is not a capture or simulation of the game's summon animation.*

A one-piece control can assign the entire model to a single non-root summon bone while retaining the full rig. It helps test mesh references and completion without evaluating a detailed build-up. It does not provide individual LEGO assembly behavior.

Preserve the driving model's final assembled shape and material-slot names while preparing the
summon copy's weights. A correct Blender preview does not prove the FBX exporter preserved its
bind transforms; validate the exported mesh against the summon donor.

## Import, build and test the assembly model

1. Finish and save the driving body first.
2. Open **Body & setup → Assembly model** and export that donor's summon reference.
3. Prepare the separate weighted model, then import its FBX with scale **1.0000**.
4. Compare the final assembled shape and assign materials. Review **Use matching body materials** if slot names differ.
5. Save the vehicle and rebuild its mod. Install the complete release and cold-launch the game.
6. Test summoning, completion into the driving body, dismissing and the active parked car in the Batcave.

Assembly behavior remains experimental and can vary by donor. If driving works but summon/parked
appearance is invisible or distorted, report that separately with both donor and mesh names. Do
not replace the working driving rig to compensate for a failed summon model.

Destruction/debris authoring, new collision rigs and arbitrary display-stand replacements are not
supplied by this importer. The [vehicle menu icon](vehicles.md#vehicle-menu-icon) is a separate 2D image.
