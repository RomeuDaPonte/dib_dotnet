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
