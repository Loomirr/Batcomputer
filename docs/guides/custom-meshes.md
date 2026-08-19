# Custom static meshes

Batcomputer can import supported OBJ geometry as a **static-mesh attachment**. This is intended for
items such as cowls and accessories that can follow an existing character socket.

## Import

1. Open **Parts** and start a custom mesh import.
2. Select the OBJ file.
3. Give the mesh a clear display and object name.
4. Choose the target component role and attachment socket.
5. Set the initial scale, offsets, and rotation.
6. Import, then open the suit in the 3D viewer.

Use the generated name shown by Batcomputer, not a temporary source filename or UUID, when reviewing
the component tree and material assignments.

![Custom static-mesh import window](../assets/screenshots/custom-mesh-import.jpg){ .bc-doc-shot loading=lazy }

## Place the mesh

In the 3D viewer:

1. Select the custom mesh.
2. Adjust scale, X/Y/Z offset, pitch, yaw, and roll.
3. Select the intended UV channel when relevant.
4. Save the preview state.
5. Choose **Bake to game** before packaging.

Offsets use Unreal centimeters. The viewer converts them for preview, and the bake step
rebuilds the generated cooked mesh using the saved values.

## Apply a material

Custom meshes expose material slots in the inspector. Generate or select a compatible material,
then drag or apply it to the custom mesh slot as you would for a normal game part. Reopen the 3D
viewer and confirm the material resolved on that mesh before baking.

## Current limits

- OBJ static meshes only.
- No custom skeletal-mesh cooking or skeleton transfer.
- No custom collision or physics setup.
- Geometry that does not fit the tested game-mesh template may be rejected.
- The viewer cannot reproduce every game shader, animation, or lighting condition.

Always test the baked result in-game before publishing.
