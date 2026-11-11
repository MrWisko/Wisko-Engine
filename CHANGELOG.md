# Changelog

## 0.1.0 (not yet released)

The first public release of Wisko.

**Editor**

- Project Hub with template worlds (Classic, Testing World) shown as pictures, project pictures and a History.
- Parts (cubes, spheres, cylinders, wedges, pyramids) and imported models (GLB / glTF, FBX, OBJ); Move, Rotate and Resize tools;
  dragging a Part's body with Select, Rotate or Resize; rotation typed in degrees; undo and redo.
- A deletable baseplate; World panel with groups; Save | Don't Save | Cancel when leaving with changes.
- Environment & Graphics panel (atmosphere, lighting, rendering, post-processing, materials), a game GUI editor, Physics
  and Animation panels, a script editor with code completion.

**Play and scripting**

- Physics (BepuPhysics) and sandboxed Lua 5.4 scripts with an instruction budget and a Lua memory limit.
- Gameplay API: movement and camera, physics queries and forces, collision groups, joints, attributes and events, GUI
  from Lua, animation, navigation, perception, game time, saved games, cameras and camera feeds, particles, debug
  drawing, seeded random numbers and noise, paths, optional Lua modules (dialogue, quests, crafting, building, races,
  weather, day/night and more).
- Text chat building blocks for games: channels, filters, history, mute and block, message Data for custom styling, a
  text box element and an optional ready-made chat window. Local only.

**Rendering**

- The Universal renderer on Windows (PBR materials and textures, lights, shadows, HDR, bloom, ambient occlusion, toon
  shading, quality presets) and the Classic renderer.

**Reliability**

- Every project and user file is saved atomically: a crash or a full disk during a save never leaves a damaged file.
- A project's files are saved together as one transaction: after a crash they are always all from the same save.

**Branding**

- The golden W logo as the application, window and installer icon, and in the installer's wizard.

**Platforms**

- Windows: tested. macOS: a preview in 0.1.0, until the hands-on test on a real Mac has passed (see
  [docs/macos.md](docs/macos.md)).
- macOS (Metal + Classic): Command shortcuts in the 3D editor (Command+Z, Command+Shift+Z, Command+D, Command+G,
  Command+S; Ctrl+S on Windows too), World panel names typed with the keyboard's layout, a clean refusal instead of a
  crash when no display is available at start, Universal-only settings marked as not drawn (and kept) with a notice and
  a note on the New World screen, light icons and the selected baseplate's outline, particles with fog and never all
  dropped at high counts, 4x MSAA (`--msaa=`), and vsync for the optional `--renderer=opengl`.
