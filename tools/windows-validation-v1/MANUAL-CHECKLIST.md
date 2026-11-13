# Wisko Engine 0.1.0: Windows manual checklist

Commit `f2488258927abae3bd47998efebd551d7ac60b7c`. Do this after the automated run. Write **Pass**, **Fail** or a short
note in the last column. For every failure, note the steps that lead to it, and keep a screenshot and
`%LOCALAPPDATA%\Wisko\Logs\Wisko.log`.

## Where to test (protecting your projects and your installed Wisko)

**Recommended: a separate local Windows user account**, for example "WiskoTest" (Settings > Accounts > Other users).
It has its own `Documents\Wisko`, its own `%LOCALAPPDATA%\Wisko` and its own per-user installs, so your projects, logs,
preferences and installed Wisko are never touched. The installer tests (section B) must be done there.

1. Copy the two release files from `<output folder>\packages\` into that account, for example into its Downloads
   folder: `Wisko-0.1.0-win-x64-setup.exe` and `Wisko-0.1.0-win-x64-portable.zip`.
2. Mark them as downloaded, so SmartScreen behaves as for a real download. Run this in PowerShell in that account, in
   the folder that holds them:
   ```powershell
   foreach ($f in 'Wisko-0.1.0-win-x64-setup.exe', 'Wisko-0.1.0-win-x64-portable.zip') {
       Set-Content -Path $f -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
   }
   ```
3. Do section B first: it installs Wisko for that account. Then do section A with the installed app.

**Without a second account**, section A only: run `C:\WiskoValidation\Wisko-Engine\dist\Wisko\Wisko.exe --projects
C:\WiskoValidation\manual\Projects`. Projects and History then stay in `C:\WiskoValidation\manual`. Wisko still writes
its log (and any graphics preference you change) to your `%LOCALAPPDATA%\Wisko`; the previous log becomes
`Wisko.previous.log`. Make the example projects for it first:
`dotnet run --project C:\WiskoValidation\Wisko-Engine\Tests\Wisko.Tests -c Release -- --make-graphics-demo C:\WiskoValidation\manual\Projects`
(and `--make-demo`, `--make-anim-demo`, `--make-v5-demo`, `--make-release-demo`).

---

## A. Release blockers: the editor

A failure here blocks the release when it **loses work, crashes, or makes a documented feature unusable**.

| # | Area | Steps | Expected | Result |
|---|---|---|---|---|
| A1 | Opening Wisko | Start Wisko (Start menu, or the exe above). Check the log | The Hub appears within a few seconds. The log has `[Startup] First frame`, an OpenGL 3.3+ renderer line, one `[Startup] Visual C++ runtime:` line, and no `[Fatal]` | |
| A2 | Creating a world | **+ New World** > Classic; then **+ New World** > Testing World. Name and save one | Both templates open; the saved world appears on the Hub, and its folder exists | |
| A3 | Selecting and transforming | Add a cube, sphere, cylinder, wedge and pyramid. Click to select, Ctrl+click several. Move, Rotate and Resize with the gizmo handles; type 45 in a rotation field; Ctrl+Z, Ctrl+Y, Ctrl+D, Delete | Each part follows the handles; the typed rotation applies; undo and redo are exact; nothing jumps | |
| A4 | Lua scripting and Play | Anchor a part, **+ New Script**, type the README's example (Start prints, spins at 40°/s; E prints). Play, press E, Stop | "Hello from Wisko" in Output; the part spins steadily; "E pressed" on each press; Stop puts everything back | |
| A5 | Script errors | Add a script with a syntax error, and one with `while true do end`; Play | The error shows its line; the runaway loop is stopped; the editor stays responsive; Stop works | |
| A6 | Physics | Gameplay Basics Demo: Play. Walk, jump, ride the conveyors and moving platforms; push things | Nothing falls through the floor; platforms carry the player; nothing jitters at rest | |
| A7 | Animation | Animation Demo: ToolBench > Animation, preview Fox, CesiumMan and RobotExpressive; Play: idle, walk, run, Space to jump, E to wave | Smooth motion; no T-pose, no stretched or exploded meshes | |
| A8 | Model importing | In a saved project: **Add Meshes > Import Model…**, a `.glb`, a `.gltf` with its files, an `.fbx` and an `.obj` with a texture, from a folder whose path has spaces and an accent (for example `C:\Tést Models`); also Cancel once | Each is copied into `Assets\Models` and placed; animated ones list their clips; Cancel shows no error | |
| A9 | File dialogs | Materials tab > choose a texture; attach an existing script; a project picture on the Hub | The dialog opens in front, filters the right files, Cancel is silent, and the editor responds afterwards | |
| A10 | Saving and reopening | Change a world (parts, a script, a material, an animation setting), Ctrl+S, close Wisko, start it again, reopen | Everything is exactly as saved | |
| A11 | Unsaved-change warnings | Change a world, then: Leave to Hub (Save / Don't Save / Cancel); the window's close button (the same three); Delete Project (from a Hub card and from the editor menu) | Each choice does exactly what it says; Cancel loses nothing; Delete asks first | |
| A12 | Universal graphics presets | Graphics Showcase: Environment & Graphics > Rendering > Quality: Low, Medium, High, Ultra | Each preset applies without errors or a crash; the frame rate is usable at the default | |
| A13 | Lighting and shadows | Graphics Showcase: the sun, point and spot lights; move a lit part | Shadows are drawn and follow; lights light the scene; no flickering shadows | |
| A14 | A project made on the Mac | Copy a project saved on the Mac (with Universal settings) to the PC and open it | Opens without warnings, and the Universal look is intact | |

## B. Release blockers: installer and portable zip (separate Windows account)

| # | Test | Expected | Result |
|---|---|---|---|
| B1 | Run the marked `Wisko-0.1.0-win-x64-setup.exe` with the defaults | SmartScreen "Windows protected your PC": **More info > Run anyway** (record the text). No administrator prompt. Installed in `%LOCALAPPDATA%\Programs\Wisko`; Start menu entry "Wisko"; no desktop shortcut; the five examples in `Documents\Wisko\Projects` | |
| B2 | Start Wisko from the Start menu; open each of the five examples and press Play | Every example plays; no script faults in Output | |
| B3 | Run setup again and choose the desktop shortcut | Same folder (no folder page); the shortcut appears; projects unchanged | |
| B4 | Change an example project, then run setup again with the examples task | The changed project is left exactly as it was | |
| B5 | With Wisko open and an unsaved change, run setup, then the uninstaller | Both refuse before changing anything and say Wisko is running; Wisko keeps its change | |
| B6 | **Upgrade**: install the same setup over itself once more (or a newer test build, if you have one) | Installs over the existing one; projects, History and preferences unchanged | |
| B7 | **Uninstall** (Settings > Apps > Wisko > Uninstall) | App folder and Start menu entry gone; `Documents\Wisko` and `%LOCALAPPDATA%\Wisko` still there | |
| B8 | **Portable zip**: extract the marked zip to a folder whose path has spaces and an accent; start `Wisko\Wisko.exe`; copy an example from `Wisko\Examples` into `Documents\Wisko\Projects` and play it | SmartScreen asks once; Wisko runs; the example plays | |

## C. Optional visual checks (record findings; not release blockers by themselves)

| # | Check | Look for | Result |
|---|---|---|---|
| C1 | Universal presets side by side (Low to Ultra) in the Graphics Showcase | Quality steps up visibly; nothing looks broken at Low | |
| C2 | Shadows close up | Shadow acne, peter-panning, blocky edges at High and Ultra | |
| C3 | Bloom, tone mapping, ambient occlusion, toon shading (Post-Processing tab) | Each visibly changes the image; no black or white screens | |
| C4 | PBR materials and textures on the Khronos sample models | Correct transparency, double-sided leaves, normal maps not inverted | |
| C5 | Classic renderer (an older or Classic project) | Colours and sky as before; light icons; the selected baseplate's outline | |
| C6 | Display: 125% and 150% scaling, a second monitor, minimise and restore, sleep and wake with Wisko open | Layout and clicks correct; the picture comes back; low CPU while minimised | |
| C7 | Performance at the default preset in the Graphics Showcase and the Gameplay Systems Showcase | Smooth at the display's refresh rate on the RTX 3060 | |
| C8 | Typing in the World panel rename and the script editor with accents (and AZERTY, if available) | Characters arrive as typed | |

## Verdict

- **Windows passes** when the automated run has no FAIL and all of A1–A14 and B1–B8 pass.
- Optional findings (C) are listed and triaged. They block the release only if they turn out to be a crash or lost
  work.
- Not covered here, and still unverified: Windows 10, a clean computer without the Visual C++ runtime, and an
  all-users install. These are in `packaging/windows/CLEAN-MACHINE-TESTING.md` in the repository; the documentation
  already says they are untested.
