# Clean-machine testing on Windows (VMware)

**Status (2026-10-25): a plan. Nothing has been installed, downloaded, enabled or rebooted. Every step marked
_(approval)_ needs separate approval.**

## Why a clean machine

The development PC cannot show what a new user sees:

- It has the Visual C++ runtime (14.51), which hides the missing-runtime problem described in
  [NATIVE-LIBRARIES.md](NATIVE-LIBRARIES.md).
- It has the .NET SDK and runtimes.
- It runs Wisko and its builds every day.

Only a fresh Windows can show:

- whether the self-contained app starts without any .NET;
- what model import and file dialogs do without the Visual C++ runtime;
- what SmartScreen shows for the unsigned installer;
- a per-user install as a standard user;
- uninstall and upgrade on a machine without leftovers;
- Windows 10, which the installer accepts but 0.1.0 does not claim to support until it has been tested.

## Host

What matters on the development PC (checked 2026-10-25):

| | |
|---|---|
| Windows | Windows 11 Home: Hyper-V and Windows Sandbox are not available |
| CPU and memory | a 12th-generation Intel Core CPU and 16 GB of memory: enough for one VM with 4 virtual CPUs and 6 GB |
| Hypervisor | running (virtualization-based security is on), so VMware must use the Windows Hypervisor Platform |
| Windows Hypervisor Platform | **disabled** (Windows feature `HypervisorPlatform`) |
| Virtual Machine Platform | enabled (used by WSL 2) |
| Free space | little on C:, plenty on D:. Everything for the VM goes on D: |
| System Restore on C: | not enabled, so there is no restore point to fall back on |

## What would be installed, in order

1. **Windows Hypervisor Platform** _(approval: Windows feature, administrator, reboot)_.

   VMware Workstation runs on top of an active hypervisor only through this API: Workstation 15.5.5 or later, on
   Windows 10 20H1 (19041.264) or later. VMware's installer may offer to enable it itself. Check first, then enable,
   from an administrator PowerShell:

   ```powershell
   Get-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform
   Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform    # then reboot
   ```

2. **VMware Workstation Pro**, the newest release on the portal (26H1u1, build 25688693, released 2026-09-03, when this
   was written) _(approval: download and install)_.
   - **Price:** free for personal, educational and commercial use; no license key ("free version" during setup).
   - **Download:** https://support.broadcom.com/group/ecx/productdownloads?subfamily=VMware%20Workstation%20Pro&freeDownloads=true
     It needs a Broadcom Support Portal account, a basic profile, Broadcom's trade compliance form, and accepting its
     terms (Broadcom knowledge article 368667).
   - **Verify:** the installer's Authenticode signature (`Get-AuthenticodeSignature`) must be valid, and the SHA-256
     must match the checksum on the download page, if the page shows one.
   - **Space:** it installs on C: (about 2.5 GB).
   - **Reboot:** its setup may ask for one (virtual network drivers). Accept it only when nothing else is open.
3. **Windows 11 Enterprise evaluation ISO** (version 26H2, 64-bit, English (United States); 90 days) _(approval:
   download)_.
   - **Download:** https://www.microsoft.com/en-us/evalcenter/download-windows-11-enterprise. It asks for a
     registration and a sign-in with a Microsoft account.
   - **Integrity:** compare `Get-FileHash <iso>` (SHA-256) with the value Microsoft publishes at
     https://aka.ms/VerifyWindowsISO. On 2026-10-25 that was `BC3F24086EBADC94489066B5AD78089E2CF5C3491E90E790BB81A2B199C10E38`
     for "Enterprise Eval x64 Eval EN-US DVD9". Check the page again on the day of the download: a newer ISO has a
     new value.
   - **Expiry:** after 90 days, or if activation fails, the desktop turns black and the VM shuts down every hour.
   - **Windows 10:** Microsoft no longer offers a Windows 10 evaluation (the Evaluation Center's Windows 10 page now
     redirects to the end-of-support announcement). Windows 10 1809 or later, which the installer accepts but which is
     not verified, therefore needs a suitable licensed Windows 10 installation to be tested (T12).

### Resources and time

