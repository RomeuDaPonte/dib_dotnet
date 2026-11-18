#!/usr/bin/env bash
# Phase 0 (PLAN.md §10): runs the UNCHANGED legacy Dib4t app locally, for manual comparison and for
# the E2E suite (legacy comparison, §11).
#
#   mongo:4.1.4 (Docker, linux/amd64)  127.0.0.1:27017   legacy database "dib4t"
#   back-end   (Node 16, NODE_ENV unset)  :3001           ../back-end
#   front-end  (react-scripts start)      :3000           ../front-end
#
# The ports are fixed by the legacy code, which is not changed (C10):
#   - back-end/config/default.json: dev DB "mongodb://localhost:27017/dib4t", devPort 3001
#   - front-end/src/_constants/url.js: API = page origin with the last char replaced by "1"
#
# Usage: tools/legacy/legacy.sh <command>
#   up [--fresh]         start mongo + back-end + front-end and wait until all answer
#                        (--fresh: drop the legacy DB volume first; the back-end then seeds the
#                         default admin user and default prices on startup)
#   down [--purge]       stop everything (--purge: also delete the DB volume)
#   status               show what is running
#   restore <file>       restore a .dibbak into the legacy DB through the legacy API
#                        (POST /api/database/restore); the back-end must be up
#   backup <file>        download a .dibbak from the legacy API (GET /api/database/backup)
#   token                print a settings-capable JWT for the legacy API (dev only)
#   seed                 fill a FRESH legacy DB with synthetic data through the legacy API
#                        (seed-legacy.cs; users get the password "layout-test")
#   characterize         record the characterization golden files (characterize.cs) on a FRESH
#                        legacy DB → tests/fixtures/legacy/characterization/ (PLAN.md §11.2)
#   ssh-golden           write the NAS SSH command golden files (ssh-golden.js) by running the
#                        unchanged legacy routes with a recording SSH client on a virtual NAS, in a
#                        throwaway DB "dib4t_ssh_golden" → tests/fixtures/legacy/ssh/ (PLAN.md §5.1)
#   auth-golden          write the login contract golden file (auth-golden.js): the unchanged legacy Joi
#                        login validation, isemail, bcryptjs and jsonwebtoken, run in Node (nothing is
#                        started) → tests/fixtures/legacy/auth/ (PLAN.md §4.6)
#   settings-golden      write the users/prices/payment-method/printer contract golden file
#                        (settings-golden.js): the unchanged legacy Joi validations and Mongoose models run in
#                        Node, offline (nothing is started) → tests/fixtures/legacy/settings/ (PLAN.md §6, §7.2)
#   entity-golden        write the entity contract golden file (entity-golden.js): the unchanged legacy Joi
#                        validation, Mongoose entity model and search RegExps run in Node, offline (nothing is
#                        started) → tests/fixtures/legacy/entities/ (PLAN.md §6, §7.2)
#   budget-golden        write the budget list contract golden file (budget-golden.js): the unchanged legacy
#                        Joi validations, repository queries, numbering and Budget/Task models run in Node,
#                        offline (nothing is started, SSH replaced by a recorder) → tests/fixtures/legacy/budgets/
#                        (PLAN.md §6, §7.1)
#   budget-details-golden  write the budget details contract golden file (budget-details-golden.js): the
#                        unchanged legacy task routes, patchProperty and import handlers run in Node with an
#                        in-memory store, offline (nothing is started) → tests/fixtures/legacy/budget-details/
#                        (PLAN.md §6, §7.1)
#   job-golden           write the job create contract golden file (job-golden.js): the unchanged legacy
#                        POST /api/job handler, Joi validation, Job/Budget models and the label templates run in
#                        Node with an in-memory store, offline (nothing is started, SSH and telnet replaced by
#                        recorders) → tests/fixtures/legacy/jobs/ (PLAN.md §6, §7.3)
#   pdf-samples          write reference budget PDFs (pdf-samples.js) by running the unchanged legacy
#                        PDF route on the anonymized production fixture, in a throwaway DB
#                        "dib4t_pdf_samples" → tests/fixtures/legacy/pdf/ (PLAN.md §7.1)
#   pdf-golden           write the budget PDF golden files (pdf-golden.js): the unchanged generatePdf laid out
#                        by the legacy pdfmake bundle, offline, for the samples, synthetic budgets and every
#                        budget of the anonymized fixture → tests/fixtures/legacy/pdf-layout/ and the embedded
#                        assets (fonts, logo, Unicode tables) → src/Dib4t.Infrastructure/Pdf/Assets/ (PLAN.md §7.1)
#   layout [options]     run the §8.4 layout matrix against the legacy UI (layout-matrix.cs)
#   logs [backend|frontend|mongo]
#
# Environment overrides:
#   DIB4T_LEGACY_ROOT   folder holding back-end/ and front-end/ (default: ../ of dib4t-dotnet)
#   LEGACY_NO_FRONTEND=1  start only mongo + back-end (API-only tests)
#   LEGACY_MONGO_VOLUME  Docker volume of the legacy DB (default dib4t-legacy-db; the E2E harness uses
#                        dib4t-legacy-e2e so a test run never touches the dev copy of the data)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
LEGACY_ROOT="${DIB4T_LEGACY_ROOT:-$(cd "$REPO_ROOT/.." && pwd)}"
BACKEND_DIR="$LEGACY_ROOT/back-end"
FRONTEND_DIR="$LEGACY_ROOT/front-end"
RUN_DIR="$SCRIPT_DIR/.run"            # pid files + logs (git-ignored)

