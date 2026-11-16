# Budget details contract golden file (`budget-details-golden.json`)

Written by `tools/legacy/legacy.sh budget-details-golden` (`tools/legacy/budget-details-golden.js`, Node 16). It runs
the **unchanged legacy route handlers** from `../back-end` offline: the six routes of `routes/task.js` and
`PUT /patchProperty/:budgetId`, `PUT /import/:budgetId` of `routes/budget.js`, with their Joi validations, the Mongoose
models (`budget.patch`, `budget.setIncomplete`, `task.update`, casts, `validate()`, `$__delta()`) and
`TaskRepository.createDefaultTask`. Only the repositories' MongoDB queries are replaced by an in-memory store
(`findById` casts the id like Mongoose: js-bson `ObjectId`, else a `CastError`); `save()` records what Mongoose would
send. Nothing is started (no database, server, network or SSH). A fixed clock (`2026-03-15T10:20:30.123Z`, `TZ=UTC`)
makes it deterministic: two runs give byte-identical files (checked 2026-11-16).

## Sections

| Section | What the legacy code does | Used by |
|---|---|---|
| `budgets`, `tasks`, `entity`, `user`, `prices` | the stored documents every case starts from (canonical Extended JSON, id names) | all |
| `listTasks`, `defaultTask` | `GET /getBudgetTasks/:id`, `POST /createDefaultTask/:id` for 7 ids (stored, imported, with a job, unknown, `abc`, 12 characters, upper case) | `BudgetDetailsGoldenTests` |
| `updateTask` | `PUT /:taskId/:budgetId`: 39 bodies on one task, 15 cases of stored shapes, ids and lookups | `BudgetDetailsGoldenTests`, `BudgetDetailsTests` |
| `deleteTask` | `DELETE /:taskId/:budgetId`: 8 cases | `BudgetDetailsGoldenTests` |
| `newTask` | `POST /` (legacy: always 400, D8) and the intended schema (with `budgetId`) on 10 bodies, with the document `new Task({ ...req.body })` stores | `BudgetDetailsGoldenTests` (D8) |
| `patch` | `PUT /patchProperty/:id`: 68 bodies on one budget, 14 cases of other stored shapes and lookups | `BudgetDetailsGoldenTests`, `BudgetDetailsTests` |
| `import` | `PUT /import/:id`: 29 cases (what is imported, source shapes, truthiness of texts, refusals in order) | `BudgetDetailsGoldenTests`, `BudgetDetailsTests` |

Each case: `status` and `text` (plain answer), `json` (the answer, `toJSON`, ids as names; a populated `madeBy` still
holds the password hash, which the new app never sends), or `error` (an exception: Express's 500 page, D16), and
`effects`: the saves in order (`update` = `$__delta` of `save()`, `null` = no command; `insert`, `insertMany`,
`deleteMany`, `deleteOne`).

## Findings (all legacy behavior, kept unless a decision says otherwise)

- **Order of checks**: task edit = body (Joi) → ids (Joi, `taskId` first) → task ("Tarefa não encontrada") → budget
  ("Orçamento não existe") → job ("Orçamento gerou obra!"). The task need not belong to the budget (neither is checked).
  Delete = ids → budget → job → **the budget is marked incomplete first**, then a missing task is a 500 "Erro no
  servidor". List and default task cast the id (500 for `abc`; a 12-character text is a valid id).
- **The default task does not mark the budget incomplete** (the page saves the new total right after, which does).
- **Numbers**: a number text Joi accepts (`"30"`, `" 12.50 "`, `"1e3"`) is stored as its number with the js-bson rule
  (`Int32 30`, `Double 12.5`, `Int32 1000`); `"0x10"`, `"12abc"`, `""` are "must be a number"; above 2^53 "must be a
  safe number"; `-0` passes `min(0)` and is stored `Int32 0`. An equal number writes nothing (the stored type is kept).
- **Task description**: missing in the body → removed from a stored task (`$unset`), stays missing on a task without
  one; `""` is allowed. Texts are stored as sent (legacy: no NFC; the new app writes NFC, §4.3 rule 2).
- **patchProperty**: any refusal is 400 `"Não é possível atualizar " + String(propertyName)` (`undefined` when missing,
  arrays joined: `["a","b"]` → `a,b`, objects `[object Object]`). `complete` takes `true`/`false` and the texts
  `"true"`/`"false"`; Joi also accepts `"TRUE"`/`"False"` but Mongoose cannot cast them: **500 after the lookups**.
  The flags are reset, then the property is set: `complete: true` on a complete budget still sends
  `$set: { complete: true, wasPdfDownloaded: false }`; on an imported budget without flags only what differs from the
  defaults is written. A budget with a job is answered unchanged with **200**.
- **import**: Joi texts prefixed `"Erro: "`; the source is `findOne({ budgetNumber })` without a sort (natural order:
  revision 0 first), checked **before** the target id (an unknown number with the id `abc` is the 400, not the 500).
  Tasks: the target's are deleted, the source's copied (`taskType` before `budgetId`, a missing description left out),
  then `total` and `margin` copied (a source without `total` removes it). Description: copied (missing → removed).
  **The complete/PDF flags are not touched.** The route tests the original values' truthiness, so `"false"` imports.
- **Importing from the budget's own number** (revision 0) deletes its tasks before reading them: the budget ends with
  no tasks, and the legacy details page then renders nothing. The new app imports differently (D24, owner
  2026-11-16: the open budget's tasks/description go into the highest revision of the typed number; nothing is
  deleted): only the import refusals D24 leaves unchanged replay from this section.
