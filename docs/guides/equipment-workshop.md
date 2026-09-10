# Equipment workshop (experimental)

Create a suit-local equipment derivative under **Equipment → + Custom equipment**. This is separate from an Abilities held prop: it retains the selected gadget's controls, equipment slot, abilities and native upgrade references. It does not overwrite the shipped gadget or change either runtime DLL.

## Workflow

1. Select a suit or character, open the workshop, and choose base equipment. The support banner distinguishes tested, experimental and view-only donors. Untested skeletal equipment is available with a warning; equipment without a confirmed playable owner may depend on NPC-specific abilities.
2. Start in **Models & icons**, or use the category filter and search to find a part. Select a held/projectile model and choose **Import / align a 3D model**. Import an OBJ, align/scale it against the native reference, toggle either model's visibility, and assign cooked materials to the OBJ's slots. Validate and use the recipe.
3. Edit held and projectile components separately. To reuse a finished custom OBJ, choose **Copy model and settings**, select another static-model part, then **Paste model and settings**. This copies the OBJ, scale, position, rotation and material slots, replacing that destination's existing model/material overrides. Copies stay independently editable; review alignment against each part's native reference. Upgraded projectiles can have their own meshes, so paste onto each variant you want to change.
4. For existing static meshes, materials or textures, enter a compatible cooked package path and choose **Use this game asset**. Import or cook your textures in the Textures category first. **Show technical paths** exposes the exact owner, component and asset reference. Reset a part to discard its override.
5. **Save equipment**, choose the equipment slot, then **Build mod**. Reopen its custom Equipment tile to edit it. The saved recipe embeds the OBJ and transforms, so subsequent builds do not depend on the original OBJ file remaining in place.

The name identifies the recipe in Batcomputer; it does not currently author new localized equipment-name text. Selecting a different donor starts a new recipe and asks before discarding current edits. Cancel does not change the saved suit. Selecting an ordinary native gadget for the same slot removes that slot's custom recipe.

Model copying is local to the open equipment workshop. Copy requires a custom OBJ; paste works on static-model parts and supported after-hit models in that recipe. It does not copy icons, gameplay settings or the original component's identity.

### Skeletal equipment

Select a skeletal model and choose **Import / edit weighted FBX**. Export its native reference, prepare the weights and placement in Blender, then import the FBX using that same rig. Keep native bone names and the rest pose. Assign materials and inspect the deformation preview before saving.

Gordon's rubber-bullet pistol has passed an in-game replacement test. Other skeletal equipment is **experimental**, not blocked just because it is untested. Rig, weight and cooked-file validation still apply. Batcomputer retains native animations and sockets; it does not auto-rig models or create new cloth, morph or physics setups. Test those behaviors before releasing a replacement.

## Equipment icons

Start with a **white silhouette on a transparent background**. No painted outline is needed for SDF: the material draws it. A solid black background is not transparency.

Use **256×256** as the working canvas, with about **32px of clear space on each edge**. SDF cooks to **64×64**, scaling the whole canvas without auto-cropping. Keep green accents about **12–16 source pixels wide** or larger. These are starting guidelines, not strict dimensions. Older outlined artwork still imports, but those opaque outline pixels become part of the silhouette.

1. Paint any accent areas **pure green (`#00FF00`)** on the white silhouette. Leave it all white for no accent. Import as **Equipment SDF icon (white + green PNG)**.
2. Open **Set up HUD icon material…** above the equipment parts list. Choose the SDF cook, or enable **Package path** to enter one.
3. Adjust the appearance if wanted, then **Use material**. Start with the native defaults; **Glow strength** defaults to **0.5**, and **0** disables accents.
4. **Save equipment**, rebuild/install the mod, and restart the game.

Only inputs labelled **Suggested: BCA** need an **Equipment color icon (BCA)** import. That profile preserves the painted colours, outline and alpha at 256×256. Keep green marker paint out of BCA artwork unless you want it visibly green. Use distinct names if importing both, such as `Dart_BCA` and `Dart_SDF`.

Cooking a texture does not assign it. The picker includes SDF cooks from other saved suits and characters. If one is missing or outdated, reimport it in the project that owns it. A BCA input, if present, remains a separate assignment under **Suggested: BCA → Use this game asset**. Batarang's HUD material only needs the SDF.

The shared material uses the native `M_UI_SDFIconGadget` shader through a private copy of its Batarang material instance. It drives the gameplay HUD and character-menu equipment icon across all modes. Gameplay upgrades and separate upgrade-tree icons are not changed. Earlier individual HUD edits remain saved but inactive; **Remove material** restores them. Canceling either editor leaves saved equipment unchanged.

### Material appearance

- **Fill / outline colour and opacity:** independent colour pickers and opacity percentages.
- **Outline width:** material-drawn border, default **0.2**. Width uses shader units, not pixels.
- **Glow strength:** accent intensity, default **0.5**. Green paint marks the area; it does not set the final glow colour.
- **Texture grain / edge sharpness:** defaults **0.5 / 8**.

**Native defaults** resets appearance without changing the SDF. Artwork tips are behind **Help**; hover over a setting for its default and purpose.

### Individual bindings (advanced)

These entries remain available for inspection and older recipes. Inputs owned by the shared HUD material are labelled **Uses shared HUD material** and edited from that material's window instead.

| Workshop entry | What to assign |
|---|---|
| **HUD SDF / direct** | The generated SDF cook |
| **HUD SDF / material** | The same SDF as the matching direct entry |
| **Upgrade SDF / direct** and **Upgrade SDF / material** | A matching SDF for that upgrade; use the base icon for both if you want the same appearance |
| **Color / alpha (BCA)** | The BCA cook made from the original, unmarked artwork |
| **Aiming reticle** | A texture compatible with that reticle's original material |

