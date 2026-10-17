param([switch]$RunTests)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
& (Join-Path $PSScriptRoot 'restore-dependencies.ps1')
$manifest = Get-Content -LiteralPath './dependencies.json' -Raw | ConvertFrom-Json
foreach ($library in $manifest.Libraries) {
    $path = Join-Path $PSScriptRoot ('libs/' + $library.Name)
    if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $library.Sha256) {
        throw "Dipendenza mancante o modificata: $($library.Name)"
    }
}
$setup = Join-Path $PSScriptRoot 'tools/PawnIO_setup.exe'
if (-not (Test-Path -LiteralPath $setup) -or (Get-FileHash -LiteralPath $setup).Hash -ne $manifest.PawnIoSha256) {
    throw 'Installer PawnIO mancante o modificato.'
}
$referenceRoot = Join-Path $PSScriptRoot '.build/reference-assemblies'
if (-not (Test-Path -LiteralPath (Join-Path $referenceRoot 'build/.NETFramework/v4.8/mscorlib.dll'))) {
    New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot '.build') -Force | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $archive = Join-Path $PSScriptRoot '.build/net48-reference-assemblies.zip'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net48/1.0.3/microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg' -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive).Hash -ne $manifest.ReferenceAssembliesSha256) { throw 'Checksum targeting pack non valido.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $referenceRoot -Force
}
$msbuild = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { throw 'MSBuild .NET Framework non trovato.' }
& $msbuild './OpenWidget.csproj' /t:Rebuild /p:Configuration=Release /p:Platform=x64 /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw 'Compilazione fallita. Se il widget è aperto, chiudilo dal menu della tray.' }
if ($RunTests) { & (Join-Path $PSScriptRoot 'test.ps1') -NoBuild }
Write-Host 'Build pronta: bin/Release/OpenWidget.exe'
