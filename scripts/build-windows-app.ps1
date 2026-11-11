<#
.SYNOPSIS
    Builds the Windows desktop app: dist\Wisko\Wisko.exe (x64, self-contained .NET 10, a normal folder).

.DESCRIPTION
    Publishes the editor (Game\Wisko.Editor.csproj) for win-x64 with the .NET runtime included, so the folder runs
    without an installed .NET and without this repository. Wisko.exe carries the Wisko icon and uses the Windows GUI
    subsystem (no console window; output goes to %LOCALAPPDATA%\Wisko\Logs\Wisko.log). Needs only the .NET SDK.

    The app folder carries its licenses: LICENSE.txt, NOTICE.txt, THIRD-PARTY-NOTICES.md and BRANDING.md from this
    repository, and licenses\dotnet\ (the license and third-party notices of the bundled .NET runtime, copied from the
    runtime pack the publish used).

    -Install also copies the app to %LOCALAPPDATA%\Programs\Wisko (per user, no administrator rights) and creates or
    updates the "Wisko" shortcut on the desktop. Projects, history and logs live outside the app folder and are never
    touched. Only this script's own output is ever replaced.

    -Package also makes the release files in dist\: the example projects (made by the test runner, with their models'
    credits), the portable zip (Wisko-<version>-win-x64-portable.zip) and, when Inno Setup 6 is installed, the
    installer (Wisko-<version>-win-x64-setup.exe, from packaging\windows\Wisko.iss). -InnoCompiler names ISCC.exe when
    it is not in a standard place. See packaging\windows\README.md.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build-windows-app.ps1 -Install

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build-windows-app.ps1 -Package
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Package,
    [string]$InnoCompiler
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'Game\Wisko.Editor.csproj'
$Rid = 'win-x64'
$Executable = 'Wisko.exe'
$Dist = Join-Path $Root 'dist'
$FinalApp = Join-Path $Dist 'Wisko'
$InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\Wisko'

function Fail([string]$Message) {
    [Console]::Error.WriteLine("error: $Message")
    exit 1
}

