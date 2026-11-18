# Legacy API characterization fixtures (golden files)

Recorded from the **unchanged legacy back-end** (Node 16, `mongo:4.1.4`, `NODE_ENV` unset) by
`tools/legacy/characterize.cs`. These files are the contract for the new API's characterization tests (PLAN.md §11.2):
replay each request against the new app (on its own `mongo:9.0.8`), apply the same normalization, and compare the
response and the DB changes, **including BSON types**.

## Re-recording

```sh
LEGACY_NO_FRONTEND=1 tools/legacy/legacy.sh up --fresh   # fresh legacy DB (admin user + default prices only)
tools/legacy/legacy.sh characterize                        # about 2 min; rewrites the *.json files here
```

The recorder refuses to run on a DB that is not fresh. The output is deterministic: two runs on fresh DBs give
byte-identical files (checked 2026-11-14, three runs). Because the scenario ends with a destructive defect (D2),
**run `legacy.sh up --fresh` again before you use the legacy app for anything else.**

## Files

| File | Content |
|---|---|
| `00-initial-db.json` | DB right after a fresh start (seeded admin user + default prices) |
| `01-account.json` … `07-account-delete.json` | Steps per area, in execution order (each area builds on the previous ones) |
| `99-final-db.json` | Whole DB at the end |
| `index.json` | One line per step: file, id, route pattern, status (quick coverage view) |

Each step:

```jsonc
{
  "id": "budget-create",                       // unique, stable
  "title": "…",
  "request":  { "method": "POST", "path": "/api/budget", "auth": "admin", "body": { … } },
  "response": { "status": 200, "contentType": "application/json; charset=utf-8", "json": { … } },
  //            or "text": "…" (plain-text bodies, legacy res.send("…")), + "jwtPayload" for login
  //            or "expressErrorPage": { "error": "TypeError: …" }  (see below)
  //            or "body": { … }  summary for the PDF (base64) and backup (gzip) bodies
  "db": { "budgets": { "inserted": [ … ], "updated": [ { "_id", "changed": [fields], "after": { … } } ], "deleted": [ … ] } }
}
```

DB documents are **canonical Extended JSON** (`{"$numberInt":"35"}` vs `{"$numberDouble":"43.5"}`, `{"$oid":…}`,
`{"$date":…}`), read from the legacy app's own backup (`GET /api/database/backup`: raw BSON, exact types). The .NET
driver 3.x cannot talk to MongoDB 4.1.4 directly (it needs 4.4+), so the DB is snapshotted through that endpoint
after every step. Those snapshot calls are not part of the recorded steps.

### Auth labels

| Label | Token |
|---|---|
| `none` | no `x-auth-token` header |
| `invalid` | `not-a-jwt` |
| `tool` | settings-capable token signed by `legacy.sh token` (no `_id`, name `legacy.sh`) |
| `admin` | login token of *Ana Administradora* (every role) |
| `limited` | login token of *Bruno Maquinação* (`canSeeObras`, `canSeeLeituras`; no settings role). Its name in the token stays the pre-rename one, which shows up in `machiningStatistic.validatedBy` |

## Replay

`Dib4t.Server.Tests/Characterization/LegacyRunReplayTests` replays `01`–`05` and `07` in order on a fresh new app
(`05`: the job create steps, the clones before them, the blocked budget/task routes and the final list; the other job
routes come with later Phase 4 steps and are applied from the recording).
Steps of routes that a later feature step implements (`task-*`, `budget-patch-*`, `budget-pdf*`, `budget-import*`) are
not sent: their recorded document changes are written directly, so the following steps run on exactly the legacy state.
Remove a prefix from `NotYetImplemented` when its routes exist. Allow-listed (decided differences): `user-me-legacy`,
D2 (`user-delete-d2`, `users-after-delete`, `budget-list-after-delete`), D7 (`budget-update-missing-user`), D9
(`budget-search-bad-regex`); D24 also covers `blocked-import` (05): a budget with a job may send its tasks elsewhere.

## Normalization (apply the same to the new app's output before comparing)

