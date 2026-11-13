# Dib4t — Rewrite plan: Node/React ➜ .NET (ASP.NET Core + Blazor WebAssembly)

> Status: **Plan / not started** · Created 2026-11-12 · Updated 2026-11-13 (two databases: legacy untouched, new app on MongoDB 9.0.8 fed by legacy backups; port 3010; latest packages; test policy; responsive layout; NAS and printers not reachable from the dev network: converted from the legacy code, real-device checks deferred to the owner, §5.1)
> Source of truth for the behavior: `../back-end` (Node/Express/Mongoose) and `../front-end` (React 17 / MUI v4).

---

## 1. Goal and hard constraints

Rebuild the Dib4t ERP web app (budgets, jobs, entities, settings, dashboard, readings) on a .NET stack, **keeping every feature**.

| # | Constraint | What it means in practice |
|---|------------|---------------------------|
| C1 | **.NET, WebAssembly, server side** | ASP.NET Core server (REST API + host) and a Blazor WebAssembly client. |
| C2 | **Two databases, same data model** | The old app keeps its database (`mongo:4.1.4`). The new app has **its own database** on the latest stable MongoDB (**9.0.8**, Docker, x86-64). Same database name (`dib4t`), collections and document shapes, so legacy data works as-is. (§2.2) |
| C3 | **Old and new app run at the same time; data moves by backup** | Both apps run side by side, each on its own database. **A backup (`.dibbak`) downloaded from the old app must always import into the new app** (restore), with every feature working on the imported data. This is the only data path between them (§4). |
| C4 | **No new data or fields in the DB** | No new collections, fields, indexes, validators, lock documents or migration markers. The new app stores nothing the old app does not already store, so its data stays in the legacy model. |
| C5 | **Same color theme** | Same palettes (dark by default, light toggle), same status colors, same logos and imagery. |
| C6 | **Production-only logic kept** | Everything guarded by `NODE_ENV === "prod"` (SSH folder management on the NAS, label printing, production DB/port, print endpoint guard) must be reproduced and guarded by the .NET environment (`Production`). |
| C7 | **Senior-level .NET practices** | Clean layering, DI, async with cancellation, options pattern, no DB access from UI components, tests. See §3. |
| C8 | **Tests ship with every feature** | No feature is done without **unit tests** and an **end-to-end (Playwright) test**. E2E tests also run the same scenario against the **old app** and compare behavior (§11). See the Definition of Done in §10. |
| C9 | **Fix the legacy layout bugs** | The old UI breaks when the browser font size or zoom changes and at other screen resolutions. The new UI must be responsive and scale with font size and zoom (§8.4). Colors stay the same (C5); layout is allowed to differ. |
| C10 | **No changes to the old app** | The legacy front-end, back-end, configuration and database (`mongo:4.1.4`, port 4002) stay exactly as they are. Tests and tooling use the old app as-is. |

---

## 2. Target stack (recommended)

