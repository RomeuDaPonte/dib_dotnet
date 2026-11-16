#!/usr/bin/env node
// Phase 3 step 1 (PLAN.md §6, §7.1, §10): golden file for the budget list/create/clone/revision/edit contract
// (routes/budget.js, repositories/budgetRepository.js, models/budget.js), derived FROM THE LEGACY CODE by running it.
// Nothing is started: no database, no network, no legacy server, no SSH (ssh2-promise is replaced by a recorder
// before any legacy module loads, so the NAS is never contacted).
//
//   node tools/legacy/budget-golden.js [outDir]      (default tests/fixtures/legacy/budgets)
//
// Runs, from ../back-end (DIB4T_LEGACY_ROOT), unchanged:
//   - validations/budgetValidation.js (Joi 14.3.1): validateNewBudget (POST / and POST /clone/:id) on raw JSON
//     bodies; the schema validateUpdateBudget was meant to apply (D7: legacy skips it, see "update") on the same
//     bodies; validateSearch on query strings parsed by the legacy Express's own query parser;
//   - repositories/budgetRepository.js with the Mongoose query methods replaced by in-memory answers (the real
//     repository code runs): getBudgetCountForThisYear (the filter it sends), getBudgetsWithLastRevision (dedupe +
//     row shape), searchBudgets (the pipeline it sends, RegExp source + flags, dedupe + row shape, or the error);
//   - models/budget.js getBudgetNumber for boundary counts; Budget documents built like the routes build them
//     (create, clone, new revision; the object literals of routes/budget.js), stored shape (BSON types from the
//     legacy driver's own js-bson 1.1.6 rule) and answer (toJSON, key order); budget.update(body, madeBy) + the
//     save's $set from $__delta (the SSH commands it would send are recorded); toJSON of stored shapes;
//   - repositories/taskRepository.js createDefaultTask (pricesRepository replaced by an in-memory answer) and the
//     task copies of clone/new revision.
// A fixed clock (2026-03-15T10:20:30.123Z, TZ=UTC like the legacy production container) makes it deterministic.
//
// Output: <outDir>/budget-golden.json (README.md is hand-written). Two runs give identical files.

"use strict";

process.env.TZ = "UTC";

const path = require("path");
const fs = require("fs");
const Module = require("module");

const REPO_ROOT = path.resolve(__dirname, "../..");
const BACKEND = path.resolve(process.env.DIB4T_LEGACY_ROOT || path.join(REPO_ROOT, ".."), "back-end");
const OUT_DIR = path.resolve(process.argv[2] || path.join(REPO_ROOT, "tests/fixtures/legacy/budgets"));

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

let pricesAnswer = null;
stubModule("./pricesRepository", path.join(BACKEND, "repositories/taskRepository.js"), {
  findAny: async () => pricesAnswer,
});

const legacyRequire = Module.createRequire(path.join(BACKEND, "index.js"));
const _ = legacyRequire("lodash");
const mongoose = legacyRequire("mongoose");
const { Budget, getBudgetNumber } = legacyRequire("./models/budget");
const { Task } = legacyRequire("./models/task");
const { Entity } = legacyRequire("./models/entity");
const { User } = legacyRequire("./models/user");
const BudgetRepository = legacyRequire("./repositories/budgetRepository");
const TaskRepository = legacyRequire("./repositories/taskRepository");
const budgetValidations = legacyRequire("./validations/budgetValidation");
const Joi = legacyRequire("joi");
Joi.ObjectId = legacyRequire("joi-objectid")(Joi);
const expressUtils = legacyRequire("express/lib/utils");
const BSON = legacyRequire("bson"); // 1.1.6, the serializer of the legacy driver
const bson = new BSON();

const versions = {
  bson: legacyRequire("bson/package.json").version,
  express: legacyRequire("express/package.json").version,
  joi: legacyRequire("joi/package.json").version,
  "joi-objectid": legacyRequire("joi-objectid/package.json").version,
  mongoose: legacyRequire("mongoose/package.json").version,
  qs: legacyRequire("qs/package.json").version,
  node: process.version,
};

