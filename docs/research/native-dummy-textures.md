# Native dummy textures

Read-only audit of the installed game's shared EoM placeholders, 2026-09-28. References were
checked against resolved base-game cowl, body and LEGOface material instances. Decoded PNGs and
machine-local reports are under `artifacts/DummySurfaceAudit`; they are not redistributed.

These are real textures, not null references. The sampled EoM dummies are 8×8 constant maps:

| Native texture | Decoded RGBA bytes | Purpose / limitation |
| --- | --- | --- |
| `T_Dummy_CTUV` | 129, 0, 255, 255 | Constant geometry-control input; not a black CT map or a face visibility switch. |
| `T_Dummy_RAO` | 129, 255, 255, 255 | Structural roughness R is constant; G has no baked AO darkening. B is retained as shipped, not reinterpreted. |
| `T_Dummy_Norm` | 127, 127, 0, 255 | BC5 normal data: XY is stored, Z is reconstructed by the normal sampler. Its decoded blue byte is not an authored RGB normal. |
| `T_Dummy_MMR` | 0, 0, 51, 255 | Native packed surface default, including nonzero roughness. Not interchangeable with RAO or arbitrary ORM packing. |
| `T_Dummy_Black_BC` | 0, 0, 0, 254 | Black colour/ID default; alpha is nearly opaque in this decoded sample, not invisible. |
| `T_Dummy_White_BC` | 255, 255, 255, 254 | White colour default used by several native inputs; tinting still comes from the shader. |
| `T_Dummy_E` | 0, 0, 0, 255 | Black emissive data input. |
| `T_Dummy_Alpha_Off` | 0, 0, 0, 0 | Transparent artwork default for appropriate face layers; not a generic substitute for every face atlas. |
| `T_Dummy_WhiteGrayScale` | 255, 255, 255, 255 | Single-channel G8 white data, used as a full-weight input by several material controls; not an emission-off map. |
| `T_Dummy_NML` (Shared, not EoM) | 128, 126, 0, 255 | Distinct native LEGOface BC5 normal default. |

The bytes above describe the local decoder's output, including compression quantization; they
are not instructions to manufacture replacements. Reference the game's assets themselves.
LEGOface also has a distinct `/Game/Characters/Textures/Shared/T_Dummy_NML` normal default.

The supplied Blender material report independently confirms RAO.R roughness and RAO.G AO and
uses CT.R in a surface mix. It does not establish every CT channel or RAO.B's complete Unreal
semantics, so this implementation does not claim full EoM shader parity. Existing switches,
offsets, metalness controls, micro-detail layers, UV choice and lighting continue to affect the
result even when one input is replaced with a dummy.

## Forge behavior and safety

`MaterialDummyTextureService` chooses only known EoM inputs and compatible LEGOface normals/MMR,
or preserves an already inherited native dummy exactly. Generic alpha/ORM inputs, legacy cape
shaders and arbitrary custom parents are not assigned a guessed map. Face BC atlases are left
to the visibility helpers because eye, eyelid and lash artwork can share data.

`Set dummy` changes only the selected texture override. A blank cell inherits; `Set None` writes
null. No shader switches, scalar values, parent material or source package are changed by the
button, and generation still uses the ordinary validated material pipeline. Two normal inputs
using the verified flat native dummy do not trigger a doubled-detail warning; real duplicated
authored normals still do.
