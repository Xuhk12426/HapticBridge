[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$AppVersion,
    [switch]$Windows10Test,
    [ValidateRange(1,65535)][int]$SetupRevision
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$AppVersion) { $AppVersion = ([xml](Get-Content -Raw -LiteralPath "$root\src\Directory.Build.props")).Project.PropertyGroup.Version }
if ($SetupRevision -eq 0) { $SetupRevision = if ($Windows10Test) { 4 } else { 1 } }
$setupVersion = "$AppVersion.$SetupRevision"
$exe = Join-Path $root "build\installer-$setupVersion\HapticBridge-Setup-$setupVersion-x64.exe"
$expected = ((Get-Content -LiteralPath "$exe.sha256" -Raw) -split '\s+')[0]
if ((Get-FileHash -LiteralPath $exe).Hash -ne $expected) { throw 'Installer checksum mismatch.' }
if ((Get-Item -LiteralPath $exe).VersionInfo.FileVersion.Trim() -ne $setupVersion) { throw 'Unexpected installer version.' }
$qa = New-Item -ItemType Directory -Force (Join-Path $root ('.cache\phase2-verify\' + [guid]::NewGuid().ToString('N')))
$log = Join-Path $qa.FullName 'verify.log'
# An occupied test destination is a second guard against an accidental normal install.
$occupied = New-Item -ItemType Directory (Join-Path $qa.FullName 'occupied')
Set-Content -LiteralPath (Join-Path $occupied.FullName 'sentinel.txt') -Value 'Verification only'
$priorTemp = $env:TEMP
$priorTmp = $env:TMP
try {
    $env:TEMP = $qa.FullName
    $env:TMP = $env:TEMP
    $argsList = @('/CURRENTUSER','/VERIFYONLY','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',
        ('/DIR="' + $occupied.FullName + '"'),('/LOG="' + $log + '"'))
    $process = Start-Process -FilePath $exe -ArgumentList $argsList -Wait -PassThru -WindowStyle Hidden
} finally { $env:TEMP = $priorTemp; $env:TMP = $priorTmp }
# Returning false from InitializeSetup gives exit 1. The log marker distinguishes success from an error.
if ($process.ExitCode -ne 1 -or !(Select-String -LiteralPath $log -SimpleMatch 'HapticBridge embedded payload and target policy verified. No installation requested.') -or
    @(Get-ChildItem -LiteralPath $occupied.FullName -Force).Count -ne 1) { throw "Verification failed. Inspect $log" }
Write-Output "Verified embedded files and target policy. Exit $($process.ExitCode). Log: $log"
