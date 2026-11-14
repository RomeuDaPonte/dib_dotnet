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

## Docker

```sh
docker build --platform linux/amd64 -t dib4t-dotnet:1.0.0 .
docker compose -f docker-compose.dev.yml up -d --build                     # new app :3010 + mongo:9.0.8
docker compose -f docker-compose.dev.yml --profile legacy up -d --build    # legacy app :3000/:3001 + mongo:4.1.4
```

## Before every commit

```sh
tools/verify.sh          # format, build, unit + coverage, integration, E2E, Docker image smoke test
tools/verify.sh --fast   # format, build, unit only
```

There is no CI: each feature is verified locally with `tools/verify.sh`, then committed.

Prerequisites: `tools/check-prereqs.sh`. Legacy app tooling: `tools/legacy/legacy.sh`.
