<#
.SYNOPSIS
    Checks a rebuilt Assimp64.dll or nfd.dll before it may replace the shipped one (packaging\windows\NATIVE-LIBRARIES.md,
    checks 1 and 2).

.DESCRIPTION
    Reads the DLL's PE headers (read-only; no tools needed) and checks:
      - the architecture is x64;
      - it imports only Windows itself: the Universal C runtime (api-ms-win-crt-*, ucrtbase.dll) and Windows system
        DLLs. A Visual C++ runtime (MSVCP*, VCRUNTIME*), the old msvcrt.dll, a debug runtime or a MinGW runtime DLL
        (libgcc*, libstdc++*, libwinpthread*) fails the check, and so does any DLL not on the list below;
      - it exports every function Wisko calls (Assimp: the 14 that Engine\Assets\Importing\AssimpModelImporter.cs uses;
        nfd: the ones NativeFileDialogs.Net's Nfd.OpenDialog reaches, which is all Wisko uses);
      - with -Reference (the shipped DLL): it exports every C function the reference exports (Assimp: ai*; nfd: NFD_*).
        C++ names are not compared: they differ between compilers and Wisko does not use them.
    Exits with 0 when every check passes, 1 otherwise.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File packaging\windows\check-native-dll.ps1 -Path build\Assimp64.dll -Kind Assimp -Reference dist\Wisko\Assimp64.dll
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][ValidateSet('Assimp', 'Nfd')][string]$Kind,
    [string]$Reference
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Required = @{
    Assimp = @('aiGetVersionMajor', 'aiGetVersionMinor', 'aiGetVersionPatch', 'aiGetExtensionList', 'aiIsExtensionSupported',
        'aiImportFileEx', 'aiGetErrorString', 'aiReleaseImport', 'aiGetMaterialString', 'aiGetMaterialColor',
        'aiGetMaterialFloatArray', 'aiGetMaterialIntegerArray', 'aiGetMaterialTextureCount', 'aiGetMaterialTexture')
    Nfd = @('NFD_Init', 'NFD_OpenDialogU8', 'NFD_GetError', 'NFD_Quit', 'NFD_FreePathU8')
}
$CPrefix = @{ Assimp = 'ai'; Nfd = 'NFD_' }
# Windows system DLLs a rebuilt library may import. Anything else needs a reason and an update of this list.
$WindowsDlls = @('kernel32.dll', 'user32.dll', 'gdi32.dll', 'shell32.dll', 'ole32.dll', 'oleaut32.dll', 'advapi32.dll',
    'shlwapi.dll', 'comctl32.dll', 'comdlg32.dll', 'ucrtbase.dll')

function Read-PeFile([string]$File) {
    $b = [IO.File]::ReadAllBytes((Resolve-Path $File))
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    $sections = [BitConverter]::ToUInt16($b, $pe + 6)
    $optionalSize = [BitConverter]::ToUInt16($b, $pe + 20)
    $optional = $pe + 24
    $directories = if ([BitConverter]::ToUInt16($b, $optional) -eq 0x20b) { $optional + 112 } else { $optional + 96 }
    $sectionTable = $optional + $optionalSize
    $toOffset = {
        param([uint32]$Rva)
        for ($i = 0; $i -lt $sections; $i++) {
            $s = $sectionTable + 40 * $i
            $va = [BitConverter]::ToUInt32($b, $s + 12)
            $size = [Math]::Max([BitConverter]::ToUInt32($b, $s + 8), [BitConverter]::ToUInt32($b, $s + 16))
            if ($Rva -ge $va -and $Rva -lt $va + $size) { return [int]($Rva - $va + [BitConverter]::ToUInt32($b, $s + 20)) }
        }
        throw "RVA 0x{0:X} is outside every section of $File" -f $Rva
    }
    $readString = {
        param([int]$Offset)
        $end = $Offset
        while ($b[$end] -ne 0) { $end++ }
        [Text.Encoding]::ASCII.GetString($b, $Offset, $end - $Offset)
    }

    $imports = New-Object System.Collections.Generic.List[string]
    $importRva = [BitConverter]::ToUInt32($b, $directories + 8)
    if ($importRva) {
        $o = & $toOffset $importRva
        while (($nameRva = [BitConverter]::ToUInt32($b, $o + 12)) -ne 0) {
            $imports.Add((& $readString (& $toOffset $nameRva)))
            $o += 20
        }
    }

    $exports = New-Object System.Collections.Generic.List[string]
    $exportRva = [BitConverter]::ToUInt32($b, $directories)
    if ($exportRva) {
        $o = & $toOffset $exportRva
        $count = [BitConverter]::ToUInt32($b, $o + 24)
        $names = & $toOffset ([BitConverter]::ToUInt32($b, $o + 32))
        for ($i = 0; $i -lt $count; $i++) {
            $exports.Add((& $readString (& $toOffset ([BitConverter]::ToUInt32($b, $names + 4 * $i)))))
        }
    }

    [pscustomobject]@{ Machine = [BitConverter]::ToUInt16($b, $pe + 4); Imports = $imports; Exports = $exports }
}

$failures = 0
function Report([bool]$Ok, [string]$Text) {
    if ($Ok) { Write-Host "  PASS  $Text" } else { Write-Host "  FAIL  $Text"; $script:failures++ }
}

$dll = Read-PeFile $Path
Write-Host "$Path ($Kind)"
Report ($dll.Machine -eq 0x8664) ('architecture x64 (machine 0x{0:X4})' -f $dll.Machine)

foreach ($import in $dll.Imports) {
    $name = $import.ToLowerInvariant()
    $ok = $name.StartsWith('api-ms-win-crt-') -or $WindowsDlls -contains $name
    Report $ok "imports $import$(if (-not $ok) { ' (not part of Windows, or not on the allowed list)' })"
}

$missing = @($Required[$Kind] | Where-Object { $dll.Exports -notcontains $_ })
Report ($missing.Count -eq 0) ("exports the {0} functions Wisko calls{1}" -f $Required[$Kind].Count,
    $(if ($missing.Count) { ': missing ' + ($missing -join ', ') }))

if ($Reference) {
    $referenceDll = Read-PeFile $Reference
    $prefix = $CPrefix[$Kind]
    $referenceC = @($referenceDll.Exports | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) })
    $missingC = @($referenceC | Where-Object { $dll.Exports -notcontains $_ })
    $extraC = @($dll.Exports | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) -and $referenceC -notcontains $_ })
    Report ($referenceC.Count -gt 0) ("the reference exports {0} {1}* functions" -f $referenceC.Count, $prefix)
    Report ($missingC.Count -eq 0) ("exports all {0} {1}* functions of {2}{3}" -f $referenceC.Count, $prefix, $Reference,
        $(if ($missingC.Count) { ': missing ' + ($missingC -join ', ') }))
    if ($extraC.Count) { Write-Host "  NOTE  $($extraC.Count) $prefix* functions the reference does not export: $($extraC -join ', ')" }
}

if ($failures) { Write-Host "$failures check(s) failed."; exit 1 }
Write-Host 'All checks passed.'
exit 0
