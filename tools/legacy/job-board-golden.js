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
// Output: <outDir>/job-board-golden.json (README.md is hand-written). Two runs give identical files.

"use strict";

process.env.TZ = "UTC";

const path = require("path");
const fs = require("fs");
const Module = require("module");

const REPO_ROOT = path.resolve(__dirname, "../..");
const BACKEND = path.resolve(process.env.DIB4T_LEGACY_ROOT || path.join(REPO_ROOT, ".."), "back-end");
const OUT_DIR = path.resolve(process.argv[2] || path.join(REPO_ROOT, "tests/fixtures/legacy/jobs"));

// ---------------------------------------------------------------- fixed clock (before any legacy require)
const NOW = Date.parse("2026-03-15T10:20:30.123Z");
const RealDate = Date;
class FixedDate extends RealDate {
  constructor(...args) {
    if (args.length === 0) super(NOW);
    else super(...args);
  }
  static now() {
    return NOW;
  }
}
Object.defineProperty(FixedDate, "name", { value: "Date" }); // Mongoose checks the schema type's name
global.Date = FixedDate;

// ---------------------------------------------------------------- module stubs (before any legacy require)
function stubModule(request, fromFile, exportsValue) {
  const resolved = Module.createRequire(fromFile).resolve(request);
  const m = new Module(resolved);
  m.filename = resolved;
  m.loaded = true;
  m.exports = exportsValue;
  require.cache[resolved] = m;
}

const contacted = [];
class SshRecorder {
  exec(command) {
    contacted.push("ssh " + command);
    return Promise.resolve("1\n");
  }
}
stubModule("ssh2-promise", path.join(BACKEND, "helpers/pathHlelper.js"), SshRecorder);
class TelnetRecorder {
  async connect() { contacted.push("telnet connect"); }
  async send() { return ""; }
  async end() {}
}
stubModule("telnet-client", path.join(BACKEND, "models/printerSettings.js"), TelnetRecorder);

const legacyRequire = Module.createRequire(path.join(BACKEND, "index.js"));
const _ = legacyRequire("lodash");
const mongoose = legacyRequire("mongoose");
const { Budget } = legacyRequire("./models/budget");
const { Task } = legacyRequire("./models/task");
const { Entity } = legacyRequire("./models/entity");
const { User } = legacyRequire("./models/user");
const { Job } = legacyRequire("./models/job");
const JobRepository = legacyRequire("./repositories/jobRepository");
const TaskRepository = legacyRequire("./repositories/taskRepository");
const jobRouter = legacyRequire("./routes/job");
const BSON = legacyRequire("bson"); // 1.1.6, the serializer of the legacy driver
const bson = new BSON();

const versions = {
  bson: legacyRequire("bson/package.json").version,
  express: legacyRequire("express/package.json").version,
  joi: legacyRequire("joi/package.json").version,
  "joi-objectid": legacyRequire("joi-objectid/package.json").version,
  mongoose: legacyRequire("mongoose/package.json").version,
  node: process.version,
};

const oid = (hex) => new mongoose.Types.ObjectId(hex);

// ---------------------------------------------------------------- ids
const JOB_NAMES = [
  "dev", "onapproval", "approved", "machining", "assembly", "invoicing", "billed", "deleted",
  "old", "no-stats", "dates-null", "no-budget", "machining-no-stat", "assembly-fraction", "invoicing-full",
];
const IDS = {
  client: "5f1e9a2b3c4d5e6f70820001",
  user: "5f1e9a2b3c4d5e6f70830001",
  "missing-budget": "5f1e9a2b3c4d5e6f708100ff",
  missing: "5f1e9a2b3c4d5e6f708600ff",
};
JOB_NAMES.forEach((name, i) => {
  const n = (i + 1).toString(16).padStart(2, "0");
  IDS[`job-${name}`] = "5f1e9a2b3c4d5e6f708600" + n;
  IDS[`budget-${name}`] = "5f1e9a2b3c4d5e6f708100" + n;
  IDS[`dev-${name}`] = "5f1e9a2b3c4d5e6f708700" + n;
  IDS[`mach-${name}`] = "5f1e9a2b3c4d5e6f708800" + n;
  IDS[`asm-${name}`] = "5f1e9a2b3c4d5e6f708900" + n;
  IDS[`dates-${name}`] = "5f1e9a2b3c4d5e6f708a00" + n;
});
const KNOWN_IDS = new Map(Object.entries(IDS).map(([name, hex]) => [hex, `{{${name}}}`]));
const NAMES = new Map([...KNOWN_IDS].map(([hex, name]) => [name, hex]));
const j = (value) => JSON.stringify(value);

