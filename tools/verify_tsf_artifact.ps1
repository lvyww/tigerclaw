param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][ValidateSet('Win32', 'x64', 'ARM64')][string]$Architecture,
    [string]$Destination
)
$ErrorActionPreference = 'Stop'
function Assert-PeArchitecture([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { throw "Invalid DOS header: $Path" }
        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt $stream.Length - 24) { throw "Invalid PE offset: $Path" }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE signature: $Path" }
        $machine = $reader.ReadUInt16()
        $expected = @{ Win32 = 0x14C; x64 = 0x8664; ARM64 = 0xAA64 }[$Architecture]
        if ($machine -ne $expected) { throw ('Wrong PE architecture in {0}: 0x{1:X4}, expected {2}' -f $Path, $machine, $Architecture) }
        $stream.Position = $offset + 22
        if (($reader.ReadUInt16() -band 0x2000) -eq 0) { throw "Not a DLL: $Path" }
    } finally { $reader.Dispose() }
}
Assert-PeArchitecture $Source
if ($Destination) {
    Assert-PeArchitecture $Destination
    if ((Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash) {
        throw "Copied DLL differs from build artifact: $Destination"
    }
}
Write-Host "Verified TSF $Architecture : $Source"
