#!/usr/bin/env node
// Phase 2 settings step (PLAN.md §6, §7.2, §10): golden file for the users, prices, payment methods and printer
// settings contract, derived FROM THE LEGACY CODE by running it. Nothing is started: no database, no network, no
// legacy server.
//
//   node tools/legacy/settings-golden.js [outDir]      (default tests/fixtures/legacy/settings)
//
// Runs, from ../back-end (DIB4T_LEGACY_ROOT), unchanged:
//   - the Joi 14.3.1 validations of validations/userValidation.js (validateUser, validateEditarUser),
//     pricesValidation.js, printerSettingsValidation.js (incl. the joi-objectid id check) and
//     paymentMethodValidation.js on raw JSON bodies (JSON.parse, like express.json()) → the 400 text or null;
//   - for every body that passes, what the route then does with the Mongoose 5.13 models (offline: documents are
//     built or hydrated in memory, validated with validateSync, and the save is read from $__delta):
//       * the stored document (create) or the $set/$unset/$inc of the save (update), BSON types by the js-bson 1.x
//         number rule (integral Int32 range → $numberInt, else $numberDouble);
//       * or "500" when the save would fail (Mongoose validation, bcryptjs "Illegal arguments"), which legacy answers
//         with Express's error page (the new app: "Something failed.", D16);
//       * the JSON the route answers (_.pick of the document), with the new document's _id as "{{oid}}" and every
//         bcrypt hash as "{{bcrypt}}".
//   - Mongoose findById casts of path ids (non-ObjectId → CastError → 500 in legacy).
//
// Output: <outDir>/settings-golden.json (README.md is hand-written). Deterministic: two runs give identical files.

"use strict";

const path = require("path");
const fs = require("fs");
const Module = require("module");

const REPO_ROOT = path.resolve(__dirname, "../..");
const BACKEND = path.resolve(process.env.DIB4T_LEGACY_ROOT || path.join(REPO_ROOT, ".."), "back-end");
const OUT_DIR = path.resolve(process.argv[2] || path.join(REPO_ROOT, "tests/fixtures/legacy/settings"));

const legacyRequire = Module.createRequire(path.join(BACKEND, "index.js"));
const _ = legacyRequire("lodash");
const mongoose = legacyRequire("mongoose");
const bcrypt = legacyRequire("bcryptjs");
const { User } = legacyRequire("./models/user");
const { Prices } = legacyRequire("./models/prices");
const { PrinterSettings } = legacyRequire("./models/printerSettings");
const { PaymentMethod } = legacyRequire("./models/paymentMethods");
// printerSettingsValidation.js uses Joi.ObjectId without installing it: in the running app another module
// (budgetValidation.js, loaded earlier by startup/routes.js) sets it on the shared Joi instance. Same here.
legacyRequire("./validations/budgetValidation");
const { validateUser, validateEditarUser } = legacyRequire("./validations/userValidation");
const { validatePrices } = legacyRequire("./validations/pricesValidation");
const { validatePrinterSettings, validatePrinterSettingsId } = legacyRequire("./validations/printerSettingsValidation");
const { validatePaymentMethod } = legacyRequire("./validations/paymentMethodValidation");
const { automation, consultancy, development, machining, assembly } = legacyRequire("./helpers/taskType");
const { getAllRoles } = legacyRequire("./helpers/roleName");
const BSON = legacyRequire("bson"); // 1.1.6, the serializer of the legacy driver
const bson = new BSON();

const versions = {
  bson: legacyRequire("bson/package.json").version,
  joi: legacyRequire("joi/package.json").version,
  "joi-objectid": legacyRequire("joi-objectid/package.json").version,
  mongoose: legacyRequire("mongoose/package.json").version,
  bcryptjs: legacyRequire("bcryptjs/package.json").version,
  lodash: legacyRequire("lodash/package.json").version,
};

const FIXED_ID = "5f1e9a2b3c4d5e6f70819299";
const HASH_PLACEHOLDER = "$2a$10$" + "x".repeat(53); // 60 characters, like every bcrypt hash
const BCRYPT_SALT_COST_4 = "$2a$04$abcdefghijklmnopqrstuu";

// ---------------------------------------------------------------- helpers
const j = (value) => JSON.stringify(value);
const s = (n, c = "a") => c.repeat(n);

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
    // The BSON type the legacy driver writes: ask its serializer (element type byte 0x10 = Int32, 0x01 = Double).
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

