# Dib4t (.NET)

Rewrite of the Dib4t ERP web app (`../back-end` Node/Express + `../front-end` React) on
ASP.NET Core 11 + Blazor WebAssembly. It runs side by side with the legacy app, on its own
MongoDB 9.0 database, and imports backups downloaded from the legacy app. The legacy app is
not changed.

See [PLAN.md](PLAN.md) for the full implementation plan.

## Build and test

```sh
dotnet build Dib4t.slnx
dotnet test --solution Dib4t.slnx        # xUnit v3 on Microsoft.Testing.Platform (global.json)
dotnet run --project src/Dib4t.Server    # http://localhost:3010
```

Unit, integration and E2E suites separately: `tests/unit.slnf`, `tests/integration.slnf`, `tests/e2e.slnf`.

The E2E suite (`tests/Dib4t.E2E`) starts the unchanged legacy app itself (`tools/legacy/legacy.sh`, own DB volume
`dib4t-legacy-e2e`) and the new app on a Testcontainers `mongo:9.0.8`, and loads the same seed backup into both.
The legacy ports (3000/3001/27017) must be free: stop a running legacy app with `tools/legacy/legacy.sh down`, or set
`DIB4T_E2E_REUSE_LEGACY=1` to use it (its database is replaced by the seed). `DIB4T_E2E_HEADED=1` shows the browser;
traces of failed tests go to `TestResults/e2e-artifacts/`.

`dotnet run` in Development uses the database of `docker-compose.dev.yml` (`dib4t-db`, `localhost:27018`). Load a
legacy backup into it with `curl -H 'Content-Type: application/octet-stream' --data-binary @<file>.dibbak
http://localhost:3010/api/database/restore` (open outside Production until the auth step).

The import compatibility suite (`tests/Dib4t.ImportCompatibility.Tests`) also restores a full, real production
backup byte for byte when `DIB4T_FULL_BACKUP=<path to .dibbak>` is set (never commit that file); skipped otherwise.
Its synthetic large backup is 32 MB; before a release run it at the legacy 1 GB limit with `DIB4T_LARGE_BACKUP_MB=1000`
(about 20 s for the restore).

Every restore first saves the current database to `Backup:Directory` (`dib4t-pre-restore-<UTC time>.dibbak`, the
last `Backup:PreRestoreKeep` = 5 kept): `/app/backups` in the container, `src/Dib4t.Server/backups/` with `dotnet run`.

## Docker

```sh
docker build --platform linux/amd64 -t dib4t-dotnet:1.0.0 .
docker compose -f docker-compose.dev.yml up -d --build                     # new app :3010 + mongo:9.0.8
docker compose -f docker-compose.dev.yml --profile legacy up -d --build    # legacy app :3000/:3001 + mongo:4.1.4
```

## NAS folders (Production only)

Budget create/clone make the budget's folder on the NAS over SSH, and renaming revision 0 renames it (PLAN.md §5).
This happens **only** with `ASPNETCORE_ENVIRONMENT=Production`; in every other environment the commands are only
written to the log (`Registos`, category `Nas`, "would run: …"). Settings (environment variables, never in the repo):

| Variable | Default | |
|---|---|---|
| `Nas__Enabled` | `true` in Production | `false` runs Production without touching the NAS (side-by-side run, PLAN.md §4.7) |
| `Nas__Host`, `Nas__Port` | –, `22` | required when enabled |
| `Nas__UserName`, `Nas__Password` | – | required when enabled; the password is never logged |
| `Nas__HostFingerprint` | empty | the NAS host key, `SHA256:…` as `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` prints it on the NAS; empty = any key accepted (as legacy) with a warning in the log |
| `Nas__ConnectTimeoutSeconds`, `Nas__CommandTimeoutSeconds` | `10`, `60` | |

The app refuses to start in Production with the NAS enabled and no host, user or password. Tests use a fake NAS
container (`tests/fixtures/nas-fake`), never the real one.

## Job folders and label printers (Production only)

Creating a job (`POST /api/job`, "Criar obra") moves the budget's `Documentos` and `Geometria` from
`/share/Orçamentos/…` into its new folder under `/share/Obras/…` over SSH (the same `Nas__*` settings), then sends the
two labels (`producao`, then `maquinacao`) to every printer of Settings → Impressoras over raw TCP (PLAN.md §5). As
with the NAS, this happens **only** with `ASPNETCORE_ENVIRONMENT=Production`; elsewhere the labels are only written
to the log (`Registos`, category `Printer`, "would send: …").

| Variable | Default | |
|---|---|---|
| `LabelPrinter__Enabled` | `true` in Production | `false` runs Production without printing (side-by-side run, PLAN.md §4.7) |
| `LabelPrinter__ConnectTimeoutMilliseconds`, `LabelPrinter__SendTimeoutMilliseconds` | `1500`, `2000` | |

"Imprimir" (budget row menu and jobs board options menu, `POST /api/job/print/:budgetId`) prints the labels of a
job again: the ticked templates (Produção, Maquinação) on every printer of that template. As legacy, it works only in
Production; elsewhere it answers "Só funciona em produção!". With `LabelPrinter__Enabled=false` it answers as usual and
only logs the labels.

A printer that cannot be reached is logged as an error and the job is still created (as legacy); "Imprimir" goes on
with the other printers. Tests use an in-test TCP listener, never a real printer.

## Before every commit

```sh
tools/verify.sh          # format, build, unit + coverage, integration, E2E, Docker image smoke test
tools/verify.sh --fast   # format, build, unit only
```

There is no CI: each feature is verified locally with `tools/verify.sh`, then committed.

Prerequisites: `tools/check-prereqs.sh`. Legacy app tooling: `tools/legacy/legacy.sh`.
