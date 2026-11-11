# Windows packaging

Wisko for Windows ships as an installer (`Wisko-<version>-win-x64-setup.exe`, Inno Setup 6) and a portable zip
(`Wisko-<version>-win-x64-portable.zip`). Both hold the same app folder: `Wisko.exe` with the .NET runtime included
(self-contained, x64), its native libraries, the licenses, and the example projects in `Examples\`.

## Building

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-windows-app.ps1 -Package
```

This publishes `dist\Wisko`, makes the example projects with the test runner (`dist\Examples`, each with its models'
credits and licenses), writes the portable zip and, when Inno Setup 6 is installed, compiles
[Wisko.iss](Wisko.iss) into the installer. Without Inno Setup the zip is still made and the script says the installer
was skipped; `-InnoCompiler <path to ISCC.exe>` names a compiler in another place. Needs the .NET SDK (see
`global.json`) and nothing else.

The version is the `<Version>` of `Game\Wisko.Editor.csproj`; product, company and copyright come from
`Directory.Build.props`, so `Wisko.exe`'s properties and the installer show the same.

## What is in the app folder

| | |
|---|---|
| `Wisko.exe`, `*.dll`, `Wisko.runtimeconfig.json`, `Wisko.deps.json` | the editor and the .NET 10 runtime |
| `glfw3.dll`, `lua54.dll`, `Assimp64.dll`, `nfd.dll` | windows, Lua, model import, file dialogs |
| `LICENSE.txt`, `NOTICE.txt`, `THIRD-PARTY-NOTICES.md`, `BRANDING.md` | Wisko's license and notices |
| `licenses\dotnet\` | the .NET runtime's license and third-party notices, from the runtime pack the build used |
| `Examples\` | the example projects (installer and zip only) |

`Assimp64.dll` and `nfd.dll` need the Microsoft Visual C++ Redistributable (x64), version 14.44 or later, which Wisko
does not include ([NATIVE-LIBRARIES.md](NATIVE-LIBRARIES.md), option 1). Nothing downloads or installs it silently:

- **Setup** checks `vcruntime140.dll`, `vcruntime140_1.dll` and `msvcp140.dll` in Windows' system folder. When one is
  missing or older than 14.44, the finished page asks whether to open Microsoft's link,
  https://aka.ms/vc14/vc_redist.x64.exe. A silent install only writes it to the setup log.
- **Wisko itself** (installed or portable zip) makes the same check at start, logs it (`[Startup] Visual C++
  runtime: ...`), and explains it once with the same offer. Import and file dialogs then say why they cannot work
  instead of failing; everything else works. Model import is also refused with a runtime older than 14.44.

Test it on a clean computer (below, and CLEAN-MACHINE-TESTING.md tests T2 to T7). `/DInstallerTestNoVcRuntime`
(with `/DInstallerTest`) builds a test setup that acts as if the runtime were missing.

## Windows versions

**Wisko 0.1.0 supports Windows 11**, the only version tested. Windows 10 version 1809 or later is not blocked: the
installer accepts it (`MinVersion=10.0.17763`) and Wisko does not check the Windows version. But it is **not verified**
and not advertised as supported until it has been tested on a suitable installation. Microsoft no longer offers a
Windows 10 evaluation, so that test needs a licensed Windows 10 image. What sets the floor:

- **.NET 10** lists Windows 11 and, of Windows 10, only the Enterprise LTSC and IoT editions (1607, 1809, 21H2) as
  supported; Windows 10 22H2 (Home, Pro) is out of Microsoft's support and not on that list.
- **The native libraries' headers** ask for nothing newer than Windows 7, and they import no Windows API sets beyond the
  Universal C runtime, which is part of Windows 10 and 11.
- **Wisko** needs an OpenGL 3.3 driver.

## The installer

- **Per user by default**: `%LOCALAPPDATA%\Programs\Wisko`, no administrator rights. "Install for all users" on the
  first page installs to `Program Files\Wisko` (administrator).
- **Start menu** entry "Wisko"; a **desktop shortcut** only when the task is chosen (off by default).
- **Example projects** (optional, selected by default): the task copies each example into `%USERPROFILE%\Documents\Wisko\Projects`,
  the folder Wisko opens. A folder that already has that name is never changed, and each copy appears only once it is
  complete. Uninstalling leaves the copies (they are the person's projects from then on). For an all-users install
  they go to the projects folder of the person who runs setup.
- **Upgrades**: a newer installer (same AppId) installs over the previous version in the same folder and first removes
  the previous version's program files, so nothing outdated stays behind. It only removes files in a folder that
  already held `Wisko.exe` when setup started.
- **Uninstall** (Settings > Apps, or the uninstaller in the app folder) removes the app folder only. Projects
  (`Documents\Wisko`), the Hub's History, and `%LOCALAPPDATA%\Wisko` (preferences, logs) stay.
- **A running Wisko is never closed by setup** (it may hold unsaved work): while `Wisko.exe` in the folder runs, setup
  and the uninstaller stop before changing anything and say why; close Wisko and run them again.
- Not code-signed yet: Windows SmartScreen warns about the downloaded file ("Windows protected your PC" > More info >
  Run anyway).

The icons and images are the branding files: setup uses `branding\AppIcon\Wisko-AppIcon.ico`, the shortcuts and Apps
list use `Wisko.exe`'s own icon (the same file, through `Game\Wisko.Editor.csproj`), and the wizard shows the golden W
on black from `branding\Installer` (the large image on the Finished page, the small one at the top right of the other
pages; one file per display scaling).

## The portable zip

Unzip anywhere and start `Wisko\Wisko.exe`; nothing is installed and nothing outside the folder is written except
the projects folder, the History beside it and `%LOCALAPPDATA%\Wisko` (as with the installer). To use the example
projects, copy folders from `Wisko\Examples` into `%USERPROFILE%\Documents\Wisko\Projects`, or start Wisko with
`--projects <folder>` to use another projects folder.

## Testing the installer

**On a development computer**, test only an isolated test build: it has its own AppId and the name "Wisko Installer
Test" (its own shortcuts and Apps entry), and copies the examples into a folder you name instead of the projects folder.
Install it silently into a scratch folder, so a real Wisko, its shortcut and the projects are never touched:

```powershell
ISCC.exe /DInstallerTest=1 /DTestProjectsFolder=C:\scratch\projects /DAppVersion=0.1.0 /DSourceDir=dist\Wisko `
    /DExamplesDir=dist\Examples /DOutputDir=C:\scratch\out /DIconFile=branding\AppIcon\Wisko-AppIcon.ico `
    /DWizardImages=branding\Installer\Wisko-WizardImage-*.png /DWizardSmallImages=branding\Installer\Wisko-WizardSmallImage-*.png `
    packaging\windows\Wisko.iss
C:\scratch\out\Wisko-0.1.0-win-x64-setup-INSTALLER-TEST.exe /VERYSILENT /SUPPRESSMSGBOXES /CURRENTUSER /DIR=C:\scratch\app /TASKS="examples"
```

