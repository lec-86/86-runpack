# ============================================================================
#  setver.ps1 - inject a VERSIONINFO resource into a freshly built exe.
#  Usage: powershell -NoProfile -ExecutionPolicy Bypass -File setver.ps1 <exe> <versioninfo.txt>
#  This file is intentionally PURE ASCII; all localized strings come from the
#  version info file (UTF-8). The legacy csc.exe shipped with .NET Framework
#  has no /win32versionresource option, so we build the VERSIONINFO binary
#  blob by hand and write it with kernel32 BeginUpdateResource/UpdateResource.
# ============================================================================
param([string]$Exe, [string]$VerFile)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Exe))     { Write-Host '[VER] exe not found, skipped'; exit 0 }
if (-not (Test-Path -LiteralPath $VerFile)) { Write-Host '[VER] version file not found, skipped'; exit 0 }

# ---------- read key=value pairs ----------
$map = @{}
foreach ($raw in [IO.File]::ReadAllLines($VerFile, [Text.Encoding]::UTF8)) {
    $s = $raw.Trim()
    if ($s -eq '' -or $s.StartsWith('#')) { continue }
    $i = $s.IndexOf('=')
    if ($i -le 0) { continue }
    $map[$s.Substring(0, $i).Trim()] = $s.Substring($i + 1)
}

function ParseVer([string]$v) {
    $p = @($v.Split('.'))
    while ($p.Count -lt 4) { $p += '0' }
    $n = @()
    foreach ($x in $p) { $iv = 0; [void][int]::TryParse($x.Trim(), [ref]$iv); $n += $iv }
    return $n
}
$fv = ParseVer $map['FILEVERSION']
$pv = ParseVer $map['PRODUCTVERSION']

# ---------- binary helpers ----------
function U16([int]$v)  { return [BitConverter]::GetBytes([uint16]$v) }
function U32([long]$v)  { $b = [BitConverter]::GetBytes([long]$v); return ,@($b[0], $b[1], $b[2], $b[3]) }
function W([string]$s) { return [Text.Encoding]::Unicode.GetBytes($s + [char]0) }
function Pad4([byte[]]$b) {
    $rem = $b.Length % 4
    if ($rem -eq 0) { return ,$b }
    $p = New-Object byte[] (4 - $rem)
    return ,($b + $p)
}
# szKey must be padded so that the member following the 6-byte header starts on a 4-byte boundary.
function PadKey([byte[]]$b) {
    $rem = ($b.Length + 6) % 4
    if ($rem -eq 0) { return ,$b }
    $p = New-Object byte[] (4 - $rem)
    return ,($b + $p)
}
# Build one VERSIONINFO structure: 6-byte header + aligned key + body, total padded to 4.
function MakeNode([int]$valueLen, [int]$type, [byte[]]$keyPadded, [byte[]]$body) {
    $core = 6 + $keyPadded.Length + $body.Length
    $total = $core
    $rem = $total % 4
    if ($rem -ne 0) { $total += 4 - $rem }
    $pad = New-Object byte[] ($total - $core)
    return @((U16 $total) + (U16 $valueLen) + (U16 $type) + $keyPadded + $body + $pad)
}

# ---------- string entries ----------
$stBody = New-Object byte[] 0
foreach ($k in @('CompanyName','FileDescription','FileVersion','InternalName',
                 'LegalCopyright','OriginalFilename','ProductName','ProductVersion')) {
    if (-not $map.ContainsKey($k)) { continue }
    $keyB = PadKey (W $k)
    $valB = Pad4 (W $map[$k])
    $stBody += MakeNode ($valB.Length / 2) 1 $keyB $valB
}

# ---------- StringFileInfo / 080404b0 ----------
$stkB  = PadKey (W '080404b0')
$stTbl = MakeNode 0 1 $stkB $stBody
$sfiB  = PadKey (W 'StringFileInfo')
$sfi   = MakeNode 0 1 $sfiB $stTbl

# ---------- VarFileInfo / Translation ----------
$trkB  = PadKey (W 'Translation')
$trk   = MakeNode 4 0 $trkB (U32 0x04B00804)
$vfiB  = PadKey (W 'VarFileInfo')
$vfi   = MakeNode 0 0 $vfiB $trk

# ---------- VS_FIXEDFILEINFO (52 bytes) ----------
$fvi = @()
$fvi += U32 0xFEEF04BD
$fvi += U32 0x00010000
$fvi += U32 (($fv[0] -shl 16) -bor $fv[1])
$fvi += U32 (($fv[2] -shl 16) -bor $fv[3])
$fvi += U32 (($pv[0] -shl 16) -bor $pv[1])
$fvi += U32 (($pv[2] -shl 16) -bor $pv[3])
$fvi += U32 0x3F
$fvi += U32 0
$fvi += U32 0x00040004
$fvi += U32 1
$fvi += U32 0
$fvi += U32 0
$fvi += U32 0

$rootKey = PadKey (W 'VS_VERSION_INFO')
$rootBody = @($fvi + $sfi + $vfi)
$blob = MakeNode 52 0 $rootKey $rootBody

# ---------- inject via kernel32 ----------
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ResUpdate {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr BeginUpdateResource(string pFileName, bool bDeleteExistingResources);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, uint cbData);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);
}
'@

# NOTE: bDeleteExistingResources must stay false, otherwise the manifest and
# icon resources embedded by csc would be wiped as well.
# Legacy csc emits an empty VERSIONINFO stub under language 0x0000; version.dll
# and .NET FileVersionInfo read that stub first, so we overwrite it with the
# real blob (UpdateResource replaces same type/name/language entries).
$h = [ResUpdate]::BeginUpdateResource($Exe, $false)
if ($h -eq [IntPtr]::Zero) { throw ('BeginUpdateResource failed, err=' + [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
try {
    # RT_VERSION = 16, ID = 1; inject for 0x0000 (neutral) and 0x0804 (zh-CN)
    foreach ($lang in @(0x0000, 0x0804)) {
        $ok = [ResUpdate]::UpdateResource($h, [IntPtr]16, [IntPtr]1, $lang, $blob, [uint32]$blob.Length)
        if (-not $ok) { throw ('UpdateResource lang=0x' + $lang.ToString('x4') + ' failed, err=' + [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
    }
    $ok2 = [ResUpdate]::EndUpdateResource($h, $false)
    if (-not $ok2) { throw ('EndUpdateResource failed, err=' + [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
}
catch {
    [void][ResUpdate]::EndUpdateResource($h, $true)
    throw
}

$vi = (Get-Item -LiteralPath $Exe).VersionInfo
Write-Host ('[VER] ProductName=' + $vi.ProductName + '  FileVersion=' + $vi.FileVersion)
