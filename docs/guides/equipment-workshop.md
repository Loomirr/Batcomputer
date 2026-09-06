# Equipment workshop (experimental)

Create a suit-local equipment derivative under **Equipment → + Custom equipment**. This is separate from an Abilities held prop: it retains the selected gadget's controls, equipment slot, abilities and native upgrade references. It does not overwrite the shipped gadget or change either runtime DLL.

## Workflow

1. Select a suit, open the workshop, and choose base equipment. The support banner explains whether customization is available or why the equipment is **View only**. Only donors with a confirmed playable owner qualify; a boss archetype is not proof of playability.
2. Start in **Models & icons**, or use the category filter and search to find a part. Select a held/projectile model and choose **Import / align a 3D model**. Import an OBJ, align/scale it against the native reference, toggle either model's visibility, and assign cooked materials to the OBJ's slots. Validate and use the recipe.
3. Edit held and projectile components separately. Upgraded projectiles can have their own meshes; changing the held mesh does not automatically change every projectile variant.
4. For existing static meshes, materials or textures, enter a compatible cooked package path and choose **Use this game asset**. Import or cook your textures in the Textures category first. **Show technical paths** exposes the exact owner, component and asset reference. Reset a part to discard its override.
5. **Save equipment**, choose the equipment slot, then **Build mod**. Reopen its custom Equipment tile to edit it. The saved recipe embeds the OBJ and transforms, so subsequent builds do not depend on the original OBJ file remaining in place.

The name identifies the recipe in Batcomputer; it does not currently author new localized equipment-name text. Selecting a different donor starts a new recipe and asks before discarding current edits. Cancel does not change the saved suit. Selecting an ordinary native gadget for the same slot removes that slot's custom recipe.

## Icons, materials and effects

HUD icons can be material-backed. The workshop includes the icon material's texture parameters, allowing a texture replacement inside a cloned material. Many gadget icons use **SDF textures**: an ordinary color PNG imported as a suit icon is not automatically a correct gadget SDF. Use a compatible cooked SDF texture or material. Upgrade icons are separate bindings.

Custom OBJ materials take precedence over the component's original override materials. Set those slots in the 3D editor; conflicting component overrides are rejected. The preview uses neutral/reference and colored-slot alignment shaders, not a final in-game material renderer.

Native effects and audio are visible for inspection but read-only in this pass. Leftover Batarang trails, impact effects or sounds are therefore expected. This does not re-enable the parked extra-effects/status experiments.

## Limits and testing

- Static meshes are the supported model-baking path. Equipment with a primarily skeletal body (including Rubber Bullet Gun and Foam Gun) is **entirely view-only**, including its static bullets, icons and materials. Counting static bullet references does not make a skinned gun eligible. A clearly static main body can retain editable static parts while secondary skeletal components remain locked.
- Model changes do not author new firing logic, projectile movement, damage, sockets, reload animations or upgrades. The OBJ baker reuses its verified static-mesh donor collision shell; it does not generate collision fitted to the new shape. Keep visual changes modest and test aiming/hits.
- NPC/boss gear, equipment without a confirmed playable owner, uncataloged variants and unverified/mismatched definitions are **view-only**. The same policy is enforced during custom-equipment builds, so old saved recipes cannot bypass it. Restore the slot to native equipment or choose a supported custom donor if an old recipe is blocked. Existing ordinary native loadout swaps are unchanged.
- First-time extraction and Full refresh include gadget/upgrade HUD assets and narrow equipment model dependencies. A missing story-specific actor can still block a particular variant rather than produce an incomplete clone.

The banana metadata proof and inherited Batarang upgrades are confirmed in-game. Earlier offline Rubber Bullet Gun experiments are now parked behind the skeletal-body restriction. Test an eligible tool-built mod before sharing it: equip/switch, HUD, held model, quick fire, aimed fire, upgraded projectiles, restart persistence, and an ordinary suit retaining its native gadget.

See the [equipment inventory](equipment-inventory.md) for the current extraction audit.
