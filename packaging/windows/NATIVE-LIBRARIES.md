# Windows native libraries and the Visual C++ runtime

**Status (2026-10-25): Wisko 0.1.0 ships the NuGet packages' own DLLs and checks for Microsoft's runtime (option 1).
Nothing has been rebuilt. No rebuild route below is approved for distribution: a rebuild needs a confirmed licence
basis and every check under [Checks](#checks-before-a-rebuilt-dll-may-replace-the-shipped-one) passed.**

Summary:

- Two shipped DLLs need the Visual C++ Redistributable, which is not part of Windows.
- Rebuilding them with MSVC and a static (hybrid) runtime is **not verified as redistributable**: the licence texts
  read do not allow it in plain words (see [What the licences say](#what-the-licences-say)).
- A MinGW-w64 (GCC, UCRT) rebuild has a clear licence basis but is **untested**.
- Shipping the package DLLs and checking for Microsoft's runtime needs no rebuild, and nothing of Microsoft's is
  redistributed. **This is the 0.1.0 route** (option 1 below): setup and Wisko itself detect the runtime and point to
  Microsoft's official download.

## What the app ships today

| Library | From | Built with | Needs |
|---|---|---|---|
| `glfw3.dll` | OpenTK.redist.glfw 3.4.0.44 | MSVC 14.29, static C runtime | Windows only |
| `lua54.dll` | KeraLua 1.4.9 (Lua 5.4.8) | MSVC 14.44, static C runtime | Windows only (imports only `KERNEL32.dll`) |
| `Assimp64.dll` | Ultz.Native.Assimp 6.0.2 (Silk.NET) | MSVC 14.44, `/MD` | `MSVCP140.dll`, `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll` |
| `nfd.dll` | NativeFileDialogs.Net.Runtime 1.3.0 | MSVC 14.44, `/MD` | `VCRUNTIME140.dll` |

The Visual C++ runtime DLLs (14.44 or later) are not part of Windows. A computer without the Visual C++ Redistributable
cannot load `Assimp64.dll` or `nfd.dll`: model import and file dialogs fail there. The Universal C runtime
(`api-ms-win-crt-*`) that all four use is part of Windows 10 and 11.

### Provenance

Checked on the shipped files:

- **`Assimp64.dll`** reports version 6.0.2, revision `fb375dd8` (the commit of assimp's `v6.0.2` tag). It is x64, with
  1,478 exports, of which 129 are `ai*` C functions.
- **`nfd.dll`** is x64 with 36 exports (`NFD_*`).

### What Wisko uses

- **Assimp:** 14 functions, from `Engine/Assets/Importing/AssimpModelImporter.cs`: `aiGetVersionMajor`, `Minor`,
  `Patch`, `aiGetExtensionList`, `aiIsExtensionSupported`, `aiImportFileEx` (with Wisko's own `aiFileIO`),
  `aiGetErrorString`, `aiReleaseImport`, and six `aiGetMaterial*` functions: `String`, `Color`, `FloatArray`,
  `IntegerArray`, `TextureCount`, `Texture`.
- **nfd:** Wisko calls only NativeFileDialogs.Net's `Nfd.OpenDialog`. That reaches `NFD_Init`, `NFD_OpenDialogU8`,
  `NFD_GetError` (on errors) and `NFD_Quit`.

Two facts about the binding (NativeFileDialogs.Net 1.3.0.1, from its metadata):

- **Six declared entry points do not exist.** The binding declares 22 entry points. Six of them (`NFD_*U8_With`) are
  inline functions in nfd 1.3.0's header, so nfd.dll exports them only as `NFD_*U8_With_Impl`. Calling them would fail,
  but Wisko never does.
- **Each dialog leaks one path string.** `Nfd.OpenDialog` never calls `NFD_FreePathU8`, so the chosen path is not
  freed. The memory belongs to nfd's own runtime, so a different C runtime in nfd changes nothing about this.

## What the licences say

These were read on 2026-10-25, each in the text Microsoft publishes:

- the Build Tools licence terms: "Visual Studio 2022 … and Build Tools", and "Visual Studio 2026 … and Build Tools",
  last updated October 1, 2025;
- Visual Studio Community 2026, last updated October 1, 2025;
- the Visual Studio 2026 Distributable List (https://aka.ms/vs/18/redistribution);
- the Visual C++ v14 Redistributable licence (VS 2026).

| Route | What the texts say | Status |
|---|---|---|
| MSVC with a static/hybrid runtime, built with Build Tools and no Visual Studio licence | Allowed to *compile* Open Source Dependencies. Not allowed to "share, publish, rent, lease, or otherwise distribute the software or any of its code". No Distributable Code section. | **Not verified**: a static runtime puts Microsoft's code into the DLL |
| MSVC with a static/hybrid runtime, built with a Visual Studio Community licence | The Distributable List names merge modules and the files in `VC\redist`. It names no static libraries. | **Not verified** |
| App-local copies of the `VC\redist` DLLs (Community licence) | Allowed, unmodified, under the Distribution Requirements (below). | Allowed with conditions; a decision for the maintainer |
| Microsoft's redistributable package downloaded from Microsoft, or installed by the user | The end user installs Microsoft's package under Microsoft's licence ("You may install and use any number of copies"). Wisko distributes nothing of Microsoft's. | Allowed |
| Bundling `vc_redist.x64.exe` inside Wisko's installer | "Distribution of the Visual C++ Runtime Redistributable package … is limited to licensed Visual Studio users and is subject to its license terms." | Allowed with conditions, as for app-local DLLs |
| MinGW-w64 GCC (UCRT), runtime linked statically | The GCC Runtime Library Exception allows distributing the combined code under terms of one's choice. The mingw-w64 CRT and winpthreads are under permissive licences that require their notices. No Microsoft code is involved: the UCRT is part of Windows and the import libraries are mingw-w64's own. | **Licence basis clear; build untested** |

### Build Tools (2022 and 2026)

The open-source clause, in both versions:

> "if you don't have a valid license to any of the Visual Studio Products you may still install and use copies of the
> software to compile and build C++ components that (i) have been released by a third party under an open-source
> software license approved by the Open Source Initiative; and (ii) are also reasonably required to build your
> applications ("Open Source Dependencies")."

Assimp is BSD-3-Clause and nativefiledialog-extended is zlib, so both qualify. The licence also counts "changing
options in a build script" as an allowed "minor modification".

The scope clause, 2026 version:

> "you may not: … share, publish, rent, lease, or otherwise distribute the software or any of its code; or provide the
> software as a stand-alone offering or combined with any of your applications for others to use"

The 2022 version has the same clause without "or otherwise distribute … any of its code". Neither version has a
Distributable Code section.

A static runtime (vcruntime and the C++ library, `libvcruntime.lib` and `libcpmt.lib`) copies the software's object code
into the DLL that Wisko would distribute. Nothing in these texts allows that.

Microsoft's licensing guidance has this sentence: "Code identified as distributable that has the extension .lib cannot
be directly distributed; [it] must be linked into the application. However, the resulting output can be distributed."
It applies to code *identified as distributable*. The 2026 Distributable List identifies no `.lib` files.

Static linking is common practice, but this repository does not rely on practice. Marking the route verified would need
a written answer from Microsoft licensing.

### Visual Studio Community 2026

- **Individual License:** "If you are an individual working on your own applications, either to sell or for any other
  purpose, you may use the software to develop and test those applications."
- **Distributable Code:** "You may copy and distribute the object code form of code listed on the Distributable List."
  For Visual C++, that list names the `Microsoft_VC143_*` merge modules, and "any of the files within the following
  folder and its subfolders … You may not modify these files: `[VisualStudioFolder]\VC\redist`". It excludes
  `debug_nonredist`.
- **Distribution Requirements:** distributing any Distributable Code requires all three of:
  - "add significant primary functionality to it in your applications";
  - "require distributors and external end users to agree to terms that protect the Distributable Code at least as much
    as these license terms";
  - "indemnify, defend, and hold harmless Microsoft from any claims … related to the distribution or use of your
    applications".

  Wisko is Apache-2.0. Shipping Microsoft's DLLs app-local would therefore need end-user terms covering them, plus the
  indemnity. That is a legal decision, not a technical one.

## Options

1. **Keep the package DLLs and check for the runtime.** _Chosen for 0.1.0 and implemented; see
   [README.md](README.md)._
   - Setup and Wisko itself check the runtime DLLs (`vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll`) and
     their version: 14.44 or later, because the DLLs are built with 14.44 and an older runtime is not enough.
   - When it is missing or too old, they say so and offer to open Microsoft's link
     (https://aka.ms/vc14/vc_redist.x64.exe), only if the person chooses to.
   - The portable zip gets the same check from Wisko itself.
   - No rebuild, no licence question, no administrator rights. A computer without the runtime still cannot import
     models or open file dialogs until the user installs it.
2. **The installer downloads Microsoft's package.** If the runtime is missing, setup downloads `vc_redist.x64.exe` from
   Microsoft and runs it.
   - Microsoft's file changes with every runtime update, so a fixed hash cannot be checked. Setup must verify its
     Authenticode signature instead, with a helper such as PowerShell's `Get-AuthenticodeSignature`.
   - It needs internet and administrator rights during setup (Wisko otherwise installs without them).
   - It does not help the portable zip.
3. **MinGW-w64 rebuild** ([procedure below](#mingw-w64-rebuild-proposed-untested)). This is the only route to
   dependency-free DLLs with a clear licence basis.
   - It needs approval to install MSYS2, or to run a GitHub-hosted Windows job.
   - Every check must pass. Assimp v6.0.2's own CI does not build with MinGW; nativefiledialog-extended's CI does,
     but only as a static library.
4. **App-local `VC\redist` DLLs.** Allowed with the Community conditions above. Not recommended without a decision on
   the end-user terms and the indemnity.
5. **MSVC with a static/hybrid runtime.** Not verified ([procedure kept for reference](#msvc-hybrid-runtime-reference-only)).

Decision for 0.1.0 (2026-10-25):

- Option 1, so the release depends on no unverified licence or untested binary. Option 2 is not used: nothing is
  downloaded or installed silently.
- Option 3 is a possible follow-up. It replaces the DLLs only after every check below has passed. MSYS2 and Build Tools
  are not installed.

## MinGW-w64 rebuild (proposed, untested)

### Toolchain

Nothing below has been installed. Installing MSYS2 needs approval.

- **Source:** MSYS2 from https://www.msys2.org/; the installer comes from the `msys2/msys2-installer` GitHub releases.
  Verify the installer against the SHA-256 published with that release. Pacman checks package signatures itself.
- **Removal:** MSYS2's uninstaller, or deleting its folder (`C:\msys64` by default).
- **Alternative:** a GitHub-hosted Windows runner with the `msys2/setup-msys2` action. That needs a workflow change,
  and a run needs approval.

In the **MSYS2 UCRT64** shell:

```sh
pacman -Syu        # repeat until nothing is left to update
pacman -S --needed git mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-ninja
gcc --version; cmake --version
pacman -Q | grep -E '^mingw-w64-ucrt-x86_64-(gcc|gcc-libs|crt|winpthreads|headers|binutils|cmake|ninja)'   # record these
```

### Sources

```sh
git clone --depth 1 --branch v6.0.2 https://github.com/assimp/assimp.git
git -C assimp rev-parse HEAD     # must start with fb375dd8
git clone --depth 1 --branch v1.3.0 https://github.com/btzy/nativefiledialog-extended.git nfd
git -C nfd rev-parse HEAD        # must start with fc168e8
```

### Build

These are the options Silk.NET's own build uses: a shared library, bundled zlib, no tests. `-static` makes the linker
take libgcc, libstdc++ and winpthreads from their static archives. Windows' own DLLs, including the UCRT, stay
imports.

```sh
cmake -S assimp -B build-assimp -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_SHARED_LIBS=ON -DASSIMP_BUILD_ZLIB=ON -DASSIMP_BUILD_TESTS=OFF -DASSIMP_BUILD_ASSIMP_TOOLS=OFF \
  -DASSIMP_WARNINGS_AS_ERRORS=OFF -DASSIMP_INSTALL=OFF -DCMAKE_SHARED_LINKER_FLAGS=-static
cmake --build build-assimp

cmake -S nfd -B build-nfd -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_SHARED_LIBS=ON -DNFD_BUILD_TESTS=OFF -DCMAKE_SHARED_LINKER_FLAGS=-static
cmake --build build-nfd

find build-assimp build-nfd -name '*.dll'   # the Assimp DLL becomes Assimp64.dll, the nfd DLL nfd.dll
```

### Notices

These go into `THIRD-PARTY-NOTICES.md`:

- a statement that the GCC runtime libraries are used under the GCC Runtime Library Exception;
- the licence files of the mingw-w64 CRT and winpthreads, taken from the packages actually used
  (`pacman -Ql <package> | grep -i -E 'copying|licen'`).

## MSVC hybrid runtime (reference only)

**The licence basis is not verified (see above). Do not distribute DLLs built this way.**

This links the compiler runtime (vcruntime) and the C++ library statically, and the UCRT dynamically.

| Library | Configure (x64, Release) |
|---|---|
| Assimp `v6.0.2` (`fb375dd8`) | `cmake -S assimp -B build -A x64 -DBUILD_SHARED_LIBS=ON -DASSIMP_BUILD_ZLIB=ON -DASSIMP_BUILD_TESTS=OFF -DASSIMP_WARNINGS_AS_ERRORS=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded "-DCMAKE_SHARED_LINKER_FLAGS=/NODEFAULTLIB:libucrt.lib /DEFAULTLIB:ucrt.lib"` |
| nativefiledialog-extended `v1.3.0` (`fc168e8`) | `cmake -S nfd -B build -A x64 -DBUILD_SHARED_LIBS=ON -DNFD_BUILD_TESTS=OFF -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded "-DCMAKE_SHARED_LINKER_FLAGS=/NODEFAULTLIB:libucrt.lib /DEFAULTLIB:ucrt.lib"` |

Notes:

- Build with `cmake --build build --config Release`.
- `CMAKE_MSVC_RUNTIME_LIBRARY` is used instead of Assimp's `USE_STATIC_CRT`, which adds `/MT` on top of CMake's default
  `/MD`.
- nativefiledialog-extended asks for CMake 3.10, so policy CMP0091 must be switched on for `CMAKE_MSVC_RUNTIME_LIBRARY`
  to apply.
- The Build Tools licence allows building and testing these as Open Source Dependencies. Installing Build Tools needs
  approval.

## Checks before a rebuilt DLL may replace the shipped one

Run all commands from the repository root, in PowerShell. `<new>` is the folder with the rebuilt `Assimp64.dll` and
`nfd.dll`.

1. **Architecture and runtime.** The DLL must be x64 and import only Windows: `api-ms-win-crt-*`, `ucrtbase.dll` and
   Windows system DLLs. No `MSVCP*`, `VCRUNTIME*`, `msvcrt.dll`, `libgcc*`, `libstdc++*` or `libwinpthread*`.
2. **Exports.** The DLL must export the functions Wisko calls, and every C function of the shipped DLL (`ai*` and
   `NFD_*`). C++ names differ between compilers and are not compared. Checks 1 and 2 together:

   ```powershell
   powershell -ExecutionPolicy Bypass -File packaging\windows\check-native-dll.ps1 -Path <new>\Assimp64.dll -Kind Assimp -Reference dist\Wisko\Assimp64.dll
   powershell -ExecutionPolicy Bypass -File packaging\windows\check-native-dll.ps1 -Path <new>\nfd.dll -Kind Nfd -Reference dist\Wisko\nfd.dll
   ```

   Today's shipped DLLs pass every check except the imports: `MSVCP140`, `VCRUNTIME140` and `VCRUNTIME140_1` for
   Assimp, and `VCRUNTIME140` for nfd. Those imports are what the rebuild is for.
3. **Version** and 4. **identical import results.** Import every model the repository tracks with Wisko's flags and
   compare with the baseline:

   ```powershell
   git archive --output="$env:TEMP\wisko-examples.tar" HEAD examples
   New-Item -ItemType Directory "$env:TEMP\wisko-examples" | Out-Null      # a new, empty folder
   tar -xf "$env:TEMP\wisko-examples.tar" -C "$env:TEMP\wisko-examples"
   dotnet run --project tools\Wisko.AssimpImportReport -c Release -- "$env:TEMP\wisko-examples" --assimp <new>\Assimp64.dll --out "$env:TEMP\new-import.txt"
   Compare-Object (Get-Content packaging\windows\assimp-import-baseline.txt) (Get-Content "$env:TEMP\new-import.txt")
   ```

   The report must start with `Assimp 6.0.2 revision fb375dd8` and end with `models 13; digest 138102FE9E1C0EB7`.
   `Compare-Object` must print nothing, and the tool exits with 1 if any model fails to import. Any difference is
   explained before going further.

   The baseline was reproduced on 2026-10-25 with the Silk.NET package DLL and with the shipped DLL, both through this
   exact procedure. The report counts, per model:
   - meshes, materials, animations and nodes;
   - per mesh: vertices, faces, bones, material, normals, UVs and a vertex-position checksum;
   - per animation: channels, duration and ticks per second.

   There is a second baseline. After `scripts\build-windows-app.ps1 -Package`, the report over the whole repository
   adds the 16 model copies in `dist\Examples`. With the shipped DLL it gives `models 29; digest 14334DE65415A21F`.
5. **Wisko's tests.** Build, put the rebuilt DLLs in the test runner's folder, then run without building again. Building
   copies the package DLLs back.

   ```powershell
   dotnet build Tests\Wisko.Tests -c Release
   Copy-Item <new>\Assimp64.dll, <new>\nfd.dll Tests\Wisko.Tests\bin\Release\net10.0\runtimes\win-x64\native\
   dotnet run --project Tests\Wisko.Tests -c Release --no-build
   dotnet run --project Tests\Wisko.Tests -c Release --no-build -- --gui-smoke
   Get-FileHash Tests\Wisko.Tests\bin\Release\net10.0\runtimes\win-x64\native\Assimp64.dll, <new>\Assimp64.dll
   ```

   Both suites must pass. The hashes must match, which shows the tested DLL was the rebuilt one. The headless suite
   covers model import through Wisko's own `aiFileIO`, animation import and the Khronos samples.
6. **File dialogs (by hand).** Use an app folder with the rebuilt DLLs, never the installed Wisko. Check that:
   - the import dialog opens and shows the model filter;
   - Cancel returns without an error;
   - choosing a model imports it;
   - a path with non-ASCII characters works.
7. **Clean computer.** Follow [CLEAN-MACHINE-TESTING.md](CLEAN-MACHINE-TESTING.md): on Windows without any Visual C++
   Redistributable, models import and file dialogs open.
8. **Interop.** No memory may cross between the libraries' runtime and Wisko. This has been checked in the code:
   - Assimp's scene is released by `aiReleaseImport` (`AssimpModelImporter.cs`).
   - Wisko's `aiFileIO` is allocated and freed by Wisko (`AssimpFileSystem.Create` and `Free`).
   - nfd's returned path is never freed (the binding leak above), so nothing crosses there either.
9. **Provenance and notices.** The DLLs go into `ThirdParty/windows-native/` with a `VERSION.txt`, like
   `ThirdParty/bgfx/VERSION.txt`. It records the sources and commits, the toolchain and package versions, the exact
   commands and each DLL's SHA-256. `THIRD-PARTY-NOTICES.md` gains the toolchain runtime notices.

   `scripts\build-windows-app.ps1` would then copy the DLLs over the package DLLs after publishing, verifying the
   hashes. That script change is proposed, not made.

## Keeping it secure

- The UCRT is updated by Windows Update.
- A statically linked runtime is fixed at build time. Rebuild when its maker ships a security fix:
  - MinGW route: GCC's libstdc++ and libgcc, mingw-w64's CRT and winpthreads (MSYS2 package updates).
  - MSVC route: Microsoft's MSRC advisories and the MSVC release notes.
- Assimp parses model files, which may come from other people's projects, so its own security fixes matter more than the
  runtime's. Follow Assimp and nativefiledialog-extended releases. Every rebuild repeats every check above.
- With option 1 or 2, Microsoft's runtime is serviced by Microsoft Update, and the package DLLs change only with a
  Silk.NET or NativeFileDialogs.Net update.