/** A JS value as canonical Extended JSON with the js-bson 1.x number rule (what the legacy driver stores). */
function bsonValue(value) {
  if (value === null || value === undefined) return null;
  if (value instanceof mongoose.Types.ObjectId) return { $oid: idText(value.toHexString()) };
  if (value instanceof RealDate) return { $date: isNaN(value.getTime()) ? "Invalid Date" : value.toISOString() };
  if (Array.isArray(value)) return value.map(bsonValue);
  if (typeof value === "number") {
    if (Number.isNaN(value)) return { $numberDouble: "NaN" };
    const isInt32 = bson.serialize({ v: value })[4] === 0x10;
    return isInt32 ? { $numberInt: String(value) } : { $numberDouble: String(value) };
  }
  if (typeof value === "object") {
    const out = {};
    for (const key of Object.keys(value)) out[key] = bsonValue(value[key]);
    return out;
  }
  return value;
}

function idText(hex) {
  return KNOWN_IDS.get(hex) || "{{new}}";
}

/** Names in a test text ("{{job-dev}}") → their hex ids, so paths and bodies are real. */
function bind(text) {
  return text.replace(/\{\{[a-z0-9-]+\}\}/g, (name) => NAMES.get(name) || name);
}

/** res.send(doc): JSON.stringify of toJSON, with ids replaced by their names (populated sub-documents included). */
function answer(value) {
  return JSON.parse(JSON.stringify(value), (key, v) => (typeof v === "string" && /^[0-9a-f]{24}$/.test(v) ? idText(v) : v));
}

function saveDelta(doc) {
  const delta = doc.$__delta();
  if (!delta) return null;
  const update = delta[1];
  const out = {};
  for (const op of Object.keys(update).sort()) out[op] = bsonValue(update[op]);
  return out;
}

// ---------------------------------------------------------------- stored documents
const entityDoc = {
  _id: oid(IDS.client),
  name: "Metalúrgica Exemplo, Lda.",
  fiscalNumber: "509999990",
  address: "Rua das Flores, 12",
  zipCode: "4000-123",
  location: "Porto",
  paymentCondition: "30 dias",
  type: "Cliente",
  __v: 0,
};
const userDoc = {
  _id: oid(IDS.user),
  roles: ["canSeeObras", null, "canSeeDefinicoes"],
  fieldOfCompetencies: "Montagem",
  name: "Ana Administradora",
  email: "ana.admin@dib4t.local",
  password: "$2a$10$abcdefghijklmnopqrstuuabcdefghijklmnopqrstuvwxyz01234",
  __v: 0,
};

const budgetShapes = JOB_NAMES.filter((name) => name !== "no-budget").map((name, i) => ({
  _id: oid(IDS[`budget-${name}`]),
  revision: name === "approved" ? 2 : 0,
  complete: true,
  wasPdfDownloaded: true,
  margin: 35,
  daysNeededToDoTheWork: 45,
  validity: 30,
  hasGenerateJob: true,
  client: oid(IDS.client),
  descriptive: `Máquina ${name}`,
  personInCharge: "Eng.ª Maria",
  madeBy: oid(IDS.user),
  budgetNumber: `26-${(i + 1).toString().padStart(4, "0")}`,
  paymentMethod: "30 dias",
  total: 12345.67,
  date: new RealDate("2026-02-01T09:00:00.000Z"),
  __v: 0,
  description: "Fornecimento conforme especificação.",
}));

