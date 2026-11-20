#!/usr/bin/env node
// Phase 4 step 4 (PLAN.md §5, §6, §10): golden file for `POST /api/job/print/:budgetId` (the "Imprimir etiquetas"
// dialog), derived FROM THE LEGACY CODE by running the route handler itself. Nothing is started: no database, no
// network, no legacy server, no SSH, no printer.
//
//   node tools/legacy/job-print-golden.js [outDir]      (default tests/fixtures/legacy/jobs)
//
// The harness (./job-harness.js, shared with the other job golden files) runs, from ../back-end (DIB4T_LEGACY_ROOT),
// unchanged: the Express handler of routes/job.js with a recording `res` (a thrown error is a 500 by Express),
// validations/jobValidation.js (Joi 14.3.1, joi-objectid), repositories/jobRepository.js `getByBudgetId`, and
// models/printerSettings.js (`print` and `getTemplateData`) with telnet-client replaced by a recorder: each printer
// call is recorded as telnet-client would make it (host, port, timeout, negotiationMandatory, the sent text and its
// wire bytes: telnet-client 1.4.9 `send()` appends its `ors`, "\n"). Only two repository functions are replaced by
// the in-memory store: BudgetRepository.findById (the budget with its client and author populated, null when missing)
// and PrinterSettingsRepository.findByTemplateName (the case's printers as PrinterSettings documents, in order).
//
// NODE_ENV is set per case ("prod", or unset for the development guard): the handler reads it at request time.
// The cases that print are run twice: as is (`legacy`, TZ=UTC like the legacy production container) and with D23
// (ADR-0005: printed dates are the Europe/Lisbon date, as the new app does), `fixed`, run with TZ=Europe/Lisbon.
// A fixed clock (2026-03-15T10:20:30.123Z).
//
// Output: <outDir>/job-print-golden.json (README-print.md is hand-written). Two runs give identical files.

"use strict";

process.env.TZ = "UTC";

const path = require("path");
const fs = require("fs");
const {
  REPO_ROOT, RealDate, IDS, KNOWN_IDS, NAMES, legacyRequire, contacted, printerHook, j, oid, castId,
  jobShapes, budgetShapes, populatedBudget, run, goldenHeader,
} = require("./job-harness");

const OUT_DIR = path.resolve(process.argv[2] || path.join(REPO_ROOT, "tests/fixtures/legacy/jobs"));
const BudgetRepository = legacyRequire("./repositories/budgetRepository");
const PrinterSettingsRepository = legacyRequire("./repositories/printerSettingsRepository");
const { PrinterSettings } = legacyRequire("./models/printerSettings");

// ---------------------------------------------------------------- the print golden's own budgets and jobs
// (added to the shared store of the job golden files; the other golden files never see them)
const extra = (name, n) => {
  const budget = "5f1e9a2b3c4d5e6f708110" + n;
  const job = "5f1e9a2b3c4d5e6f708610" + n;
  for (const [key, hex] of [[`budget-${name}`, budget], [`job-${name}`, job]]) {
    IDS[key] = hex;
    KNOWN_IDS.set(hex, `{{${key}}}`);
    NAMES.set(`{{${key}}}`, hex);
  }
};
const base = budgetShapes.find((b) => b._id.toHexString() === IDS["budget-dev"]);
const baseJob = jobShapes.find((d) => d._id.toHexString() === IDS["job-dev"]);
function addBudget(name, patch = {}, omit = []) {
  const doc = { ...base, _id: oid(IDS[`budget-${name}`]), ...patch };
  for (const key of omit) delete doc[key];
  budgetShapes.push(doc);
}
function addJob(name, patch = {}, omit = []) {
  const doc = { ...baseJob, _id: oid(IDS[`job-${name}`]), budget: oid(IDS[`budget-${name}`]), ...patch };
  for (const key of omit) delete doc[key];
  jobShapes.push(doc);
}