# Replaces $To with a copy of $From. $To must not exist or must be an earlier copy of this app (it holds Wisko.exe),
# and Wisko must not be running from it.
function Copy-App([string]$From, [string]$To) {
    if (Test-Path $To) {
        if (-not (Test-Path (Join-Path $To $Executable))) {
            Fail "$To exists but is not a Wisko app folder; not replacing it"
        }
        $running = @(Get-Process -Name Wisko -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith("$To\", [StringComparison]::OrdinalIgnoreCase) })
        if ($running.Count -gt 0) {
            Fail "Wisko is running from $To; close it and run the script again"
        }
        Remove-Item -Recurse -Force $To
    }
    Copy-Item -Recurse $From $To
}

if ($env:OS -ne 'Windows_NT') { Fail 'build on Windows' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'missing required tool: dotnet (.NET SDK)' }

$Version = (& dotnet msbuild $Project -getProperty:Version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not $Version) { Fail "could not read <Version> from $Project" }
Write-Host "==> Wisko $Version ($Rid)"

# Fail before building rather than after: an existing shortcut is only ever updated if it already opens this app.
$Shell = New-Object -ComObject WScript.Shell
$ShortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Wisko.lnk'
$InstalledExe = Join-Path $InstallDir $Executable
if ($Install -and (Test-Path $ShortcutPath)) {
    $existingTarget = $Shell.CreateShortcut($ShortcutPath).TargetPath
    if ($existingTarget -ne $InstalledExe) {
        Fail "$ShortcutPath already exists and opens '$existingTarget'; not replacing it"
    }
}

$Work = Join-Path ([IO.Path]::GetTempPath()) ('wisko-app-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$Staged = Join-Path $Work 'Wisko'
try {
    Write-Host "==> dotnet publish (Release, self-contained, $Rid)"
    # WiskoWindowsApp: Wisko.exe, GUI subsystem (see Game\Wisko.Editor.csproj).
    & dotnet publish $Project -c Release -r $Rid --self-contained true `
        -p:WiskoWindowsApp=true -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishTrimmed=false `
        -o $Staged
    if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish failed' }

    Write-Host '==> Validate'
    # The app, the bundled .NET host and runtime, and the native libraries: windowing (GLFW), Lua, model import
    # (Assimp) and file dialogs (NFD).
    foreach ($required in $Executable, 'Wisko.dll', 'Wisko.Engine.dll', 'Wisko.runtimeconfig.json', 'Wisko.deps.json',
                          'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll',
                          'glfw3.dll', 'lua54.dll', 'Assimp64.dll', 'nfd.dll') {
        if (-not (Test-Path (Join-Path $Staged $required))) { Fail "publish output is missing $required" }
    }

    $runtimeConfig = Get-Content (Join-Path $Staged 'Wisko.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (-not $runtimeConfig.runtimeOptions.PSObject.Properties['includedFrameworks']) {
        Fail 'Wisko.runtimeconfig.json does not include the .NET runtime (not self-contained)'
    }
    $RuntimeVersion = $runtimeConfig.runtimeOptions.includedFrameworks[0].version

    $exeBytes = [IO.File]::ReadAllBytes((Join-Path $Staged $Executable))
    $peHeader = [BitConverter]::ToInt32($exeBytes, 0x3C)
    if ([BitConverter]::ToUInt16($exeBytes, $peHeader + 4) -ne 0x8664) { Fail "$Executable is not x64" }
    # IMAGE_OPTIONAL_HEADER.Subsystem (offset 68): 2 = Windows GUI (no console window), 3 = console.
    if ([BitConverter]::ToUInt16($exeBytes, $peHeader + 24 + 68) -ne 2) { Fail "$Executable is not a GUI-subsystem executable" }
    if ([Text.Encoding]::ASCII.GetString($exeBytes) -match 'requireAdministrator|highestAvailable') {
        Fail "$Executable asks for administrator rights"
    }

    Add-Type -Namespace WiskoBuild -Name Shell32 -MemberDefinition @'
[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
public static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
'@
    if ([WiskoBuild.Shell32]::ExtractIconEx((Join-Path $Staged $Executable), -1, $null, $null, 0) -lt 1) {
        Fail "$Executable has no icon"
    }

    # The runtime's configuration files must not point at this machine (absolute paths).
    foreach ($config in Get-ChildItem $Staged -Filter *.json) {
        if ((Get-Content $config.FullName -Raw) -match '[A-Za-z]:(\\\\|/)') {
            Fail "$($config.Name) contains an absolute path"
        }
    }

    Write-Host '==> Licenses and notices'
    Copy-Item (Join-Path $Root 'LICENSE') (Join-Path $Staged 'LICENSE.txt')
    Copy-Item (Join-Path $Root 'NOTICE') (Join-Path $Staged 'NOTICE.txt')
    Copy-Item (Join-Path $Root 'THIRD-PARTY-NOTICES.md') $Staged
    Copy-Item (Join-Path $Root 'BRANDING.md') $Staged
    # The bundled runtime's own license and notices, from the runtime pack this publish used (same version).
    $packagesListing = (& dotnet nuget locals global-packages --list | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $packagesListing -notmatch 'global-packages:\s*(.+)$') { Fail 'could not find the NuGet packages folder' }
    $runtimePack = Join-Path $Matches[1].Trim() "microsoft.netcore.app.runtime.$Rid\$RuntimeVersion"
    $dotnetLicenses = Join-Path $Staged 'licenses\dotnet'
    New-Item -ItemType Directory -Force $dotnetLicenses | Out-Null
    foreach ($file in 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') {
        $source = Join-Path $runtimePack $file
        if (-not (Test-Path $source)) { Fail ".NET runtime pack $RuntimeVersion has no $file ($runtimePack)" }
        Copy-Item $source $dotnetLicenses
    }

    New-Item -ItemType Directory -Force $Dist | Out-Null
    Copy-App $Staged $FinalApp
}
finally {
    if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }
}

if ($Install) {
    Write-Host "==> Install for this user: $InstallDir"
    New-Item -ItemType Directory -Force (Split-Path $InstallDir) | Out-Null
    Copy-App $FinalApp $InstallDir

    $shortcut = $Shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $InstalledExe
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.IconLocation = "$InstalledExe,0"
    $shortcut.Description = 'Wisko game engine editor'
    $shortcut.Save()
    Write-Host "==> Desktop shortcut: $ShortcutPath"
}

if ($Package) {
    $PackageWork = Join-Path ([IO.Path]::GetTempPath()) ('wisko-package-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $ExampleProjects = Join-Path $PackageWork 'Projects'
    New-Item -ItemType Directory -Force $ExampleProjects | Out-Null
    try {
        Write-Host '==> Example projects (made by the test runner)'
        $tests = Join-Path $Root 'Tests\Wisko.Tests\Wisko.Tests.csproj'
        & dotnet build $tests -c Release -nologo -v q
        if ($LASTEXITCODE -ne 0) { Fail 'building the test runner (it makes the example projects) failed' }
        $runner = (& dotnet msbuild $tests -p:Configuration=Release -getProperty:TargetPath | Out-String).Trim()
        foreach ($maker in '--make-demo', '--make-v5-demo', '--make-anim-demo', '--make-graphics-demo', '--make-release-demo') {
            & dotnet $runner $maker $ExampleProjects
            if ($LASTEXITCODE -ne 0) { Fail "the test runner could not make an example project ($maker)" }
        }

        # Each example's models travel with their credits and licences (CC BY 4.0 requires the attribution).
        $animationModels = Join-Path $Root 'examples\animation-demo\Models'
        $graphicsModels = Join-Path $Root 'examples\graphics-demo\Models'
        $credits = @{
            'Animation Demo'            = @(@{ From = $animationModels; Name = 'CREDITS.md' })
            'Gameplay Systems Showcase' = @(@{ From = $animationModels; Name = 'CREDITS.md' })
            'Graphics Showcase'         = @(@{ From = $graphicsModels; Name = 'CREDITS.md' }, @{ From = $animationModels; Name = 'CREDITS-animation-models.md' })
        }
        foreach ($project in Get-ChildItem -Directory $ExampleProjects) {
            $models = Join-Path $project.FullName 'Assets\Models'
            if (-not (Test-Path $models)) { continue }
            if (-not $credits.ContainsKey($project.Name)) { Fail "example project '$($project.Name)' has models but no credits are known for it" }
            foreach ($credit in $credits[$project.Name]) { Copy-Item (Join-Path $credit.From 'CREDITS.md') (Join-Path $models $credit.Name) }
            if (Test-Path (Join-Path $models 'animation_with_skeleton')) {
                Copy-Item (Join-Path $animationModels 'AnimationWithSkeletonFbx\LICENSE-assimp.txt') (Join-Path $models 'animation_with_skeleton')
            }
            if (Test-Path (Join-Path $models 'Fox')) { Copy-Item (Join-Path $animationModels 'Fox\LICENSE.md') (Join-Path $models 'Fox') }
        }

        $ExamplesOut = Join-Path $Dist 'Examples'
        if (Test-Path $ExamplesOut) { Remove-Item -Recurse -Force $ExamplesOut }
        Copy-Item -Recurse $ExampleProjects $ExamplesOut

        Write-Host '==> Portable zip'
        # The app folder with the example projects in Examples\ (copied into the projects folder by hand).
        $zipRoot = Join-Path $PackageWork 'zip\Wisko'
        New-Item -ItemType Directory -Force (Split-Path $zipRoot) | Out-Null
        Copy-Item -Recurse $FinalApp $zipRoot
        Copy-Item -Recurse $ExampleProjects (Join-Path $zipRoot 'Examples')
        $PortableZip = Join-Path $Dist "Wisko-$Version-win-x64-portable.zip"
        if (Test-Path $PortableZip) { Remove-Item -Force $PortableZip }
        # Entries are written one by one: Windows PowerShell's ZipFile.CreateFromDirectory stores backslashes, but zip
        # entry names use forward slashes.
        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
        $zipBase = Split-Path $zipRoot
        $zipStream = [IO.File]::Open($PortableZip, [IO.FileMode]::CreateNew)
        $archive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in Get-ChildItem -Recurse -File $zipRoot) {
                $entryName = $file.FullName.Substring($zipBase.Length + 1).Replace('\', '/')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally {
            $archive.Dispose()
            $zipStream.Dispose()
        }

        Write-Host '==> Installer (Inno Setup 6)'
        $iscc = $null
        if ($InnoCompiler) {
            if (-not (Test-Path $InnoCompiler)) { Fail "-InnoCompiler: $InnoCompiler does not exist" }
            $iscc = $InnoCompiler
        } else {
            $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                      "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
        }

        $Installer = Join-Path $Dist "Wisko-$Version-win-x64-setup.exe"
        if ($iscc) {
            if (Test-Path $Installer) { Remove-Item -Force $Installer }
            & $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$FinalApp" "/DExamplesDir=$ExamplesOut" "/DOutputDir=$Dist" `
                "/DIconFile=$(Join-Path $Root 'branding\AppIcon\Wisko-AppIcon.ico')" `
                "/DWizardImages=$(Join-Path $Root 'branding\Installer\Wisko-WizardImage-*.png')" `
                "/DWizardSmallImages=$(Join-Path $Root 'branding\Installer\Wisko-WizardSmallImage-*.png')" `
                (Join-Path $Root 'packaging\windows\Wisko.iss')
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path $Installer)) { Fail 'Inno Setup could not build the installer' }
        } else {
            $Installer = $null
            Write-Warning 'Inno Setup 6 (ISCC.exe) was not found: the installer was not built (the portable zip was).'
        }
    }
    finally {
        if (Test-Path $PackageWork) { Remove-Item -Recurse -Force $PackageWork }
    }
}

$files = @(Get-ChildItem -Recurse -File $FinalApp)
$megabytes = ($files | Measure-Object Length -Sum).Sum / 1MB
Write-Host ''
Write-Host "Built: dist\Wisko\$Executable"
Write-Host ('  version {0}, .NET {1} included, {2} files, {3:N0} MB' -f $Version, $RuntimeVersion, $files.Count, $megabytes)
if ($Install) {
    Write-Host "Installed: $InstalledExe (desktop shortcut: Wisko)"
}
if ($Package) {
    Write-Host ('Example projects: dist\Examples ({0})' -f ((Get-ChildItem -Directory $ExamplesOut | ForEach-Object Name) -join ', '))
    Write-Host ('Portable zip: dist\{0} ({1:N0} MB)' -f (Split-Path -Leaf $PortableZip), ((Get-Item $PortableZip).Length / 1MB))
    if ($Installer) { Write-Host ('Installer: dist\{0} ({1:N0} MB)' -f (Split-Path -Leaf $Installer), ((Get-Item $Installer).Length / 1MB)) }
}