The native Batarang's direct HUD reference and icon material both use the same SDF. Its BCA image is not a second required input for those bindings. Without the shared material, upgrade variants retain their individual pairs.

The SDF converter writes the **whole silhouette into red**, including the painted area, and an independent accent distance field into **green**. Painting a region green does not remove it from the icon. Blue and alpha retain the native packed values. The source's green is a marker, not the final glow colour; the material controls that colour and intensity. The selection frame around a gadget is separate from its icon accent.

Use white artwork with optional green markers, not an existing red/blue channel-packed SDF, with the new SDF profile. Sources larger than 4096×4096, opaque backgrounds, clipped silhouettes and accent marks too small to survive the 64px conversion are rejected with a reason. Green paint should be opaque; antialiased edges are supported. Keep a reasonably sized accent area because the final SDF is only 64×64.

### Existing cooks and later edits

Older BCA, prepared-SDF and transparent-PNG recipes retain their original cook profiles. Reimporting an existing prepared SDF still preserves its channels; the new import option does not reinterpret it as white artwork.

New imports offer separate **Equipment color icon (BCA)** and **Equipment SDF icon (white + green PNG)** choices. Existing paired imports remain valid; they are not silently converted to the green-marker recipe. Create a new marked-SDF cook to adopt the new workflow, then assign it to the appropriate equipment inputs.

Each cook has its own saved source and recipe. **Reimport image** recooks that saved source; **Replace image** selects a different source. The marked-SDF recipe retains green-marker interpretation when reopened or rebuilt. Neither output is automatically assigned to equipment or character portraits.

### Current limit

The shared SDF material, green-marker converter and appearance controls have worked in-game. Exaggerated borders/glow can reach the HUD frame; keep artwork padded and use fill opacity 1 for a solid icon. Upgraded states still need targeted testing. Accent colour remains native; shader switches and disabled-state controls are not editable. Equipment icons stay separate from suit and character portraits.

## Upgrade choices — experimental

For Batarang-based equipment, open **Upgrades…**, check the upgrades the item should receive, then save and rebuild. **Scatterang** enables simultaneous throws and three-target lock-on. **Batarang Combo** increases capacity for rapid throws; it is not the same upgrade. To allow only simultaneous throws, select Scatterang and uncheck the other seven. Older saved choices retain their original meaning.

Existing recipes allow all eight native upgrades. Choices are saved with the equipment and copied independently. Cancelling either editor leaves the original recipe unchanged.

This filters the item's upgrade functionality, not the save's purchases. It does not buy upgrades, remove global unlocks or hide their entries in the global upgrade menu. Test with the relevant upgrades already purchased. Bat Swarm is a focus-throw upgrade.

Batarang builds now create a private upgrade chain and retarget Alarmarang/Concussive functionality to the custom equipment instance. The original shared upgrades and other gadgets remain unchanged. The native definitions targeted the original Batarang Blueprint, so simply keeping those references was not sufficient for custom clones.

Filtering requires exactly one Batarang-family item in the loadout because these items share character upgrade attributes. Builds with a filtered item alongside another Batarang are blocked. Other equipment types retain their native upgrade data; their selectors remain unavailable until their contracts are checked.

All-upgrades and no-upgrades configurations have passed in-game after the registration fix. The old “multi-only” test selected Combo rather than Scatterang; the labels now distinguish them. Single-upgrade configurations still need focused gameplay checks. A successful build alone does not prove runtime behavior.

## Materials and effects

Custom OBJ materials take precedence over the component's original override materials. Set those slots in the 3D editor; conflicting component overrides are rejected. The preview uses neutral/reference and colored-slot alignment shaders, not a final in-game material renderer.

Most effects and audio remain view-only. The Batarang's **After-hit / ground model** has an experimental mesh adapter: import an OBJ or paste your thrown model there. It makes a private effect copy and uses your model's materials without replacing the native effect.

**Batarang left behind after a hit:** `ProjectileSuccessfulVFX` points to `NS_Batarang_SuccessfulHit`, which has its own mesh renderer. Changing a held or thrown model does not change this input. Test the replacement's impact placement and fading. Separate Bat Swarm and Concussive shockwave effects are outside this adapter and may still contain native Batarang visuals.

## Limits and testing

- Static component models accept OBJ files. Native skeletal components accept weighted FBX files using their original rig. Untested skeletal donors remain available with warnings; unusual non-component mesh references may still need a separate adapter.
- Model changes do not author new firing logic, projectile movement, damage, sockets, reload animations or upgrades. The OBJ baker reuses its verified static-mesh donor collision shell; it does not generate collision fitted to the new shape. Keep visual changes modest and test aiming/hits.
- Cataloged skeletal NPC/boss gear can be tried experimentally, even without a confirmed playable owner. Its abilities may require setup that playable characters lack. Static-only gear without a playable owner, uncataloged variants and mismatched definitions remain view-only. Missing dependencies or invalid rig data still block builds. Ordinary native loadout swaps are unchanged.
- First-time extraction and Full refresh include gadget/upgrade HUD assets and narrow equipment model dependencies. A missing story-specific actor can still block a particular variant rather than produce an incomplete clone.

Custom equipment, HUD artwork, all/no Batarang upgrades and Gordon's weighted pistol replacement have passed gameplay checks. Combo-only and Scatterang-only still need separate checks: Combo increases rapid-throw capacity, while Scatterang enables simultaneous throws. Before sharing a mod, test equip/switch, HUD, held model, quick fire, aimed fire, upgraded projectiles, restart persistence, and an ordinary suit retaining its native gadget.

See the [equipment inventory](equipment-inventory.md) for the current extraction audit.
