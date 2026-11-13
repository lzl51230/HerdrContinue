#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet_bin="${DOTNET:-dotnet}"
runtime="${RUNTIME:-osx-arm64}"
case "$runtime" in osx-arm64|osx-x64) ;; *) echo "Unsupported macOS runtime: $runtime" >&2; exit 2 ;; esac
app="$PWD/dist/Herdr Auto Continue.app"
version=$("$dotnet_bin" msbuild src/HerdrAutoContinue -getProperty:Version -nologo)
mkdir -p dist
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
for project in src/HerdrAutoContinue src/Watchdog.Probe; do
    "$dotnet_bin" publish "$project" -c Release -r "$runtime" --self-contained true -p:PublishTrimmed=false -p:DebugType=None -o "$app/Contents/MacOS"
done
"$dotnet_bin" publish src/Watchdog.Updater -c Release -r "$runtime" --self-contained true -p:DebugType=None -o dist/updater
cp dist/updater/Watchdog.Updater "$app/Contents/MacOS/"
cmp "src/HerdrAutoContinue/obj/Release/native/libAvaloniaNative.dylib" "$app/Contents/MacOS/libAvaloniaNative.dylib"
mv "$app/Contents/MacOS/AvaloniaNative-LICENSE.txt" "$app/Contents/Resources/AvaloniaNative-LICENSE.txt"
cp assets/AppIcon.icns "$app/Contents/Resources/AppIcon.icns"
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>HerdrAutoContinue</string>
<key>CFBundleIdentifier</key><string>local.alex.herdr-auto-continue</string>
<key>CFBundleName</key><string>Herdr Auto Continue</string>
<key>CFBundleDisplayName</key><string>Herdr Auto Continue</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>$version</string>
<key>CFBundleVersion</key><string>$version</string>
<key>CFBundleIconFile</key><string>AppIcon</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSPrincipalClass</key><string>NSApplication</string>
</dict></plist>
PLIST
python3 - "$app" "$version" "$runtime" <<'PYMANIFEST'
import pathlib, json, sys
root = pathlib.Path(sys.argv[1])
files = [str(p.relative_to(root)) for p in root.rglob('*') if p.is_file()]
(root / 'Contents/Resources/update-manifest.json').write_text(json.dumps(dict(Version=sys.argv[2], Runtime=sys.argv[3], Files=files)))
PYMANIFEST
# macOS treats every file in MacOS/ as code, including .NET metadata files.
while IFS= read -r -d '' code_file; do
    codesign --force --sign - "$code_file"
done < <(find "$app/Contents/MacOS" -type f ! -name HerdrAutoContinue -print0)
codesign --force --sign - "$app"
codesign --verify --deep --strict "$app"
ditto -c -k --sequesterRsrc --keepParent "$app" "dist/HerdrAutoContinue-$runtime.zip"

if [[ "${1:-}" == "--install" ]]; then
    target="/Applications/Herdr Auto Continue.app"
    if [[ -e "$target" ]]; then
        backup="$target.backup.$(date +%Y%m%d-%H%M%S)"
        mv "$target" "$backup"
    fi
    ditto "$app" "$target"
    codesign --verify --deep --strict "$target"
    printf 'Installed: %s\n' "$target"
else
    printf 'Built: %s\n' "$app"
fi