MONGO_IMAGE="mongo:4.1.4"
MONGO_CONTAINER="dib4t-legacy-mongo"
MONGO_VOLUME="${LEGACY_MONGO_VOLUME:-dib4t-legacy-db}"
MONGO_PORT=27017
BACKEND_PORT=3001
FRONTEND_PORT=3000
NODE_VERSION="v16.20.2"

log()  { printf '\033[36m[legacy]\033[0m %s\n' "$*"; }
die()  { printf '\033[31m[legacy] %s\033[0m\n' "$*" >&2; exit 1; }

port_open() { nc -z 127.0.0.1 "$1" >/dev/null 2>&1; }
pid_alive() { [ -f "$1" ] && kill -0 "$(cat "$1")" 2>/dev/null; }

wait_for() { # <description> <timeout seconds> <command...>
  local what="$1" timeout="$2"; shift 2
  local start=$SECONDS
  until "$@" >/dev/null 2>&1; do
    (( SECONDS - start >= timeout )) && die "timeout after ${timeout}s waiting for $what"
    sleep 1
  done
  log "$what is up ($((SECONDS - start))s)"
}

check_prereqs() {
  [ -d "$BACKEND_DIR" ] || die "legacy back-end not found at $BACKEND_DIR (set DIB4T_LEGACY_ROOT)"
  [ -d "$FRONTEND_DIR" ] || die "legacy front-end not found at $FRONTEND_DIR (set DIB4T_LEGACY_ROOT)"
  command -v docker >/dev/null || die "docker not found"
  docker info >/dev/null 2>&1 || die "Docker daemon not running"
  command -v node >/dev/null || die "node not found (need $NODE_VERSION)"
  [ "$(node -v)" = "$NODE_VERSION" ] || log "WARNING: node $(node -v); legacy app expects $NODE_VERSION"
}

ensure_node_modules() { # <dir>
  if [ ! -d "$1/node_modules" ]; then
    log "installing dependencies in $1 (npm ci)"
    (cd "$1" && npm ci --no-audit --no-fund)
  fi
}

mongo_ping() {
  docker exec "$MONGO_CONTAINER" mongo --quiet --eval 'db.adminCommand({ping:1}).ok' 2>/dev/null | grep -q '^1$'
}

