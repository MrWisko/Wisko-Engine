# Security

## Reporting a problem

Please report security problems privately through the official Wisko repository's security advisory form ("Report a
vulnerability") rather than in a public issue. Include what you found, how to reproduce it and the Wisko version.

## What Wisko protects against, and what it does not

Wisko is an editor that opens projects and runs their scripts on your computer. It takes care that a mistake in a
project cannot easily harm the editor or your files, but **it is not a sandbox for hostile projects. Open projects only
from people you trust**, as you would with any game engine or 3D tool.

**Lua scripts** run in a sandbox:

- Scripts get no file, operating-system or network access, and no access to .NET or native code. `require` reads only
  the project's own `Assets/Scripts` folder and Wisko's built-in modules. Game saves (`GameSave`) go only to the
  project's `Saves` folder, under plain slot names.
- Each callback has an instruction budget, and all scripts share a Lua memory limit (256 MB by default), so a runaway
  loop or allocation stops that script instead of freezing or crashing the editor. See
  [docs/lua-limits.md](docs/lua-limits.md).
- Scripts can still do anything the API offers within a project: change the world in Play, show any GUI, write game
  saves.

**Project files** (`Scene.json`, `UI.json`, `Atmosphere.json`, `Graphics.json`) are read with checks: unknown or invalid
values fall back to defaults, and an unreadable UI file is kept aside rather than overwritten.

**Models and images** in a project are decoded by third-party code: models by the native Open Asset Import Library
(Assimp), images by StbImageSharp (which uses unsafe code). A deliberately malformed file could exploit a bug in those
decoders. This is the main reason to open only projects you trust.

**Saving** never leaves a damaged file: every file is written to a temporary file that replaces the old one only once
it is complete on the disk.

**Network:** Wisko makes no network connections. Chat is local only (see [docs/chat.md](docs/chat.md)).

## Builds

The Windows and macOS applications are built from this repository with `scripts/build-windows-app.ps1` and
`scripts/build-macos-app.sh`. Release builds contain no paths from the build machine. Signing of the released
applications is still being arranged; until it is, Windows SmartScreen and macOS Gatekeeper warn about downloaded
copies.
