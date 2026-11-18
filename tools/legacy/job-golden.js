#!/usr/bin/env node
// Phase 4 step 1 (PLAN.md §6, §7.3, §10): golden file for POST /api/job (job create) and the label payloads,
// derived FROM THE LEGACY CODE by running the route handler itself.
// Nothing is started: no database, no network, no legacy server, no SSH, no printer (ssh2-promise and telnet-client
// are replaced by recorders before any legacy module loads, so the NAS and the printers are never contacted).
//
//   node tools/legacy/job-golden.js [outDir]      (default tests/fixtures/legacy/jobs)
//
// Runs, from ../back-end (DIB4T_LEGACY_ROOT), unchanged:
//   - the Express handler of POST / of routes/job.js, verbatim, with a recording `res` and express-async-errors'
//     behavior (a thrown error is a 500 by Express), NODE_ENV unset (the production NAS/label part is the subject of
//     tests/fixtures/legacy/ssh, recorded by ssh-golden.js);
//   - validations/jobValidation.js (Joi 14.3.1, joi-objectid) through it;
//   - models/job.js (setNewStatistics) and models/budget.js (createJob) with Mongoose 5.13's casts, `validate()` and
//     `$__delta()` / `toObject()` (what `save()` would send);
//   - models/printerSettings.js print() → getTemplateData for the label payloads (telnet-client recorded).
// The repositories' MongoDB queries are replaced by an in-memory store (findById casts the id like Mongoose and
// populates client and madeBy, null when the referenced document is missing).
// A fixed clock (2026-03-15T10:20:30.123Z, TZ=UTC like the legacy production container) makes it deterministic; the
// label payloads are also written with TZ=Europe/Lisbon (the zone the new app prints in, App:TimeZone).
//
// Output: <outDir>/job-golden.json (README.md is hand-written). Two runs give identical files.

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

const sshCommands = [];
class SshRecorder {
  constructor() { /* credentials ignored on purpose: never read, never recorded */ }
  exec(command) {
    sshCommands.push(command);
    return Promise.resolve("1\n");
  }
}
stubModule("ssh2-promise", path.join(BACKEND, "helpers/pathHlelper.js"), SshRecorder);

let printed = [];
class TelnetRecorder {
  async connect(params) { this.params = params; }
  async send(data) {
    const wire = data + "\n"; // telnet-client 1.4.9: ors "\n" appended, written as UTF-8
    printed.push({
      host: this.params.host,
      port: this.params.port,
      connectTimeoutMs: this.params.timeout,
      negotiationMandatory: this.params.negotiationMandatory,
      send: data,
      wireHex: Buffer.from(wire, "utf8").toString("hex"),
    });
    return "";
  }
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
const { PrinterSettings } = legacyRequire("./models/printerSettings");
const BudgetRepository = legacyRequire("./repositories/budgetRepository");
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
  "telnet-client": legacyRequire("telnet-client/package.json").version,
  node: process.version,
};

const oid = (hex) => new mongoose.Types.ObjectId(hex);
const IDS = {
  budget: "5f1e9a2b3c4d5e6f70810001",
  "budget-r1": "5f1e9a2b3c4d5e6f70810002",
  "other-r0": "5f1e9a2b3c4d5e6f70810003",
  "other-r1": "5f1e9a2b3c4d5e6f70810004",
  "lone-r1": "5f1e9a2b3c4d5e6f70810005",
  imported: "5f1e9a2b3c4d5e6f70810006",
  "no-client": "5f1e9a2b3c4d5e6f70810007",
  "no-author": "5f1e9a2b3c4d5e6f70810008",
  "no-payment": "5f1e9a2b3c4d5e6f70810009",
  "no-tasks": "5f1e9a2b3c4d5e6f7081000a",
  "odd-tasks": "5f1e9a2b3c4d5e6f7081000b",
  "nan-task": "5f1e9a2b3c4d5e6f7081000c",
  "job-itself": "5f1e9a2b3c4d5e6f7081000d",
  "empty-descriptive": "5f1e9a2b3c4d5e6f7081000e",
  client: "5f1e9a2b3c4d5e6f70820001",
  "missing-client": "5f1e9a2b3c4d5e6f708200ff",
  user: "5f1e9a2b3c4d5e6f70830001",
  "missing-user": "5f1e9a2b3c4d5e6f708300ff",
  missing: "5f1e9a2b3c4d5e6f708100ff",
};
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