function scrub(text) {
  return text.split(HASH_PLACEHOLDER).join("{{bcrypt}}");
}

function scrubObject(value) {
  return JSON.parse(scrub(JSON.stringify(value)));
}

/** The document the legacy save inserts (field order included), as Extended JSON. */
function storedDocument(doc) {
  const raw = doc.toObject({ depopulate: true, versionKey: true, transform: false });
  const { _id, ...fields } = bsonValue(raw);
  // The insert puts _id first (characterization/01-account.json), whatever the order toObject() lists it in.
  const bson = { _id: "{{oid}}", ...fields };
  if (!("__v" in raw)) bson.__v = { $numberInt: "0" }; // Mongoose adds __v: 0 on insert
  return scrubObject(bson);
}

/** The update the legacy save sends: { $set, $unset, $inc } as Extended JSON, or null when nothing changed. */
function saveDelta(doc) {
  const delta = doc.$__delta();
  if (!delta) return null;
  const update = delta[1];
  const out = {};
  for (const op of Object.keys(update).sort()) {
    out[op] = bsonValue(update[op]);
  }
  return scrubObject(out);
}

/** The route's JSON answer, keys in the order Express sends them. */
function responseJson(value) {
  const parsed = JSON.parse(JSON.stringify(value));
  if (parsed && parsed._id && parsed._id !== FIXED_ID) parsed._id = "{{oid}}";
  return scrubObject(parsed);
}

function validationFailure(doc) {
  const error = doc.validateSync();
  return error ? error.message : null;
}

function bcryptThrows(password) {
  try {
    bcrypt.hashSync(password, BCRYPT_SALT_COST_4);
    return null;
  } catch (e) {
    return e.message;
  }
}

// ---------------------------------------------------------------- users: create (POST /api/account/create)
const allRoles = getAllRoles();
const okUser = {
  name: "Ana Administradora",
  email: "ana.admin@dib4t.local",
  password: "char-test-1",
  roles: allRoles,
  fieldOfCompetencies: "Desenvolvimento",
};
const withUser = (patch) => j({ ...okUser, ...patch });
const withoutUser = (key) => j(_.omit(okUser, key));

const createUserBodies = [
  // recorded and ordinary
  j(okUser),
  j({ ...okUser, passwordConfirmation: okUser.password }),
  j({ name: "Bruno Maquinação", email: "bruno.limitado@dib4t.local", password: "char-test-1", roles: ["canSeeObras", "canSeeLeituras"], fieldOfCompetencies: "Maquinação" }),
  j({ name: "Carla Sem Área", email: "carla.semarea@dib4t.local", password: "char-test-1", roles: ["canSeeDashboard"] }),
  // the legacy UI's body: one slot per checkbox, null when unchecked; canSeeAdjudicacoes is not in getAllRoles
  withUser({ roles: ["canSeeOrcamentos", null, "canSeeAdjudicacoes", null, "canSeeDefinicoes", null, null] }),
  withUser({ roles: [null, null, null, null, null, null, null] }),
  // presence
  j({}),
  withoutUser("name"),
  withoutUser("email"),
  withoutUser("password"),
  withoutUser("roles"),
  withoutUser("fieldOfCompetencies"),
  // name
  withUser({ name: "Al" }),
  withUser({ name: "Ana" }),
  withUser({ name: "" }),
  withUser({ name: null }),
  withUser({ name: 123 }),
  withUser({ name: "   " }),
  withUser({ name: "😀a" }),
  // email
  withUser({ email: "a@b.pt" }),
  withUser({ email: "abc@d.ef.pt" }),
  withUser({ email: "not-an-email" }),
  withUser({ email: "" }),
  withUser({ email: 1234567890 }),
  withUser({ email: "ana.admin@dib4t.local " }),
  // password
  withUser({ password: "1234" }),
  withUser({ password: "12345" }),
  withUser({ password: s(255) }),
  withUser({ password: s(256) }),
  withUser({ password: 12345 }),
  withUser({ password: "" }),
  // passwordConfirmation = Joi.ref("password")
  withUser({ passwordConfirmation: "other-pass" }),
  withUser({ passwordConfirmation: "" }),
  withUser({ passwordConfirmation: null }),
  withUser({ passwordConfirmation: 12345 }),
  j({ passwordConfirmation: "char-test-1", ...okUser }),
  j({ name: "Al", passwordConfirmation: "x", email: "x", password: "char-test-1" }),
  j({ passwordConfirmation: "x", name: "Ana Administradora", email: "ana.admin@dib4t.local", password: "1" }),
  // roles: array().min(1), any items; Mongoose [String] casts them
  withUser({ roles: [] }),
  withUser({ roles: "canSeeObras" }),
  withUser({ roles: '["canSeeObras","canSeeLeituras"]' }),
  withUser({ roles: "[]" }),
  withUser({ roles: null }),
  withUser({ roles: 1 }),
  withUser({ roles: {} }),
  withUser({ roles: ["x"] }),
  withUser({ roles: [1, 2.5, -0, 1e21, true, false] }),
  withUser({ roles: [{ a: 1 }] }),
  withUser({ roles: [["canSeeObras"]] }),
  withUser({ roles: [[]] }),
  withUser({ roles: [["a", "b"]] }),
  withUser({ roles: [""] }),
  // fieldOfCompetencies: string(); the Mongoose enum decides
  withUser({ fieldOfCompetencies: "Montagem" }),
  withUser({ fieldOfCompetencies: "Para faturação" }),
  withUser({ fieldOfCompetencies: "Faturada" }),
  withUser({ fieldOfCompetencies: "desenvolvimento" }),
  withUser({ fieldOfCompetencies: "" }),
  withUser({ fieldOfCompetencies: null }),
  withUser({ fieldOfCompetencies: 1 }),
  // unknown keys and error order
  withUser({ remember: true }),
  j({ remember: true, ...okUser }),
  j({ ...okUser, _id: FIXED_ID }),
  j({ ...okUser, __v: 3 }),
  j({ name: "Al", email: "x" }),
  j({ email: "x", name: "Al" }),
  j({ roles: [], name: "Ana Administradora", email: "ana.admin@dib4t.local", password: "char-test-1" }),
  "[]",
];

