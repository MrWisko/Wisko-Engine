# Wisko on Windows

Windows runs the editor on the OpenGL renderer (3.3 core, forward-compatible context), the command-line default.
`--renderer=bgfx` is refused on Windows: the bgfx backend currently targets Apple Silicon macOS (Metal) only.

## Desktop app

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-windows-app.ps1            # dist\Wisko\Wisko.exe
powershell -ExecutionPolicy Bypass -File scripts\build-windows-app.ps1 -Install   # + per-user copy and desktop shortcut
powershell -ExecutionPolicy Bypass -File scripts\build-windows-app.ps1 -Package   # + example projects, portable zip, installer
```

`-Package` makes the release files (the installer needs Inno Setup 6); see
[packaging/windows/README.md](../packaging/windows/README.md) for what the installer and the zip do and how to test them.

The script publishes the editor for win-x64 as a normal folder (`dist\Wisko`, about 96 MB): `Wisko.exe`, the .NET 10
runtime (self-contained, so no installed .NET is needed) and the native libraries (GLFW, Lua, Assimp, file dialogs).
The folder does not use the repository and can be moved anywhere. The publish sets `WiskoWindowsApp=true`
(`Game/Wisko.Editor.csproj`), which names the executable `Wisko.exe` and builds it for the Windows GUI subsystem: started
from Explorer or a shortcut it opens no console window, and its output goes to the log. Terminal and IDE builds are
unchanged (`Wisko.Editor.exe`, with console output). The script checks the result (required files, self-contained, x64,
GUI subsystem, icon, no administrator manifest, no absolute paths in the runtime configuration).

`-Install` copies the app to `%LOCALAPPDATA%\Programs\Wisko` (per user, no administrator rights) and creates or updates
the **Wisko** shortcut on the desktop. It only ever replaces an earlier copy of the app, refuses while Wisko is running
from it, and never replaces a `Wisko` shortcut that opens something else. Projects, history and logs live outside the app
folder, so updating or removing the app keeps them. To remove the app, delete `%LOCALAPPDATA%\Programs\Wisko` and the
shortcut.

Known limitations:

- Not code-signed. A copy downloaded from elsewhere can get a SmartScreen warning; a locally built one does not. The
  installer has been tested only as isolated test builds on a development computer with Windows 11: silently, and
  once interactively (wizard and finished page); not yet on a clean computer.
- Wisko 0.1.0 supports Windows 11, the only version tested. Windows 10 1809 or later is not blocked (the installer
  accepts it and Wisko does not check the version), but it is not verified and not advertised as supported.
- The bundled .NET runtime is the version the SDK ships with (10.0.13 with SDK 10.0.402) and does not update with
  Windows Update. Rebuild to pick up .NET fixes.
- `Assimp64.dll` and `nfd.dll` (model import, file dialogs) need the Microsoft Visual C++ Redistributable (x64) 14.44
  or later, which Wisko does not include. Without it, Wisko still runs: at start, and again when Import or a file
  dialog is used, it explains what is missing and offers to open Microsoft's download link,
  https://aka.ms/vc14/vc_redist.x64.exe (nothing is downloaded or installed unless the person chooses to). The log
  has one `[Startup] Visual C++ runtime:` line either way. The installer makes the same check.
- With no console, an error during startup (for example no OpenGL 3.3 driver) shows no message: the reason is in the
  log.
- The package includes the PDB files (for line numbers in logged stack traces). Release builds map source paths to
  `/_/`, so they name no folder of the build machine; nothing reads them at run time.

## Frame pacing

After every `SwapBuffers`, the window thread calls `DwmFlush`, which waits in the kernel until the desktop compositor's
next frame. The editor therefore runs at the display's refresh rate (60 fps on a 60 Hz display) with the thread asleep
between frames. This is `CompositorFramePacer` (`Engine/Core/CompositorFramePacer.cs`), created by `EngineWindow` only
for the OpenGL backend on Windows. The platform analyzer (CA1416) rejects any unguarded use, so macOS, Linux and the
bgfx backend are untouched. At startup the log says `[Renderer] Frame pacing: desktop compositor (DwmFlush)`.

**Why not the driver's vsync (swap interval 1)?** Without any synchronisation the OpenGL loop is unthrottled:
2,100-4,200 fps measured, and most of a CPU core even in the Project Hub. With the driver's vsync the frame rate drops to the
refresh rate, but NVIDIA's OpenGL driver waits for the vertical blank by busy-waiting inside `SwapBuffers`, so the
window thread still uses about 95% of a core at 60 fps (97-100% of its cycles measured inside `SwapBuffers`). Waiting on
the compositor instead costs almost nothing. The OpenGL driver's vsync is not used on Windows.

**When the compositor does not pace a frame.** `DwmFlush` may fail, or return without waiting (for example while
nothing is being composed). One early return is normal, because a heavy frame can end just as a composition happens.
From the second unpaced frame in a row, each unpaced frame is followed by an 8 ms sleep. The loop can then never spin
(at most about 125 fps, the thread asleep most of the time), and a frame never waits longer than one compositor wait
plus that sleep. Every frame asks the compositor again, so pacing resumes by itself. There is no permanent fallback.
The first throttled frame and the first recovery are logged once each
(`[Renderer] Frame pacing: the compositor is not pacing frames ...` / `... pacing frames again`).

### Measurements

Windows 11, NVIDIA GeForce RTX 3060 (driver 610.74), one 60 Hz display, real `Wisko.Editor.exe`, idle in the Project
Hub:

| | Frame rate | Process CPU | GPU board power |
|---|---|---|---|
| Before (no synchronisation) | ~2,400 fps | 74% of one core | 30.3 W |
| Driver vsync (rejected; measured in the test harness) | 60 fps | ~95% of one core | — |
| Compositor pacing | 60 fps | 5% of one core | 11.6 W (7.7 W with nothing running) |

In the GUI smoke test (`Tests/Wisko.Tests --gui-smoke`): 60 fps in the Hub, the editor, Play Mode and minimized, at
7-19% of a core.

### Known limitations

- **Verified on one configuration only**: one 60 Hz display, NVIDIA, Windows 11. Not yet verified: high-refresh
  displays, several displays with different refresh rates (the compositor's clock may follow a display other than the
  one showing the window), AMD or Intel GPUs, Remote Desktop, the lock screen or a sleeping display.
- **Tearing** is not expected (windowed OpenGL output goes through the compositor), but it cannot be detected
  automatically. Check by eye with a fast camera pan.
- **The frame rate follows the compositor.** If Windows composes at a lower rate (for example a power-saving refresh
  rate), the editor follows it.
- **The unpaced fallback is sleep-based.** The 8 ms sleep lasts at least one system timer tick, so throttled frames run
  at about 60-125 fps depending on the timer resolution.
- **Minimized**, the editor keeps rendering at the display's rate (about 8% of a core measured).
- Moving or resizing the window pauses rendering while the mouse button is held (GLFW's modal loop; unrelated to
  frame pacing).
- Linux (not a supported target) has no frame pacing and still runs the OpenGL loop unthrottled.

## Anti-aliasing

The editor window asks the driver for 4x MSAA (Windows, OpenGL; `EngineWindow`). Without it, a pixel is either fully
covered by a triangle or empty, so object edges are stair-stepped and the edge pixels of distant objects pop in and
out as the camera moves. The log reports what the driver provided, for example `[Renderer] MSAA: 4x (requested 4x)`. A
driver may provide fewer samples (logged the same way). If the window cannot be created with MSAA at all, it is created
again without it (`[Renderer] MSAA: could not create a 4x window ...`). Hub thumbnails read the resolved image.

Measured on an RTX 3060: about +0.05-0.1 ms of GPU time per frame and about 21 MB more framebuffer memory at 1280x720
(about 47 MB more maximized at 1920x1080); the frame rate and frame pacing are unchanged. 8x MSAA and supersampling were
measured too and gave no visible gain over 4x for their extra cost. macOS and the bgfx backend do not use MSAA yet.

## OpenGL line widths

A forward-compatible core context rejects any line width above 1 with `GL_INVALID_VALUE`, even when
`GL_ALIASED_LINE_WIDTH_RANGE` reports more (NVIDIA reports 1-10). `ColoredMesh` therefore has no line-width setting:
line primitives always draw 1 px wide, on every platform and backend. The editor itself draws no lines (gizmos and
outlines are triangle meshes); a wider line would need geometry (quads), not a line width.

## Other notes

- Projects live in `%USERPROFILE%\Documents\Wisko\Projects`. That path is literal: when Windows redirects Documents
  (for example to OneDrive), Explorer's Documents folder is a different one.
- The log is `%LOCALAPPDATA%\Wisko\Logs\Wisko.log` (the previous session's log is kept as `Wisko.previous.log`). It
  records how long startup took (`[Startup] First frame ... ms after the process started`) and each Play start
  (`[PlayMode] Play #1 ready in ... ms`, with physics, script loading and Start, and the first Play frame's time).

