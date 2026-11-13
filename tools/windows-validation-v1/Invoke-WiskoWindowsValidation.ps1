<#
.SYNOPSIS
    Wisko Engine 0.1.0: automated Windows validation of the approved release commit.

.DESCRIPTION
    Runs against a FRESH clone of MrWisko/Wisko-Engine checked out at the approved commit, never against a development
    checkout. In order:

      1. Repository checks: the commit, the origin remote, a clean working tree, no earlier dist\ folder.
      2. Prerequisites: Windows, PowerShell, git, the .NET SDK that global.json asks for, the Microsoft Visual C++
         Redistributable (x64) 14.44 or later, Inno Setup 6, free disk space, GPU and display information.
         Nothing is ever installed. A missing prerequisite stops the script with what to install.
      3. dotnet build Wisko.sln, Debug and Release (warnings and errors counted).
      4. The headless test suite, Release and Debug.
      5. Targeted runs: "Save safety", "Project save journal" and every section whose title contains "Windows".
      6. The GUI smoke test (--gui-smoke): only with -RunGuiSmoke, on the local, interactive desktop. Otherwise its
         exact command is written to the output folder for later.
      7. Packaging with the repository's own script: scripts\build-windows-app.ps1 -Package (portable zip and, with
         Inno Setup 6, the installer), then read-only checks of both files.
      8. Optional, only with -InstallerSmokeTest: the repository's isolated installer TEST build ("Wisko Installer
         Test", its own AppId), installed silently into the output folder and uninstalled again.
      9. SHA-256 checksums (SHA256SUMS.txt) of the release files.
     10. SUMMARY.md, all logs, and a results zip to bring back.

    Safety:
      - Every test uses new folders inside the output folder. The test runner deletes its --work folder when it
        finishes, so only folders this script has just created are ever passed to it.
      - Documents\Wisko (your projects and History) is never used. The repository's tests and the GUI smoke test keep
        their projects, History and preferences in their work folders.
      - The real installer is built but never run. The installed Wisko (%LOCALAPPDATA%\Programs\Wisko) and its
        shortcuts are never touched; build-windows-app.ps1 is never given -Install.
      - Nothing is pushed, tagged or published. Only the clone's ignored folders (bin\, obj\, dist\) are written.

.PARAMETER RepoPath
    The fresh clone of https://github.com/MrWisko/Wisko-Engine (see 00-START-HERE.md).

.PARAMETER OutputRoot
    A new folder for logs, packages and results. Default: %USERPROFILE%\WiskoValidation\run-<date>-<time>.
    It must not exist yet and must not be inside the clone.

.PARAMETER PrerequisitesOnly
    Only steps 1 and 2: checks the clone and the tools, builds nothing. Run this first.

.PARAMETER RunGuiSmoke
    Also run the GUI smoke test. Needs the local desktop (not Remote Desktop), unlocked, the display on, 100% display
    scaling; a Wisko window appears and is driven by the test. Do not touch the mouse or keyboard while it runs.

.PARAMETER GuiOnly
    With -RunGuiSmoke: run only the GUI smoke test (after the repository and prerequisite checks and a Release build).

.PARAMETER InstallerSmokeTest
    Also build and silently install and uninstall the isolated installer TEST build inside the output folder. It adds
    a temporary "Wisko Installer Test" entry to Apps and the Start menu, which the silent uninstall removes again.

.PARAMETER InnoCompiler
    The full path of ISCC.exe when Inno Setup 6 is not in a standard place (passed on to build-windows-app.ps1).

.PARAMETER KeepWork
    Pass --keep to the test runner so its work folders stay (inside the output folder) for investigation. They can
    be large.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Invoke-WiskoWindowsValidation.ps1 -RepoPath C:\WiskoValidation\Wisko-Engine -PrerequisitesOnly

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Invoke-WiskoWindowsValidation.ps1 -RepoPath C:\WiskoValidation\Wisko-Engine -RunGuiSmoke
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RepoPath,
    [string]$OutputRoot,
    [string]$ExpectedCommit = 'f2488258927abae3bd47998efebd551d7ac60b7c',
    [string]$ExpectedVersion = '0.1.0',
    [switch]$PrerequisitesOnly,
    [switch]$RunGuiSmoke,
    [switch]$GuiOnly,
    [switch]$InstallerSmokeTest,
    [string]$InnoCompiler,
    [switch]$KeepWork
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptVersion = '1.0 (2026-11-11)'
$ReleaseAppId = '{1F1F2E25-3D43-4D51-A531-87DB5CD0F33A}'      # packaging\windows\Wisko.iss, release builds
$TestAppId = '{6E0B4F51-9C2A-4B7E-8D35-0F6A1C9E2B74}'         # packaging\windows\Wisko.iss, /DInstallerTest builds
$ExampleNames = @('Gameplay Basics Demo', 'Animation Demo', 'Gameplay Systems Showcase', 'Graphics Showcase', 'Editor and Chat Showcase')
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:Results = New-Object System.Collections.Generic.List[object]
$script:Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

# ------------------------------------------------------------------------------------------------------------------
# Helpers
# ------------------------------------------------------------------------------------------------------------------

function Stop-Validation([string]$Message) {
    $script:Results.Add([pscustomobject]@{ Step = 'Stopped'; Status = 'FAIL'; Detail = $Message; Required = $true })
    Write-Host ''
    Write-Host "STOPPED: $Message" -ForegroundColor Red
    if ($script:LogDir -and (Test-Path $script:LogDir)) {
        [IO.File]::AppendAllText((Join-Path $script:LogDir 'STOPPED.txt'), $Message + "`r`n", $Utf8NoBom)
    }
    exit 3
}

