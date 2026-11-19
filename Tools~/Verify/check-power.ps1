# Verify the breaker cycle without launching Unity or changing any baseline report.
param([string]$UnityVersion = "6000.6.6f1", [string]$UnityData = "")
$ErrorActionPreference = "Stop"
if (-not $UnityData) { $UnityData = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Data" }
$powerRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$powerDotnet = Join-Path $UnityData "DotNetSdk\dotnet.exe"
if (-not (Test-Path -LiteralPath $powerDotnet)) { throw "Unity's bundled .NET SDK was not found at $powerDotnet." }
$powerCsc = Get-ChildItem (Join-Path $UnityData "DotNetSdk\sdk") -Recurse -Filter csc.dll |
    Where-Object { $_.FullName -like "*\Roslyn\bincore\csc.dll" } | Select-Object -First 1
$powerReferencePack = Get-ChildItem (Join-Path $UnityData "DotNetSdk\packs\Microsoft.NETCore.App.Ref") -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $powerCsc -or -not $powerReferencePack) { throw "The bundled C# compiler or .NET reference pack is missing." }
$powerReferences = Get-ChildItem (Join-Path $powerReferencePack.FullName "ref\net8.0") -Filter *.dll
$powerOutput = Join-Path $env:TEMP "CrushHourPowerCheck"
New-Item -ItemType Directory -Path $powerOutput -Force | Out-Null
$powerAssembly = Join-Path $powerOutput "PowerOutageCheck.dll"
$powerResponse = Join-Path $powerOutput "power-check.rsp"
$powerLines = @('-nologo', '-nostdlib', '-langversion:9.0', '-target:exe', "-out:`"$powerAssembly`"") +
    @($powerReferences | ForEach-Object { "-r:`"$($_.FullName)`"" }) +
    @("`"$(Join-Path $PSScriptRoot 'PowerOutageCheck.cs')`"", "`"$(Join-Path $powerRoot 'Assets\Scripts\CrushHour\PowerOutageCycle.cs')`"")
[IO.File]::WriteAllLines($powerResponse, [string[]]$powerLines)
& $powerDotnet $powerCsc.FullName "@$powerResponse"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
[IO.File]::WriteAllText((Join-Path $powerOutput "PowerOutageCheck.runtimeconfig.json"),
    '{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}')
& $powerDotnet $powerAssembly
exit $LASTEXITCODE
