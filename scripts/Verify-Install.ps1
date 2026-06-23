[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $GameRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$win64 = Join-Path $GameRoot 'skyverse\Binaries\Win64'
$required = @(
    'dwmapi.dll'
    'ue4ss\UE4SS.dll'
    'ue4ss\UE4SS-settings.ini'
    'ue4ss\UE4SS_Signatures\GUObjectArray.lua'
    'ue4ss\Mods\EverwindTrainer\Scripts\main.lua'
    'ue4ss\Mods\EverwindDiscovery\Scripts\main.lua'
)

$missing = foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $win64 $path) -PathType Leaf)) {
        $path
    }
}

if ($missing) {
    $missingText = $missing -join ', '
    throw "Installation is incomplete. Missing: $missingText"
}

Write-Host 'Everwind Trainer installation verified.'
