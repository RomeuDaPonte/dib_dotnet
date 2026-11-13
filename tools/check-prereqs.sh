#!/usr/bin/env bash
# Phase 0 / step 1 (PLAN.md §10): checks the local tooling needed by the Dib4t .NET rewrite.
#   - .NET 11 SDK (pinned in global.json)
#   - Docker (Testcontainers, mongo:9.0.8 dev DB, legacy mongo:4.1.4)
#   - Playwright browsers for Microsoft.Playwright 1.64.1
#   - Node 16.20.2 (runs the unchanged legacy app)
# Usage: tools/check-prereqs.sh            (exit code = number of missing prerequisites)

set -u
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

PLAYWRIGHT_BROWSERS=(chromium-1248 chromium_headless_shell-1248 firefox-1555 webkit-2370 ffmpeg-1013) # Microsoft.Playwright 1.64.1
NODE_VERSION="v16.20.2"
MONGO_NEW_IMAGE="mongo:9.0.8"

# Fall back to the user-level SDK install if dotnet is not on PATH.
if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$DOTNET_ROOT:$PATH"
  DOTNET_NOT_ON_PATH=1
fi

missing=0
ok()   { printf '  \033[32mOK\033[0m    %s\n' "$1"; }
warn() { printf '  \033[33mWARN\033[0m  %s\n' "$1"; }
fail() { printf '  \033[31mFAIL\033[0m  %s\n' "$1"; missing=$((missing + 1)); }

echo "Dib4t .NET prerequisites"

# .NET SDK
want_sdk="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$REPO_ROOT/global.json")"
if command -v dotnet >/dev/null 2>&1; then
  if (cd "$REPO_ROOT" && dotnet --version >/dev/null 2>&1); then
    ok ".NET SDK $(cd "$REPO_ROOT" && dotnet --version) (global.json wants $want_sdk)"
  else
    fail ".NET SDK $want_sdk required by global.json; installed: $(dotnet --list-sdks | tr '\n' ' ')"
  fi
  [ "${DOTNET_NOT_ON_PATH:-0}" = 1 ] && warn "dotnet found in ~/.dotnet but not on PATH (add DOTNET_ROOT and PATH to ~/.zshrc)"
else
  fail ".NET SDK not found (install $want_sdk: https://dotnet.microsoft.com/download/dotnet/11.0)"
fi

# Docker
if command -v docker >/dev/null 2>&1; then
  if docker info >/dev/null 2>&1; then
    ok "Docker $(docker version --format '{{.Server.Version}}' 2>/dev/null) (daemon running)"
    if docker image inspect "$MONGO_NEW_IMAGE" >/dev/null 2>&1; then
      ok "image $MONGO_NEW_IMAGE present"
    else
      warn "image $MONGO_NEW_IMAGE not pulled yet (docker pull $MONGO_NEW_IMAGE)"
    fi
  else
    fail "Docker CLI found but the daemon is not running (start Docker Desktop)"
  fi
else
  fail "Docker not found (install Docker Desktop for Mac, Apple silicon: https://docs.docker.com/desktop/setup/install/mac-install/)"
fi

# Playwright browsers
pw_dir="${PLAYWRIGHT_BROWSERS_PATH:-$HOME/Library/Caches/ms-playwright}"
pw_missing=()
for b in "${PLAYWRIGHT_BROWSERS[@]}"; do [ -d "$pw_dir/$b" ] || pw_missing+=("$b"); done
if [ ${#pw_missing[@]} -eq 0 ]; then
  ok "Playwright 1.64.1 browsers in $pw_dir"
else
  fail "Playwright browsers missing: ${pw_missing[*]} (after Phase 1 build: pwsh tests/<E2E>/bin/Debug/net11.0/playwright.ps1 install)"
fi

# Node (legacy app)
if command -v node >/dev/null 2>&1; then
  if [ "$(node -v)" = "$NODE_VERSION" ]; then ok "Node $(node -v) (legacy app)"; else warn "Node $(node -v); legacy app expects $NODE_VERSION"; fi
else
  fail "Node $NODE_VERSION not found (needed to run the legacy app)"
fi

echo
if [ "$missing" -eq 0 ]; then echo "All prerequisites present."; else echo "$missing prerequisite(s) missing."; fi
exit "$missing"
