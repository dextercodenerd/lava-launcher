#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
PUBLISH_DIR=${1:-"$REPO_ROOT/LavaLauncher.Desktop/bin/Release/net10.0/linux-x64/publish"}
PACKAGE_VERSION=${PACKAGE_VERSION:-$(sed -n 's:.*<AppVersion>\\(.*\\)</AppVersion>.*:\\1:p' "$REPO_ROOT/Directory.Build.props" | head -n 1)}
APP_BINARY=${APP_BINARY:-YamLauncher}
RPM_ROOT="$REPO_ROOT/artifacts/linux/rpm"
STAGING_DIR="$RPM_ROOT/staging"
BUILDROOT_DIR="$RPM_ROOT/buildroot"
SPEC_FILE="$RPM_ROOT/lavalauncher.spec"

if [ ! -d "$PUBLISH_DIR" ]; then
  echo "Publish directory not found: $PUBLISH_DIR" >&2
  exit 1
fi

rm -rf "$STAGING_DIR" "$BUILDROOT_DIR"
mkdir -p "$STAGING_DIR/usr/bin"
mkdir -p "$STAGING_DIR/usr/lib/lavalauncher"
mkdir -p "$STAGING_DIR/usr/share/applications"
mkdir -p "$STAGING_DIR/usr/share/icons/hicolor/scalable/apps"
mkdir -p "$RPM_ROOT/RPMS" "$RPM_ROOT/SOURCES" "$RPM_ROOT/SPECS" "$RPM_ROOT/SRPMS" "$RPM_ROOT/BUILD"

cp -a "$PUBLISH_DIR"/. "$STAGING_DIR/usr/lib/lavalauncher/"
sed "s/__APP_BINARY__/$APP_BINARY/g" \
  "$REPO_ROOT/packaging/linux/common/lavalauncher-wrapper.sh.in" \
  > "$STAGING_DIR/usr/bin/lavalauncher"
chmod 755 "$STAGING_DIR/usr/bin/lavalauncher"
cp "$REPO_ROOT/packaging/linux/common/lavalauncher.desktop" \
  "$STAGING_DIR/usr/share/applications/lavalauncher.desktop"
cp "$REPO_ROOT/packaging/linux/common/lavalauncher.svg" \
  "$STAGING_DIR/usr/share/icons/hicolor/scalable/apps/lavalauncher.svg"

sed \
  -e "s/@VERSION@/$PACKAGE_VERSION/g" \
  -e "s#@STAGING_DIR@#$STAGING_DIR#g" \
  "$REPO_ROOT/packaging/linux/rpm/lavalauncher.spec.in" \
  > "$SPEC_FILE"

rpmbuild \
  --define "_topdir $RPM_ROOT" \
  --define "_buildrootdir $BUILDROOT_DIR" \
  -bb "$SPEC_FILE"
