# Troubleshooting

The script stops with `STOPPED: <reason>` for problems with the clone or the prerequisites (also in `logs\STOPPED.txt`).
A failing test or build shows `[FAIL]` and the run continues where that makes sense.

## The script does not start

| Message | Cause and fix |
|---|---|
| "…cannot be loaded because running scripts is disabled…" or "…is not digitally signed…" | Start it as shown, with `powershell -NoProfile -ExecutionPolicy Bypass -File …` (this process only). If the file is marked as downloaded: `Unblock-File .\Invoke-WiskoWindowsValidation.ps1` |
| "Run this in 64-bit PowerShell" | Open "Windows PowerShell" or "PowerShell 7", not the "(x86)" one |
| A parse error, with a line number | The file was changed in transfer. Compare it with `PACKAGE-SHA256SUMS.txt` and copy the package again |

## Repository checks

| Message | Fix |
|---|---|
| "the clone is at '…', not the approved commit" | `git checkout --detach f2488258927abae3bd47998efebd551d7ac60b7c` in the clone |
| "origin is '…', not MrWisko/Wisko-Engine" | You pointed it at another checkout (for example the development one). Make a fresh clone (00-START-HERE.md, step 2) |
| "the clone has changes or untracked files" | Use a fresh clone. Do not clean the development checkout to make it pass |
| "…dist already exists from an earlier run" | A full run needs a new clone, for example `git clone … Wisko-Engine-2`. A `-GuiOnly -RunGuiSmoke` run can reuse the clone |
| "OutputRoot already exists" / "must not be inside the clone" | Leave out `-OutputRoot` (a new dated folder is used), or name a new folder outside the clone |

## Prerequisites

| Message | Fix |
|---|---|
| "git is not installed" / ".NET SDK not found" | Install Git for Windows or the .NET 10 SDK (x64), then open a **new** PowerShell window |
| "no .NET SDK satisfies global.json" | Install the .NET SDK 10.0.402 or a later 10.0 SDK. `dotnet --list-sdks` shows what is there. Do not edit `global.json` |
| "Microsoft Visual C++ Redistributable (x64) 14.44 or later is needed" | Install https://aka.ms/vc14/vc_redist.x64.exe (as administrator), then run again. The model-import tests need it |
| Inno Setup 6: FAIL | Install Inno Setup 6, or pass `-InnoCompiler "C:\path\to\ISCC.exe"`. Without it, the zip is still built but the installer is not, which is a release blocker |
| Local session: WARN (Remote Desktop) | Run at the PC itself. Over Remote Desktop the frame-pacing check and the GUI test do not measure a real display |

## Build or test failures

| Symptom | What to check |
|---|---|
| Build: warnings or errors | `logs\10-build-*.txt`. A restore error ("Unable to load the service index") means no network to nuget.org: the first build downloads the packages |
| Headless: "Real DwmFlush (Windows): frame rate bounded…" fails | The display was off, asleep or locked, or this is Remote Desktop. Keep the display on (the script asks Windows to keep it awake while it runs) and run at the PC |
| Save safety: a "held" or "read-only" check fails | Antivirus, backup or sync software (OneDrive) can lock files in the work folder. Run again with `-OutputRoot` on a local folder outside OneDrive, for example `C:\WiskoValidation\run2` |
| "Visual C++ runtime (Windows)" or "Assimp model import" fail | The runtime is missing or older than 14.44 (see above) |
| No `SUMMARY` line in a test log | The runner crashed. The end of that log has the exception. Run again with `-KeepWork` to keep its work folder |
| Many failures across sections | First check that the clone is clean and at the right commit, and that the disk is not full |

## GUI smoke test

| Symptom | Fix |
|---|---|
| NOT RUN: "prepared: run again with -RunGuiSmoke" | It only runs when asked: add `-RunGuiSmoke` (or `-GuiOnly -RunGuiSmoke` to run just the GUI test) |
| NOT RUN: "no local interactive desktop" | Run at the PC's own console: not Remote Desktop, not over SSH, not as a scheduled task |
| "GUI display conditions" fails | The display was off, the session locked, or the refresh rate was outside 20–500 Hz. Wake the display, unlock, run again |
| Clicks land in the wrong place; "display scaling is …%" in the log | Set Settings > System > Display > Scale to 100%, then `-GuiOnly -RunGuiSmoke` |
| Failures after you touched the mouse or keyboard | The test drives the editor itself; any input can interfere. Run it again without touching anything |
| OpenGL errors | Update the NVIDIA driver, then run again. Keep `logs\40-gui-smoke.txt` |

## Packaging and installer test

| Symptom | Fix |
|---|---|
| Packaging script: FAIL | `logs\50-package.txt` names the check that failed (missing file, not self-contained, icon, absolute path…) |
| Installer test build: "already exists" | A "Wisko Installer Test" from an earlier run is installed: remove it in Settings > Apps, then run again |
| Installer test build: silent install FAIL | `logs\52-installer-test-setup-log.txt` (Inno Setup's own log) |
| "Installed Wisko unchanged": FAIL | Should never happen: check Settings > Apps > Wisko and report it with the logs |

## Starting over

Everything the script made is in the run folder (`%USERPROFILE%\WiskoValidation\run-…`) and in the clone's ignored
folders. Delete both, or just make a new clone and run again. Your projects, your installed Wisko and your development
checkout are not involved.