let nextTask = 1;
const taskShapes = [];
for (const name of JOB_NAMES) {
  if (name === "no-budget") continue;
  const add = (taskType, quantity) => {
    const hex = "5f1e9a2b3c4d5e6f7084" + (nextTask++).toString(16).padStart(4, "0");
    KNOWN_IDS.set(hex, `{{task-${nextTask - 1}}}`);
    NAMES.set(`{{task-${nextTask - 1}}}`, hex);
    taskShapes.push({ _id: oid(hex), budgetId: oid(IDS[`budget-${name}`]), taskType, description: "", unitPrice: 30, quantity, __v: 0 });
  };
  add("automation", 1);
  add("development", 20);
  add("development", 0.5);
  add("machining", 15);
  add("assembly", name === "assembly-fraction" ? 10.5 : 10);
  add("consultancy", 3);
  add("components", 1.5);
}

const day = (iso) => new RealDate(iso);
const stat = (name, kind, patch = {}) => {
  const hours = { numberOfHoursUsed: 0, budgetedHours: kind === "dev" ? 20.5 : kind === "mach" ? 15 : 10, deviation: kind === "dev" ? 20.5 : kind === "mach" ? 15 : 10 };
  if (kind === "dev") return { ...hours, _id: oid(IDS[`dev-${name}`]), ...patch };
  if (kind === "mach") {
    return {
      ...hours,
      verificationAndShapeControl: "n/a",
      dimensionalVerificationAndControl: "n/a",
      analysisOfTheDegreeOfFinish: "n/a",
      _id: oid(IDS[`mach-${name}`]),
      ...patch,
    };
  }
  return {
    ...hours,
    pneumaticCircuits: "n/a",
    electricalBoardsAndCircuits: "n/a",
    kinematics: "n/a",
    programmingFeaturesPLC: "n/a",
    operator_equipmentSafetyConditions: "n/a",
    visualInspection: "n/a",
    validTryOnFirstTest: "1",
    _id: oid(IDS[`asm-${name}`]),
    ...patch,
  };
};

/** A job as job create stores it (field order of the insert), then the fields later saves add. */
function jobShape(name, { state, dueDate, priority = "Média", patch = {}, omit = [], after = {} }) {
  const doc = {
    _id: oid(IDS[`job-${name}`]),
    invoicedPercent: 0,
    finalPrice: 15000.5,
    state,
    orderIdentification: "ENC-2030/001",
    isOfficialOrderIdentification: false,
    components: 0,
    automation: 0,
    materials: 0,
    others: 0,
    costNonQuality: 0,
    commentType: [],
    budget: oid(IDS[`budget-${name}`]),
    createdBy: "Ana Administradora",
    dueDate: day(dueDate),
    startDate: day("2026-02-02T08:00:00.000Z"),
    devStatistic: stat(name, "dev"),
    machiningStatistic: stat(name, "mach"),
    assemblyStatistic: stat(name, "asm"),
    priority,
    __v: 0,
    ...patch,
  };
  for (const key of omit) delete doc[key];
  return { ...doc, ...after };
}

const dates = (name, fields) => ({ _id: oid(IDS[`dates-${name}`]), ...fields });
const T = (iso) => day(iso);

