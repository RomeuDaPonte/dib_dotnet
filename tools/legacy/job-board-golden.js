#!/usr/bin/env node
// Phase 4 step 2 (PLAN.md §6, §7.3, §10): golden file for the jobs board routes, derived FROM THE LEGACY CODE by
// running the route handlers themselves:
//   PUT statistics/:jobId, PATCH dueDate/:jobId, PATCH state/:jobId, PATCH changeJobPriority/:jobId,
//   GET getCounts, getUnfinished, getAll, getByState/:state, getById/:jobId, /:jobId, getByBudgetNumber/:n,
//   getByBudgetId/:budgetId.
// Nothing is started: no database, no network, no legacy server, no SSH, no printer.
//
//   node tools/legacy/job-board-golden.js [outDir]      (default tests/fixtures/legacy/jobs)
//
// Runs, from ../back-end (DIB4T_LEGACY_ROOT), unchanged:
//   - the Express handlers of routes/job.js, verbatim, with a recording `res` and express-async-errors' behavior (a
//     thrown error is a 500 by Express), NODE_ENV unset;
//   - validations/jobValidation.js (Joi 14.3.1, joi-objectid) through them;
//   - models/job.js (setStatistics, updateStateDates, changeState, …) and models/jobStatistics.js (confirmDeviation)
//     with Mongoose 5.13's casts, `validate()` and `$__delta()` (what `save()` would send, $inc of __v included);
//   - repositories/jobRepository.js's own ordering (orderArray) on top of an in-memory store that answers the
//     MongoDB queries: find with the filter, sort({ dueDate: 1 }) (missing first, ties in natural order), populate
//     (budget → client [→ madeBy]; null when the referenced document is missing), countDocuments, findById casts.
// `PATCH state/:jobId` reads `req.params.id` (D1): every case is run twice, as is (`legacy`) and with `req.params.id`
// = the job id (`fixed`, the legacy code with D1 fixed, which the new app implements).
// A fixed clock (2026-03-15T10:20:30.123Z, TZ=UTC like the legacy production container).
//
// The harness (legacy modules, store, clock, recorders, run) is ./job-harness.js, shared with job-details-golden.js.
// Output: <outDir>/job-board-golden.json (README.md is hand-written). Two runs give identical files.

"use strict";

process.env.TZ = "UTC";

const path = require("path");
const fs = require("fs");
const { REPO_ROOT, contacted, j, run, goldenHeader } = require("./job-harness");

const OUT_DIR = path.resolve(process.argv[2] || path.join(REPO_ROOT, "tests/fixtures/legacy/jobs"));

// ---------------------------------------------------------------- cases
const cases = [];
const add = (section, spec) => cases.push({ section, ...spec });

// PUT statistics/:jobId -------------------------------------------------------------------------------------------
const stats = (job, body) => add("statistics", { method: "put", route: "/statistics/:jobId", params: { jobId: `{{job-${job}}}` }, body: j(body) });
const statsRaw = (jobId, body) => add("statistics", { method: "put", route: "/statistics/:jobId", params: { jobId }, body: j(body) });