extra("no-job", "01"); // a budget without a job
addBudget("no-job", { budgetNumber: "26-0101", descriptive: "Sem obra", hasGenerateJob: false });
extra("other-client", "02"); // its client is not in the entities (populate gives null)
IDS["missing-client"] = "5f1e9a2b3c4d5e6f708200ff";
KNOWN_IDS.set(IDS["missing-client"], "{{missing-client}}");
NAMES.set("{{missing-client}}", IDS["missing-client"]);
addBudget("other-client", { budgetNumber: "26-0102", descriptive: "Cliente apagado", client: oid(IDS["missing-client"]) });
addJob("other-client");
extra("no-client", "03"); // no client field
addBudget("no-client", { budgetNumber: "26-0103", descriptive: "Sem cliente" }, ["client"]);
addJob("no-client");
extra("late-start", "04"); // started 23:30 UTC in summer: 00:30 the next day in Lisbon (D23)
addBudget("late-start", { budgetNumber: "26-0104", descriptive: "Arranque tardio" });
addJob("late-start", { startDate: new RealDate("2026-07-31T23:30:00.000Z") });
extra("winter-start", "05"); // 23:30 UTC in winter: the same day in Lisbon
addBudget("winter-start", { budgetNumber: "26-0105", descriptive: "Arranque de inverno" });
addJob("winter-start", { startDate: new RealDate("2026-01-09T23:30:00.000Z") });
extra("null-start", "06"); // startDate stored null
addBudget("null-start", { budgetNumber: "26-0106", descriptive: "Início nulo" });
addJob("null-start", { startDate: null });
extra("no-start", "07"); // no startDate: Mongoose's default (Date.now) when the job is read
addBudget("no-start", { budgetNumber: "26-0107", descriptive: "Sem início" });
addJob("no-start", {}, ["startDate"]);
extra("no-texts", "08"); // no number and no descriptive: template literals print "undefined"
addBudget("no-texts", {}, ["budgetNumber", "descriptive"]);
addJob("no-texts");
extra("special", "09"); // commas, accents, a quote and a line break are sent as they are
addBudget("special", { budgetNumber: "26-0109", descriptive: "Suporte 30/40 mm, \"inox\"\nlinha 2 – ação" });
addJob("special");

// ---------------------------------------------------------------- the two replaced repository functions
BudgetRepository.findById = async (budgetId) => {
  const _id = castId(budgetId, "Budget");
  return _id ? populatedBudget(_id, true) : null;
};

const PRINTER_SETS = {
  both: [["producao", "10.0.0.1", 9100], ["maquinacao", "10.0.0.2", 9100]],
  "two-each": [["producao", "10.0.0.1", 9100], ["maquinacao", "10.0.0.2", 9100], ["producao", "10.0.0.3", 9101], ["maquinacao", "10.0.0.4", 9100]],
  "producao-only": [["producao", "10.0.0.1", 9100]],
  "maquinacao-only": [["maquinacao", "10.0.0.2", 9100]],
  none: [],
  // 10.0.0.9 refuses the connection: print() logs it and returns false, the route goes on
  "one-down": [["producao", "10.0.0.9", 9100], ["producao", "10.0.0.1", 9100], ["maquinacao", "10.0.0.2", 9100]],
};
const UNREACHABLE = "10.0.0.9";
let printers = [];
PrinterSettingsRepository.findByTemplateName = async (templateName) =>
  printers.filter((p) => p.templateName === templateName).map((p) => PrinterSettings.hydrate({ ...p, _id: oid(p._id), __v: 0 }));

let labels;
printerHook.record = (kind, value) => {
  if (kind === "connect") {
    labels.push({ host: value.host, port: value.port, timeout: value.timeout, negotiationMandatory: value.negotiationMandatory });
    if (value.host === UNREACHABLE) throw new Error(`connect ECONNREFUSED ${value.host}:${value.port}`);
  } else if (kind === "send") {
    const label = labels[labels.length - 1];
    label.send = value;
    label.wireHex = Buffer.from(value + "\n", "utf8").toString("hex");
  }
};

