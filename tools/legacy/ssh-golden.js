#!/usr/bin/env node
// Phase 0 (PLAN.md §5, §5.1, §10): SSH command golden files for the NAS folder operations,
// derived FROM THE LEGACY CODE by running it, not from captures. The NAS is never contacted.
//
//   tools/legacy/legacy.sh ssh-golden          (starts the legacy mongo:4.1.4 if needed)
//   node tools/legacy/ssh-golden.js [outDir]   (mongo must answer on 127.0.0.1:27017)
//
// How: the UNCHANGED legacy Express routers (routes/budget.js, routes/job.js) and models run in this
// process against a throwaway database (dib4t_ssh_golden on the legacy mongo, dropped before every
// scenario and at the end; the "dib4t" database is never touched). Only three things are replaced:
//   - ssh2-promise  → a recorder that runs each command on a VIRTUAL file system (see vfsExec) and
//                     answers like ssh2-promise 1.0.1 exec(): resolves stdout on close, REJECTS on the
//                     first stderr data. The SSH credentials from config/default.json are not read.
//   - telnet-client → a recorder of the label printer payloads (no socket).
//   - the clock     → a virtual "now" per step (Date shifted, still advancing), TZ=Europe/Lisbon.
// NODE_ENV is set per step ("prod" or unset), because the routes read it at request time.
//
// Output: <outDir>/ssh-commands.json (+ README.md is hand-written). Deterministic: no ids, no real
// times; the only inputs are the scenarios below.

"use strict";

process.env.TZ = "Europe/Lisbon";

const path = require("path");
const fs = require("fs");
const http = require("http");
const Module = require("module");

const REPO_ROOT = path.resolve(__dirname, "../..");
const BACKEND = path.resolve(process.env.DIB4T_LEGACY_ROOT || path.join(REPO_ROOT, ".."), "back-end");
const OUT_DIR = path.resolve(process.argv[2] || path.join(REPO_ROOT, "tests/fixtures/legacy/ssh"));
const MONGO_URL = process.env.SSH_GOLDEN_MONGO || "mongodb://127.0.0.1:27017/dib4t_ssh_golden";
const SSH_HOME = "/home/ssh-user"; // cwd of every exec channel: the SSH user's home (not "/")

const legacyRequire = Module.createRequire(path.join(BACKEND, "index.js"));

// ---------------------------------------------------------------- virtual clock
// Schemas need the real Date class (mongoose maps `type: Date` by identity), so: Date.now is patched
// first (schemas capture it as `default: Date.now`), and the global Date is swapped for a subclass
// whose no-argument constructor uses the virtual clock only AFTER the models are loaded.
const RealDate = Date;
const realNow = RealDate.now.bind(RealDate);
let clockBase = realNow(); // virtual ms at clockSetAt
let clockSetAt = realNow();
const virtualNow = () => clockBase + (realNow() - clockSetAt);
RealDate.now = virtualNow;
class FakeDate extends RealDate {
  constructor(...a) {
    if (a.length === 0) super(virtualNow());
    else super(...a);
  }
  static now() { return virtualNow(); }
  static [Symbol.hasInstance](v) { return v instanceof RealDate; }
}
Object.defineProperty(FakeDate, "name", { value: "Date" });
const installFakeDate = () => { global.Date = FakeDate; };
function setClock(localIso) { // "2026-11-14T10:00:00" in Europe/Lisbon
  clockBase = new RealDate(localIso).getTime(); // TZ is set, so this parses as local time
  clockSetAt = realNow();
}

// ---------------------------------------------------------------- event recording
let events = [];
const record = (e) => events.push(e);

