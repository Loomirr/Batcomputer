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
name, path, donor, or slot. Open a tile to inspect its exact usage or choose from its
condensed donor list. **Inspect in 3D** shows its mesh, materials,
parent, tags, and animation class when available.

The status tells you where the wiring came from:

| Status | Meaning | What you can do |
| --- | --- | --- |
| Native recipe | At least one component was observed in a game Blueprint. | Select a specific compatible usage. **Use selected recipe** is enabled only for a safe one. |
| Inferred | The mesh is cataloged and Batcomputer can suggest hair/hat metadata, but no Blueprint usage was found. | Preview and inspect it. There is no safe one-click graft. |
| Preview only | The mesh exists, but no wiring recipe was established. | Browse and preview it; do not assume a mounting slot. |

Different recipes for one mesh are not interchangeable. A Hair mesh can be chest-mounted;
Satchel has both a Costume usage and a misleading Cape usage. Choose the exact donor context
you intend to use, and test the result in-game. Capes and gliders still use the dedicated
**Gliders** workflow, which handles their paired playable/cutscene setup. Face and root-body
usages can be inspected here, but applying them still uses **Faces** and **Native body profiles**.
Equipment has its own workflow too. Tile tooltips summarize the selected usage; the detail
window contains the donor list, scoped to the selected point for a condensed tile.

The advanced slot-based part picker and graft path remain available. Existing saved projects
keep the same part records and build path; browsing this catalog does not rewrite them.
After a game update, refresh extracted assets and rebuild the part index so the catalog can
associate meshes with the new build's Blueprint recipes.
