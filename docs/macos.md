# Wisko on macOS

> **Status: preview in 0.1.0.** The macOS app builds and passes its automated tests in CI, and starts and draws on
> real Apple silicon hardware (automated checks, November 2026). The hands-on [real-Mac
> checklist](macos-real-mac-checklist.md) is still to be completed; until then macOS is a preview and its support is
> not final.

## How Wisko runs on macOS

- **Renderer:** bgfx on Metal (`ThirdParty/bgfx`, a pinned build of bgfx, bx and bimg for Apple Silicon). The app bundle
  uses it by default; `--renderer=opengl` asks for OpenGL instead.
- **Processor:** Apple Silicon (arm64) only. The bgfx library is built for arm64.
- **Window icon:** macOS shows the app bundle's icon in the Dock; GLFW cannot set a window icon there.

## Known differences from Windows

Wisko 0.1.0 on macOS is **Metal + Classic**. Checked with headless tests and automated GPU probes (the real editor
driven by scripted input, as the GUI tests do on Windows) on an Apple M5 Pro, macOS 26.5; the hand checklist is still to
be done.

| Area | macOS (bgfx / Metal) |
|---|---|
| Renderer | Classic only: projects set to the Universal renderer are drawn with Classic (no shadows, PBR materials, textures, part lights or post-processing beyond exposure, saturation and contrast). Their settings are kept and saved unchanged for Windows. Environment & Graphics shows those settings muted (still editable) with a note; opening such a project shows a short notice once per session; the New World screen notes that its pictures show the Universal renderer. |
| Camera feeds (GUI images showing a camera) | not drawn |
| Animated models | skinned on the CPU |
| Light icons, the selected baseplate's outline | drawn, as on Windows |
| Particles and debug lines | drawn; particles with the Atmosphere's haze and fog. The engine's maximum (60,000 particles) fits; a frame that could not hold them all would draw as many as fit rather than none (logged once). |
| Anti-aliasing | 4x MSAA by default (about 110 MB more GPU memory at 2560 x 1440); `--msaa=0`, `--msaa=2` or `--msaa=4` |
| Frame pacing | Metal vsync at the display's refresh rate, 120 Hz included (the Windows compositor pacing does not apply) |
| Keyboard | Command for the editor's shortcuts (Command+Z, Command+Shift+Z, Command+D, Command+G, Command+S; see [editor.md](editor.md)); the letter printed on the key counts (AZERTY included) |
| No display at start | the display asleep or none connected: Wisko waits up to 15 seconds for one, then quits with a message in the log (exit code 5) instead of crashing |

`--renderer=opengl` starts the OpenGL editor instead (not supported on macOS; for diagnostics). It presents with the
display's vsync.

## Building the app

On a Mac with the .NET SDK and the Xcode command line tools:

```sh
./scripts/build-macos-app.sh
```

This builds `dist/Wisko Engine.app` (Apple Silicon, self-contained .NET). The app is not signed or notarized yet, so
Gatekeeper refuses to open it when it was downloaded. Its licenses are in `Contents/Resources` (Wisko's, and the
bundled .NET runtime's in `licenses/dotnet`).

## Testing on macOS

[.github/workflows/macos.yml](../.github/workflows/macos.yml) runs on a GitHub-hosted Apple Silicon runner on every push
to `main`, on pull requests (documentation-only changes skipped) and when started by hand. It does the following:

- builds Wisko and runs the headless tests (Debug and Release), plus the save-safety and project save journal crash and
  recovery tests on their own;
- compares Assimp imports with the Windows baseline;
- checks the native libraries:
  - the vendored `libbgfx.dylib` against `ThirdParty/bgfx/VERSION.txt`;
  - separately, as a verification only, that the pinned commits still build an equivalent library (minimum macOS,
    exports). The rebuilt file is never packaged;
- reports the runner's Metal device and lists every binary's minimum macOS;
- builds the app bundle and checks its native libraries:
  - `libbgfx.dylib` is the committed file and `libassimp.6.dylib` the package's file, byte for byte apart from the
    bundle's install name and signature;
  - `libassimp.5.dylib` is absent;
  - the bundled Assimp imports like the Windows baseline;
- opens the app through LaunchServices and checks that its window shows content.

`scripts/build-macos-app.sh` itself refuses to package a `libbgfx.dylib` whose SHA-256 differs from
`ThirdParty/bgfx/VERSION.txt`, or a `libassimp.5.dylib`.

A hosted runner is a virtual machine without a display or a real GPU of its own, so some things can only be checked on
a Mac; the [real-Mac checklist](macos-real-mac-checklist.md) covers them:

| Check | Hosted runner | Needs a real Mac |
|---|---|---|
| Build, headless tests, save-safety and save-journal crash and recovery tests (APFS, `F_FULLFSYNC`) | yes | |
| Native libraries: architectures, minimum macOS, dependencies, bgfx exports and rebuild | yes | |
| App bundle: build, ad-hoc signature, Info.plist, licenses | yes | |
| The app starts with bgfx on Metal and draws a first frame | yes (on the runner's virtual GPU) | to confirm on real hardware |
| What the renderer draws (Classic look, GUI, text, particles, debug lines, CPU-skinned models) | | yes |
| Frame pacing and frame rate on a real display (including 120 Hz) | | yes |
| The editor by hand: mouse, keyboard, text input, file dialogs, Play, saving | | yes (the GUI test runner drives the Windows editor only) |
| Retina scaling, several displays, sleep and wake | | yes |
| A downloaded copy: Gatekeeper, and later signing and notarization | | yes |

Until every row has been verified, macOS is a preview, not a supported platform.
