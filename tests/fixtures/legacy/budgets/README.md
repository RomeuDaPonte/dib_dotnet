# Budget list contract golden file (`budget-golden.json`)

Written by `tools/legacy/legacy.sh budget-golden` (`tools/legacy/budget-golden.js`, Node 16). It runs the **unchanged
legacy code** from `../back-end` offline: nothing is started (no database, no server, no network). `ssh2-promise` is
replaced by a recorder before any legacy module loads, so the NAS is never contacted (the line
`cd /;mv /share/Orçamentos/…` printed during a run is the legacy `console.log` in `updateFolderName`, not a command
sent anywhere). A fixed clock (`2026-03-15T10:20:30.123Z`, `TZ=UTC` like the legacy production container) makes the
file deterministic: two runs give byte-identical files (checked 2026-11-16).

## Sections

| Section | What the legacy code does | Used by |
|---|---|---|
| `newBudget` | `validateNewBudget` (create, clone) on 44 raw JSON bodies → the Joi 400 text or `null` | `BudgetGoldenTests` |
| `update` | `validateUpdateBudget` (`legacyError`: always `null`, D7) and the schema it was meant to apply (`message`) | `BudgetGoldenTests` |
| `search` | 30 query strings through the legacy Express query parser (qs, "extended") → `validateSearch` → `new RegExp(q, "i")` (source, flags) or the 500 text | `BudgetGoldenTests`, `BudgetRepositoryTests` |
| `budgetNumbers` | `getBudgetNumber(count)` at the boundaries (`26-0001` … `26-10001`) | `BudgetGoldenTests` |
| `countForThisYear` | the filter `getBudgetCountForThisYear` sends (`date: { $gte: 2026-01-01T00:00:00.000Z }`, distinct `budgetNumber`) | `BudgetGoldenTests` |
| `list`, `searchRows` | the real repository functions on in-memory answers: query (sort, limit 200, populate), dedupe, row shape; the search pipeline stage by stage | documentation, `BudgetRepositoryTests` |
| `create` | `new Budget({…})` of `POST /` (object literal verbatim) for 6 price sets: stored document (js-bson 1.x number types), answer (`toJSON`, key order) or the refused save | `BudgetGoldenTests`, `BudgetRepositoryTests` |
| `defaultTask` | `TaskRepository.createDefaultTask` (prices stubbed) | `BudgetRepositoryTests` |
| `clone`, `newRevision` | the routes' `new Budget({…})` from 6 stored shapes, and the task copies | `BudgetRepositoryTests` |
| `edit` | `budget.update(body, madeBy)` + the save's `$set`/`$unset` from `$__delta`, with the SSH commands it would send | `BudgetRepositoryTests` |
| `read` | `toJSON` of stored shapes (unpopulated and populated): key order | documentation |

Ids are written as names: `{{budget}}`, `{{client}}`, `{{user}}`, `{{user2}}`, `{{budget-r1}}` …, `{{new}}` for
generated ones.

## Findings (all legacy behavior)

- **Key order of a budget answer**: the paths with a static default first, in schema order (`revision, complete,
  wasPdfDownloaded, margin, daysNeededToDoTheWork, validity, hasGenerateJob`), then `_id`, then the other stored
  fields **in stored order** (a field added later, e.g. `description` after a patch, comes last); a missing `date`
  gets `Date.now` on read and comes last. The new app answers in a new budget's order (known difference).
- **Defaults on read, but not in search rows**: list rows come from hydrated documents (`hasGenerateJob: false` for an
  old document), search rows from the aggregate (a missing field is left out).
- **An edit writes a flag only when it changes**: an imported budget without `complete`/`wasPdfDownloaded` saved with
  `false` sends no `$set` (Mongoose applied the default on read). `MongooseSave` takes the defaults for this.
- **A budget whose client no longer exists breaks the whole list** (`TypeError: Cannot read properties of null
  (reading 'name')` → 500). The new app shows the row without the name.
- A client without `paymentCondition` makes the create's save fail (`Path paymentMethod is required`, 500).
- `validateNewBudget` accepts `" "`, `/`, `'` and any length in `descriptive` (the dialog's yup schema is stricter:
  only `a-zA-ZçÇã,.êÊàáÀÁéÉúÚÙù0-9_` and space, at most 30 characters, so no `-`, `(`, `ó`, `í`, `ª`).
- Search: `q` must be a string of at least 2 characters; a repeated `q`, `q[]`, `q[x]` are not strings; any other key
  is "not allowed". JavaScript refuses `((`, `a{2,1}`, `a**` and a NUL (the driver); `\k` passes JavaScript but MongoDB
  refuses it (both legacy 500; the new app answers 400, D9).
- The count for the number is the distinct numbers with a document dated from 1 January 00:00 UTC, so a 2026
  revision of a 2025 number counts for 2026 (numbers are skipped, never repeated, as long as nothing is deleted).
- Totals: `Math.trunc(margin * automation / 100) + automation` (e.g. 35 % of 32.5 → 43.5, 30 % of 33.33 → 42.33).
- A task copy leaves out a missing description and keeps the text as stored (no trim, no NFC).