| | Amount |
|---|---|
| C: | about 2.5 GB (VMware) |
| D: | the ISO (about 6 to 7 GB), the VM's disk (grows to 30 to 45 GB with updates, at most 64 GB), snapshots (a few GB each), transfer images (about 0.2 GB) |
| Memory | 6 GB for the VM while it runs (the host keeps about 10 GB) |
| CPU | 4 of the 12 threads while it runs |
| Reboots | one after enabling Windows Hypervisor Platform; possibly one more if VMware's setup asks |
| Time | about 1 to 2 hours for the setup, then about 1 hour for the test cases |

### Order of approval

1. Enable Windows Hypervisor Platform, then reboot.
2. Download, verify and install VMware Workstation Pro (Broadcom account).
3. Download and verify the Windows 11 Enterprise evaluation ISO (Microsoft account).
4. Create the VM and snapshots, then run the test cases below.
5. Afterwards: delete the VM and ISO, uninstall VMware and disable Windows Hypervisor Platform (one more reboot), or
   keep them for the next release.

## The virtual machine

- **Folders:**
  - `D:\WiskoVM\Win11-Clean\`: the VM;
  - `D:\WiskoVM\iso\`: the Windows ISO;
  - `D:\WiskoVM\transfer\`: the transfer images.
- **Guest settings:**
  - Windows 11 x64;
  - UEFI with Secure Boot, and a virtual TPM. VMware adds the TPM with VM encryption and asks for a password; keep it
    with these notes;
  - 4 virtual CPUs, 6 GB memory, a 64 GB disk grown on demand;
  - NAT network;
  - 3D acceleration on.
- **No VMware Tools in the test snapshots.** The tools add drivers and services, and it is not known whether they add
  runtime libraries. A clean machine has none of that.
  - Files reach the VM as a virtual CD instead (below).
  - To find out what the tools install, compare the runtime state (test T1) before and after installing them, in a
    throwaway snapshot.
- **Accounts:**
  - the administrator account made during setup;
  - a **standard (non-administrator) local user**, used for the tests unless a test says otherwise.

### Snapshots

- **S0 "clean":** right after setup and the standard user, before anything else.
- **S1 "updated":** S0 plus Windows Update. Optional, but closer to a real user's machine.
- **S2 "with VC++ runtime":** S1 (or S0) plus Microsoft's Visual C++ Redistributable, installed in test T6.

### Getting the test builds in

On the host, put the installer, the portable zip, a few models and a SHA-256 list into a transfer folder. Pack the
folder into an ISO, then attach the ISO as the VM's CD drive.

```powershell
Get-FileHash D:\WiskoVM\transfer\* | Format-Table Hash, Path > D:\WiskoVM\transfer\SHA256.txt
powershell -ExecutionPolicy Bypass -File packaging\windows\make-transfer-iso.ps1 -Source D:\WiskoVM\transfer -Output D:\WiskoVM\wisko-transfer-1.iso
```

In the VM, compare `Get-FileHash` of the copied files with `SHA256.txt`. `make-transfer-iso.ps1` uses Windows' own
IMAPI2. It was tested on 2026-10-25: the installer, the zip and a file with a non-ASCII name were extracted from the
image byte-identical.

Files copied from a CD image are not marked as downloaded, so SmartScreen would not show what a person who downloads
Wisko sees. Before T2 and T7, copy the files from the CD to `C:\WiskoTest` in the VM and give the copies the same mark
a browser adds:

```powershell
Get-ChildItem C:\WiskoTest\*.exe, C:\WiskoTest\*.zip | ForEach-Object {
    Set-Content -Path $_.FullName -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"   # 3 = the internet
}
```

## Test cases

Before each test, revert to the named snapshot. For every test, record:

- the result;
- screenshots;
- `%LOCALAPPDATA%\Wisko\Logs\Wisko.log`.

| # | Snapshot | Test | Expected |
|---|---|---|---|
| T1 | S0, S1 | Runtime state: `HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64`; whether `vcruntime140.dll`, `vcruntime140_1.dll` and `msvcp140.dll` are in `System32` (with versions); `dotnet --list-runtimes`; Smart App Control's state (Windows Security > App & browser control) | Recorded. This states what "clean" means for the other tests. |
| T2 | S1 | Installer as the standard user, default options, after marking the copied files as downloaded (below) | SmartScreen warns about an unsigned app (record the text); no UAC prompt; installed in `%LOCALAPPDATA%\Programs\Wisko`; Start menu entry; examples in `Documents\Wisko\Projects`. If T1 found the Visual C++ runtime missing: the finished page asks whether to open Microsoft's link; No opens nothing, Yes opens https://aka.ms/vc14/vc_redist.x64.exe in the browser and installs nothing by itself |
| T3 | after T2 | First start | Hub without any .NET installed; the OpenGL version is in the log (Wisko needs 3.3); one `[Startup] Visual C++ runtime:` line in the log; new project, Play Mode and a Lua script work. Without the runtime: one message after the Hub appears, explaining it and offering Microsoft's link (answer No here) |
| T4 | after T2 | Import a model, without the Visual C++ runtime | No crash. The toast says the file dialog needs the Microsoft Visual C++ Redistributable (x64), and the message offers the link again. Opening an example project with models: they stay placeholders and the log names the runtime. Record the exact texts. |
| T5 | after T2 | Choose a texture (Materials) and a project image (Hub), without the Visual C++ runtime | No crash; the same explanation; everything else keeps working. |
| T6 | S1 | Install Microsoft's Visual C++ Redistributable (from Microsoft, as administrator), take S2, repeat T2 to T5 | Import and dialogs work |
| T7 | S1 | Portable zip, extracted to a folder whose path has spaces and non-ASCII characters | As T3 to T5 |
| T8 | after T3 | Uninstall (Settings > Apps), once while Wisko runs and once after closing it | Refused while Wisko runs. Afterwards the app folder is gone, and `Documents\Wisko\Projects` and `%LOCALAPPDATA%\Wisko` remain. |
| T9 | S2 | Upgrade: install an older test build, open a project, then install the new build | Project intact; stale files of the old build gone |
| T10 | S1 | Only if a native rebuild is approved: an app folder with the rebuilt DLLs | Import and dialogs work without the runtime (NATIVE-LIBRARIES.md check 7) |
| T11 | S1 | Installer "for all users" (administrator) | UAC prompt; installed in `Program Files` |
| T12 | Windows 10 guest | T2 to T5, only with a suitable licensed Windows 10 image (no evaluation exists) | Needed before Windows 10 may be called supported; until then it is not blocked but unverified |
| T13 | S1, if T1 found Smart App Control on (or in evaluation) | The marked installer and the marked portable `Wisko.exe` | Expected for an unsigned build: blocked, without a per-app way to allow it. Record the exact message. This is what users with Smart App Control on would see ([distribution-and-signing.md](../../docs/distribution-and-signing.md)). Do not turn Smart App Control off in the snapshot used for the other tests. |

The VM's virtual GPU is not representative: rendering looks and speed are not judged here. If OpenGL 3.3 is not
available in the VM, record what Wisko shows; that tests its error path.

## Rollback

- **The VM:** revert to a snapshot. When finished, use VMware's Manage > Delete from Disk, then delete `D:\WiskoVM`.
  The evaluation expires after 90 days, after which it shuts down every hour; reinstall from the ISO or delete it.
- **VMware:** uninstall it in Settings > Apps, which removes its services and virtual network adapters. Check Device
  Manager for leftover VMware adapters.
- **Windows Hypervisor Platform:** from an administrator PowerShell, then reboot. Virtual Machine Platform (WSL 2) is
  a separate feature and stays as it is.

  ```powershell
  Disable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform
  ```

- **System Restore** is off on C:, so there is no restore point. Optionally, and only with approval: switch it on and
  create a restore point before installing anything. C: has little free space, so keep its space limit small.
- Nothing here touches the installed Wisko, the user's projects or the repository.

## Alternatives considered

- **Hyper-V and Windows Sandbox:** not available on Windows 11 Home.
- **A GitHub-hosted Windows runner:** it has Visual Studio and the Visual C++ runtime installed, so it is not a clean
  machine, and it has no interactive desktop.
- **VirtualBox:** it would also need the Windows Hypervisor Platform on this host. VMware was chosen earlier.
- **A second, freshly installed physical PC:** the most realistic option, if one is available.