const oid = (hex) => new mongoose.Types.ObjectId(hex);
const BUDGET_ID = "5f1e9a2b3c4d5e6f70810001";
const CLIENT_ID = "5f1e9a2b3c4d5e6f70820001";
const USER_ID = "5f1e9a2b3c4d5e6f70830001";
const USER2_ID = "5f1e9a2b3c4d5e6f70830002";
const KNOWN_IDS = new Map([
  [BUDGET_ID, "{{budget}}"],
  [CLIENT_ID, "{{client}}"],
  [USER_ID, "{{user}}"],
  [USER2_ID, "{{user2}}"],
  ["5f1e9a2b3c4d5e6f70810002", "{{budget-r1}}"],
  ["5f1e9a2b3c4d5e6f70810003", "{{budget-r2}}"],
  ["5f1e9a2b3c4d5e6f70810004", "{{budget-6}}"],
  ["5f1e9a2b3c4d5e6f70810005", "{{budget-5}}"],
]);
const j = (value) => JSON.stringify(value);

function joiMessage(result) {
  return result && result.error ? result.error.details[0].message : null;
}

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

// ---------------------------------------------------------------- stored documents used by several sections
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
const user2Doc = { ...userDoc, _id: oid(USER2_ID), name: "Bruno Silva", email: "bruno@dib4t.local", roles: ["canSeeObras"] };
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
const hydrateEntity = (doc = entityDoc) => Entity.hydrate(_.cloneDeep(doc));
const hydrateUser = (doc = userDoc) => User.hydrate(_.cloneDeep(doc));

/** findById(...).populate(["madeBy", "client"]) as the routes receive it: populated paths, nothing modified. */
function populatedBudget(stored, client = entityDoc, madeBy = userDoc) {
  const budget = Budget.hydrate(_.cloneDeep(_.omitBy(stored, (v) => v === undefined)));
  if (client) budget.client = hydrateEntity(client);
  else if (client === null) budget.client = null; // populate of a reference to a missing document
  if (madeBy) budget.madeBy = hydrateUser(madeBy);
  budget.$__reset(); // populated on read: the assignments above are not modifications
  return budget;
}

// ---------------------------------------------------------------- Joi: create / clone (validateNewBudget)
const okBody = { clientId: CLIENT_ID, madeById: USER_ID, descriptive: "Máquina de corte", personInCharge: "Eng.ª Maria" };
const withBody = (patch) => j({ ...okBody, ...patch });
const withoutBody = (key) => j(_.omit(okBody, key));

const newBudgetBodies = [
  j(okBody),
  withBody({ clientId: CLIENT_ID.toUpperCase() }),
  withBody({ descriptive: "  Espaços  " }),
  withBody({ descriptive: "Suporte 30/40 mm" }),
  withBody({ descriptive: "O'Brien" }),
  withBody({ descriptive: "a".repeat(300) }),
  withBody({ descriptive: "Café Decomposto" }),
  j({ personInCharge: "x", descriptive: "x", madeById: USER_ID, clientId: CLIENT_ID }),
  // presence
  j({}),
  withoutBody("clientId"),
  withoutBody("madeById"),
  withoutBody("descriptive"),
  withoutBody("personInCharge"),
  // ObjectId (joi-objectid: string + /^[0-9a-fA-F]{24}$/)
  withBody({ clientId: "" }),
  withBody({ clientId: "abc" }),
  withBody({ clientId: CLIENT_ID + "0" }),
  withBody({ clientId: "5f1e9a2b3c4d5e6f7082000g" }),
  withBody({ clientId: "abcdefghijkl" }),
  withBody({ clientId: null }),
  withBody({ clientId: 1 }),
  withBody({ clientId: { _id: CLIENT_ID } }),
  withBody({ clientId: [CLIENT_ID] }),
  withBody({ madeById: "x" }),
  withBody({ madeById: null }),
  // strings
  withBody({ descriptive: "" }),
  withBody({ descriptive: " " }),
  withBody({ descriptive: null }),
  withBody({ descriptive: 5 }),
  withBody({ descriptive: true }),
  withBody({ descriptive: ["a"] }),
  withBody({ descriptive: {} }),
  withBody({ personInCharge: "" }),
  withBody({ personInCharge: null }),
  withBody({ personInCharge: 1 }),
  // unknown keys and error order
  withBody({ extra: 1 }),
  j({ extra: 1, ...okBody }),
  withBody({ client: { _id: CLIENT_ID } }),
  withBody({ _id: BUDGET_ID }),
  j({ descriptive: "", clientId: "x" }),
  j({ personInCharge: "", descriptive: "" }),
  "[]",
  '"texto"',
  "null",
  '{"descriptive":"Primeiro","descriptive":"Último","clientId":"' + CLIENT_ID + '","madeById":"' + USER_ID + '","personInCharge":"x"}',
];