/** Names in a test text ("{{budget}}") → their hex ids, so paths and bodies are real. */
function bind(text) {
  return text.replace(/\{\{[a-z0-9-]+\}\}/g, (name) => NAMES.get(name) || name);
}

/** The inserted document (Mongoose toObject, _id first as the driver writes it, __v 0 added by the save). */
function storedDocument(doc) {
  const raw = doc.toObject({ depopulate: true, versionKey: true, transform: false });
  const { _id, ...fields } = bsonValue(raw);
  const out = { _id, ...fields };
  if (!("__v" in raw)) out.__v = { $numberInt: "0" };
  return out;
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
  roles: ["canSeeOrcamentos", null, "canSeeDefinicoes"],
  fieldOfCompetencies: "Desenvolvimento",
  name: "Ana Administradora",
  email: "ana.admin@dib4t.local",
  password: "$2a$10$abcdefghijklmnopqrstuuabcdefghijklmnopqrstuvwxyz01234",
  __v: 0,
};
const base = {
  revision: 0,
  complete: true,
  wasPdfDownloaded: true,
  margin: 35,
  daysNeededToDoTheWork: 45,
  validity: 30,
  hasGenerateJob: false,
  client: oid(IDS.client),
  descriptive: "Máquina de corte",
  personInCharge: "Eng.ª Maria",
  madeBy: oid(IDS.user),
  budgetNumber: "26-0007",
  paymentMethod: "30 dias",
  total: 12345.67,
  date: new RealDate("2026-02-01T09:00:00.000Z"),
  __v: 0,
  description: "Fornecimento conforme especificação.",
};
const shape = (name, patch, omit = []) => _.omit({ ...base, _id: oid(IDS[name]), ...patch }, omit);
const budgetShapes = [
  shape("budget", {}),
  shape("budget-r1", { revision: 1, descriptive: "Máquina de corte (rev. 1)", date: new RealDate("2026-03-01T09:00:00.000Z") }),
  shape("other-r0", { budgetNumber: "26-0008" }),
  shape("other-r1", { budgetNumber: "26-0008", revision: 1, hasGenerateJob: true }),
  shape("lone-r1", { budgetNumber: "26-0009", revision: 1 }),
  // imported: no flags, Double margin, revision 0 as a Double
  shape("imported", { budgetNumber: "26-0010", margin: 32.5, revision: 0.0 }, ["complete", "wasPdfDownloaded", "hasGenerateJob", "description"]),
  shape("no-client", { budgetNumber: "26-0011", client: oid(IDS["missing-client"]) }),
  shape("no-author", { budgetNumber: "26-0012", madeBy: oid(IDS["missing-user"]) }),
  shape("no-payment", { budgetNumber: "26-0013" }, ["paymentMethod"]),
  shape("no-tasks", { budgetNumber: "26-0014" }),
  shape("odd-tasks", { budgetNumber: "26-0015" }),
  shape("nan-task", { budgetNumber: "26-0016" }),
  shape("job-itself", { budgetNumber: "26-0017", hasGenerateJob: true }),
  shape("empty-descriptive", { budgetNumber: "26-0018", descriptive: "" }),
];

let nextTask = 1;
const task = (budget, taskType, quantity, patch = {}) => {
  const hex = "5f1e9a2b3c4d5e6f708400" + (nextTask++).toString(16).padStart(2, "0");
  return _.omitBy({ _id: oid(hex), budgetId: oid(IDS[budget]), taskType, description: "", unitPrice: 30, quantity, __v: 0, ...patch }, (v) => v === undefined);
};
const taskShapes = [
  task("budget", "automation", 1),
  task("budget", "development", 20),
  task("budget", "development", 0.5),
  task("budget", "machining", 15),
  task("budget", "assembly", 10),
  task("budget", "consultancy", 3),
  task("budget", "components", 1.5),
  task("budget", "others", 2),
  task("budget-r1", "development", 8),
  task("budget-r1", "assembly", 4),
  task("imported", "machining", 2.25),
  task("odd-tasks", "development", 0.1),
  task("odd-tasks", "development", 0.2),
  task("odd-tasks", "machining", -3),
  task("odd-tasks", "assembly", 1e21),
  task("odd-tasks", "assembly", 0),
  task("nan-task", "development", undefined),
  task("nan-task", "machining", 4),
];

