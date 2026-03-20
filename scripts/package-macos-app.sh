#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
. "$REPO_ROOT/packaging/macos/common/package-vars.sh"

PROJECT_PATH="$REPO_ROOT/LavaLauncher.Desktop/LavaLauncher.Desktop.csproj"
RUNTIME_IDENTIFIER=${RUNTIME_IDENTIFIER:-osx-arm64}
PUBLISH_DIR=${PUBLISH_DIR:-"$REPO_ROOT/artifacts/publish/$RUNTIME_IDENTIFIER"}
MACOS_ROOT="$REPO_ROOT/artifacts/macos"
APP_BUNDLE_PATH="$MACOS_ROOT/$APP_NAME.app"
CONTENTS_PATH="$APP_BUNDLE_PATH/Contents"
MACOS_CONTENTS_PATH="$CONTENTS_PATH/MacOS"
RESOURCES_PATH="$CONTENTS_PATH/Resources"
ICONSET_PATH="$MACOS_ROOT/app-icon.iconset"
ICON_SOURCE_PATH="$REPO_ROOT/packaging/common/app-icon.svg"
PLIST_TEMPLATE_PATH="$REPO_ROOT/packaging/macos/common/Info.plist.in"
RSVG_CONVERT_BIN=${RSVG_CONVERT_BIN:-$(command -v rsvg-convert || true)}
ICONUTIL_BIN=${ICONUTIL_BIN:-$(command -v iconutil || true)}

build_icon() {
  icon_size=$1
  output_name=$2
  output_path="$ICONSET_PATH/$output_name"

  "$RSVG_CONVERT_BIN" \
    --width "$icon_size" \
    --height "$icon_size" \
    "$ICON_SOURCE_PATH" \
    --output "$output_path"
}

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

if [ ! -f "$ICON_SOURCE_PATH" ]; then
  echo "Shared icon not found: $ICON_SOURCE_PATH" >&2
  exit 1
fi

if [ -z "$RSVG_CONVERT_BIN" ]; then
  echo "rsvg-convert is required to generate the macOS app icon from the shared SVG." >&2
  exit 1
fi

if [ -z "$ICONUTIL_BIN" ]; then
  echo "iconutil is required to build the macOS .icns bundle icon." >&2
  exit 1
fi

rm -rf "$APP_BUNDLE_PATH" "$ICONSET_PATH"
mkdir -p "$MACOS_CONTENTS_PATH" "$RESOURCES_PATH" "$ICONSET_PATH"

cp -a "$PUBLISH_DIR"/. "$MACOS_CONTENTS_PATH/"
chmod 755 "$MACOS_CONTENTS_PATH/$APP_BINARY"

build_icon 16 "icon_16x16.png"
build_icon 32 "icon_16x16@2x.png"
build_icon 32 "icon_32x32.png"
build_icon 64 "icon_32x32@2x.png"
build_icon 128 "icon_128x128.png"
build_icon 256 "icon_128x128@2x.png"
build_icon 256 "icon_256x256.png"
build_icon 512 "icon_256x256@2x.png"
build_icon 512 "icon_512x512.png"
build_icon 1024 "icon_512x512@2x.png"

"$ICONUTIL_BIN" -c icns "$ICONSET_PATH" -o "$RESOURCES_PATH/app-icon.icns"

sed \
  -e "s/__APP_NAME__/$APP_NAME/g" \
  -e "s/__APP_BINARY__/$APP_BINARY/g" \
  -e "s/__MAC_BUNDLE_IDENTIFIER__/$MAC_BUNDLE_IDENTIFIER/g" \
  -e "s/__PACKAGE_VERSION__/$PACKAGE_VERSION/g" \
  "$PLIST_TEMPLATE_PATH" \
  > "$CONTENTS_PATH/Info.plist"

rm -rf "$ICONSET_PATH"

printf '%s\n' "$APP_BUNDLE_PATH"
