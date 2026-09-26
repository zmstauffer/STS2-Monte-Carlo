# Runs the advisor next to the game: it watches the folder the mod writes decision snapshots to and prints a
# recommendation for each one (also saved to %APPDATA%\SpireMonteCarlo\advice\latest.txt and latest.json).
#
#   1. Deploy the mod once (scripts\deploy.ps1, game closed) and update the data cache (advisor codex update).
#   2. Start this script, then play. Each card reward, rest site, map choice, shop, and event it understands gets advice.
#
# Pass extra options through, e.g.  .\scripts\watch.ps1 --n 4000   (more simulated futures = steadier answers, slower)
$repo = Split-Path -Parent $PSScriptRoot
dotnet build "$repo\SpireMonteCarlo.Advisor" -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project "$repo\SpireMonteCarlo.Advisor" -c Release --no-build -- watch @args