// ---------------------------------------------------------------- POSIX sh word parser (no expansion)
// Splits a command line the way sh does for the constructs legacy produces: words, '...', "...",
// backslash, ';'. Anything that sh would EXPAND outside single quotes ($, `, globs, ~, |&<>()) is
// reported in `expansions` and kept literally, so nothing is ever executed or expanded here.
function shParse(line) {
  const commands = [];
  let argv = [];
  let word = null;
  const expansions = [];
  let i = 0;
  const flush = () => { if (word !== null) { argv.push(word); word = null; } };
  const endCommand = () => { flush(); if (argv.length) commands.push(argv); argv = []; };
  while (i < line.length) {
    const c = line[i];
    if (c === "'") {
      const j = line.indexOf("'", i + 1);
      if (j < 0) return { error: `unterminated single quote at offset ${i}`, expansions };
      word = (word ?? "") + line.slice(i + 1, j);
      i = j + 1;
    } else if (c === '"') {
      let j = i + 1;
      let s = "";
      for (;;) {
        if (j >= line.length) return { error: `unterminated double quote at offset ${i}`, expansions };
        const d = line[j];
        if (d === '"') break;
        if (d === "\\" && j + 1 < line.length && '$`"\\\n'.includes(line[j + 1])) { s += line[j + 1]; j += 2; continue; }
        if (d === "$" || d === "`") expansions.push({ char: d, offset: j, quoted: "double" });
        s += d;
        j++;
      }
      word = (word ?? "") + s;
      i = j + 1;
    } else if (c === "\\") {
      if (i + 1 < line.length) word = (word ?? "") + line[i + 1];
      i += 2;
    } else if (c === ";") {
      endCommand();
      i++;
    } else if (c === " " || c === "\t" || c === "\n") {
      flush();
      i++;
    } else {
      if ("$`*?[|&<>()".includes(c) || (c === "~" && word === null) || (c === "#" && word === null))
        expansions.push({ char: c, offset: i, quoted: "none" });
      word = (word ?? "") + c;
      i++;
    }
  }
  endCommand();
  return { commands, expansions };
}

// ---------------------------------------------------------------- virtual NAS file system
let dirs = new Set();
let forceTestDZero = false; // scenario "NAS says the folder is missing": every test -d prints 0
function resetFs() {
  dirs = new Set(["/", "/home", SSH_HOME, "/share", "/share/Orçamentos", "/share/Obras"]);
}
function norm(p, cwd) {
  const abs = p.startsWith("/") ? p : `${cwd}/${p}`;
  const out = [];
  for (const seg of abs.split("/")) {
    if (seg === "" || seg === ".") continue;
    if (seg === "..") out.pop(); else out.push(seg);
  }
  return "/" + out.join("/");
}
const parentOf = (p) => (p === "/" ? "/" : p.slice(0, p.lastIndexOf("/")) || "/");
const baseOf = (p) => p.slice(p.lastIndexOf("/") + 1);

// Runs one exec() command line. Returns { stdout, stderr, ops } where ops are the effective
// operations with absolute paths: the contract the new app's INasFileShare must reproduce.
function vfsExec(line) {
  const parsed = shParse(line);
  if (parsed.error) return { parsed, stdout: "", stderr: `sh: syntax error: ${parsed.error}\n`, ops: [] };
  let cwd = SSH_HOME;
  let stdout = "";
  let stderr = "";
  const ops = [];
  const cmds = parsed.commands;
  for (let k = 0; k < cmds.length; k++) {
    const [name, ...args] = cmds[k];
    if (name === "if" && args[0] === "test" && args[1] === "-d" && args.length === 3) {
      // if test -d X; then echo 1; else echo 0; fi
      const p = norm(args[2], cwd);
      const exists = !forceTestDZero && dirs.has(p);
      ops.push({ op: "test-d", path: p, result: exists });
      const thenCmd = cmds[k + 1], elseCmd = cmds[k + 2], fi = cmds[k + 3];
      if (!thenCmd || thenCmd[0] !== "then" || !elseCmd || elseCmd[0] !== "else" || !fi || fi[0] !== "fi")
        throw new Error(`unsupported if-form: ${line}`);
      stdout += (exists ? thenCmd : elseCmd).slice(2).join(" ") + "\n"; // "echo 1" → "1"
      k += 3;
      continue;
    }
    switch (name) {
      case "cd": {
        const p = norm(args[0] ?? SSH_HOME, cwd);
        if (!dirs.has(p)) { stderr += `sh: cd: can't cd to ${args[0]}\n`; break; }
        cwd = p;
        break;
      }
      case "mkdir": {
        if (args[0] !== "-p" || args.length < 2) throw new Error(`unsupported mkdir: ${line}`);
        for (const a of args.slice(1)) {
          const p = norm(a, cwd);
          ops.push({ op: "mkdir-p", path: p });
          for (let q = p; !dirs.has(q); q = parentOf(q)) dirs.add(q);
        }
        break;
      }
      case "mv": {
        if (args.length !== 2) {
          ops.push({ op: "mv", argc: args.length, args: args.map((a) => norm(a, cwd)), result: "usage-error" });
          stderr += `mv: expected 2 operands, got ${args.length}\n`;
          break;
        }
        const from = norm(args[0], cwd);
        let to = norm(args[1], cwd);
        if (!dirs.has(from)) {
          ops.push({ op: "mv", from, to, result: "source-missing" });
          stderr += `mv: cannot stat '${args[0]}': No such file or directory\n`;
          break;
        }
        if (dirs.has(to)) to = `${to}/${baseOf(from)}`; // existing target dir: move INTO it
        if (!dirs.has(parentOf(to))) {
          ops.push({ op: "mv", from, to, result: "target-parent-missing" });
          stderr += `mv: cannot move '${args[0]}' to '${args[1]}': No such file or directory\n`;
          break;
        }
        ops.push({ op: "mv", from, to, result: "ok" });
        for (const d of [...dirs]) {
          if (d === from || d.startsWith(from + "/")) { dirs.delete(d); dirs.add(to + d.slice(from.length)); }
        }
        break;
      }
      default:
        throw new Error(`unsupported command '${name}' in: ${line}`);
    }
  }
  return { parsed, stdout, stderr, ops };
}