const jobShapes = [
  jobShape("dev", { state: "Desenvolvimento", dueDate: "2030-02-10T00:00:00.000Z" }),
  jobShape("onapproval", {
    state: "Em Aprovação",
    dueDate: "2030-01-20T00:00:00.000Z",
    priority: "Alta",
    patch: { components: 120.5, automation: 40, commentType: ["Detalhes de fabrico"], devStatistic: stat("onapproval", "dev", { numberOfHoursUsed: 18, deviation: 2.5 }) },
    after: { comments: "Desenvolvimento concluído.", jobStatisticsDates: dates("onapproval", { devEndedInDate: T("2026-03-01T10:00:00.000Z") }) },
  }),
  jobShape("approved", {
    state: "Aprovado",
    dueDate: "2030-03-05T00:00:00.000Z",
    priority: "Baixa",
    after: { comments: "", jobStatisticsDates: dates("approved", { devEndedInDate: T("2026-03-01T10:00:00.000Z"), onApprovalEndedInDate: T("2026-03-05T10:00:00.000Z") }) },
  }),
  jobShape("machining", {
    state: "Maquinação",
    dueDate: "2030-01-25T00:00:00.000Z",
    patch: { machiningStatistic: stat("machining", "mach", { dimensionalVerificationAndControl: "Nok" }) },
    after: { jobStatisticsDates: dates("machining", { devEndedInDate: T("2026-03-01T10:00:00.000Z"), onApprovalEndedInDate: T("2026-03-02T10:00:00.000Z"), approvedEndedInDate: T("2026-03-03T10:00:00.000Z") }) },
  }),
  jobShape("assembly", {
    state: "Montagem",
    dueDate: "2030-01-05T00:00:00.000Z",
    priority: "Alta",
    patch: { assemblyStatistic: stat("assembly", "asm", { validTryOnFirstTest: "2" }) },
    after: { deliveryDate: null, comments: "Aguardar peças." },
  }),
  jobShape("invoicing", {
    state: "Para faturação",
    dueDate: "2029-12-20T00:00:00.000Z",
    priority: "Baixa",
    patch: { invoicedPercent: 60, isOfficialOrderIdentification: true },
    after: {
      jobStatisticsDates: dates("invoicing", {
        devEndedInDate: T("2026-03-01T10:00:00.000Z"), onApprovalEndedInDate: T("2026-03-02T10:00:00.000Z"), approvedEndedInDate: T("2026-03-03T10:00:00.000Z"),
        machiningEndedInDate: T("2026-03-04T10:00:00.000Z"), assemblyEndedInDate: T("2026-03-05T10:00:00.000Z"),
      }),
      deliveryDate: T("2026-03-05T00:00:00.000Z"),
      comments: "Entregue.",
    },
  }),
  jobShape("billed", {
    state: "Faturada",
    dueDate: "2029-11-30T00:00:00.000Z",
    patch: { invoicedPercent: 100 },
    after: { comments: "", deliveryDate: T("2026-02-20T00:00:00.000Z"), invoicedBy: "Ana Administradora" },
  }),
  jobShape("deleted", { state: "Apagada", dueDate: "2029-12-01T00:00:00.000Z", patch: { invoicedPercent: 20 } }),
  // an imported old job: no commentType/comments/createdBy/jobStatisticsDates/priority, isHightPriority (unknown),
  // costNonQuality and deliveryDate stored null, a Double price, no validatedBy
  jobShape("old", {
    state: "Maquinação",
    dueDate: "2029-10-01T00:00:00.000Z",
    patch: { finalPrice: 1234.5, costNonQuality: null },
    omit: ["commentType", "createdBy", "priority"],
    after: { deliveryDate: null, isHightPriority: true },
  }),
  jobShape("no-stats", { state: "Desenvolvimento", dueDate: "2030-04-01T00:00:00.000Z", omit: ["devStatistic", "machiningStatistic", "assemblyStatistic"] }),
  jobShape("dates-null", {
    state: "Aprovado",
    dueDate: "2030-04-10T00:00:00.000Z",
    priority: "Alta",
    after: { jobStatisticsDates: dates("dates-null", { devEndedInDate: null }) },
  }),
  jobShape("no-budget", { state: "Desenvolvimento", dueDate: "2030-05-01T00:00:00.000Z", patch: { budget: oid(IDS["missing-budget"]) } }),
  jobShape("machining-no-stat", { state: "Maquinação", dueDate: "2030-05-10T00:00:00.000Z", omit: ["machiningStatistic"] }),
  jobShape("assembly-fraction", {
    state: "Montagem",
    dueDate: "2030-05-20T00:00:00.000Z",
    patch: { assemblyStatistic: stat("assembly-fraction", "asm", { budgetedHours: 10.5, deviation: 10.5 }) },
  }),
  jobShape("invoicing-full", { state: "Para faturação", dueDate: "2030-06-01T00:00:00.000Z", patch: { invoicedPercent: 100 } }),
];