backend_up() { # any HTTP answer from the legacy API (401 without token is fine)
  curl -s -o /dev/null -w '%{http_code}' "http://localhost:$BACKEND_PORT/api/prices" | grep -qE '^[1-5][0-9][0-9]$'
}

backend_db_ready() { # the seeded admin user exists once the back-end connected and seeded
  docker exec "$MONGO_CONTAINER" mongo dib4t --quiet \
    --eval 'db.users.countDocuments({email:"userlogado@gmail.com"})' 2>/dev/null | grep -qE '^[1-9]'
}

frontend_up() {
  curl -s -o /dev/null -w '%{http_code}' "http://localhost:$FRONTEND_PORT/" | grep -q '^200$'
}

start_mongo() {
  if [ "$(docker inspect -f '{{.State.Running}}' "$MONGO_CONTAINER" 2>/dev/null)" = "true" ]; then
    log "mongo already running ($MONGO_CONTAINER)"
    return
  fi
  docker rm -f "$MONGO_CONTAINER" >/dev/null 2>&1 || true
  port_open "$MONGO_PORT" && die "port $MONGO_PORT is taken by another process; the legacy back-end needs it (hard-coded)"
  log "starting $MONGO_IMAGE on 127.0.0.1:$MONGO_PORT (volume $MONGO_VOLUME)"
  docker run -d --name "$MONGO_CONTAINER" --platform linux/amd64 \
    -p "127.0.0.1:$MONGO_PORT:27017" -v "$MONGO_VOLUME:/data/db" \
    "$MONGO_IMAGE" --bind_ip_all >/dev/null
  wait_for "mongo $MONGO_IMAGE" 120 mongo_ping
}

start_backend() {
  if pid_alive "$RUN_DIR/backend.pid"; then log "back-end already running"; return; fi
  port_open "$BACKEND_PORT" && die "port $BACKEND_PORT is taken by another process"
  ensure_node_modules "$BACKEND_DIR"
  log "starting legacy back-end on :$BACKEND_PORT"
  # NODE_ENV must NOT be "prod": dev DB (localhost:27017), dev port, no SSH/NAS, no printing.
  # The redirections apply to the whole background job: a forked shell must not keep the caller's stdout/stderr open
  # (a caller reading them, like the E2E harness, would otherwise wait forever).
  (cd "$BACKEND_DIR" && exec env -u NODE_ENV nohup node index.js) </dev/null >"$RUN_DIR/backend.log" 2>&1 &
  echo $! >"$RUN_DIR/backend.pid"
  wait_for "legacy back-end" 60 backend_up
  wait_for "legacy DB seed (admin user)" 30 backend_db_ready
}

start_frontend() {
  if pid_alive "$RUN_DIR/frontend.pid"; then log "front-end already running"; return; fi
  port_open "$FRONTEND_PORT" && die "port $FRONTEND_PORT is taken by another process"
  ensure_node_modules "$FRONTEND_DIR"
  log "starting legacy front-end on :$FRONTEND_PORT (first compile takes a while)"
  (cd "$FRONTEND_DIR" && BROWSER=none PORT="$FRONTEND_PORT" CI=true exec nohup npx react-scripts start) \
    </dev/null >"$RUN_DIR/frontend.log" 2>&1 &
  echo $! >"$RUN_DIR/frontend.pid"
  wait_for "legacy front-end" 300 frontend_up
}

tree_pids() { # <pid>: the pid and all its descendants (npx -> react-scripts -> start.js)
  local child
  for child in $(pgrep -P "$1" 2>/dev/null); do tree_pids "$child"; done
  echo "$1"
}

