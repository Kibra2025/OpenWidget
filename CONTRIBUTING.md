# Contributing to OpenWidget

Use [GitHub Issues](https://github.com/Kibra2025/OpenWidget/issues) for bugs and feature requests. Reports in English or Italian are welcome.

For a bug, include the application version, Windows version, affected hardware, USB/Bluetooth/receiver connection type, expected behavior and reproduction steps. Review diagnostic files and screenshots before sharing them; remove personal data and device identifiers that are not needed.

For code changes, keep compatibility with Windows x64 and .NET Framework 4.8. Keep sensor reads off the UI thread, preserve unavailable readings, and avoid changing device settings from battery-monitoring code. Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -RunTests` before opening a pull request.

Do not commit generated builds, local settings or logs. Update the documentation when changing user-visible behavior. Contributions to original OpenWidget code are made under the repository's MIT license; retain notices for adapted third-party code.
