# Dib4t — Rewrite plan: Node/React ➜ .NET (ASP.NET Core + Blazor WebAssembly)

> Status: **Plan / not started** · Created 2026-11-12
> Source of truth for the behavior: `../back-end` (Node/Express/Mongoose) and `../front-end` (React 17 / MUI v4).

---

## 1. Goal and hard constraints

Rebuild the Dib4t ERP web app (budgets, jobs, entities, settings, dashboard, readings) on a .NET stack, **keeping every feature**.

| # | Constraint | What it means in practice |
|---|------------|---------------------------|
| C1 | **.NET, WebAssembly, server side** | ASP.NET Core server (REST API + host) and a Blazor WebAssembly client. |
| C2 | **Same database** | The same MongoDB database (`dib4t`), same collections, same document shapes. |
| C3 | **Old and new app run at the same time** | Both apps read and write the same documents at once. Every document the new app writes must be 100% readable and editable by the Mongoose app, and the other way round. |
| C4 | **No new data or fields in the DB** | No new collections, fields, indexes, validators, lock documents or migration markers. The new app stores nothing the old app does not already store. |
| C5 | **Same color theme** | Same palettes (dark by default, light toggle), same status colors, same logos and imagery. |
| C6 | **Production-only logic kept** | Everything guarded by `NODE_ENV === "prod"` (SSH folder management on the NAS, label printing, production DB/port, print endpoint guard) must be reproduced and guarded by the .NET environment (`Production`). |
| C7 | **Senior-level .NET practices** | Clean layering, DI, async with cancellation, options pattern, no DB access from UI components, tests. See §3. |

---

## 2. Target stack (recommended)