// D7: validateUpdateBudget(req) returns the error object of validateBudgetId, so the route's `const { error }` is
// always undefined and nothing is checked. The schema it was meant to apply (its second Joi.validate):
const intendedUpdateSchema = {
  madeById: Joi.ObjectId().required(),
  descriptive: Joi.string().required(),
  personInCharge: Joi.string().required(),
};
const okUpdate = _.omit(okBody, "clientId");
const updateBodies = [
  j(okUpdate),
  j({ ...okUpdate, clientId: CLIENT_ID }), // the legacy edit dialog sends only the three fields (budgetService.updateBudget)
  j(_.omit(okUpdate, "madeById")),
  j(_.omit(okUpdate, "descriptive")),
  j(_.omit(okUpdate, "personInCharge")),
  j({ ...okUpdate, madeById: "x" }),
  j({ ...okUpdate, descriptive: "" }),
  j({ ...okUpdate, descriptive: 5 }),
  j({ ...okUpdate, personInCharge: null }),
  j({ ...okUpdate, descriptive: "Suporte 30/40 mm" }),
  j({}),
  "[]",
];

function runNewBudgetBody(text) {
  return { body: text, message: joiMessage(budgetValidations.validateNewBudget(JSON.parse(text))) };
}

function runUpdateBody(text) {
  const body = JSON.parse(text);
  const legacy = budgetValidations.validateUpdateBudget({ body, params: { budgetId: BUDGET_ID } });
  return {
    body: text,
    legacyError: legacy && legacy.error ? legacy.error.details[0].message : null, // what the route sees: always null
    message: joiMessage(Joi.validate(body, intendedUpdateSchema)),
  };
}

// ---------------------------------------------------------------- search (GET /search?q=)
const parseQuery = expressUtils.compileQueryParser("extended"); // the legacy app's "query parser" setting (default)
const searchQueries = [
  "q=corti",
  "q=CORTI",
  "q=26-0001",
  "q=ab",
  "q=a",
  "q=",
  "",
  "q=%20%20",
  "q=M%C3%A1quina",
  "q=Ma%CC%81quina", // decomposed á
  "q=a.c",
  "q=26-000%5B12%5D", // 26-000[12]
  "q=%28%28", // ((
  "q=%5B", // [
  "q=a%7B2%2C1%7D", // a{2,1}
  "q=%5C", // \
  "q=a%2Fb", // a/b
  "q=a%0Ab", // a\nb
  "q=a%00b", // a\0b
  "q=%5Ck", // \k
  "q=%28%3F%3Cn%3Ea%29", // (?<n>a)
  "q=%28%3F%3C%3Da%29b", // (?<=a)b
  "q=%5Cp%7BL%7D", // \p{L}
  "q=a**",
  "q=ab&q=cd",
  "q[]=ab",
  "q[x]=ab",
  "q=ab&extra=1",
  "Q=ab",
  "q=ab&q=",
];

function runSearch(queryString) {
  const query = parseQuery(queryString);
  const message = joiMessage(budgetValidations.validateSearch(query));
  const result = { query: queryString, parsed: query, message };
  if (message !== null) return result;
  try {
    const regex = new RegExp(query.q, "i");
    bson.serialize({ v: regex }); // the legacy driver refuses some sources (null bytes)
    return { ...result, outcome: "200", source: regex.source, flags: regex.flags };
  } catch (e) {
    return { ...result, outcome: "500", text: "Error performing search: " + e.message };
  }
}