## Checking a Windows build

```powershell
dotnet build Wisko.sln -c Release
dotnet run --project Tests/Wisko.Tests                  # headless suite, includes the frame-pacing checks
dotnet run --project Tests/Wisko.Tests -- --gui-smoke    # real editor: GL errors, pacing, Play/Stop
powershell -ExecutionPolicy Bypass -File scripts\build-windows-app.ps1   # desktop app package
```

**The "Real DwmFlush" check.** It paces 60 frames (after 5 warm-up frames) with the real compositor in a fresh child
process, and requires at most 500 fps and at most 50% of one core, measured as the CPU time of the pacing thread itself
(GetThreadTimes). That is the quantity busy-waiting would raise; the whole process's CPU is reported beside it as
information only. Two regression probes keep the check honest: a pacer that busy-waits for each frame must fail it
(measured 98.7% of a core), and real pacing beside a thread that keeps a whole core busy must pass it (pacing thread
0.0%, process 98.7%).

The check used to measure the whole test process's CPU, which made it flaky: work left running by earlier test
sections (background JIT compilation, the garbage collector) counted against it, and it read anywhere from 0% to 60%
in full runs. Since the change it passed 20 full headless runs in a row (pacing thread 0.0-0.1%).

**The GUI suite** keeps the display awake while it runs (SetThreadExecutionState; no input is simulated) and first
checks that the compositor composes at a real display rate (20-500 Hz), that the session is not locked and that it is
not a remote session. A display that is already off cannot be woken this way: the suite then says so in its first
check instead of failing the frame-pacing checks with a misleading frame rate.
