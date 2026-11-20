# Dashboard counters golden file (from the legacy code)

The contract of the dashboard's pie chart (`GET /api/job/getValidTryOnFirstTestCountersByYear/:year`, PLAN.md §6,
§7.3, Phase 5 step 1), **derived from the legacy code by running it**, offline.

## `job-dashboard-golden.json`

`tools/legacy/job-dashboard-golden.js` (`tools/legacy/legacy.sh job-dashboard-golden`) runs the **unchanged** handler
of `routes/job.js` and `repositories/jobRepository.js`'s `getValidTryOnFirstTestCountersByYear`, through the harness
shared with the other job golden files (`tools/legacy/job-harness.js`: the clock `2026-03-15T10:20:30.123Z`,
`TZ=UTC` like the legacy production container). Its three `Job.find(filter).countDocuments()` get a real Mongoose 5.13
query, whose own `cast()` gives the filter (an Invalid Date is Mongoose's `CastError`, the 500 of Express), counted by
an in-memory store with MongoDB's semantics for the three conditions. Nothing is started and nothing is contacted. Two
runs give byte-identical files.

The store is the file's own **24 jobs** (`jobs`; the shared budgets, tasks, entity and user are kept): 2026 starts at
both ends of the year (1 January 00:00, 31 December 00:00, 12:00 and 23:59:59.999 UTC), the last millisecond of 2025,
2027, other states (`Montagem`, `Apagada`, `Desenvolvimento`, `faturada`), `validTryOnFirstTest` as the number `1` and
as `"4"`, no `assemblyStatistic`, no statistics, `startDate` `null`, missing and as text, and four 1994–1996 starts
around the Lisbon new year (Portugal was on CET, UTC+1, from 1992 to 1996).

Each of the 26 year texts is run twice: `legacy` as is, and `fixed` = the legacy code with **D10** fixed (ADR-0005),
made by replacing the repository's range in its source: `[1 January 00:00, 1 January of the next year 00:00)` in
Europe/Lisbon (Node's ICU time zone data) of the year `new Date(`${year}-01-01`)` gives in UTC, still an Invalid Date
where legacy has one. `startDate` of each outcome is the cast filter the first count sends.

### Findings (kept for parity)

- **The filter**: `state` in `["Para faturação", "Faturada"]`, `startDate` `$gte new Date(`${year}-01-01`)` and
  `$lte new Date(`${year}-12-31`)` (UTC midnights), `assemblyStatistic.validTryOnFirstTest` equal to the text `"1"`,
  `"2"`, `"3"`: one count each, answered as `{ oneCounter, twoCounter, threeCounter }` (in that key order).
- **Not counted**: a start on 31 December after 00:00 UTC (in no year: D10), other states (also `faturada`), the number
  `1`, `"4"`, a job without the assembly statistic, a `startDate` that is `null`, missing or a text (MongoDB compares
  dates only with dates).
- **The year text**: `2026`, `+002026`, `0000`, `-000001`, `+010000`, `9999` are ISO years. `26`, `2026x`, `2026 `,
  `2026.5`, `2026-01`, `+275760` (its 31 December is beyond the last JavaScript date) and `275760` are Invalid Dates:
  Mongoose's `CastError: Cast to date failed for value "Invalid Date" (type Date) at path "startDate" for model "Job"`
  → 500 (Express's page, D16). V8's fallback parser reads `abc`, `-000000` (2001), `0` (2000), ` 2026` (2026) and
  `10000` (year 10000) — legacy counts those years.
- The legacy page asks for `new Date().getFullYear()` (the browser's year). Nothing is written.

### D10 in the file

19 cases have another filter; the answers that change: 2026 (`+002026`, ` 2026`) `1/2/2` → `2/3/2` (31 December
12:00 and 23:59:59.999), 2025 `0/0/0` → `1/0/0` (its last millisecond), 1995 `1/0/0` → `1/1/1` (1994-12-31T23:30Z is
1995 in Lisbon, 1995-12-31T22:00Z is 31 December), 1996 `0/0/0` → `0/0/1` (1995-12-31T23:30Z is 1996 in Lisbon).

### In the new app

`JobsService.CountValidTryOnFirstTestAsync` with `DashboardYear` (the D10 range in `App:TimeZone`) and
`MongoJobRepository.CountValidTryOnFirstTestAsync` (the three counts, `$lt` the next new year). Known difference: the
texts only V8's fallback parser reads are the 500 of an Invalid Date (`JsDate` ports the ECMAScript format only). Before
1912 .NET's zone data has Lisbon mean time in whole minutes (−0:36, ICU: −0:36:45); no job starts then. Replayed over
HTTP by `Dib4t.Server.Tests/Jobs/JobDashboardGoldenTests` (expected: `fixed`); the ranges are compared with the
`fixed` filters in `Dib4t.Application.Tests/Jobs/JobDashboardRulesTests`.
