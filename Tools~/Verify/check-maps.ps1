# Compile the project, then verify all three generated layouts and their difficulty rounds headlessly.
param([string]$UnityVersion = "6000.6.6f1", [string]$UnityData = "")
$ErrorActionPreference = "Stop"
if (-not $UnityData) { $UnityData = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Data" }
& (Join-Path $PSScriptRoot "verify.ps1") -UnityData $UnityData -SkipBaseline
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$mapCheckRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$mapCheckDir = Join-Path $env:TEMP "CrushHourMapCheck"
New-Item -ItemType Directory -Force -Path $mapCheckDir | Out-Null
$mapCheckDotnet = Join-Path $UnityData "DotNetSdk\dotnet.exe"
$mapCheckCsc = Get-ChildItem (Join-Path $UnityData "DotNetSdk\sdk") -Recurse -Filter csc.dll |
    Where-Object { $_.FullName -like "*\Roslyn\bincore\csc.dll" } | Select-Object -First 1
$mapCheckReferences = Get-Content (Join-Path $env:TEMP "CrushHourVerify\compile.rsp") | Where-Object { $_ -like '-r:*' }
$mapCheckSources = @((Join-Path $PSScriptRoot "MapLayoutCheck.cs")) +
    @("FestivalVenue.cs", "CrowdSimulation.cs", "Incidents.cs", "Difficulty.cs", "MapProgress.cs" | ForEach-Object {
        Join-Path $mapCheckRoot "Assets\Scripts\CrushHour\$_"
    })
$mapCheckOutput = Join-Path $mapCheckDir "MapLayoutCheck.dll"
$mapCheckLines = @('-nologo', '-nostdlib', '-langversion:9.0', '-target:exe', "-out:`"$mapCheckOutput`"") +
    $mapCheckReferences + @($mapCheckSources | ForEach-Object { "`"$_`"" })
$mapCheckRsp = Join-Path $mapCheckDir "map-check.rsp"
[IO.File]::WriteAllLines($mapCheckRsp, [string[]]$mapCheckLines)
& $mapCheckDotnet $mapCheckCsc.FullName "@$mapCheckRsp"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($mapCheckReference in $mapCheckReferences) {
    $mapCheckPath = $mapCheckReference.Substring(3).Trim('"')
    if ([IO.Path]::GetFileName($mapCheckPath) -in @('UnityEngine.CoreModule.dll', 'UnityEngine.SharedInternalsModule.dll')) {
        Copy-Item -LiteralPath $mapCheckPath -Destination $mapCheckDir -Force
    }
}
[IO.File]::WriteAllText((Join-Path $mapCheckDir "MapLayoutCheck.runtimeconfig.json"),
    '{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}')
& $mapCheckDotnet $mapCheckOutput
exit $LASTEXITCODE