function runCreateUser(text) {
  const body = JSON.parse(text);
  const message = joiMessage(validateUser(body));
  if (message !== null) return { body: text, message };

  const user = new User(_.pick(body, ["name", "email", "password", "roles", "fieldOfCompetencies"]));
  user._id = new mongoose.Types.ObjectId(FIXED_ID);
  const thrown = typeof user.password === "string" ? null : bcryptThrows(user.password);
  if (thrown) return { body: text, message: null, outcome: "500", reason: thrown };
  user.password = HASH_PLACEHOLDER;
  const failure = validationFailure(user);
  if (failure) return { body: text, message: null, outcome: "500", reason: failure };
  return {
    body: text,
    message: null,
    outcome: "200",
    stored: storedDocument(user),
    response: responseJson(_.pick(user, ["_id", "name", "email", "roles"])),
  };
}

// ---------------------------------------------------------------- users: edit (PUT /api/account/:id)
const existingUser = {
  _id: new mongoose.Types.ObjectId(FIXED_ID),
  roles: ["canSeeObras", "canSeeLeituras"],
  fieldOfCompetencies: "Maquinação",
  name: "Bruno Maquinação",
  email: "bruno.limitado@dib4t.local",
  password: "$2a$04$abcdefghijklmnopqrstuu" + "y".repeat(31),
  __v: 0,
};
const okEdit = {
  name: "Bruno Maquinação",
  email: "bruno.limitado@dib4t.local",
  roles: ["canSeeObras", "canSeeLeituras"],
  fieldOfCompetencies: "Maquinação",
};
const edit = (patch, existing = {}) => ({ existing: { ...existingUser, ...existing }, body: j({ ...okEdit, ...patch }) });
const editWithout = (key, existing = {}) => ({ existing: { ...existingUser, ...existing }, body: j(_.omit(okEdit, key)) });