| Concern | Choice | Why |
|---|---|---|
| Runtime | **.NET 10 (LTS)** | Current LTS, supported until Nov 2028. |
| Server | ASP.NET Core 10, **Controllers** (API) + Blazor Web App host | Controllers suit a large, existing REST surface that has to stay identical. |
| Client | **Blazor WebAssembly** (Blazor Web App template, `InteractiveWebAssembly` render mode for all pages, **prerendering off**) | Meets "WebAssembly". With prerendering off, auth can stay in `sessionStorage` (same as today). |
| UI kit | **MudBlazor** (Material Design) | Closest to MUI: same components (AppBar, Drawer, Tabs, Table, Dialog, Snackbar, DatePicker) and palette model, so the look carries over. |
| DB driver | **MongoDB.Driver** (official C# driver) | Fine-grained control over BSON types and `$set` updates, which compatibility depends on (§4). Version depends on the server version, see Phase 0. |
| Validation | **FluentValidation** | Ports the Joi schemas. Shared between server and client where useful. |
| Auth | Custom `x-auth-token` authentication handler + HS256 JWT | Tokens compatible with the old API (§4.6). |
| Passwords | **BCrypt.Net-Next** | Reads and writes the bcryptjs `$2a/$2b$10$` hashes already in `users`. |
| SSH (prod) | **SSH.NET** (`Renci.SshNet`) | Replaces `ssh2-promise` for NAS folder operations. |
| Label printer (prod) | `System.Net.Sockets.TcpClient` | Replaces `telnet-client` (raw TCP to port 9100, no negotiation, 1500 ms timeout). |
| PDF | **PDFsharp 6 (MIT)** | The legacy PDF is laid out with absolute coordinates. PDFsharp draws at exact points, so it matches 1:1. QuestPDF is an option if its license fits. |
| Logging | **Serilog** (console + rolling file) | Replaces winston; keeps an `uncaughtExceptions` style file sink. |
| Charts | MudBlazor `MudChart` (pie) | Replaces react-google-charts on the dashboard. |
| Tests | xUnit v3, FluentAssertions/Shouldly, **Testcontainers (MongoDB)**, **bUnit**, **Playwright** | Unit, integration, component and E2E coverage. |
| Packaging | Docker (`mcr.microsoft.com/dotnet/aspnet:10.0`) | Same deployment style as the current apps. |

> Avoid MediatR and AutoMapper (both moved to commercial licenses in 2025). Use plain application services and explicit mapping (or Mapperly, a source generator under MIT).

---

## 3. Solution architecture

```
dib4t-dotnet/
├─ Dib4t.slnx
├─ Directory.Build.props          # net10.0, Nullable=enable, TreatWarningsAsErrors, analyzers, LangVersion latest
├─ Directory.Packages.props       # Central Package Management (one version per package)
├─ .editorconfig
├─ src/
│  ├─ Dib4t.SharedKernel/         # No dependencies. Well-known string constants + pure calculations
│  │                              #   (JobStates, JobPriorities, TaskTypes, RoleNames, CommentTypes,
│  │                              #    QuestionResponses, EntityTypes, PrintTemplates, BudgetCalculations,
│  │                              #    JobCostCalculations = port of front-end taskHelper.js)
│  ├─ Dib4t.Domain/               # Aggregates + behavior: Budget, BudgetTask, BusinessEntity, Job (+ statistics
│  │                              #   value objects), User, Prices, PaymentMethod, PrinterSettings.
│  │                              #   Domain rules: budget numbering, ChangeState, SetStatistics,
│  │                              #   UpdateStateDates, SetNewStatistics, UpdateRegisteredValue, ...
│  ├─ Dib4t.Application/          # Use-case services (BudgetService, JobService, ...), ports (interfaces):
│  │                              #   I*Repository, INasFileShare, ILabelPrinter, IBudgetPdfGenerator,
│  │                              #   IPasswordHasher, ITokenService, IDatabaseBackupService, ICurrentUser.
│  │                              #   FluentValidation validators, Result<T>/Error types. Uses TimeProvider.
│  ├─ Dib4t.Infrastructure/       # MongoDB (class maps, repositories, UpdateDefinition builders),
│  │                              #   SSH.NET NAS adapter (+ NoOp adapter for non-prod), TCP label printer,
│  │                              #   PDFsharp generator, .dibbak backup format, BCrypt, JWT.
│  ├─ Dib4t.Contracts/            # Request/response DTOs shared by server and client (JSON shape = legacy API).
│  ├─ Dib4t.Server/               # ASP.NET Core host: Controllers, auth handler, policies, exception
│  │                              #   handler, Serilog, health checks, hosts the WASM client.
│  └─ Dib4t.Client/               # Blazor WebAssembly: Pages, Components, Layout, Theme,
│                                 #   typed API clients, state/view-model services, JS interop (clipboard,
│                                 #   file download/upload streaming, sessionStorage).
└─ tests/
   ├─ Dib4t.Domain.Tests/
   ├─ Dib4t.Application.Tests/
   ├─ Dib4t.Infrastructure.Tests/   # Testcontainers MongoDB at the PRODUCTION server version
   ├─ Dib4t.Server.Tests/           # WebApplicationFactory API tests
   ├─ Dib4t.Compatibility.Tests/    # Runs the LEGACY node API in a container on the same DB (§11.3)
   ├─ Dib4t.Client.Tests/           # bUnit
   └─ Dib4t.E2E/                    # Playwright
```

### 3.1 Dependency rules (enforced with an architecture test, e.g. NetArchTest)

```
SharedKernel  ←  Domain  ←  Application  ←  Infrastructure  ←  Server
SharedKernel  ←  Contracts  ←  Client
                 Contracts  ←  Server
```

- `Client` **never** references Domain, Application or Infrastructure, and has no MongoDB driver.
- **Components never touch data directly.** A `.razor` component injects a page state / view-model service (e.g. `BudgetDetailsState`), which calls a typed API client (`IBudgetApiClient` → `HttpClient`). The API controller calls an application service, which calls a repository, which talks to MongoDB. No `HttpClient` calls inside components either.
- Controllers stay thin: bind, call the service, map the `Result` to an HTTP response. No business logic in controllers.
- Domain types have no MongoDB attributes. Mapping lives in Infrastructure (`BsonClassMap`), so the domain stays persistence-agnostic.
- Avoid naming types `Task` or `Entity`, which collide with framework names. Use **`BudgetTask`** and **`BusinessEntity`**. The collection names stay `tasks` and `entities`.

### 3.2 Cross-cutting practices

- `async` all the way with `CancellationToken` passed through.
- Options pattern with `ValidateDataAnnotations().ValidateOnStart()`: `MongoOptions`, `JwtOptions`, `NasOptions`, `PrinterOptions`.
- **No secrets in the repo.** The legacy repo commits the JWT key and the SSH password in `config/default.json`. In the new repo they live in user-secrets (dev) and environment variables (prod). Rotating the SSH credential is recommended.
- `TimeProvider` for every "now" (budget year, state dates, PDF date), so tests are deterministic.
- `IExceptionHandler` returns `500 "Something failed."` as plain text (legacy parity) and logs the exception.
- Health check `/health` (Mongo ping).
- `Result<T>` with typed errors (`NotFound`, `Validation`, `Conflict`, `BusinessRule`). Controllers map them to the **legacy status code and message** (§6).

---

## 4. Coexistence and database compatibility contract (most critical section)

Both apps share one database and read and write the same documents at once. Each rule below exists for that reason.

### 4.1 Collections (Mongoose model → collection)

| Model | Collection | Notes |
|---|---|---|
| Budget | `budgets` | `client` → ObjectId(entities), `madeBy` → ObjectId(users) |
| Entity | `entities` | |
| Job | `jobs` | `budget` → ObjectId(budgets); embedded sub-docs with their own `_id` |
| Task | `tasks` | `budgetId` → ObjectId(budgets) |
| User | `users` | bcrypt hash in `password` |
| Prices | `prices` | single document |
| PaymentMethod | `paymentmethods` | |
| PrinterSettings | `printersettings` | |

Phase 0 confirms the names with `listCollections` on the production DB, because Mongoose pluralization can surprise.

### 4.2 Document shapes, with Mongoose defaults the new app must reproduce

All documents carry `_id: ObjectId` and **`__v: Int32`** (Mongoose version key).

**budgets**: `client` ObjectId (req), `madeBy` ObjectId (req), `descriptive` string (req), `description` string?, `personInCharge` string?, `budgetNumber` string `"YY-NNNN"` (req), `date` Date (default now), `revision` number (default 0), `complete` bool (default false), `wasPdfDownloaded` bool (default false), `paymentMethod` string (req), `margin` number (default 30), `total` number?, `daysNeededToDoTheWork` number (default 60), `validity` number (default 30), `hasGenerateJob` bool (default false).

**entities**: `name` (req), `fiscalNumber`?, `address`?, `zipCode`?, `location`?, `paymentCondition`?, `type` ∈ {`Cliente`, `Fornecedor`, `Cliente e Fornecedor`} (req).

**tasks**: `budgetId` ObjectId (req), `taskType` ∈ {automation, consultancy, development, machining, assembly, others, components} (req), `description`?, `unitPrice` number (req), `quantity` number (req).

**users**: `name` (min 3), `email` (min 10), `password` (bcrypt), `roles: string[]`, `fieldOfCompetencies` ∈ {Desenvolvimento, Maquinação, Montagem, Para faturação} (default `Desenvolvimento`).

**prices**: `automation`, `consultancy`, `development`, `margin` (1–100), `machining`, `assembly` (all numbers, required).

**paymentmethods**: `name`. **printersettings**: `ip`, `port` (number), `templateName` ∈ {`producao`, `maquinacao`}.

**jobs**:
- `budget` ObjectId, `startDate` Date (default now), `dueDate` Date, `deliveryDate` Date?, `invoicedPercent` (default 0), `finalPrice` (default 1), `state` ∈ {Desenvolvimento, Em Aprovação, Aprovado, Maquinação, Montagem, Para faturação, Faturada, Apagada}, `orderIdentification` (default `"Por preencher"`), `isOfficialOrderIdentification` (default false), `components`/`automation`/`materials`/`others`/`costNonQuality` (default 0), `comments`?, `commentType: string[]` (default `[]`), `priority` ∈ {Alta, Média, Baixa} (**no effective default**: the legacy default resolves to `undefined`), `createdBy`?, `invoicedBy`?
- `devStatistic` {`_id`, `numberOfHoursUsed`, `budgetedHours`, `deviation`} (defaults 0)
- `machiningStatistic` {`_id`, hours/budgeted/deviation, `verificationAndShapeControl`, `dimensionalVerificationAndControl`, `analysisOfTheDegreeOfFinish` ∈ {Ok, Nok, n/a} (default `n/a`), `validatedBy`?}
- `assemblyStatistic` {`_id`, hours/budgeted/deviation, `pneumaticCircuits`, `electricalBoardsAndCircuits`, `kinematics`, `programmingFeaturesPLC`, `operator_equipmentSafetyConditions`, `visualInspection` (default `n/a`), `validTryOnFirstTest` ∈ {"1","2","3"} (default "1", **string**), `validatedBy`?}
- `jobStatisticsDates` {`_id`, `devEndedInDate`, `onApprovalEndedInDate`, `approvedEndedInDate`, `machiningEndedInDate`, `assemblyEndedInDate`, `forInvoicingEndedInDate`}

### 4.3 Write rules (new app → DB)

1. **Exact BSON types.** ObjectId references are stored as `ObjectId`, never strings. Dates are BSON `Date` (UTC). Numbers are `Int32` when the value is an integer that fits in 32 bits, otherwise `Double`. That is what the Node driver writes for JS numbers. **Never write `Decimal128` or `Int64`**, because Mongoose would hand those to the old app as objects and break its arithmetic. Implement this with one custom `JsNumberSerializer` used for every numeric field, with C# type `double`.
2. **Exact strings.** Enum values are Portuguese strings with accents. Keep them as constants in `SharedKernel`, compare ordinally, and store Unicode NFC.
3. **Updates use `$set` on changed fields only** (`UpdateOne` + `UpdateDefinition`), never a full-document `ReplaceOne`. That matches Mongoose `save()` semantics (it only `$set`s modified paths) and keeps concurrent edits from both apps from overwriting each other's untouched fields.
4. **`__v` emulation.** Inserts write `__v: 0`. When an update replaces an array (`roles`, `commentType`), also `$inc: { __v: 1 }`, like Mongoose versioning.
5. **Embedded sub-documents get an `_id`** (new `ObjectId`) when created or replaced as a whole, the same way Mongoose assigns them.
6. **Inserts include the Mongoose defaults** from §4.2. The old app applies defaults on read anyway, but writing them keeps queries such as `hasGenerateJob: true` consistent.
7. **Round-trip unknown fields**: `[BsonExtraElements]`-style catch-all in class maps, so a field the new app doesn't know is never lost.
8. **No schema side effects.** No `CreateIndex`, no collection validators, no new collections, no "app metadata" documents (C4).

### 4.4 Read rules (DB → new app)

1. Tolerate both `Int32` and `Double` (and legacy `Int64`) for every number.
2. Apply the Mongoose defaults when a field is missing (old documents predate some fields: `hasGenerateJob`, `priority`, `jobStatisticsDates`, `commentType`, `invoicedPercent`, …).
3. A missing `priority` behaves like the legacy app: it sorts into the "low" bucket and shows no icon.
4. Ignore `__v` in the domain, but keep it in persistence.

### 4.5 Data-shape inventory script (Phase 0)

Before any code, run an aggregation per collection (`$objectToArray` + `$type`) on a **copy** of production. It counts the BSON type of every field path, finds missing fields, Int32/Double mixes, string-vs-Date drift, and so on. The output becomes test fixtures, and serializers must accept every observed variant.

### 4.6 Authentication compatibility

- Header **`x-auth-token`** (not `Authorization: Bearer`).
- Token: HS256 JWT, payload `{ _id, name, email, roles, fieldOfCompetencies, iat }`, **no `exp`**, signed with the legacy key (configurable; today the legacy code hard-codes it in `models/user.js`).
- ⚠ `Microsoft.IdentityModel` refuses HMAC keys shorter than 256 bits (the legacy key is 72 bits). Implement `ITokenService` with a small HMAC-SHA256 signer/validator of our own (or the `JWT` NuGet package, which doesn't enforce key length), wrapped in a custom `AuthenticationHandler`.
- Responses: missing token → `401 "Access denied. No token provided."`; invalid token → **`400 "Invalid token."`**; settings role missing → `401 "Não tem permissão para concluir esta acção."`.
- Policy `CanSeeDefinicoes` maps to `authCanSeeSettings`.
- Result: a token from either app works on both APIs. That allows parity testing (old front-end pointed at the new API) and a gradual cut-over.

### 4.7 What cannot be coordinated across the two apps (C4 forbids shared locks) and how it's mitigated

| Risk | Mitigation |
|---|---|
| **Budget number collision.** Both apps compute `count(distinct budgetNumber this year) + 1` at the same moment. The same risk exists today between two users of the old app. | In the new app, a `SemaphoreSlim` around numbering. After the insert, re-check for another rev-0 document with the same number and a different `_id`; if one exists, renumber with retry and log a warning. A unique index would be the real fix, but it's a DB change, so it's deferred until the legacy app is retired. |
| **Backup/restore lock is per process.** A restore from one app can run while the other app is writing. | Operational rule: restore only during maintenance, with the other app stopped. The UI shows a strong warning (it already does). |
| **Lost updates.** Two users edit the same budget in different apps. | Same last-write-wins behavior the old app already has, kept field-granular by rule 4.3.3. |

---

## 5. Production-only behavior (must be preserved, gated by `IHostEnvironment.IsProduction()`)

| Legacy trigger | Behavior to reproduce | New home |
|---|---|---|
| `NODE_ENV=prod` → port | Dev 3001, prod 4001. The legacy front-end derives the API URL from its own origin (`origin` with the last char replaced by `1`). | New app: one origin serves UI + `/api`. Port set by `ASPNETCORE_URLS` and **different from the legacy ports**, so both run side by side (e.g. `:4100`). |
| `NODE_ENV=prod` → DB | `db` (localhost) vs `serverDb` (NAS host, port 4002). | `MongoOptions.ConnectionString` per environment (appsettings.Production.json + env var). |
| `POST /api/budget` (create) | After save: SSH `mkdir -p` of `/share/Orçamentos/{currentYear}/'{client}'/'{budgetNumber} - {descriptive}'`, plus `/Geometria` and `/Documentos`, then `test -d`. If the folder is missing → `500 "Erro ao criar pasta"`. | `INasFileShare.CreateBudgetFolderAsync`. Prod: SSH.NET; other envs: no-op that logs. |
| `POST /api/budget/clone/:id` | Same folder creation as create. | Same. |
| `PUT /api/budget/:id` (**not gated in legacy**) | If `revision == 0` and `descriptive` changed: `cd /;mv {oldPath} {newPath}` (year taken from `budget.date`). | `INasFileShare.RenameBudgetFolderAsync`. See defect D5 about gating. |
| `POST /api/job` (create) | In prod after save: (1) `updateFolderPath(revZero.descriptive)`; (2) `moveBudgetFolderToJobs("{budgetYear}/'{client}'/'{num} - {desc}'")`: `mkdir -p /share/Obras/{p}/CAD`, `/CAD/Equipamento`, `/CAM`; `mv share/Orçamentos/{p}/Documentos share/Obras/{p}/"Informações Técnicas"`; `mv …/Geometria …/CAD/Artigo`; (3) print labels on **every** printer with template `producao`, then every printer with `maquinacao`. | `JobService.CreateAsync` → `INasFileShare` + `ILabelPrinter`. |
| `POST /api/job/print/:budgetId` | Non-prod → `400 "Só funciona em produção!"`. Prod: print selected templates (`producao`/`maquinacao`) to all matching printers. Error if none is configured. | `LabelPrintingService`. |

**Label printer protocol** (`PrinterSettings.print`): raw TCP to `ip:port`, connect timeout 1500 ms, send the template string, close, swallow errors (logged; returns false). Templates (date is `job.startDate` as `d/M/yyyy`, no zero padding, server local time):
- `producao`: `"\n      ia3,^TS001{num},{date},{descriptive},{client},{num},\n      "` (the leading/trailing whitespace is part of the payload)
- `maquinacao`: `"ia3,^TS002{num},{date},{descriptive},{client},"`

> **Byte-exact verification required:** in Phase 0, point a printer setting at `nc -l 9100` and capture what the legacy app sends (encoding, whether `telnet-client` appends a line terminator). The capture becomes a golden-file test.

**SSH safety:** legacy builds shell commands by string concatenation, with client and descriptive in single quotes. The new adapter must produce **the same resulting paths** but escape arguments correctly. Commands are logged at Information level, without credentials.

---

## 6. API contract (kept identical, routes are case-insensitive as in Express)

The new server exposes **the same routes, payloads, JSON shape (`_id`, camelCase, ISO-8601 UTC dates with milliseconds, populated sub-objects) and status codes/messages**. That gives us:
- characterization tests run against both back-ends with the same scenarios (§11.2), and
- the option to point the legacy React UI at the new API during UAT.

Errors are plain-text bodies (legacy `res.status(x).send("…")`), not ProblemDetails.

Auth column: **A** = anonymous, **T** = token, **S** = token + `canSeeDefinicoes`.

| Area | Method & route | Auth | Key behavior |
|---|---|---|---|
| Account | `POST /api/account/login` | A | Joi-like validation; unknown email → 400 "Invalid email or password."; wrong pwd → 400 "Password incorreta!"; returns the raw JWT string |
| | `POST /api/account/create` | S | email unique ("Email já registado."), bcrypt salt 10, returns `{_id,name,email,roles}` |
| | `GET /api/account/roles` | S | legacy role list (without `canSeeAdjudicacoes`, as today) |
| | `GET /api/account/users` | S | `name email roles fieldOfCompetencies` sorted by name |
| | `GET/PUT/DELETE /api/account/:id` | S | PUT: password re-hashed only if provided. DELETE: see defect D2 |
| Budget | `GET /api/budget` | T | 200 latest docs sorted `budgetNumber desc, revision desc`, deduped to the latest revision per number, flattened row DTO |
| | `GET /api/budget/search?q=` | T | `q` min 2; case-insensitive **regex** over number, client name, descriptive, personInCharge, madeBy name ($lookup); deduped. Invalid regex → 500 "Error performing search: …" |
| | `GET /api/budget/getById/:id` | T | populated `madeBy`, `client` |
| | `GET /api/budget/revisions/:number` | T | all revisions desc, `madeBy` populated |
| | `POST /api/budget` | T | see §7.1; prod folder creation |
| | `POST /api/budget/clone/:id` | T | new number, copies tasks/fields; prod folder |
| | `POST /api/budget/newrevision/:id` | T | `revision = count(docs with number)`; copies tasks |
| | `PUT /api/budget/:id` | T | descriptive/personInCharge/madeBy; complete=false, pdf=false; folder rename (rev 0); no-op if `hasGenerateJob` |
| | `PUT /api/budget/patchProperty/:id` | T | whitelisted property + typed value; sets `complete=false, wasPdfDownloaded=false` **then** the property; no-op 200 if `hasGenerateJob` |
| | `PUT /api/budget/import/:id` | T | import tasks (replace) and/or description from budget number; copies `total`, `margin` with tasks |
| | `GET /api/budget/getPdf/:id` | T | sets `wasPdfDownloaded=true`; body = **base64 string**, `Content-Type: application/pdf` |
| Task | `POST /api/task/createDefaultTask/:budgetId` | T | `automation`, unitPrice = prices.automation, qty 1, desc "" (blocked if job generated) |
| | `GET /api/task/getBudgetTasks/:budgetId`, `GET /api/task/getTaskTypes` | T | |
| | `POST /api/task`, `PUT /api/task/:taskId/:budgetId`, `DELETE …` | T | each marks budget incomplete; blocked with "Orçamento gerou obra!" |
| Entity | `GET getAll`, `getallclients`, `getentityTypes`, `getAllPaymentConditions`, `GET /:id` | T | clients = type Cliente or Cliente e Fornecedor, sorted by name |
| | `POST /api/entity`, `PUT /api/entity/:id` | T | unique name on create ("Nome de entidade já registado") |
| | `POST /api/entity/search` | T | AND of prefix (`^`) case-insensitive regex on name, fiscalNumber, location, address, zipCode |
| | `POST addPaymentMethod`, `DELETE deletePaymentMethod/:id` | T | unique name |
| Prices | `GET /api/prices` (T), `POST /api/prices` (S) | | pick fields only (no `_id`) |
| Printers | `GET /`, `GET /:templateName`, `POST /`, `PUT /:id`, `DELETE /:id` under `/api/printer-settings` | S | IP + port + template validation |
| Job | `POST /api/job` | T | see §7.3; prod NAS + print |
| | `PUT /api/job/statistics/:jobId` | T | state-transition workflow (§7.3); `405 {reason, updatedJob}` when billing < 100 % |
| | `GET getById/:id`, `GET /:jobId`, `getCounts`, `getUnfinished`, `getAll` (limit 10000), `getByState/:state`, `getByBudgetNumber/:n`, `getByBudgetId/:budgetId` (returns the id only), `getValidTryOnFirstTestCountersByYear/:year` | T | priority ordering High → Mid → other; `billed`/`forInvoicing` lists are not re-ordered |
| | `PATCH dueDate/:id`, `statisticsDates/:id`, `register/:id`, `state/:id`, `changeJobPriority/:id`, `singleProperty/:id`, `updateVerifications/:id` | T | see §7.3 |
| | `POST /api/job/print/:budgetId` | T | prod only |
| Database | `GET /api/database/info`, `GET /api/database/backup`, `POST /api/database/restore` | S | §7.6 |

---

## 7. Business rules inventory (to port into Domain/Application, each with unit tests)

### 7.1 Budgets
- **Numbering**: `count(distinct budgetNumber where date >= {currentYear}-01-01T00:00:00Z) + 1`, formatted `YY-NNNN` (zero-padded to 4; ≥1000 not padded).
- **Create**: client must exist; a **supplier** entity returns **200** with the text "Entidade registada como fornecedor!" (not an error; legacy behavior). `madeBy` must exist. `paymentMethod = entity.paymentCondition`, `margin = prices.margin`, `total = trunc(margin*automation/100) + automation`. Then prod folder, then a default task.
- **Clone**: new number; copies paymentMethod, margin, total, daysNeededToDoTheWork, validity, description, tasks; new client/descriptive/personInCharge/madeBy from the form.
- **New revision**: same number, `revision = number of docs with that number`, copies everything + tasks, date = now, flags reset.
- **Edit lock (UI)**: editable unless `complete && wasPdfDownloaded`; the user can toggle lock/unlock manually. Every change while locked → warning toast "Oçamento bloqueado".
- **Totals (UI, live)**: tasks total = Σ qty × unit; "C / Margem" = total × (1 + margin/100) (2 decimals); recalculated `total = tasksTotal + trunc(tasksTotal*margin/100)` and patched whenever tasks change; effective margin = `margin − margin·(withMargin − total)/(margin/100·tasksTotal)`, colored **lightGreen > 25, red < 20, yellow otherwise**.
- **Patch whitelist & validation**: `total`, `daysNeededToDoTheWork` (number ≥ 0), `paymentMethod`, `description` (non-empty string), `margin` (number ≥ 1), `complete` (bool), `validity` (int ≥ 1).
- **Menu actions**: "Obter pdf" (if complete), "Detalhes obra" + "Imprimir" (if has job), "Clonar" (always), "Nova revisão" (complete && !hasJob), "Criar obra" (complete && pdfDownloaded && !hasJob).
- **Copy path** (clipboard): with a job → `D:\NAS\Obras\{jobStartYear or 20+YY}\{client}\{num} - {desc}` + `\CAD` (dev/approval/approved), `\CAM` (machining), root otherwise; without a job → `\\DIB4T\Orçamentos\{budgetYear}\{client}\{num} - {desc}`.
- **PDF** ("Proposta comercial", A4, Roboto): reproduce the pdfmake layout exactly. Logo (from `helpers/logoForPdf.js`, base64 JPEG → embedded resource) 80 pt wide at (30,10). Title at (424,70) 15 pt bold. Lines at y=95 (2 pt) and y=320 (1 pt) from x=20 to x=580. Grey rect `#eff0f2` at (270,145) 290×115. Company block (DIB4T LDA, address, NIF, email, site, document date `d/M/yyyy`, issuer name/email). Client block. "Descritivo", "Designação técnica" + justified description. Delivery time ("N semanas" if days > 7, ceil(days/7), else "N dias"), validity, payment terms, VAT note, Total (right-aligned, 18 pt). The canvas coordinates are offset by the 40 pt page margins. **Acceptance = visual diff (pixel compare) against PDFs generated by the legacy app for the same budgets.**

### 7.2 Entities and settings
- Entity CRUD; client-side filter on name (case-insensitive), location and type.
- Users: create/edit dialog with roles as checkboxes (`canSeeOrcamentos`, `canSeeObras`, `canSeeAdjudicacoes`, `canSeeEntidades`, `canSeeDefinicoes`, `canSeeDashboard`, `canSeeLeituras`) and field of competencies; password + confirmation.
- Prices (single doc), payment methods (add on Enter, delete), printers (list + add/edit/delete form, IPv4 regex).
- Startup seeding (idempotent, same checks): default prices `{automation 10, consultancy 10, development 10, margin 35, machining 10, assembly 10}` if none; admin user `userlogado@gmail.com` (same hash, same roles) if missing.
- Info tab: "Webapp ERP", Software ID `69988ec8-c573-4799-bafc-0b6d30340961`, version (bump to 1.0.0 for the new app, or keep 0.9.1: open question), "2018, DIB4T".

### 7.3 Jobs
- **Create**: budget must exist; no other budget with the same number may have `hasGenerateJob`; revision 0 must exist; mark the budget `hasGenerateJob`; job from the form (`finalPrice`, `dueDate`, initial `state`, `orderIdentification`, `isOfficialOrderIdentification`), `createdBy = token.name`; `SetNewStatistics(tasks)`: zero the costs; budgeted hours per type (dev/machining/assembly = Σ quantity of tasks of that type); deviation = budgeted; priority `Média`. Then prod NAS + labels.
- **Statistics transition** (`PUT statistics`): validate per **current** state (dev/approval/approved, machining, assembly, forInvoicing schemas, which differ in integer vs decimal rules). If `devStatistic` is missing → initialize, change state, save. Otherwise check the deviation (`Σ quantity(tasks of the mapped type) − hoursUsed == deviation`; empty list → OK), `SetStatistics` (per-state field mapping incl. end-date stamps, `validatedBy`/`invoicedBy = token.name`), `UpdateStateDates(newState)` (back-fill missing earlier end dates), then `ChangeState` (to `Faturada` only if `invoicedPercent ≥ 100`; otherwise save and return **405** `{reason:"Valor faturado inferior a 100%", updatedJob}`).
- **Registered values** (`register`): development→dev hours, assembly→assembly hours, machining→machining hours, automation, components, others, `custoNaoQualidade`→costNonQuality, `materials`.
- **Single property** whitelist: deliveryDate (only in Montagem / Para faturação, else 400), orderIdentification, isOfficialOrderIdentification, comments, invoicedPercent, with Mongoose-like casting (string → number/date/bool).
- **Verifications**: replace the machining or assembly sub-document (validatedBy = current user name, set by the client today; the new server should set it from the token).
- **Counters** per state; **dashboard**: for jobs in Para faturação/Faturada with `startDate` between `{year}-01-01` and `{year}-12-31` (UTC midnight), counts of `validTryOnFirstTest` "1"/"2"/"3".
- **Jobs UI**: tabs Todas/Desenvolvimento/Em aprovação/Aprovadas/Maquinação/Montagem/Para faturação/Faturadas (billed + deleted, sorted by due date) with counts. The **initial tab comes from the user's `fieldOfCompetencies`**. Columns: Nº, Cliente, Descritivo, Data de início, Finalizar até (date picker + countdown icon: red when ≤ 0 days, yellow 1–4, green 5–9, "9+" beyond; hidden for invoicing/billed/deleted), priority icon (Alta yellow ↑, Baixa blue ↓), Estado (red when Apagada), "Ft %" in the invoicing tab. Actions: copy path, comments modal (only when comments exist; info icon `#535bd5`), change state menu (Faturada/Eliminar only from Para faturação), priority/print menu, order check icon (lightGreen), delivered icon (lightGreen), non-conformity warning (red) when any Nok or validTryOnFirstTest ≠ "1". Statistics modal fields depend on the current state (§ legacy `jobStatisticsModal.jsx`, `useJobStatisticsForm.js`). Dense-table toggle, pagination 5/10/25 (default 25), sortable columns.
- **Job details page**: header boxes (job no., client/descriptive/ATT, ORC author+date, job creator+start date, delivery validatedBy+date [editable], invoice invoicedBy+percent [editable]+date, order identification + official checkbox, state icon). Cost summary (tasks total, budget total, registered expenses, profit € and % with profit **lightgreen** / loss **red**). State end-date editors. Cost table rows (Development, Components, Automation, Assembly, Machining, Materials, Others, Non-quality cost) with live deviation using `JobCostCalculations` (port of `taskHelper.js`, including the average-hour-price rule and the fallback to configured prices when nothing was budgeted). Observations. Verifications (Nok/Ok checkbox pairs + first-test select, validatedBy).

### 7.4 Readings ("Leituras")
Barcode-scanner page: autofocus input "N Orçamento"; Enter → full-screen dialog with the job state in 15 rem text, **red** if Faturada / Para faturação, **green** otherwise; auto-closes after 3 s; clears the input.

### 7.5 Placeholders kept as-is
"Adjudicações" (`/landing/jobReceivings`) shows "Recebimentos"; "Home" shows the home placeholder.

### 7.6 Database backup / restore (`.dibbak`, must be **byte-format compatible** with legacy)
- gzip stream: `"DIBBAK01"` + frames `[type:1][len:uint32 LE][payload]`; `H` JSON header `{format:"dib4t-backup", version:1, createdAt, createdBy, database, serverVersion, collections[]}`, per collection a `C` frame JSON `{name, options, indexes, count}` followed by `D` frames (raw BSON documents, use `RawBsonDocument`, no re-serialization), `Z` end `{collections, documents}`.
- Restore: gunzip, validate the whole file (all legacy Portuguese error messages), drop all user collections, recreate with options, `insertMany` in batches of 1000 (ordered, bypass validation), recreate indexes (minus `_id_`, strip `v`/`ns`).
- Info: db stats, buildInfo, per-collection `collStats` with fallback to `estimatedDocumentCount` + `indexes`.
- One operation at a time (409 with "Já existe uma operação em curso (…)"). Max upload 1 GB (`[RequestSizeLimit]`, Kestrel limits).
- **WASM caveat:** a 1 GB file must not pass through WASM memory. Upload and download go through **JS interop `fetch`/XHR** with the token header and progress events (download → blob → anchor). The Blazor component only receives progress callbacks.
- **Cross-app test:** a backup made by the legacy app restores with the new app and the other way round (byte-level frame parse + document equality).

---

## 8. Front-end (Blazor WASM) plan

### 8.1 Routes (same paths, so bookmarks keep working)
`/` (login) · `/landing/home` · `/landing/dashboard` · `/landing/budgets` · `/landing/budgets/{id}` · `/landing/job` · `/landing/job/{id}` · `/landing/leituras` · `/landing/jobReceivings` · `/landing/entity` · `/landing/settings`.
Every `/landing/*` route requires auth (`[Authorize]` + `AuthorizeRouteView`). Menu items are shown per role.

### 8.2 Client architecture
- `AuthenticationStateProvider` reads the JWT from **`sessionStorage["token"]`** (same key and storage as legacy), decodes the claims for the UI (name, roles, fieldOfCompetencies).
- `AuthHeaderHandler : DelegatingHandler` adds `x-auth-token`. A global error handler shows "An anexpected error occurred" for non-4xx errors (legacy text) and passes 4xx bodies to the caller.
- Typed clients: `IAccountApi`, `IBudgetApi`, `ITaskApi`, `IEntityApi`, `IJobApi`, `IPricesApi`, `IPrinterSettingsApi`, `IDatabaseApi`.
- Page state services (scoped): `BudgetsState`, `BudgetDetailsState` (replaces BudgetContext + CanBudgetBeEdited), `JobsState`, `JobDetailsState` (replaces JobContext + JobResultsContext), `JobStatisticsState`. Components bind to them and raise intents; the state services call the API clients.
- Shared calculations come from `Dib4t.SharedKernel` (the same code the server uses), so client and server numbers agree.
- Notifications: MudBlazor `ISnackbar`, **top-right**, success / error / warning / info. Original Portuguese texts are kept verbatim.
- JS interop module (`wwwroot/js/interop.js`): clipboard with fallback ("Link copiado!" / "Erro ao copiar!"), blob download, streamed upload with progress, `beforeunload` guard during backup/restore.
- Forms: MudForm + FluentValidation (port of the Yup schemas, e.g. descriptive regex `^[a-zA-ZçÇã,.êÊàáÀÁéÉúÚÙù,0-9_ ]+$`, max 30).

### 8.3 Theme (C5): exact legacy tokens

| Token | Dark (default) | Light (switch on) |
|---|---|---|
| Primary | `#ff9800` (MUI orange[500]) | `#03a9f4` (lightBlue[500]) |
| Secondary | `#bf360c` (deepOrange[900]) | `#673ab7` (deepPurple[500]) |
| Background | `#303030` | `#fafafa` |
| Surface / Paper | `#424242` | `#ffffff` |
| AppBar | Primary (MUI v4 AppBar uses primary in both modes) | Primary |
| Text primary / secondary | `#ffffff` / `rgba(255,255,255,0.7)` | `rgba(0,0,0,0.87)` / `rgba(0,0,0,0.54)` |
| Divider | `rgba(255,255,255,0.12)` | `rgba(0,0,0,0.12)` |
| Error | `#f44336` | `#f44336` |

Status colors used in components: warning `yellow`, error `red`, success `lightGreen` (`#90ee90`), copy icon `darkGrey`, low-priority `blue`, comments icon `#535bd5`, revisions panel background `#555559`, profit `lightgreen` / loss `red`, readings red/green, dashboard pie `red`/`orange`/`green` with transparent background and white legend, PDF rect `#eff0f2`.

- The **login page is always dark** (orange/deep-orange), background image `LogInImg.jpg` on the left (md 9/12), sign-in paper on the right with a white avatar and lock icon.
- The landing layout has a permanent drawer of 240 px that collapses to 56/72 px, `MenuLogo.jpg` (45 % width, shadow `1px 1px 15px 5px #ccc`), the user name in the AppBar title, a theme switch on the right, and the logout icon at the bottom of the menu.
- Font: **Roboto** (MUI default). Table head cells at 1.5 em. Dense tables by default.
- Assets to copy: `src/images/LogInImg.jpg`, `Logo.jpg`, `MenuLogo.jpg`, `public/favicon.ico`; PDF logo extracted from `helpers/logoForPdf.js`.
- Visual regression: Playwright screenshots of every page in both apps with the same data, compared side by side in UAT.

---

## 9. Legacy defects found during analysis: decide per item (keep for parity vs. fix)

None of the fixes needs a DB change. **Recommendation** column = suggested default.

| # | Legacy behavior (file) | Impact | Recommendation |
|---|---|---|---|
| D1 | `PATCH /job/state/:jobId` looks up `req.params.id` (undefined) → always `400 "Obra não existe"` (`routes/job.js`) | "Eliminar" and moving jobs out of Faturada/Apagada never work | **Fix** (use `jobId`) |
| D2 | `DELETE /account/:id` calls `remove(req.param.id)` → `User.deleteOne(undefined)` **deletes the first user** (`routes/account.js`) | Dangerous; not used by the UI | **Fix** (delete by id); keep it out of the UI |
| D3 | Job creation in prod: `updateFolderPath` references an undefined `newValues` when revision > 0 and the descriptive changed → 500 after the job is saved, folders not moved, labels not printed (`models/budget.js`) | Prod data/NAS inconsistency | **Fix**: implement the intended rename (rev-0 descriptive → current) |
| D4 | `moveBudgetFolderToJobs` compares a Promise with `< 1`, so the check never runs (`helpers/pathHlelper.js`) | Silent NAS failures | Run the real check, but **log a warning** instead of failing (same user-visible outcome) |
| D5 | Budget folder rename on `PUT /budget/:id` is **not** gated by prod | Fails outside prod | Gate it with the environment like the other NAS calls |
| D6 | Budget create in prod: if folder creation fails → 500, budget saved **without its default task** (the details page then never renders) | Broken budget | **Fix the order**: create the default task before the NAS step |
| D7 | `validateUpdateBudget` returns the error object instead of `{error}`, so validation is skipped | Weak validation | Apply the intended validation (the UI always sends the fields) |
| D8 | `POST /api/task` always fails validation (`budgetId` "not allowed") | Unused by the UI | Implement as intended |
| D9 | Budget search uses the user input as a raw regex | Power-user feature / 500 on bad input | Keep the regex semantics; invalid regex → 400 instead of 500 |
| D10 | Dashboard year range ends at `{year}-12-31T00:00Z` (Dec 31 excluded) | Minor stats skew | Keep for parity (or fix and announce) |
| D11 | Job `priority` default resolves to `undefined` | Old jobs without priority | Keep the read behavior (treated as low, no icon) |
| D12 | JWT without expiry, short hard-coded key | Security | Keep compatible during coexistence; move the key to config; rotate and add `exp` once the legacy app is retired |
| D13 | Import modal sets `total + 1` to force a reload (front-end) | Hack | Replace with an explicit reload in `BudgetDetailsState` |
| D14 | "Lembrar" (remember me) checkbox does nothing | Cosmetic | Keep the checkbox as-is (or remove: open question) |
| D15 | `canSeeAdjudicacoes` role exists in the UI but not in `GET /roles` | Cosmetic | Keep |

---

## 10. Phased delivery plan (vertical slices: each phase ships API + UI + tests)

### Phase 0: Discovery and safety net
- [ ] Install the .NET 10 SDK (not installed on this machine yet), Docker, Node 14 (to run the legacy app in tests).
- [ ] **Find the production MongoDB server version** (`db.version()`). It decides the C# driver version: driver 3.x needs server ≥ 4.0 (later 3.x releases ≥ 4.2); on an older server, pin driver 2.x or plan a server upgrade.
- [ ] Take a fresh `.dibbak` backup; create an anonymized dev copy.
- [ ] Run the data-shape inventory (§4.5) → fixtures.
- [ ] **Record characterization fixtures** from the legacy API: a scripted run (e.g. `.http`/Playwright API) of every endpoint in §6 against a seeded DB; save requests, responses and the resulting DB documents as golden files.
- [ ] Capture the printer bytes (`nc -l 9100`), the SSH commands (log them on a test SSH server) and sample PDFs for 5–10 real budgets.
- [ ] Decide the open questions (§14) and the defect list (§9).
- **Exit:** fixtures committed under `tests/fixtures/legacy`; decisions recorded in `docs/decisions/` (ADRs).

### Phase 1: Foundation
- [ ] Solution, projects and dependency rules (§3), `Directory.Build.props`, central packages, `.editorconfig`, analyzers, architecture tests.
- [ ] CI pipeline (build, test, Testcontainers), Dockerfile, `docker-compose.dev.yml` (Mongo at the prod version + legacy back-end + new app).
- [ ] Mongo infrastructure: `JsNumberSerializer`, conventions, class maps for all 8 collections, round-trip tests against fixtures (read every variant, write, and check the BSON types match §4.3).
- [ ] Auth: legacy-compatible token service and `x-auth-token` handler, policies, login endpoint and **login page**.
- [ ] Blazor shell: MudBlazor theme (§8.3), landing layout, role-based menu, theme toggle, logout, snackbar, global HTTP error handling.
- **Exit:** login with an existing prod user (the dev copy) works in the new app; a token issued by the new app is accepted by the legacy API and the other way round.

### Phase 2: Settings and entities
- [ ] Users (list, create, edit), prices, payment methods, printers, info tab, startup seeding.
- [ ] Entities: list/filter/sort/paginate, create/edit, types, clients list, search endpoint.
- **Exit:** characterization tests green for account/prices/printer-settings/entity endpoints; cross-app tests (§11.3) green.

### Phase 3: Budgets and tasks
- [ ] Budget list (latest revisions, search, revisions collapse, copy path, row menu), new/edit/clone dialogs, new revision, numbering (+ collision guard §4.7).
- [ ] Budget details: header grid, live totals and margin colors, lock/unlock, import, tasks editor (save on blur), description + "Concluir", payment method.
- [ ] PDF generation (PDFsharp) + download (base64 contract) + `wasPdfDownloaded`.
- [ ] `INasFileShare` (prod SSH.NET / dev no-op) for create/clone/rename.
- **Exit:** PDF visual diff within tolerance; budget workflows interoperate across both apps (create in new → edit in old → revise in new …).

### Phase 4: Jobs
- [ ] Create job (dialog with due date = today + daysNeededToDoTheWork), prod NAS move + label printing (`ILabelPrinter`).
- [ ] Jobs board: tabs/counters, priority ordering, due-date picker + countdown icons, state menu, statistics modal per state (incl. 405 handling), comments modal, priority/print menu, icons.
- [ ] Job details page: header, cost summary, state dates, cost table with live calculations, observations, verifications.
- [ ] Print endpoint + print dialog.
- **Exit:** characterization tests for every job endpoint; printer golden bytes match; SSH command sequence matches the legacy capture (against a test SSH server in a container).

### Phase 5: Dashboard, readings, backup/restore
- [ ] Dashboard pie chart (current year counters).
- [ ] Readings page (scanner flow, 3 s auto-close).
- [ ] Database info, streamed backup download, streamed restore with progress, busy lock.
- **Exit:** a legacy backup restores in the new app and a new backup restores in the legacy app, on test databases.

### Phase 6: Hardening, UAT, side-by-side production run
- [ ] Full Playwright E2E suite; screenshot comparison with legacy.
- [ ] Performance check (job lists with populated budgets: use `$lookup` aggregations instead of the legacy N+1 / load-all patterns, e.g. `getByBudgetNumber` loads every job today).
- [ ] Security review (SSH escaping, authz on every endpoint, request size limits, no secrets in logs).
- [ ] Deploy the new container next to the legacy ones (same Mongo, `ASPNETCORE_ENVIRONMENT=Production`, new port). Pilot users on the new app while others stay on the legacy app.
- **Exit:** 2–4 weeks of parallel use without data issues → plan the legacy retirement (then apply D12 and the unique index for budget numbers).

---

## 11. Testing strategy

1. **Unit (Domain/Application/SharedKernel):** numbering, flags, patch rules, `ChangeState`, `SetStatistics` per state, `UpdateStateDates`, deviation check, priority ordering, every `taskHelper` calculation (ported tests use the same inputs and outputs as the JS functions), PDF text helpers, template strings, path builders.
2. **Characterization / contract tests:** the golden files from Phase 0 replayed against the new API; JSON compared after normalization (ids, dates, `__v`), plus the resulting DB documents compared field by field **including BSON types**.
3. **Cross-application compatibility tests** (`Dib4t.Compatibility.Tests`): Testcontainers starts Mongo (prod version) and the **legacy Node back-end** image. Scenarios write through one API and read/modify through the other (budget → revision → job → statistics → billing; entity edit; user edit with password; backup/restore). This is the guarantee behind C3 and C4.
4. **Integration:** repositories against real Mongo; NAS adapter against an `openssh-server` container; printer against an in-test TCP listener.
5. **Component (bUnit):** state-dependent rendering (menu items, statistics modal fields, lock behavior, colors).
6. **E2E (Playwright):** login, budget lifecycle, job lifecycle, settings, readings, backup.

---

## 12. Deployment and side-by-side operation

```
NAS / server host
├─ mongo            :4002   (unchanged, shared)
├─ legacy back-end  :4001   (unchanged)
├─ legacy front-end :4000   (unchanged; derives API port by replacing the last digit with 1)
└─ dib4t-dotnet     :4100   (new: UI + /api on one origin, ASPNETCORE_ENVIRONMENT=Production)
```
- Config through env vars: `Mongo__ConnectionString`, `Jwt__Key`, `Nas__Host`, `Nas__User`, `Nas__Password` (or key file), Kestrel URL.
- Container user without root privileges, health check, log volume.
- Rollback: stop the new container. Nothing in the DB depends on it (C4).

---

## 13. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Subtle BSON type drift breaks the legacy app | Medium | §4.3 rules, type assertions in tests, cross-app suite |
| Old MongoDB server not supported by the current C# driver | Unknown | Phase 0 check; pin a driver version |
| PDF not pixel-identical | Medium | PDFsharp absolute layout, same Roboto fonts, visual diff |
| Printer payload differs (line terminator/encoding) | Medium | Byte capture in Phase 0 |
| Budget number collisions between apps | Low/Medium | §4.7 guard, limited pilot, unique index after retirement |
| Large backup files in the browser | Medium | JS streaming interop (§7.6) |
| Scope: legacy quirks reproduced by accident or "fixed" by accident | Medium | Characterization tests + explicit defect decisions (§9) |

---

## 14. Open questions (need an answer before or during Phase 0)

1. Confirm **.NET 10 LTS** and **MudBlazor**.
2. Port and hostname for the new app in production (proposed `:4100`)?
3. Production MongoDB **server version**?
4. Approve the defect decisions in §9 (especially D1, D3, D6).
5. Version shown in the Info tab for the new app (`1.0.0`?).
6. Should the new app also accept tokens from the legacy app (shared key, recommended during coexistence)?
7. Are there other consumers of the API (scanner stations, scripts) that must keep working?
