param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$testRoot = Join-Path $PSScriptRoot '.build/tests'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
# Keep tests in their own portable data directory even if the user has settings
# in LocalAppData from an older, non-elevated installation.
'{}' | Set-Content -LiteralPath (Join-Path $testRoot 'config.json') -Encoding UTF8
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'bin/Release') -Filter '*.dll' | Copy-Item -Destination $testRoot -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/OpenWidget.exe') -Destination $testRoot -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/OpenWidget.exe.config') -Destination (Join-Path $testRoot 'OpenWidget.Tests.exe.config') -Force
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$wpf = Join-Path $framework 'WPF'
$testSource = Join-Path $PSScriptRoot 'tests\RegressionTests.cs'
& (Join-Path $framework 'csc.exe') /nologo /platform:x64 "/out:$testRoot\OpenWidget.Tests.exe" "/reference:$testRoot\OpenWidget.exe" "/reference:$testRoot\LibreHardwareMonitorLib.dll" "/reference:$wpf\PresentationFramework.dll" "/reference:$wpf\PresentationCore.dll" "/reference:$wpf\WindowsBase.dll" "/reference:$framework\System.Xaml.dll" /reference:System.Xml.dll /reference:System.Drawing.dll /reference:System.Runtime.Serialization.dll /reference:Microsoft.CSharp.dll $testSource
if ($LASTEXITCODE -ne 0) { throw 'Compilazione test fallita.' }
& (Join-Path $testRoot 'OpenWidget.Tests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Test di regressione falliti.' }