// ---------------------------------------------------------------- module stubs (before any legacy require)
function stubModule(request, fromFile, exportsValue) {
  const resolved = Module.createRequire(fromFile).resolve(request);
  const m = new Module(resolved);
  m.filename = resolved;
  m.loaded = true;
  m.exports = exportsValue;
  require.cache[resolved] = m;
}

class SshRecorder {
  constructor() { /* credentials ignored on purpose: never read, never recorded */ }
  exec(cmd) {
    const r = vfsExec(cmd);
    const ev = { type: "ssh", command: cmd };
    if (r.parsed.error) ev.parseError = r.parsed.error;
    else ev.argv = r.parsed.commands;
    if (r.parsed.expansions && r.parsed.expansions.length) ev.shellExpansions = r.parsed.expansions;
    ev.ops = r.ops;
    if (r.stdout) ev.stdout = r.stdout;
    if (r.stderr) ev.stderr = r.stderr;
    ev.outcome = r.stderr ? "rejects (stderr)" : "resolves";
    record(ev);
    // ssh2-promise 1.0.1: resolve(buffer) on 'close', reject(data) on the first stderr 'data'.
    return r.stderr ? Promise.reject(Buffer.from(r.stderr)) : Promise.resolve(r.stdout);
  }
}
stubModule("ssh2-promise", path.join(BACKEND, "helpers/pathHlelper.js"), SshRecorder);

class TelnetRecorder {
  async connect(params) { this.params = params; }
  async send(data) {
    const wire = data + "\n"; // telnet-client 1.4.9: ors "\n" appended, UTF-8
    record({
      type: "printer",
      host: this.params.host,
      port: this.params.port,
      connectTimeoutMs: this.params.timeout,
      negotiationMandatory: this.params.negotiationMandatory,
      send: data,
      wireUtf8Bytes: Buffer.byteLength(wire, "utf8"),
      wireHex: Buffer.from(wire, "utf8").toString("hex"), // starts with 1b ("ESC i a 3": P-touch template mode)
    });
    return ""; // the real client waits 2000 ms and rejects "response not received" (ignored by callers)
  }
  async end() {}
}
stubModule("telnet-client", path.join(BACKEND, "models/printerSettings.js"), TelnetRecorder);

// ---------------------------------------------------------------- legacy app (unchanged code)
const mongoose = legacyRequire("mongoose");
legacyRequire("express-async-errors"); // startup/logging.js does this
const express = legacyRequire("express");

for (const fn of ["save"]) {
  const orig = mongoose.Model.prototype[fn];
  mongoose.Model.prototype[fn] = function (...a) {
    record({ type: "db", op: `${this.constructor.modelName}.${fn}`, ...(this.revision !== undefined && this.budgetNumber ? { budgetNumber: this.budgetNumber, revision: this.revision } : {}) });
    return orig.apply(this, a);
  };
}
const origInsertMany = mongoose.Model.insertMany;
mongoose.Model.insertMany = function (docs, ...a) {
  record({ type: "db", op: `${this.modelName}.insertMany`, count: Array.isArray(docs) ? docs.length : 1 });
  return origInsertMany.call(this, docs, ...a);
};

const { Entity } = legacyRequire("./models/entity");
const { User } = legacyRequire("./models/user");
const { Prices } = legacyRequire("./models/prices");
const { PrinterSettings } = legacyRequire("./models/printerSettings");
const { Budget } = legacyRequire("./models/budget");
const { Task } = legacyRequire("./models/task");

