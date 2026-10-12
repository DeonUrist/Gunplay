param([string]$GameDir = 'E:\SteamLibrary\steamapps\common\Apocalypter', [switch]$WithLegacy)
$ErrorActionPreference = 'Stop'
$taskGame = (Resolve-Path -LiteralPath $GameDir).Path
$taskRepo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskRuntime = Join-Path $PSScriptRoot $(if ($WithLegacy) { 'runtime-ammo-legacy' } else { 'runtime-ammo' })
$taskOutput = Join-Path $PSScriptRoot ('output\' + (Get-Date -Format yyyyMMdd-HHmmss))
dotnet build (Join-Path $taskRepo 'Gunplay.csproj') -c Release "-p:GameDir=$taskGame" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed' }
dotnet build (Join-Path $PSScriptRoot 'NativeVerifier.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Verifier build failed' }
New-Item -ItemType Directory -Path "$taskRuntime\BepInEx\plugins", "$taskRuntime\BepInEx\config", $taskOutput -Force | Out-Null
foreach ($file in @('Apocalypter.exe','UnityPlayer.dll','winhttp.dll','doorstop_config.ini','.doorstop_version')) { Copy-Item -LiteralPath "$taskGame\$file" -Destination $taskRuntime -Force }
Copy-Item -LiteralPath "$taskGame\BepInEx\core" -Destination "$taskRuntime\BepInEx" -Recurse -Force
foreach ($dir in @('Apocalypter_Data','MonoBleedingEdge')) { if (!(Test-Path -LiteralPath "$taskRuntime\$dir")) { New-Item -ItemType Junction -Path "$taskRuntime\$dir" -Target "$taskGame\$dir" | Out-Null } }
Copy-Item -LiteralPath "$taskRepo\bin\Release\Gunplay.dll", "$PSScriptRoot\bin\Release\net472\GunplayNativeVerifier.dll" -Destination "$taskRuntime\BepInEx\plugins" -Force
if ($WithLegacy) { Copy-Item -LiteralPath "$taskGame\BepInEx\plugins\Apocaunloader.dll" -Destination "$taskRuntime\BepInEx\plugins" -Force }
@'
[Logging.Console]
Enabled = false
[Logging.Disk]
Enabled = true
LogLevels = All
'@ | Set-Content -LiteralPath "$taskRuntime\BepInEx\config\BepInEx.cfg"
@'
[General]
Enabled = true
HoldSeconds = 0.55
AnimationTimeout = 5.5
FallbackKey = X
DropBackpackOverflow = true
'@ | Set-Content -LiteralPath "$taskRuntime\BepInEx\config\com.denis.apocalypter.apocaunloader.cfg"
@'
[Ammo]
HoldSeconds = 0.45
[Debug]
VerboseLog = false
'@ | Set-Content -LiteralPath "$taskRuntime\BepInEx\config\com.denis.apocalypter.gunplay.cfg"
$taskArgs = @('-batchmode','-nographics','-logFile',('"' + $taskRuntime + '\unity.log"'),('-magazine-test-root="' + $taskOutput + '"'))
$taskProcess = Start-Process -FilePath "$taskRuntime\Apocalypter.exe" -WorkingDirectory $taskRuntime -WindowStyle Hidden -ArgumentList $taskArgs -PassThru
try {
    $taskDeadline = (Get-Date).AddMinutes(3)
    while (!$taskProcess.WaitForExit(1000)) { if ((Get-Date) -gt $taskDeadline) { throw 'Native verifier timed out' } }
    $taskReport = Join-Path $taskOutput 'report.txt'
    if (!(Test-Path -LiteralPath $taskReport)) { throw 'Native verifier produced no report' }
    Get-Content -LiteralPath $taskReport
    Write-Output "Report: $taskReport"
    if (Select-String -LiteralPath $taskReport -Pattern '^FAIL:') { throw 'Native verifier failed' }
} finally { if (!$taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id } }
