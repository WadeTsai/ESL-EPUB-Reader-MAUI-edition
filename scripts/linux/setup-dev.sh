#!/usr/bin/env bash
# ============================================================================
# scripts/linux/setup-dev.sh — prepare a Linux machine to build and run the
# GTK front end (src/EslEpubReader.Linux).
#
#   1. Native runtime libraries: GTK 4, WebKitGTK 6.0, speech-dispatcher
#      (read-aloud), GStreamer plugins (WebKit media) — via the distro's
#      package manager (apt, dnf, pacman or zypper; needs sudo).
#   2. The .NET 10 SDK — skipped if a 10.x SDK is already on PATH or in
#      ~/.dotnet; otherwise installed per-user into ~/.dotnet with
#      Microsoft's dotnet-install.sh (no sudo), and added to ~/.bashrc.
#   3. A restore + build of the Linux project to prove the setup.
#
# Safe to re-run. Pass --no-system-packages to skip step 1 (e.g. when you
# lack sudo and the libraries are already installed).
# ============================================================================
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$REPO_ROOT/src/EslEpubReader.Linux/EslEpubReader.Linux.csproj"
DOTNET_CHANNEL="10.0"
SKIP_SYSTEM=0
[[ "${1:-}" == "--no-system-packages" ]] && SKIP_SYSTEM=1

say() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }

# ---------------------------------------------------------------- 1. natives
install_system_packages() {
    local sudo=""
    [[ $EUID -ne 0 ]] && sudo="sudo"

    if command -v apt-get >/dev/null; then
        say "Installing GTK 4 / WebKitGTK 6.0 / speech packages (apt)"
        $sudo apt-get update
        $sudo apt-get install -y \
            libgtk-4-1 libwebkitgtk-6.0-4 \
            speech-dispatcher speech-dispatcher-espeak-ng \
            gstreamer1.0-plugins-base gstreamer1.0-plugins-good \
            ca-certificates curl
    elif command -v dnf >/dev/null; then
        say "Installing GTK 4 / WebKitGTK 6.0 / speech packages (dnf)"
        $sudo dnf install -y gtk4 webkitgtk6.0 speech-dispatcher espeak-ng \
            gstreamer1-plugins-base gstreamer1-plugins-good ca-certificates curl
    elif command -v pacman >/dev/null; then
        say "Installing GTK 4 / WebKitGTK 6.0 / speech packages (pacman)"
        $sudo pacman -S --needed --noconfirm gtk4 webkitgtk-6.0 speech-dispatcher espeak-ng \
            gst-plugins-base gst-plugins-good ca-certificates curl
    elif command -v zypper >/dev/null; then
        say "Installing GTK 4 / WebKitGTK 6.0 / speech packages (zypper)"
        $sudo zypper install -y libgtk-4-1 libwebkitgtk-6_0-4 speech-dispatcher espeak-ng \
            gstreamer-plugins-base gstreamer-plugins-good ca-certificates curl
    else
        warn "unknown package manager — install GTK 4, WebKitGTK 6.0 and speech-dispatcher yourself"
    fi
}

if [[ $SKIP_SYSTEM -eq 0 ]]; then
    install_system_packages
else
    say "Skipping system packages (--no-system-packages)"
fi

# ------------------------------------------------------------ 2. .NET 10 SDK
has_dotnet10() { "$1" --list-sdks 2>/dev/null | grep -q "^${DOTNET_CHANNEL%%.*}\."; }

DOTNET=""
if command -v dotnet >/dev/null && has_dotnet10 "$(command -v dotnet)"; then
    DOTNET="$(command -v dotnet)"
elif [[ -x "$HOME/.dotnet/dotnet" ]] && has_dotnet10 "$HOME/.dotnet/dotnet"; then
    DOTNET="$HOME/.dotnet/dotnet"
else
    say "Installing the .NET $DOTNET_CHANNEL SDK into ~/.dotnet"
    tmp="$(mktemp)"
    curl -sSL https://dot.net/v1/dotnet-install.sh -o "$tmp"
    bash "$tmp" --channel "$DOTNET_CHANNEL" --install-dir "$HOME/.dotnet"
    rm -f "$tmp"
    DOTNET="$HOME/.dotnet/dotnet"
fi
say "Using $("$DOTNET" --version) at $DOTNET"

if [[ "$DOTNET" == "$HOME/.dotnet/dotnet" ]] && ! grep -q 'DOTNET_ROOT="$HOME/.dotnet"' "$HOME/.bashrc" 2>/dev/null; then
    say "Adding ~/.dotnet to PATH in ~/.bashrc (open a new terminal afterwards)"
    cat >> "$HOME/.bashrc" <<'RC'

# .NET SDK (user-local install, ~/.dotnet)
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
RC
fi
export DOTNET_ROOT="$(dirname "$DOTNET")"
export PATH="$DOTNET_ROOT:$PATH"

# ------------------------------------------------------------------ 3. build
say "Restoring and building the Linux project"
"$DOTNET" build "$PROJECT" -c Debug

say "Done. Run the app with:  scripts/linux/run.sh [book.epub]"
