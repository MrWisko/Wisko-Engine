# Animation demo

Real rigged models (see [`Models/CREDITS.md`](Models/CREDITS.md) for their authors and licences) and the Lua scripts
that animate them. The automated tests import these exact files and play these exact scripts.

## Create the project

```
dotnet run --project Tests/Wisko.Tests -- --make-anim-demo "%USERPROFILE%\Documents\Wisko\Projects"
```

This creates a project called **Animation Demo** (it refuses when one already exists, so it never overwrites
anything). Every model is imported through the editor's own import (copied into the project's `Assets/Models`), the
scripts are copied into `Assets/Scripts`, and the scene is saved. Open it from the Hub.

## What is in it

| Object | Model | What it shows |
|---|---|---|
| PlayerBody + Robot | RobotExpressive (humanoid, 14 clips) | `Character.lua`: WASD walks, LeftShift runs, Space jumps, E waves; the mouse turns the camera (Tab frees it). The clips follow the body's real speed: Idle, Walking, Running, with crossfades and clip speed matched to the movement; Jump and Wave play once. |
| Fox Survey, Fox Walk, Fox Run, Fox Tempo | Fox (one asset, 3 clips) | Four instances of one model, each its own clip and speed (Play on start); Fox Tempo's `SpeedPulse.lua` makes its speed rise and fall. |
| Dancer 1-3 | RobotExpressive | `Dancer.lua`: each cycles Dance, Wave, Yes, ThumbsUp, No and Punch with crossfades, at its own tempo. |
| Cesium Man | CesiumMan | A skinned walk cycle (its single clip has no name: "Animation 1"). |
| Rigged Simple | RiggedSimple | The simplest rig: a cylinder bent by two bones. |
| Box Animated | BoxAnimated | Node (rigid) animation, no skeleton. |
| Static Box | Box | A model without animation (unchanged behaviour). |
| FBX Rig | animation_with_skeleton.fbx | FBX skinning (up to 6 influences per vertex in the file; the 4 strongest are used). |

## Things to try

- **Editor preview**: select a fox (World panel > Fox Herd), open **ToolBench > Animation**, choose a clip, press
  Play, Pause, drag the timeline, Stop. Nothing about the project changes.
- **Saved settings**: in the same panel change Loop, Speed or Play on start, then press Play.
- **Play**: walk, run, jump and wave with the character; watch the dancers and the foxes.

The scripts are plain Lua in [`Scripts`](Scripts); [`docs/animation.md`](../../docs/animation.md) explains the API they
use.