| Value | Becomes |
|---|---|
| top-level `_id` of a document in collection *c* | `{{c:n}}` (n = order of first appearance), everywhere it appears (paths, bodies, references) |
| other ObjectIds (sub-document `_id`s) | `{{oid:n}}` |
| dates produced during the run (`date`, `startDate`, `…EndedInDate`, `fetchedAt`, backup `createdAt`) | `{{now}}` (dates sent by the scenario are fixed, in 2030, and kept) |
| current year in budget numbers / paths | `{{yy}}-0001`, `{{yyyy}}` |
| JWT strings | `{{jwt}}`; decoded payload kept in `jwtPayload` with `iat` → `{{iat}}` |
| bcrypt hashes | `{{bcrypt:$2a$10$}}` (prefix kept: legacy `bcryptjs` writes `$2a$`; the seeded admin is `$2b$`) |
| `GET /database/info` sizes (`size`, `avgObjSize`, `storageSize`, `indexSize`, `dataSize`) | `{{varies}}` |
| PDF body (base64) | `{ base64, decodedStartsWith: "%PDF-1.3", decodedEndsWithEof }` (sample PDFs are a separate Phase 0 item) |
| backup body | gzip flag + parsed header, collection frames and end frame |
| backup `Content-Disposition` timestamp | `{{stamp}}` |
| local absolute paths | `{{local-path}}` |

Known compare rules for the replay (PLAN.md §11.2): ignore key **order** in JSON responses (Mongoose emits defaults
first), compare `__v` only in DB documents, and treat `{{now}}` as "a date within the test run".

## Findings recorded here (all legacy behavior, unchanged)

- **Unhandled errors return Express's default HTML error page with a stack trace**, not `"Something failed."`: the
  legacy `middleware/error.js` is never registered. Recorded as `expressErrorPage.error` (stack dropped). Cases:
  `GET /account/me` (cast error, the route is new-app only), `GET /budget/getPdf/<unknown>`,
  `PATCH /job/statisticsDates` on a job without `jobStatisticsDates`, `PUT /job/statistics` to *Faturada* without
  `commentType`.
- **D2 confirmed:** `DELETE /account/<bruno>` answers with Bruno but deletes the **first** user (the seeded admin).
- **D1 confirmed:** `PATCH /job/state/:id` is always `400 "Obra não existe"`.
- **D7 confirmed:** `PUT /budget/:id` skips validation; without `madeById` it is `400 "Erro interno relacionado com user!"`.
- **D8 confirmed:** `POST /api/task` is always `400 "\"budgetId\" is not allowed"`.
- **D9:** invalid search regex → `500 "Error performing search: Invalid regular expression: /((/: Unterminated group"`.
- `POST /budget` for a supplier → **200** text `"Entidade registada como fornecedor!"` (no budget created).
- `GET /job/getById/<unknown>` and `GET /job/getByBudgetNumber/<no job>` → **200 with an empty body**.
- `GET /job/getByBudgetId/:id` → the job id as a JSON string.
- `DELETE /task/<unknown>/:budgetId` → `500 "Erro no servidor"` (the route marks the budget incomplete before it looks for the task).
- `PATCH /job/register` with `taskType: "consultancy"` is a no-op (`case development || consultancy` only matches development).
- `POST /entity/search` with missing fields matches nothing (`^undefined`); the UI always sends all five fields.
- `PUT /budget/patchProperty` with `total: "999"` stores a number (Joi converts, Mongoose casts).
- `PUT /job/statistics` with invoicing below 100 % → `405 {reason, updatedJob}` **and saves** the statistics.
- Restore of a garbage or truncated file → `400 "O ficheiro não é um backup válido da Dib4t."`; restoring the
  backup just taken leaves the DB unchanged (empty `db` diff).

## Not covered (and why)

- `POST /job` and `POST /job/print` **production paths** (NAS folders, label printing), plus the NAS rename in
  `PUT /budget/:id` for a revision-0 descriptive change (D5: it calls SSH even in dev). They are never triggered here:
  the NAS and printers are not reachable from the dev network (PLAN.md §5.1). They are covered by the SSH
  command golden files and the fakes in Phase 3/4.
- The `409` busy lock of backup/restore (needs two overlapping long operations; tested in Phase 5).