function Add-Result([string]$Step, [string]$Status, [string]$Detail, [bool]$Required = $true) {
    $script:Results.Add([pscustomobject]@{ Step = $Step; Status = $Status; Detail = $Detail; Required = $Required })
    $color = 'Cyan'
    if ($Status -eq 'PASS') { $color = 'Green' }
    elseif ($Status -eq 'FAIL') { $color = 'Red' }
    elseif ($Status -eq 'WARN') { $color = 'Yellow' }
    Write-Host ('[{0}] {1}: {2}' -f $Status, $Step, $Detail) -ForegroundColor $color
}

function Write-Section([string]$Title) {
    Write-Host ''
    Write-Host ('==== {0} ====' -f $Title) -ForegroundColor White
}

# Runs a native program from $WorkingDirectory, shows its output and appends it (UTF-8) to $LogFile. Returns the
# exit code. stderr lines are kept as text (Windows PowerShell would otherwise turn them into errors).
function Invoke-Logged([string]$LogFile, [string]$WorkingDirectory, [string]$FilePath, [string[]]$Arguments) {
    $writer = New-Object System.IO.StreamWriter($LogFile, $true, $Utf8NoBom)
    $previous = $ErrorActionPreference
    $code = -1
    Push-Location $WorkingDirectory
    try {
        $writer.WriteLine(('> {0} {1}' -f $FilePath, ($Arguments -join ' ')))
        $writer.WriteLine(('  (in {0}, {1})' -f $WorkingDirectory, (Get-Date -Format 's')))
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = -1
        & $FilePath @Arguments 2>&1 | ForEach-Object {
            $line = "$_"
            Write-Host $line
            $writer.WriteLine($line)
        }
        $code = $LASTEXITCODE
        $writer.WriteLine(('< exit code {0} ({1})' -f $code, (Get-Date -Format 's')))
    }
    finally {
        $ErrorActionPreference = $previous
        Pop-Location
        $writer.Dispose()
    }
    return $code
}

# The output of a short command as one string (for version checks); never throws.
function Get-CommandText([string]$WorkingDirectory, [string]$FilePath, [string[]]$Arguments) {
    $previous = $ErrorActionPreference
    Push-Location $WorkingDirectory
    try {
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = -1
        $text = (& $FilePath @Arguments 2>&1 | ForEach-Object { "$_" }) -join "`n"
        return [pscustomobject]@{ Text = $text.Trim(); ExitCode = $LASTEXITCODE }
    }
    catch {
        return [pscustomobject]@{ Text = "$_"; ExitCode = -1 }
    }
    finally {
        $ErrorActionPreference = $previous
        Pop-Location
    }
}

function Read-BuildCounts([string]$LogFile) {
    $text = [IO.File]::ReadAllText($LogFile)
    $w = [regex]::Matches($text, '(?m)^\s*(\d+) Warning\(s\)')
    $e = [regex]::Matches($text, '(?m)^\s*(\d+) Error\(s\)')
    $warnings = $null; $errors = $null
    if ($w.Count -gt 0) { $warnings = [int]$w[$w.Count - 1].Groups[1].Value }
    if ($e.Count -gt 0) { $errors = [int]$e[$e.Count - 1].Groups[1].Value }
    return [pscustomobject]@{ Warnings = $warnings; Errors = $errors }
}

# The test runner ends with "=== SUMMARY: N passed, M failed ===" and prints each failed check as "[FAIL] ...".
function Read-TestSummary([string]$LogFile) {
    $text = [IO.File]::ReadAllText($LogFile)
    $m = [regex]::Matches($text, '=== SUMMARY: (\d+) passed, (\d+) failed ===')
    $fails = @([regex]::Matches($text, '(?m)^\[FAIL\].*$') | ForEach-Object { $_.Value.Trim() })
    if ($m.Count -eq 0) { return [pscustomobject]@{ Found = $false; Passed = 0; Failed = 0; FailLines = $fails } }
    $last = $m[$m.Count - 1]
    return [pscustomobject]@{ Found = $true; Passed = [int]$last.Groups[1].Value; Failed = [int]$last.Groups[2].Value; FailLines = $fails }
}

# A new, empty folder that did not exist before (the test runner deletes its --work folder at the end).
function New-FreshFolder([string]$Path) {
    if (Test-Path $Path) { Stop-Validation "internal: $Path already exists; refusing to reuse it" }
    New-Item -ItemType Directory -Path $Path | Out-Null
    return (Resolve-Path $Path).Path
}

