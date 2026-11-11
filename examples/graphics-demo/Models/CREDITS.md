# Models used by the Graphics Showcase and tests

Third-party glTF models with licences that allow this use, from the
[Khronos glTF Sample Assets](https://github.com/KhronosGroup/glTF-Sample-Assets). Each folder keeps the upstream
`LICENSE.md` and `README.md` next to the file. Wisko does not modify them; the showcase builder and the tests import
them through the editor's normal import (they are copied into the project's `Assets/Models`).

| Folder | File | What it tests | Author / owner | Licence |
|---|---|---|---|---|
| `WaterBottle` | WaterBottle.glb | Metal/roughness, normal, occlusion and emissive maps on one material | Microsoft (owner: Public) | CC0 1.0 |
| `NormalTangentMirrorTest` | NormalTangentMirrorTest.glb | Supplied tangents and mirrored normal maps (real bumps beside normal-mapped ones) | Ed Mackey, © Analytical Graphics, Inc. | CC BY 4.0 |
| `AlphaBlendModeTest` | AlphaBlendModeTest.glb | `OPAQUE`, `BLEND` and `MASK` with cutoffs 0.25, 0.5 and 0.75 | Ed Mackey, © Analytical Graphics, Inc. | CC BY 4.0 |
| `EmissiveStrengthTest` | EmissiveStrengthTest.glb | `KHR_materials_emissive_strength` 1, 2, 4, 8 and 16 | Ed Mackey, © AGI | CC BY 4.0 |
| `TextureSettingsTest` | TextureSettingsTest.glb | `doubleSided`, and sampler wrap modes `REPEAT`, `CLAMP_TO_EDGE`, `MIRRORED_REPEAT` | Ed Mackey, © Analytical Graphics, Inc. | CC BY 4.0 |

The showcase's animated character is the Fox from `examples/animation-demo/Models/Fox` (see the credits there).

Not used: DamagedHelmet, because part of it is licensed CC BY-NC 4.0 (non-commercial).

Sources: https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models (WaterBottle, NormalTangentMirrorTest,
AlphaBlendModeTest, EmissiveStrengthTest, TextureSettingsTest).