const app = express();
app.use(express.json());
app.use((req, _res, next) => { req.user = { name: "Utilizador Golden" }; next(); });
app.use("/api/budget", legacyRequire("./routes/budget"));
app.use("/api/job", legacyRequire("./routes/job"));
installFakeDate(); // all legacy modules are loaded now
app.use((err, _req, _res, next) => { // records, then hands over to Express's default handler (D16)
  record({ type: "error", name: Buffer.isBuffer(err) ? "Buffer (ssh2-promise stderr rejection)" : err && err.name, message: String(err && err.message ? err.message : err) });
  next(err);
});

let server;
let baseUrl;
function call(method, url, body) {
  return new Promise((resolve, reject) => {
    const data = body === undefined ? null : Buffer.from(JSON.stringify(body));
    const req = http.request(baseUrl + url, {
      method,
      headers: data ? { "content-type": "application/json", "content-length": data.length } : {},
    }, (res) => {
      const chunks = [];
      res.on("data", (c) => chunks.push(c));
      res.on("end", () => resolve({ status: res.statusCode, type: res.headers["content-type"] || "", text: Buffer.concat(chunks).toString("utf8") }));
    });
    req.on("error", reject);
    if (data) req.write(data);
    req.end();
  });
}

// ---------------------------------------------------------------- fixed seed data
const oid = (n) => new mongoose.Types.ObjectId(n.toString(16).padStart(24, "0"));
const ENTITY_TYPE_CLIENT = legacyRequire("./helpers/entityTypes").entityTypes.client;
const CLIENTS = {
  silva: { _id: oid(0x101), name: "Metalúrgica Silva & Filhos, Lda." },
  apostrophe: { _id: oid(0x102), name: "Pastelaria D'Ouro" },
  nfd: { _id: oid(0x103), name: "Fundição Norte" }, // non-NFC on purpose (§4.5)
  shellish: { _id: oid(0x104), name: "Ferragens $HOME `id` (Porto)" },
};
const USER = { _id: oid(0x201), name: "Utilizador Golden", email: "golden@dib4t.local", password: "$2a$10$abcdefghijklmnopqrstuuJ0yQ9pQe5mVQm1tq7ZyH8oW8x8Qy5mS" };

async function seed() {
  await Prices.create({ automation: 50, consultancy: 40, development: 30, margin: 30, machining: 35, assembly: 25 });
  for (const c of Object.values(CLIENTS))
    await Entity.create({ ...c, type: ENTITY_TYPE_CLIENT, paymentCondition: "30 dias" });
  await new User(USER).save({ validateBeforeSave: false });
  await PrinterSettings.create({ ip: "127.0.0.1", port: 9100, templateName: "producao" });
  await PrinterSettings.create({ ip: "127.0.0.2", port: 9100, templateName: "maquinacao" });
}

// ---------------------------------------------------------------- scenario DSL
// A scenario is a list of steps on a fresh DB and a fresh virtual NAS. Steps can reference
// budgets created by earlier steps through `ref` names (their ids are never written out).
const refs = {};
const budgetBody = (client, descriptive) => ({ clientId: CLIENTS[client]._id.toString(), madeById: USER._id.toString(), descriptive, personInCharge: "Eng. Teste" });
const S = {
  createBudget: (ref, client, descriptive, opts = {}) => ({ ...opts, ref, label: `POST /api/budget (${client}, "${descriptive}")`, method: "POST", url: () => "/api/budget", body: () => budgetBody(client, descriptive) }),
  cloneBudget: (ref, from, client, descriptive, opts = {}) => ({ ...opts, ref, label: `POST /api/budget/clone/{${from}}`, method: "POST", url: () => `/api/budget/clone/${refs[from]}`, body: () => budgetBody(client, descriptive) }),
  newRevision: (ref, from, opts = {}) => ({ ...opts, ref, label: `POST /api/budget/newrevision/{${from}}`, method: "POST", url: () => `/api/budget/newrevision/${refs[from]}` }),
  updateBudget: (target, descriptive, opts = {}) => ({ ...opts, label: `PUT /api/budget/{${target}} (descriptive "${descriptive}")`, method: "PUT", url: () => `/api/budget/${refs[target]}`, body: () => ({ madeById: USER._id.toString(), descriptive, personInCharge: "Eng. Teste" }) }),
  createJob: (from, opts = {}) => ({ ...opts, label: `POST /api/job (from {${from}})`, method: "POST", url: () => "/api/job", body: () => ({ budgetId: refs[from], finalPrice: 1000, dueDate: "2027-02-01", state: legacyRequire("./helpers/jobStates").jobStates.development, orderIdentification: "PO-1", isOfficialOrderIdentification: true }) }),
};

