# ADR-0001: The app is reached on the local network by IP and port

- Status: Accepted (owner, 2026-11-14)
- Answers: PLAN.md §14 question 1

## Decision

The new app runs only on the company's local network. Users open it as **`http://<NAS IP>:3010`**. There is no
hostname, no DNS entry and no public access.

## Consequences

- **Plain HTTP, no TLS**, exactly like the legacy app (`:4000`/`:4001`). `UseHttpsRedirection` and HSTS stay off.
  Kestrel listens on `http://0.0.0.0:3010` (`ASPNETCORE_URLS`); the port is already fixed (§4.7, §12).
- **Same origin for UI and API.** The Blazor client calls `/api/...` relative to the page's own origin, so whatever IP
  the users type works without configuration. No CORS policy is needed (the legacy one is not ported).
- **Browser features that need HTTPS ("secure context") are not available** on `http://<ip>`. The app must not
  depend on them:
  - Clipboard ("copy path" in the budget list): `navigator.clipboard` is `undefined` over plain HTTP. Keep the
    legacy fallback (`front-end/src/_helpers/fallbackCopyToClipboard.js`: hidden `<textarea>` +
    `document.execCommand("copy")`). E2E test runs against an `http://<non-localhost IP>` origin, because
    `localhost` counts as secure and would hide the problem.
  - No service worker / PWA, no `crypto.subtle` in the client, no camera APIs (the readings scanner is a keyboard
    wedge device, not the camera).
- `localStorage` (login token, §4.6) is per origin. A user who switches between two addresses of the same server
  (e.g. two IPs) logs in once per address. Expected; not a problem.
- The JWT key and every secret stay server side; the LAN is not treated as trusted for authorization (every
  endpoint keeps its role checks, §6).
