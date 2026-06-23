[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $GameRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$win64 = Join-Path $GameRoot 'skyverse\Binaries\Win64'
$backupRoot = Join-Path $win64 '.everwind-trainer-backup'
$installMarker = Join-Path $win64 '.everwind-trainer-installed'

foreach ($name in @('dwmapi.dll', 'ue4ss')) {
    $installed = Join-Path $win64 $name
    if (Test-Path -LiteralPath $installed) {
        Remove-Item -LiteralPath $installed -Recurse -Force
    }

    $backup = Join-Path $backupRoot $name
    if (Test-Path -LiteralPath $backup) {
        Move-Item -LiteralPath $backup -Destination $installed
    }
}

if (Test-Path -LiteralPath $backupRoot) {
    Remove-Item -LiteralPath $backupRoot -Recurse -Force
}

if (Test-Path -LiteralPath $installMarker -PathType Leaf) {
    Remove-Item -LiteralPath $installMarker -Force
}

Write-Host "Removed Everwind Trainer runtime from '$win64'."