const SCENARIOS = [
  {
    id: "budget-create",
    title: "Budget create (prod): 3 × mkdir -p, then test -d",
    steps: [S.createBudget("b", "silva", "Máquina de corte", { at: "2026-11-14T10:00:00" })],
  },
  {
    id: "budget-create-dev",
    title: "Budget create outside prod: no SSH",
    steps: [S.createBudget("b", "silva", "Máquina de corte", { at: "2026-11-14T10:00:00", env: "development" })],
  },
  {
    id: "budget-create-folder-missing",
    title: "Budget create (prod), NAS answers 0 to test -d: 500 \"Erro ao criar pasta\", budget saved without its default task (D6)",
    nasFailsTestD: true,
    steps: [S.createBudget("b", "silva", "Máquina de corte", { at: "2026-11-14T10:00:00" })],
  },
  {
    id: "budget-create-slash-in-descriptive",
    title: "Descriptive with '/': legacy creates NESTED folders (the path keeps the slash)",
    steps: [S.createBudget("b", "silva", "Suporte 30/40 mm", { at: "2026-11-14T10:00:00" })],
  },
  {
    id: "budget-create-quotes-and-spaces",
    title: "Double quotes, %, double and trailing spaces inside single quotes: kept literally",
    steps: [S.createBudget("b", "silva", 'Molde "especial"  100% ', { at: "2026-11-14T10:00:00" })],
  },
  {
    id: "budget-create-shell-chars-in-client",
    title: "$, backticks and parentheses inside single quotes: literal (no expansion) in legacy",
    steps: [S.createBudget("b", "shellish", "Peça $1", { at: "2026-11-14T10:00:00" })],
  },
  {
    id: "budget-create-non-nfc-client",
    title: "Non-NFC client name (combining marks): bytes passed through unchanged",
    steps: [S.createBudget("b", "nfd", "Caixa", { at: "2026-11-14T10:00:00" })],
  },
  {
    id: "budget-create-apostrophe-in-client",
    title: "Apostrophe in the client name breaks legacy quoting: sh syntax error → exec rejects → 500 (budget saved, no task, no folder)",
    steps: [S.createBudget("b", "apostrophe", "Vitrine refrigerada", { at: "2026-11-14T10:00:00" })],
  },
  {
    id: "budget-create-apostrophe-in-descriptive",
    title: "Apostrophe in the descriptive: same quoting failure",
    steps: [S.createBudget("b", "silva", "Tampa O'Neill", { at: "2026-11-14T10:00:00" })],
  },
  {
    id: "budget-clone",
    title: "Budget clone (prod): new number, folder in the CURRENT year, same commands as create",
    steps: [
      S.createBudget("a", "silva", "Máquina de corte", { at: "2025-06-02T09:00:00" }),
      S.cloneBudget("b", "a", "silva", "Máquina de corte v2", { at: "2026-11-14T10:00:00" }),
    ],
  },
  {
    id: "budget-rename-rev0",
    title: "PUT on revision 0 with a new descriptive: cd /;mv old new, year from budget.date",
    steps: [
      S.createBudget("b", "silva", "Máquina de corte", { at: "2025-12-30T16:00:00" }),
      S.updateBudget("b", "Máquina de corte CNC", { at: "2026-01-05T09:00:00" }),
    ],
  },
  {
    id: "budget-rename-rev0-dev",
    title: "Same PUT outside prod: legacy still runs SSH (D5, not gated)",
    steps: [
      S.createBudget("b", "silva", "Máquina de corte", { at: "2026-11-14T10:00:00", env: "development" }),
      S.updateBudget("b", "Máquina de corte CNC", { at: "2026-11-14T10:05:00", env: "development" }),
    ],
  },
  {
    id: "budget-rename-same-descriptive",
    title: "PUT on revision 0, descriptive unchanged: no SSH",
    steps: [
      S.createBudget("b", "silva", "Máquina de corte", { at: "2026-11-14T10:00:00" }),
      S.updateBudget("b", "Máquina de corte", { at: "2026-11-14T10:05:00" }),
    ],
  },
  {
    id: "budget-rename-revision",
    title: "PUT on revision 1 with a new descriptive: no SSH (only revision 0 renames)",
    steps: [
      S.createBudget("b0", "silva", "Máquina de corte", { at: "2026-11-14T10:00:00" }),
      S.newRevision("b1", "b0", { at: "2026-11-14T10:01:00" }),
      S.updateBudget("b1", "Máquina de corte (rev. 1)", { at: "2026-11-14T10:02:00" }),
    ],
  },
  {
    id: "budget-rename-slash",
    title: "Rename to a descriptive with '/': mv into a folder whose parent does not exist → fails",
    steps: [
      S.createBudget("b", "silva", "Suporte", { at: "2026-11-14T10:00:00" }),
      S.updateBudget("b", "Suporte 30/40", { at: "2026-11-14T10:05:00" }),
    ],
  },
  {
    id: "job-create-rev0",
    title: "Job create from revision 0 (prod): 3 × mkdir -p, 2 × mv, 2 × test -d (not awaited, D4), labels producao then maquinacao",
    steps: [
      S.createBudget("b", "silva", "Máquina de corte", { at: "2026-11-02T10:00:00" }),
      S.createJob("b", { at: "2026-11-14T10:00:00" }),
    ],
  },
  {
    id: "job-create-dev",
    title: "Job create outside prod: no SSH, no labels",
    steps: [
      S.createBudget("b", "silva", "Máquina de corte", { at: "2026-11-02T10:00:00", env: "development" }),
      S.createJob("b", { at: "2026-11-14T10:00:00", env: "development" }),
    ],
  },
  {
    id: "job-create-rev1-same-descriptive",
    title: "Job create from revision 1 with the revision-0 descriptive: updateFolderPath returns early, same move",
    steps: [
      S.createBudget("b0", "silva", "Máquina de corte", { at: "2026-11-02T10:00:00" }),
      S.newRevision("b1", "b0", { at: "2026-11-05T10:00:00" }),
      S.createJob("b1", { at: "2026-11-14T10:00:00" }),
    ],
  },
  {
    id: "job-create-rev1-renamed",
    title: "Job create from revision 1 whose descriptive differs from revision 0: ReferenceError → 500 after the job is saved, no SSH, no labels (D3)",
    steps: [
      S.createBudget("b0", "silva", "Máquina de corte", { at: "2026-11-02T10:00:00" }),
      S.newRevision("b1", "b0", { at: "2026-11-05T10:00:00" }),
      S.updateBudget("b1", "Máquina de corte (rev. 1)", { at: "2026-11-05T10:01:00" }),
      S.createJob("b1", { at: "2026-11-14T10:00:00" }),
    ],
  },
  {
    id: "job-create-after-rev0-rename",
    title: "Revision 0 renamed (folder renamed too), job from revision 0: the move uses the new name and works",
    steps: [
      S.createBudget("b", "silva", "Máquina de corte", { at: "2026-11-02T10:00:00" }),
      S.updateBudget("b", "Máquina de corte CNC", { at: "2026-11-03T10:00:00" }),
      S.createJob("b", { at: "2026-11-14T10:00:00" }),
    ],
  },
  {
    id: "job-create-rev1-later-year",
    title: "Revision 1 made in the next year: the move uses the REVISION's year, the source folder is missing → mv rejects → 500 after the job is saved, Obras folders already made",
    steps: [
      S.createBudget("b0", "silva", "Máquina de corte", { at: "2025-12-15T10:00:00" }),
      S.newRevision("b1", "b0", { at: "2026-01-10T10:00:00" }),
      S.createJob("b1", { at: "2026-01-12T10:00:00" }),
    ],
  },
  {
    id: "job-create-slash-in-descriptive",
    title: "Job create for a budget whose descriptive has '/': the nested folders are moved as one",
    steps: [
      S.createBudget("b", "silva", "Suporte 30/40 mm", { at: "2026-11-02T10:00:00" }),
      S.createJob("b", { at: "2026-11-14T10:00:00" }),
    ],
  },
];