const editUserCases = [
  // recorded: the legacy UI sends the whole form; password "" when not changed
  edit({}),
  edit({ password: "" }),
  edit({ name: "Bruno Maquinação Silva", roles: ["canSeeObras", "canSeeLeituras", "canSeeHistorico"], fieldOfCompetencies: "Montagem" }),
  edit({ password: "char-test-2" }),
  edit({ password: "x" }),
  edit({ password: " " }),
  edit({ password: null }),
  edit({ password: 0 }),
  edit({ password: false }),
  edit({ password: 12345 }),
  edit({ password: true }),
  edit({ password: {} }),
  edit({ password: [] }),
  edit({ password: ["char-test-2"] }),
  // the legacy UI's roles with nulls
  edit({ roles: ["canSeeOrcamentos", null, null, null, "canSeeDefinicoes", null, null] }),
  edit({ roles: ["canSeeLeituras", "canSeeObras"] }),
  edit({ roles: ["canSeeObras", "canSeeLeituras", null] }),
  edit({ roles: [] }),
  edit({ roles: "canSeeObras" }),
  edit({ roles: '["canSeeObras"]' }),
  edit({ roles: [1, true] }),
  edit({ roles: [{ a: 1 }] }),
  editWithout("roles"),
  // fieldOfCompetencies
  editWithout("fieldOfCompetencies"),
  edit({ fieldOfCompetencies: "Faturada" }),
  edit({ fieldOfCompetencies: "" }),
  edit({ fieldOfCompetencies: "Para faturação" }),
  // name and e-mail: Joi lets through what Mongoose then refuses (minlength 3 / 10)
  edit({ name: "Al" }),
  edit({ name: "" }),
  edit({ name: 1 }),
  editWithout("name"),
  edit({ email: "a@b.pt" }),
  edit({ email: "abc@d.ef.pt" }),
  edit({ email: "not-an-email" }),
  edit({ email: "ana.admin@dib4t.local" }),
  editWithout("email"),
  // unknown keys (the legacy UI never sends passwordConfirmation on edit: it deletes it)
  edit({ passwordConfirmation: "x" }),
  edit({ confirmPassword: "x" }),
  edit({ _id: FIXED_ID }),
  // imported shapes: no fieldOfCompetencies stored, no __v, null role elements stored
  edit({}, { fieldOfCompetencies: undefined }),
  edit({ fieldOfCompetencies: "Desenvolvimento" }, { fieldOfCompetencies: undefined }),
  edit({ roles: ["canSeeObras"] }, { __v: undefined }),
  edit({}, { __v: undefined }),
  edit({}, { roles: ["canSeeObras", "canSeeLeituras", null] }),
  edit({ roles: ["canSeeObras", "canSeeLeituras", null] }, { roles: ["canSeeObras", "canSeeLeituras", null] }),
  edit({ roles: ["canSeeObras"] }, { __v: 7 }),
  { existing: existingUser, body: "[]" },
  { existing: existingUser, body: "{}" },
];

function runEditUser({ existing, body: text }) {
  const body = JSON.parse(text);
  const stored = _.omitBy(existing, (v) => v === undefined);
  const message = joiMessage(validateEditarUser(body));
  const result = { existing: bsonValue(stored), body: text, message };
  if (message !== null) return result;

  const user = User.hydrate(_.cloneDeep(stored));
  user.update(body);
  if (body.password) {
    const thrown = bcryptThrows(body.password);
    if (thrown) return { ...result, outcome: "500", reason: thrown };
    user.password = HASH_PLACEHOLDER;
  }
  const failure = validationFailure(user);
  if (failure) return { ...result, outcome: "500", reason: failure };
  return {
    ...result,
    outcome: "200",
    update: saveDelta(user),
    response: responseJson(_.pick(user, ["_id", "name", "email", "roles"])),
  };
}

// ---------------------------------------------------------------- prices (POST /api/prices)
const existingPrices = { _id: new mongoose.Types.ObjectId(FIXED_ID), automation: 10, consultancy: 10, development: 10, margin: 35, machining: 10, assembly: 10, __v: 0 };
const okPrices = { automation: 32.5, consultancy: 45, development: 30, machining: 38, assembly: 28, margin: 35 };
const price = (patch) => j({ ...okPrices, ...patch });

