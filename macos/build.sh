#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$ROOT/.." && pwd)"
if [[ "$(uname -s)" != Darwin ]]; then
  echo "Build requires macOS 13+ and Xcode Command Line Tools (Swift 5.9+)." >&2
  exit 1
fi
if ! xcrun --find swift >/dev/null 2>&1; then
  echo "Install Xcode Command Line Tools first: xcode-select --install" >&2
  exit 1
fi
VERSION="$(tr -d '\r\n' < "$REPO/VERSION")"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Invalid VERSION" >&2; exit 1; }
OUT="$REPO/dist/macos"
APP="$OUT/Codex Usage Bar.app"
mkdir -p "$OUT"
cd "$ROOT"
# Run core tests natively on the build host; the distributable is always arm64.
xcrun swift test
xcrun swift build -c release --triple arm64-apple-macosx13.0
BIN_DIR="$(xcrun swift build -c release --triple arm64-apple-macosx13.0 --show-bin-path)"
# Only remove our known generated app bundle, never a caller-supplied path.
if [[ -e "$APP" ]]; then
  [[ "$APP" == "$REPO/dist/macos/Codex Usage Bar.app" && ! -L "$APP" && ! -L "$OUT" && ! -L "$REPO/dist" ]] || exit 1
  rm -rf -- "$APP"
fi
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/CodexUsageBar" "$APP/Contents/MacOS/CodexUsageBar"
cp "$ROOT/Info.plist" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP/Contents/Info.plist"
cp "$REPO/installer/package-quota.js" "$APP/Contents/Resources/package-quota.js"
cp "$REPO/LICENSE" "$APP/Contents/Resources/LICENSE"
cp "$REPO/THIRD-PARTY-NOTICES.md" "$APP/Contents/Resources/THIRD-PARTY-NOTICES.md"
ICONSET="$OUT/AppIcon.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$REPO/assets/codex-usage-bar-logo.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$REPO/assets/codex-usage-bar-logo.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
plutil -lint "$APP/Contents/Info.plist"
lipo "$APP/Contents/MacOS/CodexUsageBar" -verify_arch arm64
if [[ -n "${CODE_SIGN_IDENTITY:-}" ]]; then
  codesign --force --options runtime --timestamp --entitlements "$ROOT/Entitlements.plist" --sign "$CODE_SIGN_IDENTITY" "$APP"
else
  codesign --force --entitlements "$ROOT/Entitlements.plist" --sign - "$APP"
fi
codesign --verify --deep --strict "$APP"
if [[ "$(uname -m)" == arm64 ]]; then
  CODEXBAR_TEST_OUTPUT="$OUT/previews" "$APP/Contents/MacOS/CodexUsageBar" --self-test
else
  echo "arm64 app built; runtime self-test requires an Apple Silicon Mac." >&2
  exit 1
fi
ZIP="$OUT/CodexUsageBar-v${VERSION}-macOS-arm64.zip"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
if [[ -n "${NOTARY_PROFILE:-}" ]]; then
  [[ -n "${CODE_SIGN_IDENTITY:-}" ]] || { echo "Notarization requires CODE_SIGN_IDENTITY" >&2; exit 1; }
  xcrun notarytool submit "$ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP"
  ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
fi
shasum -a 256 "$ZIP" > "$ZIP.sha256"
DMG="$OUT/CodexUsageBar-v${VERSION}-macOS-arm64.dmg"
STAGING="$(mktemp -d "$OUT/dmg-stage.XXXXXX")"
trap 'rm -rf -- "$STAGING"' EXIT
ditto "$APP" "$STAGING/Codex Usage Bar.app"
ln -s /Applications "$STAGING/Applications"
cp "$REPO/INSTALL-MACOS.md" "$STAGING/Install-and-Test.md"
hdiutil create -volname "Codex Usage Bar $VERSION" -srcfolder "$STAGING" -format UDZO -ov "$DMG"
if [[ -n "${CODE_SIGN_IDENTITY:-}" ]]; then
  codesign --force --timestamp --sign "$CODE_SIGN_IDENTITY" "$DMG"
  codesign --verify --strict "$DMG"
fi
if [[ -n "${NOTARY_PROFILE:-}" ]]; then
  xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$DMG"
  xcrun stapler validate "$DMG"
fi
hdiutil verify "$DMG"
shasum -a 256 "$DMG" > "$DMG.sha256"
echo "Built and self-tested: $APP"
echo "Archive: $ZIP"
echo "Disk image: $DMG"