// ---------------------------------------------------------------- in-memory store behind the repositories
let budgets;
let tasks;
let effects;

function resetStore() {
  budgets = budgetShapes.map((b) => _.cloneDeep(b));
  tasks = taskShapes.map((t) => _.cloneDeep(t));
  effects = [];
}

/** Mongoose's cast of an _id filter value (js-bson ObjectId), else a CastError like findById throws. */
function castId(value) {
  try {
    return new mongoose.Types.ObjectId(value);
  } catch (e) {
    const err = new Error(`Cast to ObjectId failed for value "${value}" at path "_id"`);
    err.name = "CastError";
    throw err;
  }
}

const sameId = (a, b) => a && b && a.toHexString() === b.toHexString();

function populatedBudget(stored) {
  const budget = Budget.hydrate(_.cloneDeep(stored));
  budget.client = sameId(stored.client, entityDoc._id) ? Entity.hydrate(_.cloneDeep(entityDoc)) : null;
  budget.madeBy = sameId(stored.madeBy, userDoc._id) ? User.hydrate(_.cloneDeep(userDoc)) : null;
  budget.$__reset(); // populated on read: the assignments above are not modifications
  return budget;
}

BudgetRepository.findById = async (id) => {
  const _id = castId(id);
  const stored = budgets.find((b) => sameId(b._id, _id));
  return stored ? populatedBudget(stored) : null;
};
// findOne({ hasGenerateJob: true, budgetNumber }) / findOne({ budgetNumber, revision: 0 }): the first in natural order
BudgetRepository.getByNumberAndHasJob = async (number) => {
  const stored = budgets.find((b) => b.hasGenerateJob === true && b.budgetNumber === String(number));
  return stored ? Budget.hydrate(_.cloneDeep(stored)) : null;
};
BudgetRepository.getRevisionZero = async (number) => {
  const stored = budgets.find((b) => b.budgetNumber === String(number) && b.revision === 0);
  return stored ? Budget.hydrate(_.cloneDeep(stored)) : null;
};
TaskRepository.findByBudgetId = async (budgetId) => {
  const _id = castId(budgetId);
  return tasks.filter((t) => sameId(t.budgetId, _id)).map((t) => Task.hydrate(_.cloneDeep(t)));
};

// save(): Mongoose validates, then inserts (new) or sends $__delta (existing).
mongoose.Model.prototype.save = async function save() {
  await this.validate();
  const collection = this.constructor.modelName === "Job" ? "jobs" : "budgets";
  if (this.isNew) {
    this._id = oid("5f1e9a2b3c4d5e6f708500aa");
    if (this.get("__v") === undefined) this.set("__v", 0); // the version key a real insert sets
    effects.push({ insert: collection, document: storedDocument(this) });
  } else {
    const update = saveDelta(this);
    effects.push({ update: collection, _id: bsonValue(this._id), update });
    const index = budgets.findIndex((d) => sameId(d._id, this._id));
    if (index >= 0) budgets[index] = this.toObject({ depopulate: true, versionKey: true, transform: false });
  }
  this.isNew = false;
  this.$__reset();
  return this;
};

// ---------------------------------------------------------------- run the legacy route handler
function handlerOf(router, method, routePath) {
  const layer = router.stack.find((l) => l.route && l.route.path === routePath && l.route.methods[method]);
  if (!layer) throw new Error(`no ${method} ${routePath}`);
  return layer.route.stack[0].handle;
}

async function runCreate(bodyText) {
  resetStore();
  const res = {
    statusCode: 200,
    status(code) { this.statusCode = code; return this; },
    send(value) { this.sent = value; return this; },
  };
  const req = { params: {}, body: JSON.parse(bind(bodyText)), query: {}, user: { _id: IDS.user, name: "Ana Administradora" } };
  let outcome;
  try {
    await handlerOf(jobRouter, "post", "/")(req, res, () => {});
    outcome = typeof res.sent === "string"
      ? { status: res.statusCode, text: res.sent }
      : { status: res.statusCode, json: answer(res.sent) };
  } catch (e) {
    // express-async-errors: next(err) → Express's default error handler (500, the D16 page)
    outcome = { status: 500, error: `${e.name}: ${e.message}` };
  }
  return { body: bodyText, ...outcome, effects };
}

