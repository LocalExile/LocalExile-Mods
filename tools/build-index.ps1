<#
.SYNOPSIS
  Writes mods.json, the index Waypoint's Browse panel reads, from every mods/<id>/mod.json.
.DESCRIPTION
  Run it from the repository root after adding or changing a mod, and commit mods.json with the change.
  -Check writes nothing and fails when mods.json is not what the mods say (the pull request check runs it).
  It also checks every mod's folder against AGENTS.md: the id is the folder's lower-case name, mod.json and README.md are
  there, the mod has scripts or one project, and no built files are committed.
  Runs on Windows PowerShell 5.1 and PowerShell 7; the JSON is written by hand so both write the same bytes.
#>
param([switch]$Check)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$index = Join-Path $root 'mods.json'
$problems = New-Object System.Collections.Generic.List[string]

function Quote([string]$text) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    foreach ($c in $text.ToCharArray()) {
        switch ($c) {
            '"' { [void]$sb.Append('\"') }
            '\' { [void]$sb.Append('\\') }
            "`n" { [void]$sb.Append('\n') }
            "`r" { [void]$sb.Append('\r') }
            "`t" { [void]$sb.Append('\t') }
            default { if ([int]$c -lt 32) { [void]$sb.Append(('\u{0:x4}' -f [int]$c)) } else { [void]$sb.Append($c) } }
        }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Text($manifest, [string]$name, [string]$fallback) {
    $property = $manifest.PSObject.Properties | Where-Object { $_.Name -ieq $name } | Select-Object -First 1
    if ($null -eq $property -or $null -eq $property.Value) { return $fallback }
    return [string]$property.Value
}

$entries = @()
$folders = Get-ChildItem -LiteralPath (Join-Path $root 'mods') -Directory | Sort-Object Name
foreach ($folder in $folders) {
    $id = $folder.Name
    $where = "mods/$id"
    if ($id -cnotmatch '^[a-z0-9][a-z0-9_.-]{0,127}$') { $problems.Add("${where}: the folder name is the mod's id, in lower case"); continue }
    $manifestPath = Join-Path $folder.FullName 'mod.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { $problems.Add("${where}: mod.json is missing"); continue }
    if (-not (Test-Path -LiteralPath (Join-Path $folder.FullName 'README.md'))) { $problems.Add("${where}: README.md is missing") }
    try { $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { $problems.Add("${where}/mod.json: $($_.Exception.Message)"); continue }
    if ((Text $manifest 'id' $id) -cne $id) { $problems.Add("${where}/mod.json: id is not the folder's name") }
    $version = Text $manifest 'version' ''
    if ($version -notmatch '^\d+\.\d+\.\d+$') { $problems.Add("${where}/mod.json: version is not <major>.<minor>.<patch>") }

    $built = @(Get-ChildItem -LiteralPath $folder.FullName -Recurse -File | Where-Object { $_.Extension -in '.dll', '.pdb', '.exe', '.zip' })
    if ($built.Count) { $problems.Add("${where}: built files are committed ($($built.Name -join ', ')); they reach players through releases") }
    $projects = @(Get-ChildItem -LiteralPath $folder.FullName -Filter '*.csproj' -File)
    $scripts = @(Get-ChildItem -LiteralPath $folder.FullName -Filter '*.cs' -File)
    if ($projects.Count -gt 1) { $problems.Add("${where}: more than one project") }
    if ($projects.Count -eq 1 -and $projects[0].BaseName.ToLowerInvariant() -cne $id) { $problems.Add("${where}: the project's name in lower case is not the id") }
    if (-not $projects.Count -and -not $scripts.Count) { $problems.Add("${where}: no scripts and no project") }

    $depends = @()
    $dependsProperty = $manifest.PSObject.Properties | Where-Object { $_.Name -ieq 'depends' } | Select-Object -First 1
    if ($dependsProperty -and $dependsProperty.Value) { $depends = @($dependsProperty.Value | ForEach-Object { [string]$_ }) }

    $fields = [ordered]@{
        id          = Quote $id
        name        = Quote (Text $manifest 'name' $id)
        description = Quote (Text $manifest 'description' '')
        author      = Quote (Text $manifest 'author' '')
        version     = Quote $version
        kind        = Quote ((Text $manifest 'kind' 'feature').ToLowerInvariant())
        type        = Quote ($(if ($projects.Count) { 'project' } else { 'script' }))
        path        = Quote $where
        depends     = '[' + (($depends | ForEach-Object { Quote $_ }) -join ', ') + ']'
        settings    = $(if (Test-Path -LiteralPath (Join-Path $folder.FullName 'settings.json')) { 'true' } else { 'false' })
    }
    $entries += '    { ' + (($fields.Keys | ForEach-Object { "$(Quote $_): $($fields[$_])" }) -join ', ') + ' }'
}

if ($problems.Count) {
    $problems | ForEach-Object { Write-Host "error: $_" }
    exit 1
}

$json = "{`n  `"version`": 1,`n  `"mods`": [`n" + ($entries -join ",`n") + "`n  ]`n}`n"
if ($Check) {
    $current = if (Test-Path -LiteralPath $index) { [IO.File]::ReadAllText($index).Replace("`r`n", "`n") } else { '' }
    if ($current -cne $json) {
        Write-Host 'error: mods.json is out of date: run tools/build-index.ps1 and commit mods.json'
        exit 1
    }
    Write-Host "mods.json is current ($($folders.Count) mods)"
    exit 0
}
[IO.File]::WriteAllText($index, $json, (New-Object System.Text.UTF8Encoding $false))
Write-Host "wrote mods.json ($($folders.Count) mods)"
