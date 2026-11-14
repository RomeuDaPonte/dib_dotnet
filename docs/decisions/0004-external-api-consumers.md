# ADR-0004: The API has no consumers other than the app's own front-end

- Status: Accepted (owner, 2026-11-14; corrects an earlier "yes" given the same day)
- Answers: PLAN.md §14 question 5

## Decision

The legacy API serves **only the legacy front-end**. No scanner station, script or other system calls it.

## Consequences

- The §6 API contract is still kept (same routes, `x-auth-token`, bodies, status codes and messages), because the
  characterization fixtures and the legacy comparison (§11) depend on it and it keeps the port low-risk. But it is
  not an external contract: where the new UI needs a better API, it may get **additional** endpoints (e.g. a `$lookup`
  job list for Phase 6 performance) without a compatibility layer.
- **No token transition**: the new app signs tokens with its own key (D12) and does not accept legacy tokens. Users
  simply log in once in the new app.
- **No address transition**: only browsers change address at switch-over (`:4000` → `:3010`, ADR-0001). Nothing
  else must be re-pointed and the new app does not need to listen on `:4001`.
- Routes the legacy UI never calls (`DELETE /api/account/:id`, `POST /api/task`, D2/D8) are still implemented as
  decided in ADR-0005, but nothing outside the app depends on them.
