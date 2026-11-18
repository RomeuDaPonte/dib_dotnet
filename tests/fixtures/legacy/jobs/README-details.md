# Job details golden files (from the legacy code)

The contract of the job details page (PLAN.md §6, §7.3, Phase 4 step 3), **derived from the legacy code by running
it**, offline. Two files:

## `job-details-golden.json`: the routes

`tools/legacy/job-details-golden.js` (`tools/legacy/legacy.sh job-details-golden`) runs the **unchanged** handlers of
`PATCH statisticsDates/:jobId`, `PATCH register/:jobId`, `PATCH singleProperty/:jobId` and
`PATCH updateVerifications/:jobId` of `routes/job.js`, with `validations/jobValidation.js` (Joi 14.3.1) and
`models/job.js` (Mongoose 5.13 casts, `validate()`, `$__delta()`), through the harness shared with the jobs board
(`tools/legacy/job-harness.js`: the same in-memory store of 15 job shapes, the clock `2026-03-15T10:20:30.123Z`,
`TZ=UTC`, SSH and telnet replaced by recorders). Two runs give byte-identical files.

160 cases (`statisticsDates` 33, `register` 48, `singleProperty` 55, `verifications` 24). Each has `legacy` (as is)
and, for the sections with a decided fix, `fixed` (the legacy model with the fix applied; what the new app does):

- `statisticsDates`: **D17** (ADR-0005): a job without `jobStatisticsDates` gets a new one (`_id`, then the date), as
  `setStatistics` does; legacy: `TypeError`, 500. Production: **124 of 1118** jobs have none.
- `register`: **D18** (ADR-0005): `consultancy` sets `devStatistic.numberOfHoursUsed` like `development`; legacy: nothing.
- `verifications`: the replaced statistic's `validatedBy` is the signed-in user's name (PLAN.md §7.3); legacy stores
  the name the client sends (or none).

15 cases change with a fix. `ids`, `jobs`, `budgets`, `tasks`, `entity` and `user` are those of
`job-board-golden.json`.

### Findings (kept for parity unless stated)

- **statisticsDates**: id → Joi (`forJobState` one of the five end dates before billing; `newDate` any; no other key)
  → job (`"Obra não existe"`) → `jobStatisticsDates[field] = newDate`: an existing sub-document gets the dotted path;
  `""`/`null` are stored `null`, a missing date is `$unset`, an invalid one is a 500 (CastError), `"2026-02-30"` is
  2 March, a time without offset is UTC, an unchanged date writes nothing. `"10000-01-01"` is stored by legacy
  (`+010000-01-01`); the new app refuses it with a 500 and nothing written (dates end in 9999; the known difference
  of job create).
- **register**: id → job → `updateRegisteredValue` with no validation: Mongoose's number cast (`" 12 "` → 12,
  `"1e3"` → 1000, `true` → 1, `""`/`null` → `null`, missing → `$unset`, `"abc"`/arrays/objects → 500); hours go to the
  statistic's dotted path (a job without that statistic: `TypeError`, 500); an unknown type (also `"Development"`)
  changes nothing and answers the job; negative values are stored.
- **singleProperty**: id → job → a `deliveryDate` outside "Montagem"/"Para faturação" is 400
  `"Estado de obra não permite alterar data de entrega"` (**before** Joi) → Joi (`propertyName` one of five; `value`
  any; no other key) → `job[name] = value`: `orderIdentification` empty/`null`/missing fails `required` (500), a number
  becomes its text; `isOfficialOrderIdentification` takes Mongoose's boolean cast (`"true"`, `1`; `"TRUE"` is a 500;
  `null` is stored, missing is `$unset`); `invoicedPercent` has **no range check** (150, -5 stored); `comments` an
  array is a 500, missing on a job without comments writes nothing. A stored `null` or a removed field is answered
  `null` / left out (the new app's `Stored<T>`).
- **verifications**: id → job → `updateMachiningVerificatons == true` (JavaScript loose equality: `true`, `1`, `"1"`;
  not `"true"`) replaces `machiningStatistic`, otherwise `assemblyStatistic`, with `{ ...body.xStatistic }`: the schema
  paths in schema order (defaults for missing ones, unknown keys dropped), the sent `_id` kept (else a new one); an
  invalid answer (`"OK"`, `validTryOnFirstTest` `"4"`) is a 500; a sub-document equal to the stored one writes nothing.
  The legacy page sends the assembly statistic as both statistics for an assembly check (harmless: only one is used).

## `job-costs-golden.json`: the cost summary and table

`tools/legacy/job-costs-golden.js` (`tools/legacy/legacy.sh job-costs-golden`) runs the legacy **front-end**'s
`_helpers/taskHelper.js` and `jobDetais/table/customHooks/useTaskHoursRowState.js` verbatim (turned into CommonJS by the
front-end's own Babel; the hook with a React stand-in whose effect runs at once); the components' glue (which values
they pass, how they print) is transcribed from `costsTableHeade.jsx`, `useJobResultsContext.js` and the `*Row.jsx`
files. 135 scenarios (16 written + 119 seeded random): tasks, prices (`GET /api/prices` + `others = components = 1`),
budget total and the job's values as the page holds them (stored numbers, typed texts, `null`, missing statistics),
with every printed text and color class.

### Findings

- Registered cost of hours = `Math.floor((budgetedValue / budgetedHours).toFixed(2) × hours)` per type (consultancy in
  development); without budgeted hours of the type, `Math.floor(price × hours)`; components/automation/others rows
  use the typed amount when truthy.
- The machining row's deviation also subtracts the materials; the non-quality cost only enters the totals.
- Texts are JavaScript's number printing (`100.02000000000001 €`, `NaN €` for an invalid typed value).
- **Fixed in the new page** (checked explicitly in `JobCostCalculationsTests`): a job without statistics counts 0
  hours (legacy: the page crashes; the scenario's `fixed` page); a row percentage with nothing budgeted is `-` (legacy
  means to, but prints `-Infinity %`); the previewed margin is colored by its sign (legacy: always green).

## How the new app uses them

`Dib4t.Application.Tests/Jobs/JobDetailsRulesTests` (Joi texts, casts, D17, D18), `Dib4t.Server.Tests/Jobs/JobDetailsGoldenTests`
(all 160 cases over HTTP on mongo:9.0.8, answers and documents BSON-for-BSON, `fixed` where present),
`Dib4t.Domain.Tests/SharedKernel/JobCostCalculationsTests` (all 135 scenarios) and `Dib4t.Client.Tests/Jobs/JobDetailsPageTests`.