function Test-PathInside([string]$Child, [string]$Parent) {
    $c = [IO.Path]::GetFullPath($Child).TrimEnd('\') + '\'
    $p = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    return $c.StartsWith($p, [StringComparison]::OrdinalIgnoreCase)
}

function Find-InnoCompiler {
    if ($InnoCompiler) {
        if (Test-Path $InnoCompiler) { return (Resolve-Path $InnoCompiler).Path }
        return $null
    }
    # The same places scripts\build-windows-app.ps1 looks.
    $candidates = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

function Get-UninstallEntry([string]$AppId) {
    $key = $AppId + '_is1'
    foreach ($root in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
                      'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
        $path = Join-Path $root $key
        if (Test-Path $path) {
            $p = Get-ItemProperty $path
            $version = ''; $location = ''; $name = ''
            if ($p.PSObject.Properties['DisplayVersion']) { $version = $p.DisplayVersion }
            if ($p.PSObject.Properties['InstallLocation']) { $location = $p.InstallLocation }
            if ($p.PSObject.Properties['DisplayName']) { $name = $p.DisplayName }
            return [pscustomobject]@{ Key = $path; Name = $name; Version = $version; Location = $location }
        }
    }
    return $null
}

# Writes a zip whose entry names use forward slashes (Windows PowerShell's Compress-Archive writes backslashes).
function New-ResultsZip([string]$SourceFolder, [string]$ZipPath, [string[]]$ExcludeTopFolders) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $base = (Resolve-Path $SourceFolder).Path.TrimEnd('\')
    $stream = [IO.File]::Open($ZipPath, [IO.FileMode]::CreateNew)
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -Recurse -File $base) {
            $relative = $file.FullName.Substring($base.Length + 1)
            $top = $relative.Split('\')[0]
            if ($ExcludeTopFolders -contains $top) { continue }
            if ($file.FullName -eq $ZipPath) { continue }
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
        $stream.Dispose()
    }
}

# ------------------------------------------------------------------------------------------------------------------
# 0. Output folder, console, transcript
# ------------------------------------------------------------------------------------------------------------------

if ($env:OS -ne 'Windows_NT') { Write-Host 'This script runs on Windows only.' -ForegroundColor Red; exit 3 }
if (-not [Environment]::Is64BitProcess) { Write-Host 'Run this in 64-bit PowerShell (not "Windows PowerShell (x86)").' -ForegroundColor Red; exit 3 }

if (-not (Test-Path $RepoPath)) { Write-Host "RepoPath does not exist: $RepoPath (clone the repository first; see 00-START-HERE.md)" -ForegroundColor Red; exit 3 }
$RepoPath = (Resolve-Path $RepoPath).Path

if (-not $OutputRoot) { $OutputRoot = Join-Path $env:USERPROFILE ('WiskoValidation\run-' + $script:Stamp) }
$OutputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
if (Test-Path $OutputRoot) { Write-Host "OutputRoot already exists: $OutputRoot (choose a new folder)" -ForegroundColor Red; exit 3 }
if (Test-PathInside $OutputRoot $RepoPath) { Write-Host 'OutputRoot must not be inside the clone.' -ForegroundColor Red; exit 3 }
$documentsWisko = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Wisko'
if (Test-PathInside $OutputRoot $documentsWisko) { Write-Host 'OutputRoot must not be inside Documents\Wisko.' -ForegroundColor Red; exit 3 }

New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$script:LogDir = New-FreshFolder (Join-Path $OutputRoot 'logs')
$WorkRoot = New-FreshFolder (Join-Path $OutputRoot 'work')

$originalOutputEncoding = [Console]::OutputEncoding
try { [Console]::OutputEncoding = $Utf8NoBom } catch { }   # test names contain non-ASCII text (for example accents)
$transcriptStarted = $false
try { Start-Transcript -Path (Join-Path $script:LogDir 'transcript.txt') | Out-Null; $transcriptStarted = $true } catch { }

# Keep the display and the computer awake while this script runs (the Windows frame-pacing check measures the real
# compositor). This thread's request ends when the script ends.
Add-Type -Namespace WiskoValidation -Name Power -MemberDefinition @'
[DllImport("kernel32.dll")]
public static extern uint SetThreadExecutionState(uint esFlags);
'@
[void][WiskoValidation.Power]::SetThreadExecutionState([uint32]2147483651)   # ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED

Write-Host "Wisko Engine $ExpectedVersion Windows validation, script $ScriptVersion"
Write-Host "Clone:  $RepoPath"
Write-Host "Output: $OutputRoot"

try {
    # --------------------------------------------------------------------------------------------------------------
    # 1. Repository checks
    # --------------------------------------------------------------------------------------------------------------
    Write-Section '1. Repository'
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Stop-Validation 'git is not installed or not on PATH (install Git for Windows, then open a new PowerShell)' }

    $head = (Get-CommandText $RepoPath 'git' @('rev-parse', 'HEAD')).Text
    if ($head -ne $ExpectedCommit) { Stop-Validation "the clone is at '$head', not the approved commit $ExpectedCommit (git checkout $ExpectedCommit)" }
    Add-Result 'Commit' 'PASS' $head

    $origin = (Get-CommandText $RepoPath 'git' @('remote', 'get-url', 'origin')).Text
    if ($origin -notmatch 'github\.com[/:]MrWisko/Wisko-Engine(\.git)?$') {
        Stop-Validation "origin is '$origin', not MrWisko/Wisko-Engine. Use a fresh clone of the new repository, not a development checkout."
    }
    Add-Result 'Origin remote' 'PASS' $origin

    $status = (Get-CommandText $RepoPath 'git' @('status', '--porcelain')).Text
    if ($status) { Stop-Validation "the clone has changes or untracked files; use a fresh clone:`n$status" }
    Add-Result 'Clean working tree' 'PASS' 'no changes, no untracked files'

    # Packaging replaces dist\ in the clone: a full run needs a clone whose dist\ does not exist yet, so the packages
    # are known to come from this run. A prerequisites-only or GUI-only run does not package.
    $dist = Join-Path $RepoPath 'dist'
    if (Test-Path $dist) {
        if ($PrerequisitesOnly -or $GuiOnly) { Add-Result 'dist folder' 'INFO' "$dist exists from an earlier run (fine for this run, which does not package)" $false }
        else { Stop-Validation "$dist already exists from an earlier run; make a fresh clone for a full run (see 00-START-HERE.md)" }
    }

    # --------------------------------------------------------------------------------------------------------------
    # 2. Prerequisites (checked only; nothing is installed)
    # --------------------------------------------------------------------------------------------------------------
    Write-Section '2. Prerequisites'
    $envLog = Join-Path $script:LogDir '00-environment.txt'
    $os = Get-CimInstance Win32_OperatingSystem
    $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
    $gpus = @(Get-CimInstance Win32_VideoController)
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("Date: $(Get-Date -Format 's')")
    $lines.Add("Windows: $($os.Caption) $($os.Version) build $($os.BuildNumber) ($($os.OSArchitecture))")
    $lines.Add("CPU: $cpu")
    $lines.Add(('Memory: {0:N1} GB' -f ($os.TotalVisibleMemorySize / 1MB)))
    foreach ($g in $gpus) {
        $lines.Add(("GPU: {0}, driver {1}, {2}x{3} at {4} Hz" -f $g.Name, $g.DriverVersion, $g.CurrentHorizontalResolution, $g.CurrentVerticalResolution, $g.CurrentRefreshRate))
    }
    $dpi = $null
    try { $dpi = (Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name AppliedDPI -ErrorAction Stop).AppliedDPI } catch { }
    if ($dpi) { $lines.Add(('Display scaling: {0}% (AppliedDPI {1})' -f [math]::Round($dpi / 96 * 100), $dpi)) } else { $lines.Add('Display scaling: unknown') }
    $sessionName = '' + $env:SESSIONNAME
    $lines.Add("Session: '$sessionName', interactive: $([Environment]::UserInteractive)")
    $lines.Add("PowerShell: $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))")
    [IO.File]::WriteAllLines($envLog, $lines, $Utf8NoBom)
    foreach ($l in $lines) { Write-Host $l }

    $osBuild = [int]$os.BuildNumber
    if ($osBuild -ge 22000) { Add-Result 'Windows version' 'PASS' "$($os.Caption) (build $osBuild)" }
    else { Add-Result 'Windows version' 'WARN' "build $osBuild: Wisko 0.1.0 supports Windows 11; Windows 10 is not verified" }

    if ($sessionName -like 'RDP*') { Add-Result 'Local session' 'WARN' 'Remote Desktop session: the frame-pacing check and the GUI smoke test need the local console' }
    else { Add-Result 'Local session' 'PASS' "session '$sessionName'" }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Stop-Validation '.NET SDK not found (install the .NET 10 SDK 10.0.402 or a later 10.0 SDK from https://dotnet.microsoft.com/download/dotnet/10.0, then open a new PowerShell)' }
    $sdk = Get-CommandText $RepoPath 'dotnet' @('--version')
    $sdkList = (Get-CommandText $RepoPath 'dotnet' @('--list-sdks')).Text
    [IO.File]::AppendAllText($envLog, "`r`ndotnet --version (in the clone, honours global.json): $($sdk.Text)`r`ndotnet --list-sdks:`r`n$sdkList`r`n", $Utf8NoBom)
    $sdkOk = $false
    if ($sdk.ExitCode -eq 0 -and $sdk.Text -match '(?m)^10\.0\.(\d+)\s*$') { $sdkOk = ([int]$Matches[1] -ge 402) }
    if (-not $sdkOk) {
        Stop-Validation "no .NET SDK satisfies global.json (10.0.402 or a later 10.0 SDK). dotnet says: $($sdk.Text). Installed: $sdkList"
    }
    Add-Result '.NET SDK (global.json)' 'PASS' $sdk.Text

    # The Visual C++ runtime that Assimp64.dll (model import) and nfd.dll (file dialogs) need; the same files and
    # version Wisko itself checks. The headless suite imports models, so this is required here.
    $vcProblems = @()
    $vcVersions = @()
    foreach ($dll in 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll') {
        $path = Join-Path ([Environment]::GetFolderPath('System')) $dll
        if (-not (Test-Path $path)) { $vcProblems += "$dll missing"; continue }
        $vi = (Get-Item $path).VersionInfo
        $v = New-Object Version($vi.FileMajorPart, $vi.FileMinorPart, $vi.FileBuildPart, $vi.FilePrivatePart)
        $vcVersions += "$dll $v"
        if ($v -lt (New-Object Version(14, 44))) { $vcProblems += "$dll is $v (needs 14.44 or later)" }
    }
    if ($vcProblems.Count -gt 0) {
        Stop-Validation ("Microsoft Visual C++ Redistributable (x64) 14.44 or later is needed: " + ($vcProblems -join '; ') + '. Install it from https://aka.ms/vc14/vc_redist.x64.exe yourself, then run again.')
    }
    Add-Result 'Visual C++ runtime (x64)' 'PASS' ($vcVersions -join ', ')

    $iscc = Find-InnoCompiler
    if ($iscc) {
        $isccVersion = (Get-Item $iscc).VersionInfo.ProductVersion
        Add-Result 'Inno Setup 6 (installer)' 'PASS' "$iscc ($isccVersion)"
    }
    else {
        Add-Result 'Inno Setup 6 (installer)' 'FAIL' 'ISCC.exe not found: the installer cannot be built (the portable zip still can). Install Inno Setup 6 or pass -InnoCompiler.'
    }

    $drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($OutputRoot).Substring(0, 1))
    $repoDrive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($RepoPath).Substring(0, 1))
    $freeGb = [math]::Round([math]::Min($drive.Free, $repoDrive.Free) / 1GB, 1)
    if ($freeGb -lt 10) { Add-Result 'Free disk space' 'WARN' "$freeGb GB free; 10 GB or more is recommended" }
    else { Add-Result 'Free disk space' 'PASS' "$freeGb GB free" }

    $realInstallBefore = Get-UninstallEntry $ReleaseAppId
    if ($realInstallBefore) { Add-Result 'Installed Wisko (left untouched)' 'INFO' ("{0} {1} at {2}" -f $realInstallBefore.Name, $realInstallBefore.Version, $realInstallBefore.Location) $false }
    else { Add-Result 'Installed Wisko (left untouched)' 'INFO' 'no Wisko installed with the release installer' $false }

    if ($PrerequisitesOnly) {
        Write-Section 'Prerequisites only: nothing was built'
        return
    }

    $testsProject = Join-Path $RepoPath 'Tests\Wisko.Tests\Wisko.Tests.csproj'
    function Get-TestRunner([string]$Configuration) {
        $r = Get-CommandText $RepoPath 'dotnet' @('msbuild', $testsProject, "-p:Configuration=$Configuration", '-getProperty:TargetPath')
        return $r.Text.Trim()
    }

    function Invoke-TestRun([string]$Name, [string]$LogName, [string]$Configuration, [string[]]$ExtraArguments, [bool]$Required) {
        $runner = Get-TestRunner $Configuration
        if (-not (Test-Path $runner)) { Add-Result $Name 'FAIL' "test runner not found ($runner)" $Required; return }
        $work = New-FreshFolder (Join-Path $WorkRoot ($LogName -replace '[^A-Za-z0-9-]', '-'))
        $arguments = @($runner) + $ExtraArguments + @('--work', $work)
        if ($KeepWork) { $arguments += '--keep' }
        $log = Join-Path $script:LogDir "$LogName.txt"
        $started = Get-Date
        $code = Invoke-Logged $log $RepoPath 'dotnet' $arguments
        $seconds = [math]::Round(((Get-Date) - $started).TotalSeconds)
        $summary = Read-TestSummary $log
        if (-not $summary.Found) { Add-Result $Name 'FAIL' "no SUMMARY line (exit code $code, ${seconds}s); see logs\$LogName.txt" $Required; return }
        $detail = "$($summary.Passed) passed, $($summary.Failed) failed, exit code $code, ${seconds}s"
        if ($code -eq 0 -and $summary.Failed -eq 0) { Add-Result $Name 'PASS' $detail $Required }
        else {
            Add-Result $Name 'FAIL' ($detail + '; ' + (($summary.FailLines | Select-Object -First 5) -join ' | ')) $Required
        }
    }

    # --------------------------------------------------------------------------------------------------------------
    # 3. Builds
    # --------------------------------------------------------------------------------------------------------------
    $configurations = @('Debug', 'Release')
    if ($GuiOnly) { $configurations = @('Release') }
    foreach ($configuration in $configurations) {
        Write-Section "3. Build $configuration"
        $log = Join-Path $script:LogDir ("10-build-{0}.txt" -f $configuration.ToLowerInvariant())
        $code = Invoke-Logged $log $RepoPath 'dotnet' @('build', 'Wisko.sln', '-c', $configuration, '-nologo', '-tl:off')
        $counts = Read-BuildCounts $log
        $detail = "exit code $code, $($counts.Warnings) warning(s), $($counts.Errors) error(s)"
        if ($code -eq 0 -and $counts.Warnings -eq 0 -and $counts.Errors -eq 0) { Add-Result "Build $configuration" 'PASS' $detail }
        else { Add-Result "Build $configuration" 'FAIL' $detail }
    }
    if (@($script:Results | Where-Object { $_.Step -like 'Build *' -and $_.Status -eq 'FAIL' }).Count -gt 0) {
        Stop-Validation 'a build failed or had warnings; see logs\10-build-*.txt (nothing else was run)'
    }

    if (-not $GuiOnly) {
        # ----------------------------------------------------------------------------------------------------------
        # 4. Headless suite
        # ----------------------------------------------------------------------------------------------------------
        Write-Section '4. Headless suite (Release)'
        Invoke-TestRun 'Headless suite, Release' '20-headless-release' 'Release' @() $true
        Write-Section '4. Headless suite (Debug)'
        Invoke-TestRun 'Headless suite, Debug' '21-headless-debug' 'Debug' @() $true

        # ----------------------------------------------------------------------------------------------------------
        # 5. Targeted runs (--only matches section titles, case-insensitive)
        # ----------------------------------------------------------------------------------------------------------
        Write-Section '5. Save safety'
        Invoke-TestRun 'Save safety (Release)' '30-save-safety' 'Release' @('--only', 'Save safety') $true
        Write-Section '5. Project save journal'
        Invoke-TestRun 'Project save journal (Release)' '31-save-journal' 'Release' @('--only', 'Project save journal') $true
        Write-Section '5. Windows-specific sections'
        # Matches "Visual C++ runtime (Windows)...", "Windows frame pacing (compositor wait)" and "macOS fix pass: editor
        # shortcuts (Windows Ctrl unchanged, ...)".
        Invoke-TestRun 'Windows-specific sections (Release)' '32-windows-specific' 'Release' @('--only', 'Windows') $true
    }

    # --------------------------------------------------------------------------------------------------------------
    # 6. GUI smoke test (interactive desktop only)
    # --------------------------------------------------------------------------------------------------------------
    Write-Section '6. GUI smoke test'
    $guiShots = Join-Path $OutputRoot 'gui-screenshots'
    $releaseRunner = Get-TestRunner 'Release'
    $guiCommand = "cd `"$RepoPath`"`r`ndotnet `"$releaseRunner`" --gui-smoke --screenshots `"<new empty folder>`" --work `"<new empty folder>`""
    [IO.File]::WriteAllText((Join-Path $script:LogDir 'gui-smoke-command.txt'), $guiCommand + "`r`n", $Utf8NoBom)
    $interactive = [Environment]::UserInteractive -and ($sessionName -eq 'Console') -and (-not $env:SSH_CONNECTION)
    if (-not $RunGuiSmoke) {
        Add-Result 'GUI smoke test' 'NOT RUN' 'prepared: run again with -RunGuiSmoke on the local desktop (logs\gui-smoke-command.txt has the command)'
    }
    elseif (-not $interactive) {
        Add-Result 'GUI smoke test' 'NOT RUN' "no local interactive desktop (session '$sessionName'); it needs the local console, unlocked, display on"
    }
    else {
        if ($dpi -and $dpi -ne 96) { Add-Result 'Display scaling for the GUI test' 'WARN' ('{0}%: the tour''s click positions assume 100%' -f [math]::Round($dpi / 96 * 100)) $false }
        Write-Host 'A Wisko window will open and be driven by the test. Do not touch the mouse or keyboard until it ends.' -ForegroundColor Yellow
        Start-Sleep -Seconds 10
        New-Item -ItemType Directory -Path $guiShots | Out-Null
        Invoke-TestRun 'GUI smoke test (Release)' '40-gui-smoke' 'Release' @('--gui-smoke', '--screenshots', $guiShots) $true
    }

    if ($GuiOnly) { return }

    # --------------------------------------------------------------------------------------------------------------
    # 7. Packaging (the repository's own script; never -Install)
    # --------------------------------------------------------------------------------------------------------------
    Write-Section '7. Packaging: scripts\build-windows-app.ps1 -Package'
    $packageLog = Join-Path $script:LogDir '50-package.txt'
    $packageArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $RepoPath 'scripts\build-windows-app.ps1'), '-Package')
    if ($InnoCompiler) { $packageArguments += @('-InnoCompiler', $InnoCompiler) }
    $code = Invoke-Logged $packageLog $RepoPath 'powershell.exe' $packageArguments
    $zip = Join-Path $dist "Wisko-$ExpectedVersion-win-x64-portable.zip"
    $setup = Join-Path $dist "Wisko-$ExpectedVersion-win-x64-setup.exe"
    if ($code -ne 0) { Add-Result 'Packaging script' 'FAIL' "exit code $code; see logs\50-package.txt" }
    else { Add-Result 'Packaging script' 'PASS' 'exit code 0 (its own checks passed: files, self-contained, x64, GUI subsystem, icon, no admin manifest, no absolute paths)' }

    $packagesDir = New-FreshFolder (Join-Path $OutputRoot 'packages')
    $checkLog = Join-Path $script:LogDir '51-package-checks.txt'
    $checkLines = New-Object System.Collections.Generic.List[string]

    if (Test-Path $zip) {
        Copy-Item $zip $packagesDir
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $names = @($archive.Entries | ForEach-Object { $_.FullName })
            $required = @('Wisko/Wisko.exe', 'Wisko/Wisko.dll', 'Wisko/Wisko.Engine.dll', 'Wisko/Wisko.runtimeconfig.json', 'Wisko/glfw3.dll',
                          'Wisko/lua54.dll', 'Wisko/Assimp64.dll', 'Wisko/nfd.dll', 'Wisko/LICENSE.txt', 'Wisko/NOTICE.txt',
                          'Wisko/THIRD-PARTY-NOTICES.md', 'Wisko/BRANDING.md', 'Wisko/licenses/dotnet/LICENSE.TXT',
                          'Wisko/licenses/dotnet/THIRD-PARTY-NOTICES.TXT')
            $missing = @($required | Where-Object { $names -notcontains $_ })
            $backslashes = @($names | Where-Object { $_.Contains('\') })
            $examples = @($names | Where-Object { $_ -like 'Wisko/Examples/*/*' } | ForEach-Object { $_.Split('/')[2] } | Sort-Object -Unique)
            $missingExamples = @($ExampleNames | Where-Object { $examples -notcontains $_ })
            $checkLines.Add("Portable zip: $zip")
            $checkLines.Add("  entries: $($names.Count); examples: $($examples -join ', ')")
            $checkLines.Add("  missing required files: $($missing -join ', ')")
            $checkLines.Add("  entries with backslashes: $($backslashes.Count)")
            $checkLines.Add("  missing examples: $($missingExamples -join ', ')")
            if ($missing.Count -eq 0 -and $backslashes.Count -eq 0 -and $missingExamples.Count -eq 0) {
                Add-Result 'Portable zip contents' 'PASS' ('{0} entries, app, licences and the 5 example projects; {1:N0} MB' -f $names.Count, ((Get-Item $zip).Length / 1MB))
            }
            else { Add-Result 'Portable zip contents' 'FAIL' "missing: $($missing -join ', ') $($missingExamples -join ', '); backslash entries: $($backslashes.Count)" }
        }
        finally { $archive.Dispose() }
    }
    else { Add-Result 'Portable zip' 'FAIL' "not produced: $zip" }

    $exe = Join-Path $dist 'Wisko\Wisko.exe'
    if (Test-Path $exe) {
        $vi = (Get-Item $exe).VersionInfo
        $sig = (Get-AuthenticodeSignature $exe).Status
        $checkLines.Add("Wisko.exe: ProductName '$($vi.ProductName)', ProductVersion '$($vi.ProductVersion)', FileVersion '$($vi.FileVersion)', signature $sig")
        if ($vi.ProductName -eq 'Wisko Engine' -and ('' + $vi.ProductVersion).StartsWith($ExpectedVersion)) {
            Add-Result 'Wisko.exe version info' 'PASS' "Wisko Engine $($vi.ProductVersion); signature: $sig (unsigned is expected for 0.1.0)"
        }
        else { Add-Result 'Wisko.exe version info' 'FAIL' "ProductName '$($vi.ProductName)', ProductVersion '$($vi.ProductVersion)'" }
    }

    if (Test-Path $setup) {
        Copy-Item $setup $packagesDir
        $vi = (Get-Item $setup).VersionInfo
        $sig = (Get-AuthenticodeSignature $setup).Status
        $checkLines.Add("Installer: $setup; ProductName '$($vi.ProductName)', ProductVersion '$($vi.ProductVersion)', FileDescription '$($vi.FileDescription)', signature $sig")
        if ($vi.ProductName -eq 'Wisko Engine' -and ('' + $vi.ProductVersion).StartsWith($ExpectedVersion)) {
            Add-Result 'Installer built' 'PASS' ('{0} ({1:N0} MB); signature: {2} (unsigned is expected for 0.1.0); not run' -f (Split-Path -Leaf $setup), ((Get-Item $setup).Length / 1MB), $sig)
        }
        else { Add-Result 'Installer built' 'FAIL' "ProductName '$($vi.ProductName)', ProductVersion '$($vi.ProductVersion)'" }
    }
    else { Add-Result 'Installer built' 'FAIL' "not produced: $setup (Inno Setup 6 missing or failed; see logs\50-package.txt)" }
    [IO.File]::WriteAllLines($checkLog, $checkLines, $Utf8NoBom)

    # --------------------------------------------------------------------------------------------------------------
    # 8. Optional: the isolated installer TEST build, installed and uninstalled silently inside the output folder
    # --------------------------------------------------------------------------------------------------------------
    if ($InstallerSmokeTest) {
        Write-Section '8. Installer smoke test (isolated "Wisko Installer Test" build)'
        if (-not $iscc) { Add-Result 'Installer smoke test' 'FAIL' 'Inno Setup 6 not found' }
        elseif (-not (Test-Path (Join-Path $dist 'Examples'))) { Add-Result 'Installer smoke test' 'FAIL' 'dist\Examples missing (packaging failed)' }
        elseif (Get-UninstallEntry $TestAppId) { Add-Result 'Installer smoke test' 'FAIL' 'a "Wisko Installer Test" installation already exists; uninstall it first (Settings > Apps); nothing was changed' }
        else {
            $itRoot = New-FreshFolder (Join-Path $OutputRoot 'installer-test')
            $itOut = New-FreshFolder (Join-Path $itRoot 'out')
            $itProjects = Join-Path $itRoot 'projects'
            $itApp = Join-Path $itRoot 'app'
            $itLog = Join-Path $script:LogDir '52-installer-test.txt'
            # The command from packaging\windows\README.md, "Testing the installer".
            $isccArguments = @('/DInstallerTest=1', "/DTestProjectsFolder=$itProjects", "/DAppVersion=$ExpectedVersion",
                               "/DSourceDir=$(Join-Path $dist 'Wisko')", "/DExamplesDir=$(Join-Path $dist 'Examples')", "/DOutputDir=$itOut",
                               "/DIconFile=$(Join-Path $RepoPath 'branding\AppIcon\Wisko-AppIcon.ico')",
                               "/DWizardImages=$(Join-Path $RepoPath 'branding\Installer\Wisko-WizardImage-*.png')",
                               "/DWizardSmallImages=$(Join-Path $RepoPath 'branding\Installer\Wisko-WizardSmallImage-*.png')",
                               (Join-Path $RepoPath 'packaging\windows\Wisko.iss'))
            $code = Invoke-Logged $itLog $RepoPath $iscc $isccArguments
            $testSetup = Join-Path $itOut "Wisko-$ExpectedVersion-win-x64-setup-INSTALLER-TEST.exe"
            if ($code -ne 0 -or -not (Test-Path $testSetup)) { Add-Result 'Installer smoke test' 'FAIL' "the test build was not made (exit code $code)" }
            else {
                $setupArgs = '/VERYSILENT /SUPPRESSMSGBOXES /CURRENTUSER /DIR="' + $itApp + '" /TASKS="examples" /LOG="' + (Join-Path $script:LogDir '52-installer-test-setup-log.txt') + '"'
                $p = Start-Process -FilePath $testSetup -ArgumentList $setupArgs -Wait -PassThru
                $entry = Get-UninstallEntry $TestAppId
                $copied = @()
                if (Test-Path $itProjects) { $copied = @(Get-ChildItem -Directory $itProjects | ForEach-Object { $_.Name }) }
                $installOk = ($p.ExitCode -eq 0) -and (Test-Path (Join-Path $itApp 'Wisko.exe')) -and ($null -ne $entry) -and ($copied.Count -eq $ExampleNames.Count)
                $detail = "setup exit $($p.ExitCode); app: $(Test-Path (Join-Path $itApp 'Wisko.exe')); Apps entry: $($null -ne $entry); examples copied: $($copied.Count)"
                if ($installOk) { Add-Result 'Installer test build: silent install' 'PASS' $detail } else { Add-Result 'Installer test build: silent install' 'FAIL' $detail }

                $uninstaller = Join-Path $itApp 'unins000.exe'
                if (Test-Path $uninstaller) {
                    $uArgs = '/VERYSILENT /SUPPRESSMSGBOXES /LOG="' + (Join-Path $script:LogDir '52-installer-test-uninstall-log.txt') + '"'
                    Start-Process -FilePath $uninstaller -ArgumentList $uArgs -Wait | Out-Null
                    # The uninstaller hands over to a copy of itself and returns early: wait until the files are gone.
                    $deadline = (Get-Date).AddMinutes(3)
                    while ((Get-Date) -lt $deadline -and ((Test-Path (Join-Path $itApp 'Wisko.exe')) -or (Get-UninstallEntry $TestAppId))) { Start-Sleep -Seconds 2 }
                    $gone = -not (Test-Path (Join-Path $itApp 'Wisko.exe')) -and -not (Get-UninstallEntry $TestAppId)
                    $kept = 0
                    if (Test-Path $itProjects) { $kept = @(Get-ChildItem -Directory $itProjects).Count }
                    $detail = "program files removed: $gone; Apps entry removed: $(-not (Get-UninstallEntry $TestAppId)); example projects kept: $kept"
                    if ($gone -and $kept -eq $ExampleNames.Count) { Add-Result 'Installer test build: silent uninstall' 'PASS' $detail } else { Add-Result 'Installer test build: silent uninstall' 'FAIL' $detail }
                }
                else { Add-Result 'Installer test build: silent uninstall' 'FAIL' 'unins000.exe not found' }
            }
        }
        $realInstallAfter = Get-UninstallEntry $ReleaseAppId
        $same = (($null -eq $realInstallBefore) -and ($null -eq $realInstallAfter)) -or
                (($null -ne $realInstallBefore) -and ($null -ne $realInstallAfter) -and $realInstallBefore.Version -eq $realInstallAfter.Version -and $realInstallBefore.Location -eq $realInstallAfter.Location)
        if ($same) { Add-Result 'Installed Wisko unchanged' 'PASS' 'the release installation entry is the same as before' }
        else { Add-Result 'Installed Wisko unchanged' 'FAIL' 'the release installation entry changed; check Settings > Apps' }
    }
    else {
        Add-Result 'Installer smoke test' 'NOT RUN' 'optional: -InstallerSmokeTest (isolated test build); the real installer is tested by hand in a separate Windows user' $false
    }

    # --------------------------------------------------------------------------------------------------------------
    # 9. Checksums
    # --------------------------------------------------------------------------------------------------------------
    Write-Section '9. SHA-256 checksums'
    $sumLines = @()
    foreach ($file in Get-ChildItem -File $packagesDir | Sort-Object Name) {
        $hash = (Get-FileHash -Algorithm SHA256 $file.FullName).Hash.ToLowerInvariant()
        $original = Join-Path $dist $file.Name
        if ((Get-FileHash -Algorithm SHA256 $original).Hash.ToLowerInvariant() -ne $hash) { Add-Result "Checksum $($file.Name)" 'FAIL' 'the copy differs from dist\' }
        $sumLines += "$hash  $($file.Name)"
        Write-Host "$hash  $($file.Name)"
    }
    if ($sumLines.Count -gt 0) {
        # LF line endings and two spaces: the format of `shasum -a 256 -c` and `sha256sum -c`.
        [IO.File]::WriteAllText((Join-Path $packagesDir 'SHA256SUMS.txt'), (($sumLines -join "`n") + "`n"), $Utf8NoBom)
        Copy-Item (Join-Path $packagesDir 'SHA256SUMS.txt') (Join-Path $OutputRoot 'SHA256SUMS.txt')
        Add-Result 'Checksums' 'PASS' "$($sumLines.Count) file(s) in SHA256SUMS.txt"
    }
    else { Add-Result 'Checksums' 'FAIL' 'no release files to checksum' }
}
finally {
    # ------------------------------------------------------------------------------------------------------------------
    # 10. Summary
    # ------------------------------------------------------------------------------------------------------------------
    [void][WiskoValidation.Power]::SetThreadExecutionState([uint32]2147483648)   # ES_CONTINUOUS: back to normal
    try { [Console]::OutputEncoding = $originalOutputEncoding } catch { }

    $failed = @($script:Results | Where-Object { $_.Status -eq 'FAIL' })
    $notRun = @($script:Results | Where-Object { $_.Status -eq 'NOT RUN' -and $_.Required })
    $verdict = 'AUTOMATED CHECKS PASSED'
    if ($failed.Count -gt 0) { $verdict = "FAILED ($($failed.Count) failing step(s))" }
    elseif ($notRun.Count -gt 0) { $verdict = 'AUTOMATED CHECKS PASSED, BUT REQUIRED STEPS WERE NOT RUN: ' + (($notRun | ForEach-Object { $_.Step }) -join ', ') }
    if ($PrerequisitesOnly) { $verdict = 'PREREQUISITES ONLY: ' + $verdict }

    $md = New-Object System.Collections.Generic.List[string]
    $md.Add("# Wisko Engine $ExpectedVersion Windows validation: summary")
    $md.Add('')
    $md.Add("- Verdict: **$verdict**")
    $md.Add("- Commit: $ExpectedCommit")
    $md.Add("- Clone: $RepoPath")
    $md.Add("- Finished: $(Get-Date -Format 's'); script $ScriptVersion")
    $md.Add("- Options: PrerequisitesOnly=$PrerequisitesOnly RunGuiSmoke=$RunGuiSmoke GuiOnly=$GuiOnly InstallerSmokeTest=$InstallerSmokeTest KeepWork=$KeepWork")
    $md.Add('')
    $md.Add('| Step | Status | Detail |')
    $md.Add('|---|---|---|')
    foreach ($r in $script:Results) { $md.Add(('| {0} | {1} | {2} |' -f $r.Step, $r.Status, ($r.Detail -replace '\|', '/'))) }
    $md.Add('')
    $md.Add('The manual checklist (MANUAL-CHECKLIST.md) is still needed: this script cannot judge how things look, use the real mouse and keyboard, or run the release installer.')
    $summaryPath = Join-Path $OutputRoot 'SUMMARY.md'
    [IO.File]::WriteAllLines($summaryPath, $md, $Utf8NoBom)

    Write-Host ''
    Write-Host "==== $verdict ====" -ForegroundColor White
    foreach ($r in $script:Results) { Write-Host ('  [{0}] {1}: {2}' -f $r.Status, $r.Step, $r.Detail) }
    Write-Host ''
    Write-Host "Summary: $summaryPath"

    if ($transcriptStarted) { try { Stop-Transcript | Out-Null } catch { } }

    # Everything to bring back, except the large packages folder and the test work folders.
    try {
        $resultsZip = Join-Path (Split-Path $OutputRoot) ((Split-Path -Leaf $OutputRoot) + '-results.zip')
        if (-not (Test-Path $resultsZip)) {
            New-ResultsZip $OutputRoot $resultsZip @('packages', 'work', 'installer-test')
            Write-Host "Results to bring back: $resultsZip"
        }
    }
    catch { Write-Host "Could not write the results zip: $_" -ForegroundColor Yellow }

    if ($failed.Count -gt 0) { exit 1 }
}
exit 0