// ---------------------------------------------------------------- run
function fsSnapshot() {
  return [...dirs].filter((d) => d.startsWith("/share/") && d !== "/share/Orçamentos" && d !== "/share/Obras").sort();
}

function summarizeResponse(res) {
  const out = { status: res.status };
  if (/^<!DOCTYPE html>/i.test(res.text)) out.body = "expressErrorPage"; // D16: stack trace page, not kept
  else if (res.status >= 300 || res.type.startsWith("text/")) out.body = res.text;
  else {
    const j = JSON.parse(res.text);
    out.body = "json";
    if (j && typeof j === "object" && !Array.isArray(j)) {
      const keep = {};
      for (const k of ["budgetNumber", "revision", "descriptive", "hasGenerateJob", "state"]) if (k in j) keep[k] = j[k];
      out.fields = keep;
    }
  }
  return out;
}

async function runScenario(sc) {
  events = []; // detach from the previous step's list before seeding
  await mongoose.connection.db.dropDatabase();
  await seed();
  resetFs();
  for (const k of Object.keys(refs)) delete refs[k];
  const steps = [];
  for (const st of sc.steps) {
    setClock(st.at);
    if ((st.env || "prod") === "prod") process.env.NODE_ENV = "prod"; else delete process.env.NODE_ENV;
    events = [];
    const fsBefore = new Set(dirs);
    const res = await call(st.method, st.url(), st.body ? st.body() : undefined);
    // un-awaited exec() calls (D4) are recorded synchronously, so they are already in `events`.
    await new Promise((r) => setImmediate(r));
    if (process.env.SSH_GOLDEN_DEBUG && res.status >= 400) process.stderr.write(`${st.label}: ${res.status} ${res.text.slice(0, 300)}\n`);
    if (st.ref && res.status === 200) refs[st.ref] = JSON.parse(res.text)._id;
    const dbAfter = {
      budgets: await Budget.countDocuments(),
      tasks: await Task.countDocuments(),
      jobs: await mongoose.connection.db.collection("jobs").countDocuments(),
    };
    const created = [...dirs].filter((d) => !fsBefore.has(d)).sort();
    const removed = [...fsBefore].filter((d) => !dirs.has(d)).sort();
    steps.push({
      step: st.label,
      at: st.at + " Europe/Lisbon",
      NODE_ENV: (st.env || "prod") === "prod" ? "prod" : null,
      response: summarizeResponse(res),
      events,
      dbAfter,
      nasChanges: { created, removed },
    });
  }
  return { id: sc.id, title: sc.title, ...(sc.nasFailsTestD ? { nas: "answers 0 to every test -d (folder not found)" } : {}), steps, nasFoldersAtEnd: fsSnapshot() };
}