// ---------------------------------------------------------------- numbering
const numberCounts = [0, 1, 8, 9, 10, 98, 99, 100, 998, 999, 1000, 9998, 9999, 10000];

async function runCountForYear() {
  let captured = null;
  const original = Budget.find;
  Budget.find = (filter) => {
    captured = filter;
    return { distinct: async (field) => { captured = { filter, field }; return ["26-0001", "26-0002", "25-0100"]; } };
  };
  try {
    const count = await BudgetRepository.getBudgetCountForThisYear();
    return { filter: bsonValue(captured.filter), distinctField: captured.field, countOfThreeDistinct: count };
  } finally {
    Budget.find = original;
  }
}

// ---------------------------------------------------------------- list (GET /) and search rows
function listBudgets() {
  // find().sort({ budgetNumber: -1, revision: -1 }) answers, populated; the repository dedupes and maps them.
  return [
    populatedBudget({ ...storedBudget, _id: oid("5f1e9a2b3c4d5e6f70810003"), revision: 2, budgetNumber: "26-0007" }),
    populatedBudget({ ...storedBudget, _id: oid("5f1e9a2b3c4d5e6f70810002"), revision: 1, budgetNumber: "26-0007" }),
    populatedBudget(storedBudget),
    populatedBudget({ ...storedBudget, _id: oid("5f1e9a2b3c4d5e6f70810004"), budgetNumber: "26-0006", personInCharge: undefined, total: undefined, description: undefined }),
    populatedBudget({ ...storedBudget, _id: oid("5f1e9a2b3c4d5e6f70810005"), budgetNumber: "26-0005", hasGenerateJob: undefined, daysNeededToDoTheWork: undefined }),
  ];
}

async function runList() {
  let chain = null;
  const original = Budget.find;
  Budget.find = (filter) => {
    chain = { filter };
    const q = {
      sort: (s) => { chain.sort = s; return q; },
      limit: (n) => { chain.limit = n; return q; },
      populate: async (p) => { chain.populate = p; return listBudgets(); },
    };
    return q;
  };
  try {
    const rows = await BudgetRepository.getBudgetsWithLastRevision();
    const orphan = await (async () => {
      Budget.find = () => ({ sort() { return this; }, limit() { return this; }, populate: async () => [populatedBudget(storedBudget, null)] });
      try { await BudgetRepository.getBudgetsWithLastRevision(); return "200"; } catch (e) { return `500 (${e.name}: ${e.message})`; }
    })();
    return { query: { filter: chain.filter || {}, sort: chain.sort, limit: chain.limit, populate: chain.populate }, rows: answer(rows), clientMissing: orphan };
  } finally {
    Budget.find = original;
  }
}

async function runSearchRows() {
  let pipeline = null;
  const original = Budget.aggregate;
  Budget.aggregate = async (p) => {
    pipeline = p;
    // $lookup + $unwind answer plain objects; already sorted by budgetNumber desc, revision desc.
    const plain = (b, extra = {}) => ({ ..._.omit(b, "client", "madeBy"), client: entityDoc, madeBy: userDoc, ...extra });
    return [
      plain(storedBudget, { _id: oid("5f1e9a2b3c4d5e6f70810003"), revision: 2 }),
      plain(storedBudget, { _id: oid("5f1e9a2b3c4d5e6f70810002"), revision: 1 }),
      plain(storedBudget),
      plain(_.omit(storedBudget, ["personInCharge", "total", "hasGenerateJob"]), { _id: oid("5f1e9a2b3c4d5e6f70810004"), budgetNumber: "26-0006" }),
    ];
  };
  try {
    const rows = await BudgetRepository.searchBudgets("corti");
    const serialized = pipeline.map((stage) => JSON.parse(JSON.stringify(stage, (k, v) => (v instanceof RegExp ? { $regex: v.source, $options: v.flags } : v))));
    return { pipeline: serialized, rows: answer(rows) };
  } finally {
    Budget.aggregate = original;
  }
}

