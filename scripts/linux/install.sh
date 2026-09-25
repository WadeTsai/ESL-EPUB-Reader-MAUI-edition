#!/usr/bin/env bash
# ============================================================================
# scripts/linux/install.sh — publish a self-contained Release build and
# install it for the current user (no sudo):
#
#   ~/.local/lib/eslepubreader/          the published app
#   ~/.local/bin/eslepubreader           launcher symlink
#   ~/.local/share/applications/…desktop menu entry (+ .epub "Open With")
#   ~/.local/share/icons/hicolor/…       app icon
#
# --uninstall removes all of the above.
# ============================================================================
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP_ID="com.wadetsai.eslepubreader"
PREFIX="${XDG_DATA_HOME:-$HOME/.local/share}"
LIB_DIR="$HOME/.local/lib/eslepubreader"
BIN_LINK="$HOME/.local/bin/eslepubreader"
DESKTOP="$PREFIX/applications/$APP_ID.desktop"
ICON="$PREFIX/icons/hicolor/scalable/apps/$APP_ID.svg"

refresh_caches() {
    command -v update-desktop-database >/dev/null && update-desktop-database -q "$PREFIX/applications" || true
    command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$PREFIX/icons/hicolor" || true
}

if [[ "${1:-}" == "--uninstall" ]]; then
    rm -rf "$LIB_DIR" "$BIN_LINK" "$DESKTOP" "$ICON"
    refresh_caches
    echo "Uninstalled."
    exit 0
fi

if ! command -v dotnet >/dev/null && [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
fi

dotnet publish "$REPO_ROOT/src/EslEpubReader.Linux/EslEpubReader.Linux.csproj" -c Release -o "$LIB_DIR.new"
rm -rf "$LIB_DIR" && mv "$LIB_DIR.new" "$LIB_DIR"

mkdir -p "$(dirname "$BIN_LINK")" "$(dirname "$DESKTOP")" "$(dirname "$ICON")"
ln -sf "$LIB_DIR/eslepubreader" "$BIN_LINK"
install -m 644 "$REPO_ROOT/src/EslEpubReader.Linux/Resources/$APP_ID.svg" "$ICON"
sed "s|^Exec=.*|Exec=$LIB_DIR/eslepubreader %f|" \
    "$REPO_ROOT/src/EslEpubReader.Linux/Resources/$APP_ID.desktop" > "$DESKTOP"
refresh_caches

echo "Installed. Launch \"ESL EPUB Reader\" from the app menu, or run: eslepubreader [book.epub]"
