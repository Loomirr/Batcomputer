# Attachment catalog coverage — current extraction

This is a snapshot of the locally extracted game build checked on 2026-09-27, not a
guaranteed count for every future game build.

| Category | Distinct mesh packages |
| --- | ---: |
| Total attachment meshes | 386 |
| Attachment meshes visible in Parts → All parts | 386 |
| Additional meshes observed through character Blueprints outside the attachment library | 35 |
| Distinct meshes visible in Parts → All parts | 421 |
| Scrollable All parts entries (common meshes condensed by attachment point) | 1,605 |
| At least one native Blueprint usage | 345 |
| Inferred hair/hat metadata only | 23 |
| Preview only; no proven Blueprint usage or inferred recipe | 18 |

The Blueprint index contains 3,530 attachment component usages across the 345 native-backed
meshes. All usages remain distinct. The six previously hidden Cape-tagged Satchel usages are
retained for inspection but marked unsafe; the valid Costume usages remain available.
The additional 35 Blueprint meshes do not change the 386-mesh attachment-library count.
All parts displays ordinary Blueprint usages individually. When a mesh has at least 20
references, it uses one tile per observed attachment point; the exact donors remain in
that tile's detail list. Face-specific and root-body usages
are inspect-only in this view and retain their separate application workflows; a visible
Blueprint usage is not automatically a safe graft recipe.

## Newly browsable, preview-only meshes

These 18 are visible in **Parts → All parts**, but **Use selected recipe** is not
available until a trustworthy wiring recipe is established. In particular, being shaped like
a cape, hairpiece, weapon, or torso part does not prove its component class or mounting rule.

| Mesh | Package path |
| --- | --- |
| Face plane | `/Game/AdditionalContent/VillainMode/Characters/Attachments/HEAD/FacePlane/SM_Head_FacePlane` |
| Harley Arkham Asylum skirt | `/Game/AdditionalContent/VillainMode/Characters/Attachments/Hip/HarleyQuinn_ArkhamAsylumSkirt/SK_HipA_HarleyQuinn_ArkhamAsylum` |
| Harley Batman Ninja torso | `/Game/AdditionalContent/VillainMode/Characters/Attachments/Torso/HarleyQuinn_BatmanNinja/SK_TorsoA_HarleyQuinn_BatmanNinja` |
| Henchmaster collar | `/Game/AdditionalContent/VillainMode/Characters/Attachments/Torso/Henchmaster/SK_Collar_Henchmaster` |
| Advanced one-hole cape | `/Game/Characters/Attachments/Cape/OneHole_Basic/SK_CAPE_OneHole_Advanced` |
| Shared cape mesh | `/Game/Characters/Attachments/Cape/SK_CAPE` |
| Cape collar | `/Game/Characters/Attachments/Cape/SM_CAPE_Collar` |
| Very short cape | `/Game/Characters/Attachments/Cape/SM_Cape_VeryShort` |
| Advanced Yak cape | `/Game/Characters/Attachments/Cape/Yak/SK_CAPE_Yak_TwoHole_Advanced` |
| Yak collar | `/Game/Characters/Attachments/Cape/Yak/SK_Yak_Collar` |
| Sofia Falcone big hair | `/Game/Characters/Attachments/Hair/SofiaFalcone/SK_BIGHAIR_SofiaFalcone` |
| Left head patch | `/Game/Characters/Attachments/Misc/HeadPatch/SM_LHeadPatch` |
| Right head patch | `/Game/Characters/Attachments/Misc/HeadPatch/SM_RHeadPatch` |
| Left arm patch | `/Game/Characters/Attachments/Misc/LArmPatch/LArm_Patch` |
| Ice axe | `/Game/Characters/Attachments/Misc/Snow_Axe/SM_Ice_Axe` |
| Static umbrella | `/Game/Characters/Attachments/Umbrella/Mesh/SM_Umbrella` |
| Skeletal umbrella | `/Game/Characters/Attachments/Umbrella/SK_Umbrella` |
| Scarecrow weapon | `/Game/Characters/Attachments/Weapon/Weapon_Scarecrow/SM_Weapon_Scarecrow` |

The previous index had 3,817 component records. The new index has 3,823: none of the old
records disappeared, and the only six additions are the unsafe Satchel/Cape observations.
Existing records' recipe data matched byte-for-byte after excluding the newly recognized
`Costume` visual-slot classification. The automated native checks rebuilt representative
Joker, Harley, Batman, Nightwing, and Satchel scenarios without changing the game's install
or the user's saved projects.
