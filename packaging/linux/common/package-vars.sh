#!/bin/sh

set -eu

REPO_ROOT=${REPO_ROOT:-$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)}
USER_PROPS_PATH="$REPO_ROOT/user.props"

read_xml_property() {
  file_path=$1
  property_name=$2

  if [ ! -f "$file_path" ]; then
    return 1
  fi

  sed -n "s:.*<$property_name>\\(.*\\)</$property_name>.*:\\1:p" "$file_path" | head -n 1
}

resolve_property() {
  current_value=$1
  property_name=$2
  fallback_value=$3

  if [ -n "$current_value" ]; then
    printf '%s\n' "$current_value"
    return 0
  fi

  user_value=$(read_xml_property "$USER_PROPS_PATH" "$property_name" || true)
  if [ -n "$user_value" ]; then
    printf '%s\n' "$user_value"
    return 0
  fi

  printf '%s\n' "$fallback_value"
}

rid_to_deb_arch() {
  case "$1" in
    linux-x64)   printf 'amd64\n' ;;
    linux-arm64)  printf 'arm64\n' ;;
    linux-arm)    printf 'armhf\n' ;;
    *)
      echo "Unsupported RUNTIME_IDENTIFIER for DEB architecture: $1" >&2
      exit 1
      ;;
  esac
}

rid_to_rpm_arch() {
  case "$1" in
    linux-x64)   printf 'x86_64\n' ;;
    linux-arm64)  printf 'aarch64\n' ;;
    *)
      echo "Unsupported RUNTIME_IDENTIFIER for RPM architecture: $1" >&2
      exit 1
      ;;
  esac
}

PACKAGE_VERSION=${PACKAGE_VERSION:-$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "$REPO_ROOT/Directory.Build.props" | head -n 1)}
APP_NAME=$(resolve_property "${APP_NAME:-}" "AppName" "Yet Another Minecraft Launcher")
APP_BINARY=$(resolve_property "${APP_BINARY:-}" "AppAssemblyName" "YamLauncher")
LINUX_FOLDER_NAME=$(resolve_property "${LINUX_FOLDER_NAME:-}" "LinuxFolderName" "yamlauncher")
