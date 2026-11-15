#!/usr/bin/env bash
# Local verification before each commit (PLAN.md §10, Definition of Done). There is no CI: run this, then commit.
#
#   tools/verify.sh            everything: format, build, unit (+ coverage), integration, E2E, Docker image smoke test
#   tools/verify.sh --fast     format, build, unit tests only (no Docker needed)
#   tools/verify.sh --no-image everything except the Docker image
#
# Results (TRX, coverage) go to TestResults/ (git-ignored). Exit code 0 = all green.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

FAST=0; IMAGE=1
for arg in "$@"; do
  case "$arg" in
    --fast) FAST=1 ;;
    --no-image) IMAGE=0 ;;
    -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
fi
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

RESULTS="$REPO_ROOT/TestResults"
SMOKE_CONTAINER="dib4t-verify-smoke"
SMOKE_PORT=3019
START=$SECONDS

step() { printf '\n\033[36m== %s\033[0m\n' "$*"; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }
fail() { printf '\033[31mFAILED: %s\033[0m\n' "$*" >&2; exit 1; }

cleanup() { docker rm -f "$SMOKE_CONTAINER" >/dev/null 2>&1 || true; }
trap cleanup EXIT

rm -rf "$RESULTS"

step "Restore"
dotnet tool restore >/dev/null
dotnet restore Dib4t.slnx -v q >/dev/null

step "Format (.editorconfig)"
dotnet format Dib4t.slnx --no-restore --verify-no-changes || fail "code style; fix with: dotnet format Dib4t.slnx"

step "Build (Release, warnings are errors)"
dotnet build Dib4t.slnx -c Release --no-restore -v q -nologo || fail "build"

run_tests() { # <name> <slnf> [extra args...]
  local name="$1" filter="$2"; shift 2
  step "Tests: $name"
  dotnet test --solution "$filter" -c Release --no-build \
    --results-directory "$RESULTS/$name" --report-xunit-trx "$@" || fail "$name tests"
}

run_tests unit tests/unit.slnf --coverlet --coverlet-output-format cobertura

step "Coverage: Domain, Application, SharedKernel (target 90 %, PLAN.md §11)"
if ls "$RESULTS"/unit/*.cobertura.*.xml >/dev/null 2>&1; then
  dotnet reportgenerator -reports:"$RESULTS/unit/*.cobertura.*.xml" -targetdir:"$RESULTS/coverage" \
    -reporttypes:'TextSummary;Html' -assemblyfilters:'+Dib4t.Domain;+Dib4t.Application;+Dib4t.SharedKernel' \
    -verbosity:Warning
  sed -n '1,16p' "$RESULTS/coverage/Summary.txt"
  echo "HTML report: $RESULTS/coverage/index.html"
fi

if [ "$FAST" = 1 ]; then
  ok "Fast checks green ($((SECONDS - START)) s). Run without --fast before committing."
  exit 0
fi

docker info >/dev/null 2>&1 || fail "Docker is not running (needed by Testcontainers, E2E and the image)"

run_tests integration tests/integration.slnf
run_tests e2e tests/e2e.slnf

if [ "$IMAGE" = 1 ]; then
  step "Docker image (linux/amd64) + smoke test"
  docker build --platform linux/amd64 -q -t dib4t-dotnet:verify . >/dev/null || fail "docker build"
  cleanup
  # No database: the connection string is only validated at startup; the version check logs "not reachable" and retries.
  # Production needs its own signing key (PLAN.md §4.6): a throwaway one per run.
  docker run -d --name "$SMOKE_CONTAINER" --platform linux/amd64 -p "127.0.0.1:$SMOKE_PORT:3010" \
    -e Mongo__ConnectionString='mongodb://127.0.0.1:1/dib4t?retryWrites=false' \
    -e Jwt__Key="verify-smoke-$(openssl rand -hex 24)" dib4t-dotnet:verify >/dev/null
  for _ in $(seq 1 60); do curl -sf -o /dev/null "http://127.0.0.1:$SMOKE_PORT/" && break; sleep 1; done
  check() { # <path> <expected status>
    local code; code=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$SMOKE_PORT$1")
    echo "  $1 -> $code"; [ "$code" = "$2" ] || { docker logs "$SMOKE_CONTAINER" | tail -20; fail "image: $1 returned $code, expected $2"; }
  }
  check / 200
  check /_framework/blazor.web.js 200
  check /api/does-not-exist 404
  check /api/account/me 401
  body=$(curl -s -X POST -H 'content-type: application/json' -d '{}' "http://127.0.0.1:$SMOKE_PORT/api/account/login")
  echo "  POST /api/account/login {} -> $body"
  [ "$body" = '"email" is required' ] || fail "image: login validation answered: $body"
  [ "$(docker exec "$SMOKE_CONTAINER" id -u)" != "0" ] || fail "image: container runs as root"
  DIB4T_LEGACY_ROOT=/nonexistent docker compose -f docker-compose.dev.yml --profile legacy config -q || fail "docker-compose.dev.yml"
  echo "  docker-compose.dev.yml is valid"
fi

ok "All green ($((SECONDS - START)) s). Ready to commit."
