#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
PUBLISH_DIR=${1:-"$REPO_ROOT/LavaLauncher.Desktop/bin/Release/net10.0/linux-x64/publish"}
PACKAGE_VERSION=${PACKAGE_VERSION:-$(sed -n 's:.*<AppVersion>\\(.*\\)</AppVersion>.*:\\1:p' "$REPO_ROOT/Directory.Build.props" | head -n 1)}
APP_BINARY=${APP_BINARY:-YamLauncher}
STAGING_DIR="$REPO_ROOT/artifacts/linux/deb/staging"
PACKAGE_ROOT="$STAGING_DIR/lavalauncher_${PACKAGE_VERSION}_amd64"

if [ ! -d "$PUBLISH_DIR" ]; then
  echo "Publish directory not found: $PUBLISH_DIR" >&2
  exit 1
fi

rm -rf "$PACKAGE_ROOT"
mkdir -p "$PACKAGE_ROOT/DEBIAN"
mkdir -p "$PACKAGE_ROOT/usr/bin"
mkdir -p "$PACKAGE_ROOT/usr/lib/lavalauncher"
mkdir -p "$PACKAGE_ROOT/usr/share/applications"
mkdir -p "$PACKAGE_ROOT/usr/share/icons/hicolor/scalable/apps"

cp -a "$PUBLISH_DIR"/. "$PACKAGE_ROOT/usr/lib/lavalauncher/"
sed "s/__APP_BINARY__/$APP_BINARY/g" \
  "$REPO_ROOT/packaging/linux/common/lavalauncher-wrapper.sh.in" \
  > "$PACKAGE_ROOT/usr/bin/lavalauncher"
chmod 755 "$PACKAGE_ROOT/usr/bin/lavalauncher"
cp "$REPO_ROOT/packaging/linux/common/lavalauncher.desktop" \
  "$PACKAGE_ROOT/usr/share/applications/lavalauncher.desktop"
cp "$REPO_ROOT/packaging/linux/common/lavalauncher.svg" \
  "$PACKAGE_ROOT/usr/share/icons/hicolor/scalable/apps/lavalauncher.svg"

cat > "$PACKAGE_ROOT/DEBIAN/control" <<EOF
Package: lavalauncher
Version: $PACKAGE_VERSION
Section: games
Priority: optional
Architecture: amd64
Maintainer: Lava Launcher
Description: A simple Minecraft launcher
EOF

dpkg-deb --build "$PACKAGE_ROOT" "$REPO_ROOT/artifacts/linux/deb/lavalauncher_${PACKAGE_VERSION}_amd64.deb"
