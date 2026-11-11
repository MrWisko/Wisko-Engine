# bgfx shaders

Compiled Metal binaries in `compiled/metal/*.bin` are embedded into Wisko.Engine and loaded at runtime
(no runtime compilation). Compiled with shaderc built from the pinned bgfx commit
`abf165d8a78f962ad05da05f10adf0380bce286d` (shaderc 1.19.163, API 163; see `ThirdParty/bgfx/VERSION.txt`).
shaderc is not part of this repository. Run in this folder, with `SHADERC` = that shaderc binary and
`BGFX_SRC` = that checkout's `bgfx/src`.

## Screen UI (Project Hub, editor toolbar and panels) — `varying.def.sc`

```
$SHADERC -f vs_ui.sc       -o compiled/metal/vs_ui.bin       --type vertex   --platform osx -p metal --varyingdef varying.def.sc -i $BGFX_SRC
$SHADERC -f fs_ui_solid.sc -o compiled/metal/fs_ui_solid.bin --type fragment --platform osx -p metal --varyingdef varying.def.sc -i $BGFX_SRC
$SHADERC -f fs_ui_text.sc  -o compiled/metal/fs_ui_text.bin  --type fragment --platform osx -p metal --varyingdef varying.def.sc -i $BGFX_SRC
$SHADERC -f fs_ui_image.sc -o compiled/metal/fs_ui_image.bin --type fragment --platform osx -p metal --varyingdef varying.def.sc -i $BGFX_SRC
```

## 3D editor (ports of the OpenGL editor shaders) — `varying.world.def.sc`

- `vs_unlit` / `fs_unlit`: editor overlays (selection outline, gizmos, label backgrounds); no Atmosphere.
- `vs_env` / `fs_env`: the environment (baseplate): unlit colours with the Atmosphere applied.
- `vs_lit` / `fs_lit`: part surfaces (vertex normal or derivative normal, `gl_FrontFacing`, the Atmosphere sun +
  ambient, haze, fog and image controls). The normal matrix comes from the CPU (`u_normalMatrix`): shaderc's Metal
  path has no `inverse()`.
- `vs_atmo_sky` / `fs_atmo_sky`: the Atmosphere sky (one full-screen triangle drawn first, no depth), sun disc included.
- `vs_world_text` / `fs_world_text`: world-space dimension-label glyphs (shared glyph atlas).

`atmosphere.sh` holds the Atmosphere functions and the `u_atmo` parameter layout. It is plain GLSL shared by both
backends: these shaders `#include` it, and the OpenGL renderer embeds the same file and prepends it to its GLSL 330
sources. After changing it, recompile `fs_lit`, `fs_env` and `fs_atmo_sky`.

```
for s in vs_unlit vs_lit vs_env vs_atmo_sky vs_world_text; do
  $SHADERC -f $s.sc -o compiled/metal/$s.bin --type vertex   --platform osx -p metal --varyingdef varying.world.def.sc -i $BGFX_SRC
done
for s in fs_unlit fs_lit fs_env fs_atmo_sky fs_world_text; do
  $SHADERC -f $s.sc -o compiled/metal/$s.bin --type fragment --platform osx -p metal --varyingdef varying.world.def.sc -i $BGFX_SRC
done
```

Metal only for now; Direct3D 11 binaries come with the Windows backend.

## Skinned meshes (Animation)

There is no skinning shader here (OpenGL has one: `SkinnedVertexShaderSource` in `Renderer.cs`). On bgfx a skinned
mesh is skinned on the CPU (`SkinningCpu`, the same linear blend formula) into a dynamic vertex buffer per model instance
and mesh, refilled when that instance's pose changes, and drawn with `vs_lit` / `fs_lit` (its selection rim with
`vs_unlit` / `fs_unlit`). This path has not been run on macOS yet. A `vs_lit_skinned` port (bone matrices as a `mat4`
uniform array) would move the work to the GPU once shaderc is available to compile it.
