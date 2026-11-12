# Dib4t (.NET)

Rewrite of the Dib4t ERP web app (`../back-end` Node/Express + `../front-end` React) on
ASP.NET Core 11 + Blazor WebAssembly. It runs side by side with the legacy app, on its own
MongoDB 9.0 database, and imports backups downloaded from the legacy app. The legacy app is
not changed.

See [PLAN.md](PLAN.md) for the full implementation plan.