// ---------------------------------------------------------------- bodies
const newJob = { budgetId: "{{budget}}", finalPrice: 15000.5, dueDate: "2030-01-15T00:00:00.000Z", state: "Desenvolvimento", orderIdentification: "ENC-2030/001", isOfficialOrderIdentification: true };
const withJob = (patch) => j({ ...newJob, ...patch });
const withoutJob = (key) => j(_.omit(newJob, key));

const bodies = [
  j(newJob),
  // the legacy dialog: the total as a number (or the input's text), the date input's yyyy-MM-dd, "" → Joi refuses
  withJob({ finalPrice: 12345.67, dueDate: "2026-04-29", orderIdentification: "PO-1", isOfficialOrderIdentification: false }),
  withJob({ finalPrice: "12345.67" }),
  withJob({ finalPrice: " 12 " }),
  withJob({ finalPrice: "1e3" }),
  withJob({ finalPrice: 1 }),
  withJob({ finalPrice: 9007199254740993 }),
  // every initial state the dialog offers
  ...["Em Aprovação", "Aprovado", "Maquinação", "Montagem", "Para faturação"].map((state) => withJob({ state })),
  // dueDate: Joi.date() then Mongoose's Date cast of the ORIGINAL value
  withJob({ dueDate: "2030-01-15" }),
  withJob({ dueDate: "2030-01-15T10:00" }),
  withJob({ dueDate: "2030-01-15T10:00:00+01:00" }),
  withJob({ dueDate: "2030-01-15T10:00:00.5Z" }),
  withJob({ dueDate: "2030-01" }),
  withJob({ dueDate: "2030" }),
  withJob({ dueDate: "+002030-01-15" }),
  withJob({ dueDate: 1894665600000 }),
  withJob({ dueDate: "1894665600000" }),
  withJob({ dueDate: 0 }),
  withJob({ dueDate: -1 }),
  withJob({ dueDate: 1.5 }),
  withJob({ dueDate: "2030/01/15" }),
  withJob({ dueDate: "Jan 15 2030" }),
  withJob({ dueDate: "15/01/2030" }),
  withJob({ dueDate: " 2030-01-15 " }),
  withJob({ dueDate: "2030-02-30" }),
  withJob({ dueDate: "2030-13-01" }),
  withJob({ dueDate: "abc" }),
  withJob({ dueDate: "" }),
  withJob({ dueDate: null }),
  withJob({ dueDate: true }),
  withJob({ dueDate: 8.64e15 }),
  withJob({ dueDate: 8.64e15 + 1 }),
  // isOfficialOrderIdentification: Joi.boolean() then Mongoose's Boolean cast
  withJob({ isOfficialOrderIdentification: "true" }),
  withJob({ isOfficialOrderIdentification: "false" }),
  withJob({ isOfficialOrderIdentification: "TRUE" }),
  withJob({ isOfficialOrderIdentification: "False" }),
  withJob({ isOfficialOrderIdentification: "yes" }),
  withJob({ isOfficialOrderIdentification: 1 }),
  withJob({ isOfficialOrderIdentification: null }),
  // orderIdentification
  withJob({ orderIdentification: " " }),
  withJob({ orderIdentification: "Encomenda nº 5  " }),
  withJob({ orderIdentification: "" }),
  withJob({ orderIdentification: 5 }),
  withJob({ orderIdentification: null }),
  // refusals, in schema order (budgetId, finalPrice, dueDate, state, orderIdentification, isOfficial…), then unknown keys
  j({}),
  withoutJob("budgetId"),
  withoutJob("finalPrice"),
  withoutJob("dueDate"),
  withoutJob("state"),
  withoutJob("orderIdentification"),
  withoutJob("isOfficialOrderIdentification"),
  withJob({ budgetId: "abc" }),
  withJob({ budgetId: "5F1E9A2B3C4D5E6F70810001" }),
  withJob({ budgetId: "abcdefghijkl" }),
  withJob({ budgetId: 5 }),
  withJob({ budgetId: null }),
  withJob({ finalPrice: 0.5 }),
  withJob({ finalPrice: 0 }),
  withJob({ finalPrice: -1 }),
  withJob({ finalPrice: "" }),
  withJob({ finalPrice: "abc" }),
  withJob({ finalPrice: null }),
  withJob({ finalPrice: true }),
  withJob({ finalPrice: 1e400 }),
  withJob({ state: "Faturada" }),
  withJob({ state: "Apagada" }),
  withJob({ state: "desenvolvimento" }),
  withJob({ state: "" }),
  withJob({ state: null }),
  withJob({ isOfficialOrderIdentification: "on" }),
  withJob({ extra: 1 }),
  withJob({ priority: "Alta" }),
  withJob({ budget: "{{budget}}" }),
  j({ budgetId: "abc", finalPrice: 0 }),
  "[]",
];

