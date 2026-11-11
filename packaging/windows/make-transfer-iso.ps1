<#
.SYNOPSIS
    Packs a folder into an ISO image, to hand test builds to a clean virtual machine as a virtual CD
    (packaging\windows\CLEAN-MACHINE-TESTING.md).

.DESCRIPTION
    Uses Windows' own image mastering API (IMAPI2), so nothing needs to be installed and the virtual machine needs no
    guest tools. The image has ISO 9660, Joliet and UDF file systems. Never overwrites an existing file.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File packaging\windows\make-transfer-iso.ps1 -Source D:\WiskoVM\transfer -Output D:\WiskoVM\wisko-transfer.iso
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Output,
    [string]$Label = 'WISKO_TEST'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Source = (Resolve-Path $Source).Path
if (-not (Test-Path $Source -PathType Container)) { throw "$Source is not a folder" }
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path $Output) { throw "$Output already exists" }

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices.ComTypes;

public static class WiskoIsoWriter
{
    public static void Save(object image, string path, int blockSize, int blocks)
    {
        var stream = (IStream)image;
        var buffer = new byte[blockSize];
        using (var file = new FileStream(path, FileMode.CreateNew))
        {
            for (var i = 0; i < blocks; i++)
            {
                stream.Read(buffer, blockSize, IntPtr.Zero);
                file.Write(buffer, 0, blockSize);
            }
        }
    }
}
'@

$image = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
$image.ChooseImageDefaultsForMediaType(13)   # IMAPI_MEDIA_TYPE_DVDPLUSRW_DUALLAYER: room for 8.5 GB
$image.FileSystemsToCreate = 7               # ISO 9660 + Joliet + UDF
$image.VolumeName = $Label
$image.Root.AddTree($Source, $false)         # the folder's contents, not the folder itself
$result = $image.CreateResultImage()
[WiskoIsoWriter]::Save($result.ImageStream, $Output, $result.BlockSize, $result.TotalBlocks)
Write-Host ("wrote {0} ({1:N0} bytes) from {2}" -f $Output, (Get-Item $Output).Length, $Source)
