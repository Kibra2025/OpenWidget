param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -RunTests }
$release = Join-Path $PSScriptRoot 'bin/Release'
$executable = Join-Path $release 'OpenWidget.exe'
$version = ([Diagnostics.FileVersionInfo]::GetVersionInfo($executable)).FileVersion
$version = ([version]$version).ToString(3)
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies.json') -Raw | ConvertFrom-Json
if ($manifest.OpenWidgetVersion -ne $version) { throw 'Versione assembly e dependencies.json non coerente.' }
$distributionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist'))
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.build/publish'))
$packageRoot = [IO.Path]::GetFullPath((Join-Path $stagingRoot 'OpenWidget-Portable'))
$workspaceRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
if (-not $stagingRoot.StartsWith($workspaceRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not $packageRoot.StartsWith($stagingRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Percorso di pubblicazione non valido.' }
if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
foreach ($name in @('OpenWidget.exe', 'OpenWidget.exe.config')) {
    Copy-Item -LiteralPath (Join-Path $release $name) -Destination $packageRoot
}
Get-ChildItem -LiteralPath $release -Filter '*.dll' | Copy-Item -Destination $packageRoot
foreach ($folder in @('tools', 'licenses')) {
    Copy-Item -LiteralPath (Join-Path $release $folder) -Destination $packageRoot -Recurse
}
foreach ($name in @('README.md', 'LICENSE', 'CHANGELOG.md', 'CONTRIBUTING.md', 'dependencies.json', 'third-party-sources.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $packageRoot
}
New-Item -ItemType Directory -Path (Join-Path $packageRoot 'docs/images') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/README.it.md') -Destination (Join-Path $packageRoot 'docs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/DEVELOPMENT.md') -Destination (Join-Path $packageRoot 'docs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/README.it.md') -Destination (Join-Path $packageRoot 'LEGGIMI.md')
Copy-Item -Path (Join-Path $PSScriptRoot 'docs/images/*.png') -Destination (Join-Path $packageRoot 'docs/images')
New-Item -ItemType Directory -Path (Join-Path $packageRoot 'Assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/AppIcon.png') -Destination (Join-Path $packageRoot 'Assets')
$launcher = '@echo off', 'cd /d "%~dp0"', 'start "" "%~dp0OpenWidget.exe"'
$launcher | Set-Content -LiteralPath (Join-Path $packageRoot 'Avvia.bat') -Encoding ASCII
Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
} | Set-Content -LiteralPath (Join-Path $packageRoot 'SHA256SUMS.txt') -Encoding ASCII
New-Item -ItemType Directory -Path $distributionRoot -Force | Out-Null
$zipName = "OpenWidget-Portable-$version-win-x64.zip"
$zip = Join-Path $distributionRoot $zipName
Add-Type -AssemblyName System.IO.Compression
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
$zipStream = [IO.File]::Open($zip, [IO.FileMode]::CreateNew)
$zipArchive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
try {
    Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
        $entry = $zipArchive.CreateEntry('OpenWidget-Portable/' + $relative, [IO.Compression.CompressionLevel]::Optimal)
        $inputStream = $_.OpenRead()
        $outputStream = $entry.Open()
        try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
    }
} finally { $zipArchive.Dispose(); $zipStream.Dispose() }
& (Join-Path $PSScriptRoot 'package-sources.ps1')
@($zip, (Join-Path $distributionRoot 'OpenWidget-ThirdPartySources.zip')) | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_)
} | Set-Content -LiteralPath (Join-Path $distributionRoot "OpenWidget-$version-SHA256SUMS.txt") -Encoding ASCII
# Refresh only packaged application files; retain a portable user's settings/logs.
$portableRoot = Join-Path $distributionRoot 'OpenWidget-Portable'
New-Item -ItemType Directory -Path $portableRoot -Force | Out-Null
Get-ChildItem -LiteralPath $packageRoot -Recurse -File | ForEach-Object {
    $destination = Join-Path $portableRoot $_.FullName.Substring($packageRoot.Length + 1)
    if (-not (Test-Path -LiteralPath $destination) -or
        (Get-FileHash -LiteralPath $_.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
}
Write-Host "Pacchetto portatile: $zip"
