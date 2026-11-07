<#
.SYNOPSIS
  Builds one mod's release zip, <id>-<version>.zip, and its release notes, into out/.
.DESCRIPTION
  The zip holds exactly what goes into a LocalExile's Mods/Binaries/<id>/ (AGENTS.md, "Releases"):
  - a project mod: its build output, <id>.dll with mod.json, settings.json and the other files it copies, without the
    server's own assemblies (the Gateway has them) or .pdb files;
  - a script mod: its folder, without README.md.
  A project mod builds against GameServer.Modding of the LocalExile checkout in -LocalExileRoot (by default LocalExile/
  beside this repository); the notes name that checkout's commit. The build goes to out/, never into that checkout's
  Mods/Binaries/, so a running Gateway there is not disturbed.
  -Tag (<id>-v<version>, as the release workflow passes it) fails when the mod's mod.json has another version.
  Run from the repository root: tools/build-release.ps1 itemquantity
#>
param(
    [Parameter(Position = 0)][string]$Id,
    [string]$Tag,
    [string]$LocalExileRoot,
    [string]$OutFolder
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $LocalExileRoot) { $LocalExileRoot = Join-Path (Split-Path $root -Parent) 'LocalExile' }
if (-not $OutFolder) { $OutFolder = Join-Path $root 'out' }
$LocalExileRoot = (Resolve-Path -LiteralPath $LocalExileRoot).Path
New-Item -ItemType Directory -Force -Path $OutFolder | Out-Null
$OutFolder = (Resolve-Path -LiteralPath $OutFolder).Path

$tagVersion = $null
if ($Tag) {
    if ($Tag -notmatch '\A([a-z0-9][a-z0-9_.-]*)-v(\d+\.\d+\.\d+)\z') { throw "The tag $Tag is not <id>-v<major>.<minor>.<patch>." }
    if ($Id -and $Id -ne $Matches[1]) { throw "The tag $Tag is not for the mod $Id." }
    $Id = $Matches[1]
    $tagVersion = $Matches[2]
}
if (-not $Id) { throw 'Name the mod: tools/build-release.ps1 <id>, or -Tag <id>-v<version>.' }

$folder = Join-Path (Join-Path $root 'mods') $Id
if (-not (Test-Path -LiteralPath (Join-Path $folder 'mod.json'))) { throw "There is no mod $Id (mods/$Id/mod.json)." }
$manifest = Get-Content -LiteralPath (Join-Path $folder 'mod.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$version = [string]$manifest.version
if ($manifest.id -ne $Id) { throw "mods/$Id/mod.json says its id is $($manifest.id)." }
if ($version -notmatch '\A\d+\.\d+\.\d+\z') { throw "mods/$Id/mod.json has the version '$version', not <major>.<minor>.<patch>." }
if ($tagVersion -and $tagVersion -ne $version) { throw "The tag $Tag does not match mods/$Id/mod.json, which is version ${version}: bump it, or tag $Id-v$version." }

$name = "$Id-$version"
$stage = Join-Path $OutFolder $name
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

$projects = @(Get-ChildItem -LiteralPath $folder -Filter '*.csproj' -File)
$built = $null
if ($projects.Count -gt 1) { throw "mods/$Id has more than one project." }
if ($projects.Count -eq 1) {
    if (-not (Test-Path -LiteralPath (Join-Path $LocalExileRoot 'src/GameServer.Modding/GameServer.Modding.csproj'))) {
        throw "$LocalExileRoot is not a LocalExile checkout (no src/GameServer.Modding); pass -LocalExileRoot."
    }
    $output = Join-Path $OutFolder "build/$Id"
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    # OutputPath on the command line wins over mods/Directory.Build.props, which builds into the checkout's Mods/Binaries/.
    & dotnet build $projects[0].FullName -c Release -nologo -v:minimal "-p:LocalExileRoot=$LocalExileRoot" "-p:OutputPath=$output/"
    if ($LASTEXITCODE -ne 0) { throw "The build of $Id failed." }
    if (-not (Test-Path -LiteralPath (Join-Path $output "$Id.dll"))) { throw "The build of $Id made no $Id.dll." }
    # The server's own assemblies are referenced, not shipped: every mod uses the Gateway's.
    Get-ChildItem -LiteralPath $output -File |
        Where-Object { $_.Extension -ne '.pdb' -and $_.Name -notlike 'GameServer.*' } |
        Copy-Item -Destination $stage
    $built = 'project'
} else {
    if (-not @(Get-ChildItem -LiteralPath $folder -Filter '*.cs' -File).Count) { throw "mods/$Id has no scripts and no project." }
    Get-ChildItem -LiteralPath $folder -Force | Where-Object { $_.Name -notin 'README.md', 'bin', 'obj' } |
        Copy-Item -Destination $stage -Recurse
    $built = 'script'
}

# The zip, written entry by entry so its paths use '/' on every system.
$zip = Join-Path $OutFolder "$name.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName) {
        $entry = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }

# The release notes: what the mod is, and what it was built against.
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("$($manifest.name) ${version}: $($manifest.description)")
$lines.Add('')
$lines.Add("Install it with Waypoint's Browse panel, or extract ``$name.zip`` into ``Mods/Binaries/$Id/`` of your LocalExile.")
if ($built -eq 'project') {
    $commit = (& git -C $LocalExileRoot rev-parse HEAD).Trim()
    $describe = (& git -C $LocalExileRoot describe --tags --always 2>$null)
    $lines.Add('')
    $lines.Add("Built against LocalExile ``$describe`` ([$($commit.Substring(0, 8))](https://github.com/LocalExile/LocalExile/commit/$commit)): it works with that version and later ones until the mod API changes.")
}
$notes = Join-Path $OutFolder "$name.md"
[System.IO.File]::WriteAllText($notes, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding $false))

Write-Host "$zip"
Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object { Write-Host "  $($_.FullName.Substring($stage.Length + 1))" }
