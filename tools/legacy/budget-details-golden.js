#!/usr/bin/env node
// Phase 3 step 2 (PLAN.md §6, §7.1, §10): golden file for the budget details contract: the task routes
// (routes/task.js), PUT /api/budget/patchProperty/:budgetId and PUT /api/budget/import/:budgetId (routes/budget.js),
// derived FROM THE LEGACY CODE by running the route handlers themselves.
// Nothing is started: no database, no network, no legacy server, no SSH (ssh2-promise is replaced by a recorder
// before any legacy module loads, so the NAS is never contacted; none of these routes uses it).
//
//   node tools/legacy/budget-details-golden.js [outDir]      (default tests/fixtures/legacy/budget-details)
//
// Runs, from ../back-end (DIB4T_LEGACY_ROOT), unchanged:
//   - the Express handlers of routes/task.js (all six) and of PUT /patchProperty/:budgetId and PUT /import/:budgetId
//     of routes/budget.js, verbatim, with a recording `res` and express-async-errors' behavior (a thrown error is a
//     500 by Express);
//   - validations/taskValidation.js and validations/budgetValidation.js (Joi 14.3.1, joi-objectid) through them;
//   - models/budget.js (patch, setIncomplete) and models/task.js (update) with Mongoose 5.13's casts, `validate()`
//     and `$__delta()` (what `save()` would send);
//   - repositories/taskRepository.js createDefaultTask (prices stubbed).
// The repositories' MongoDB queries are replaced by an in-memory store (findById casts the id like Mongoose:
// js-bson ObjectId, else a CastError). The intended D8 schema (POST /api/task with budgetId) is run on the same
// bodies (legacy: always "budgetId" is not allowed).
// A fixed clock (2026-03-15T10:20:30.123Z, TZ=UTC like the legacy production container) makes it deterministic.
//
// Output: <outDir>/budget-details-golden.json (README.md is hand-written). Two runs give identical files.

"use strict";

process.env.TZ = "UTC";

const path = require("path");
const fs = require("fs");
const Module = require("module");

const REPO_ROOT = path.resolve(__dirname, "../..");
const BACKEND = path.resolve(process.env.DIB4T_LEGACY_ROOT || path.join(REPO_ROOT, ".."), "back-end");
const OUT_DIR = path.resolve(process.argv[2] || path.join(REPO_ROOT, "tests/fixtures/legacy/budget-details"));

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

let pricesAnswer = { automation: 32.5, consultancy: 10, development: 10, margin: 35, machining: 10, assembly: 10 };
stubModule("./pricesRepository", path.join(BACKEND, "repositories/taskRepository.js"), {
  findAny: async () => pricesAnswer,
});

