[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $GameRoot,

    [switch] $EnableDiscovery,

    [switch] $AllowUnstableInjection
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $AllowUnstableInjection) {
    throw @"
The UE4SS injection installer is disabled by default because it caused Everwind
to crash on launch during testing. The current supported path is the external
trainer/probe, which does not copy files into the game install.

If you intentionally want to retry this unstable path, rerun with:
  -AllowUnstableInjection
"@
}

$releaseName = 'UE4SS_v3.0.1-971-g9ec5ece7.zip'
$releaseUrl = "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/experimental-latest/$releaseName"
$releaseSha256 = '476D6D38627B0905723288D95AB7ACB5FCD2834879455684B9DEF47A6007B8D5'
$scriptRoot = Split-Path -Parent $PSCommandPath
$repositoryRoot = Split-Path -Parent $scriptRoot
$win64 = Join-Path $GameRoot 'skyverse\Binaries\Win64'
$shippingExe = Join-Path $win64 'Everwind-Win64-Shipping.exe'

if (-not (Test-Path -LiteralPath $shippingExe -PathType Leaf)) {
    throw "Everwind executable not found at '$shippingExe'. Pass the directory containing Engine and skyverse."
}

$cacheRoot = Join-Path $env:LOCALAPPDATA 'EverwindTrainer\cache'
$archive = Join-Path $cacheRoot $releaseName
$expanded = Join-Path $cacheRoot ([IO.Path]::GetFileNameWithoutExtension($releaseName))
New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null

if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    Write-Host "Downloading UE4SS from its official GitHub release..."
    Invoke-WebRequest -Uri $releaseUrl -OutFile $archive -Headers @{ 'User-Agent' = 'EverwindTrainer' }
}

$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($actualHash -ne $releaseSha256) {
    throw "UE4SS archive checksum mismatch. Expected $releaseSha256, got $actualHash."
}

if (Test-Path -LiteralPath $expanded) {
    Remove-Item -LiteralPath $expanded -Recurse -Force
}
Expand-Archive -LiteralPath $archive -DestinationPath $expanded

$backupRoot = Join-Path $win64 '.everwind-trainer-backup'
$installMarker = Join-Path $win64 '.everwind-trainer-installed'
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
foreach ($name in @('dwmapi.dll', 'ue4ss')) {
    $destination = Join-Path $win64 $name
    $backup = Join-Path $backupRoot $name
    if ((Test-Path -LiteralPath $destination) -and
        -not (Test-Path -LiteralPath $installMarker) -and
        -not (Test-Path -LiteralPath $backup)) {
        Copy-Item -LiteralPath $destination -Destination $backup -Recurse
    }
}

Copy-Item -LiteralPath (Join-Path $expanded 'dwmapi.dll') -Destination $win64 -Force
$ue4ssDestination = Join-Path $win64 'ue4ss'
New-Item -ItemType Directory -Force -Path $ue4ssDestination | Out-Null
Copy-Item -Path (Join-Path $expanded 'ue4ss\*') -Destination $ue4ssDestination -Recurse -Force

$runtimeSource = Join-Path $repositoryRoot 'runtime'
if (Test-Path -LiteralPath $runtimeSource -PathType Container) {
    Copy-Item -Path (Join-Path $runtimeSource '*') -Destination $ue4ssDestination -Recurse -Force
}

$modsDestination = Join-Path $ue4ssDestination 'Mods'
foreach ($modName in @('EverwindTrainer', 'EverwindDiscovery')) {
    $source = Join-Path $repositoryRoot "mod\$modName"
    $destination = Join-Path $modsDestination $modName
    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    Copy-Item -LiteralPath $source -Destination $destination -Recurse

    $upperScripts = Join-Path $destination 'Scripts'
    $lowerScripts = Join-Path $destination 'scripts'
    if (Test-Path -LiteralPath $upperScripts -PathType Container) {
        $temporaryScripts = Join-Path $destination '__scripts_tmp__'
        if (Test-Path -LiteralPath $temporaryScripts) {
            Remove-Item -LiteralPath $temporaryScripts -Recurse -Force
        }
        Move-Item -LiteralPath $upperScripts -Destination $temporaryScripts
        Move-Item -LiteralPath $temporaryScripts -Destination $lowerScripts
    }
}

$mods = @(
    [ordered]@{ mod_name = 'EverwindTrainer'; mod_enabled = $true },
    [ordered]@{ mod_name = 'EverwindDiscovery'; mod_enabled = [bool] $EnableDiscovery }
)
$utf8NoBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText(
    (Join-Path $modsDestination 'mods.json'),
    ($mods | ConvertTo-Json),
    $utf8NoBom
)
@(
    'EverwindTrainer:1'
    "EverwindDiscovery:$([int][bool]$EnableDiscovery)"
) | Set-Content -LiteralPath (Join-Path $modsDestination 'mods.txt') -Encoding ascii

foreach ($mod in $mods) {
    $enabledPath = Join-Path (Join-Path $modsDestination $mod.mod_name) 'enabled.txt'
    if ($mod.mod_enabled) {
        [IO.File]::WriteAllText($enabledPath, '', $utf8NoBom)
    }
    elseif (Test-Path -LiteralPath $enabledPath) {
        Remove-Item -LiteralPath $enabledPath -Force
    }
}

$settingsPath = Join-Path $ue4ssDestination 'UE4SS-settings.ini'
$settings = Get-Content -LiteralPath $settingsPath -Raw
$settings = $settings -replace 'SecondsToScanBeforeGivingUp\s*=\s*\d+', 'SecondsToScanBeforeGivingUp = 30'
$settings = $settings -replace 'ConsoleEnabled\s*=\s*\d+', 'ConsoleEnabled = 0'
$settings = $settings -replace 'GuiConsoleEnabled\s*=\s*\d+', 'GuiConsoleEnabled = 0'
$settings = $settings -replace 'GuiConsoleVisible\s*=\s*\d+', 'GuiConsoleVisible = 0'
[IO.File]::WriteAllText($settingsPath, $settings, $utf8NoBom)
[IO.File]::WriteAllText($installMarker, "UE4SS_v3.0.1-971-g9ec5ece7`n", $utf8NoBom)

Write-Host "Installed Everwind Trainer runtime to '$win64'."
Write-Host "Discovery enabled: $([bool] $EnableDiscovery)"
