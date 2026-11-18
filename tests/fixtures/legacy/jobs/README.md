# Job create golden file (from the legacy code)

The contract of `POST /api/job` (PLAN.md §6, §7.3) and of the label payloads, **derived from the legacy code by running
it**, offline: nothing is started, the NAS and the printers are never contacted.

`tools/legacy/job-golden.js` (`tools/legacy/legacy.sh job-golden`) loads the **unchanged** legacy `routes/job.js`,
`validations/jobValidation.js` (Joi 14.3.1, joi-objectid), `models/job.js`, `models/budget.js` and
`models/printerSettings.js` in Node 16 and runs the `POST /` handler itself with a recording `res`. Replaced:

| Replaced | By | Why |
|---|---|---|
| the repositories' queries (`findById` with `populate`, `getByNumberAndHasJob`, `getRevisionZero`, `findByBudgetId`) | an in-memory store (first match in natural order; `populate` gives `null` for a missing document) | no database |
| `Model.prototype.save` | Mongoose's own `validate()`, then `toObject()` for an insert, `$__delta()` for an update | the documents each save would send |
| `ssh2-promise`, `telnet-client` | recorders | no NAS, no printer |
| the clock | `2026-03-15T10:20:30.123Z`, `TZ=UTC` (the legacy production container) | deterministic |

`NODE_ENV` stays unset: the production NAS move and the labels of a new job are the subject of
`../ssh/ssh-commands.json` (`ssh-golden.js`, job scenarios). Two runs give byte-identical files.

## File

`job-golden.json`:

| Key | Content |
|---|---|
| `ids` | name → hex of the stored documents (`{{budget}}`, …); `{{new}}` = an id the save generates |
| `budgets`, `tasks`, `entity`, `user` | the stored documents (canonical Extended JSON, js-bson number rule) |
| `create` | 79 bodies on `{{budget}}` (revision 0 with 8 tasks of every type): status, `text` (plain answer) or `json` (the answer, ids as names) or `error` (Express 500, D16), and `effects` (the budget's `$set`, the job's insert) in order |
| `shapes` | 13 stored shapes with the base body: a revision, another revision or the budget itself with a job, no revision 0, an imported budget (no flags, Double revision), client/author missing, no payment method, empty descriptive, no tasks, odd quantities (0.1 + 0.2, negative, 1e21), a task without quantity, an unknown id |
| `labels` | `print(budget, job)` of both templates for 5 samples, with `TZ=UTC` and `TZ=Europe/Lisbon`: the `send` text and the wire bytes (`UTF8(send + "\n")`) |

## Findings

- **Order of the checks**: Joi (schema order `budgetId`, `finalPrice`, `dueDate`, `state`, `orderIdentification`,
  `isOfficialOrderIdentification`, then the first unknown key) → budget (`"Orçamento não encontrado"`) → any budget of
  the number with a job (`"Obra baseada neste orçamento já foi criada anteriormente"`) → revision 0
  (`"Algo deu errado!"`) → tasks → `budget.save()` (`hasGenerateJob`) → `job.save()`.
- The route spreads the **original** body into `new Job({…})`: Mongoose casts it, not Joi. `" 12 "` and `"1e3"` are
  stored as `12` and `1000` (Int32). `dueDate`: Joi reads a text of digits as milliseconds (`"2030"` = 2,030 ms),
  Mongoose reads it as a date text below 275761 (`"2030"` = 2030-01-01); both agree on every body here.
  `"2030-02-30"` is 2 March (`MakeDay` overflow); `"2030-01-15T10:00"` is UTC (the container's zone).
  V8's fallback parser accepts `"2030/01/15"`, `"Jan 15 2030"` and `" 2030-01-15 "`.
- `isOfficialOrderIdentification`: Joi accepts `"TRUE"`/`"False"` (any case), Mongoose only `true`/`"true"`/`1`/`"1"`/`"yes"`
  and their opposites, so `"TRUE"` is a **500 after the budget was marked `hasGenerateJob`**: the number can never get
  its job (D26). The same for a task without a quantity (budgeted hours NaN). No production task lacks a quantity.
- A budget the legacy save refuses (client or author document gone, empty descriptive, no payment method) is a 500
  with **nothing** written (its validation runs before the job).
- The job document: the defaulted paths in schema order (`invoicedPercent` … `commentType`), then `budget`,
  `createdBy` (`req.user.name`), `dueDate`, `startDate`, the three statistics (zero hours used, budgeted = deviation =
  the sum of the task quantities of that type in the tasks' order: `0.1 + 0.2` = `0.30000000000000004`; consultancy
  counts nowhere; each with a new `_id` after its fields), `priority: "Média"`, `__v: 0`. The budget update is
  `$set: { hasGenerateJob: true }` only (also on an imported budget without the field).
- The answer is the job with the **populated** budget (its client and its author, the author's password hash included).
- Labels: both templates start with ESC (U+001B, `ESC i a 3`); the `producao` one keeps the source's line break and six
  spaces before and after. The date is `d/M/yyyy` of the server's local time (`TZ=UTC`: `31/7/2026` for
  2026-07-31T23:30Z; Lisbon: `1/8/2026`). Commas or line breaks in a name are sent as they are.

## How the new app uses it

`Dib4t.Application.Tests/Jobs/JobGoldenTests` (Joi texts and the casts per body, labels, budgeted hours) and
`Dib4t.Server.Tests/Jobs/JobCreateGoldenTests` (every case over HTTP on mongo:9.0.8: same answer and the same documents
BSON-for-BSON). Decided differences, each tested: D26 (nothing written), the three V8-only date texts (Joi's 400), a
date beyond the year 9999 (500, nothing written), the label date in `App:TimeZone` (Europe/Lisbon).