// development / approval / approved: the dialog's form (texts once typed), Joi validateDevStatistics
const devForm = { numberOfHoursUsed: "18", components: "120.5", automation: "40", invoicedPercent: 0, budgetedHours: 20.5, deviation: 2.5, comments: "", commentType: [] };
const withDev = (patch) => ({ ...devForm, state: "Em Aprovação", ...patch });
stats("dev", withDev({}));
stats("dev", withDev({ numberOfHoursUsed: 18, components: 120.5, automation: 40 }));
for (const state of ["Desenvolvimento", "Aprovado", "Maquinação", "Montagem", "Para faturação", "Faturada"]) stats("dev", withDev({ state }));
stats("dev", withDev({ state: "Faturada", invoicedPercent: 100 }));
stats("dev", withDev({ invoicedPercent: undefined }));
stats("dev", withDev({ invoicedPercent: "50" }));
stats("dev", withDev({ commentType: ["Outros", "Tipo de material"], comments: "  Ver desenho rev. B  " }));
stats("dev", withDev({ commentType: undefined }));
stats("dev", withDev({ commentType: "[]" }));
stats("dev", withDev({ commentType: ["x"] }));
stats("dev", withDev({ commentType: ["x"], numberOfHoursUsed: undefined }));
stats("dev", withDev({ comments: 5 }));
stats("dev", withDev({ comments: null }));
stats("dev", withDev({ comments: { a: 1 } }));
stats("dev", withDev({ comments: undefined }));
stats("dev", withDev({ deviation: 5 }));
stats("dev", withDev({ deviation: "2.5" }));
stats("dev", withDev({ numberOfHoursUsed: "20.5", deviation: 0 }));
stats("dev", withDev({ numberOfHoursUsed: "0.1", deviation: 20.4 }));
stats("dev", withDev({ numberOfHoursUsed: -1 }));
stats("dev", withDev({ numberOfHoursUsed: "abc" }));
stats("dev", withDev({ numberOfHoursUsed: "" }));
stats("dev", withDev({ numberOfHoursUsed: undefined }));
stats("dev", withDev({ components: undefined }));
stats("dev", withDev({ automation: -5 }));
stats("dev", withDev({ invoicedPercent: 10.5 }));
stats("dev", withDev({ invoicedPercent: 101 }));
stats("dev", withDev({ invoicedPercent: -1 }));
stats("dev", withDev({ state: "Apagada" }));
stats("dev", withDev({ state: undefined }));
stats("dev", withDev({ state: "outro" }));
stats("dev", withDev({ budgetedHours: undefined }));
stats("dev", withDev({ budgetedHours: "abc" }));
stats("dev", withDev({ deviation: undefined }));
stats("dev", withDev({ materials: 0 }));
stats("dev", withDev({ deliveryDate: "" }));
stats("dev", {});
stats("onapproval", withDev({ state: "Aprovado", numberOfHoursUsed: "19", deviation: 1.5, invoicedPercent: 10, commentType: ["Detalhes de fabrico"], comments: "Desenvolvimento concluído." }));
stats("onapproval", withDev({ state: "Em Aprovação", numberOfHoursUsed: 18, components: 120.5, automation: 40, commentType: ["Detalhes de fabrico"], comments: "Desenvolvimento concluído." }));
stats("onapproval", withDev({ state: "Desenvolvimento" }));
stats("approved", withDev({ state: "Maquinação", numberOfHoursUsed: "20", deviation: 0.5, invoicedPercent: 20 }));
stats("approved", withDev({ state: "Para faturação" }));
stats("dates-null", withDev({ state: "Maquinação" }));
stats("dates-null", withDev({ state: "Aprovado" }));
stats("no-stats", withDev({ state: "Em Aprovação", deviation: 999 }));
stats("no-stats", withDev({ state: "Faturada" }));
stats("no-budget", withDev({}));

// machining: validateMachiningStatistics (integers)
const machForm = {
  numberOfHoursUsed: "14", materials: "300", budgetedHours: 15, deviation: 1, invoicedPercent: 30,
  verificationAndShapeControl: "Ok", dimensionalVerificationAndControl: "Nok", analysisOfTheDegreeOfFinish: "n/a", comments: "Peça 12 retrabalhada.", commentType: ["Tipo de material", "Tolerâncias a respeitar"],
};
const withMach = (patch) => ({ ...machForm, state: "Montagem", ...patch });
stats("machining", withMach({}));
stats("machining", withMach({ state: "Maquinação" }));
stats("machining", withMach({ state: "Para faturação", invoicedPercent: 0 }));
stats("machining", withMach({ numberOfHoursUsed: "14.5", deviation: 0.5 }));
stats("machining", withMach({ materials: 1.5 }));
stats("machining", withMach({ invoicedPercent: undefined }));
stats("machining", withMach({ verificationAndShapeControl: "OK" }));
stats("machining", withMach({ analysisOfTheDegreeOfFinish: undefined }));
stats("machining", withMach({ components: 0 }));
stats("machining", withMach({ deviation: 2 }));
stats("machining", withMach({ commentType: undefined }));
// the legacy comments dialog on a machining job: no invoicedPercent (Joi requires it)
stats("machining", { state: "Maquinação", numberOfHoursUsed: 0, budgetedHours: 15, deviation: 15, comments: "Nota", commentType: [], materials: 0, verificationAndShapeControl: "n/a", dimensionalVerificationAndControl: "Nok", analysisOfTheDegreeOfFinish: "n/a" });
stats("old", withMach({ numberOfHoursUsed: "15", deviation: 0, commentType: [] }));
stats("machining-no-stat", withMach({}));

