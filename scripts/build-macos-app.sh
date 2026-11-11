#!/usr/bin/env bash
# Builds dist/Wisko Engine.app (Apple Silicon, self-contained .NET, bgfx/Metal by default).
# Uses only the .NET SDK and tools that ship with macOS (sips, iconutil, plutil, codesign, otool, file).
# Local development build: ad-hoc signed, NOT Developer ID signed or notarized.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_NAME="Wisko Engine"
EXECUTABLE="Wisko.Editor"
PROJECT="$ROOT/Game/Wisko.Editor.csproj"
ICON_SOURCE="$ROOT/branding/AppIcon/Wisko-AppIcon-1024.png"
PLIST_TEMPLATE="$ROOT/scripts/macos/Info.plist.in"
RID="osx-arm64"
DIST="$ROOT/dist"
FINAL_APP="$DIST/$APP_NAME.app"

fail() { echo "error: $*" >&2; exit 1; }

[[ "$(uname -s)" == "Darwin" && "$(uname -m)" == "arm64" ]] || fail "build on Apple Silicon macOS (the bgfx dylib is osx-arm64)"
for tool in dotnet sips iconutil plutil codesign otool install_name_tool file xattr ditto shasum; do
    command -v "$tool" >/dev/null || fail "missing required tool: $tool"
done

VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version | tr -d '[:space:]')"
[[ -n "$VERSION" ]] || fail "could not read <Version> from $PROJECT"
echo "==> Wisko Engine $VERSION ($RID)"

# The bundle is assembled and signed in a private temp directory: a repo inside an iCloud-synced folder
# (e.g. ~/Documents) gets File Provider/Finder attributes stamped on new packages, which codesign rejects.
WORK="$(mktemp -d "${TMPDIR:-/tmp}/wisko-app.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
APP="$WORK/$APP_NAME.app"

echo "==> dotnet publish (Release, self-contained, $RID)"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -o "$WORK/publish"
for required in "$EXECUTABLE" libbgfx.dylib libcoreclr.dylib libhostfxr.dylib libglfw.3.dylib libassimp.6.dylib; do
    [[ -f "$WORK/publish/$required" ]] || fail "publish output is missing $required"
done
# Only the approved native libraries are packaged: the committed bgfx build that ThirdParty/bgfx/VERSION.txt records
# (never a different or freshly rebuilt one), and Assimp 6.0.2 without the package's leftover 5.4.1 build.
BGFX_EXPECTED="$(sed -n 's/^ *SHA-256: *\([0-9a-f]\{64\}\).*/\1/p' "$ROOT/ThirdParty/bgfx/VERSION.txt")"
BGFX_ACTUAL="$(shasum -a 256 "$WORK/publish/libbgfx.dylib" | awk '{print $1}')"
[[ -n "$BGFX_EXPECTED" && "$BGFX_ACTUAL" == "$BGFX_EXPECTED" ]] \
    || fail "libbgfx.dylib ($BGFX_ACTUAL) is not the build ThirdParty/bgfx/VERSION.txt records ($BGFX_EXPECTED)"
[[ ! -e "$WORK/publish/libassimp.5.dylib" ]] || fail "libassimp.5.dylib (Assimp 5.4.1) must not be packaged"

echo "==> App bundle"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$WORK/publish/." "$APP/Contents/MacOS/"   # executable, .NET runtime, libbgfx.dylib side by side
# The prebuilt bgfx dylib still carries its build-tree install name; give the bundled copy a loader-relative one.
install_name_tool -id @rpath/libbgfx.dylib "$APP/Contents/MacOS/libbgfx.dylib" 2>/dev/null

echo "==> Icon"
ICONSET="$WORK/Wisko.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$ICON_SOURCE" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    sips -z "$((size * 2))" "$((size * 2))" "$ICON_SOURCE" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Wisko.icns"

echo "==> Licenses and notices"
# Wisko's own, and the bundled .NET runtime's license and notices from the runtime pack this publish used (resources,
# not code: Contents/Resources, sealed by the bundle signature).
RESOURCES="$APP/Contents/Resources"
cp "$ROOT/LICENSE" "$RESOURCES/LICENSE.txt"
cp "$ROOT/NOTICE" "$RESOURCES/NOTICE.txt"
cp "$ROOT/THIRD-PARTY-NOTICES.md" "$ROOT/BRANDING.md" "$RESOURCES/"
RUNTIME_VERSION="$(grep -A4 '"includedFrameworks"' "$WORK/publish/$EXECUTABLE.runtimeconfig.json" \
    | sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' | head -1)"