async function main() {
  if (!fs.existsSync(BACKEND)) throw new Error(`legacy back-end not found at ${BACKEND}`);
  await mongoose.connect(MONGO_URL, { useNewUrlParser: true, useUnifiedTopology: true, serverSelectionTimeoutMS: 5000 });
  if (mongoose.connection.db.databaseName === "dib4t") throw new Error("refusing to use the legacy 'dib4t' database");
  server = http.createServer(app);
  await new Promise((r) => server.listen(0, "127.0.0.1", r));
  baseUrl = `http://127.0.0.1:${server.address().port}`;

  const results = [];
  for (const sc of SCENARIOS) {
    forceTestDZero = !!sc.nasFailsTestD;
    results.push(await runScenario(sc));
    process.stderr.write(`[ssh-golden] ${sc.id}\n`);
  }
  forceTestDZero = false;

  await mongoose.connection.db.dropDatabase();
  await mongoose.disconnect();
  server.close();

  const doc = {
    description: "Expected NAS SSH command sequences, derived by running the unchanged legacy routes with a recording SSH client on a virtual file system (PLAN.md §5, §5.1). See README.md.",
    generator: "tools/legacy/ssh-golden.js",
    legacySources: ["back-end/helpers/pathHlelper.js", "back-end/models/budget.js", "back-end/routes/budget.js", "back-end/routes/job.js", "back-end/models/printerSettings.js"],
    sshClient: "ssh2-promise 1.0.1 exec(): one channel per call, cwd = SSH user's home (" + SSH_HOME + " here), resolves stdout on close, rejects on the first stderr data",
    timeZone: "Europe/Lisbon",
    nasInitialFolders: ["/share/Orçamentos", "/share/Obras"],
    scenarios: results,
  };
  fs.mkdirSync(OUT_DIR, { recursive: true });
  fs.writeFileSync(path.join(OUT_DIR, "ssh-commands.json"), JSON.stringify(doc, null, 2) + "\n");
  process.stderr.write(`[ssh-golden] wrote ${path.join(OUT_DIR, "ssh-commands.json")}\n`);

  const dibbak = process.env.SSH_GOLDEN_DIBBAK || path.join(REPO_ROOT, "tests/fixtures/legacy/prod-2026-11-11.anon.dibbak");
  if (fs.existsSync(dibbak)) {
    const stats = folderNameStats(dibbak);
    fs.writeFileSync(path.join(OUT_DIR, "folder-name-chars.json"), JSON.stringify(stats, null, 2) + "\n");
    process.stderr.write(`[ssh-golden] wrote ${path.join(OUT_DIR, "folder-name-chars.json")} (from ${path.basename(dibbak)})\n`);
  }
}

