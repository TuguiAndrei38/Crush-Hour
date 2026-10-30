# Crush Hour: compile check + headless balance baseline, without opening Unity.
# Uses the .NET SDK and Roslyn that ship with the Unity editor. See AGENTS.md, "Verifying without the editor".
#
#   powershell -ExecutionPolicy Bypass -File "Tools~\Verify\verify.ps1"                 # compile + baseline
#   (if Windows blocks the baseline program, use Tools > Crush Hour > Run Balance Baseline in Unity)
#   powershell -ExecutionPolicy Bypass -File "Tools~\Verify\verify.ps1" -SkipBaseline   # compile only
#   ... -Seeds 11,21,33,47                                                               # other seeds
#   ... -UnityData "D:\Unity\6000.6.6f1\Editor\Data"                                     # Unity installed elsewhere
#
# Works while Unity is open (it doesn't touch the project lock). Output goes to %TEMP%\CrushHourVerify;
# the baseline table is also written to Tools~\Verify\baseline.md.
param(
    [string]$UnityVersion = "6000.6.6f1",
    [string]$UnityData = "",
    [switch]$SkipBaseline,
    [int[]]$Seeds = @(11, 21, 33)
)
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $UnityData) { $UnityData = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Data" }
$dotnet = Join-Path $UnityData "DotNetSdk\dotnet.exe"
if (-not (Test-Path $dotnet)) { throw "Can't find $dotnet. Pass the Unity 'Editor\Data' folder with -UnityData." }
$csc = Get-ChildItem (Join-Path $UnityData "DotNetSdk\sdk") -Recurse -Filter csc.dll |
    Where-Object { $_.FullName -like "*\Roslyn\bincore\csc.dll" } | Select-Object -First 1
if (-not $csc) { throw "Can't find Roslyn (csc.dll) under $UnityData\DotNetSdk\sdk." }

# References and defines come from the project file Unity generates.
$editorProj = Join-Path $Root "Assembly-CSharp-Editor.csproj"
$runtimeProj = Join-Path $Root "Assembly-CSharp.csproj"
if (Test-Path $editorProj) { $proj = $editorProj; $withEditor = $true }
elseif (Test-Path $runtimeProj) {
    $proj = $runtimeProj; $withEditor = $false
    Write-Warning "Assembly-CSharp-Editor.csproj not found: checking runtime scripts only (Editor folders skipped). In Unity, Assets > Open C# Project generates it."
}
else { throw "No .csproj in $Root. Open the project in Unity once (Assets > Open C# Project) so it generates them." }

[xml]$xml = Get-Content $proj
$refs = @()
foreach ($hint in $xml.SelectNodes("//*[local-name()='HintPath']")) {
    $path = $hint.InnerText.Trim()
    if (-not [System.IO.Path]::IsPathRooted($path)) { $path = Join-Path $Root $path }
    $leaf = [System.IO.Path]::GetFileName($path)
    if ($leaf -eq "Assembly-CSharp.dll" -or $leaf -eq "Assembly-CSharp-Editor.dll") { continue }
    if (Test-Path $path) { $refs += $path }
}
# Package assemblies (Input System...) can be ProjectReferences instead of HintPaths: take them from Unity's build output.
$scriptAssemblies = Join-Path $Root "Library\ScriptAssemblies"
if (Test-Path $scriptAssemblies) {
    $known = @{}
    foreach ($r in $refs) { $known[[System.IO.Path]::GetFileName($r)] = $true }
    foreach ($dll in Get-ChildItem $scriptAssemblies -Filter *.dll) {
        if ($dll.Name -like "Assembly-CSharp*" -or $known.ContainsKey($dll.Name)) { continue }
        if (-not $withEditor -and $dll.Name -like "*Editor*") { continue }
        $refs += $dll.FullName
    }
}
$defineNode = $xml.SelectNodes("//*[local-name()='DefineConstants']") | Select-Object -First 1
$defines = "UNITY_EDITOR"
if ($defineNode) { $defines = $defineNode.InnerText.Trim() + ";UNITY_EDITOR" }

$out = Join-Path $env:TEMP "CrushHourVerify"
New-Item -ItemType Directory -Force $out | Out-Null

function Write-Rsp($file, $target, $output, $sources) {
    $lines = @("-nologo", "-nostdlib", "-unsafe", "-langversion:9.0", "-target:$target",
        "-nowarn:0169,0414,0649,1591,0618", "-out:`"$output`"", "-define:`"$defines`"")
    $lines += $refs | ForEach-Object { "-r:`"$_`"" }
    $lines += $sources | ForEach-Object { "`"$_`"" }
    [System.IO.File]::WriteAllLines($file, [string[]]$lines)
}

# 1. Compile check: every script under Assets as one assembly, like AGENTS.md describes.
$sources = Get-ChildItem (Join-Path $Root "Assets") -Recurse -Filter *.cs |
    Where-Object { $withEditor -or ($_.FullName -notmatch '\\Editor\\') } | ForEach-Object { $_.FullName }
$rsp = Join-Path $out "compile.rsp"
Write-Rsp $rsp "library" (Join-Path $out "Check.dll") $sources
Write-Host "Compiling $($sources.Count) scripts ($([System.IO.Path]::GetFileName($proj)))..." -ForegroundColor Cyan
& $dotnet $csc.FullName "@$rsp"
if ($LASTEXITCODE -ne 0) { Write-Host "COMPILE FAILED" -ForegroundColor Red; exit 1 }
Write-Host "COMPILE OK" -ForegroundColor Green
if ($SkipBaseline) { exit 0 }

# 2. Headless baseline: FestivalVenue + CrowdSimulation + Baseline.cs as a .NET 8 console program.
$bdir = Join-Path $out "baseline"
New-Item -ItemType Directory -Force $bdir | Out-Null
$scripts = Join-Path $Root "Assets\Scripts\CrushHour"
$bsources = @((Join-Path $PSScriptRoot "Baseline.cs"), (Join-Path $scripts "FestivalVenue.cs"), (Join-Path $scripts "CrowdSimulation.cs"), (Join-Path $scripts "Incidents.cs"),
    (Join-Path $Root "Assets\Scripts\Editor\BalanceBaselineRunner.cs"))
$brsp = Join-Path $bdir "baseline.rsp"
Write-Rsp $brsp "exe" (Join-Path $bdir "Baseline.dll") $bsources
& $dotnet $csc.FullName "@$brsp"
if ($LASTEXITCODE -ne 0) { Write-Host "BASELINE BUILD FAILED" -ForegroundColor Red; exit 1 }
foreach ($name in @("UnityEngine.CoreModule.dll", "UnityEngine.SharedInternalsModule.dll")) {
    $dll = $refs | Where-Object { [System.IO.Path]::GetFileName($_) -eq $name } | Select-Object -First 1
    if ($dll) { Copy-Item $dll $bdir -Force }
}
$config = '{ "runtimeOptions": { "tfm": "net8.0", "framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0" } } }'
[System.IO.File]::WriteAllText((Join-Path $bdir "Baseline.runtimeconfig.json"), $config)

$table = Join-Path $PSScriptRoot "baseline.md"
Write-Host "Running baseline (seeds $($Seeds -join ', ')). This takes a few minutes..." -ForegroundColor Cyan
& $dotnet (Join-Path $bdir "Baseline.dll") ($Seeds -join ",") $table
if ($LASTEXITCODE -ne 0) {
    Write-Host "BASELINE FAILED" -ForegroundColor Red
    Write-Host "If Windows says an Application Control policy blocked Baseline.dll, run the same baseline inside Unity instead:" -ForegroundColor Yellow
    Write-Host "  Tools > Crush Hour > Run Balance Baseline  (writes Tools~\Verify\baseline.md)" -ForegroundColor Yellow
    exit 1
}
Write-Host "Table saved to $table" -ForegroundColor Green
