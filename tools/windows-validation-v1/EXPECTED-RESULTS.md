# Wisko Engine 0.1.0: expected results on Windows

The reference results come from macOS for the same commit (`f2488258…`): CI run 43107907079, and a local run on a Mac.
**Windows has no recorded results yet.** The first successful run on this PC becomes the Windows baseline.

## Automated steps (`SUMMARY.md`)

| Step | Expected on Windows | Reference (macOS, same commit) |
|---|---|---|
| Commit, Origin remote, Clean working tree | PASS | |
| Windows version | PASS (Windows 11) | |
| .NET SDK (global.json) | PASS: 10.0.402 or a later 10.0 SDK | CI used 10.0.404 |
| Visual C++ runtime (x64) | PASS: all three files 14.44 or later | |
| Inno Setup 6 | PASS | |
| Build Debug / Build Release | PASS: **0 warning(s), 0 error(s)** each | 0 / 0 |
| Headless suite, Release / Debug | PASS: **N passed, 0 failed**. N may be a little higher than on macOS, because some checks run only on Windows: the real `DwmFlush` checks, the Visual C++ runtime checks, and the Windows branches of save safety and game saves. Record N: it becomes the Windows baseline | 1,453 passed, 0 failed (each) |
| Save safety (Release) | PASS, 0 failed. It includes Windows-only checks (a file held open, a read-only file) | 25 passed |
| Project save journal (Release) | PASS, 0 failed | 46 passed |
| Windows-specific sections (Release) | PASS, 0 failed. This is `--only Windows`: "Visual C++ runtime (Windows)", "Windows frame pacing (compositor wait)" (including the real `DwmFlush` probe: at most 500 fps and 50% of a core) and "macOS fix pass: editor shortcuts (Windows Ctrl unchanged…)" | the first two have no checks to run on macOS |
| GUI smoke test (Release) | PASS, 0 failed (with `-RunGuiSmoke`). It first checks the display: on, composing at 20–500 Hz, unlocked, local session | none on macOS (the GUI test drives the Windows editor) |
| Packaging script | PASS: the build script's own checks pass (required files, self-contained, x64, GUI subsystem, icon, no administrator manifest, no absolute paths) | |
| Portable zip contents | PASS: app, licences (`LICENSE.txt`, `NOTICE.txt`, `THIRD-PARTY-NOTICES.md`, `BRANDING.md`, `licenses/dotnet/`), the five examples, forward-slash entries; about 100 MB or more | |
| Wisko.exe version info | PASS: "Wisko Engine" 0.1.0; signature NotSigned (expected) | |
| Installer built | PASS: `Wisko-0.1.0-win-x64-setup.exe`, "Wisko Engine" 0.1.0, NotSigned (expected); **not run** | |
| Installer test build: silent install / uninstall | PASS (with `-InstallerSmokeTest`): installed into the output folder, Apps entry present, 5 examples copied; then removed, with the examples kept | |
| Installed Wisko unchanged | PASS (with `-InstallerSmokeTest`) | |
| Checksums | PASS: `SHA256SUMS.txt` with 2 files | |

Final line: `==== AUTOMATED CHECKS PASSED ====` and exit code 0.

Other outcomes:
- A required step that was not run (for example the GUI test without `-RunGuiSmoke`) gives `AUTOMATED CHECKS PASSED,
  BUT REQUIRED STEPS WERE NOT RUN: …`.
- Any FAIL gives `FAILED (…)` and exit code 1.
- A stop on a prerequisite or a repository check gives exit code 1 or 3 and `logs\STOPPED.txt`.

## Files written

In `%USERPROFILE%\WiskoValidation\run-<date>-<time>\` (or `-OutputRoot`):

| Path | Contents |
|---|---|
| `SUMMARY.md` | The verdict and every step's result |
| `SHA256SUMS.txt` | SHA-256 of the portable zip and the installer (format of `shasum -a 256 -c`) |
| `logs\00-environment.txt` | Windows, CPU, memory, GPU and driver, display scaling, session, PowerShell, .NET SDKs |
| `logs\10-build-debug.txt`, `10-build-release.txt` | Full build output |
| `logs\20-headless-release.txt`, `21-headless-debug.txt` | Full test output, ending with `=== SUMMARY: … ===` |
| `logs\30-save-safety.txt`, `31-save-journal.txt`, `32-windows-specific.txt` | Targeted runs |
| `logs\40-gui-smoke.txt` | GUI smoke test (with `-RunGuiSmoke`) |
| `logs\gui-smoke-command.txt` | The exact GUI command, for running it by hand |
| `logs\50-package.txt`, `51-package-checks.txt` | Packaging output and the zip, exe and installer checks |
| `logs\52-installer-test*.txt` | Installer test build, and its setup and uninstall logs (with `-InstallerSmokeTest`) |
| `logs\transcript.txt` | Everything shown in the console |
| `logs\STOPPED.txt` | Only when the script stopped early, with the reason |
| `gui-screenshots\` | PNGs from the GUI smoke test |
| `packages\` | Copies of `Wisko-0.1.0-win-x64-portable.zip`, `Wisko-0.1.0-win-x64-setup.exe` and `SHA256SUMS.txt` |
| `work\` | The test runner's work folders. Empty unless `-KeepWork`: the runner deletes them when it ends |
| `installer-test\` | The test installer, its install folder (removed again) and its copied example projects |

Next to the run folder: `run-<date>-<time>-results.zip`. It holds everything above except `packages\`, `work\` and
`installer-test\`.

In the clone (ignored by git): `bin\`, `obj\` and `dist\` (`dist\Wisko\` the app, `dist\Examples\`, the zip and the
installer).
