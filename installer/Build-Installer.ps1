[CmdletBinding()]
param(
    [string]$Compiler,
    [string]$Payload,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$AppVersion,
    [switch]$Windows10Test,
    [ValidateRange(1,65535)][int]$SetupRevision
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$Compiler) { $Compiler = Join-Path $root '.tools\inno-7.1.1\ISCC.exe' }
if (!$AppVersion) { $AppVersion = ([xml](Get-Content -Raw -LiteralPath "$root\src\Directory.Build.props")).Project.PropertyGroup.Version }
if ($AppVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid desktop version.' }
if (!$Payload) { $Payload = Join-Path $root "build\$AppVersion" }
$Payload = (Resolve-Path -LiteralPath $Payload).Path
$appExe = if ($AppVersion -eq '1.3.17') { 'Razer DualSense Installer.exe' } else { 'HapticBridge.exe' }
if ($SetupRevision -eq 0) { $SetupRevision = if ($Windows10Test) { 4 } else { 1 } }
$setupVersion = "$AppVersion.$SetupRevision"
$minimumWindows = if ($Windows10Test) { '10.0.19045' } else { '10.0.22000' }
$productVersion = (Get-Item -LiteralPath (Join-Path $Payload $appExe)).VersionInfo.ProductVersion.Trim()
if ($productVersion -ne $AppVersion) { throw "Expected desktop version $AppVersion, found $productVersion." }
$compilerVersion = & $Compiler --version
if ($LASTEXITCODE -ne 0 -or "$compilerVersion".Trim() -ne '7.1.1') { throw 'Use Inno Setup 7.1.1.' }

$manifest = Get-Content -Raw -LiteralPath "$PSScriptRoot\payload-$AppVersion.json" | ConvertFrom-Json
if ($appExe -cnotin $manifest.path) { throw 'The desktop executable is missing from the payload lock.' }
$work = Join-Path $root ('.cache\phase2-build\' + [guid]::NewGuid().ToString('N'))
$release = Join-Path $root "build\installer-$setupVersion"
New-Item -ItemType Directory -Force $work,$release | Out-Null
$stage = New-Item -ItemType Directory (Join-Path $work 'payload')
$files = [Collections.Generic.List[string]]::new()
$checks = [Collections.Generic.List[string]]::new()
$paths = [Collections.Generic.List[string]]::new()
$checks.Add('procedure VerifyPayload;')
$checks.Add('begin')
$paths.Add('function PayloadHasReparsePoint(Path: String): Boolean;')
$paths.Add('begin')
$paths.Add('  Result := True;')
foreach ($entry in $manifest) {
    # The approved layout contains only root files and flat third-party notices.
    if ($entry.path -cnotmatch '^(LICENSE|(licenses/)?[A-Za-z0-9][A-Za-z0-9 .-]*\.(exe|dll|json|txt))$' -or
        $entry.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid payload lock entry.' }
    $source = Join-Path $Payload $entry.path
    $copy = Join-Path $stage.FullName $entry.path
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy)) | Out-Null
    [IO.File]::Copy($source, $copy, $false)
    if ((Get-FileHash -LiteralPath $copy).Hash -ne $entry.sha256) { throw "Release file differs from lock: $($entry.path)" }
    $dest = if ($entry.path.StartsWith('licenses/')) { '{app}\licenses' } else { '{app}' }
    $name = [IO.Path]::GetFileName($copy)
    $files.Add(('Source: "{0}"; DestDir: "{1}"; Flags: ignoreversion' -f $copy,$dest))
    $checks.Add(("  CheckEmbedded('{0}', '{1}');" -f $name,$entry.sha256))
    $paths.Add(("  if ContainsReparsePoint(AddBackslash(Path) + '{0}') then Exit;" -f $entry.path.Replace('/','\')))
}
$helpFile = Join-Path $work 'Installation help.txt'
$help = (Get-Content -Raw -LiteralPath "$PSScriptRoot\Installation help.txt").Replace('@APP_VERSION@', $AppVersion).Replace('@APP_EXE@', $appExe)
[IO.File]::WriteAllText($helpFile, $help)
$helpHash = (Get-FileHash -LiteralPath $helpFile).Hash.ToLowerInvariant()
$checks.Add("  CheckEmbedded('Installation help.txt', '$helpHash');")
$checks.Add('end;')
$paths.Add("  if ContainsReparsePoint(AddBackslash(Path) + 'Installation help.txt') then Exit;")
$paths.Add('  Result := False;')
$paths.Add('end;')
$files | Set-Content -LiteralPath "$work\PayloadFiles.iss" -Encoding UTF8
$checks | Set-Content -LiteralPath "$work\PayloadChecks.iss" -Encoding UTF8
$paths | Set-Content -LiteralPath "$work\PayloadPaths.iss" -Encoding UTF8
& $Compiler --no-ide-signtools "--include-dirs=$work" "--output-dir=$work" "--define=AppVersion=$AppVersion" "--define=SetupVersion=$setupVersion" "--define=AppExe=$appExe" "--define=HelpFile=$helpFile" "--define=MinWindows=$minimumWindows" "$PSScriptRoot\HapticBridge.iss"
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$exe = Get-Item -LiteralPath "$work\HapticBridge-Setup-$setupVersion-x64.exe"
$dest = Join-Path $release $exe.Name
Copy-Item -LiteralPath $exe.FullName -Destination $dest
$hash = (Get-FileHash -LiteralPath $dest).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$dest.sha256", "$hash  $($exe.Name)" + [Environment]::NewLine)
Write-Output "Built $dest ($hash)"