// ---------------------------------------------------------------- create (POST /): the route's new Budget({...})
const priceCases = [
  { automation: 10, consultancy: 10, development: 10, margin: 35, machining: 10, assembly: 10 },
  { automation: 32.5, consultancy: 10, development: 10, margin: 35, machining: 10, assembly: 10 },
  { automation: 33.33, consultancy: 10, development: 10, margin: 30, machining: 10, assembly: 10 },
  { automation: 100, consultancy: 10, development: 10, margin: 1, machining: 10, assembly: 10 },
  { automation: 0.5, consultancy: 10, development: 10, margin: 99.5, machining: 10, assembly: 10 },
  { automation: 10, consultancy: 10, development: 10, margin: 35.5, machining: 10, assembly: 10 },
];

function runCreate(prices, index) {
  const entity = hydrateEntity(index === 1 ? _.omit(entityDoc, "paymentCondition") : entityDoc);
  const madeBy = hydrateUser();
  const body = { ...okBody, descriptive: index === 2 ? "  Espaços  " : okBody.descriptive };
  const newBudgetNumber = getBudgetNumber(6);
  // routes/budget.js POST /, verbatim:
  const budget = new Budget({
    client: entity,
    descriptive: body.descriptive,
    personInCharge: body.personInCharge,
    madeBy: madeBy,
    budgetNumber: newBudgetNumber,
    paymentMethod: entity.paymentCondition,
    margin: prices.margin,
    total: Math.trunc((prices.margin * prices.automation) / 100) + prices.automation,
    serverFolderPath: `/share/Orçamentos/${new Date().getFullYear()}/'${entity.name}'/'${newBudgetNumber} - ${body.descriptive}'`,
  });
  budget._id = oid(BUDGET_ID);
  const invalid = budget.validateSync();
  return {
    prices,
    entityHasPaymentCondition: index !== 1,
    descriptive: body.descriptive,
    outcome: invalid ? "500" : "200",
    reason: invalid ? invalid.message : undefined,
    stored: invalid ? undefined : storedDocument(budget),
    response: invalid ? undefined : answer({ ...budget.toJSON(), __v: 0 }),
  };
}

async function runDefaultTask(prices) {
  pricesAnswer = prices;
  const task = await TaskRepository.createDefaultTask(BUDGET_ID);
  task._id = oid("5f1e9a2b3c4d5e6f70840001");
  return { prices, stored: storedDocument(task), response: answer({ ...task.toJSON(), __v: 0 }) };
}

// ---------------------------------------------------------------- clone / new revision: the routes' new Budget({...})
const sourceShapes = [
  { name: "typical", doc: storedBudget },
  { name: "no description, no personInCharge", doc: _.omit(storedBudget, ["description", "personInCharge"]) },
  { name: "no total, Double margin", doc: { ..._.omit(storedBudget, ["total"]), margin: 32.5 } },
  { name: "old document: no hasGenerateJob/validity/days/__v", doc: _.omit(storedBudget, ["hasGenerateJob", "validity", "daysNeededToDoTheWork", "__v"]) },
  { name: "with job, locked", doc: { ...storedBudget, hasGenerateJob: true, revision: 3 } },
  { name: "unknown field", doc: { ...storedBudget, serverFolderPath: "/share/x" } },
];
const sourceTasks = [
  { _id: oid("5f1e9a2b3c4d5e6f70840011"), budgetId: oid(BUDGET_ID), taskType: "automation", description: "", unitPrice: 32.5, quantity: 1, __v: 0 },
  { _id: oid("5f1e9a2b3c4d5e6f70840012"), taskType: "development", budgetId: oid(BUDGET_ID), description: "Projeto", unitPrice: 30, quantity: 0.5, __v: 0 },
  { _id: oid("5f1e9a2b3c4d5e6f70840013"), budgetId: oid(BUDGET_ID), taskType: "machining", unitPrice: 38.5, quantity: 15, __v: 0 }, // no description
  { _id: oid("5f1e9a2b3c4d5e6f70840014"), budgetId: oid(BUDGET_ID), taskType: "others", description: "Cáfé  ", unitPrice: 1e21, quantity: -2 },
];

