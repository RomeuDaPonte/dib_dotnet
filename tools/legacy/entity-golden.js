#!/usr/bin/env node
// Phase 2 entities step (PLAN.md §6, §7.2, §10): golden file for the entity contract (routes/entity.js), derived
// FROM THE LEGACY CODE by running it. Nothing is started: no database, no network, no legacy server.
//
//   node tools/legacy/entity-golden.js [outDir]      (default tests/fixtures/legacy/entities)
//
// Runs, from ../back-end (DIB4T_LEGACY_ROOT), unchanged:
//   - validations/entityValidation.js (Joi 14.3.1) on raw JSON bodies (JSON.parse, like express.json()) → the 400
//     text or null;
//   - for every body that passes, what the route then does with the Mongoose 5.13 Entity model (offline: documents
//     are built or hydrated in memory, validated with validateSync, the save read from $__delta):
//       * POST /api/entity: the stored document and the answer (res.send(newEntity)), or "500" when the save fails;
//       * PUT /api/entity/:id: the $set/$unset of entity.set({…}) + save on an existing document, the answer
//         (_.pick of the 8 fields), or "500";
//   - POST /api/entity/search: the five RegExps of repositories/entityRepository.js search() built from the body
//     (source + flags, as the legacy driver's js-bson 1.x serializer writes them), or the error that makes the
//     route fail (500);
//   - GET /api/entity/:id and getAll: the JSON of a stored document (key order, missing and extra fields);
//   - getAllEntityTypes(); Mongoose findById casts of path ids.
//
// Output: <outDir>/entity-golden.json (README.md is hand-written). Deterministic: two runs give identical files.

"use strict";

const path = require("path");
const fs = require("fs");
const Module = require("module");

const REPO_ROOT = path.resolve(__dirname, "../..");
const BACKEND = path.resolve(process.env.DIB4T_LEGACY_ROOT || path.join(REPO_ROOT, ".."), "back-end");
const OUT_DIR = path.resolve(process.argv[2] || path.join(REPO_ROOT, "tests/fixtures/legacy/entities"));

const legacyRequire = Module.createRequire(path.join(BACKEND, "index.js"));
const _ = legacyRequire("lodash");
const mongoose = legacyRequire("mongoose");
const { Entity } = legacyRequire("./models/entity");
const { validateEntity } = legacyRequire("./validations/entityValidation");
const { getAllEntityTypes } = legacyRequire("./helpers/entityTypes");
const BSON = legacyRequire("bson"); // 1.1.6, the serializer of the legacy driver
const bson = new BSON();

const versions = {
  bson: legacyRequire("bson/package.json").version,
  joi: legacyRequire("joi/package.json").version,
  mongoose: legacyRequire("mongoose/package.json").version,
  lodash: legacyRequire("lodash/package.json").version,
  node: process.version,
};

const FIXED_ID = "5f1e9a2b3c4d5e6f70819299";
const j = (value) => JSON.stringify(value);

function joiMessage(result) {
  return result && result.error ? result.error.details[0].message : null;
}

