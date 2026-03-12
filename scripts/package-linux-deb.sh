#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
. "$REPO_ROOT/packaging/linux/common/package-vars.sh"

PROJECT_PATH="$REPO_ROOT/LavaLauncher.Desktop/LavaLauncher.Desktop.csproj"
RUNTIME_IDENTIFIER=${RUNTIME_IDENTIFIER:-linux-x64}
PUBLISH_DIR=${PUBLISH_DIR:-"$REPO_ROOT/artifacts/publish/$RUNTIME_IDENTIFIER"}
STAGING_DIR="$REPO_ROOT/artifacts/linux/deb/staging"
PACKAGE_ROOT="$STAGING_DIR/${LINUX_FOLDER_NAME}_${PACKAGE_VERSION}_amd64"

if [ $# -gt 0 ]; then
  if [ -d "$1" ]; then
    PUBLISH_DIR=$1
  else
    RUNTIME_IDENTIFIER=$1
    PUBLISH_DIR="$REPO_ROOT/artifacts/publish/$RUNTIME_IDENTIFIER"
  fi
fi

if [ ! -d "$PUBLISH_DIR" ]; then
  dotnet publish "$PROJECT_PATH" \
    -c Release \
    -r "$RUNTIME_IDENTIFIER" \
    --self-contained true \
    -p:PublishAot=true \
    -p:PublishTrimmed=true \
    -p:PublishSingleFile=true \
    -o "$PUBLISH_DIR"
fi

rm -rf "$PACKAGE_ROOT"
mkdir -p "$PACKAGE_ROOT/DEBIAN"
mkdir -p "$PACKAGE_ROOT/usr/bin"
mkdir -p "$PACKAGE_ROOT/usr/lib/$LINUX_FOLDER_NAME"
mkdir -p "$PACKAGE_ROOT/usr/share/applications"
mkdir -p "$PACKAGE_ROOT/usr/share/icons/hicolor/scalable/apps"

cp -a "$PUBLISH_DIR"/. "$PACKAGE_ROOT/usr/lib/$LINUX_FOLDER_NAME/"
sed \
  -e "s/__APP_BINARY__/$APP_BINARY/g" \
  -e "s/__LINUX_FOLDER_NAME__/$LINUX_FOLDER_NAME/g" \
  "$REPO_ROOT/packaging/linux/common/lavalauncher-wrapper.sh.in" \
  > "$PACKAGE_ROOT/usr/bin/$LINUX_FOLDER_NAME"
chmod 755 "$PACKAGE_ROOT/usr/bin/$LINUX_FOLDER_NAME"
sed \
  -e "s/__LINUX_FOLDER_NAME__/$LINUX_FOLDER_NAME/g" \
  "$REPO_ROOT/packaging/linux/common/lavalauncher.desktop" \
  > "$PACKAGE_ROOT/usr/share/applications/$LINUX_FOLDER_NAME.desktop"
cp "$REPO_ROOT/packaging/common/app-icon.svg" \
  "$PACKAGE_ROOT/usr/share/icons/hicolor/scalable/apps/$LINUX_FOLDER_NAME.svg"

cat > "$PACKAGE_ROOT/DEBIAN/control" <<EOF
Package: $LINUX_FOLDER_NAME
Version: $PACKAGE_VERSION
Section: games
Priority: optional
Architecture: amd64
Maintainer: Lava Launcher
Description: A simple Minecraft launcher
EOF

dpkg-deb --build "$PACKAGE_ROOT" "$REPO_ROOT/artifacts/linux/deb/${LINUX_FOLDER_NAME}_${PACKAGE_VERSION}_amd64.deb"
