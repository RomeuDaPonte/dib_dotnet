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

Prerequisites: `tools/check-prereqs.sh`. Legacy app tooling: `tools/legacy/legacy.sh`.
