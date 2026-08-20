# Fail-closed check for the obfuscated (protected) build.
#
# The mod installs its Harmony patches BY NAME (nameof(...) baked to a string at compile time). If the
# obfuscator renames such a method, the patch silently never installs and a screen goes quiet - the
# failure this project fears most. obfuscar.xml keeps every method whose name ends in the project's
# _Postfix/_Prefix/_Finalizer/_Transpiler convention. This script proves that held, and it runs on the
# STAGING output BEFORE anything is published to the shipping folder.
#
# Arg 1: the directory holding the obfuscation Mapping.txt to check (the staging dir). Defaults to
# bin\Obfuscated for a manual run.
#
# Exit 0 = safe to publish. Exit 1 = a reflected method was renamed (build must fail). Exit 2 = the
# mapping is missing/empty/incomplete, so nothing can be certified (build must fail).
#
# Three independent checks, all shape-independent (they do not try to parse every Harmony registration
# form - a real one, Hook(...), resolves the method name from a variable and would defeat call-site
# parsing):
#   GUARD - the mapping actually did substantial work (else an empty/stale file could "pass").
#   A     - no method whose NAME follows the convention was renamed (verifies the skip rule directly).
#   C     - every method the mod references by nameof (bare, or qualified with a mod type) was NOT
#           renamed - catches a reflected method registered through ANY shape, and one that does not
#           follow the convention.

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $PSScriptRoot                 # mod/  (this script lives in mod/build/)
$mapDir = if ($args.Count -ge 1 -and $args[0]) { $args[0] } else { Join-Path $root 'bin\Obfuscated' }
$map    = Join-Path $mapDir 'Mapping.txt'
if (-not (Test-Path $map)) { Write-Host "verify-obfuscation: mapping not found at $map" -ForegroundColor Red; exit 2 }
$lines = Get-Content $map

# --- Parse the mapping into renamed / skipped method-name sets, and count evidence of real work. ---
# Method lines read either:  ...::Name[argc]( args ) -> NewName      (renamed)
#                     or:     ...::Name[argc]( args ) skipped: reason (kept)
$renamed = [System.Collections.Generic.HashSet[string]]::new()
$skipped = [System.Collections.Generic.HashSet[string]]::new()
$typeRenames = 0; $methodLines = 0
$reRen  = [regex]'::([A-Za-z0-9_]+)\[\d+\]\([^)]*\)\s*->\s*\S'
$reSkip = [regex]'::([A-Za-z0-9_]+)\[\d+\]\([^)]*\)\s*skipped'
$reType = [regex]'^\[[^\]]+\][^ ]+ -> \[[^\]]+\]\S'          # a top-level type-rename line
foreach ($ln in $lines) {
  $m = $reRen.Match($ln);  if ($m.Success) { [void]$renamed.Add($m.Groups[1].Value); $methodLines++; continue }
  $m = $reSkip.Match($ln); if ($m.Success) { [void]$skipped.Add($m.Groups[1].Value); $methodLines++; continue }
  if ($reType.IsMatch($ln)) { $typeRenames++ }
}

# Collect the mod's declared type names up front (used by the guard AND by Check C to tell
# nameof(ModType.X) from nameof(GameType.X)).
$src = Get-ChildItem $root -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$modTypes = [System.Collections.Generic.HashSet[string]]::new()
$srcText  = @{}
foreach ($f in $src) {
  $t = [IO.File]::ReadAllText($f.FullName); $srcText[$f.FullName] = $t
  foreach ($ty in [regex]::Matches($t, '(?m)\b(?:class|struct|record)\s+([A-Za-z0-9_]+)')) { [void]$modTypes.Add($ty.Groups[1].Value) }
}

# --- GUARD: refuse to certify an empty / stale / truncated / wrong-format mapping (fail closed). ---
# The bar is PROPORTIONAL to the source, so it scales as the mod grows or shrinks and can't be
# satisfied by a truncated mapping: a real run renames essentially every mod type (KeepPublicApi=false,
# HidePrivateApi=true). Require at least 40% of the source types renamed, plus small absolute floors.
$minTypes = [int]($modTypes.Count * 0.4)
if ($typeRenames -lt $minTypes -or $typeRenames -lt 30 -or $methodLines -lt 150) {
  Write-Host "verify-obfuscation: mapping looks empty/incomplete/truncated (types=$typeRenames need>=$minTypes and >=30; methods=$methodLines need>=150; source types=$($modTypes.Count)) - refusing to certify." -ForegroundColor Red
  exit 2
}

# --- CHECK A: no convention-named method was renamed (the skip rule did its job). ---
$conv = [regex]'_(Postfix|Prefix|Finalizer|Transpiler)$'
$badA = @($renamed | Where-Object { $conv.IsMatch($_) } | Sort-Object -Unique)

# --- CHECK C: every mod method referenced by nameof survived. Secondary to Check A (which already
# covers every convention-following method regardless of how it is registered); this additionally
# guards a reflected method that does NOT follow the convention. A nameof references a MOD member when
# it is bare (a member of the enclosing mod class) or its FIRST segment is a mod type
# (nameof(ModType.X), nameof(ModType.X.Y) -> take the last segment); a game-type-qualified nameof is a
# game method and is ignored, so a mod method that merely shares a game method's name never false-fails.
# Only method renames matter (fields/properties live in a separate mapping section), so a nameof of a
# renamed field can't trip this either. ---
$reflected = [System.Collections.Generic.HashSet[string]]::new()
$reNameof  = [regex]'nameof\(\s*([A-Za-z0-9_]+(?:\s*\.\s*[A-Za-z0-9_]+)*)\s*\)'
foreach ($f in $src) {
  foreach ($n in $reNameof.Matches($srcText[$f.FullName])) {
    $parts = $n.Groups[1].Value -split '\s*\.\s*'
    if ($parts.Count -eq 1) { [void]$reflected.Add($parts[0]) }                         # bare nameof(X)
    elseif ($modTypes.Contains($parts[0])) { [void]$reflected.Add($parts[-1]) }         # nameof(ModType. ... .X)
  }
}
# Only method renames matter here: `renamed` holds method names only, so a nameof'd field/property
# (fields are renamed, but live in a separate mapping section) never trips this.
$badC = @($reflected | Where-Object { $renamed.Contains($_) } | Sort-Object -Unique)

$bad = @($badA + $badC | Sort-Object -Unique)
if ($bad.Count) {
  Write-Host "OBFUSCATION VERIFY FAILED - these game-reflected methods were RENAMED; the protected mod would break:" -ForegroundColor Red
  $bad | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
  Write-Host "Fix: make each end with _Postfix / _Prefix / _Finalizer / _Transpiler (obfuscar.xml keeps those), then rebuild." -ForegroundColor Yellow
  exit 1
}
Write-Host "Obfuscation verify OK: convention preserved (types=$typeRenames, methods=$methodLines); $($reflected.Count) nameof-referenced mod methods all preserved."
exit 0
