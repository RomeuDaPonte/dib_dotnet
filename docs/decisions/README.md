# Decisions (ADRs)

One file per decision: context, decision, consequences. Accepted decisions change only through a new ADR that
supersedes the old one.

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-lan-access-by-ip.md) | Local network only, `http://<NAS IP>:3010`, no hostname/TLS; no secure-context browser APIs (clipboard fallback) | Accepted |
| [0002](0002-supported-screens.md) | Phones, tablets and desktops; smallest width 360 px | Accepted |
| [0003](0003-version-1-0-0.md) | New app version 1.0.0 | Accepted |
| [0004](0004-external-api-consumers.md) | No other API consumers: the API only serves the app's front-end; no token or address transition | Accepted |
| [0005](0005-legacy-defects.md) | Legacy defects D1–D24: all proposed fixes; D10, D18, D21, D22, D23 decided; D24 kept as legacy | Accepted |