| Concern | Choice | Why |
|---|---|---|
| Runtime | **.NET 11** (SDK 11.0.100) | Newest release. It is STS, but its end of support (2028-11-14) is the **same date as .NET 10 LTS**, so it costs no support time. Fallback: .NET 10 LTS if a library blocks (see MudBlazor in §2.1). |
| Server | ASP.NET Core 11, **Controllers** (API) + Blazor Web App host | Controllers suit a large, existing REST surface that has to stay identical. |
| Client | **Blazor WebAssembly** (Blazor Web App template, `InteractiveWebAssembly` render mode for all pages, **prerendering off**) | Meets "WebAssembly". With prerendering off, the token lives in browser storage (`localStorage`, so the user stays logged in, §4.6). |
| UI kit | **MudBlazor** (Material Design) | Closest to MUI: same components (AppBar, Drawer, Tabs, Table, Dialog, Snackbar, DatePicker) and palette model, so the look carries over. |
| DB driver | **MongoDB.Driver** (official C# driver) | Fine-grained control over BSON types and `$set` updates, which compatibility depends on (§4). **3.13.0** (latest). Fully supports MongoDB 9.0 (since 3.10). See §2.2. |
| Validation | **FluentValidation** | Ports the Joi schemas. Shared between server and client where useful. |
| Auth | `Microsoft.AspNetCore.Authentication.JwtBearer`, token read from the `x-auth-token` header | Same header and claims as the old API; its own key (§4.6). |
| Passwords | **BCrypt.Net-Next** | Reads and writes the bcryptjs `$2a/$2b$10$` hashes already in `users`. |
| SSH (prod) | **SSH.NET** (`Renci.SshNet`) | Replaces `ssh2-promise` for NAS folder operations. |
| Label printer (prod) | `System.Net.Sockets.TcpClient` | Replaces `telnet-client` (raw TCP to port 9100, no negotiation, 1500 ms timeout). |
| PDF | **PDFsharp 6 (MIT)** | The legacy PDF is laid out with absolute coordinates. PDFsharp draws at exact points, so it matches 1:1. QuestPDF is an option if its license fits. |
| Logging | **Serilog** (console + rolling file) | Replaces winston; keeps an `uncaughtExceptions` style file sink. |
| Charts | MudBlazor `MudChart` (pie) | Replaces react-google-charts on the dashboard. |
| Tests | xUnit v3, **AwesomeAssertions**, NSubstitute, **Testcontainers (`mongo:9.0.8`)**, **bUnit**, **Playwright** + axe-core | Unit, integration, component and E2E coverage. AwesomeAssertions is the Apache-2.0 fork of FluentAssertions, which became commercial in v8. |
| Packaging | Docker (`mcr.microsoft.com/dotnet/aspnet:11.0`) | Same deployment style as the current apps. |

> Avoid MediatR and AutoMapper (both moved to commercial licenses in 2025). Use plain application services and explicit mapping (or Mapperly, a source generator under MIT).

### 2.1 Package versions (latest stable on 2026-11-12, managed in `Directory.Packages.props`)

| Package | Version | Note |
|---|---|---|
| .NET SDK / runtime, `Microsoft.AspNetCore.*`, `Microsoft.Extensions.*` | **11.0.x** (SDK 11.0.100) | `Components.WebAssembly`, `Components.WebAssembly.Server`, `Mvc.Testing`, `TimeProvider.Testing` at 11.0.0 |
| **MongoDB.Driver** | **3.13.0** | Latest; supports server 9.0. |
| MudBlazor | 9.12.0 | Ships `net8.0`/`net9.0`/`net10.0` builds, no `net11.0` build yet; the `net10.0` build runs on .NET 11. Verified in Phase 1 (smoke test of every component used); if it fails, the solution falls back to .NET 10 LTS. |
| FluentValidation (+ DependencyInjectionExtensions) | 12.2.0 | |
| BCrypt.Net-Next | 4.2.1 | |
| SSH.NET | 2026.0.0 | |
| PDFsharp | 6.5.0 | |
| Serilog.AspNetCore / Serilog.Sinks.File | 10.0.0 / 7.0.0 | |
| Riok.Mapperly | 4.3.1 | |
| xunit.v3 / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk | 4.0.3 / 4.0.1 / 18.12.0 | |
| AwesomeAssertions | 9.6.0 | |
| NSubstitute | 6.2.0 | |
| Testcontainers.MongoDb | 4.16.0 | Image pinned to `mongo:9.0.8`, platform `linux/amd64` in CI (§2.2) |
| bunit | 2.15.2 | Has a `net11.0` build |
| Microsoft.Playwright / Deque.AxeCore.Playwright | 1.64.1 / 4.14.0 | |
| NetArchTest.Rules | 1.3.2 | |
| coverlet.collector | 10.1.0 | |

- No `AspNetCore.HealthChecks.MongoDb`: the `/health` Mongo ping is a 10-line custom `IHealthCheck`, which avoids a package that could pull a newer driver.
- Renovate/Dependabot keeps everything current. Versions are re-checked at the start of Phase 1. The Mongo image tag is updated only within `9.0.x` patch releases, and tests run on the new tag before production does.

### 2.2 Databases: legacy stays on 4.1.4, new app gets its own MongoDB 9.0.8

```
NAS (AMD RX-421ND, x86-64)
├─ mongo:4.1.4  :4002  ← legacy back-end (unchanged, C10)
└─ mongo:9.0.8  (docker network only) ← dib4t-dotnet :3010
        ▲
        └── .dibbak backup downloaded from the old app, restored in the new app
```

- The legacy database is **never touched** by the new app: no connection string to it, no reads, no writes. The new app talks only to its own server.
- New database server: **`mongo:9.0.8`**, platform `linux/amd64` (an `arm64` image also exists for Apple-silicon dev machines). Same database name `dib4t`.
- **CPU:** MongoDB 5.0+ needs AVX on x86-64. The NAS has an **AMD RX-421ND** (AMD Embedded R-Series "Merlin Falcon", Excavator cores), which supports AVX and AVX2. To be confirmed on site by the owner (`grep -o -m1 -w avx /proc/cpuinfo` + test start; §5.1, check N1). The NAS is not reachable from the development network.
- Container: named volume for `/data/db`, `--wiredTigerCacheSizeGB` sized so both MongoDB servers fit in the NAS memory together, `ulimit nofile 64000`, health check `mongosh --eval "db.adminCommand('ping')"`. Not published on a host port; it is reachable only from the new app's Docker network (admin through `docker exec`).
- Standalone server (like today): no transactions or change streams, `retryWrites=false`. At startup the app logs `buildInfo.version` and stops if the server version is outside what the driver supports.
- Use current APIs only (e.g. `$collStats`, not the deprecated `collStats` command).

**Getting data into the new app**
1. In the **old app**: Settings → Base de dados → **download the backup** (`.dibbak`). This is an existing feature; nothing changes in the old app.
2. In the **new app**: Settings → Base de dados → **restore** that file (streamed upload with progress, §7.6). The restore replaces everything in the new database.
3. The restore **verifies itself** when it finishes: it re-reads the new database and compares it with the file (counts per collection, indexes, per-document checksum of field names, BSON types and values), and shows the result in the restore dialog (and in the log). No separate tool is needed.

This can be repeated as often as needed (test data, pilot, final switch). Each restore replaces the new database, so anything entered only in the new app since the last import is lost; the restore dialog says so.

---

## 3. Solution architecture

```
dib4t-dotnet/
├─ Dib4t.slnx
├─ Directory.Build.props          # net11.0, Nullable=enable, TreatWarningsAsErrors, analyzers, LangVersion latest
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
│                                 #   file download/upload streaming, localStorage).
└─ tests/
   ├─ Dib4t.Domain.Tests/
   ├─ Dib4t.Application.Tests/
   ├─ Dib4t.Infrastructure.Tests/   # Testcontainers mongo:9.0.8 (same image as production)
   ├─ Dib4t.Server.Tests/           # WebApplicationFactory API tests
   ├─ Dib4t.ImportCompatibility.Tests/ # Legacy app (unchanged, mongo:4.1.4) makes backups → new app imports them (§11.3)
   ├─ Dib4t.Client.Tests/           # bUnit
   └─ Dib4t.E2E/                    # Playwright (.NET): new app E2E, legacy-vs-new comparison, layout matrix
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
- **No secrets in the repo.** The legacy repo commits its JWT key and the SSH password in `config/default.json`. In the new repo they live in user-secrets (dev) and environment variables (prod). Rotating the SSH credential is recommended.
- `TimeProvider` for every "now" (budget year, state dates, PDF date), so tests are deterministic.
- `IExceptionHandler` returns `500 "Something failed."` as plain text (legacy parity) and logs the exception.
- Health check `/health` (Mongo ping).
- `Result<T>` with typed errors (`NotFound`, `Validation`, `Conflict`, `BusinessRule`). Controllers map them to the **legacy status code and message** (§6).

---

## 4. Legacy backup import contract and data model (most critical section)

The two apps never share a database. The **guarantee** this plan gives is: **any backup downloaded from the old app can be restored in the new app, and every feature then works on that data.** The new app also keeps the legacy data model when it writes, so imported and new documents look the same (C4) and later imports never meet a different shape.

### 4.0 What the import guarantee covers
1. **Format:** every `.dibbak` the legacy code produces (format version 1, header `serverVersion` 4.1.4 or anything else) is accepted. The new app reads the legacy frame format byte for byte (§7.6).
2. **Content:** every document is restored unchanged: same `_id`s, field names, values and BSON types (raw BSON copy, no re-serialization). Collection options and index definitions are recreated, after cleaning anything MongoDB 9.0 rejects (§7.6).
3. **Behavior on imported data:** login works with the imported users and their existing bcrypt passwords; budgets, revisions, tasks, jobs in every state, statistics, comments, entities, prices, payment methods and printer settings all display and can be edited. Old documents with missing fields, `Int32`/`Double`/`Int64` mixes or older shapes are handled by the read rules (§4.4).
4. **Errors:** a damaged or wrong file is rejected with the legacy Portuguese messages and leaves the new database unchanged (validated fully before anything is dropped).
5. **Size:** up to the 1 GB legacy limit, streamed (never fully in WASM memory).
How it is proven: §11, item 3 (import compatibility suite, runs on every pull request).

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

### 4.3 Write rules (new app → its DB, keeping the legacy model)

1. **Exact BSON types.** ObjectId references are stored as `ObjectId`, never strings. Dates are BSON `Date` (UTC). Numbers are `Int32` when the value is an integer that fits in 32 bits, otherwise `Double`. That is what the Node driver writes for JS numbers. **Never write `Decimal128` or `Int64`**, so new documents have the same types as imported ones (sums, sorts and comparisons in queries stay consistent). Implement this with one custom `JsNumberSerializer` used for every numeric field, with C# type `double`.
2. **Exact strings.** Enum values are Portuguese strings with accents. Keep them as constants in `SharedKernel`, compare ordinally, and store Unicode NFC.
3. **Updates use `$set` on changed fields only** (`UpdateOne` + `UpdateDefinition`), never a full-document `ReplaceOne`. That matches Mongoose `save()` semantics (it only `$set`s modified paths) and keeps concurrent edits by two users from overwriting each other's untouched fields.
4. **`__v` emulation.** Inserts write `__v: 0`. When an update replaces an array (`roles`, `commentType`), also `$inc: { __v: 1 }`, like Mongoose versioning.
5. **Embedded sub-documents get an `_id`** (new `ObjectId`) when created or replaced as a whole, the same way Mongoose assigns them.
6. **Inserts include the Mongoose defaults** from §4.2, so queries such as `hasGenerateJob: true` behave the same on new and imported documents.
7. **Round-trip unknown fields**: `[BsonExtraElements]`-style catch-all in class maps, so a field the new app doesn't know is never lost.
8. **No schema side effects.** No `CreateIndex`, no collection validators, no new collections, no "app metadata" documents (C4). Indexes are the ones that come with the imported backup.

### 4.4 Read rules (DB → new app)

1. Tolerate both `Int32` and `Double` (and legacy `Int64`) for every number.
2. Apply the Mongoose defaults when a field is missing (old documents predate some fields: `hasGenerateJob`, `priority`, `jobStatisticsDates`, `commentType`, `invoicedPercent`, …).
3. A missing `priority` behaves like the legacy app: it sorts into the "low" bucket and shows no icon.
4. Ignore `__v` in the domain, but keep it in persistence.

### 4.5 Data-shape inventory script (Phase 0)

Before any code, run an aggregation per collection (`$objectToArray` + `$type`) on a **copy** of production. It counts the BSON type of every field path, finds missing fields, Int32/Double mixes, string-vs-Date drift, and so on. The output becomes test fixtures, and serializers must accept every observed variant.

**Done 2026-11-13** on the production backup of 2026-11-11, by reading the `.dibbak` BSON directly with the .NET `MongoDB.Bson` library (`tools/legacy/dibbak.cs inventory`, same result as the aggregation, and a first proof that .NET reads the legacy backup format). Full table: `docs/data/legacy-data-shapes.md`; machine-readable: `tests/fixtures/legacy/data-shapes.json`. Findings the class maps, serializers and UI must handle (all are now test cases):
1. **Numbers:** no `Int64` or `Decimal128` anywhere. Int32/Double **mixes** in `budgets.total`, `jobs.finalPrice`, `jobs.materials`, `devStatistic.{budgetedHours,deviation,numberOfHoursUsed}`, `machiningStatistic.{budgetedHours,deviation}`, `tasks.{quantity,unitPrice}`; the doubles are fractional (e.g. 0.5 h) → `double` + `JsNumberSerializer` (§4.3) confirmed. Negative deviations are normal.
2. **Nulls where the schema has none:** `users.roles[]` contains **`null` elements** (30 of 60, in every user's array); `jobs.deliveryDate` null in 126 jobs (missing in 58 more); `jobs.costNonQuality` null once; `jobStatisticsDates.devEndedInDate` null once. Role checks must skip `null`; round-trips must keep the nulls (arrays are never rewritten unless the user edits them).
3. **Missing fields:** `budgets.description` (50); `jobs.commentType` (934), `comments` (139), `createdBy` (270), `invoicedBy` (299), `jobStatisticsDates` (124) and most dates inside it, `machiningStatistic.validatedBy`/`assemblyStatistic.validatedBy`; `users.fieldOfCompetencies` (1). Read defaults per §4.4.
4. **Unknown field:** `jobs.isHightPriority` (Boolean, 7 old jobs) is not in the current schema → must round-trip untouched (§4.3 rule 7, extra elements).
5. **Dates:** `jobs.deliveryDate` has a typo year (**0022**-07-29). Dates before 1970 must read, display and save without errors (no `DateTimeOffset` assumptions, no range validation on read). No string-vs-Date drift (no date-like strings in date fields).
6. **Strings:** leading/trailing whitespace is common (`budgets.description` 1,277, `descriptive` 34, `jobs.orderIdentification` 34, `tasks.description` 346) and a tab inside `entities.fiscalNumber`; **never trim on import or read**. 6 non-NFC strings (`budgets.description`, `tasks.description`): keep them as stored; NFC applies only to values the new app writes (§4.3 rule 2). Many empty strings (`jobs.comments` 953, task descriptions 5,288).
7. `jobs.priority` is present in every production job (`Média` 1,108), but the "missing priority" path stays (§4.4.3) for older backups. `assemblyStatistic.validTryOnFirstTest` is always a string. `budgets.client`/`madeBy` are always ObjectIds. Payment-method texts are long (up to 109 chars) → layout.
8. Only `_id` indexes and empty collection options: nothing for the 9.0 restore cleanup to do on current data (keep the cleanup for older backups).

### 4.6 Authentication

- Same header **`x-auth-token`** and same claims `{ _id, name, email, roles, fieldOfCompetencies }` as the legacy API, so the client and the API contract stay the same.
- The new app has **its own signing key** (≥ 256 bits, from configuration/secrets) and uses the standard `JwtBearer` handler (reading the token from `x-auth-token` in `OnMessageReceived`). Tokens of the old app are not accepted and not needed: the systems are separate.
- Passwords: the imported bcryptjs hashes (`$2a$`/`$2b$`, cost 10) are verified with BCrypt.Net-Next; new or changed passwords are written in the same format.
- Responses keep the legacy texts: missing token → `401 "Access denied. No token provided."`; invalid or expired token → **`400 "Invalid token."`**; settings role missing → `401 "Não tem permissão para concluir esta acção."`.
- Policy `CanSeeDefinicoes` maps to `authCanSeeSettings`.

**Staying logged in (decided):** the user never has to log in again; only the logout button ends the session.
- Tokens have **no expiry**, like the old app: no `exp` claim, `TokenValidationParameters.RequireExpirationTime = false` and `ValidateLifetime = false`.
- The client keeps the token in **`localStorage["token"]`** (the old app uses `sessionStorage`, which loses the login when the tab or browser closes). So the login survives closing the tab, the browser and restarting the computer, and is shared by all tabs. A `storage` event listener logs out every open tab when one tab logs out.
- **Logout** (button at the bottom of the menu): removes the token from `localStorage` and goes to the login page. That is the only way the session ends for the user.
- The key is never changed in normal operation, because changing it (`Jwt:Key`) logs **everyone** out. That is also the emergency switch if a token is stolen: no server-side token list is possible without new data (C4), the same as in the old app.
- After a new deploy, the token stays valid (same key). After a backup restore it stays valid too, as long as the user still exists with the same `_id` (imports keep `_id`s).
- If the server answers `400 "Invalid token."` (e.g. after a key change), the client deletes the stored token and shows the login page, instead of showing errors.
- **Roles are checked against the database, not only the token.** Because a token lives forever, the server loads the user by the token's `_id` on each request (cached in memory for 30 s) and uses the user's **current** roles for authorization. So removing a role, or deleting a user, takes effect within 30 s without a new login (a deleted user's token gets `400 "Invalid token."`). The old app trusts the roles inside the token; this is a deliberate difference, with no new data.
- The client refreshes name and roles for the menu on start from **`GET /api/account/me`** (new-app-only endpoint, reads the user document; added to §6), so a changed role shows up without logging in again.

### 4.7 Running two separate systems at the same time

| Risk | Mitigation |
|---|---|
| **Data diverges.** Work done in one app is not in the other. | One app is the system of record at a time. Until the switch, the old app is; the new app runs on imported copies (pilot, training, UAT). At the switch, a final backup is imported and the old app is no longer used for daily work (it stays installed, unchanged, for consultation). |
| **Duplicate budget numbers and NAS folders.** Each app numbers budgets from its own data, and both would create folders under `/share/Orçamentos/{year}/...` in production. | While the old app is the system of record, the new app runs in Production with its NAS and printer side effects **switched off by configuration** (`Nas:Enabled=false`, `LabelPrinter:Enabled=false`; §5). They are switched on at the switch-over. |
| **Re-import overwrites new-app work.** | Restore warns and requires confirmation; the new app makes its own backup automatically before a restore. |
| **Budget numbering inside the new app** (two users at once). | `SemaphoreSlim` around numbering plus a re-check after insert (same rule the old app lacks). |

---

## 5. Production-only behavior (must be preserved, gated by `IHostEnvironment.IsProduction()`)

| Legacy trigger | Behavior to reproduce | New home |
|---|---|---|
| `NODE_ENV=prod` → port | Dev 3001, prod 4001. The legacy front-end derives the API URL from its own origin (`origin` with the last char replaced by `1`). | New app: one origin serves UI + `/api`. Port set by `ASPNETCORE_URLS`: **`3010`** in every environment, which is different from the legacy ports (3000/3001 dev, 4000/4001 prod), so both run side by side. |
| (new) | — | `Nas:Enabled` and `LabelPrinter:Enabled` (default `true` in Production, always off elsewhere) so the new app can run in Production without touching the NAS or printers while the old app is still the system of record (§4.7). |
| `NODE_ENV=prod` → DB | `db` (localhost) vs `serverDb` (NAS host, port 4002). | The new app's **own** database (`mongo:9.0.8`), `MongoOptions.ConnectionString` per environment. It never points at the legacy server on 4002. |
| `POST /api/budget` (create) | After save: SSH `mkdir -p` of `/share/Orçamentos/{currentYear}/'{client}'/'{budgetNumber} - {descriptive}'`, plus `/Geometria` and `/Documentos`, then `test -d`. If the folder is missing → `500 "Erro ao criar pasta"`. | `INasFileShare.CreateBudgetFolderAsync`. Prod: SSH.NET; other envs: no-op that logs. |
| `POST /api/budget/clone/:id` | Same folder creation as create. | Same. |
| `PUT /api/budget/:id` (**not gated in legacy**) | If `revision == 0` and `descriptive` changed: `cd /;mv {oldPath} {newPath}` (year taken from `budget.date`). | `INasFileShare.RenameBudgetFolderAsync`. See defect D5 about gating. |
| `POST /api/job` (create) | In prod after save: (1) `updateFolderPath(revZero.descriptive)`; (2) `moveBudgetFolderToJobs("{budgetYear}/'{client}'/'{num} - {desc}'")`: `mkdir -p /share/Obras/{p}/CAD`, `/CAD/Equipamento`, `/CAM`; `mv share/Orçamentos/{p}/Documentos share/Obras/{p}/"Informações Técnicas"`; `mv …/Geometria …/CAD/Artigo`; (3) print labels on **every** printer with template `producao`, then every printer with `maquinacao`. | `JobService.CreateAsync` → `INasFileShare` + `ILabelPrinter`. |
| `POST /api/job/print/:budgetId` | Non-prod → `400 "Só funciona em produção!"`. Prod: print selected templates (`producao`/`maquinacao`) to all matching printers. Error if none is configured. | `LabelPrintingService`. |

**Label printer protocol** (`PrinterSettings.print`): raw TCP to `ip:port`, connect timeout 1500 ms, send the template string, close, swallow errors (logged; returns false). Templates (date is `job.startDate` as `d/M/yyyy`, no zero padding, server local time):
- `producao`: `"\n      ia3,^TS001{num},{date},{descriptive},{client},{num},\n      "` (the leading/trailing whitespace is part of the payload)
- `maquinacao`: `"ia3,^TS002{num},{date},{descriptive},{client},"`

**Wire behavior, derived from the legacy code** (`models/printerSettings.js` + `telnet-client` 1.4.9 `lib/index.js`, no capture needed):
- Connect with `negotiationMandatory: false`, so `connect()` resolves as soon as the TCP connection is up; nothing is written before the payload (`initialLFCR`/`initialCTRLC` are off).
- `send(data)` appends the output record separator `ors`, default **`"\n"`** (LF only), and writes the string with Node's default encoding **UTF-8** (accents in `descriptive`/client name are multi-byte).
- So the bytes on the wire are exactly `UTF8(template + "\n")`, followed by a normal close (`socket.end()`, FIN).
- `send()` then waits `sendTimeout` (2000 ms) for a reply. A label printer normally sends nothing back, so legacy rejects with `"response not received"`, logs `Printer error: …` and `print()` returns `false`. **Every caller ignores the return value** (`routes/job.js`), so this has no visible effect except about 2 s of latency per printer.
- New app: write `UTF8(template + "\n")`, flush, close; no wait for a reply (decision: same bytes, same visible result, without the 2 s per printer). Errors are logged and swallowed as in legacy.
- Golden-file tests assert these bytes against an in-test TCP listener. The real printers are checked by the owner later (§5.1, check N3).

**SSH safety:** legacy builds shell commands by string concatenation, with client and descriptive in single quotes. The new adapter must produce **the same resulting paths** but escape arguments correctly. Commands are logged at Information level, without credentials.

### 5.1 NAS and printers: converted from code, real-device checks deferred

The NAS (`192.168.2.10`) and the label printers are **not reachable from the development network**. So:
- Everything that depends on them is **converted from the legacy source code**, like the rest of the app: SSH commands and paths from `helpers/pathHlelper.js`, `models/budget.js` and `routes/budget.js`/`routes/job.js`; printer bytes from `models/printerSettings.js` + `telnet-client` (above). The expected command sequences and byte payloads are written down as golden files **from the code**, not from captures.
- Automated tests use **local fakes only** (an `openssh-server` container that records commands, an in-test TCP listener for the printer). No test, script or development step connects to the real NAS or printers.
- The owner runs the real-device checks later, on site. They are tracked here and are **not blockers** for any phase before Phase 6:

| # | On-site check (owner) | Where it is used |
|---|---|---|
| N1 | NAS CPU has AVX (`grep -o -m1 -w avx /proc/cpuinfo`), free memory/disk for a second MongoDB server, test start of `mongo:9.0.8` on a throwaway volume next to the running 4.1.4 (§2.2). | Deployment (§12) |
| N2 | Budget create/clone/rename and job create make and move the expected folders under `/share/Orçamentos` and `/share/Obras` (compare with the old app on a test client/budget). | Phase 3, Phase 4 |
| N3 | Both label templates (`producao`, `maquinacao`) print correctly on the real printers, including accented text. | Phase 4 |
| N4 | Deploy on the NAS, with NAS/printer side effects off until switch-over (§4.7). | Phase 6 |

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
| | `GET /api/account/me` | T | **New-app only**: current user `{_id,name,email,roles,fieldOfCompetencies}` from the DB (§4.6) |
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
- **Import of legacy backups (the key requirement, §4.0):** before recreating them on MongoDB 9.0, clean collection options and index specs from the legacy server: keep only options 9.0 knows (`unique`, `sparse`, `partialFilterExpression`, `expireAfterSeconds`, `collation`, text/2dsphere options, …), drop `v`, `ns`, `background` and anything unknown, and log what was dropped. Restore continues on the server if the browser disconnects, and progress can be read again. Before a restore, the new app automatically keeps a backup of its current database.
- Backups made by the new app use the same format and restore in the new app. Restoring them in the old app is not required (and not done, C10).

---

## 8. Front-end (Blazor WASM) plan

### 8.1 Routes (same paths, so bookmarks keep working)
`/` (login) · `/landing/home` · `/landing/dashboard` · `/landing/budgets` · `/landing/budgets/{id}` · `/landing/job` · `/landing/job/{id}` · `/landing/leituras` · `/landing/jobReceivings` · `/landing/entity` · `/landing/settings`.
Every `/landing/*` route requires auth (`[Authorize]` + `AuthorizeRouteView`). Menu items are shown per role.

### 8.2 Client architecture
- `AuthenticationStateProvider` reads the JWT from **`localStorage["token"]`** (legacy: `sessionStorage`; changed so the login is never lost, §4.6), decodes the claims for the UI (name, roles, fieldOfCompetencies), and syncs logout across tabs.
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
- Visual comparison: Playwright screenshots of every page in both apps with the same data, reviewed side by side in UAT. Colors, texts, icons and content must match; **layout may differ** where it fixes a legacy bug (§8.4).

### 8.4 Responsive and scalable layout (C9: fix the legacy layout bugs)

**Legacy causes found in `../front-end/src`** (do not port these patterns):

| Where | Problem |
|---|---|
| `jobDetais/jobDetails.css` | Grid rows in viewport units (`grid-template-rows: 12vh 65vh`, `height: 4vh`, `margin-top: 24vh` / `8vh`) mixed with fixed `rem` heights (`8rem`, `8.5rem`, `6rem`) and fixed widths (`7rem`, `20%`). Content overflows or overlaps when the font grows or the window is short. |
| `jobDetais/jobDetails.css`, `jobDetailsHead.jsx` | `position: absolute` boxes (`right: 0`) and inline `width: 60px` that ignore the content size. |
| `account/login.css` | Image box `height: 100vh; width: 100vh` (width tied to the height), so it is cut off or overflows on narrow or portrait screens. |
| `budgetDetails/budgetGeneralDetails.jsx` | Totals and margin typed in `fontSize: 30` (px) that does not follow the user's font size. |
| `dashboard/dashboardIndex.jsx` | Chart with a fixed `height="600px"`. |
| `genericStyles/tableStyles.js`, `settings/settingsStyles.js` | Tables with `minWidth: 750` / `650` and no horizontal scroll container, so they push the page wider than the screen. |
| `genericStyles/landingStyles.js`, `landing.jsx` | `height: 100vh` containers and an `absolute` AppBar; the permanent drawer stays open on small screens and squeezes the content. |
| `job/jobStyle.js` | Fixed `48px` icon columns. |

**Rules for the new UI**
1. **Sizes in `rem`/`em`, not `px`**, for type, spacing, and component heights. The root font size is never hard-coded, so the browser font size setting and zoom scale the whole UI. Only borders and shadows may stay in `px`.
2. **No fixed heights for content.** Use `min-height` and let content grow. `100dvh` only on the outer shell, never on inner panels; no `vh` for margins or grid rows.
3. **Layout with CSS Grid/Flexbox and MudBlazor breakpoints** (`MudGrid`/`MudItem xs/sm/md/lg`), never `position: absolute` for page layout. Header boxes on Job details and Budget details wrap onto more rows when space runs out.
4. **Breakpoints:** phone `< 600px`, tablet `600–960px`, laptop `960–1280px`, desktop `≥ 1280px` (MudBlazor defaults). The drawer is **temporary (overlay)** below `md`, **mini (56/72 px → rem)** on `md`, and **permanent** on `lg` and up; the AppBar is a normal sticky element.
5. **Tables** sit in a horizontal scroll container (`MudTable` with `HorizontalScrollbar`), keep a sticky header, and on phones hide low-priority columns or switch to a card list (budgets, jobs, entities).
6. **Dialogs** use `MaxWidth` + `FullWidth` and become `FullScreen` below `sm`.
7. **Login page**: image column hidden below `md` (form centered); image uses `object-fit: cover` instead of `width: 100vh`.
8. **Charts** size to their container (aspect ratio), not a fixed `px` height.
9. **Text wraps** instead of overflowing (`overflow-wrap: anywhere` for paths, budget descriptions, comments); numbers use `tabular-nums`.
10. **Accessibility baseline:** WCAG 2.2 AA reflow (usable at 320 CSS px width / 400 % zoom without two-way scrolling, except data tables), text spacing, and 200 % text resize without loss of content.

**Verification (automated, part of the E2E suite, see §11):** every page is opened in a matrix of viewports **360×640, 768×1024, 1366×768, 1920×1080, 2560×1440**, with **browser zoom 100 % / 150 % / 200 %** and **root font size 16 px / 20 px / 24 px**. Each case asserts:
- no horizontal scroll on the page (`document.documentElement.scrollWidth <= innerWidth`), except inside table scroll containers;
- no overlapping or clipped elements among key controls (bounding boxes of buttons, inputs, header boxes do not intersect, and are inside the viewport or a scroll container);
- primary actions are visible and clickable;
- an axe-core accessibility scan has no serious/critical violations.
Screenshots of the matrix are kept as baselines (Playwright visual comparison) so later layout regressions fail the build.

---

## 9. Legacy defects found during analysis: decide per item (keep for parity vs. fix)

None of the fixes needs a DB change, and none touches the old app (C10): fixes are made in the new app only. **Recommendation** column = suggested default.

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
| D12 | JWT without expiry, short hard-coded key | Security | **Keep no expiry** (decided: users stay logged in until they press logout, §4.6). **Fix the key** in the new app only: own ≥ 256-bit key from secrets, not in the code. The old app is left as is (C10) |
| D13 | Import modal sets `total + 1` to force a reload (front-end) | Hack | Replace with an explicit reload in `BudgetDetailsState` |
| D14 | "Lembrar" (remember me) checkbox does nothing | Cosmetic | Remove it: the new app always remembers the login (§4.6) |
| D15 | `canSeeAdjudicacoes` role exists in the UI but not in `GET /roles` | Cosmetic | Keep |

---

## 10. Phased delivery plan (vertical slices: each phase ships API + UI + tests)

### Phase 0: Discovery and safety net
- [x] Install the .NET 11 SDK, Docker (for Testcontainers and the `mongo:9.0.8` dev database), Playwright browsers. Node 16.20.2 is already installed locally and is used to run the legacy app. Check with `tools/check-prereqs.sh`.
  - [x] .NET SDK 11.0.100 in `~/.dotnet` (user-level install), pinned in `global.json`.
  - [x] Playwright browsers for Microsoft.Playwright 1.64.1 (chromium-1248, firefox-1555, webkit-2370).
  - [x] Node 16.20.2.
  - [x] Docker Desktop 29.9.3 (Apple silicon), `mongo:9.0.8` pulled and smoke-tested; legacy `mongo:4.1.4` (amd64) runs under emulation.
- [~] ~~Confirm AVX on the NAS, check memory/disk, test-start `mongo:9.0.8` there~~ → **deferred to the owner (on site)**: the NAS is not reachable from the development network. Tracked as check N1 in §5.1; not a blocker before Phase 6.
- [x] Download real `.dibbak` backups from the old app (production) for the import tests: an anonymized one committed as a fixture, the full one kept outside the repo for the size/real-data run.
  - [x] Production backup of **2026-11-11** received (`dib4t-backup-2026-11-11_16-15-31.dibbak`, 1.2 MB). Kept **outside the repo** in the parent folder `Dib4t/` (not a git repo); never committed, never sent to external services.
  - [x] Content: format `dib4t-backup` v1, server 4.1.4, database `dib4t`, 8 collections, **30,754 documents** (budgets 3,397 · entities 235 · jobs 1,118 · paymentmethods 20 · prices 1 · printersettings 2 · tasks 25,971 · users 10). Only `_id` indexes, no collection options (nothing for the 9.0 option/index cleanup to do on this file).
  - [x] Restores into the local legacy app (`legacy.sh restore`, < 1 s); counts match; jobs board, budgets and dashboard load with no page errors. Note: `GET /api/job/getAll` returns 2.6 MB (budgets populated in every job) → Phase 6 performance item.
  - [x] Anonymized copy as a fixture: `tests/fixtures/legacy/prod-2026-11-11.anon.dibbak` (see "anonymized dev copy" below).
- [x] **Run the old app locally** (unchanged code; legacy back-end on `:3001` + front-end on `:3000` against its own `mongo:4.1.4` with a dev copy of the data) with a start script in `tools/legacy/` that the E2E suite reuses (§11).
  - `tools/legacy/legacy.sh up [--fresh] | down [--purge] | status | restore <file> | backup <file> | token | logs`. `LEGACY_NO_FRONTEND=1` starts only Mongo + back-end (API-only tests). Pids and logs go to `tools/legacy/.run/` (git-ignored).
  - The legacy ports are **hard-coded** in the unchanged code: DB `mongodb://localhost:27017/dib4t` (`config/default.json`), API `:3001`, UI `:3000`. So legacy Mongo always publishes host port **27017** (bound to 127.0.0.1, volume `dib4t-legacy-db`). The new app's local `mongo:9.0.8` must use a **different host port** (or none: Testcontainers picks a random one).
  - `NODE_ENV` is left unset, so the legacy back-end stays in dev mode: no SSH/NAS and no printing (§5.1).
  - On a fresh DB, the legacy back-end seeds the admin user (`userlogado@gmail.com`) and default prices. Only the bcrypt hash of that password is in the legacy code. For API calls, `legacy.sh token` signs a settings-capable JWT with the key and role names read from the legacy code at runtime, so no legacy secret is copied into this repo. To log in through the UI, restore a backup with known users (`legacy.sh restore`).
  - Mongo is stopped cleanly (`docker stop`): killing a 4.1.4 `mongod` leaves stale collection counts (`count()` returns 0 after restart).
  - Verified 2026-11-13: fresh start, login page renders, API with and without token (200/401), backup → restore round trip through the legacy API, data survives `down`/`up`, all ports freed by `down`.
- [x] Record the **legacy layout bugs** with screenshots at the §8.4 matrix (viewports × zoom × font size), as the "before" evidence and a checklist for C9.
  - `legacy.sh seed` fills a fresh legacy DB with synthetic data through the legacy API (`tools/legacy/seed-legacy.cs`: 4 users with password `layout-test`, 22 entities, 14 budgets + revisions with tasks, 10 jobs in every state incl. one billed; long texts on purpose).
  - `legacy.sh layout` runs the matrix (`tools/legacy/layout-matrix.cs`, .NET file-based app, Microsoft.Playwright 1.64.1, full Chromium). Zoom z is emulated as CSS viewport = window / z with devicePixelRatio z; the root font size is the Chromium profile setting `default_font_size`. Measures H (page overflow), O (controls outside the viewport), X (overlapping controls), T (overlapping text), C (controls/text cut off). Off-screen screen-reader-only content is ignored.
  - Result: 495 cases, **190 with issues**. Generated numbers: `docs/layout/legacy-layout-matrix.md`; curated checklist LB-01…LB-15 with evidence and causes: `docs/layout/legacy-layout-bugs.md`. Screenshots + gallery in `artifacts/legacy-layout/` (git-ignored, regenerate in about 4 min).
  - Worst pages: budget details (header over task rows, already at 1366×768 100 %), job details (cost table text collisions, page cut at a fixed height), jobs board and settings (tabs cut off/unreachable). The permanent drawer breaks every page on phones. The new app's E2E layout matrix (§11) reuses the same checks.
- [x] Take a fresh `.dibbak` backup; create an anonymized dev copy.
  - `tools/legacy/dibbak.cs anonymize <in> <out>` → `tests/fixtures/legacy/prod-2026-11-11.anon.dibbak` (2.0 MB, committed). **Safe by default**: every string is scrambled except an allowlist (enum fields, `budgetNumber`). Scrambling keeps length, case, whitespace, punctuation, digit/letter classes, accents and non-NFC sequences, and is deterministic per value under a random key that is never stored, so equal values stay equal across collections (user names ↔ `createdBy`/`invoicedBy`/`validatedBy`, payment-method names ↔ `paymentMethod`/`paymentCondition`) and nothing can be reversed. Users become `utilizador01…10@dib4t.local`, all with password **`dib4t-dev`** (bcrypt `$2a$`, cost 10); printer IPs become `127.0.0.x`; the header's `createdBy` is replaced.
  - Kept as-is: ObjectIds, all numbers with their exact BSON types, dates, booleans, nulls, field order, unknown fields. **Numbers (prices, totals, hours) are real values**; only names and texts are anonymized.
  - Checks: built-in leak check (no output string equals or contains any original scrambled value); the inventory of the anonymized file is **identical** to the original's in every property (paths, presence, types, nulls, Int32/Double split, array sizes, enum values, non-NFC and date-like counts); restores into the legacy app; login with `utilizador02@dib4t.local` / `dib4t-dev` works; pages render.
  - `.gitignore`: `*.dibbak` ignored except `tests/fixtures/legacy/*.anon.dibbak`.
- [x] Run the data-shape inventory (§4.5) → fixtures. Findings in §4.5; `docs/data/legacy-data-shapes.md`, `tests/fixtures/legacy/data-shapes.json`.
- [ ] **Record characterization fixtures** from the legacy API: a scripted run (e.g. `.http`/Playwright API) of every endpoint in §6 against a seeded DB; save requests, responses and the resulting DB documents as golden files.
- [x] Printer bytes: derived from the legacy code instead of a capture (§5, "Wire behavior"): `UTF8(template + "\n")`, then close.
- [ ] SSH commands: write the expected command sequences per operation (budget create/clone/rename, job create) **from the legacy code** as golden files (§5.1). No NAS access.
- [ ] Sample PDFs for 5–10 real budgets (generated by the old app running locally; no NAS needed).
- [ ] Decide the open questions (§14) and the defect list (§9).
- **Exit:** fixtures committed under `tests/fixtures/legacy`; decisions recorded in `docs/decisions/` (ADRs).

### Definition of Done (applies to every feature in every phase)
A feature is done only when all of these are in the same pull request:
1. **Unit tests** for its domain/application logic (and the shared calculations), written with the feature, not after it. Happy path, validation errors, and every legacy status code/message it can return.
2. **API/integration tests** (`WebApplicationFactory` + Testcontainers `mongo:9.0.8`) for its endpoints, including the BSON types written (§4.3).
3. **bUnit tests** for components with logic (state-dependent rendering, enabling/disabling).
4. **E2E test (Playwright)** for the user flow in the new app, **plus the legacy comparison**: the same scenario run against the old app (each app on its own database, both loaded from the same backup), comparing visible results and the resulting documents (§11).
5. **Layout check**: the feature's pages pass the §8.4 responsive matrix.
6. CI green; no new analyzer warnings.

### Phase 1: Foundation
- [ ] Solution, projects and dependency rules (§3), `Directory.Build.props`, central packages, `.editorconfig`, analyzers, architecture tests.
- [ ] CI pipeline (build, unit, integration, E2E incl. legacy comparison and layout matrix), Dockerfile, `docker-compose.dev.yml` (legacy: `mongo:4.1.4` + back-end + front-end, unchanged; new: `mongo:9.0.8` + new app on `:3010`; all `linux/amd64`).
- [ ] E2E harness: Playwright fixture that loads the same seed backup into both databases (legacy restore on 4.1.4, new-app restore on 9.0.8), starts both apps, and exposes page objects for both UIs (§11).
- [ ] **Backup restore (import) slice first**: `.dibbak` reader + restore endpoint (API only) so every later phase can load real legacy data into the new app and test on it.
- [ ] Mongo infrastructure (MongoDB.Driver 3.13.0, startup version check, `retryWrites=false`): `JsNumberSerializer`, conventions, class maps for all 8 collections, round-trip tests against fixtures (read every variant, write, and check the BSON types match §4.3).
- [ ] Auth: `JwtBearer` with the `x-auth-token` header and own key, tokens without expiry stored in `localStorage`, roles checked against the DB, `GET /api/account/me`, bcrypt verification of imported passwords, policies, login endpoint, **login page** and logout (synced across tabs). E2E: still logged in after closing and reopening the browser and after a server restart; logged out only by the logout button (and in every tab); a removed role disappears without a new login.
- [ ] Blazor shell: MudBlazor theme (§8.3), **responsive** landing layout (§8.4: temporary/mini/permanent drawer), role-based menu, theme toggle, logout, snackbar, global HTTP error handling. Layout matrix test running from the first page.
- **Exit:** a legacy backup imports through the API, and an existing user from that backup logs in to the new app with their current password.

### Phase 2: Settings and entities
- [ ] Users (list, create, edit), prices, payment methods, printers, info tab, startup seeding.
- [ ] Entities: list/filter/sort/paginate, create/edit, types, clients list, search endpoint.
- **Exit:** characterization tests green for account/prices/printer-settings/entity endpoints; import suite (§11.3) green for these collections.

### Phase 3: Budgets and tasks
- [ ] Budget list (latest revisions, search, revisions collapse, copy path, row menu), new/edit/clone dialogs, new revision, numbering (+ collision guard §4.7).
- [ ] Budget details: header grid, live totals and margin colors, lock/unlock, import, tasks editor (save on blur), description + "Concluir", payment method.
- [ ] PDF generation (PDFsharp) + download (base64 contract) + `wasPdfDownloaded`.
- [ ] `INasFileShare` (prod SSH.NET / dev no-op) for create/clone/rename, converted from the legacy code and tested against the `openssh-server` fake only (§5.1). Real NAS: owner check N2.
- **Exit:** PDF visual diff within tolerance; imported budgets of every kind (all revisions, locked, with/without job) open, edit, revise and clone in the new app.

### Phase 4: Jobs
- [ ] Create job (dialog with due date = today + daysNeededToDoTheWork), prod NAS move + label printing (`ILabelPrinter`), converted from the legacy code and tested against fakes only (§5.1). Real NAS/printers: owner checks N2, N3.
- [ ] Jobs board: tabs/counters, priority ordering, due-date picker + countdown icons, state menu, statistics modal per state (incl. 405 handling), comments modal, priority/print menu, icons.
- [ ] Job details page: header, cost summary, state dates, cost table with live calculations, observations, verifications.
- [ ] Print endpoint + print dialog.
- **Exit:** characterization tests for every job endpoint; printer golden bytes match the code-derived payload (§5); SSH command sequence matches the code-derived golden files (against a test SSH server in a container). Real-device checks N2/N3 (§5.1) are done by the owner and do not block this exit.

### Phase 5: Dashboard, readings, backup/restore
- [ ] Dashboard pie chart (current year counters).
- [ ] Readings page (scanner flow, 3 s auto-close).
- [ ] Database info, streamed backup download, streamed restore with progress, busy lock.
- [ ] Restore UI and hardening for legacy imports (§7.6): option/index cleanup, full-size backup, server-side continuation, automatic pre-restore backup.
- **Exit:** a real backup downloaded from the old app restores in the new app on 9.0.8 with zero differences in the built-in verification, at full production size; restore UI with progress, warnings and automatic pre-restore backup.

### Phase 6: Hardening, UAT, side-by-side production run
- [ ] Full regression run of the E2E suite (new app + legacy comparison) and the complete layout matrix; screenshot review with legacy in UAT.
- [ ] Performance check (job lists with populated budgets: use `$lookup` aggregations instead of the legacy N+1 / load-all patterns, e.g. `getByBudgetNumber` loads every job today).
- [ ] Security review (SSH escaping, authz on every endpoint, request size limits, no secrets in logs).
- [ ] Owner on-site checks N1–N3 (§5.1) done before deploying.
- [ ] Deploy the new app and its `mongo:9.0.8` next to the legacy containers (legacy untouched), `ASPNETCORE_ENVIRONMENT=Production`, port **3010**, NAS/printer switched off (§4.7). Import a fresh backup; pilot users try the new app on that copy while daily work stays in the old app.
- [ ] **Switch-over:** stop daily work in the old app, download the final backup, restore it in the new app, check the verification result, switch NAS/printer on, users move to the new app. The old app stays installed and unchanged.
- **Exit:** pilot finds no blocking difference → switch-over done; the old app is kept read-only by agreement (no technical change to it).

---

## 11. Testing strategy

Policy: tests are written **together with each feature** (Definition of Done, §10). Target ≥ 90 % line coverage on Domain, Application and SharedKernel; coverage is reported in CI.

1. **Unit (Domain/Application/SharedKernel):** numbering, flags, patch rules, `ChangeState`, `SetStatistics` per state, `UpdateStateDates`, deviation check, priority ordering, every `taskHelper` calculation (ported tests use the same inputs and outputs as the JS functions), PDF text helpers, template strings, path builders.
2. **Characterization / contract tests:** the golden files from Phase 0 replayed against the new API; JSON compared after normalization (ids, dates, `__v`), plus the resulting DB documents compared field by field **including BSON types**.
3. **Import compatibility suite** (`Dib4t.ImportCompatibility.Tests`, runs on every pull request): this is what proves C3.
   - **Backups made by the real old app:** Testcontainers starts `mongo:4.1.4` and the **unchanged legacy back-end**. A script fills it through the legacy API with scenarios that cover every feature and state (budgets with revisions, locked/complete, every job state with statistics, comments, priorities, entities of every type, users with every role, prices, payment methods, printers), then downloads the backup from `GET /api/database/backup`.
   - The new app (on `mongo:9.0.8`) restores it through `POST /api/database/restore`. Assertions: every document equal to the source (raw BSON compare: `_id`, fields, values, types), indexes present, counts match.
   - Then the new app is used on the imported data: login with imported users, open/edit every kind of record, run every state change, generate PDFs. The same requests on the legacy API (same data) give the same results.
   - **Real production backups:** the anonymized fixture always; the full-size one in a nightly job.
   - **Edge cases:** empty collections, documents missing later fields, Int32/Double/Int64 mixes, accents/Unicode, extra unknown fields, index options from 4.1.4, a 1 GB synthetic backup, truncated/corrupted/wrong files (legacy error messages, new DB unchanged).
4. **Integration:** repositories against real Mongo (`mongo:9.0.8`); NAS adapter against an `openssh-server` container; printer against an in-test TCP listener.
5. **Component (bUnit):** state-dependent rendering (menu items, statistics modal fields, lock behavior, colors).
6. **E2E (Playwright for .NET, `Dib4t.E2E`):** one test per user flow, added with each feature: login/logout and roles, budget lifecycle (create, edit, revision, clone, lock, import, PDF), job lifecycle (create, every state change with statistics, comments, priority, due date, billing, details page), entities, settings (users, prices, payment methods, printers, backup/restore), dashboard, readings.
7. **Legacy comparison (E2E, behavior parity):** the harness runs the **old app** (unchanged, on its own `mongo:4.1.4`) and the new app (on its own `mongo:9.0.8`), both loaded from the **same seed backup**. Each scenario is written once against a small driver interface and executed in both UIs (separate page objects for the React and Blazor DOMs). Compared after each step:
   - the visible result (table rows, totals, states, notification texts, error messages);
   - the API responses (normalized ids/dates);
   - the resulting DB documents, field by field and by BSON type.
   Differences fail the test unless listed in an allow-list that points to a decided defect in §9 or a layout fix in §8.4. Generated values (`_id`s, dates, tokens) are normalized before comparing.
8. **Layout matrix (E2E):** the §8.4 viewport × zoom × font-size checks and visual baselines for every page.
9. **Production-only paths** are tested with fakes in E2E (test SSH server container, TCP listener for the printer) by running the new app with a test configuration that enables them; real NAS and printer are never touched by tests (they are not reachable from the development network; §5.1).

---

## 12. Deployment and side-by-side operation

**The new app needs exactly 2 containers:**

| # | Container | Image | What it does |
|---|---|---|---|
| 1 | `dib4t-db` | `mongo:9.0.8` (`linux/amd64`) | The new database. Volume for `/data/db`. Not published on a host port. |
| 2 | `dib4t-app` | `dib4t-dotnet` (built from `mcr.microsoft.com/dotnet/aspnet:11.0`) | One ASP.NET Core process that serves **everything**: the Blazor WebAssembly files, the `/api` endpoints, the health check, the NAS (SSH) and printer (TCP) calls, and the scheduled backups. Port `3010`. |

Nothing else runs: no separate front-end or web server (Kestrel serves the static WASM files), no reverse proxy, no cron or backup container, no migration or seeding job (seeding of the admin user and the startup checks run inside the app), no message queue or cache. The test-only containers (legacy app on `mongo:4.1.4`, SSH server, printer listener) exist only in CI and on developer machines.

**Memory (fewer than 20 users):** about **1.5 GB in total** is enough: database 1 GB (0.5 GB WiredTiger cache + the server's own overhead), app 512 MB (typically 150–300 MB in use). The load comes from the size of the data, not from the number of users: the cache only needs to hold the data and indexes, which for this app is tens of MB: a backup exported from the old app (2026-11) is **under 2 MB** compressed, so roughly 10–20 MB of data. The limits above leave large headroom; no tuning is expected. The cache size must be set explicitly, because inside a container MongoDB otherwise sizes it from the NAS's total RAM and would take far too much.

`docker-compose.yml` for production has just these two services:
```yaml
services:
  dib4t-db:
    image: mongo:9.0.8
    platform: linux/amd64
    command: ["--wiredTigerCacheSizeGB", "0.5"]   # < 20 users; raise only if data + indexes grow past ~400 MB
    mem_limit: 1g
    volumes: [ "dib4t-db-data:/data/db" ]
    healthcheck: { test: ["CMD", "mongosh", "--quiet", "--eval", "db.adminCommand('ping')"] }
    restart: unless-stopped
  dib4t-app:
    image: dib4t-dotnet:<version>
    platform: linux/amd64
    ports: [ "3010:3010" ]
    mem_limit: 512m
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_URLS: http://+:3010
      Mongo__ConnectionString: mongodb://dib4t-db:27017/dib4t?retryWrites=false
      # Jwt__Key, Nas__* from an env file / Docker secrets, never in the repo
    volumes: [ "dib4t-logs:/app/logs", "/share/<backup folder>:/app/backups" ]
    depends_on: { dib4t-db: { condition: service_healthy } }
    restart: unless-stopped
volumes: { dib4t-db-data: {}, dib4t-logs: {} }
```

```
NAS / server host (AMD RX-421ND, x86-64)
├─ legacy (unchanged, C10)
│  ├─ mongo:4.1.4      :4002   legacy database
│  ├─ back-end         :4001
│  └─ front-end        :4000   (derives API port by replacing the last digit with 1)
└─ new
   ├─ mongo:9.0.8      (internal Docker network only)  new database, filled from legacy backups
   └─ dib4t-dotnet     :3010   (UI + /api on one origin, ASPNETCORE_ENVIRONMENT=Production)
```
- Development also runs the new app on `:3010` (legacy dev: front-end `:3000`, back-end `:3001`).
- Config through env vars: `Mongo__ConnectionString`, `Jwt__Key`, `Nas__Host`, `Nas__User`, `Nas__Password` (or key file), Kestrel URL.
- Container user without root privileges, health check, log volume.
- Rollback: users go back to the old app, which was never changed. (After the switch-over, work done in the new app would have to be re-entered; the new app's own backups keep it.)
- Backups of the new database: the backup feature in the UI (download `.dibbak`), plus a **scheduled backup inside the app** (`BackgroundService`, daily at a configurable time) that writes a `.dibbak` to the mounted NAS folder and keeps the last N files (`Backup:Schedule`, `Backup:Keep`). No extra container.

---

## 13. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| NAS CPU can't run MongoDB 9.0 | Low | AMD RX-421ND (Excavator) has AVX; owner confirms with a test start on site (§5.1, N1) before deployment |
| A legacy backup doesn't import, or imports with differences | Medium | Import suite on every PR with backups made by the real old app + real production backups; built-in verification at every import |
| Work done in the wrong app during the pilot (data diverges) | Medium | One system of record at a time, NAS/printer off in the new app until switch-over, clear communication (§4.7) |
| NAS memory with two MongoDB servers | Low/Medium | WiredTiger cache limits for both, checked on site by the owner (§5.1, N1) |
| A 4.1.4 backup has index/collection options 9.0 rejects | Medium | Option cleanup in the restore (§2.2), tested with real backups |
| MudBlazor has no `net11.0` build yet | Low | Its `net10.0` build runs on .NET 11; Phase 1 smoke test; fallback to .NET 10 LTS |
| New documents drift from the legacy model (types, defaults) | Medium | §4.3 rules, BSON type assertions in tests |
| PDF not pixel-identical | Medium | PDFsharp absolute layout, same Roboto fonts, visual diff |
| Printer payload differs (line terminator/encoding) | Low | Payload derived from the `telnet-client` source (§5); golden-file test; owner prints on the real printers (§5.1, N3) |
| Large backup files in the browser | Medium | JS streaming interop (§7.6) |
| Legacy comparison tests are flaky (two UIs, timing) | Medium | Wait on network/DB state, not timeouts; run comparison on a dedicated seeded DB per test |
| Responsive redesign drifts from the familiar look | Low/Medium | Same theme tokens and components; layout changes only where §8.4 requires; UAT review with users |
| Scope: legacy quirks reproduced by accident or "fixed" by accident | Medium | Characterization tests + explicit defect decisions (§9) |

---

## 14. Open questions (need an answer before or during Phase 0)

Answered: two databases — the old app stays on its `mongo:4.1.4` unchanged, the new app has its own `mongo:9.0.8` (x86-64 Docker) filled by importing old-app backups (§2.2, §4); the new app runs on port **3010**; use the newest libraries (§2.1); no changes to the old app; during the pilot NAS folders and label printing are off in the new app and switched on at the switch-over (§4.7); login never expires, only logout ends it (§4.6).

1. Hostname for the new app in production (port is 3010)?
2. Smallest screen the app must support (phone, or tablet and up)? The plan assumes phones (360 px) work.
3. Approve the defect decisions in §9 (especially D1, D3, D6).
4. Version shown in the Info tab for the new app (`1.0.0`?).
5. Are there other consumers of the API (scanner stations, scripts) that must keep working?