// assembly: validateAssemblyStatistics (integers, deliveryDate any)
const asmForm = {
  numberOfHoursUsed: "12", components: "150", automation: "45", budgetedHours: 10, deviation: -2, deliveryDate: "2030-03-02", invoicedPercent: 60,
  pneumaticCircuits: "Ok", electricalBoardsAndCircuits: "Ok", kinematics: "Nok", programmingFeaturesPLC: "n/a", operator_equipmentSafetyConditions: "Ok",
  visualInspection: "Ok", validTryOnFirstTest: "2", comments: "", commentType: [],
};
const withAsm = (patch) => ({ ...asmForm, state: "Para faturação", ...patch });
stats("assembly", withAsm({}));
stats("assembly", withAsm({ state: "Montagem" }));
stats("assembly", withAsm({ deliveryDate: "" }));
stats("assembly", withAsm({ deliveryDate: undefined }));
stats("assembly", withAsm({ deliveryDate: null }));
stats("assembly", withAsm({ deliveryDate: "abc" }));
stats("assembly", withAsm({ deliveryDate: "2030-03-02T10:00:00.000Z" }));
stats("assembly", withAsm({ validTryOnFirstTest: 2 }));
stats("assembly", withAsm({ validTryOnFirstTest: "4" }));
stats("assembly", withAsm({ deviation: -2.5, numberOfHoursUsed: "12.5" }));
stats("assembly", withAsm({ kinematics: undefined }));
stats("assembly", withAsm({ materials: 0 }));
stats("assembly", withAsm({ state: "Faturada", invoicedPercent: 100 }));
stats("assembly-fraction", withAsm({ budgetedHours: 10.5, deviation: -1.5 }));
stats("assembly-fraction", withAsm({ budgetedHours: 10, deviation: -1.5 }));

// para faturação: validateForInvoicingStatistics
const invForm = { orderIdentification: "ENC-2030/001-A", isOfficialOrderIdentification: true, deliveryDate: "2030-03-02", invoicedPercent: 100, comments: "", commentType: [] };
const withInv = (patch) => ({ ...invForm, state: "Faturada", ...patch });
stats("invoicing", withInv({}));
stats("invoicing", withInv({ invoicedPercent: 80, comments: "Falta faturar 20 %." }));
stats("invoicing", withInv({ invoicedPercent: "100" }));
stats("invoicing", withInv({ isOfficialOrderIdentification: "true" }));
stats("invoicing", withInv({ isOfficialOrderIdentification: "TRUE" }));
stats("invoicing", withInv({ isOfficialOrderIdentification: "TRUE", invoicedPercent: 80 }));
stats("invoicing", withInv({ orderIdentification: "" }));
stats("invoicing", withInv({ orderIdentification: undefined }));
stats("invoicing", withInv({ invoicedPercent: undefined }));
stats("invoicing", withInv({ commentType: undefined }));
stats("invoicing", withInv({ deliveryDate: "" }));
stats("invoicing", withInv({ state: "Para faturação", invoicedPercent: 60, comments: "Ver nota" }));
stats("invoicing", withInv({ state: "Montagem", invoicedPercent: 60 }));
stats("invoicing", withInv({ state: "Apagada" }));
stats("invoicing", withInv({ numberOfHoursUsed: 0 }));
stats("invoicing-full", withInv({}));

// billed / deleted: no Joi schema for these states (the comments dialog on the "Faturadas" tab)
const billedComment = { state: "Faturada", numberOfHoursUsed: 0, components: 0, automation: 0, budgetedHours: 20.5, deviation: 20.5, comments: "Arquivado.", commentType: ["Outros"] };
stats("billed", billedComment);
stats("billed", { ...billedComment, commentType: undefined });
stats("billed", { ...billedComment, state: "Desenvolvimento" });
stats("billed", { state: "Faturada", commentType: [], whatever: true });
stats("deleted", { ...billedComment, state: "Apagada" });
stats("deleted", { ...billedComment, state: "Faturada" });

statsRaw("{{missing}}", withDev({}));
statsRaw("abc", withDev({}));

// PATCH dueDate/:jobId -----------------------------------------------------------------------------------------------
const due = (jobId, body) => add("dueDate", { method: "patch", route: "/dueDate/:jobId", params: { jobId }, body: body === undefined ? undefined : j(body) });
due("{{job-dev}}", { dueDate: "2030-05-01T00:00:00.000Z" });
due("{{job-dev}}", { dueDate: "2030-05-01" });
due("{{job-dev}}", { dueDate: "2030-02-10" });
due("{{job-dev}}", { dueDate: "2030-02-30" });
due("{{job-dev}}", { dueDate: 1894665600000 });
due("{{job-dev}}", { dueDate: "" });
due("{{job-dev}}", { dueDate: null });
due("{{job-dev}}", { dueDate: "abc" });
due("{{job-dev}}", {});
due("{{job-dev}}", { dueDate: "2030-05-01", extra: 1 });
due("{{job-old}}", { dueDate: "2030-05-01" });
due("{{job-billed}}", { dueDate: "2030-05-01" });
due("{{missing}}", { dueDate: "2030-05-01" });
due("abc", { dueDate: "2030-05-01" });

