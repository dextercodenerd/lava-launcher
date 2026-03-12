#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
. "$REPO_ROOT/packaging/windows/common/package-vars.sh"

PROJECT_PATH="$REPO_ROOT/LavaLauncher.Desktop/LavaLauncher.Desktop.csproj"
WIX_PROJECT_PATH="$REPO_ROOT/packaging/windows/LavaLauncher.WindowsInstaller.wixproj"
ICON_TOOL_PROJECT="$REPO_ROOT/packaging/windows/Tools/LavaLauncher.WindowsIconTool/LavaLauncher.WindowsIconTool.csproj"
ICON_SOURCE_PATH="$REPO_ROOT/packaging/common/app-icon.svg"
RUNTIME_IDENTIFIER=${RUNTIME_IDENTIFIER:-win-x64}
PUBLISH_DIR=${PUBLISH_DIR:-"$REPO_ROOT/artifacts/publish/$RUNTIME_IDENTIFIER"}
WINDOWS_ROOT="$REPO_ROOT/artifacts/windows"
GENERATED_ASSETS_DIR="$WINDOWS_ROOT/generated"
PACKAGE_OUTPUT_DIR="$WINDOWS_ROOT/msi"
RSVG_CONVERT_BIN=${RSVG_CONVERT_BIN:-$(command -v rsvg-convert || true)}
SUPPRESS_VALIDATION=${SUPPRESS_VALIDATION:-true}
CURRENT_OS=$(uname -s)

case "$CURRENT_OS" in
  CYGWIN*|MINGW*|MSYS*)
    ;;
  *)
    echo "WiX 6 packaging currently requires Windows. Run this script from Windows bash/Git Bash." >&2
    exit 1
    ;;
esac

build_png_icon() {
  icon_size=$1
  output_name=$2
  output_path="$GENERATED_ASSETS_DIR/$output_name"

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
  echo "rsvg-convert is required to generate the Windows app icon from the shared SVG." >&2
  exit 1
fi

rm -rf "$GENERATED_ASSETS_DIR" "$PACKAGE_OUTPUT_DIR"
mkdir -p "$GENERATED_ASSETS_DIR" "$PACKAGE_OUTPUT_DIR"

build_png_icon 16 "app-icon-16.png"
build_png_icon 32 "app-icon-32.png"
build_png_icon 48 "app-icon-48.png"
build_png_icon 64 "app-icon-64.png"
build_png_icon 128 "app-icon-128.png"
build_png_icon 256 "app-icon-256.png"

dotnet run \
  --project "$ICON_TOOL_PROJECT" \
  -- \
  "$GENERATED_ASSETS_DIR/app-icon.ico" \
  "$GENERATED_ASSETS_DIR/app-icon-16.png" \
  "$GENERATED_ASSETS_DIR/app-icon-32.png" \
  "$GENERATED_ASSETS_DIR/app-icon-48.png" \
  "$GENERATED_ASSETS_DIR/app-icon-64.png" \
  "$GENERATED_ASSETS_DIR/app-icon-128.png" \
  "$GENERATED_ASSETS_DIR/app-icon-256.png"

dotnet build "$WIX_PROJECT_PATH" \
  -c Release \
  -p:PublishDir="$PUBLISH_DIR" \
  -p:GeneratedAssetsDir="$GENERATED_ASSETS_DIR" \
  -p:AppName="$APP_NAME" \
  -p:AppAssemblyName="$APP_BINARY" \
  -p:AppVersion="$PACKAGE_VERSION" \
  -p:WindowsFolderName="$WINDOWS_FOLDER_NAME" \
  -p:SuppressValidation="$SUPPRESS_VALIDATION"

OUTPUT_MSI=$(find "$REPO_ROOT/packaging/windows/bin" -type f -name "${WINDOWS_FOLDER_NAME}-${PACKAGE_VERSION}-win-x64.msi" | head -n 1)
if [ -z "$OUTPUT_MSI" ]; then
  echo "Could not find the built MSI in packaging/windows/bin." >&2
  exit 1
fi

cp "$OUTPUT_MSI" "$PACKAGE_OUTPUT_DIR/"

printf '%s\n' "$PACKAGE_OUTPUT_DIR/${WINDOWS_FOLDER_NAME}-${PACKAGE_VERSION}-win-x64.msi"