const legacyRequire = Module.createRequire(path.join(BACKEND, "index.js"));
const _ = legacyRequire("lodash");
const mongoose = legacyRequire("mongoose");
const { Budget } = legacyRequire("./models/budget");
const { Task } = legacyRequire("./models/task");
const { Entity } = legacyRequire("./models/entity");
const { User } = legacyRequire("./models/user");
const BudgetRepository = legacyRequire("./repositories/budgetRepository");
const TaskRepository = legacyRequire("./repositories/taskRepository");
const taskValidations = legacyRequire("./validations/taskValidation");
const Joi = legacyRequire("joi");
Joi.ObjectId = legacyRequire("joi-objectid")(Joi);
const budgetRouter = legacyRequire("./routes/budget");
const taskRouter = legacyRequire("./routes/task");
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
const BUDGET_ID = "5f1e9a2b3c4d5e6f70810001";
const SOURCE_ID = "5f1e9a2b3c4d5e6f70810010";
const SOURCE_R1_ID = "5f1e9a2b3c4d5e6f70810011";
const BARE_ID = "5f1e9a2b3c4d5e6f70810012";
const JOB_BUDGET_ID = "5f1e9a2b3c4d5e6f70810013";
const IMPORTED_ID = "5f1e9a2b3c4d5e6f70810014";
const CLIENT_ID = "5f1e9a2b3c4d5e6f70820001";
const USER_ID = "5f1e9a2b3c4d5e6f70830001";
const MISSING_ID = "5f1e9a2b3c4d5e6f708100ff";
const TASK_ID = "5f1e9a2b3c4d5e6f70840001";
const TASK_COPY_ID = "5f1e9a2b3c4d5e6f70840002";
const TASK_NO_DESC_ID = "5f1e9a2b3c4d5e6f70840003";
const TASK_JOB_ID = "5f1e9a2b3c4d5e6f70840004";
const MISSING_TASK_ID = "5f1e9a2b3c4d5e6f708400ff";
const KNOWN_IDS = new Map([
  [BUDGET_ID, "{{budget}}"],
  [SOURCE_ID, "{{source}}"],
  [SOURCE_R1_ID, "{{source-r1}}"],
  [BARE_ID, "{{bare}}"],
  [JOB_BUDGET_ID, "{{job-budget}}"],
  [IMPORTED_ID, "{{imported}}"],
  [CLIENT_ID, "{{client}}"],
  [USER_ID, "{{user}}"],
  [MISSING_ID, "{{missing}}"],
  [TASK_ID, "{{task}}"],
  [TASK_COPY_ID, "{{task-copy}}"],
  [TASK_NO_DESC_ID, "{{task-no-description}}"],
  [TASK_JOB_ID, "{{task-job}}"],
  [MISSING_TASK_ID, "{{missing-task}}"],
  ["5f1e9a2b3c4d5e6f70840011", "{{source-task-1}}"],
  ["5f1e9a2b3c4d5e6f70840012", "{{source-task-2}}"],
  ["5f1e9a2b3c4d5e6f70840013", "{{source-task-3}}"],
  ["5f1e9a2b3c4d5e6f70840014", "{{source-task-4}}"],
  ["5f1e9a2b3c4d5e6f70840021", "{{budget-task-1}}"],
  ["5f1e9a2b3c4d5e6f70840022", "{{budget-task-2}}"],
]);
const NAMES = new Map([...KNOWN_IDS].map(([hex, name]) => [name, hex]));
const j = (value) => JSON.stringify(value);

