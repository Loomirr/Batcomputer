# Equipment HUD icon binding audit

## Confirmed native references

`BP_Batarang_ED.HudIconMtl` references `MI_UI_IconGadgetBatarang`. The base projectile data's `HudIcon` references `T_UI_IconBatarang_SDF`, and its `HudIconMtl` references that same material. The material's texture parameter uses the same SDF; its parent is the UI-domain `M_UI_SDFIconGadget`.

Alarmarang, Batswarm and Concussive variants have separate texture/material pairs. No Batarang BCA reference was found in the inspected equipment graph. The presence of a BCA asset does not mean these HUD bindings require it.

## Custom-character equipment audit

The inspected build's equipment definition references its cloned gadget icon material. That material retains the native parent and uses the automatically generated SDF. Direct projectile icon references instead use a separately prepared SDF. This confirms the saved references, not successful runtime loading or final HUD rendering.

## Channel uncertainty

Native Batarang SDF pixels contain opaque alpha, blue 77, and varying red and green data. Pixel (32,32) is RGBA (74,152,77,255); (20,20) is (200,7,77,255). A red-only silhouette with zero green is not equivalent to this authored image. These observations do not establish the shader equations or prove green causes the missing HUD icon.

The cooked material property export does not expose an editable expression graph. Alpha-to-SDF conversion remains experimental, not a verified recreation of every native HUD state. Saved images must not be silently reinterpreted.

## Next controlled test

Assign one prepared SDF to both matching direct and material inputs. If missing, restore only the native SDF texture in the same cloned material to separate authored-channel problems from clone/loading issues. Test base and upgraded states independently. No runtime DLL change follows from current evidence.
