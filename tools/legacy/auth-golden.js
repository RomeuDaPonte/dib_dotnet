#!/usr/bin/env node
// Phase 1 auth step (PLAN.md §4.6, §6, §10): golden file for the login contract, derived FROM THE LEGACY CODE by
// running it. Nothing is started: no database, no network, no legacy server.
//
//   node tools/legacy/auth-golden.js [outDir]      (default tests/fixtures/legacy/auth)
//
// Runs, from ../back-end (DIB4T_LEGACY_ROOT), unchanged:
//   - validations/userValidation.js validateLogin (Joi 14.3.1) on raw JSON bodies → the 400 text or null.
//     Bodies are parsed with JSON.parse like express.json() does (strict: object or array only).
//   - Joi.string().email() (isemail 3.2.0, Joi's defaults) on an e-mail corpus → valid or not.
//   - bcryptjs 2.4.3: hashes of fixed passwords with fixed salts (so the file is deterministic), and the
//     $2b$ prefix handling, so BCrypt.Net-Next can be checked against them.
//   - jsonwebtoken 8.5.1: a token for a fixed payload, fixed iat and a test key, so the new app's token format can
//     be compared byte for byte (header, claim order, signature). The legacy key is NOT used.
//
// Output: <outDir>/auth-golden.json (README.md is hand-written). Deterministic: two runs give identical files.

"use strict";

const path = require("path");
const fs = require("fs");
const Module = require("module");

const REPO_ROOT = path.resolve(__dirname, "../..");
const BACKEND = path.resolve(process.env.DIB4T_LEGACY_ROOT || path.join(REPO_ROOT, ".."), "back-end");
const OUT_DIR = path.resolve(process.argv[2] || path.join(REPO_ROOT, "tests/fixtures/legacy/auth"));

const legacyRequire = Module.createRequire(path.join(BACKEND, "index.js"));
const Joi = legacyRequire("joi");
const bcrypt = legacyRequire("bcryptjs");
const jwt = legacyRequire("jsonwebtoken");
const { validateLogin } = legacyRequire("./validations/userValidation");

const versions = {
  joi: legacyRequire("joi/package.json").version,
  isemail: legacyRequire("isemail/package.json").version,
  bcryptjs: legacyRequire("bcryptjs/package.json").version,
  jsonwebtoken: legacyRequire("jsonwebtoken/package.json").version,
};

// ---------------------------------------------------------------- login bodies (raw JSON text)
const ok = { email: "ana.admin@dib4t.local", password: "char-test-1" };
const s = (n, c = "a") => c.repeat(n);
const j = (value) => JSON.stringify(value);
const loginBodies = [
  // presence and order of the two keys
  j({}),
  j({ email: ok.email }),
  j({ password: ok.password }),
  j(ok),
  j({ password: ok.password, email: ok.email }),
  // email: type, empty, length, format
  j({ email: "", password: ok.password }),
  j({ email: null, password: ok.password }),
  j({ email: 12345678901, password: ok.password }),
  j({ email: true, password: ok.password }),
  j({ email: [ok.email], password: ok.password }),
  j({ email: { a: 1 }, password: ok.password }),
  j({ email: "a@b", password: ok.password }),
  j({ email: "a@b.c", password: ok.password }),
  j({ email: "abcd", password: ok.password }),
  j({ email: "not-an-email", password: ok.password }),
  j({ email: s(243) + "@dib4t.local", password: ok.password }),
  j({ email: s(244) + "@dib4t.local", password: ok.password }),
  j({ email: " " + ok.email, password: ok.password }),
  j({ email: ok.email + " ", password: ok.password }),
  j({ email: "ANA.ADMIN@DIB4T.LOCAL", password: ok.password }),
  j({ email: "joão@dib4t.pt", password: ok.password }),
  // password: type, empty, length (JS length = UTF-16 code units)
  j({ email: ok.email, password: "" }),
  j({ email: ok.email, password: null }),
  j({ email: ok.email, password: 12345 }),
  j({ email: ok.email, password: "1234" }),
  j({ email: ok.email, password: "12345" }),
  j({ email: ok.email, password: s(255) }),
  j({ email: ok.email, password: s(256) }),
  j({ email: ok.email, password: "😀😀" }),
  j({ email: ok.email, password: "😀😀a" }),
  j({ email: ok.email, password: "     " }),
  // both invalid: which error wins
  j({ email: "", password: "" }),
  j({ email: "x", password: 1 }),
  // unknown keys and their order relative to the schema keys
  j({ email: ok.email, password: ok.password, remember: true }),
  j({ remember: true, email: ok.email, password: ok.password }),
  j({ remember: true }),
  j({ remember: true, email: "x" }),
  j({ email: ok.email, password: ok.password, b: 1, a: 2 }),
  j({ email: ok.email, password: ok.password, x: 1, 2: 1, 1: 1 }),
  j({ email: ok.email, password: ok.password, "": 1 }),
  j({ email: ok.email, password: ok.password, passwordConfirmation: ok.password }),
  '{"email":"ana.admin@dib4t.local","password":"char-test-1","__proto__":1}',
  // duplicate keys: JSON.parse keeps the last value at the first position
  '{"email":1,"password":"char-test-1","email":"ana.admin@dib4t.local"}',
  '{"email":"ana.admin@dib4t.local","password":"char-test-1","x":1,"x":2}',
  // not an object
  "[]",
  '["ana.admin@dib4t.local","char-test-1"]',
];