const pricesBodies = [
  j(okPrices),
  j({ automation: 10, consultancy: 10, development: 10, machining: 10, assembly: 10, margin: 35 }),
  j({ margin: 35, automation: 10, consultancy: 10, development: 10, machining: 10, assembly: 10 }),
  // the legacy UI sends the edited text fields as strings
  price({ automation: "32.5", consultancy: "45" }),
  price({ automation: " 12 " }),
  price({ automation: "+12" }),
  price({ automation: "-12" }),
  price({ automation: "1e2" }),
  price({ automation: "1E2" }),
  price({ automation: "1.5e-3" }),
  price({ automation: "12." }),
  price({ automation: ".5" }),
  price({ automation: "0x10" }),
  price({ automation: "12abc" }),
  price({ automation: "32,5" }),
  price({ automation: "" }),
  price({ automation: " " }),
  price({ automation: "Infinity" }),
  price({ automation: "NaN" }),
  price({ automation: "007" }),
  price({ automation: "1.50" }),
  price({ automation: "1.0" }),
  price({ automation: "9007199254740993" }),
  price({ automation: "0.1000000000000000055511151231257827" }),
  price({ automation: "1e400" }),
  price({ automation: "1.5e+2" }),
  price({ automation: "15e-1" }),
  price({ automation: "1000000000000000000000" }),
  price({ automation: "1e21" }),
  price({ automation: "0.0000001" }),
  price({ automation: "1e-7" }),
  price({ automation: "123456789012345680000" }),
  // numbers
  price({ automation: 0 }),
  price({ automation: -5 }),
  price({ automation: 1.1 }),
  price({ automation: 2147483648 }),
  price({ automation: 9007199254740993 }),
  price({ automation: 1e300 }),
  price({ automation: null }),
  price({ automation: true }),
  price({ automation: [1] }),
  price({ automation: {} }),
  // margin: min(1).max(100)
  price({ margin: 0 }),
  price({ margin: 0.99 }),
  price({ margin: 1 }),
  price({ margin: 100 }),
  price({ margin: 100.01 }),
  price({ margin: 101 }),
  price({ margin: "50" }),
  price({ margin: "0" }),
  price({ margin: 35.5 }),
  // presence and unknown keys
  j({}),
  j(_.omit(okPrices, "automation")),
  j(_.omit(okPrices, "margin")),
  j(_.omit(okPrices, "assembly")),
  j({ ...okPrices, others: 1 }),
  j({ ...okPrices, _id: FIXED_ID }),
  j({ consultancy: "x", automation: "y" }),
  // raw JSON numbers JSON.stringify cannot write
  '{"automation":1e400,"consultancy":45,"development":30,"machining":38,"assembly":28,"margin":35}',
  '{"automation":-0,"consultancy":45,"development":30,"machining":38,"assembly":28,"margin":35}',
  '{"automation":10.0,"consultancy":45,"development":30,"machining":38,"assembly":28,"margin":35}',
  '{"automation":1E2,"consultancy":45,"development":30,"machining":38,"assembly":28,"margin":35}',
  "[]",
];

function runPrices(text) {
  const body = JSON.parse(text);
  const message = joiMessage(validatePrices(body));
  if (message !== null) return { body: text, message };

  const prices = Prices.hydrate(_.cloneDeep(existingPrices));
  prices.set({
    automation: body.automation,
    consultancy: body.consultancy,
    development: body.development,
    margin: body.margin,
    machining: body.machining,
    assembly: body.assembly,
  });
  const failure = validationFailure(prices);
  if (failure) return { body: text, message, outcome: "500", reason: failure };
  return {
    body: text,
    message,
    outcome: "200",
    update: saveDelta(prices),
    response: responseJson(_.pick(prices, [automation, consultancy, development, machining, assembly, "margin"])),
  };
}

// ---------------------------------------------------------------- printer settings (POST /, PUT /:id)
const okPrinter = { ip: "127.0.0.1", port: 9100, templateName: "producao" };
const printer = (patch) => j({ ...okPrinter, ...patch });

