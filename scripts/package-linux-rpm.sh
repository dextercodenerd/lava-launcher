#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
. "$REPO_ROOT/packaging/linux/common/package-vars.sh"

PROJECT_PATH="$REPO_ROOT/LavaLauncher.Desktop/LavaLauncher.Desktop.csproj"
RUNTIME_IDENTIFIER=${RUNTIME_IDENTIFIER:-linux-x64}
PUBLISH_DIR=${PUBLISH_DIR:-"$REPO_ROOT/artifacts/publish/$RUNTIME_IDENTIFIER"}
RPM_ROOT="$REPO_ROOT/artifacts/linux/rpm"
STAGING_DIR="$RPM_ROOT/staging"
BUILDROOT_DIR="$RPM_ROOT/buildroot"
SPEC_FILE="$RPM_ROOT/$LINUX_FOLDER_NAME.spec"

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

rm -rf "$STAGING_DIR" "$BUILDROOT_DIR"
mkdir -p "$STAGING_DIR/usr/bin"
mkdir -p "$STAGING_DIR/usr/lib/$LINUX_FOLDER_NAME"
mkdir -p "$STAGING_DIR/usr/share/applications"
mkdir -p "$STAGING_DIR/usr/share/icons/hicolor/scalable/apps"
mkdir -p "$RPM_ROOT/RPMS" "$RPM_ROOT/SOURCES" "$RPM_ROOT/SPECS" "$RPM_ROOT/SRPMS" "$RPM_ROOT/BUILD"

cp -a "$PUBLISH_DIR"/. "$STAGING_DIR/usr/lib/$LINUX_FOLDER_NAME/"
sed \
  -e "s/__APP_BINARY__/$APP_BINARY/g" \
  -e "s/__LINUX_FOLDER_NAME__/$LINUX_FOLDER_NAME/g" \
  "$REPO_ROOT/packaging/linux/common/lavalauncher-wrapper.sh.in" \
  > "$STAGING_DIR/usr/bin/$LINUX_FOLDER_NAME"
chmod 755 "$STAGING_DIR/usr/bin/$LINUX_FOLDER_NAME"
sed \
  -e "s/__LINUX_FOLDER_NAME__/$LINUX_FOLDER_NAME/g" \
  "$REPO_ROOT/packaging/linux/common/lavalauncher.desktop" \
  > "$STAGING_DIR/usr/share/applications/$LINUX_FOLDER_NAME.desktop"
cp "$REPO_ROOT/packaging/common/app-icon.svg" \
  "$STAGING_DIR/usr/share/icons/hicolor/scalable/apps/$LINUX_FOLDER_NAME.svg"

sed \
  -e "s/@VERSION@/$PACKAGE_VERSION/g" \
  -e "s#@STAGING_DIR@#$STAGING_DIR#g" \
  -e "s/@LINUX_FOLDER_NAME@/$LINUX_FOLDER_NAME/g" \
  "$REPO_ROOT/packaging/linux/rpm/lavalauncher.spec.in" \
  > "$SPEC_FILE"

rpmbuild \
  --define "_topdir $RPM_ROOT" \
  --define "_buildrootdir $BUILDROOT_DIR" \
  -bb "$SPEC_FILE"