function runLogin(text) {
  const body = JSON.parse(text);
  const { error } = validateLogin(body);
  return { body: text, message: error ? error.details[0].message : null };
}

// ---------------------------------------------------------------- isemail corpus
const emailSchema = Joi.string().email();
const label63 = s(63, "b");
const emails = [
  "ana.admin@dib4t.local", "a@b", "a@b.c", "a.b.c@d.e.f", "a+tag@x.pt", "A@X.PT", "o'neil@x.pt",
  "!#$%&'*+-/=?^_`{|}~@x.pt", "a@123", "a@x.123", "a@1x.pt", "a@x.1pt", "a@x.p1",
  "", "a", "@x.pt", "a@", "a@@x.pt", "a@b@c.pt", "a..b@x.pt", ".a@x.pt", "a.@x.pt",
  "a@x..pt", "a@.x.pt", "a@x.pt.", "a@-x.pt", "a@x-.pt", "a@x.-pt", "a@x.pt-", "a@x-y.pt", "a@x_y.pt",
  "a@x~y.pt", "a@x!.pt", "a b@x.pt", " a@x.pt", "a@x.pt ", "a @x.pt", "a@ x.pt", "a\t@x.pt", "a@x.pt\t",
  "a\r\n @x.pt", "a\r\n@x.pt", "a\r@x.pt", "a\n@x.pt", "a@x.pt\r\n", "a@x\r\n .pt",
  "a(comment)@x.pt", "(c)a@x.pt", "a@(c)x.pt", "a@x.pt(c)", "a@x(c).pt", "a(c@x.pt", "a(c\\)@x.pt",
  "\"quoted\"@x.pt", "\"quo ted\"@x.pt", "\"quo\\\"ted\"@x.pt", "\"quo\\ted\"@x.pt", "\"\"@x.pt",
  "\"a\"b@x.pt", "a.\"b\"@x.pt", "\"a\".b@x.pt", "\"unclosed@x.pt", "\"a\\\"@x.pt", "\"a\tb\"@x.pt",
  "\"a\u0001b\"@x.pt", "\"a\u007fb\"@x.pt", "\"a\nb\"@x.pt", "\"a\u0085b\"@x.pt",
  "a@[1.2.3.4]", "a@[1.2.3.256]", "a@[01.2.3.4]", "a@[1.2.3]", "a@[IPv6:::1]", "a@[ipv6:1:2:3:4:5:6:7:8]",
  "a@[IPv6:1:2:3:4:5:6:7]", "a@[IPv6:1:2:3:4:5:6:7:8:9]", "a@[IPv6:1::2:3:4:5:6:7]", "a@[IPv6:1::2:3:4:5:6]",
  "a@[IPv6:1::2::3]", "a@[IPv6::1:2:3:4:5:6:7]", "a@[IPv6:1:2:3:4:5:6:7:]", "a@[IPv6:1:2:3:4:5:6:1.2.3.4]",
  "a@[IPv6:1:2:3:4:5:1.2.3.4]", "a@[IPv6:g::1]", "a@[IPv6:12345::1]", "a@[ab]", "a@[a\\b]", "a@[a b]",
  "a@[1.2.3.4].pt", "a@x.[1.2.3.4]", "a@x[1.2.3.4]", "a@[1.2.3.4]x", "a@[1.2.3.4", "a@[a[b]", "a@[\u0000]",
  "a@[\u0001]", "a\\b@x.pt", "a\"b@x.pt", "a,b@x.pt", "a;b@x.pt", "a<b@x.pt", "a:b@x.pt", "a[b@x.pt",
  "\u0000@x.pt", "a\u0001@x.pt", "a\u007f@x.pt", "a\u0085@x.pt", "a @x.pt",
  "joão@x.pt", "a@joão.pt", "joão@x.pt", "😀@x.pt", "a@😀.pt", "a@x.pt。com",
  "ação.çé@dib4t.pt", "a@xn--jo-9ia.pt", "a@JOÃO.PT", "a@é.pt",
  s(64) + "@x.pt", s(65) + "@x.pt", s(32, "é") + "@x.pt", s(33, "é") + "@x.pt",
  "a@" + label63 + ".pt", "a@" + label63 + "b.pt", "a@x." + label63, "a@x." + label63 + "b",
  "a@" + s(56, "é") + ".pt", "a@" + s(57, "é") + ".pt", "a@" + s(60, "é") + ".pt",
  "a@" + [label63, label63, label63, s(61, "b")].join("."), "a@" + [label63, label63, label63, s(62, "b")].join("."),
  s(64) + "@" + [label63, label63, s(61, "b")].join("."), s(64) + "@" + [label63, label63, s(62, "b")].join("."),
];

