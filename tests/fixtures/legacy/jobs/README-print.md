# Job label print golden file (from the legacy code)

The contract of "Imprimir etiquetas" (`POST /api/job/print/:budgetId`, PLAN.md §5, §6, Phase 4 step 4), **derived
from the legacy code by running it**, offline.

## `job-print-golden.json`

`tools/legacy/job-print-golden.js` (`tools/legacy/legacy.sh job-print-golden`) runs the **unchanged** handler of
`POST print/:budgetId` of `routes/job.js`, with `validations/jobValidation.js` (Joi 14.3.1, joi-objectid),
`repositories/jobRepository.js` (`getByBudgetId`) and `models/printerSettings.js` (`print`, `getTemplateData`),
through the harness shared with the other job golden files (`tools/legacy/job-harness.js`: the same 15 job shapes,
the clock `2026-03-15T10:20:30.123Z`). Only two repository functions are replaced by the in-memory store:
`BudgetRepository.findById` (client and author populated, `null` when missing) and
`PrinterSettingsRepository.findByTemplateName` (the case's printers, in order). `telnet-client` is a recorder: each
printer call is recorded as telnet-client 1.4.9 would make it (`host`, `port`, `timeout` 1500,
`negotiationMandatory: false`, the sent text `send` and its wire bytes `wireHex` = `UTF8(send + "\n")`, the `ors`
`send()` appends). Nothing is started and nothing is contacted. Two runs give byte-identical files.

`NODE_ENV` is set per case (`nodeEnv`: `"prod"`, or `null` for the development guard). The print section is run twice:
`legacy` (`TZ=UTC`, like the legacy production container) and `fixed` with **D23** (ADR-0005: printed dates are the
Europe/Lisbon date; run with `TZ=Europe/Lisbon`). Only the late-summer start changes (`31/7/2026` → `1/8/2026`).

53 cases: `environment` 3, `validation` 21, `lookup` 4, `print` 25; 38 labels sent. The file adds 9 budgets/jobs of
its own to the shared store (`budget-no-job` … `budget-special`: no job, a client that is gone, no client, late summer
and winter starts, `startDate` `null` and missing, no number/descriptive, commas/quotes/line breaks). `printerSets`
lists the printers of each case (`printers`); `unreachablePrinter` refuses the connection.

### Findings (kept for parity)

- **Order**: not `NODE_ENV === "prod"` → 400 `"Só funciona em produção!"` before anything (an invalid id or body too)
  → Joi of the route id → Joi of the body (`maquinacao`, then `producao`, both `Joi.boolean().required()`, no other
  key; an array is `"value" must be an object`) → both the JSON `false` → 400 `"Escolhe um template"` (before the
  budget is looked up) → the budget (`"Orçamento não encontrado"`) → its job (`"Obra encontrada não encontrado"`,
  verbatim) → if `producao`: its printers (none: 400 `"Configurações de impressora para produção não encontradas"`),
  a label on each → if `maquinacao`: the same (400 `"… para maquinação não encontradas"`, **after** the `producao`
  labels were sent) → 200 `"ok"` (text).
- **The original body decides**: Joi converts `"true"`/`"false"` (any case) only for its check; the route then reads
  `req.body`: `=== false` for "Escolhe um template", JavaScript truthiness for each template. So `"false"` prints
  (`{"maquinacao":"false","producao":"false"}` prints both). `1`, `0`, `"yes"`, `null`, `[]` are
  `must be a boolean`.
- **Labels**: as job create (`job-golden.json` "labels"): `ESC i a 3` + the template, the job's `startDate` as
  `d/M/yyyy`; a missing `startDate` is Mongoose's default when the job is read (now); missing number/descriptive print
  `undefined`; commas, quotes and line breaks are sent as they are. Any job state prints (also "Apagada"), a revision
  prints its own number/descriptive.
- **What legacy throws on** (500, nothing sent): `startDate` stored `null` (`getDate` of `null`) and a budget whose
  client is gone or missing (`client.name` of `null`) — `getTemplateData` runs before the first label is sent, after the
  printer lookup (so with no printer it is the 400 instead).
- **A printer that refuses the connection** is logged (`Printer error: …`) and the route goes on with the others;
  the answer is still `"ok"` (legacy ignores `print()`'s result).
- Nothing is written (no case has a save).

### In the new app

`JobsService.PrintLabelsAsync` follows this order; the Production guard is the environment (`Production` ≙
`NODE_ENV=prod`); the label date is D23's; the 500s are D16 with the reason in the log; the 2 s wait per printer of
`telnet-client` is not reproduced (PLAN.md §5). Replayed over HTTP and TCP by
`Dib4t.Server.Tests/Jobs/JobPrintGoldenTests` (Production, every printer on an in-test listener) and
`JobPrintDevelopmentGoldenTests`.
