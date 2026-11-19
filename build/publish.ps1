# Builds the xDesigner release: tests, a self-contained publish of the app with its render host, and the installer.
# Output: artifacts\installer\xDesigner-<version>-x64-setup.exe (+ .sha256), artifacts\publish\xDesigner (the files).
# Needs the .NET 10 SDK and Inno Setup 6 (https://jrsoftware.org/isinfo.php; ISCC.exe is found in the usual places).
# Usage: powershell -ExecutionPolicy Bypass -File build\publish.ps1 [-SkipTests]
param([switch]$SkipTests)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "No <Version> in Directory.Build.props." }

$artifacts = Join-Path $root "artifacts"
$publishDir = Join-Path $artifacts "publish\xDesigner"
$installerDir = Join-Path $artifacts "installer"

Step "xDesigner ${version}: build (Release)"
Invoke-Checked "Build" { dotnet build xDesigner.slnx -c Release -nologo -v:minimal }

if (-not $SkipTests) {
    Step "Tests"
    Invoke-Checked "Tests" { dotnet test xDesigner.slnx -c Release --no-build -nologo --filter "Category!=Slow" }
}

Step "Publish (self-contained, ReadyToRun) to $publishDir"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
Invoke-Checked "Publish" { dotnet publish src\XDesigner.App -c Release -o $publishDir -nologo -v:minimal }

# One render host per framework version, in renderhosts\{folder} (FrameworkProfile.HostFolder/HostExecutable).
$renderHosts = "renderhosts\avalonia12\XDesigner.RenderHost.Avalonia12.exe", "renderhosts\avalonia11\XDesigner.RenderHost.Avalonia11.exe"
foreach ($required in @("xDesigner.exe", "xDesigner.pri", "LICENSE.txt", "THIRD-PARTY-NOTICES.txt") + $renderHosts) {
    if (-not (Test-Path (Join-Path $publishDir $required))) { throw "The publish output has no $required." }
}

foreach ($renderHost in $renderHosts) {
    $hostVersion = & (Join-Path $publishDir $renderHost) --version
    if ($LASTEXITCODE -ne 0) { throw "The published render host $renderHost doesn't start." }
    Write-Host "Render host: $hostVersion"
}
if (Get-ChildItem $publishDir -Recurse -Filter *.pdb) { throw "Debug symbols ended up in the publish output." }

Step "Installer"
$iscc = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) wasn't found. Install it from https://jrsoftware.org/isinfo.php." }

New-Item -ItemType Directory -Force $installerDir | Out-Null
# The license page: what installing means, then the license itself.
$licenseText = Join-Path $installerDir "License.txt"
$intro = @"
xDesigner is licensed under the PolyForm Noncommercial License 1.0.0, printed below.

xDesigner also includes third-party components that stay under their own licenses, among them Microsoft's Windows App SDK runtime and the .NET runtime. Their terms are in THIRD-PARTY-NOTICES.txt in the installation folder (also under Help > Third-party notices). By installing xDesigner you accept the license below and those terms.

------------------------------------------------------------------------------------------------------------------------

"@
$license = ($intro + (Get-Content (Join-Path $root "LICENSE.md") -Raw)) -replace "`r?`n", "`r`n"
[System.IO.File]::WriteAllText($licenseText, $license, [System.Text.UTF8Encoding]::new($true))

Invoke-Checked "Inno Setup" { & $iscc "/DAppVersion=$version" "/DPublishDir=$publishDir" "/DLicenseFile=$licenseText" "/DOutputDir=$installerDir" /Q (Join-Path $root "installer\xDesigner.iss") }

$setup = Join-Path $installerDir "xDesigner-$version-x64-setup.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$setup.sha256" -Value "$hash  $(Split-Path $setup -Leaf)" -Encoding ascii

$installed = (Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Step "Done"
Write-Host ("Installer: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
Write-Host ("Installed size: {0:N0} MB" -f $installed)
Write-Host "SHA-256: $hash"
