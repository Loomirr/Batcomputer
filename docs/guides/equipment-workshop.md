# Equipment workshop (experimental)

Create a suit-local equipment derivative under **Equipment → + Custom equipment**. This is separate from an Abilities held prop: it retains the selected gadget's controls, equipment slot, abilities and native upgrade references. It does not overwrite the shipped gadget or change either runtime DLL.

## Workflow

1. Select a suit, open the workshop, and choose base equipment. The support banner explains whether customization is available or why the equipment is **View only**. Only donors with a confirmed playable owner qualify; a boss archetype is not proof of playability.
2. Start in **Models & icons**, or use the category filter and search to find a part. Select a held/projectile model and choose **Import / align a 3D model**. Import an OBJ, align/scale it against the native reference, toggle either model's visibility, and assign cooked materials to the OBJ's slots. Validate and use the recipe.
3. Edit held and projectile components separately. To reuse a finished custom OBJ, choose **Copy model and settings**, select another static-model part, then **Paste model and settings**. This copies the OBJ, scale, position, rotation and material slots, replacing that destination's existing model/material overrides. Copies stay independently editable; review alignment against each part's native reference. Upgraded projectiles can have their own meshes, so paste onto each variant you want to change.
4. For existing static meshes, materials or textures, enter a compatible cooked package path and choose **Use this game asset**. Import or cook your textures in the Textures category first. **Show technical paths** exposes the exact owner, component and asset reference. Reset a part to discard its override.
5. **Save equipment**, choose the equipment slot, then **Build mod**. Reopen its custom Equipment tile to edit it. The saved recipe embeds the OBJ and transforms, so subsequent builds do not depend on the original OBJ file remaining in place.

The name identifies the recipe in Batcomputer; it does not currently author new localized equipment-name text. Selecting a different donor starts a new recipe and asks before discarding current edits. Cancel does not change the saved suit. Selecting an ordinary native gadget for the same slot removes that slot's custom recipe.

Model copying is local to the open equipment workshop, not the Windows clipboard or a persistent library. Copy requires a custom OBJ; paste only works on static-model parts of that same recipe. It does not copy icons, effects, gameplay settings or the original component's identity, and cannot bypass view-only restrictions.

## Equipment icons

For the usual HUD icon workflow, you only need **one white image with a transparent background**. Leave a little empty space around the shape. Batcomputer uses its alpha channel to make the colored SDF texture; you don't need to draw that colored version yourself.

1. Open **Textures** and import the white PNG.
2. Choose **Equipment HUD icon (transparent PNG)**. This is the conversion profile.
3. Open your custom equipment and find the matching **HUD SDF / direct** and **HUD SDF / material** entries.
4. Select the same generated texture for both. Click **Use this game asset** after choosing it in each entry.
5. Click **Save equipment**, rebuild/install the mod, and restart the game.

Cooking a texture does not assign it. You can choose a saved cook from the picker or paste its cooked `/Game/…` path. The picker also includes UI cooks from other saved suits and characters. If one is missing or outdated, reimport it in the project that owns it.

### Which entry takes which image?

| Workshop entry | What to assign |
|---|---|
| **HUD SDF / direct** | The generated SDF cook |
| **HUD SDF / material** | The same SDF as the matching direct entry |
| **Upgrade SDF / direct** and **Upgrade SDF / material** | A matching SDF for that upgrade; use the base icon for both if you want the same appearance |
| **Color / alpha (BCA)** | A color/alpha cook made with **Equipment color icon (BCA)** |
| **Aiming reticle** | A texture compatible with that reticle's original material |

The native Batarang's direct HUD reference and icon material both use the same SDF. Its BCA image is not a second required input for those bindings. Upgrade variants have separate pairs: leave both native unless you want to change them.

### Already have an SDF?

Use **Equipment HUD / upgrade icon (SDF)** only for artwork that is already encoded correctly. It preserves the channels; it does not turn a normal white PNG into an SDF. This is an alternative to the white-image workflow, not an extra step.

The current menu still offers all three profiles. For a normal white source image, stick to **Equipment HUD icon (transparent PNG)**.

### Current limit

Automatic HUD conversion is still experimental. The generated shape can look right in an image viewer without rendering correctly in every HUD state. The native shader's full channel behavior is not yet verified, so test the base and upgraded icons in-game before sharing a mod. Equipment icon cooks are kept separate from suit and character portraits.

## Materials and effects

Custom OBJ materials take precedence over the component's original override materials. Set those slots in the 3D editor; conflicting component overrides are rejected. The preview uses neutral/reference and colored-slot alignment shaders, not a final in-game material renderer.

Native effects and audio are visible for inspection but read-only in this pass. Leftover Batarang trails, impact effects or sounds are therefore expected. This does not re-enable the parked extra-effects/status experiments.

## Limits and testing

- Static meshes are the supported model-baking path. Equipment with a primarily skeletal body (including Rubber Bullet Gun and Foam Gun) is **entirely view-only**, including its static bullets, icons and materials. Counting static bullet references does not make a skinned gun eligible. A clearly static main body can retain editable static parts while secondary skeletal components remain locked.
- Model changes do not author new firing logic, projectile movement, damage, sockets, reload animations or upgrades. The OBJ baker reuses its verified static-mesh donor collision shell; it does not generate collision fitted to the new shape. Keep visual changes modest and test aiming/hits.
- NPC/boss gear, equipment without a confirmed playable owner, uncataloged variants and unverified/mismatched definitions are **view-only**. The same policy is enforced during custom-equipment builds, so old saved recipes cannot bypass it. Restore the slot to native equipment or choose a supported custom donor if an old recipe is blocked. Existing ordinary native loadout swaps are unchanged.
- First-time extraction and Full refresh include gadget/upgrade HUD assets and narrow equipment model dependencies. A missing story-specific actor can still block a particular variant rather than produce an incomplete clone.

The banana metadata proof and inherited Batarang upgrades are confirmed in-game. Earlier offline Rubber Bullet Gun experiments are now parked behind the skeletal-body restriction. Test an eligible tool-built mod before sharing it: equip/switch, HUD, held model, quick fire, aimed fire, upgraded projectiles, restart persistence, and an ordinary suit retaining its native gadget.

See the [equipment inventory](equipment-inventory.md) for the current extraction audit.
