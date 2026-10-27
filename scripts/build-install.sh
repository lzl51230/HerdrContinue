#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."

swift build -c release --product HerdrAutoContinue
app="$PWD/dist/Herdr Auto Continue.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" dist/AppIcon.iconset
cp .build/release/HerdrAutoContinue "$app/Contents/MacOS/HerdrAutoContinue"
swift scripts/MakeIcon.swift "$PWD/dist/AppIcon.iconset"
iconutil -c icns dist/AppIcon.iconset -o "$app/Contents/Resources/AppIcon.icns"
cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>HerdrAutoContinue</string>
<key>CFBundleIdentifier</key><string>local.alex.herdr-auto-continue</string>
<key>CFBundleName</key><string>Herdr Auto Continue</string>
<key>CFBundleDisplayName</key><string>Herdr Auto Continue</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>1.1.0</string>
<key>CFBundleVersion</key><string>2</string>
<key>CFBundleIconFile</key><string>AppIcon</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSPrincipalClass</key><string>NSApplication</string>
</dict></plist>
PLIST
codesign --force --sign - "$app"
codesign --verify --strict "$app"

if [[ "${1:-}" == "--install" ]]; then
    target="/Applications/Herdr Auto Continue.app"
    if [[ -e "$target" ]]; then
        backup="$target.backup.$(date +%Y%m%d-%H%M%S)"
        mv "$target" "$backup"
    fi
    ditto "$app" "$target"
    codesign --verify --strict "$target"
    printf 'Installed: %s\n' "$target"
else
    printf 'Built: %s\n' "$app"
fi