/** A JS value as canonical Extended JSON with the js-bson 1.x number rule (what the legacy driver stores). */
function bsonValue(value) {
  if (value === null || value === undefined) return null;
  if (value instanceof mongoose.Types.ObjectId) return { $oid: idText(value.toHexString()) };
  if (value instanceof RealDate) return { $date: value.toISOString() };
  if (Array.isArray(value)) return value.map(bsonValue);
  if (typeof value === "number") {
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
  _id: oid(CLIENT_ID),
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
  _id: oid(USER_ID),
  roles: ["canSeeOrcamentos", null, "canSeeDefinicoes"],
  fieldOfCompetencies: "Desenvolvimento",
  name: "Ana Administradora",
  email: "ana.admin@dib4t.local",
  password: "$2a$10$abcdefghijklmnopqrstuuabcdefghijklmnopqrstuvwxyz01234",
  __v: 0,
};
const storedBudget = {
  _id: oid(BUDGET_ID),
  revision: 0,
  complete: true,
  wasPdfDownloaded: true,
  margin: 35,
  daysNeededToDoTheWork: 60,
  validity: 30,
  hasGenerateJob: false,
  client: oid(CLIENT_ID),
  descriptive: "Máquina de corte",
  personInCharge: "Eng.ª Maria",
  madeBy: oid(USER_ID),
  budgetNumber: "26-0007",
  paymentMethod: "30 dias",
  total: 43.5,
  date: new RealDate("2026-02-01T09:00:00.000Z"),
  __v: 0,
  description: "Fornecimento conforme especificação.",
};
const budgetShapes = {
  [BUDGET_ID]: storedBudget,
  // the number to import from: revision 0 first in natural order, then revision 1
  [SOURCE_ID]: { ...storedBudget, _id: oid(SOURCE_ID), budgetNumber: "26-0003", margin: 32.5, total: 1234, description: "Texto da origem\nsegunda linha", complete: false, wasPdfDownloaded: false },
  [SOURCE_R1_ID]: { ...storedBudget, _id: oid(SOURCE_R1_ID), budgetNumber: "26-0003", revision: 1, margin: 40, total: 999, description: "Texto da R1" },
  // a source without description/total (old document)
  [BARE_ID]: _.omit({ ...storedBudget, _id: oid(BARE_ID), budgetNumber: "26-0002" }, ["description", "total"]),
  [JOB_BUDGET_ID]: { ...storedBudget, _id: oid(JOB_BUDGET_ID), budgetNumber: "26-0004", hasGenerateJob: true },
  // imported: no flags, no description, Double margin
  [IMPORTED_ID]: _.omit({ ...storedBudget, _id: oid(IMPORTED_ID), budgetNumber: "26-0005", margin: 32.5 }, ["complete", "wasPdfDownloaded", "description"]),
};
const budgetOrder = [BARE_ID, SOURCE_ID, BUDGET_ID, SOURCE_R1_ID, JOB_BUDGET_ID, IMPORTED_ID]; // natural order

const taskShapes = [
  { _id: oid(TASK_ID), budgetId: oid(BUDGET_ID), taskType: "automation", description: "", unitPrice: 32.5, quantity: 1, __v: 0 },
  { _id: oid(TASK_COPY_ID), taskType: "development", budgetId: oid(BUDGET_ID), description: "Projeto  ", unitPrice: 30, quantity: 0.5, __v: 0 },
  { _id: oid(TASK_NO_DESC_ID), budgetId: oid(IMPORTED_ID), taskType: "machining", unitPrice: 38.5, quantity: 15, __v: 0 },
  { _id: oid(TASK_JOB_ID), budgetId: oid(JOB_BUDGET_ID), taskType: "assembly", description: "x", unitPrice: 28, quantity: 2, __v: 0 },
  { _id: oid("5f1e9a2b3c4d5e6f70840011"), budgetId: oid(SOURCE_ID), taskType: "automation", description: "", unitPrice: 32.5, quantity: 1, __v: 0 },
  { _id: oid("5f1e9a2b3c4d5e6f70840012"), taskType: "development", budgetId: oid(SOURCE_ID), description: "Projeto", unitPrice: 30, quantity: 0.5, __v: 0 },
  { _id: oid("5f1e9a2b3c4d5e6f70840013"), budgetId: oid(SOURCE_ID), taskType: "machining", unitPrice: 38.5, quantity: 15, __v: 0 },
  { _id: oid("5f1e9a2b3c4d5e6f70840014"), budgetId: oid(SOURCE_ID), taskType: "others", description: "Cáfé  ", unitPrice: 1e21, quantity: -2 },
  { _id: oid("5f1e9a2b3c4d5e6f70840021"), budgetId: oid(BUDGET_ID), taskType: "components", description: "Peças", unitPrice: 100, quantity: 3, __v: 0 },
];

// ---------------------------------------------------------------- in-memory store behind the repositories
let budgets;
let tasks;
let effects;

function resetStore() {
  budgets = budgetOrder.map((id) => _.cloneDeep(budgetShapes[id]));
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
  budget.client = Entity.hydrate(_.cloneDeep(entityDoc));
  budget.madeBy = User.hydrate(_.cloneDeep(userDoc));
  budget.$__reset(); // populated on read: the assignments above are not modifications
  return budget;
}

BudgetRepository.findById = async (id) => {
  const _id = castId(id);
  const stored = budgets.find((b) => sameId(b._id, _id));
  return stored ? populatedBudget(stored) : null;
};
BudgetRepository.getBudgetByNumber = async (budgetNumber) => {
  // findOne({ budgetNumber }) without a sort: the first in natural order. Mongoose casts the filter to String.
  const stored = budgets.find((b) => b.budgetNumber === String(budgetNumber));
  return stored ? Budget.hydrate(_.cloneDeep(stored)) : null;
};
TaskRepository.findById = async (id) => {
  const _id = castId(id);
  const stored = tasks.find((t) => sameId(t._id, _id));
  return stored ? Task.hydrate(_.cloneDeep(stored)) : null;
};
TaskRepository.findByBudgetId = async (budgetId) => {
  const _id = castId(budgetId);
  return tasks.filter((t) => sameId(t.budgetId, _id)).map((t) => Task.hydrate(_.cloneDeep(t)));
};
TaskRepository.deleteTask = async (id) => {
  const _id = castId(id);
  const index = tasks.findIndex((t) => sameId(t._id, _id));
  if (index < 0) {
    effects.push({ deleteOne: { _id: bsonValue(_id) }, deletedCount: 0 });
    return null;
  }
  const [deleted] = tasks.splice(index, 1);
  effects.push({ deleteOne: { _id: bsonValue(_id) }, deletedCount: 1 });
  return Task.hydrate(deleted);
};
TaskRepository.deleteByBudgetId = async (budgetId) => {
  const _id = castId(budgetId);
  const before = tasks.length;
  tasks = tasks.filter((t) => !sameId(t.budgetId, _id));
  effects.push({ deleteMany: { budgetId: bsonValue(_id) }, deletedCount: before - tasks.length });
  return { deletedCount: before - tasks.length };
};
TaskRepository.insertMany = async (docs) => {
  for (const d of docs) {
    await d.validate();
    d._id = oid("5f1e9a2b3c4d5e6f70840099");
  }
  effects.push({ insertMany: docs.map(storedDocument) });
  return docs;
};

// save(): Mongoose validates, then inserts (new) or sends $__delta (existing). Applied to the store as Mongoose
// would leave the document (toObject), so later steps of a case read the saved values.
mongoose.Model.prototype.save = async function save() {
  await this.validate();
  const collection = this.constructor.modelName === "Task" ? "tasks" : "budgets";
  if (this.isNew) {
    if (!this._id || !KNOWN_IDS.has(this._id.toHexString())) this._id = oid("5f1e9a2b3c4d5e6f70840098");
    if (this.get("__v") === undefined) this.set("__v", 0); // the version key a real insert sets
    effects.push({ insert: collection, document: storedDocument(this) });
    tasks.push(this.toObject({ depopulate: true, versionKey: true, transform: false }));
  } else {
    const update = saveDelta(this);
    effects.push({ update: collection, _id: bsonValue(this._id), update });
    const store = collection === "tasks" ? tasks : budgets;
    const index = store.findIndex((d) => sameId(d._id, this._id));
    if (index >= 0) store[index] = this.toObject({ depopulate: true, versionKey: true, transform: false });
  }
  this.isNew = false;
  this.$__reset();
  return this;
};

// ---------------------------------------------------------------- run a legacy route handler
function handlerOf(router, method, routePath) {
  const layer = router.stack.find((l) => l.route && l.route.path === routePath && l.route.methods[method]);
  if (!layer) throw new Error(`no ${method} ${routePath}`);
  return layer.route.stack[0].handle;
}

async function run(router, method, routePath, params, bodyText) {
  resetStore();
  const res = {
    statusCode: 200,
    status(code) { this.statusCode = code; return this; },
    send(value) { this.sent = value; return this; },
  };
  const req = { params: _.mapValues(params, bind), body: bodyText === undefined ? {} : JSON.parse(bind(bodyText)), query: {} };
  let outcome;
  try {
    await handlerOf(router, method, routePath)(req, res, () => {});
    outcome = typeof res.sent === "string"
      ? { status: res.statusCode, text: res.sent }
      : { status: res.statusCode, json: answer(res.sent) };
  } catch (e) {
    // express-async-errors: next(err) → Express's default error handler (500, the D16 page)
    outcome = { status: 500, error: `${e.name}: ${e.message}` };
  }
  return { ...outcome, effects };
}

// ---------------------------------------------------------------- task routes
const taskBody = { taskType: "development", description: "Projeto mecânico", unitPrice: 30, quantity: 20 };
const withTask = (patch) => j({ ...taskBody, ...patch });
const withoutTask = (key) => j(_.omit(taskBody, key));

const taskBodies = [
  j(taskBody),
  withTask({ taskType: "automation", description: "", unitPrice: 32.5, quantity: 1 }), // equal to the stored task
  withTask({ unitPrice: "30", quantity: "20" }), // the UI sends the input's text after a change
  withTask({ unitPrice: " 12.50 ", quantity: "1.5" }),
  withTask({ unitPrice: 0, quantity: 0 }),
  withTask({ unitPrice: 1250.75, quantity: 1.5 }),
  withTask({ unitPrice: 1e21, quantity: -0 }),
  withTask({ description: "Máquina  " }), // decomposed á, trailing spaces
  withTask({ description: "linha 1\nlinha 2" }),
  withoutTask("description"),
  // validation (first error wins, schema order: taskType, description, unitPrice, quantity, then unknown keys)
  j({}),
  withoutTask("taskType"),
  withoutTask("unitPrice"),
  withoutTask("quantity"),
  withTask({ taskType: "painting" }),
  withTask({ taskType: "Automation" }),
  withTask({ taskType: "" }),
  withTask({ taskType: null }),
  withTask({ taskType: 1 }),
  withTask({ description: null }),
  withTask({ description: 5 }),
  withTask({ unitPrice: "" }),
  withTask({ unitPrice: "abc" }),
  withTask({ unitPrice: "12abc" }),
  withTask({ unitPrice: null }),
  withTask({ unitPrice: -1 }),
  withTask({ unitPrice: -0.01 }),
  withTask({ quantity: -1 }),
  withTask({ quantity: true }),
  withTask({ quantity: [1] }),
  withTask({ unitPrice: "0x10" }),
  withTask({ unitPrice: "1e3" }),
  withTask({ unitPrice: 1e400 }),
  withTask({ unitPrice: 9007199254740993 }),
  withTask({ total: 600 }),
  withTask({ _id: TASK_ID }),
  withTask({ budgetId: BUDGET_ID }),
  j({ taskType: "painting", unitPrice: -1 }),
  "[]",
];

const taskUpdateCases = [
  ...taskBodies.map((body) => ({ task: "{{task}}", budget: "{{budget}}", body })),
  // stored shapes
  { task: "{{task-copy}}", budget: "{{budget}}", body: withTask({ taskType: "development", description: "Projeto  ", unitPrice: 30, quantity: 0.5 }) }, // unchanged
  { task: "{{task-copy}}", budget: "{{budget}}", body: withoutTask("description") }, // description removed
  { task: "{{task-no-description}}", budget: "{{imported}}", body: withoutTask("description") }, // stays missing
  { task: "{{task-no-description}}", budget: "{{imported}}", body: withTask({ description: "" }) },
  { task: "{{task-no-description}}", budget: "{{imported}}", body: withTask({ taskType: "machining", unitPrice: "38.5", quantity: "15", description: undefined }) },
  // the task need not belong to the budget (legacy checks neither)
  { task: "{{task}}", budget: "{{imported}}", body: j(taskBody) },
  // ids and lookups, in the route's order: body, ids, task, budget, job
  { task: "abc", budget: "def", body: j(taskBody) },
  { task: "{{task}}", budget: "def", body: j(taskBody) },
  { task: "abc", budget: "{{budget}}", body: withTask({ taskType: "painting" }) },
  { task: "5F1E9A2B3C4D5E6F70840001", budget: "{{budget}}", body: j(taskBody) },
  { task: "abcdefghijkl", budget: "{{budget}}", body: j(taskBody) },
  { task: "{{missing-task}}", budget: "{{budget}}", body: j(taskBody) },
  { task: "{{task}}", budget: "{{missing}}", body: j(taskBody) },
  { task: "{{missing-task}}", budget: "{{missing}}", body: j(taskBody) },
  { task: "{{task-job}}", budget: "{{job-budget}}", body: j(taskBody) },
];

async function runTaskUpdate({ task, budget, body }) {
  return { taskId: task, budgetId: budget, body, ...(await run(taskRouter, "put", "/:taskId/:budgetId", { taskId: task, budgetId: budget }, body)) };
}

const taskDeleteCases = [
  { task: "{{task}}", budget: "{{budget}}" },
  { task: "{{task-no-description}}", budget: "{{imported}}" }, // imported budget: no flags stored
  { task: "{{task}}", budget: "{{imported}}" }, // not this budget's task: legacy deletes it anyway
  { task: "abc", budget: "{{budget}}" },
  { task: "{{task}}", budget: "abc" },
  { task: "{{task}}", budget: "{{missing}}" },
  { task: "{{task-job}}", budget: "{{job-budget}}" },
  { task: "{{missing-task}}", budget: "{{budget}}" }, // the flags are saved first, then 500 "Erro no servidor"
];

async function runTaskDelete({ task, budget }) {
  return { taskId: task, budgetId: budget, ...(await run(taskRouter, "delete", "/:taskId/:budgetId", { taskId: task, budgetId: budget })) };
}

const budgetIdCases = ["{{budget}}", "{{imported}}", "{{job-budget}}", "{{missing}}", "abc", "abcdefghijkl", "5F1E9A2B3C4D5E6F70810001"];

async function runDefaultTask(budgetId) {
  return { budgetId, ...(await run(taskRouter, "post", "/createDefaultTask/:budgetId", { budgetId })) };
}

async function runListTasks(budgetId) {
  return { budgetId, ...(await run(taskRouter, "get", "/getBudgetTasks/:budgetId", { budgetId })) };
}

// POST /api/task: legacy always refuses (D8, "budgetId" is not allowed); the intended schema allows budgetId.
const intendedNewTaskSchema = {
  taskType: Joi.string().valid("automation", "consultancy", "development", "machining", "assembly", "others", "components").required(),
  description: Joi.string().allow(""),
  unitPrice: Joi.number().min(0).required(),
  quantity: Joi.number().min(0).required(),
  budgetId: Joi.ObjectId().required(),
};
const newTaskBodies = [
  j({ budgetId: "{{budget}}", ...taskBody }),
  j({ ...taskBody, budgetId: "{{budget}}" }),
  j({ ...taskBody, budgetId: "{{budget}}", unitPrice: "12", quantity: "2" }),
  j({ ..._.omit(taskBody, "description"), budgetId: "{{budget}}" }),
  j(taskBody),
  j({ ...taskBody, budgetId: "abc" }),
  j({ ...taskBody, budgetId: "{{missing}}" }),
  j({ ...taskBody, budgetId: "{{job-budget}}" }),
  j({ ...taskBody, budgetId: "{{budget}}", taskType: "x" }),
  j({ ...taskBody, budgetId: "{{budget}}", extra: 1 }),
];

async function runNewTask(body) {
  const legacy = await run(taskRouter, "post", "/", {}, body);
  const parsed = JSON.parse(bind(body));
  const message = (() => {
    const { error } = Joi.validate(parsed, intendedNewTaskSchema);
    return error ? error.details[0].message : null;
  })();
  // What the route would store with the intended schema: new Task({ ...req.body }) (Mongoose casts), then the budget's
  // setIncomplete. Only the document shape is taken from here; the order of the checks is the route's.
  let stored = null;
  if (message === null) {
    const task = new Task({ ...parsed });
    task._id = oid("5f1e9a2b3c4d5e6f70840098");
    stored = storedDocument(task);
  }
  return { body, legacy: _.omit(legacy, "effects"), intended: { message, stored } };
}

// ---------------------------------------------------------------- PUT /api/budget/patchProperty/:budgetId
const patchBodies = [
  // accepted
  j({ propertyName: "description", value: "Fornecimento conforme especificação.\nInclui montagem e ensaios." }),
  j({ propertyName: "description", value: "Fornecimento conforme especificação." }), // equal: only the flags
  j({ propertyName: "description", value: " " }),
  j({ propertyName: "description", value: "Máquina" }),
  j({ propertyName: "daysNeededToDoTheWork", value: 45 }),
  j({ propertyName: "daysNeededToDoTheWork", value: 0 }),
  j({ propertyName: "daysNeededToDoTheWork", value: 7.5 }),
  j({ propertyName: "daysNeededToDoTheWork", value: "45" }),
  j({ propertyName: "validity", value: 60 }),
  j({ propertyName: "validity", value: "60" }),
  j({ propertyName: "validity", value: 1 }),
  j({ propertyName: "total", value: 12345.67 }),
  j({ propertyName: "total", value: "999" }),
  j({ propertyName: "total", value: "1234.50" }),
  j({ propertyName: "total", value: " 7 " }),
  j({ propertyName: "total", value: 0 }),
  j({ propertyName: "total", value: 43.5 }),
  j({ propertyName: "total", value: "1e3" }),
  j({ propertyName: "margin", value: 25 }),
  j({ propertyName: "margin", value: "25" }),
  j({ propertyName: "margin", value: 1 }),
  j({ propertyName: "margin", value: 250 }),
  j({ propertyName: "margin", value: 35 }),
  j({ propertyName: "paymentMethod", value: "Transferência a 30 dias" }),
  j({ propertyName: "paymentMethod", value: "30 dias" }),
  j({ propertyName: "complete", value: true }),
  j({ propertyName: "complete", value: false }),
  j({ propertyName: "complete", value: "true" }),
  j({ propertyName: "complete", value: "false" }),
  j({ propertyName: "complete", value: "TRUE" }),
  j({ propertyName: "complete", value: "False" }),
  // refused by Joi: "Não é possível atualizar " + propertyName (JS string concatenation)
  j({}),
  j({ value: 1 }),
  j({ propertyName: "budgetNumber", value: "99-9999" }),
  j({ propertyName: "hasGenerateJob", value: false }),
  j({ propertyName: "Total", value: 1 }),
  j({ propertyName: "", value: 1 }),
  j({ propertyName: null, value: 1 }),
  j({ propertyName: 5, value: 1 }),
  j({ propertyName: ["total"], value: 1 }),
  j({ propertyName: ["a", "b"], value: 1 }),
  j({ propertyName: { a: 1 }, value: 1 }),
  j({ propertyName: true, value: 1 }),
  j({ propertyName: "total", value: 1, extra: 2 }),
  j({ propertyName: "total" }),
  j({ propertyName: "total", value: null }),
  j({ propertyName: "total", value: -1 }),
  j({ propertyName: "total", value: "" }),
  j({ propertyName: "total", value: "abc" }),
  j({ propertyName: "total", value: true }),
  j({ propertyName: "total", value: 1e400 }),
  j({ propertyName: "daysNeededToDoTheWork", value: -0.5 }),
  j({ propertyName: "validity", value: 0 }),
  j({ propertyName: "validity", value: 1.5 }),
  j({ propertyName: "validity", value: "1.5" }),
  j({ propertyName: "margin", value: 0 }),
  j({ propertyName: "margin", value: 0.5 }),
  j({ propertyName: "paymentMethod", value: "" }),
  j({ propertyName: "paymentMethod", value: 5 }),
  j({ propertyName: "paymentMethod", value: null }),
  j({ propertyName: "description", value: "" }),
  j({ propertyName: "description", value: 5 }),
  j({ propertyName: "description" }),
  j({ propertyName: "complete", value: "yes" }),
  j({ propertyName: "complete", value: 1 }),
  j({ propertyName: "complete", value: null }),
  j({ propertyName: "complete", value: "on" }),
  "[]",
];

const patchCases = [
  ...patchBodies.map((body) => ({ budget: "{{budget}}", body })),
  // flags already false: only the property
  { budget: "{{source}}", body: j({ propertyName: "total", value: 999 }) },
  { budget: "{{source}}", body: j({ propertyName: "total", value: 1234 }) }, // nothing changes: no command
  { budget: "{{source}}", body: j({ propertyName: "complete", value: true }) },
  // imported: flags and description missing (defaults on read), Double margin
  { budget: "{{imported}}", body: j({ propertyName: "complete", value: false }) },
  { budget: "{{imported}}", body: j({ propertyName: "complete", value: true }) },
  { budget: "{{imported}}", body: j({ propertyName: "description", value: "Novo texto" }) },
  { budget: "{{imported}}", body: j({ propertyName: "margin", value: "32.5" }) },
  { budget: "{{bare}}", body: j({ propertyName: "total", value: 10 }) },
  // has a job: answered unchanged
  { budget: "{{job-budget}}", body: j({ propertyName: "total", value: 1 }) },
  { budget: "{{job-budget}}", body: j({ propertyName: "budgetNumber", value: 1 }) }, // Joi first
  // lookups
  { budget: "{{missing}}", body: j({ propertyName: "validity", value: 10 }) },
  { budget: "abc", body: j({ propertyName: "validity", value: 10 }) },
  { budget: "abc", body: j({ propertyName: "x", value: 10 }) },
  { budget: "5F1E9A2B3C4D5E6F70810001", body: j({ propertyName: "validity", value: 10 }) },
];

async function runPatch({ budget, body }) {
  return { budgetId: budget, body, ...(await run(budgetRouter, "put", "/patchProperty/:budgetId", { budgetId: budget }, body)) };
}

// ---------------------------------------------------------------- PUT /api/budget/import/:budgetId
const importBody = (patch) => j({ importDescription: true, importTasks: true, budgetNumber: "26-0003", ...patch });
const importCases = [
  { budget: "{{budget}}", body: importBody({}) }, // the first in natural order: revision 0 of 26-0003
  { budget: "{{budget}}", body: importBody({ importDescription: false }) },
  { budget: "{{budget}}", body: importBody({ importTasks: false }) },
  { budget: "{{budget}}", body: importBody({ importTasks: false, importDescription: false }) },
  { budget: "{{budget}}", body: importBody({ budgetNumber: "26-0002" }) }, // a source without description/total
  { budget: "{{budget}}", body: importBody({ budgetNumber: "26-0002", importTasks: false }) },
  { budget: "{{imported}}", body: importBody({}) }, // a target without flags and description
  { budget: "{{budget}}", body: importBody({ budgetNumber: "26-0007" }) }, // its own number (self-import)
  { budget: "{{budget}}", body: importBody({ budgetNumber: "26-0004" }) }, // from a budget with a job: allowed
  // Joi converts "true"/"false" strings; the route then tests the ORIGINAL value's truthiness
  { budget: "{{budget}}", body: importBody({ importTasks: "false", importDescription: "false" }) },
  { budget: "{{budget}}", body: importBody({ importTasks: "TRUE", importDescription: "true" }) },
  // refusals, in the route's order: Joi, number, budget, job
  { budget: "{{budget}}", body: j({ budgetNumber: "26-0003" }) },
  { budget: "{{budget}}", body: j({ importDescription: true, budgetNumber: "26-0003" }) },
  { budget: "{{budget}}", body: importBody({ budgetNumber: undefined }) },
  { budget: "{{budget}}", body: importBody({ budgetNumber: "" }) },
  { budget: "{{budget}}", body: importBody({ budgetNumber: 3 }) },
  { budget: "{{budget}}", body: importBody({ budgetNumber: null }) },
  { budget: "{{budget}}", body: importBody({ importTasks: 1 }) },
  { budget: "{{budget}}", body: importBody({ importTasks: null }) },
  { budget: "{{budget}}", body: importBody({ importTasks: "yes" }) },
  { budget: "{{budget}}", body: importBody({ extra: 1 }) },
  { budget: "{{budget}}", body: importBody({ budgetId: "{{budget}}" }) }, // the legacy modal does not send it
  { budget: "{{budget}}", body: importBody({ budgetNumber: "00-0000" }) },
  { budget: "{{budget}}", body: importBody({ budgetNumber: " 26-0003" }) },
  { budget: "{{missing}}", body: importBody({}) },
  { budget: "abc", body: importBody({}) },
  { budget: "abc", body: importBody({ budgetNumber: "00-0000" }) },
  { budget: "{{job-budget}}", body: importBody({}) },
  { budget: "{{budget}}", body: "[]" },
];

async function runImport({ budget, body }) {
  return { budgetId: budget, body, ...(await run(budgetRouter, "put", "/import/:budgetId", { budgetId: budget }, body)) };
}

// ---------------------------------------------------------------- write
(async () => {
  const golden = {
    generatedBy: "tools/legacy/budget-details-golden.js",
    versions,
    clock: new RealDate(NOW).toISOString() + " (TZ=UTC)",
    prices: pricesAnswer,
    ids: Object.fromEntries([...KNOWN_IDS].map(([hex, name]) => [name, hex])),
    budgets: budgetOrder.map((id) => bsonValue(budgetShapes[id])),
    tasks: taskShapes.map(bsonValue),
    entity: bsonValue(entityDoc),
    user: bsonValue(userDoc),
    taskTypes: await (async () => { const r = await run(taskRouter, "get", "/getTaskTypes", {}); return r.json; })(),
    listTasks: [],
    defaultTask: [],
    newTask: [],
    updateTask: [],
    deleteTask: [],
    patch: [],
    import: [],
    validateIdMessages: [
      { params: { taskId: "abc", budgetId: "def" }, message: taskValidations.validateId({ taskId: "abc", budgetId: "def" }).error.details[0].message },
    ],
  };
  for (const id of budgetIdCases) golden.listTasks.push(await runListTasks(id));
  for (const id of budgetIdCases) golden.defaultTask.push(await runDefaultTask(id));
  for (const body of newTaskBodies) golden.newTask.push(await runNewTask(body));
  for (const c of taskUpdateCases) golden.updateTask.push(await runTaskUpdate(c));
  for (const c of taskDeleteCases) golden.deleteTask.push(await runTaskDelete(c));
  for (const c of patchCases) golden.patch.push(await runPatch(c));
  for (const c of importCases) golden.import.push(await runImport(c));
  if (sshCommands.length) throw new Error("unexpected SSH commands: " + sshCommands.join(" | "));

  fs.mkdirSync(OUT_DIR, { recursive: true });
  const file = path.join(OUT_DIR, "budget-details-golden.json");
  fs.writeFileSync(file, JSON.stringify(golden, null, 2) + "\n");
  console.log(
    `wrote ${path.relative(REPO_ROOT, file)}: listTasks ${golden.listTasks.length}, defaultTask ${golden.defaultTask.length}, ` +
      `newTask ${golden.newTask.length}, updateTask ${golden.updateTask.length}, deleteTask ${golden.deleteTask.length}, ` +
      `patch ${golden.patch.length}, import ${golden.import.length}`,
  );
  process.exit(0);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