function copyTasks(newBudgetId) {
  // routes/budget.js clone / newrevision, verbatim:
  const budgetTasks = sourceTasks.map((t) => Task.hydrate(_.cloneDeep(t)));
  let copiedTasks = [];
  budgetTasks.map((t) => {
    copiedTasks.push(
      new Task({
        taskType: t.taskType,
        budgetId: newBudgetId,
        description: t.description,
        unitPrice: t.unitPrice,
        quantity: t.quantity,
      }),
    );
  });
  return copiedTasks.map((t) => { t._id = oid("5f1e9a2b3c4d5e6f70840099"); return storedDocument(t); });
}

function runClone({ name, doc }) {
  const budget = populatedBudget(doc);
  const entity = hydrateEntity({ ...entityDoc, paymentCondition: "Pronto pagamento" });
  const madeBy = hydrateUser(user2Doc);
  const req = { body: { ...okBody, descriptive: "Cópia", personInCharge: "Sr. Paulo" } };
  // routes/budget.js POST /clone/:budgetId, verbatim:
  const newBudget = new Budget({
    client: entity,
    descriptive: req.body.descriptive,
    personInCharge: req.body.personInCharge,
    madeBy: madeBy,
    budgetNumber: getBudgetNumber(6),
    paymentMethod: budget.paymentMethod,
    margin: budget.margin,
    total: budget.total,
    daysNeededToDoTheWork: budget.daysNeededToDoTheWork,
    validity: budget.validity,
    description: budget.description,
  });
  newBudget._id = oid("5f1e9a2b3c4d5e6f70810099");
  return { source: name, stored: storedDocument(newBudget), response: answer({ ...newBudget.toJSON(), __v: 0 }), tasks: copyTasks(newBudget._id) };
}

function runNewRevision({ name, doc }) {
  const budget = populatedBudget(doc);
  const lastRevision = 2; // getRevisionsCount(budgetNumber)
  // routes/budget.js POST /newrevision/:budgetId, verbatim:
  const budgetRevision = new Budget({
    client: budget.client,
    descriptive: budget.descriptive,
    personInCharge: budget.personInCharge,
    madeBy: budget.madeBy,
    budgetNumber: budget.budgetNumber,
    revision: lastRevision,
    paymentMethod: budget.paymentMethod,
    margin: budget.margin,
    total: budget.total,
    daysNeededToDoTheWork: budget.daysNeededToDoTheWork,
    validity: budget.validity,
    description: budget.description,
  });
  budgetRevision._id = oid("5f1e9a2b3c4d5e6f70810099");
  return { source: name, stored: storedDocument(budgetRevision), response: answer({ ...budgetRevision.toJSON(), __v: 0 }), tasks: copyTasks(budgetRevision._id) };
}

// ---------------------------------------------------------------- edit (PUT /:id): budget.update(body, madeBy) + save
const editCases = [
  { name: "same descriptive, other person and author", existing: storedBudget, body: { descriptive: storedBudget.descriptive, personInCharge: "Eng.ª Maria Lopes" }, madeBy: user2Doc },
  { name: "nothing changes but the flags", existing: storedBudget, body: { descriptive: storedBudget.descriptive, personInCharge: storedBudget.personInCharge }, madeBy: userDoc },
  { name: "flags already false: nothing to write", existing: { ...storedBudget, complete: false, wasPdfDownloaded: false }, body: { descriptive: storedBudget.descriptive, personInCharge: storedBudget.personInCharge }, madeBy: userDoc },
  { name: "revision 1, new descriptive (no NAS rename)", existing: { ...storedBudget, revision: 1 }, body: { descriptive: "Novo descritivo", personInCharge: "Eng.ª Maria" }, madeBy: userDoc },
  { name: "revision 0, new descriptive (NAS rename, D5)", existing: storedBudget, body: { descriptive: "Novo descritivo", personInCharge: "Eng.ª Maria" }, madeBy: userDoc },
  { name: "imported: no personInCharge stored", existing: _.omit(storedBudget, "personInCharge"), body: { descriptive: storedBudget.descriptive, personInCharge: "Novo" }, madeBy: userDoc },
  { name: "body without personInCharge (D7: legacy saves undefined)", existing: storedBudget, body: { descriptive: storedBudget.descriptive }, madeBy: userDoc },
  { name: "imported: no complete/wasPdfDownloaded stored", existing: _.omit(storedBudget, ["complete", "wasPdfDownloaded"]), body: { descriptive: storedBudget.descriptive, personInCharge: storedBudget.personInCharge }, madeBy: userDoc },
  { name: "has a job (route answers the budget unchanged)", existing: { ...storedBudget, hasGenerateJob: true }, body: { descriptive: "Outro", personInCharge: "X" }, madeBy: user2Doc, hasJob: true },
];