const printerBodies = [
  j(okPrinter),
  j({ ip: "127.0.0.2", port: 9100, templateName: "maquinacao" }),
  j({ templateName: "producao", port: 9100, ip: "127.0.0.1" }),
  // ip(): IPv4, IPv6, IPvFuture, optional CIDR
  printer({ ip: "999.1.1.1" }),
  printer({ ip: "255.255.255.255" }),
  printer({ ip: "256.1.1.1" }),
  printer({ ip: "1.2.3" }),
  printer({ ip: "1.2.3.4.5" }),
  printer({ ip: "01.02.03.004" }),
  printer({ ip: "001.002.003.004" }),
  printer({ ip: "0001.2.3.4" }),
  printer({ ip: "10.0.0.0/8" }),
  printer({ ip: "10.0.0.0/32" }),
  printer({ ip: "10.0.0.0/33" }),
  printer({ ip: "10.0.0.0/" }),
  printer({ ip: " 1.2.3.4" }),
  printer({ ip: "1.2.3.4 " }),
  printer({ ip: "1.2.3.4\n" }),
  printer({ ip: "localhost" }),
  printer({ ip: "::1" }),
  printer({ ip: "::" }),
  printer({ ip: "fe80::1" }),
  printer({ ip: "FE80::A:b:C" }),
  printer({ ip: "fe80::1%eth0" }),
  printer({ ip: "2001:db8::/32" }),
  printer({ ip: "2001:db8::/129" }),
  printer({ ip: "::ffff:192.168.1.1" }),
  printer({ ip: "1:2:3:4:5:6:7:8" }),
  printer({ ip: "1:2:3:4:5:6:7:8:9" }),
  printer({ ip: "1::2::3" }),
  printer({ ip: "v1.abc" }),
  printer({ ip: "v1.abc/64" }),
  printer({ ip: "[::1]" }),
  printer({ ip: "" }),
  printer({ ip: null }),
  printer({ ip: 2130706433 }),
  printer({ ip: "１.2.3.4" }),
  printer({ ip: "1.2.3.٤" }),
  // port: number()
  printer({ port: "9100" }),
  printer({ port: " 9100 " }),
  printer({ port: 9100.5 }),
  printer({ port: -1 }),
  printer({ port: 0 }),
  printer({ port: 70000 }),
  printer({ port: 3000000000 }),
  printer({ port: "abc" }),
  printer({ port: null }),
  printer({ port: true }),
  printer({ port: "" }),
  // templateName: valid(producao, maquinacao)
  printer({ templateName: "outro" }),
  printer({ templateName: "Producao" }),
  printer({ templateName: "" }),
  printer({ templateName: null }),
  printer({ templateName: 1 }),
  // presence and unknown keys
  j({}),
  j(_.omit(okPrinter, "ip")),
  j(_.omit(okPrinter, "port")),
  j(_.omit(okPrinter, "templateName")),
  j({ ...okPrinter, name: "x" }),
  j({ ...okPrinter, _id: FIXED_ID }),
  j({ ...okPrinter, __v: 0 }),
  j({ ip: "x", port: "x", templateName: "x" }),
  "[]",
];

function runCreatePrinter(text) {
  const body = JSON.parse(text);
  const message = joiMessage(validatePrinterSettings(body));
  if (message !== null) return { body: text, message };

  const settings = new PrinterSettings(body);
  settings._id = new mongoose.Types.ObjectId(FIXED_ID);
  const failure = validationFailure(settings);
  if (failure) return { body: text, message, outcome: "500", reason: failure };
  const stored = storedDocument(settings);
  // Mongoose answers the saved document: __v 0 is set by the save itself.
  const response = responseJson({ ...settings.toJSON(), __v: 0 });
  return { body: text, message, outcome: "200", stored, response };
}

const existingPrinter = { _id: new mongoose.Types.ObjectId(FIXED_ID), ip: "127.0.0.1", port: 9100, templateName: "producao", __v: 0 };
const printerUpdateCases = [
  { id: FIXED_ID, body: j({ ip: "127.0.0.11", port: 9101, templateName: "producao" }) },
  { id: FIXED_ID, body: j(okPrinter) },
  { id: FIXED_ID, body: j({ ...okPrinter, port: "9100" }) },
  { id: FIXED_ID, body: j({ ...okPrinter, port: "9101" }) },
  { id: FIXED_ID, body: j({ ...okPrinter, port: 9100.5 }) },
  { id: FIXED_ID, body: j({ ...okPrinter, templateName: "maquinacao" }) },
  { id: FIXED_ID, body: j({ ...okPrinter, ip: "::1" }) },
  { id: FIXED_ID.toUpperCase(), body: j(okPrinter) },
  { id: "abc", body: j(okPrinter) },
  { id: "abc", body: j({}) },
  { id: "abcdefghijkl", body: j(okPrinter) },
  { id: FIXED_ID + "0", body: j(okPrinter) },
  { id: "5f1e9a2b3c4d5e6f7081929g", body: j(okPrinter) },
  { id: FIXED_ID, body: j({}) },
  { id: FIXED_ID, body: j({ ...okPrinter, ip: "999.1.1.1" }) },
];

