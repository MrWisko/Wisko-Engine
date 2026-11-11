# Wisko 0.1.0 on a real Mac: test checklist

**Status: prepared, not yet run.** macOS is not supported until this checklist has passed on a real Mac. Hosted CI
(see [macos.md](macos.md#testing-on-macos)) builds the app and runs the headless tests, but runs on a virtual machine
without a real GPU, display, mouse or keyboard.

What is already shown without a person (release candidate: `macOS` workflow run 43070974530 at commit `60dc324`, in
Wisko's earlier, private development repository, so neither is visible from this repository):

- Hosted CI (macOS 15.8.2, virtual GPU): the headless tests pass (Debug and Release); Assimp 6.0.2 imports match
  Windows exactly, also with the Assimp inside the app; the packaged `libbgfx.dylib` and `libassimp.6.dylib` are the
  approved files; the app (minimum macOS 15.0) opens through LaunchServices on Metal with 4x MSAA and draws the Home
  screen.
- On an Apple M5 Pro (macOS 26.5): the app starts on Metal with 4x MSAA, and automated probes drove the real editor
  (shortcuts, saving, light icons, particles, the Environment & Graphics panel) at 120 frames a second.

Everything below needs a person at a Mac.

## Getting the tested app (no public release)

The app to test is the `macos-app-bundle` artifact of the latest successful run of the `macOS` workflow on `main` (the
0.1.0 release candidate was run 43070974530 of the earlier, private development repository; in this repository, run the
workflow once to make one). Downloading an artifact needs a GitHub account with access to the repository, and GitHub
deletes artifacts after 7 days; a new run of the workflow makes a new one. Either:

- **Download the artifact:**
  - In a browser signed in to GitHub, open the repository's **Actions** tab, choose the run, and click
    `macos-app-bundle` under Artifacts.
  - Or, with the GitHub CLI inside a clone of the repository: `gh run download <run id> -n macos-app-bundle`.

  The download contains `Wisko-Engine-macos-arm64-unsigned.zip`. Unpack it with Finder, or with
  `ditto -x -k Wisko-Engine-macos-arm64-unsigned.zip .`, which gives `Wisko Engine.app`.
- **Or build it** from the same commit on the Mac. This needs the .NET SDK from `global.json` and the Xcode command
  line tools:

  ```sh
  git checkout <the run's commit>
  ./scripts/build-macos-app.sh
  ```

  The script refuses any `libbgfx.dylib` other than the approved one. The result is the same app, with its own ad-hoc
  signature.

Check that it is the tested build before starting:

```sh
codesign --verify --deep --strict "Wisko Engine.app" && echo signature ok
plutil -extract LSMinimumSystemVersion raw "Wisko Engine.app/Contents/Info.plist"              # 15.0
shasum -a 256 "Wisko Engine.app/Contents/MacOS/libbgfx.dylib"                                    # artifact: 32d3b468...
ls "Wisko Engine.app/Contents/MacOS" | grep libassimp                                           # only libassimp.6.dylib
```

**Gatekeeper.** The app is ad-hoc signed and not notarized, so macOS refuses to open a copy downloaded in a browser.
Record exactly what it shows, then allow it once:

1. Double-click the app; macOS refuses it.
2. Open System Settings > Privacy & Security. Under Security, click **Open Anyway**; this is available for about an
   hour after the attempt.
3. Enter your password.

On macOS 15 and later, Control-click > Open no longer skips this. On a test Mac only, `xattr -dr com.apple.quarantine
"Wisko Engine.app"` removes the download mark instead. A copy fetched with `gh` or built locally has no download mark,
so it opens directly.

## Before starting

- **Mac:** Apple Silicon with macOS 15.0 or later (the app's minimum). Record the model, the macOS version and the
  displays: built-in Retina, any external display, and the refresh rate (60 or 120 Hz).
- **The app:** the tested build, as above. Record whether it is the artifact or a local build.
- **A clean start:** use a separate macOS user account, or note that `~/Documents/Wisko` and `~/Library/Logs/Wisko`
  already exist. Never delete someone's projects to test.
- **Example projects** (for sections 6 and 8): on a Mac with the .NET SDK, from the repository:
  `dotnet run --project Tests/Wisko.Tests -- --make-anim-demo ~/Documents/Wisko/Projects` (and `--make-demo`,
  `--make-v5-demo`, `--make-graphics-demo`).
- **Evidence:** after each section, keep `~/Library/Logs/Wisko/Wisko.log` and a screenshot (Shift-Command-4). Write
  Pass, Fail or a note for every step. A failure needs the steps that lead to it.

Known differences from Windows ([macos.md](macos.md#known-differences-from-windows)) are expected, not failures:

- Classic renderer only;
- no camera feeds;
- no baseplate selection outline;
- animated models skinned on the CPU.

## 1. Launching Wisko.app

1. Double-click `Wisko Engine.app` in Finder. Expected: one Dock icon with the Wisko icon, a window titled
   "Wisko Engine", and the Home screen within a few seconds.
2. Check the log. Expected:
   - `[Startup] Renderer: Bgfx (macOS app bundle default)`;
   - `[Renderer] bgfx backend: Metal`;
   - `[Startup] First frame ... ms`;
   - no `[Fatal]` line, and no `[Startup] Visual C++ runtime` line (that check is for Windows only).
3. Start it once more from a folder whose path has spaces and non-ASCII characters (for example `~/Desktop/Tést Map`).

## 2. Home screen and world creation

1. Home shows its sidebar, the project list (or "No worlds yet") and "+ New World".
2. "+ New World" shows the template screen with its preview pictures. Keyboard and mouse both work.
3. Create a Classic world, add a part, then name and save it. Expected: it appears on Home, and its folder exists in
   `~/Documents/Wisko/Projects`.
4. Create a world and leave without changes. Expected: no question, and nothing written.

## 3. Metal rendering

1. In an open world: the sky, the baseplate and the parts are drawn with the Classic look. There are no black or
   flickering frames, and text is sharp.
2. Open a project saved with the Universal renderer (`--make-graphics-demo`). Expected: it is drawn with Classic (a
   known difference), and nothing fails.
3. Particles and debug lines (`--make-v5-demo`, Play): they appear and move. These were written for bgfx and never run
   on a Mac.
4. Leave the editor idle for a minute with Activity Monitor open. Expected: low CPU and GPU use, and motion that looks
   smooth on the display's refresh rate.

## 4. Camera and gizmo interaction

1. Fly the camera with Wisko's controls ([editor.md](editor.md#the-camera)):
   - W and S move forward and back, A and D left and right, Space and C up and down; Shift moves faster;
   - drag with the right mouse button to look around;
   - the mouse wheel flies forward and back.

   Then try the trackpad: a two-finger click-drag to look around, and two-finger scroll. Record what works. The keys are
   physical positions, so on other keyboard layouts the labels may differ.
2. Select parts by clicking, and with Command-click (or Ctrl-click) for several in the World panel. Expected: both work.
3. Drag the Move, Resize and Rotate gizmo handles. Expected: the part follows, and clicks land where the cursor is,
   also after resizing the window.
4. Type a number in a rotation field. Expected: the gizmo follows.
5. Undo and redo with Command-Z and Command-Shift-Z (Command-Y also redoes); duplicate with Command-D. Expected: Command
   works and Control-Z does nothing; Command-W never undoes or moves the camera. On an AZERTY keyboard the key labelled Z
   is Command-Z. Also delete (Delete or Backspace).

## 5. Lua scripts and Play mode

1. Add a part and anchor it (Anchor in the toolbar). Create a script on it, and type this in the script editor (also
   try selection, copy and paste):

   ```lua
   -- MacCheck: an anchored part spins, and a key press reaches the script.
   function Start()
     print("MacCheck: started")
     self.AngularVelocity = Vector3.new(0, 40, 0)
   end

   function Update(dt)
     if Input.IsKeyPressed("E") then
       print("MacCheck: E pressed")
     end
   end
   ```
2. Play. Expected: "MacCheck: started" in Output, the part turning at a steady speed, and "MacCheck: E pressed" each
   time E is pressed.
3. Chat in Play: `/` opens the text field, Enter sends, Escape closes.
4. Stop. Expected: the scene is exactly as before Play.
5. A script with an error. Expected: the error is shown, Play keeps running, and nothing crashes.

## 6. Animation and model import

1. Open the Animation Demo (`--make-anim-demo`). Expected:
   - the Animation panel previews clips (Fox, CesiumMan, RobotExpressive);
   - in Play, the character idles, walks and runs, Space jumps and E waves.

   Skinning runs on the CPU here, for the first time on a Mac.
2. Import a model (Import > a `.glb` and a `.fbx` from `examples/animation-demo/Models`). Expected:
   - it is copied into the project and placed;
   - its clips are listed;
   - no `[ModelAssets] Could not load` line appears.
3. Check that the log never shows `libassimp.5` (Wisko loads only `libassimp.6.dylib`).

## 7. File dialogs

For each of Import model, a material texture (Materials tab), attaching an existing script, and a project image in
Home, check that:

1. the macOS open panel appears in front of the window;
2. the file-type filter shows only the right files;
3. Cancel returns without a message;
4. choosing a file works, also from a folder with spaces and non-ASCII characters;
5. the editor responds normally afterwards (no stuck mouse button).

## 8. Saving and reopening projects

1. Change a project, save with Command-S, quit, relaunch and reopen it. Expected: identical
   parts, scripts, materials, animation settings and camera.
2. Change it, then leave it or quit. Expected: Save, Don't Save and Cancel each do what they say.
3. Delete a project, once from a Home card's menu and once from the editor's project menu ("Delete Project…").
   Expected: a confirmation first; then it is gone from Home and from `~/Documents/Wisko/Projects`.

## 9. Window resizing and Retina scaling

1. Resize the window, from small to large, and use full screen (green button). Expected: the 3D view and the UI follow,
   clicks still land on what they point at, and the framebuffer size in the log is twice the window size on Retina.
2. Move the window between a Retina display and a non-Retina display, if there is one. Expected: text stays sharp
   and the layout keeps its size.
3. Minimize to the Dock and restore. Expected: the same picture, and no extra CPU use while minimized.

## 10. Exit and relaunch

1. Quit with Command-Q, with the red close button, and with the Dock menu's Quit. Each time unsaved changes are asked
   about first, and nothing is lost when Cancel is chosen.
2. Relaunch. Expected: Home lists the projects, `Wisko.log` is new, and the previous one is `Wisko.previous.log`.
3. Put the Mac to sleep with Wisko open, then wake it. Expected: Wisko draws again and responds.

## When it passes

Update the testing table in [macos.md](macos.md#testing-on-macos), and record the Mac, the macOS version, the commit and
the date. Then remove the preview label from README.md, CHANGELOG.md and macos.md. Until the app is signed and notarized, a downloaded copy keeps needing "Open Anyway" in Privacy & Security
(see [distribution-and-signing.md](distribution-and-signing.md)).