/** A JS value as canonical Extended JSON with the js-bson 1.x number rule (what the legacy driver stores). */
function bsonValue(value) {
  if (value === null || value === undefined) return null;
  if (value instanceof mongoose.Types.ObjectId) return { $oid: value.toHexString() };
  if (value instanceof Date) return { $date: { $numberLong: String(value.getTime()) } };
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

function storedDocument(doc) {
  const raw = doc.toObject({ depopulate: true, versionKey: true, transform: false });
  const { _id, ...fields } = bsonValue(raw);
  const out = { _id: "{{oid}}", ...fields };
  if (!("__v" in raw)) out.__v = { $numberInt: "0" };
  return out;
}

function saveDelta(doc) {
  const delta = doc.$__delta();
  if (!delta) return null;
  const update = delta[1];
  const out = {};
  for (const op of Object.keys(update).sort()) out[op] = bsonValue(update[op]);
  return out;
}

function responseJson(value) {
  const parsed = JSON.parse(JSON.stringify(value));
  if (parsed && parsed._id && parsed._id !== FIXED_ID) parsed._id = "{{oid}}";
  return parsed;
}

function validationFailure(doc) {
  const error = doc.validateSync();
  return error ? error.message : null;
}

// ---------------------------------------------------------------- create (POST /api/entity)
// The legacy dialog's body (entityService.newEntity): every field, "" when left empty.
const okEntity = {
  name: "Metalúrgica Exemplo, Lda.",
  fiscalNumber: "509999990",
  address: "Rua das Flores, 12",
  zipCode: "4000-123",
  location: "Porto",
  paymentCondition: "30 dias",
  type: "Cliente",
};
const withEntity = (patch) => j({ ...okEntity, ...patch });
const withoutEntity = (key) => j(_.omit(okEntity, key));

const createBodies = [
  // ordinary
  j(okEntity),
  withEntity({ type: "Fornecedor" }),
  withEntity({ type: "Cliente e Fornecedor" }),
  withEntity({ fiscalNumber: "", address: "", zipCode: "", location: "" }),
  j({ type: "Cliente", paymentCondition: "30 dias", location: "Porto", zipCode: "4000-123", address: "Rua", fiscalNumber: "1", name: "Ordem Inversa" }),
  j({ name: "Só obrigatórios", paymentCondition: "Pronto pagamento", type: "Cliente" }),
  withEntity({ name: "  Espaços  ", location: " Porto " }),
  withEntity({ name: "Café Decomposto" }),
  withEntity({ fiscalNumber: "509\t999990" }),
  // presence
  j({}),
  withoutEntity("name"),
  withoutEntity("paymentCondition"),
  withoutEntity("type"),
  withoutEntity("fiscalNumber"),
  withoutEntity("address"),
  withoutEntity("zipCode"),
  withoutEntity("location"),
  // name: string().required()
  withEntity({ name: "" }),
  withEntity({ name: " " }),
  withEntity({ name: null }),
  withEntity({ name: 1 }),
  withEntity({ name: true }),
  withEntity({ name: ["a"] }),
  withEntity({ name: { a: 1 } }),
  withEntity({ name: "a".repeat(500) }),
  // paymentCondition: string().required() (any text: the payment methods are not checked)
  withEntity({ paymentCondition: "" }),
  withEntity({ paymentCondition: null }),
  withEntity({ paymentCondition: 30 }),
  withEntity({ paymentCondition: "Não existe" }),
  // type: valid(…).required()
  withEntity({ type: "cliente" }),
  withEntity({ type: "Cliente " }),
  withEntity({ type: "" }),
  withEntity({ type: null }),
  withEntity({ type: 1 }),
  withEntity({ type: ["Cliente"] }),
  // Joi.any(): whatever the Mongoose String cast makes of it
  withEntity({ fiscalNumber: null }),
  withEntity({ fiscalNumber: 509999990 }),
  withEntity({ fiscalNumber: 1.5 }),
  withEntity({ fiscalNumber: -0 }),
  withEntity({ fiscalNumber: 1e21 }),
  withEntity({ fiscalNumber: 0.0000001 }),
  withEntity({ fiscalNumber: true }),
  withEntity({ fiscalNumber: false }),
  withEntity({ fiscalNumber: {} }),
  withEntity({ fiscalNumber: [] }),
  withEntity({ fiscalNumber: ["509"] }),
  withEntity({ fiscalNumber: { _id: "abc" } }),
  withEntity({ fiscalNumber: { _id: 1 } }),
  withEntity({ fiscalNumber: { _id: "" } }),
  withEntity({ fiscalNumber: { toString: 1 } }),
  withEntity({ address: 12, zipCode: 4000, location: null }),
  withEntity({ address: { a: 1 } }),
  withEntity({ zipCode: [4000] }),
  withEntity({ location: { _id: "Porto", x: 1 } }),
  // unknown keys and error order
  withEntity({ extra: 1 }),
  j({ extra: 1, ...okEntity }),
  withEntity({ _id: FIXED_ID }),
  withEntity({ __v: 3 }),
  j({ type: "x", name: "" }),
  j({ paymentCondition: "", type: "x" }),
  "[]",
  '{"name":"Duplicada","name":"Última","paymentCondition":"30 dias","type":"Cliente"}',
];

function runCreate(text) {
  const body = JSON.parse(text);
  const message = joiMessage(validateEntity(body));
  if (message !== null) return { body: text, message };

  const entity = new Entity(body);
  entity._id = new mongoose.Types.ObjectId(FIXED_ID);
  const failure = validationFailure(entity);
  if (failure) return { body: text, message, outcome: "500", reason: failure };
  // Mongoose answers the saved document: __v 0 is set by the save itself.
  return { body: text, message, outcome: "200", stored: storedDocument(entity), response: responseJson({ ...entity.toJSON(), __v: 0 }) };
}

// ---------------------------------------------------------------- edit (PUT /api/entity/:id)
const existingEntity = {
  _id: new mongoose.Types.ObjectId(FIXED_ID),
  name: "Metalúrgica Exemplo, Lda.",
  fiscalNumber: "509999990",
  address: "Rua das Flores, 12",
  zipCode: "4000-123",
  location: "Porto",
  paymentCondition: "30 dias",
  type: "Cliente",
  __v: 0,
};
const edit = (patch, existing = {}) => ({ existing: { ...existingEntity, ...existing }, body: withEntity(patch) });
const editWithout = (keys, existing = {}) => ({ existing: { ...existingEntity, ...existing }, body: j(_.omit(okEntity, keys)) });

const editCases = [
  edit({}),
  edit({ name: "Metalúrgica Exemplo, S.A.", location: "Maia" }),
  edit({ type: "Cliente e Fornecedor", paymentCondition: "60 dias" }),
  edit({ fiscalNumber: "", address: "", zipCode: "", location: "" }),
  edit({ name: "Café" }),
  // optional fields missing from the body: entity.set({ x: undefined })
  editWithout(["fiscalNumber"]),
  editWithout(["fiscalNumber", "address", "zipCode", "location"]),
  editWithout(["location"], { location: undefined }),
  // casts
  edit({ fiscalNumber: null }),
  edit({ fiscalNumber: null }, { fiscalNumber: null }),
  edit({ fiscalNumber: 509999990 }),
  edit({ fiscalNumber: 1.5 }),
  edit({ fiscalNumber: true }),
  edit({ fiscalNumber: {} }),
  edit({ fiscalNumber: ["509999990"] }),
  edit({ fiscalNumber: { _id: "509999990" } }),
  edit({ fiscalNumber: { _id: "x" } }),
  // Joi
  edit({ name: "" }),
  edit({ type: "Outro" }),
  edit({ paymentCondition: null }),
  editWithout(["name"]),
  edit({ _id: FIXED_ID }),
  edit({ __v: 0 }),
  { existing: existingEntity, body: "{}" },
  { existing: existingEntity, body: "[]" },
  // imported shapes: missing fields, no __v, a stored null, a stored number, unknown type, extra field
  edit({}, { fiscalNumber: undefined, address: undefined, zipCode: undefined, location: undefined }),
  editWithout(["fiscalNumber", "address", "zipCode", "location"], { fiscalNumber: undefined, address: undefined, zipCode: undefined, location: undefined }),
  edit({}, { __v: undefined }),
  edit({ location: "Maia" }, { __v: undefined }),
  edit({}, { zipCode: null }),
  edit({}, { fiscalNumber: 509999990 }),
  edit({ fiscalNumber: 509999990 }, { fiscalNumber: 509999990 }),
  edit({}, { type: "Desconhecido" }),
  edit({}, { name: undefined }),
  edit({}, { extra: "kept" }),
  edit({}, { __v: 7 }),
];

function runEdit({ existing, body: text }) {
  const body = JSON.parse(text);
  const stored = _.omitBy(existing, (v) => v === undefined);
  const message = joiMessage(validateEntity(body));
  const result = { existing: bsonValue(stored), body: text, message };
  if (message !== null) return result;

  const entity = Entity.hydrate(_.cloneDeep(stored));
  entity.set({
    name: body.name,
    fiscalNumber: body.fiscalNumber,
    address: body.address,
    zipCode: body.zipCode,
    location: body.location,
    paymentCondition: body.paymentCondition,
    type: body.type,
  });
  const failure = validationFailure(entity);
  if (failure) return { ...result, outcome: "500", reason: failure };
  return {
    ...result,
    outcome: "200",
    update: saveDelta(entity),
    response: responseJson(_.pick(entity, ["_id", "name", "fiscalNumber", "address", "zipCode", "location", "paymentCondition", "type"])),
  };
}

// ---------------------------------------------------------------- search (POST /api/entity/search)
const okSearch = { name: "Met", nif: "509", localidade: "Por", morada: "Rua", codigoPostal: "4000" };
const withSearch = (patch) => j({ ...okSearch, ...patch });
// The legacy service (searchEntity) sends every key; an empty field is "".
const emptySearch = { name: "", nif: "", localidade: "", morada: "", codigoPostal: "" };

const searchBodies = [
  j(okSearch),
  j(emptySearch),
  j({ ...emptySearch, name: "met" }),
  j({}),
  j({ name: "Met" }),
  "[]",
  // JavaScript String(value) of non-strings
  j({ ...emptySearch, name: null }),
  j({ ...emptySearch, name: 5 }),
  j({ ...emptySearch, name: 1.5 }),
  j({ ...emptySearch, name: 1e21 }),
  j({ ...emptySearch, name: -0 }),
  j({ ...emptySearch, name: true }),
  j({ ...emptySearch, name: {} }),
  j({ ...emptySearch, name: [] }),
  j({ ...emptySearch, name: ["a", "b"] }),
  j({ ...emptySearch, name: ["a", ["b", "c"], null, 1, {}] }),
  j({ ...emptySearch, name: { toString: 1 } }),
  j({ ...emptySearch, name: [{ toString: 1 }] }),
  // regex syntax: the user's text is the pattern
  j({ ...emptySearch, name: "M.t" }),
  j({ ...emptySearch, name: "Met|Por" }),
  j({ ...emptySearch, name: "(" }),
  j({ ...emptySearch, name: "[" }),
  j({ ...emptySearch, name: "*" }),
  j({ ...emptySearch, name: "a{2,1}" }),
  j({ ...emptySearch, name: "\\" }),
  j({ ...emptySearch, name: "a/b" }),
  j({ ...emptySearch, name: "a\nb" }),
  j({ ...emptySearch, name: "a b" }),
  j({ ...emptySearch, name: "a\u0000b" }),
  j({ ...emptySearch, name: "(?<x>a)" }),
  j({ ...emptySearch, name: "(?<=a)b" }),
  j({ ...emptySearch, name: "\\p{L}" }),
  j({ ...emptySearch, name: "\\k" }),
  j({ ...emptySearch, name: "a**" }),
  j({ ...emptySearch, name: "Ç" }),
  j({ ...emptySearch, nif: "(" }),
  j({ ...emptySearch, codigoPostal: "[" }),
  j({ ...emptySearch, extra: "(" }),
];

const searchPaths = [
  ["name", "name"],
  ["fiscalNumber", "nif"],
  ["location", "localidade"],
  ["address", "morada"],
  ["zipCode", "codigoPostal"],
];

function runSearch(text) {
  const object = JSON.parse(text);
  try {
    const regexes = searchPaths.map(([field, key]) => {
      const regex = new RegExp(`^${object[key]}`, "i");
      bson.serialize({ v: regex }); // the legacy driver refuses some sources (null bytes)
      return { field, source: regex.source, flags: regex.flags };
    });
    // The legacy query: Entity.find().and([...]) → { $and: [{ name: /…/i }, …] } (Mongoose casts nothing else).
    Entity.find().and(regexes.map((r) => ({ [r.field]: new RegExp(r.source, r.flags) }))).cast(Entity);
    return { body: text, outcome: "200", regexes };
  } catch (e) {
    return { body: text, outcome: "500", reason: `${e.name}: ${e.message}` };
  }
}

// ---------------------------------------------------------------- read (GET /:id, getAll, getallclients)
const readDocuments = [
  existingEntity,
  _.omit(existingEntity, ["fiscalNumber", "address", "zipCode", "location"]),
  { ...existingEntity, zipCode: null },
  { ...existingEntity, fiscalNumber: 509999990 },
  _.omit(existingEntity, ["__v"]),
  { type: "Cliente", _id: existingEntity._id, __v: 0, name: "Ordem", location: "Porto" },
  { ...existingEntity, extra: "x" },
  { ...existingEntity, type: "Desconhecido" },
];

function runRead(stored) {
  const entity = Entity.hydrate(_.cloneDeep(stored));
  return { stored: bsonValue(stored), response: responseJson(entity.toJSON()) };
}

// ---------------------------------------------------------------- path ids (findById casts)
const pathIds = [FIXED_ID, FIXED_ID.toUpperCase(), "abc", "getAll", "abcdefghijkl", FIXED_ID + "0", "5f1e9a2b3c4d5e6f7081929g"];

function runPathId(id) {
  try {
    const cast = mongoose.Types.ObjectId(id);
    Entity.findById(id).cast(Entity);
    return { id, cast: "ok", objectId: cast.toHexString() };
  } catch (e) {
    return { id, cast: "500", reason: e.name };
  }
}

// ---------------------------------------------------------------- write
const golden = {
  generatedBy: "tools/legacy/entity-golden.js",
  versions,
  entityTypes: getAllEntityTypes(),
  createEntity: createBodies.map(runCreate),
  editEntity: editCases.map(runEdit),
  search: searchBodies.map(runSearch),
  read: readDocuments.map(runRead),
  pathIds: pathIds.map(runPathId),
};

fs.mkdirSync(OUT_DIR, { recursive: true });
const file = path.join(OUT_DIR, "entity-golden.json");
fs.writeFileSync(file, JSON.stringify(golden, null, 2) + "\n");
console.log(
  `wrote ${path.relative(REPO_ROOT, file)}: createEntity ${golden.createEntity.length}, editEntity ${golden.editEntity.length}, ` +
    `search ${golden.search.length}, read ${golden.read.length}, pathIds ${golden.pathIds.length}`,
);
