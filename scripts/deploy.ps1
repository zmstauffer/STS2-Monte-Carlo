# Builds SpireMonteCarlo and installs it for Slay the Spire 2.
#   mods\SpireMonteCarlo\        <- DLL, manifest, dependencies, Data\ (no .json except the manifest)
#   %APPDATA%\SpireMonteCarlo\   <- tier/event/enemy JSON (the game's mod loader treats every .json under mods\ as a manifest)
# Usage: .\scripts\deploy.ps1 [-GameDir <path>] [-NoBuild]
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'SpireMonteCarlo'
$out = Join-Path $project 'bin\Release\net9.0'
$modDir = Join-Path $GameDir 'mods\SpireMonteCarlo'
$appDataDir = Join-Path $env:APPDATA 'SpireMonteCarlo'

if (-not (Test-Path (Join-Path $GameDir 'SlayTheSpire2.exe'))) {
    throw "SlayTheSpire2.exe not found in '$GameDir'. Pass -GameDir <path>."
}
if (Get-Process -Name 'SlayTheSpire2' -ErrorAction SilentlyContinue) {
    throw "Slay the Spire 2 is running and locks the mod DLL. Close the game and run again."
}

if (-not $NoBuild) {
    dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

# robocopy exit codes 0-7 mean success; 8 and above mean failure.
function Copy-Tree($from, $to, [string[]]$extra = @()) {
    robocopy $from $to /E /NFL /NDL /NJH /NJS /NP @extra | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $from -> $to" }
}

# Mod folder: the build output minus AppData\ and the deps/runtimeconfig JSON (the game loads the DLL by path and ignores them).
Copy-Tree $out $modDir @('/XD', (Join-Path $out 'AppData'), '/XF', '*.deps.json', '*.runtimeconfig.json')

# The game's loader doesn't use the mod's deps.json, so SQLite's native library must sit next to the managed DLLs.
Copy-Item (Join-Path $out 'runtimes\win-x64\native\e_sqlite3.dll') $modDir -Force

# App data: tier/event/enemy JSON. Overwrites the shipped files but never touches settings, the log, or the database.
Copy-Tree (Join-Path $out 'AppData') $appDataDir

# The loader treats every .json under mods\ as a possible manifest; warn if anything besides ours slipped in.
$strayJson = Get-ChildItem $modDir -Recurse -Filter *.json |
    Where-Object { $_.Name -ne 'SpireMonteCarlo.json' }
if ($strayJson) {
    Write-Warning "Unexpected .json files under mods\ (the loader will try to read them as manifests):"
    $strayJson | ForEach-Object { Write-Warning "  $($_.FullName)" }
}

Write-Host "Deployed to $modDir"
Write-Host "Data files in $appDataDir"
