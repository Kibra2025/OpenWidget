# Development and releases

OpenWidget is a C# / WPF Windows application targeting .NET Framework 4.8 and x64. The interface is in Italian.

## Layout

- `OpenWidget.csproj`: project and assembly definition.
- Root `*.cs` and `*.xaml`: application, settings, hardware and battery widgets.
- `Assets/`: original icon in ICO, PNG and SVG formats.
- `libs/`, `tools/`: pinned runtime libraries and official PawnIO installer.
- `dependencies.json`, `licenses/`: checksums, provenance and third-party notices.
- `tests/RegressionTests.cs`: regression suite and synthetic preview generation.
- `build.ps1`, `test.ps1`, `publish.ps1`: build, verification and packaging.
- `package-sources.ps1`, `third-party-sources.json`: checksum-pinned third-party source archives, included as a separate release asset.
- `.github/workflows/`: Windows CI and tagged release automation.

The historical `DeskHardwareWidget` namespace, configuration directory, single-instance mutex and startup task identifier are intentional compatibility details. The public application name, assembly and release artifacts are OpenWidget.

## Build and verify

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -RunTests
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1 -SkipBuild
```

`build.ps1` verifies each runtime dependency and downloads checksum-pinned Microsoft reference assemblies if needed. `test.ps1` runs in its own data directory. `publish.ps1` stages a clean portable package, includes license notices, writes per-file SHA-256 checksums and creates a ZIP plus an external checksum file. Personal settings, logs, PDBs and test outputs are excluded.

Generated folders (`bin`, `obj`, `.build`, `dist`) are ignored by Git. Runtime DLLs and the PawnIO installer are restored from official release URLs. SHA-256 verification rejects unexpected or changed downloads; restored binaries are ignored by Git. First builds require Internet access.

## Publish a release

1. Update `AssemblyVersion` and `AssemblyFileVersion` in `AssemblyInfo.cs`, `OpenWidgetVersion` in `dependencies.json`, the download examples, and `CHANGELOG.md`.
2. Run the build, regression checks and packaging commands above. Inspect the ZIP.
3. Commit and push the changes to `main`.
4. Create and push a tag matching the assembly version, for example `v1.7.0`.

The release workflow checks that the tag matches the built assembly, reruns verification, then publishes the portable ZIP, third-party source archive and checksums. It needs only the repository's built-in `GITHUB_TOKEN` with contents write permission for the release job. Untagged builds run with read-only permissions.

Application binaries are currently unsigned. The separately bundled PawnIO installer is the upstream signed installer. Publishing an OpenWidget ZIP does not sign the application or install the driver on a user's PC.