[[ -n "$RUNTIME_VERSION" ]] || fail "could not read the bundled .NET runtime version (not self-contained?)"
PACKAGES="$(dotnet nuget locals global-packages --list | sed -n 's/^global-packages: *//p' | head -1)"
RUNTIME_PACK="${PACKAGES%/}/microsoft.netcore.app.runtime.$RID/$RUNTIME_VERSION"
mkdir -p "$RESOURCES/licenses/dotnet"
for file in LICENSE.TXT THIRD-PARTY-NOTICES.TXT; do
    [[ -f "$RUNTIME_PACK/$file" ]] || fail ".NET runtime pack $RUNTIME_VERSION has no $file ($RUNTIME_PACK)"
    cp "$RUNTIME_PACK/$file" "$RESOURCES/licenses/dotnet/"
done

echo "==> Info.plist"
# Minimum macOS = the highest deployment target of any packaged Mach-O binary.
MIN_MACOS="$(
    for binary in "$APP/Contents/MacOS"/*; do
        file -b "$binary" | grep -q '^Mach-O' || continue
        otool -l "$binary" | awk '/LC_BUILD_VERSION/ { b = 1 } b && $1 == "minos" { print $2; b = 0 }
                                  /LC_VERSION_MIN_MACOSX/ { v = 1 } v && $1 == "version" { print $2; v = 0 }'
    done | sort -t. -k1,1n -k2,2n -k3,3n | tail -1
)"
[[ -n "$MIN_MACOS" ]] || fail "could not determine the minimum macOS version"
sed -e "s/@EXECUTABLE@/$EXECUTABLE/" -e "s/@VERSION@/$VERSION/g" -e "s/@MIN_MACOS@/$MIN_MACOS/" \
    "$PLIST_TEMPLATE" > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist" >/dev/null

echo "==> Ad-hoc code signature (local use only)"
xattr -cr "$APP"   # codesign rejects bundles carrying Finder info / resource-fork extended attributes
# Everything in Contents/MacOS counts as nested code (managed .dll/.json included), so each file is signed
# first; the main executable is signed last as part of the bundle.
for item in "$APP/Contents/MacOS"/*; do
    [[ "$(basename "$item")" == "$EXECUTABLE" ]] && continue
    codesign --force --sign - --timestamp=none "$item" 2>/dev/null
done
codesign --force --sign - --timestamp=none "$APP" 2>/dev/null || codesign --force --sign - --timestamp=none "$APP"

echo "==> Validate"
codesign --verify --deep --strict "$APP"
file -b "$APP/Contents/MacOS/$EXECUTABLE" | grep -q 'arm64' || fail "executable is not arm64"
file -b "$APP/Contents/MacOS/libbgfx.dylib" | grep -q 'arm64' || fail "libbgfx.dylib is not arm64"
for binary in "$APP/Contents/MacOS"/*; do
    file -b "$binary" | grep -q '^Mach-O' || continue
    # Dependencies (otool -L lines after the first) must be system or loader-relative, never machine paths. A universal
    # binary (liblua54.dylib) adds a "<file> (architecture ...):" header per slice; those name the file, not a dependency.
    if otool -L "$binary" | tail -n +2 | grep -v ':$' | grep -E "$ROOT|/Users/|/private/|/tmp/" >/dev/null; then
        fail "$(basename "$binary") links a machine-specific path"
    fi
done

# Only this script's own output is replaced.
mkdir -p "$DIST"
rm -rf "$FINAL_APP"
ditto "$APP" "$FINAL_APP"
# Not --strict here: when dist/ lives in an iCloud-synced folder, File Provider re-stamps the package's
# Finder "has bundle" flag, which --strict reports as detritus although the signature itself is intact.
codesign --verify --deep "$FINAL_APP" || fail "signature did not survive the copy to dist/"
APP="$FINAL_APP"

echo
echo "Built: dist/$APP_NAME.app"
echo "  version $VERSION, minimum macOS $MIN_MACOS, $(du -sh "$APP" | awk '{print $1}')"
