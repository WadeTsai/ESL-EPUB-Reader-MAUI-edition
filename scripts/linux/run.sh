#!/usr/bin/env bash
# scripts/linux/run.sh — build (if needed) and launch the Linux reader.
# Any arguments are passed to the app, e.g.  scripts/linux/run.sh ~/book.epub
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if ! command -v dotnet >/dev/null && [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
fi
exec dotnet run --project "$REPO_ROOT/src/EslEpubReader.Linux/EslEpubReader.Linux.csproj" -- "$@"
