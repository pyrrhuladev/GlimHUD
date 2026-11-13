# Contributing to GlimHUD

Use English for issues, pull requests, commit descriptions, and repository documentation. The application supports English and Russian.

Before starting a substantial change, describe the expected behavior in an issue. Small fixes can be submitted directly. Include the Windows version, GlimHUD version, reproduction steps, and expected result in bug reports. Remove personal paths, track names, process lists, tokens, and other private data from attachments.

## Development

Use Windows x64 and the .NET 10 SDK. See the [README](README.md) for build and dependency setup.

```powershell
dotnet restore .\GlimHUD.slnx
dotnet build .\GlimHUD.slnx --configuration Release --no-restore
.\scripts\Test.ps1 -NoRestore -NoBuild
```

Python 3 is used for the release archive fixture checks. A Windows SDK with packaging tools is only required when working on MSIX packaging.

Keep changes focused and explain their user-visible result. Add meaningful checks for changes to behavior. Keep unavailable hardware, absent media sessions, and denied capture permissions safe to handle. Default checks must not change the user's playback, volume, Windows settings, or security configuration. Use an explicitly created fixture for tests that require playback.

Add user-facing text through the localization resources, with matching English and Russian keys. English is the default and fallback language. Avoid hardcoded translated strings in UI or service messages.

Do not commit `bin`, `obj`, `.tools`, `.packages`, `artifacts`, driver caches, personal settings, diagnostics, credentials, or local tool/session files. Preserve third-party licenses and update the notices when adding dependencies. Do not claim universal game or anti-cheat compatibility without evidence.

By submitting a contribution, you agree to license your original contribution under this repository's MIT license. Third-party code must retain its applicable license and attribution.
