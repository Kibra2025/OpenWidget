param()
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies.json') -Raw | ConvertFrom-Json
$cache = Join-Path $PSScriptRoot '.build/dependencies'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'libs') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'tools') -Force | Out-Null
$missing = @($manifest.Libraries | Where-Object {
    $path = Join-Path $PSScriptRoot ('libs/' + $_.Name)
    -not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $_.Sha256
})
if ($missing.Count -gt 0) {
    $archive = Join-Path $cache 'LibreHardwareMonitor.zip'
    if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $manifest.LibreHardwareMonitorArchiveSha256) {
        Invoke-WebRequest -UseBasicParsing -Uri $manifest.LibreHardwareMonitorDownload -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive).Hash -ne $manifest.LibreHardwareMonitorArchiveSha256) { throw 'Checksum archivio LibreHardwareMonitor non valido.' }
    $extracted = Join-Path $cache 'LibreHardwareMonitor'
    Expand-Archive -LiteralPath $archive -DestinationPath $extracted -Force
    foreach ($library in $missing) {
        if ($library.Name -ne [IO.Path]::GetFileName($library.Name)) { throw 'Nome libreria non valido.' }
        $source = Join-Path $extracted $library.Name
        if ((Get-FileHash -LiteralPath $source).Hash -ne $library.Sha256) { throw "Checksum libreria non valido: $($library.Name)" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $PSScriptRoot ('libs/' + $library.Name)) -Force
    }
}
$setup = Join-Path $PSScriptRoot 'tools/PawnIO_setup.exe'
if (-not (Test-Path -LiteralPath $setup) -or (Get-FileHash -LiteralPath $setup).Hash -ne $manifest.PawnIoSha256) {
    $download = Join-Path $cache 'PawnIO_setup.exe'
    Invoke-WebRequest -UseBasicParsing -Uri $manifest.PawnIoDownload -OutFile $download
    if ((Get-FileHash -LiteralPath $download).Hash -ne $manifest.PawnIoSha256) { throw 'Checksum installer PawnIO non valido.' }
    Copy-Item -LiteralPath $download -Destination $setup -Force
}
Write-Host 'Dipendenze verificate.'