// ---------------------------------------------------------------- cases
const cases = [];
function print(section, budgetId, body, options = {}) {
  const nodeEnv = "nodeEnv" in options ? options.nodeEnv : "prod";
  const printerSet = options.printerSet || "both";
  cases.push({ section, budgetId, body: body === undefined ? undefined : typeof body === "string" ? body : j(body), nodeEnv, printerSet });
}
const all = { maquinacao: true, producao: true };

// the development guard comes first (legacy NODE_ENV not "prod"; the new app: not the Production environment)
print("environment", "{{budget-dev}}", all, { nodeEnv: undefined });
print("environment", "abc", {}, { nodeEnv: undefined });
print("environment", "{{missing-budget}}", { maquinacao: false, producao: false }, { nodeEnv: undefined, printerSet: "none" });

// Joi: the route id, then the body
print("validation", "abc", all);
print("validation", "abc", {});
print("validation", "5f1e9a2b3c4d5e6f708100", all);
print("validation", "5F1E9A2B3C4D5E6F70810001", all);
print("validation", "{{budget-dev}}", {});
print("validation", "{{budget-dev}}", { maquinacao: true });
print("validation", "{{budget-dev}}", { producao: true });
print("validation", "{{budget-dev}}", { producao: true, maquinacao: true, copies: 2 });
print("validation", "{{budget-dev}}", { maquinacao: 1, producao: true });
print("validation", "{{budget-dev}}", { maquinacao: true, producao: 0 });
print("validation", "{{budget-dev}}", { maquinacao: "yes", producao: true });
print("validation", "{{budget-dev}}", { maquinacao: null, producao: true });
print("validation", "{{budget-dev}}", { maquinacao: true, producao: [] });
print("validation", "{{budget-dev}}", "[]");
print("validation", "{{budget-dev}}", "[true, true]");
print("validation", "{{budget-dev}}", { maquinacao: false, producao: false });
print("validation", "{{missing-budget}}", { maquinacao: false, producao: false });
// Joi converts "true"/"false" (any case) for its check only; the route then reads the ORIGINAL body:
// `=== false` for "Escolhe um template", JavaScript truthiness for each template ("false" is a non-empty string)
print("validation", "{{budget-dev}}", { maquinacao: "false", producao: false });
print("validation", "{{budget-dev}}", { maquinacao: "false", producao: "false" });
print("validation", "{{budget-dev}}", { maquinacao: "TRUE", producao: "False" });
print("validation", "{{budget-dev}}", { maquinacao: false, producao: "false" }, { printerSet: "producao-only" });

// the budget, then its job
print("lookup", "{{missing-budget}}", all);
print("lookup", "{{budget-no-job}}", all);
print("lookup", "{{budget-no-budget}}", all); // the job's budget does not exist
print("lookup", "5f1e9a2b3c4d5e6f708100ff", { maquinacao: true, producao: false });

