# Writes THIRD-PARTY-NOTICES.md: every third-party component that ships with xDesigner (the app and its render host),
# with its license. Package data comes from the NuGet metadata of the projects' restore output, so restore or build the
# solution first. Run again after updating packages.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-notices.ps1
param([string]$OutPath = (Join-Path $PSScriptRoot "..\THIRD-PARTY-NOTICES.md"))

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }

# Packages that are only used while building, or whose files are kept out of the app (see XDesigner.App.csproj).
$notShipped = '^(Avalonia\.BuildServices|Microsoft\.CodeAnalysis\.Analyzers|Microsoft\.Windows\.SDK\.BuildTools.*|Microsoft\.Windows\.AI\.MachineLearning|Microsoft\.WindowsAppSDK\.(AI|ML|Search|Widgets)|.*\.NativeAssets\.(Linux|macOS|WebAssembly))$'

$packages = @{}
foreach ($assets in "src\XDesigner.App\obj\project.assets.json", "src\XDesigner.RenderHost\obj\project.assets.json") {
    $json = Get-Content (Join-Path $root $assets) -Raw | ConvertFrom-Json
    foreach ($library in $json.libraries.PSObject.Properties) {
        if ($library.Value.type -ne "package") { continue }
        $name, $version = $library.Name -split '/'
        if ($name -notmatch $notShipped) { $packages[$name] = $version }
    }
}

function Read-Nuspec($name, $version) {
    $folder = Join-Path $nuget ("{0}\{1}" -f $name.ToLowerInvariant(), $version)
    [xml]$spec = Get-Content (Get-ChildItem $folder -Filter *.nuspec | Select-Object -First 1).FullName -Raw
    $meta = $spec.package.metadata
    $license = if ($meta.license.type -eq "expression") { $meta.license.'#text' } elseif ($meta.license) { "see below" } else { "see below" }
    [pscustomobject]@{ Name = $name; Version = $version; License = $license; Url = ($meta.projectUrl -replace '\?utm_.*$', ''); Copyright = $meta.copyright; Folder = $folder }
}

$rows = $packages.GetEnumerator() | Sort-Object Name | ForEach-Object { Read-Nuspec $_.Key $_.Value }
$nl = "`r`n"
$sb = [System.Text.StringBuilder]::new()
[void]$sb.Append("# Third-party notices$nl$nl")
[void]$sb.Append("xDesigner is licensed under the PolyForm Noncommercial License 1.0.0 (LICENSE.md). It ships with the components below, which remain under their own licenses. By installing xDesigner you also accept those licenses, including Microsoft's license terms for the Windows App SDK runtime included with it.$nl$nl")
[void]$sb.Append("| Component | Version | License | Project |$nl|---|---|---|---|$nl")
[void]$sb.Append("| .NET runtime | 10 | MIT | https://github.com/dotnet/runtime |$nl")
[void]$sb.Append("| C#/WinRT projections (Microsoft.Windows.SDK.NET, WinRT.Runtime) | | MIT | https://github.com/microsoft/CsWinRT |$nl")
[void]$sb.Append("| Inter font (in Avalonia.Fonts.Inter) | 4 | SIL Open Font License 1.1 | https://github.com/rsms/inter |$nl")
foreach ($r in $rows) { [void]$sb.Append("| $($r.Name) | $($r.Version) | $($r.License) | $($r.Url) |$nl") }

[void]$sb.Append("$nl## MIT-licensed components$nl$nl")
[void]$sb.Append("The .NET runtime, C#/WinRT and the packages marked MIT above are licensed under the MIT License, with these copyright notices:$nl$nl")
[void]$sb.Append("- .NET runtime, C#/WinRT: Copyright (c) .NET Foundation and Contributors / Microsoft Corporation$nl")
foreach ($r in $rows | Where-Object License -eq "MIT") {
    $copyright = if ($r.Copyright) { $r.Copyright.Trim() } else { "see $($r.Url)" }
    [void]$sb.Append("- $($r.Name): $copyright$nl")
}
[void]$sb.Append(@"

    MIT License

    Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
    documentation files (the "Software"), to deal in the Software without restriction, including without limitation
    the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
    permit persons to whom the Software is furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all copies or substantial portions of
    the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
    THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
    TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE.

"@.Replace("`n", $nl).Replace("`r`r", "`r"))

# Components with their own license files, quoted in full (as code blocks, so nothing in them is read as Markdown).
function Add-LicenseFile($title, $path) {
    [void]$sb.Append("$nl## $title$nl$nl" + '```text' + $nl)
    [void]$sb.Append(((Get-Content $path -Raw -Encoding UTF8) -replace "`r?`n", $nl).TrimEnd() + $nl)
    [void]$sb.Append('```' + $nl)
}

foreach ($r in $rows | Where-Object License -ne "MIT") {
    $file = Get-ChildItem $r.Folder -File | Where-Object Name -match '^licen[cs]e(\.txt|\.md)?$' | Select-Object -First 1
    if ($file -and $r.Name -match '^Microsoft\.WindowsAppSDK' -and $r.Name -ne 'Microsoft.WindowsAppSDK') { continue } # same terms as the metapackage
    if ($file) { Add-LicenseFile "$($r.Name) $($r.Version)" $file.FullName }
    $notice = Join-Path $r.Folder "NOTICE.txt"
    if (Test-Path $notice) { Add-LicenseFile "$($r.Name) $($r.Version) - notices" $notice }
}

$inter = Join-Path $PSScriptRoot "licenses\Inter-OFL.txt"
Add-LicenseFile "Inter font - SIL Open Font License 1.1" $inter

$skiaNotices = Join-Path $nuget ("skiasharp.nativeassets.win32\{0}\THIRD-PARTY-NOTICES.txt" -f $packages["SkiaSharp.NativeAssets.Win32"])
Add-LicenseFile "Skia and HarfBuzz native libraries (SkiaSharp / HarfBuzzSharp) - third-party notices" $skiaNotices

[System.IO.File]::WriteAllText($OutPath, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
"Wrote $OutPath ($($rows.Count) packages)"
