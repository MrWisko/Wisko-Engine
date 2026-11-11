# Models used by the animation example and tests

Real, third-party animated models with licences that allow this use. Each folder keeps the upstream licence (and
readme where there is one) next to the file. Wisko does not modify them; the demo builder imports them through the
normal import pipeline (they are copied into the project's `Assets/Models`).

| Folder | File | What it tests | Author / source | Licence |
|---|---|---|---|---|
| `RobotExpressive` | RobotExpressive.glb | Humanoid, 14 clips (Idle, Walking, Running, Jump, Wave, Dance, ...), rigid parts on bones + skinned hands | Model by Tomás Laulhé (Quaternius); modifications by Don McCurdy; from the three.js examples | CC0 1.0 |
| `Fox` | Fox.glb | Skinned quadruped, 3 clips (Survey, Walk, Run) driving the same joints | Model: PixelMannen (CC0); rigging and animation: tomkranis; glTF conversion: @AsoboStudio and @scurest; Khronos glTF Sample Assets | CC0 1.0 + CC BY 4.0 |
| `CesiumMan` | CesiumMan.glb | Skinned humanoid walk cycle (one unnamed clip), 19 joints | © 2017 Cesium; Khronos glTF Sample Assets | CC BY 4.0 (the Cesium logo in its texture is a trademark; the demo uses the Classic renderer, which draws no textures — switched to the Universal renderer the texture shows) |
| `RiggedSimple` | RiggedSimple.glb | Simplest rig: a cylinder bent by 2 bones | © 2017 Cesium; Khronos glTF Sample Assets | CC BY 4.0 |
| `RiggedFigure` | RiggedFigure.glb | Simple skinned figure, 19 joints | © 2017 Cesium; Khronos glTF Sample Assets | CC BY 4.0 |
| `BoxAnimated` | BoxAnimated.glb | Node (rigid) animation without a skeleton | © 2017 Cesium; Khronos glTF Sample Assets | CC BY 4.0 |
| `Box` | Box.glb | A static model (no animation): compatibility | © 2017 Cesium; Khronos glTF Sample Assets | CC BY 4.0 |
| `AnimationWithSkeletonFbx` | animation_with_skeleton.fbx | FBX skinning with up to 6 influences per vertex and unnormalized weights | Assimp test model (`test/models/FBX`), © assimp team | BSD 3-Clause (LICENSE-assimp.txt) |

Sources:

- https://github.com/KhronosGroup/glTF-Sample-Assets (Models/Fox, CesiumMan, RiggedSimple, RiggedFigure, BoxAnimated, Box)
- https://github.com/mrdoob/three.js/tree/dev/examples/models/gltf/RobotExpressive
- https://github.com/assimp/assimp/tree/master/test/models/FBX
