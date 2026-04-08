#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(CDPATH= cd -- "${SCRIPT_DIR}/../.." && pwd)"

PUBLISH_DIR="${1:-${REPO_ROOT}/publish/linux-x64}"
OUTPUT_DIR="${2:-${REPO_ROOT}/artifacts/appimage}"
VERSION="${AIYOPERPS_APPIMAGE_VERSION:-$(date +%Y.%m.%d)}"
APP_NAME="AiyoPerps"
APP_ID="app.aiyo.perps"
APPDIR_PATH="${OUTPUT_DIR}/${APP_NAME}.AppDir"
APPIMAGE_PATH="${OUTPUT_DIR}/${APP_NAME}-x86_64.AppImage"
APPIMAGETOOL="${APPIMAGETOOL:-}"

if [[ "${OSTYPE:-}" != linux* ]]; then
    echo "This script must run on Linux." >&2
    exit 1
fi

if [[ ! -d "${PUBLISH_DIR}" ]]; then
    echo "Publish directory not found: ${PUBLISH_DIR}" >&2
    exit 1
fi

if [[ ! -f "${PUBLISH_DIR}/${APP_NAME}" ]]; then
    echo "Published executable not found: ${PUBLISH_DIR}/${APP_NAME}" >&2
    exit 1
fi

if [[ -z "${APPIMAGETOOL}" ]]; then
    if command -v appimagetool >/dev/null 2>&1; then
        APPIMAGETOOL="$(command -v appimagetool)"
    elif [[ -x "${REPO_ROOT}/tools/appimagetool-x86_64.AppImage" ]]; then
        APPIMAGETOOL="${REPO_ROOT}/tools/appimagetool-x86_64.AppImage"
    else
        cat >&2 <<'EOF'
appimagetool was not found.

Provide one of these:
1. Install appimagetool into PATH
2. Put appimagetool at ./tools/appimagetool-x86_64.AppImage
3. Export APPIMAGETOOL=/absolute/path/to/appimagetool
EOF
        exit 1
    fi
fi

if [[ ! -x "${APPIMAGETOOL}" ]]; then
    chmod 0755 "${APPIMAGETOOL}"
fi

mkdir -p "${OUTPUT_DIR}"
rm -rf "${APPDIR_PATH}"
mkdir -p "${APPDIR_PATH}/usr/bin"
mkdir -p "${APPDIR_PATH}/usr/share/applications"
mkdir -p "${APPDIR_PATH}/usr/share/metainfo"

cp -a "${PUBLISH_DIR}/." "${APPDIR_PATH}/usr/bin/"
install -m 0755 "${REPO_ROOT}/packaging/linux/appimage/AppRun" "${APPDIR_PATH}/AppRun"
install -m 0644 "${REPO_ROOT}/packaging/linux/appimage/aiyoperps.desktop" "${APPDIR_PATH}/${APP_ID}.desktop"
install -m 0644 "${REPO_ROOT}/packaging/linux/appimage/aiyoperps.desktop" "${APPDIR_PATH}/usr/share/applications/${APP_ID}.desktop"
install -m 0644 "${REPO_ROOT}/packaging/linux/appimage/aiyoperps.appdata.xml" "${APPDIR_PATH}/usr/share/metainfo/${APP_ID}.appdata.xml"
install -m 0644 "${REPO_ROOT}/AiyoPerps/Assets/logo.png" "${APPDIR_PATH}/aiyoperps.png"
cp -f "${APPDIR_PATH}/aiyoperps.png" "${APPDIR_PATH}/.DirIcon"

chmod 0755 "${APPDIR_PATH}/usr/bin/${APP_NAME}"

cat <<EOF
Prepared AppDir: ${APPDIR_PATH}
Using publish input: ${PUBLISH_DIR}
Using appimagetool: ${APPIMAGETOOL}
Output AppImage: ${APPIMAGE_PATH}
EOF

if command -v ldd >/dev/null 2>&1; then
    echo "Dependency check (host-side, informative only):"
    if ! ldd "${APPDIR_PATH}/usr/bin/${APP_NAME}" | grep "not found"; then
        echo "  no missing shared libraries reported by ldd on this host"
    fi
fi

ARCH=x86_64 "${APPIMAGETOOL}" "${APPDIR_PATH}" "${APPIMAGE_PATH}"
chmod 0755 "${APPIMAGE_PATH}"

echo "Created AppImage: ${APPIMAGE_PATH}"