**On a clean computer or virtual machine** (no .NET SDK, no Visual Studio, no Visual C++ runtime), test the release
installer itself:

| # | Test | Expected |
|---|---|---|
| 1 | Run setup with the defaults | Installed in `%LOCALAPPDATA%\Programs\Wisko`; Start menu entry; no desktop shortcut; examples in `Documents\Wisko\Projects`; Wisko starts from the last page |
| 2 | Start Wisko from the Start menu | The Hub shows the five example projects; the log `%LOCALAPPDATA%\Wisko\Logs\Wisko.log` has `[Startup] First frame` and no errors |
| 3 | Open each example and press Play | Every example plays; the Gameplay Systems Showcase bakes its navigation; no script faults |
| 4 | Import a model (the toolbar's Import) | The file dialog opens and the model imports (Assimp and nfd; fails without the Visual C++ runtime) |
| 5 | Make, save and reopen a project | Saved files in the project folder; `.wisko` hidden; reopens unchanged |
| 6 | Run setup again with the desktop task | Same folder (no folder page); desktop shortcut appears; projects and History unchanged |
| 7 | Change an example project, then run setup again with the examples task | The changed project is left exactly as it was |
| 8 | Upgrade with a newer build (version raised) | Installed over the old one; old program files gone; projects, History, preferences unchanged |
| 9 | Run setup, and then the uninstaller, while Wisko is open with unsaved changes | Both refuse before changing anything and say Wisko is running; Wisko keeps running with its changes |
| 10 | Uninstall | App folder and Start menu entry gone; `Documents\Wisko` and `%LOCALAPPDATA%\Wisko` still there |
| 11 | "Install for all users" (administrator) | Installed in `Program Files\Wisko`; Start menu entry for all users; uninstalls cleanly |
| 12 | Silent install `setup.exe /VERYSILENT /SUPPRESSMSGBOXES` and `unins000.exe /VERYSILENT` | Same results as 1 and 10 without any window |
| 13 | Portable zip on a clean computer | Unzips, starts, plays an example copied into the projects folder |
| 14 | Windows 10 (1809 or later), on a suitable licensed installation | As 1 to 5; needed before Windows 10 may be called supported |

Setup writes a log (`Setup Log <date>.txt` in `%TEMP%`) that records every example project it copied or left alone.
