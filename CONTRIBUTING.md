# Contributing to Wisko

Thank you for helping. Bug reports, fixes, documentation and examples are all welcome.

## Before you start

- For a bug, open an issue with the Wisko version, your operating system and GPU, what you did, what you expected and
  what happened. The log helps: `%LOCALAPPDATA%\Wisko\Logs\Wisko.log` on Windows, `~/Library/Logs/Wisko/Wisko.log` on
  macOS.
- For a larger change, open an issue first so we can agree on the approach.
- Security problems: see [SECURITY.md](SECURITY.md); please do not report them in public issues.

## Building and testing

You need the .NET SDK (see [README.md](README.md#building-from-source)).

```powershell
dotnet build Wisko.sln
dotnet run --project Tests/Wisko.Tests                  # headless suite (any OS)
dotnet run --project Tests/Wisko.Tests -- --gui-smoke    # the real editor, scripted (Windows, display on)
```

Every change should keep both suites passing. Add checks for what you change: the suites are plain C# in
`Tests/Wisko.Tests` (see its [README](Tests/Wisko.Tests/README.md)). Never weaken an existing check to make a change
pass.

## Code style

- Match the code around your change: naming, comments and structure.
- Comments explain why, not what the next line does.
- Keep changes focused; a pull request should do one thing.
- Build without warnings.
- Save files through `AtomicFile` (Engine/Platform) so a failed save never damages a file.
- Lua API changes: update `ScriptApiCatalog` (editor completion) and the docs in `docs/`.

## Licensing

Wisko is licensed under the Apache License 2.0. By contributing, you agree that your contribution is licensed under
the same license (Apache License 2.0, section 5). Do not add code or assets you do not have the right to contribute;
third-party additions need a compatible license and an entry in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The
Wisko name and logo are covered by [BRANDING.md](BRANDING.md).