// the printers of each selected template: producao first, then maquinacao
print("print", "{{budget-dev}}", all);
print("print", "{{budget-dev}}", { maquinacao: false, producao: true });
print("print", "{{budget-dev}}", { maquinacao: true, producao: false });
print("print", "{{budget-dev}}", all, { printerSet: "two-each" });
print("print", "{{budget-dev}}", all, { printerSet: "one-down" });
print("print", "{{budget-dev}}", all, { printerSet: "none" });
print("print", "{{budget-dev}}", { maquinacao: true, producao: false }, { printerSet: "none" });
print("print", "{{budget-dev}}", all, { printerSet: "producao-only" }); // labels of producao, then the 400
print("print", "{{budget-dev}}", all, { printerSet: "maquinacao-only" });
print("print", "{{budget-dev}}", { maquinacao: true, producao: false }, { printerSet: "producao-only" });
print("print", "{{budget-approved}}", all); // a revision (R2)
print("print", "{{budget-deleted}}", all); // any state: "Apagada" too
print("print", "{{budget-billed}}", { maquinacao: false, producao: true });
print("print", "{{budget-old}}", all); // an imported old job
print("print", "{{budget-no-stats}}", all);
print("print", "{{budget-late-start}}", all); // D23: legacy prints the UTC date (31/7), the fix the Lisbon date (1/8)
print("print", "{{budget-winter-start}}", all);
print("print", "{{budget-no-start}}", all); // Mongoose's default: the clock's date
print("print", "{{budget-null-start}}", all); // TypeError before the first label
print("print", "{{budget-null-start}}", all, { printerSet: "none" }); // no printer, no label: the 400
print("print", "{{budget-other-client}}", all); // TypeError (client.name of null) before the first label
print("print", "{{budget-other-client}}", { maquinacao: true, producao: false }, { printerSet: "producao-only" });
print("print", "{{budget-no-client}}", all);
print("print", "{{budget-no-texts}}", all);
print("print", "{{budget-special}}", all);

// ---------------------------------------------------------------- run
async function runCase(c, tz) {
  process.env.TZ = tz;
  printers = PRINTER_SETS[c.printerSet].map(([templateName, ip, port], i) => ({ _id: "5f1e9a2b3c4d5e6f708b00" + (i + 1).toString(16).padStart(2, "0"), ip, port, templateName }));
  labels = [];
  const outcome = await run({ method: "post", route: "/print/:budgetId", params: { budgetId: c.budgetId }, body: c.body, nodeEnv: c.nodeEnv });
  process.env.TZ = "UTC";
  if (outcome.effects.length) throw new Error("print wrote to the jobs: " + j(outcome.effects));
  return { ...outcome, labels };
}

(async () => {
  const golden = goldenHeader("tools/legacy/job-print-golden.js");
  golden.printerSets = Object.fromEntries(Object.entries(PRINTER_SETS).map(([name, list]) => [name, list.map(([templateName, ip, port]) => ({ ip, port, templateName }))]));
  golden.unreachablePrinter = UNREACHABLE;
  golden.fixes = { print: "D23 (ADR-0005): the label date is the Europe/Lisbon date of the job's start (run with TZ=Europe/Lisbon)" };
  for (const c of cases) {
    const item = {
      section: c.section,
      method: "POST",
      path: "/api/job/print/" + c.budgetId,
      body: c.body === undefined ? null : c.body,
      nodeEnv: c.nodeEnv === undefined ? null : c.nodeEnv,
      printers: c.printerSet,
    };
    item.legacy = await runCase(c, "UTC");
    if (c.section === "print") item.fixed = await runCase(c, "Europe/Lisbon");
    golden.cases.push(item);
  }
  if (contacted.length) throw new Error("unexpected NAS/printer contact: " + contacted.join(" | "));

  fs.mkdirSync(OUT_DIR, { recursive: true });
  const file = path.join(OUT_DIR, "job-print-golden.json");
  fs.writeFileSync(file, JSON.stringify(golden, null, 2) + "\n");
  const count = (s) => golden.cases.filter((c) => c.section === s).length;
  const changedByFix = golden.cases.filter((c) => c.fixed && j(c.fixed) !== j(c.legacy)).length;
  const sent = golden.cases.reduce((n, c) => n + (c.fixed || c.legacy).labels.filter((l) => l.send !== undefined).length, 0);
  console.log(
    `wrote ${path.relative(REPO_ROOT, file)}: ${golden.cases.length} cases (environment ${count("environment")}, validation ${count("validation")}, ` +
      `lookup ${count("lookup")}, print ${count("print")}; ${sent} labels sent; ${changedByFix} changed by D23)`,
  );
  process.exit(0);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