stop_pid() { # <name>
  local f="$RUN_DIR/$1.pid"
  if pid_alive "$f"; then
    local pids; pids="$(tree_pids "$(cat "$f")")"
    # shellcheck disable=SC2086
    kill -TERM $pids 2>/dev/null || true
    for _ in $(seq 1 20); do
      local alive=0 p
      for p in $pids; do kill -0 "$p" 2>/dev/null && alive=1; done
      [ "$alive" = 0 ] && break
      sleep 0.5
    done
    # shellcheck disable=SC2086
    kill -KILL $pids 2>/dev/null || true
    log "stopped $1"
  fi
  rm -f "$f"
}

stop_mongo() {
  docker inspect "$MONGO_CONTAINER" >/dev/null 2>&1 || return 0
  # Clean shutdown first: a killed mongod leaves stale collection counts (count() returns 0).
  docker stop -t 60 "$MONGO_CONTAINER" >/dev/null 2>&1 || true
  docker rm -f "$MONGO_CONTAINER" >/dev/null 2>&1 || true
  log "stopped mongo"
}

# Settings-capable token for the legacy API. The key and role name are read from the legacy code at
# runtime, so no legacy secret is copied into this repo.
legacy_token() {
  (cd "$BACKEND_DIR" && node -e '
    const jwt = require("jsonwebtoken");
    const { jwtPrivateKey } = require("./config/default.json");
    const { roleName } = require("./helpers/roleName");
    process.stdout.write(jwt.sign({ name: "legacy.sh", email: "legacy.sh@local",
      roles: Object.values(roleName), fieldOfCompetencies: [] }, jwtPrivateKey));')
}

cmd_up() {
  local fresh=0
  [ "${1:-}" = "--fresh" ] && fresh=1
  check_prereqs
  mkdir -p "$RUN_DIR"
  if [ "$fresh" = 1 ]; then
    cmd_down >/dev/null
    docker volume rm -f "$MONGO_VOLUME" >/dev/null 2>&1 || true
    log "legacy DB volume dropped"
  fi
  start_mongo
  start_backend
  [ "${LEGACY_NO_FRONTEND:-0}" = 1 ] || start_frontend
  log "ready: front-end http://localhost:$FRONTEND_PORT  API http://localhost:$BACKEND_PORT/api  DB mongodb://localhost:$MONGO_PORT/dib4t"
}

cmd_down() {
  stop_pid frontend
  stop_pid backend
  stop_mongo
  if [ "${1:-}" = "--purge" ]; then
    docker volume rm -f "$MONGO_VOLUME" >/dev/null 2>&1 || true
    log "legacy DB volume deleted"
  fi
}

cmd_status() {
  local s
  s="$(docker inspect -f '{{.State.Status}}' "$MONGO_CONTAINER" 2>/dev/null)" || s="absent"
  echo "mongo     $MONGO_IMAGE  $s"
  pid_alive "$RUN_DIR/backend.pid"  && echo "back-end  :$BACKEND_PORT  running (pid $(cat "$RUN_DIR/backend.pid"))"  || echo "back-end  :$BACKEND_PORT  stopped"
  pid_alive "$RUN_DIR/frontend.pid" && echo "front-end :$FRONTEND_PORT  running (pid $(cat "$RUN_DIR/frontend.pid"))" || echo "front-end :$FRONTEND_PORT  stopped"
}

cmd_restore() {
  local file="${1:-}"
  [ -f "$file" ] || die "usage: legacy.sh restore <file.dibbak>"
  backend_up || die "legacy back-end is not running (legacy.sh up)"
  log "restoring $(basename "$file") into the legacy DB"
  local out code
  out="$(curl -sS -w $'\n%{http_code}' -X POST "http://localhost:$BACKEND_PORT/api/database/restore" \
    -H "x-auth-token: $(legacy_token)" -H 'Content-Type: application/octet-stream' \
    --data-binary "@$file")"
  code="${out##*$'\n'}"; out="${out%$'\n'*}"
  [ "$code" = 200 ] || die "restore failed (HTTP $code): $out"
  log "restore ok: $out"
}