function runUpdatePrinter({ id, body: text }) {
  const body = JSON.parse(text);
  const idMessage = joiMessage(validatePrinterSettingsId({ id }));
  if (idMessage !== null) return { id, body: text, message: idMessage };
  const message = joiMessage(validatePrinterSettings(body));
  if (message !== null) return { id, body: text, message };

  const settings = PrinterSettings.hydrate(_.cloneDeep(existingPrinter));
  settings.ip = body.ip;
  settings.port = body.port;
  settings.templateName = body.templateName;
  const failure = validationFailure(settings);
  if (failure) return { id, body: text, message, outcome: "500", reason: failure };
  return { id, body: text, message, outcome: "200", update: saveDelta(settings), response: responseJson(settings.toJSON()) };
}

// ---------------------------------------------------------------- payment methods (POST /api/entity/addPaymentMethod)
const paymentMethodBodies = [
  j({ name: "Pronto pagamento" }),
  j({ name: "Transferência a 30 dias" }),
  j({ name: " 30 dias " }),
  j({ name: "a" }),
  j({ name: s(300) }),
  j({}),
  j({ name: "" }),
  j({ name: " " }),
  j({ name: null }),
  j({ name: 30 }),
  j({ name: ["a"] }),
  j({ name: "x", extra: 1 }),
  j({ extra: 1, name: "x" }),
  j({ name: "x", _id: FIXED_ID }),
  j({ name: "x", __v: 5 }),
  "[]",
];

function runPaymentMethod(text) {
  const body = JSON.parse(text);
  const message = joiMessage(validatePaymentMethod(body));
  if (message !== null) return { body: text, message };
  const method = new PaymentMethod(body);
  method._id = new mongoose.Types.ObjectId(FIXED_ID);
  const failure = validationFailure(method);
  if (failure) return { body: text, message, outcome: "500", reason: failure };
  return { body: text, message, outcome: "200", stored: storedDocument(method), response: responseJson({ ...method.toJSON(), __v: 0 }) };
}

// ---------------------------------------------------------------- path ids (findById casts)
const pathIds = [
  FIXED_ID,
  FIXED_ID.toUpperCase(),
  "0123456789abcdef01234567",
  "abc",
  "me",
  "roles",
  "users",
  "abcdefghijkl",
  "123456789012",
  FIXED_ID + "0",
  "5f1e9a2b3c4d5e6f7081929g",
  " " + FIXED_ID,
];

function runPathId(id) {
  try {
    const cast = mongoose.Types.ObjectId(id);
    // findById(id) → findOne({ _id: id }): the cast decides between "found or not" (a query) and a CastError (500).
    const query = User.findById(id);
    query.cast(User);
    return { id, cast: "ok", objectId: cast.toHexString() };
  } catch (e) {
    return { id, cast: "500", reason: e.name };
  }
}

// ---------------------------------------------------------------- write
const golden = {
  generatedBy: "tools/legacy/settings-golden.js",
  versions,
  roles: getAllRoles(),
  // Joi's string().ip() pattern with the default options (cidr optional, every version), as JavaScript source.
  ipPattern: legacyRequire("joi/lib/types/string/ip").createIpRegex(["ipv4", "ipv6", "ipvfuture"], "optional").source,
  createUser: createUserBodies.map(runCreateUser),
  editUser: editUserCases.map(runEditUser),
  prices: { existing: bsonValue(existingPrices), cases: pricesBodies.map(runPrices) },
  createPrinter: printerBodies.map(runCreatePrinter),
  editPrinter: { existing: bsonValue(existingPrinter), cases: printerUpdateCases.map(runUpdatePrinter) },
  createPaymentMethod: paymentMethodBodies.map(runPaymentMethod),
  pathIds: pathIds.map(runPathId),
};

fs.mkdirSync(OUT_DIR, { recursive: true });
const file = path.join(OUT_DIR, "settings-golden.json");
fs.writeFileSync(file, JSON.stringify(golden, null, 2) + "\n");
const count = (list) => list.length;
console.log(
  `wrote ${path.relative(REPO_ROOT, file)}: createUser ${count(golden.createUser)}, editUser ${count(golden.editUser)}, ` +
    `prices ${count(golden.prices.cases)}, createPrinter ${count(golden.createPrinter)}, editPrinter ${count(golden.editPrinter.cases)}, ` +
    `createPaymentMethod ${count(golden.createPaymentMethod)}, pathIds ${count(golden.pathIds)}`,
);