// ---------------------------------------------------------------- in-memory store behind the repositories
let jobs;
let effects;

function resetStore() {
  jobs = jobShapes.map((d) => _.cloneDeep(d));
  effects = [];
}

/** Mongoose's cast of an _id filter value (js-bson ObjectId), else a CastError like findById throws. */
function castId(value, model = "Job") {
  if (value === undefined || value === null) return null; // findById(undefined) → findOne({ _id: null })
  try {
    return new mongoose.Types.ObjectId(value);
  } catch (e) {
    const err = new Error(`Cast to ObjectId failed for value "${value}" (type ${typeof value}) at path "_id" for model "${model}"`);
    err.name = "CastError";
    throw err;
  }
}

const sameId = (a, b) => a && b && a.toHexString() === b.toHexString();

function populatedBudget(budgetId, withAuthor) {
  const stored = budgetShapes.find((b) => sameId(b._id, budgetId));
  if (!stored) return null;
  const budget = Budget.hydrate(_.cloneDeep(stored));
  budget.client = sameId(stored.client, entityDoc._id) ? Entity.hydrate(_.cloneDeep(entityDoc)) : null;
  if (withAuthor) budget.madeBy = sameId(stored.madeBy, userDoc._id) ? User.hydrate(_.cloneDeep(userDoc)) : null;
  budget.$__reset();
  return budget;
}

/** A stored job as Mongoose hydrates it, with `budget` populated as the repository asks. */
function hydrated(stored, populate) {
  const job = Job.hydrate(_.cloneDeep(stored));
  if (populate !== "none") {
    const budget = populate === "budget" ? budgetOnly(stored.budget) : populatedBudget(stored.budget, populate === "client+author");
    job.budget = budget; // what populate() sets (null for a missing document)
    job.$__reset();
  }
  return job;
}

function budgetOnly(budgetId) {
  const stored = budgetShapes.find((b) => sameId(b._id, budgetId));
  return stored ? Budget.hydrate(_.cloneDeep(stored)) : null;
}

/** sort({ dueDate: 1 }): MongoDB order (missing/null first), ties keep the natural order. */
function byDueDate(list) {
  return list
    .map((d, i) => ({ d, i }))
    .sort((a, b) => {
      const x = a.d.dueDate ? a.d.dueDate.getTime() : -Infinity;
      const y = b.d.dueDate ? b.d.dueDate.getTime() : -Infinity;
      return x === y ? a.i - b.i : x - y;
    })
    .map((e) => e.d);
}

// The queries of repositories/jobRepository.js; its orderArray (not exported) runs on top by the original
// functions, which we call through a Mongoose-query stand-in: Job.find/findById/findOne/countDocuments.
function query(results) {
  const q = {
    _list: results,
    _populate: "none",
    _sort: false,
    sort() { q._sort = true; return q; },
    limit() { return q; },
    populate(spec) {
      const inner = spec && spec.populate;
      if (!inner) q._populate = "budget";
      else if (Array.isArray(inner)) q._populate = "client+author";
      else q._populate = "client";
      return q;
    },
    countDocuments() { return Promise.resolve(q._list.length); },
    then(resolve, reject) {
      try {
        const list = q._sort ? byDueDate(q._list) : q._list;
        resolve(list.map((d) => hydrated(d, q._populate)));
      } catch (e) {
        reject(e);
      }
    },
  };
  return q;
}

function matches(doc, filter) {
  for (const [key, cond] of Object.entries(filter)) {
    const value = key.split(".").reduce((v, k) => (v == null ? undefined : v[k]), doc);
    if (cond && typeof cond === "object" && !(cond instanceof mongoose.Types.ObjectId)) {
      if ("$nin" in cond && cond.$nin.includes(value)) return false;
      if ("$in" in cond && !cond.$in.includes(value)) return false;
      if ("$eq" in cond && value !== cond.$eq) return false;
      if ("_id" in cond && !sameId(value, castId(cond._id))) return false;
    } else if (value !== cond) return false;
  }
  return true;
}