// PATCH state/:jobId (D1: run as is and with the id fixed) --------------------------------------------------------
const stateBodies = [
  ["invoicing", { state: "Apagada" }],
  ["invoicing", { state: "Faturada" }],
  ["invoicing-full", { state: "Faturada" }],
  ["billed", { state: "Para faturação" }],
  ["billed", { state: "Faturada" }],
  ["deleted", { state: "Desenvolvimento" }],
  ["deleted", { state: "Faturada" }],
  ["dev", { state: "Apagada" }],
  ["old", { state: "Apagada" }],
  ["dev", { state: "Outro" }],
  ["dev", {}],
  ["dev", { state: "Apagada", extra: 1 }],
  ["missing", { state: "Apagada" }],
];
for (const [job, body] of stateBodies) {
  const jobId = job === "missing" ? "{{missing}}" : `{{job-${job}}}`;
  add("state", { method: "patch", route: "/state/:jobId", params: { jobId }, body: j(body) });
}
add("state", { method: "patch", route: "/state/:jobId", params: { jobId: "abc" }, body: j({ state: "Apagada" }) });

// PATCH changeJobPriority/:jobId --------------------------------------------------------------------------------------
const prio = (jobId, body) => add("priority", { method: "patch", route: "/changeJobPriority/:jobId", params: { jobId }, body: j(body) });
for (const priority of ["Alta", "Média", "Baixa"]) prio("{{job-dev}}", { priority });
prio("{{job-old}}", { priority: "Alta" });
prio("{{job-onapproval}}", { priority: "Alta" });
prio("{{job-dev}}", { priority: "Urgente" });
prio("{{job-dev}}", { priority: "alta" });
prio("{{job-dev}}", {});
prio("{{job-dev}}", { priority: null });
prio("{{job-dev}}", { priority: "Alta", extra: 1 });
prio("{{missing}}", { priority: "Urgente" });
prio("abc", { priority: "Alta" });

// GET routes ------------------------------------------------------------------------------------------------------
const get = (route, params = {}) => add("get", { method: "get", route, params });
get("/getCounts");
get("/getUnfinished");
get("/getAll");
for (const state of ["Desenvolvimento", "Em Aprovação", "Aprovado", "Maquinação", "Montagem", "Para faturação", "Faturada", "Apagada", "Outro", "aprovado"]) {
  get("/getByState/:state", { state });
}
for (const jobId of ["{{job-dev}}", "{{job-old}}", "{{job-no-budget}}", "{{job-invoicing}}", "{{missing}}", "abc"]) get("/getById/:jobId", { jobId });
for (const jobId of ["{{job-assembly}}", "{{job-no-budget}}", "{{missing}}", "abc"]) get("/:jobId", { jobId });
for (const budgetNumber of ["26-0001", "26-0009", "26-9999", "26-1"]) get("/getByBudgetNumber/:budgetNumber", { budgetNumber });
for (const budgetId of ["{{budget-dev}}", "{{budget-old}}", "{{missing-budget}}", "abc"]) get("/getByBudgetId/:budgetId", { budgetId });

// ---------------------------------------------------------------- write
(async () => {
  const golden = goldenHeader("tools/legacy/job-board-golden.js");
  for (const c of cases) {
    const route = c.route.replace(/:([a-zA-Z]+)/g, (_m, key) => c.params[key]);
    const item = { section: c.section, method: c.method.toUpperCase(), path: "/api/job" + (route === "/" ? "" : route), body: c.body === undefined ? null : c.body };
    if (c.section === "state") {
      item.legacy = await run(c);
      item.fixed = await run({ ...c, d1Fixed: true });
    } else {
      Object.assign(item, await run(c));
    }
    golden.cases.push(item);
  }
  if (contacted.length) throw new Error("unexpected NAS/printer contact: " + contacted.join(" | "));

  fs.mkdirSync(OUT_DIR, { recursive: true });
  const file = path.join(OUT_DIR, "job-board-golden.json");
  fs.writeFileSync(file, JSON.stringify(golden, null, 2) + "\n");
  const count = (s) => golden.cases.filter((c) => c.section === s).length;
  console.log(`wrote ${path.relative(REPO_ROOT, file)}: ${golden.cases.length} cases (statistics ${count("statistics")}, dueDate ${count("dueDate")}, state ${count("state")}, priority ${count("priority")}, get ${count("get")})`);
  process.exit(0);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
