# Minifig08 root-scale fix: in-game acceptance

The user confirmed all four test variants passed on 24 September 2026. These tests override the existing Batman 2025 suit using `test_Batcomputer_ValidatedMinifig08.fbx` (SHA-256 `10EA1F2A2CF98F1B92AD0F9F5DCDFBEF876E76FC4AD550EDCE6CFBF64FC3C4A9`).

| Variant | Visible components | User result |
|---|---|---|
| 01 Body Only | Custom body | Worked; everything good in-game |
| 02 Head and Face | Body, native cowl and face | Scale and animations worked |
| 03 Cape and Glide | Body, hanging cape and glide mesh | Worked |
| 04 All Parts | Body and all native suit parts | Everything worked perfectly |

The importer normalizes the recognized 100× root-unit representation and its translations in Unreal before rebuilding inverse bind matrices and cooking. Cooked validation now requires the root to match the native donor. The corrected cook passed 53/53 bone checks. Exported vertex positions, joint indices and skin weights were identical before and after correction; maximum exported joint height changed from 161.48221 m to 1.6148219 m.

Existing skinned caches must be rebuilt using **Reimport saved FBX** with the patched editor. This result verifies the supplied low-poly body and this native suit setup; the skeleton-shaped model from the earlier screenshots was not the supplied FBX. Both actor roles are packaged, but no separate story-cinematic or ragdoll acceptance was reported.

Variant 04 remains installed in `Content/Paks/~mods/SkinScaleTest`. The original test ZIP and SHA remain unchanged; its README records the pre-test status at the time it was packaged.