function runEmail(value) {
  return { email: value, valid: !emailSchema.validate(value).error };
}

// ---------------------------------------------------------------- bcryptjs
const salts = ["$2a$10$abcdefghijklmnopqrstuu", "$2a$10$ABCDEFGHIJKLMNOPQRSTUu", "$2a$04$0123456789012345678901",
  "$2b$10$abcdefghijklmnopqrstuu"];
const passwords = ["char-test-1", "dib4t-dev", "12345", "palavra-passe çãé", "😀😀a",
  s(71, "x"), s(72, "x"), s(73, "x"), s(72, "x") + "y", s(70, "é"), s(255, "z")];
const bcryptCases = [];
for (const salt of salts) {
  for (const password of passwords) {
    bcryptCases.push({ password, hash: bcrypt.hashSync(password, salt) });
  }
}

// ---------------------------------------------------------------- jsonwebtoken
const jwtKey = "auth-golden-test-key-0123456789abcdef"; // test key only, never the legacy one
const jwtIat = 1794650400; // 2026-11-14T10:40:00Z
const jwtPayload = {
  _id: "5f1e9a2b3c4d5e6f70819203",
  name: "Ana Administradora",
  email: "ana.admin@dib4t.local",
  roles: ["canSeeOrcamentos", null, "canSeeDefinicoes"],
  fieldOfCompetencies: "Desenvolvimento",
};
const jwtCases = [
  { payload: jwtPayload, token: jwt.sign({ ...jwtPayload, iat: jwtIat }, jwtKey) },
  // A user without name/email in the DB: Mongoose leaves them undefined, so they are not in the token.
  {
    payload: { _id: jwtPayload._id, roles: [], fieldOfCompetencies: "Desenvolvimento" },
    token: jwt.sign({ _id: jwtPayload._id, name: undefined, email: undefined, roles: [], fieldOfCompetencies: "Desenvolvimento", iat: jwtIat }, jwtKey),
  },
  { payload: { ...jwtPayload, name: "Bruno Maquinação \"Silva\" </script>" }, token: jwt.sign({ ...jwtPayload, name: "Bruno Maquinação \"Silva\" </script>", iat: jwtIat }, jwtKey) },
];

// ---------------------------------------------------------------- write
const out = {
  generatedBy: "tools/legacy/auth-golden.js",
  legacyPackages: versions,
  loginValidation: loginBodies.map(runLogin),
  email: emails.map(runEmail),
  bcrypt: bcryptCases,
  jwt: { key: jwtKey, iat: jwtIat, cases: jwtCases },
};
fs.mkdirSync(OUT_DIR, { recursive: true });
const file = path.join(OUT_DIR, "auth-golden.json");
fs.writeFileSync(file, JSON.stringify(out, null, 2) + "\n");
console.log(`wrote ${path.relative(REPO_ROOT, file)}: ${out.loginValidation.length} login bodies, ${out.email.length} e-mails, ` +
  `${out.bcrypt.length} bcrypt hashes, ${jwtCases.length} tokens`);