cmd_backup() {
  local file="${1:-}"
  [ -n "$file" ] || die "usage: legacy.sh backup <file.dibbak>"
  backend_up || die "legacy back-end is not running (legacy.sh up)"
  curl -sS -f -o "$file" -H "x-auth-token: $(legacy_token)" "http://localhost:$BACKEND_PORT/api/database/backup" \
    || die "backup failed"
  log "backup written to $file ($(wc -c <"$file" | tr -d ' ') bytes)"
}

dotnet_env() { # .NET SDK from PATH or the user-level install
  if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
    export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
  fi
  command -v dotnet >/dev/null || die "dotnet not found"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
}

cmd_seed() {
  backend_up || die "legacy back-end is not running (legacy.sh up --fresh)"
  local users
  users="$(docker exec "$MONGO_CONTAINER" mongo dib4t --quiet --eval 'db.users.countDocuments({})')"
  [ "$users" = 1 ] || die "seed expects a fresh DB (found $users users); run: legacy.sh up --fresh"
  dotnet_env
  log "seeding the legacy DB through the legacy API"
  (cd "$REPO_ROOT" && LEGACY_API="http://localhost:$BACKEND_PORT/api" LEGACY_TOKEN="$(legacy_token)" \
    dotnet run "$SCRIPT_DIR/seed-legacy.cs")
}

cmd_characterize() {
  backend_up || die "legacy back-end is not running (LEGACY_NO_FRONTEND=1 legacy.sh up --fresh)"
  local users
  users="$(docker exec "$MONGO_CONTAINER" mongo dib4t --quiet --eval 'db.users.countDocuments({})')"
  [ "$users" = 1 ] || die "characterize expects a fresh DB (found $users users); run: legacy.sh up --fresh"
  dotnet_env
  log "recording characterization fixtures from the legacy API"
  (cd "$REPO_ROOT" && LEGACY_API="http://localhost:$BACKEND_PORT/api" LEGACY_TOKEN="$(legacy_token)" \
    CHARACTERIZATION_OUT="${CHARACTERIZATION_OUT:-tests/fixtures/legacy/characterization}" \
    dotnet run "$SCRIPT_DIR/characterize.cs")
}

cmd_auth_golden() {
  command -v node >/dev/null || die "node not found (need $NODE_VERSION)"
  [ -d "$BACKEND_DIR/node_modules" ] || die "legacy back-end dependencies missing ($BACKEND_DIR/node_modules)"
  (cd "$REPO_ROOT" && DIB4T_LEGACY_ROOT="$LEGACY_ROOT" node "$SCRIPT_DIR/auth-golden.js")
}

cmd_settings_golden() {
  ensure_node_modules "$BACKEND_DIR"
  log "writing the settings contract golden file from the legacy code (offline)"
  (cd "$REPO_ROOT" && DIB4T_LEGACY_ROOT="$LEGACY_ROOT" node "$SCRIPT_DIR/settings-golden.js")
}

cmd_budget_golden() {
  ensure_node_modules "$BACKEND_DIR"
  log "writing the budget contract golden file from the legacy code (offline)"
  (cd "$REPO_ROOT" && DIB4T_LEGACY_ROOT="$LEGACY_ROOT" node "$SCRIPT_DIR/budget-golden.js")
}

cmd_budget_details_golden() {
  ensure_node_modules "$BACKEND_DIR"
  log "writing the budget details contract golden file from the legacy code (offline)"
  (cd "$REPO_ROOT" && DIB4T_LEGACY_ROOT="$LEGACY_ROOT" node "$SCRIPT_DIR/budget-details-golden.js")
}

cmd_job_golden() {
  ensure_node_modules "$BACKEND_DIR"
  log "writing the job create contract golden file from the legacy code (offline)"
  (cd "$REPO_ROOT" && DIB4T_LEGACY_ROOT="$LEGACY_ROOT" node "$SCRIPT_DIR/job-golden.js")
}

