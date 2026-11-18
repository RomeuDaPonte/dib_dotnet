# Jobs board golden file (from the legacy code)

The contract of the jobs board routes (PLAN.md §6, §7.3), **derived from the legacy code by running it**, offline:
`PUT statistics/:jobId`, `PATCH dueDate/:jobId`, `PATCH state/:jobId`, `PATCH changeJobPriority/:jobId`, and
`GET getCounts`, `getUnfinished`, `getAll`, `getByState/:state`, `getById/:jobId`, `/:jobId`,
`getByBudgetNumber/:n`, `getByBudgetId/:budgetId`.

`tools/legacy/job-board-golden.js` (`tools/legacy/legacy.sh job-board-golden`) loads the **unchanged** legacy
`routes/job.js`, `validations/jobValidation.js` (Joi 14.3.1, joi-objectid), `models/job.js`,
`models/jobStatistics.js` and `repositories/jobRepository.js` (its `orderArray`) in Node 16 and runs each handler
with a recording `res`. Replaced, as in `job-golden.js`: the MongoDB queries (an in-memory store: filters,
`sort({ dueDate: 1 })` with missing dates first and ties in natural order, `populate` giving `null` for a missing
document, `countDocuments`), `Model.prototype.save` (Mongoose's own `validate()`, then `$__delta()` with the version
`$inc`), the clock (`2026-03-15T10:20:30.123Z`, `TZ=UTC`). Two runs give byte-identical files.

## File

`job-board-golden.json`: `ids` (name → hex; `{{new}}` = an id the save generates), the stored `jobs` (15 shapes:
each state, an imported old job without `commentType`/`comments`/`createdBy`/`jobStatisticsDates`/`priority` and with
`isHightPriority`, `costNonQuality` and `deliveryDate` stored `null`; no statistics; a `null` end date; a missing
budget; machining without its statistic; fractional assembly hours; billed at 100 %), `budgets`, `tasks`, `entity`,
`user`, and 176 `cases` (`statistics` 105, `dueDate` 14, `state` 14, `priority` 12, `get` 31): method, path, body,
status, `text` / `json` / `empty` (an empty 200) / `error` (Express 500, D16), and `effects` (the save's update).
`state` cases have `legacy` (as is: D1) and `fixed` (the legacy code with `req.params.id` = the job id).

## Findings (kept for parity unless stated)

- **Statistics**: id → job (`"Obra não encontrada"`) → the Joi schema of the job's **current** state (development,
  approval and approved share one; machining and assembly require integers; "Faturada"/"Apagada" have none) → a job
  without `devStatistic` gets new statistics and the state, no deviation check → otherwise `Σ quantity(tasks of the
  current state's type) − numberOfHoursUsed == deviation` (JavaScript, loose equality; "Para faturação" and later:
  no tasks, always true) → `setStatistics` → `updateStateDates` → `changeState`.
- `setStatistics` assigns the body's **original** values (Mongoose casts `"18"` to 18): `comments` (a number becomes
  its text, `null` is stored `null`, missing removes it), `commentType = [...body.commentType]` (a missing one is a
  `TypeError` → 500 with nothing written; a string is spread into its characters), the current state's costs, its end
  date now, its statistic as a **new** sub-document (new `_id`, `validatedBy` = the user's name), `deliveryDate`
  (`""` → `null`), and `invoicedPercent` (missing → **removed**, and left out of the answer).
- A new `jobStatisticsDates` is one `$set` of the whole document (`_id`, then the dates in assignment order); an
  existing one gets `jobStatisticsDates.<date>` paths. `updateStateDates` fills every missing or `null` end date of
  the states before the new one.
- "Faturada" with `invoicedPercent < 100` (also `null`): the other values are saved, the state is not, **405**
  `{ reason: "Valor faturado inferior a 100%", updatedJob }`. A Boolean or date Mongoose refuses is a 500 with
  nothing written. Saving a comment through this route re-stamps the current state's end date (legacy behavior).
- `PATCH dueDate` has no validation: an empty, `null`, missing or invalid date is a 500; an unchanged one writes
  nothing. `PATCH state` (D1) always answers 400 "Obra não existe"; with the fix, "Faturada" below 100 % is answered
  200 with the job unchanged. `PATCH changeJobPriority`: id → job → Joi.
- Reads: `getById` of an unknown id is an empty 200, of `"abc"` a 500 (CastError); `/:jobId` validates the id (400).
  The lists populate the budget and its client (the author stays an id), `getById` also the author (with the
  password hash: never sent by the new app), `getByBudgetNumber` only the budget. Order: due date, then "Alta",
  "Média", the rest (an old job without priority is last); "Faturada" and "Para faturação" by date only.
- Unknown stored fields (`isHightPriority`) are answered as stored; schema defaults are answered for missing fields
  (`commentType: []`), but not for a field the request removed.

## How the new app uses it

`Dib4t.Application.Tests/Jobs/JobBoardRulesTests` (every Joi text, the model's rules) and
`Dib4t.Server.Tests/Jobs/JobBoardGoldenTests` (all 176 cases over HTTP on mongo:9.0.8: same status and text or JSON,
and the same jobs BSON-for-BSON against the legacy update applied to a copy; `PATCH state` per `fixed`, D1).
