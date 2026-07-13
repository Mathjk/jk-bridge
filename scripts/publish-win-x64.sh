#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS="$ROOT/artifacts"
PUBLISH_ROOT="$ARTIFACTS/publish"
OUT="$PUBLISH_ROOT/win-x64"
STAGE="$PUBLISH_ROOT/.win-x64.$$.tmp"
DELIVERY="$ARTIFACTS/delivery"
ARCHIVE="$ARTIFACTS/JKBridge-win-x64.zip"
ARCHIVE_SHA256="$ARCHIVE.sha256"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

find_dotnet() {
  if [[ -n "${DOTNET:-}" && -x "$DOTNET" ]]; then
    printf '%s\n' "$DOTNET"
    return
  fi

  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    printf '%s\n' "$HOME/.dotnet/dotnet"
    return
  fi

  command -v dotnet 2>/dev/null || true
}

DOTNET_BIN="$(find_dotnet)"
if [[ -z "$DOTNET_BIN" ]]; then
  printf 'error: .NET SDK not found. Set DOTNET=/absolute/path/to/dotnet.\n' >&2
  exit 1
fi
if ! command -v sha256sum >/dev/null 2>&1; then
  printf 'error: sha256sum is required to create the delivery manifest.\n' >&2
  exit 1
fi

cleanup() {
  rm -rf -- "$STAGE"
}
trap cleanup EXIT

rm -rf -- "$OUT" "$DELIVERY"
rm -f -- "$ARCHIVE" "$ARCHIVE_SHA256"
mkdir -p -- "$PUBLISH_ROOT" "$STAGE" "$DELIVERY" "$DELIVERY/docs"
mkdir -p -- "$DELIVERY/docs/images"

printf 'Using .NET SDK %s\n' "$("$DOTNET_BIN" --version)"
"$ROOT/scripts/check-public-release.sh"
"$ROOT/scripts/check-doc-links.sh"
python3 -m unittest discover -s "$ROOT/tests/linux" -p 'test_*.py'
"$DOTNET_BIN" test "$ROOT/JKBridge.slnx" -c Release
"$DOTNET_BIN" publish "$ROOT/src/JKBridge.App/JKBridge.App.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false \
  -p:DebugType=embedded \
  -o "$STAGE"

if [[ ! -s "$STAGE/JKBridge.exe" ]]; then
  printf 'error: publish completed without a non-empty JKBridge.exe.\n' >&2
  exit 1
fi

mv -- "$STAGE" "$OUT"

install -m 0644 "$OUT/JKBridge.exe" "$DELIVERY/JKBridge.exe"
install -m 0755 "$ROOT/scripts/jkbridge-linux.py" "$DELIVERY/jkbridge-linux.py"
install -m 0644 "$ROOT/scripts/setup-windows.ps1" "$DELIVERY/Setup-JKBridge.ps1"
install -m 0644 "$ROOT/scripts/verify-windows.ps1" "$DELIVERY/Verify-JKBridge.ps1"
install -m 0644 "$ROOT/README.md" "$DELIVERY/README.md"
install -m 0644 "$ROOT/README.zh-CN.md" "$DELIVERY/README.zh-CN.md"
install -m 0644 "$ROOT/CONTRIBUTING.md" "$DELIVERY/CONTRIBUTING.md"
install -m 0644 "$ROOT/SECURITY.md" "$DELIVERY/SECURITY.md"
install -m 0644 "$ROOT/LICENSE" "$DELIVERY/LICENSE"
install -m 0644 "$ROOT/CHANGELOG.md" "$DELIVERY/CHANGELOG.md"
install -m 0644 "$ROOT/docs/INSTALLATION.md" "$DELIVERY/docs/INSTALLATION.md"
install -m 0644 "$ROOT/docs/INSTALLATION.zh-CN.md" "$DELIVERY/docs/INSTALLATION.zh-CN.md"
install -m 0644 "$ROOT/docs/TROUBLESHOOTING.md" "$DELIVERY/docs/TROUBLESHOOTING.md"
install -m 0644 "$ROOT/docs/TROUBLESHOOTING.zh-CN.md" "$DELIVERY/docs/TROUBLESHOOTING.zh-CN.md"
install -m 0644 "$ROOT/docs/DEVELOPMENT.md" "$DELIVERY/docs/DEVELOPMENT.md"
install -m 0644 "$ROOT/docs/ARCHITECTURE.md" "$DELIVERY/docs/ARCHITECTURE.md"
install -m 0644 "$ROOT/docs/PAIRING_PROTOCOL.md" "$DELIVERY/docs/PAIRING_PROTOCOL.md"
install -m 0644 "$ROOT/docs/PRODUCT_SPEC.md" "$DELIVERY/docs/PRODUCT_SPEC.md"
install -m 0644 "$ROOT/docs/SECURITY.md" "$DELIVERY/docs/SECURITY.md"
install -m 0644 "$ROOT/docs/WINDOWS_ACCEPTANCE.md" "$DELIVERY/docs/WINDOWS_ACCEPTANCE.md"
install -m 0644 "$ROOT/docs/images/app-icon.svg" "$DELIVERY/docs/images/app-icon.svg"
install -m 0644 "$ROOT/docs/images/device-setup.png" "$DELIVERY/docs/images/device-setup.png"

PAYLOAD_FILES=(
  JKBridge.exe JKBridge.exe.sha256 jkbridge-linux.py
  Setup-JKBridge.ps1 Verify-JKBridge.ps1
  README.md README.zh-CN.md CONTRIBUTING.md SECURITY.md LICENSE CHANGELOG.md
  docs/INSTALLATION.md docs/INSTALLATION.zh-CN.md
  docs/TROUBLESHOOTING.md docs/TROUBLESHOOTING.zh-CN.md
  docs/DEVELOPMENT.md docs/ARCHITECTURE.md docs/PAIRING_PROTOCOL.md
  docs/PRODUCT_SPEC.md docs/SECURITY.md docs/WINDOWS_ACCEPTANCE.md
  docs/images/app-icon.svg docs/images/device-setup.png
)

(
  cd -- "$DELIVERY"
  sha256sum JKBridge.exe > JKBridge.exe.sha256
  sha256sum "${PAYLOAD_FILES[@]}" > SHA256SUMS
  sha256sum --check SHA256SUMS
)

if command -v zip >/dev/null 2>&1; then
  (
    cd -- "$DELIVERY"
    zip -q -9 "$ARCHIVE" "${PAYLOAD_FILES[@]}" SHA256SUMS
  )
  (
    cd -- "$ARTIFACTS"
    sha256sum "$(basename -- "$ARCHIVE")" > "$(basename -- "$ARCHIVE_SHA256")"
  )
  printf 'Windows package: %s\n' "$ARCHIVE"
  printf 'Package SHA-256: %s\n' "$ARCHIVE_SHA256"
else
  printf 'warning: zip is unavailable; delivery directory was created without a ZIP archive.\n' >&2
fi

printf 'Windows build:   %s\n' "$DELIVERY/JKBridge.exe"
printf 'SHA-256 record:  %s\n' "$DELIVERY/JKBridge.exe.sha256"
printf 'Payload manifest: %s\n' "$DELIVERY/SHA256SUMS"
printf 'Delivery folder: %s\n' "$DELIVERY"
