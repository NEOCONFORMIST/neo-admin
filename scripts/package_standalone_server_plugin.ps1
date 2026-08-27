[CmdletBinding()]
param(
    [string]$SourcePackage,
    [string]$Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ProjectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
if (-not $SourcePackage) {
    $developmentPackage = Join-Path $ProjectRoot `
        "server/.stage3v-filesystem-maps/dockerbuild/package/cs2"
    $releaseSourcePackage = Join-Path $ProjectRoot `
        "server/dockerbuild/package/cs2"
    $SourcePackage = if (Test-Path -LiteralPath $developmentPackage) {
        $developmentPackage
    }
    else {
        $releaseSourcePackage
    }
}
if (-not $Destination) {
    $Destination = Join-Path $ProjectRoot "dist/server-package/neo-admin"
}

$source = [System.IO.Path]::GetFullPath($SourcePackage)
$destination = [System.IO.Path]::GetFullPath($Destination)
$allowedRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $ProjectRoot "dist/server-package")).TrimEnd('\', '/') + `
    [System.IO.Path]::DirectorySeparatorChar
if (-not $destination.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace a package outside dist/server-package: $destination"
}

$legacyBinary = Join-Path $source "addons/cs2fixes/bin/linuxsteamrt64/cs2fixes.so"
if (-not (Test-Path -LiteralPath $legacyBinary -PathType Leaf)) {
    throw "The tested server binary is missing: $legacyBinary"
}

if (Test-Path -LiteralPath $destination) {
    Remove-Item -LiteralPath $destination -Recurse -Force
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $source -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $destination -Recurse -Force
}

$neoBinaryDirectory = Join-Path $destination "addons/neo_admin/bin/linuxsteamrt64"
New-Item -ItemType Directory -Path $neoBinaryDirectory -Force | Out-Null
Copy-Item -LiteralPath $legacyBinary `
    -Destination (Join-Path $neoBinaryDirectory "neo_admin.so") -Force

$packagedLegacyBinary = Join-Path $destination "addons/cs2fixes/bin/linuxsteamrt64/cs2fixes.so"
Remove-Item -LiteralPath $packagedLegacyBinary -Force
$legacyLoader = Join-Path $destination "addons/metamod/cs2fixes.vdf"
if (Test-Path -LiteralPath $legacyLoader) {
    Remove-Item -LiteralPath $legacyLoader -Force
}

$loader = @'
"Metamod Plugin"
{
    "alias"     "neo_admin"
    "file"      "addons/neo_admin/bin/linuxsteamrt64/neo_admin"
}
'@
[System.IO.File]::WriteAllText(
    (Join-Path $destination "addons/metamod/neo_admin.vdf"),
    ($loader.Trim() + "`n"),
    [System.Text.UTF8Encoding]::new($false))

$installNotes = @'
NEO ADMIN SERVER PLUGIN

1. Install Metamod:Source 2.x separately.
2. Copy the contents of this directory into game/csgo on the server.
3. Start or restart the CS2 server.

The plugin loads as addons/neo_admin/bin/linuxsteamrt64/neo_admin.so.
The addons/cs2fixes directory currently contains required compatibility data and
configuration inherited from the CS2Fixes core. It does not contain a second
plugin binary and must remain installed until that compatibility layer is fully
extracted.

This package does not bundle Metamod, CounterStrikeSharp, administrator accounts,
server logs, access keys, SQLite databases, or server-specific configuration.
'@
[System.IO.File]::WriteAllText(
    (Join-Path $destination "NEO-ADMIN-INSTALL.txt"),
    ($installNotes.Trim() + "`n"),
    [System.Text.UTF8Encoding]::new($false))

$required = @(
    "addons/neo_admin/bin/linuxsteamrt64/neo_admin.so",
    "addons/metamod/neo_admin.vdf",
    "addons/cs2fixes/gamedata/cs2fixes.jsonc"
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $destination $relative) -PathType Leaf)) {
        throw "Standalone package is missing required file: $relative"
    }
}

if (Test-Path -LiteralPath $packagedLegacyBinary -PathType Leaf) {
    throw "Standalone package still contains the legacy plugin binary"
}
if (Test-Path -LiteralPath $legacyLoader -PathType Leaf) {
    throw "Standalone package still contains the legacy Metamod loader"
}

$forbiddenNames = @(
    "neo_admin.sqlite3",
    "neo_admin_accounts.json",
    "neo_admin_game_admins.json",
    "neo_admin_audit.json",
    "neo_admin_bans.json",
    "neo_admin_discipline.json",
    "neo_admin_operations.json"
)
foreach ($file in Get-ChildItem -LiteralPath $destination -Recurse -File -Force) {
    if ($forbiddenNames -contains $file.Name -or $file.Extension -eq '.log') {
        throw "Server-specific state entered the standalone package: $($file.FullName)"
    }
}

Write-Host "Standalone NEO ADMIN server package created: $destination"
