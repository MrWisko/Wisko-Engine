# Wisko Engine 0.1.0: Windows validation package

This package is for the Windows PC (Intel i5-12400F, NVIDIA RTX 3060). It validates the approved release commit:

| | |
|---|---|
| Repository | `MrWisko/Wisko-Engine` (private) |
| Branch | `main` |
| Commit | `f2488258927abae3bd47998efebd551d7ac60b7c` |
| macOS CI | run 43107907079 passed (1,453 tests in Debug and in Release, 0 warnings) |

**Nothing in this package has been run on Windows yet.** Windows validation has not passed until the script has run
successfully on this PC and the manual checklist is complete.

## What is in the package

| File | What it is |
|---|---|
| `00-START-HERE.md` | This page: prerequisites, setup and the exact commands |
| `Invoke-WiskoWindowsValidation.ps1` | The automated validation (Windows PowerShell 5.1 or PowerShell 7, 64-bit) |
| `MANUAL-CHECKLIST.md` | The hands-on checks, with release blockers separated from optional visual checks |
| `EXPECTED-RESULTS.md` | What each step should report, and the files it writes |
| `LOG-COLLECTION.md` | What to collect and bring back |
| `TROUBLESHOOTING.md` | What to do when a step stops or fails |
| `PACKAGE-SHA256SUMS.txt` | Checksums of the files above, to check that the transfer is intact |
| `.gitattributes` | GitHub copy only: keeps these files byte-identical on Windows (no line-ending conversion), so the checksums still match |

## 1. Prerequisites

The script only **checks** these and never installs anything. If one is missing, it stops and says what to install.

| Needed | Why | Where |
|---|---|---|
| Windows 11, 64-bit | The supported platform for 0.1.0 | |
| Git for Windows | Cloning the private repository, checking the commit | https://git-scm.com/download/win |
| .NET SDK **10.0.402 or a later 10.0 SDK** (x64) | `global.json` asks for 10.0.402 with `rollForward: latestFeature` | https://dotnet.microsoft.com/download/dotnet/10.0 |
| Microsoft Visual C++ Redistributable (x64) **14.44 or later** | Model import (Assimp64.dll) and file dialogs (nfd.dll); the test suite imports models | https://aka.ms/vc14/vc_redist.x64.exe |
| Inno Setup 6 | Building the installer (`ISCC.exe`) | https://jrsoftware.org/isdl.php |
| A current NVIDIA driver | OpenGL 3.3 for the editor | |
| Access to the private repository | Git asks you to sign in to GitHub once (Git Credential Manager) | |
| About 10 GB free disk space | Two builds, the app, the packages | |

For the run itself: be at the PC (a **local** session, not Remote Desktop), with the display on and, for the GUI test,
**100% display scaling** (Settings > System > Display > Scale).

## 2. Setup

Open **PowerShell** (64-bit; not "Windows PowerShell (x86)") and make a **fresh clone** in a new folder. Do not use
your development checkout: the script refuses any clone that is not `MrWisko/Wisko-Engine` at the approved commit, or
that has changes.

```powershell
New-Item -ItemType Directory C:\WiskoValidation
cd C:\WiskoValidation
git clone https://github.com/MrWisko/Wisko-Engine.git Wisko-Engine
cd Wisko-Engine
git checkout --detach f2488258927abae3bd47998efebd551d7ac60b7c
git rev-parse HEAD        # must print f2488258927abae3bd47998efebd551d7ac60b7c
cd ..
```

Then get this package into **`C:\WiskoValidation\Wisko-0.1.0-Windows-Validation`**, outside the clone, in one of two
ways.

- **From GitHub** (branch `validation/windows-v1`, folder `tools/windows-validation-v1`). Clone that branch into its
  own folder and copy the package out. **Never check this branch out in the validation clone above**: that clone must
  stay at the release commit.
  ```powershell
  cd C:\WiskoValidation
  git clone --branch validation/windows-v1 --single-branch https://github.com/MrWisko/Wisko-Engine.git package-branch
  Copy-Item -Recurse package-branch\tools\windows-validation-v1 C:\WiskoValidation\Wisko-0.1.0-Windows-Validation
  ```