cmd_pdf_golden() {
  ensure_node_modules "$BACKEND_DIR"
  log "writing the budget PDF golden files and assets from the legacy pdfmake (offline, about 90 s)"
  (cd "$REPO_ROOT" && DIB4T_LEGACY_ROOT="$LEGACY_ROOT" node "$SCRIPT_DIR/pdf-golden.js")
}

cmd_entity_golden() {
  ensure_node_modules "$BACKEND_DIR"
  log "writing the entity contract golden file from the legacy code (offline)"
  (cd "$REPO_ROOT" && DIB4T_LEGACY_ROOT="$LEGACY_ROOT" node "$SCRIPT_DIR/entity-golden.js")
}

cmd_ssh_golden() {
  local started=0
  if ! mongo_ping; then
    check_prereqs
    start_mongo
    started=1
    trap stop_mongo EXIT # leave mongo as it was found, also on failure
    wait_for "mongo" 60 mongo_ping
  fi
  ensure_node_modules "$BACKEND_DIR"
  log "recording SSH command golden files from the legacy code (no NAS access)"
  (cd "$REPO_ROOT" && DIB4T_LEGACY_ROOT="$LEGACY_ROOT" node "$SCRIPT_DIR/ssh-golden.js" \
    "${SSH_GOLDEN_OUT:-tests/fixtures/legacy/ssh}")
}

cmd_pdf_samples() {
  local started=0
  if ! mongo_ping; then
    check_prereqs
    start_mongo
    started=1
    trap stop_mongo EXIT # leave mongo as it was found, also on failure
    wait_for "mongo" 60 mongo_ping
  fi
  ensure_node_modules "$BACKEND_DIR"
  log "generating reference PDFs with the legacy code (anonymized fixture, throwaway DB)"
  (cd "$REPO_ROOT" && DIB4T_LEGACY_ROOT="$LEGACY_ROOT" node "$SCRIPT_DIR/pdf-samples.js" \
    "${PDF_SAMPLES_OUT:-tests/fixtures/legacy/pdf}")
}

cmd_layout() {
  backend_up && frontend_up || die "legacy app is not fully running (legacy.sh up)"
  dotnet_env
  (cd "$REPO_ROOT" && LEGACY_UI="http://localhost:$FRONTEND_PORT" LEGACY_API="http://localhost:$BACKEND_PORT/api" \
    dotnet run "$SCRIPT_DIR/layout-matrix.cs" -- "$@")
}

cmd_logs() {
  case "${1:-backend}" in
    backend|frontend) tail -n 100 -f "$RUN_DIR/$1.log" ;;
    mongo) docker logs -f --tail 100 "$MONGO_CONTAINER" ;;
    *) die "logs: backend | frontend | mongo" ;;
  esac
}

case "${1:-}" in
  up)      shift; cmd_up "$@" ;;
  down)    shift; cmd_down "$@" ;;
  status)  cmd_status ;;
  restore) shift; cmd_restore "$@" ;;
  backup)  shift; cmd_backup "$@" ;;
  seed)    cmd_seed ;;
  characterize) cmd_characterize ;;
  ssh-golden) cmd_ssh_golden ;;
  auth-golden) cmd_auth_golden ;;
  settings-golden) cmd_settings_golden ;;
  entity-golden) cmd_entity_golden ;;
  budget-golden) cmd_budget_golden ;;
  budget-details-golden) cmd_budget_details_golden ;;
  job-golden) cmd_job_golden ;;
  pdf-golden) cmd_pdf_golden ;;
  pdf-samples) cmd_pdf_samples ;;
  layout)  shift; cmd_layout "$@" ;;
  token)   legacy_token; echo ;;
  logs)    shift; cmd_logs "$@" ;;
  *) sed -n '2,65p' "$0"; exit 1 ;;
esac
