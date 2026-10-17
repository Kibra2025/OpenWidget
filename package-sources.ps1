param()
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$sourceRoot = Join-Path $PSScriptRoot '.build/source-archives'
New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$sources = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'third-party-sources.json') -Raw | ConvertFrom-Json
foreach ($source in $sources) {
    if ($source.Name -ne [IO.Path]::GetFileName($source.Name)) { throw 'Nome archivio sorgente non valido.' }
    $path = Join-Path $sourceRoot $source.Name
    if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $source.Sha256) {
        Invoke-WebRequest -UseBasicParsing -Uri $source.Url -OutFile $path
    }
    if ((Get-FileHash -LiteralPath $path).Hash -ne $source.Sha256) { throw "Checksum sorgenti non valido: $($source.Name)" }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third-party-sources.json') -Destination $sourceRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/DEPENDENCIES.txt') -Destination $sourceRoot
@'
Source archives for the unmodified MPL/GPL/LGPL components distributed with
OpenWidget. Upstream copyright, license files and build scripts are inside
each archive. Versions, origins and hashes are in third-party-sources.json.

For PawnIO 2.2.0, extract the matching PawnPP archive into the PawnIO/PawnPP
subdirectory before building. This supplies its pinned Git submodule.
Follow each upstream project's build instructions and toolchain requirements.

OpenWidget's original source and build scripts are available separately:
https://github.com/Kibra2025/OpenWidget
'@ | Set-Content -LiteralPath (Join-Path $sourceRoot 'README.txt') -Encoding ASCII
$distributionRoot = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $distributionRoot -Force | Out-Null
$zip = Join-Path $distributionRoot 'OpenWidget-ThirdPartySources.zip'
# Explicit file list prevents temporary or unlisted cache files from shipping.
$files = @($sources | ForEach-Object { Join-Path $sourceRoot $_.Name })
$files += @('third-party-sources.json', 'DEPENDENCIES.txt', 'README.txt') | ForEach-Object { Join-Path $sourceRoot $_ }
Compress-Archive -LiteralPath $files -DestinationPath $zip -Force
Write-Host "Sorgenti delle dipendenze: $zip"