- **From the zip:** unpack `Wisko-0.1.0-Windows-Validation.zip` into `C:\WiskoValidation` (right-click > Extract All…,
  destination `C:\WiskoValidation`).

Then check the transfer, if you like. The hashes must match `PACKAGE-SHA256SUMS.txt`, which is in lower case:

```powershell
cd C:\WiskoValidation\Wisko-0.1.0-Windows-Validation
Get-FileHash * -Algorithm SHA256 | Format-Table Hash, Path
```

If Windows blocks the script because it came from another computer, unblock it once:
`Unblock-File .\Invoke-WiskoWindowsValidation.ps1`. The commands below also pass `-ExecutionPolicy Bypass`, which applies
to that one PowerShell process only and changes no setting.

## 3. Run it

**Step A: prerequisites only.** This builds nothing, takes a minute, and also proves the script loads.

```powershell
cd C:\WiskoValidation\Wisko-0.1.0-Windows-Validation
powershell -NoProfile -ExecutionPolicy Bypass -File .\Invoke-WiskoWindowsValidation.ps1 -RepoPath C:\WiskoValidation\Wisko-Engine -PrerequisitesOnly
```

Fix anything it reports, then:

**Step B: the full automated run, with the GUI smoke test.** Do this at the PC with the display on, and **do not touch
the mouse or keyboard once the GUI test starts**: a Wisko window opens and is driven by the test.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Invoke-WiskoWindowsValidation.ps1 -RepoPath C:\WiskoValidation\Wisko-Engine -RunGuiSmoke -InstallerSmokeTest
```

What the run does:
- builds Debug and Release;
- runs the headless suite twice (Release, Debug);
- runs the save-safety, save-journal and Windows-specific sections again on their own;
- runs the GUI smoke test;
- builds the portable zip and the installer with the repository's own script;
- installs and uninstalls the isolated **"Wisko Installer Test"** build silently inside the output folder;
- writes the SHA-256 checksums and `SUMMARY.md`.

`-InstallerSmokeTest` is optional. It briefly adds a "Wisko Installer Test" entry to Apps and the Start menu and removes
it again; it never touches your installed Wisko. Leave it out if you prefer.

Every run writes to a **new** folder, `%USERPROFILE%\WiskoValidation\run-<date>-<time>`, or `-OutputRoot <new folder>`.
Expect roughly 15–30 minutes without the GUI test. That is estimated from the Mac and CI timings and has not been timed on
Windows; the GUI test adds more.

Other options:
- `-GuiOnly -RunGuiSmoke`: only the Release build and the GUI smoke test (for example after fixing the display scaling).
  It can reuse the same clone.
- `-KeepWork`: keeps the test runner's work folders inside the output folder, for investigating a failure. They are
  large.
- `-InnoCompiler <path to ISCC.exe>`: when Inno Setup is not in its standard place.

A second **full** run needs a new clone (step 2 with another folder name). The script refuses a clone whose `dist\`
already exists, so the packages are always known to come from that run.

## 4. Then do the manual checklist

`MANUAL-CHECKLIST.md`. It runs best in a **separate Windows user account**: this is required for the installer tests,
so your installed Wisko and your projects are never touched.

## 5. Bring back

`%USERPROFILE%\WiskoValidation\run-<date>-<time>-results.zip`, which the script writes at the end, plus the filled-in
checklist. See `LOG-COLLECTION.md`. Do not upload or publish the packages: they are for checking, and the release files
are made from the tagged commit later.

## What the script never does

- It never installs, updates or uninstalls prerequisites, and never changes system settings. It keeps the display awake
  only while it runs.
- It never uses `Documents\Wisko`: the tests keep their projects, History and preferences in their own work folders.
- It never runs the real installer, and never passes `-Install` to the build script. Your installed Wisko
  (`%LOCALAPPDATA%\Programs\Wisko`) and its shortcuts are left alone. With `-InstallerSmokeTest`, it checks the release
  installation's Apps entry before and after.
- It never changes the clone's tracked files (only the ignored `bin\`, `obj\` and `dist\`), and never commits, pushes,
  tags or publishes.
- It only gives the test runner `--work` folders that it has just created: the runner deletes its work folder when it
  ends.
