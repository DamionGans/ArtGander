#!/usr/bin/env bash
# Packages ArtGander.app into a drag-to-Applications DMG.
# Usage: installers/macos/make-dmg.sh <path/to/ArtGander.app> <version> <output-dir>
#
# Signing: if MACOS_SIGN_IDENTITY is set (a "Developer ID Application: …" identity in the keychain), the app is
# signed with it and, when APPLE_ID / APPLE_TEAM_ID / APPLE_APP_PASSWORD are also set, notarized.
# Otherwise the app gets an ad-hoc signature, which runs after the user allows it once
# (right-click > Open, or System Settings > Privacy & Security > Open Anyway).
set -euo pipefail

APP="$1"
VERSION="$2"
OUT="$3"
NAME="ArtGander"
ENTITLEMENTS="$(cd "$(dirname "$0")/../.." && pwd)/src/ArtGander.App/Platforms/MacCatalyst/Entitlements.plist"

mkdir -p "$OUT"
DMG="$OUT/$NAME-$VERSION-macOS.dmg"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

if [[ -n "${MACOS_SIGN_IDENTITY:-}" ]]; then
  codesign --force --deep --options runtime --timestamp --entitlements "$ENTITLEMENTS" --sign "$MACOS_SIGN_IDENTITY" "$APP"
else
  codesign --force --deep --sign - "$APP"
fi

cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "$NAME" -srcfolder "$STAGE" -ov -format UDZO -fs HFS+ "$DMG"

if [[ -n "${MACOS_SIGN_IDENTITY:-}" && -n "${APPLE_ID:-}" && -n "${APPLE_TEAM_ID:-}" && -n "${APPLE_APP_PASSWORD:-}" ]]; then
  codesign --sign "$MACOS_SIGN_IDENTITY" --timestamp "$DMG"
  xcrun notarytool submit "$DMG" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" --wait
  xcrun stapler staple "$DMG"
fi

echo "Created $DMG"
