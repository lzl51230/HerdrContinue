#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet_bin="${DOTNET:-dotnet}"
runtime="${RUNTIME:-osx-arm64}"
case "$runtime" in osx-arm64|osx-x64) ;; *) echo "Unsupported macOS runtime: $runtime" >&2; exit 2 ;; esac
app="$PWD/dist/Herdr Auto Continue.app"
mkdir -p dist
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
for project in src/HerdrAutoContinue src/Watchdog.Probe; do
    "$dotnet_bin" publish "$project" -c Release -r "$runtime" --self-contained true -p:PublishTrimmed=false -p:DebugType=None -o "$app/Contents/MacOS"
done
cp assets/AppIcon.icns "$app/Contents/Resources/AppIcon.icns"
cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>HerdrAutoContinue</string>
<key>CFBundleIdentifier</key><string>local.alex.herdr-auto-continue</string>
<key>CFBundleName</key><string>Herdr Auto Continue</string>
<key>CFBundleDisplayName</key><string>Herdr Auto Continue</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>1.2.0</string>
<key>CFBundleVersion</key><string>3</string>
<key>CFBundleIconFile</key><string>AppIcon</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSPrincipalClass</key><string>NSApplication</string>
</dict></plist>
PLIST
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