// stored shapes, with the base body
const shapeCases = [
  "budget-r1", // revision 0 of its number exists: the job is made from revision 1
  "other-r0", // another revision of the number has a job
  "job-itself", // this revision has a job
  "lone-r1", // no revision 0 of the number
  "imported", // no flags stored, Double revision
  "no-client", // populate gives null: the budget's save is refused (client required)
  "no-author",
  "no-payment",
  "empty-descriptive",
  "no-tasks",
  "odd-tasks",
  "nan-task",
  "missing",
].map((name) => withJob({ budgetId: `{{${name}}}` }));

// ---------------------------------------------------------------- label payloads (printerSettings.print → getTemplateData)
async function labels() {
  const out = [];
  const samples = [
    { budgetNumber: "26-0007", descriptive: "Máquina de corte", client: "Metalúrgica Exemplo, Lda.", startDate: "2026-03-15T10:20:30.123Z" },
    { budgetNumber: "26-0123", descriptive: "Garra, pneumática", client: "Cortiças Reunidas", startDate: "2026-01-05T00:30:00.000Z" },
    { budgetNumber: "25-1234", descriptive: "Molde \"especial\"  100% ", client: "Pastelaria D'Ouro", startDate: "2026-07-31T23:30:00.000Z" },
    { budgetNumber: "26-0001", descriptive: "Peça $1 `id`", client: "Fundição Norte", startDate: "2026-12-31T23:59:59.999Z" },
    { budgetNumber: "26-0002", descriptive: "linha 1\nlinha 2", client: "Ação & Cª", startDate: "2026-10-25T00:30:00.000Z" },
  ];
  for (const zone of ["UTC", "Europe/Lisbon"]) {
    process.env.TZ = zone;
    for (const s of samples) {
      for (const templateName of ["producao", "maquinacao"]) {
        printed = [];
        const settings = new PrinterSettings({ ip: "192.168.2.32", port: 9100, templateName });
        const budget = { budgetNumber: s.budgetNumber, descriptive: s.descriptive, client: { name: s.client } };
        const job = { startDate: new RealDate(s.startDate) };
        const ok = await settings.print(budget, job);
        out.push({ timeZone: zone, ...s, templateName, returned: ok, sent: printed });
      }
    }
  }
  process.env.TZ = "UTC";
  return out;
}

// ---------------------------------------------------------------- write
(async () => {
  const golden = {
    generatedBy: "tools/legacy/job-golden.js",
    versions,
    clock: new RealDate(NOW).toISOString() + " (TZ=UTC)",
    requestUser: { name: "Ana Administradora" },
    ids: Object.fromEntries([...KNOWN_IDS].map(([hex, name]) => [name, hex])),
    budgets: budgetShapes.map(bsonValue),
    tasks: taskShapes.map(bsonValue),
    entity: bsonValue(entityDoc),
    user: bsonValue(userDoc),
    create: [],
    shapes: [],
    labels: [],
  };
  for (const body of bodies) golden.create.push(await runCreate(body));
  for (const body of shapeCases) golden.shapes.push(await runCreate(body));
  if (sshCommands.length) throw new Error("unexpected SSH commands: " + sshCommands.join(" | "));
  if (printed.length) throw new Error("unexpected labels outside prod");
  golden.labels = await labels();

  fs.mkdirSync(OUT_DIR, { recursive: true });
  const file = path.join(OUT_DIR, "job-golden.json");
  fs.writeFileSync(file, JSON.stringify(golden, null, 2) + "\n");
  console.log(`wrote ${path.relative(REPO_ROOT, file)}: create ${golden.create.length}, shapes ${golden.shapes.length}, labels ${golden.labels.length}`);
  process.exit(0);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