async function runEdit({ name, existing, body, madeBy: madeByDoc, hasJob }) {
  const budget = populatedBudget(existing);
  const madeBy = hydrateUser(madeByDoc);
  sshCommands.length = 0;
  if (budget.hasGenerateJob) return { name, existing: bsonValue(existing), body, outcome: "200 unchanged", response: answer(budget.toJSON()) };
  await budget.update(body, madeBy);
  return {
    name,
    existing: bsonValue(existing),
    body,
    outcome: "200",
    update: saveDelta(budget),
    sshCommands: [...sshCommands],
    response: answer(budget.toJSON()),
  };
}

// ---------------------------------------------------------------- read: toJSON of stored shapes (key order)
const readShapes = [
  { name: "as created", doc: _.omit(storedBudget, "description") },
  { name: "description added later (stored last)", doc: storedBudget },
  { name: "new revision (description before date)", doc: (() => { const { description, ...rest } = storedBudget; const { date, __v, ...head } = rest; return { ...head, description, date, __v }; })() },
  { name: "old: no hasGenerateJob, __v", doc: _.omit(storedBudget, ["hasGenerateJob", "__v"]) },
  { name: "old: no date (default Date.now on read)", doc: _.omit(storedBudget, ["date"]) },
  { name: "unknown field", doc: { ...storedBudget, serverFolderPath: "/x" } },
  { name: "stored order differs", doc: { _id: oid(BUDGET_ID), budgetNumber: "26-0007", client: oid(CLIENT_ID), madeBy: oid(USER_ID), descriptive: "x", paymentMethod: "y", revision: 1, total: 10, date: new RealDate("2026-02-01T09:00:00.000Z") } },
];

function runRead({ name, doc }) {
  const plain = Budget.hydrate(_.cloneDeep(doc));
  const populated = populatedBudget(doc);
  return { name, stored: bsonValue(doc), response: answer(plain.toJSON()), populated: answer(populated.toJSON()) };
}

// ---------------------------------------------------------------- write
(async () => {
  const golden = {
    generatedBy: "tools/legacy/budget-golden.js",
    versions,
    clock: new RealDate(NOW).toISOString() + " (TZ=UTC)",
    newBudget: newBudgetBodies.map(runNewBudgetBody),
    update: updateBodies.map(runUpdateBody),
    search: searchQueries.map(runSearch),
    budgetNumbers: numberCounts.map((count) => ({ count, number: getBudgetNumber(count) })),
    countForThisYear: await runCountForYear(),
    list: await runList(),
    searchRows: await runSearchRows(),
    create: priceCases.map(runCreate),
    defaultTask: await Promise.all(priceCases.slice(0, 3).map(runDefaultTask)),
    clone: sourceShapes.map(runClone),
    newRevision: sourceShapes.map(runNewRevision),
    edit: [],
    read: readShapes.map(runRead),
  };
  for (const c of editCases) golden.edit.push(await runEdit(c));

  fs.mkdirSync(OUT_DIR, { recursive: true });
  const file = path.join(OUT_DIR, "budget-golden.json");
  fs.writeFileSync(file, JSON.stringify(golden, null, 2) + "\n");
  console.log(
    `wrote ${path.relative(REPO_ROOT, file)}: newBudget ${golden.newBudget.length}, update ${golden.update.length}, ` +
      `search ${golden.search.length}, create ${golden.create.length}, clone ${golden.clone.length}, ` +
      `newRevision ${golden.newRevision.length}, edit ${golden.edit.length}, read ${golden.read.length}`,
  );
  process.exit(0); // mongoose keeps no connection open, but the SSH stub module may hold timers
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