// Counts only (no values): how many budget folder names in a real backup contain characters that
// matter for the NAS commands. The anonymized fixture keeps punctuation, spaces and accents, so its
// counts equal the original's.
function folderNameStats(file) {
  const zlib = require("zlib");
  const BSON = legacyRequire("bson"); // bson 1.x: instance API
  const bson = new BSON();
  const { parseBackup } = legacyRequire("./helpers/backupFormat");
  const parsed = parseBackup(zlib.gunzipSync(fs.readFileSync(file)));
  const docs = (name) => (parsed.collections.find((c) => c.name === name) || { documents: [] }).documents.map((b) => bson.deserialize(b));
  const entities = new Map(docs("entities").map((e) => [String(e._id), e]));
  const budgets = docs("budgets");
  const classes = {
    apostrophe: (s) => s.includes("'"),
    slash: (s) => s.includes("/"),
    backslash: (s) => s.includes("\\"),
    doubleQuote: (s) => s.includes('"'),
    dollarOrBacktick: (s) => /[$`]/.test(s),
    newlineOrTab: (s) => /[\n\r\t]/.test(s),
    leadingOrTrailingSpace: (s) => /^\s|\s$/.test(s),
    doubleSpace: (s) => s.includes("  "),
    windowsReserved: (s) => /[<>:"|?*]/.test(s),
    nonNfc: (s) => s !== s.normalize("NFC"),
    nonAscii: (s) => /[^\x00-\x7f]/.test(s),
  };
  const count = (values) => {
    const out = { total: values.length };
    for (const [k, f] of Object.entries(classes)) out[k] = values.filter((v) => typeof v === "string" && f(v)).length;
    return out;
  };
  const rev0 = budgets.filter((b) => b.revision === 0 || b.revision === undefined);
  const clientName = (b) => { const e = entities.get(String(b.client)); return e ? e.name : undefined; };
  const jobBudgetIds = new Set(docs("jobs").map((j) => String(j.budget)));
  const jobsFrom = (list) => list.filter((b) => jobBudgetIds.has(String(b._id))).length;
  const byNumber = new Map(rev0.map((b) => [b.budgetNumber, b]));
  const revWithOtherDescriptive = budgets.filter((b) => b.revision > 0 && byNumber.has(b.budgetNumber) && byNumber.get(b.budgetNumber).descriptive !== b.descriptive);
  const revInLaterYear = budgets.filter((b) => b.revision > 0 && byNumber.has(b.budgetNumber) && new RealDate(b.date).getFullYear() !== new RealDate(byNumber.get(b.budgetNumber).date).getFullYear());
  const yearMismatch = rev0.filter((b) => b.budgetNumber && String(new RealDate(b.date).getFullYear()).slice(2) !== b.budgetNumber.slice(0, 2));
  return {
    source: path.basename(file),
    note: "Counts only. Legacy creates the folder '{year}/{client}/{budgetNumber} - {descriptive}' from revision 0; job creation moves it using the revision the job was made from (its date year and descriptive).",
    clientNamesOfBudgets: count(rev0.map(clientName)),
    descriptivesRev0: count(rev0.map((b) => b.descriptive)),
    revisionsWithDescriptiveDifferentFromRev0: { revisions: revWithOtherDescriptive.length, jobsCreatedFromThem: jobsFrom(revWithOtherDescriptive) },
    revisionsDatedInAnotherYearThanRev0: { revisions: revInLaterYear.length, jobsCreatedFromThem: jobsFrom(revInLaterYear) },
    budgetsWithAJob: jobBudgetIds.size,
    rev0WhoseDateYearDiffersFromNumberPrefix: yearMismatch.length,
  };
}

main().catch((e) => { console.error(e); process.exit(1); });