Job.find = (filter = {}) => query(jobs.filter((d) => matches(d, filter)));
Job.findById = (id) => {
  const _id = castId(id);
  const q = query(jobs.filter((d) => _id && sameId(d._id, _id)));
  const then = q.then;
  q.then = (resolve, reject) => then((list) => resolve(list[0] || null), reject);
  return q;
};
Job.findOne = (filter) => {
  const q = query(jobs.filter((d) => matches(d, filter)).slice(0, 1));
  const then = q.then;
  q.then = (resolve, reject) => then((list) => resolve(list[0] || null), reject);
  return q;
};

TaskRepository.findByBudgetId = async (budgetId) => {
  const _id = castId(budgetId, "Task");
  return taskShapes.filter((t) => sameId(t.budgetId, _id)).map((t) => Task.hydrate(_.cloneDeep(t)));
};
TaskRepository.getByBudgetIdAndTaskType = async (budgetId, taskType) => {
  const _id = castId(budgetId, "Task");
  return taskShapes.filter((t) => sameId(t.budgetId, _id) && t.taskType === taskType).map((t) => Task.hydrate(_.cloneDeep(t)));
};

// save(): Mongoose validates, then sends $__delta (with the version $inc); the document's __v follows the $inc.
mongoose.Model.prototype.save = async function save() {
  await this.validate();
  if (this.constructor.modelName !== "Job" || this.isNew) throw new Error("unexpected save of " + this.constructor.modelName);
  const delta = this.$__delta();
  if (delta) {
    const update = delta[1];
    const recorded = {};
    for (const op of Object.keys(update).sort()) recorded[op] = bsonValue(update[op]);
    effects.push({ update: "jobs", _id: bsonValue(this._id), update: recorded });
    if (update.$inc && update.$inc.__v) this.$__setValue("__v", (this.$__getValue("__v") || 0) + update.$inc.__v);
  }
  this.$__.version = undefined;
  this.$__reset();
  return this;
};

// ---------------------------------------------------------------- run the legacy route handlers
function handlerOf(routePath, method) {
  const layer = jobRouter.stack.find((l) => l.route && l.route.path === routePath && l.route.methods[method]);
  if (!layer) throw new Error(`no ${method} ${routePath}`);
  return layer.route.stack[0].handle;
}

async function run({ method, route, params = {}, body, d1Fixed = false }) {
  resetStore();
  const res = {
    statusCode: 200,
    status(code) { this.statusCode = code; return this; },
    send(value) { this.sent = value; this.called = true; return this; },
  };
  const boundParams = JSON.parse(bind(j(params)));
  if (d1Fixed) Object.defineProperty(boundParams, "id", { value: boundParams.jobId, enumerable: false }); // not seen by Joi
  const req = {
    params: boundParams,
    body: body === undefined ? {} : JSON.parse(bind(body)),
    query: {},
    user: { _id: IDS.user, name: "Ana Administradora" },
  };
  let outcome;
  try {
    await handlerOf(route, method)(req, res, () => {});
    if (res.sent === null || res.sent === undefined) outcome = { status: res.statusCode, empty: true };
    else if (typeof res.sent === "string") outcome = { status: res.statusCode, text: res.sent };
    else outcome = { status: res.statusCode, json: answer(res.sent) };
  } catch (e) {
    // express-async-errors: next(err) → Express's default error handler (500, the D16 page)
    outcome = { status: 500, error: `${e.name}: ${e.message}` };
  }
  return { ...outcome, effects };
}

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
  const golden = {
    generatedBy: "tools/legacy/job-board-golden.js",
    versions,
    clock: new RealDate(NOW).toISOString() + " (TZ=UTC)",
    requestUser: { name: "Ana Administradora" },
    ids: Object.fromEntries([...KNOWN_IDS].map(([hex, name]) => [name, hex])),
    jobs: jobShapes.map(bsonValue),
    budgets: budgetShapes.map(bsonValue),
    tasks: taskShapes.map(bsonValue),
    entity: bsonValue(entityDoc),
    user: bsonValue(userDoc),
    cases: [],
  };
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
