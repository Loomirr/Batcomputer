# Browse attachment meshes and native recipes

Open **Parts → All parts** to scroll indexed character Blueprint mesh usages, plus extracted
attachment meshes with no observed usage. Ordinary usages have their own tiles, labelled
with donor, context, and attachment point. Meshes shared by 20 or more Blueprint components
are condensed to one tile per attachment point, so common LEGOFace and FaceTex meshes do not
fill the list with near-identical tiles. Open that tile to choose an exact donor; its usages
are still preserved. This also includes Blueprint meshes stored outside the game's
`Characters/Attachments` folders.
Use **Filter → Attachment point** for the observed component slot (for example Head, Hair,
Torso2, or Hip). A mesh with no proven mount appears under **Unverified**. Search by mesh
name, path, donor, or slot. Drag a compatible exact-part tile onto the figure, or right-click for
its actions. Open a grouped tile to choose from its
condensed donor list. **Inspect in 3D** shows its mesh, materials,
parent, tags, and animation class when available.

The status tells you where the wiring came from:

| Status | Meaning | What you can do |
| --- | --- | --- |
| Native recipe | At least one component was observed in a game Blueprint. | Select a specific compatible usage. **Use selected part** is enabled only for supported wiring. |
| Inferred | The mesh is cataloged and Batcomputer can suggest hair/hat metadata, but no Blueprint usage was found. | Preview and inspect it. There is no safe one-click graft. |
| Preview only | The mesh exists, but no wiring recipe was established. | Browse and preview it; do not assume a mounting slot. |

Different recipes for one mesh are not interchangeable. A Hair mesh can be chest-mounted;
Satchel has both a Costume usage and a misleading Cape usage. Choose the exact donor context
you intend to use, and test the result in-game. Regular native capes can use the guarded part
workflow; a glide visual still uses **Gliders**, including the complete matching cape/glider pair
when the gameplay donor needs it. Enabling the part does not bypass those checks. Verified native
**Face** overlay recipes attached to **Head_Socket** can restore or replace a removed face through
the part workflow. Select the exact playable/cutscene donor; ambiguous counterparts are not guessed.
Use **Faces** to change the printed material on an existing face. Incomplete face recipes and face
accessories remain inspection-only. Root-body application uses **Native body profiles**.
Equipment has its own workflow too. Tile tooltips summarize the selected usage; the detail
window contains the donor list, scoped to the selected point for a condensed tile.

The advanced slot-based part picker and graft path remain available. Existing saved projects
keep the same part records and build path; browsing this catalog does not rewrite them.
After a game update, refresh extracted assets and rebuild the part index so the catalog can
associate meshes with the new build's Blueprint recipes.
