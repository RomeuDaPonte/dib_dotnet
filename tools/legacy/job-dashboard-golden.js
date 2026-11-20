#!/usr/bin/env node
// Phase 5 step 1 (PLAN.md §6, §7.3, §10): golden file for the dashboard counters,
//   GET /api/job/getValidTryOnFirstTestCountersByYear/:year,
// derived FROM THE LEGACY CODE by running the route handler itself. Nothing is started: no database, no network, no
// legacy server, no SSH, no printer.
//
//   node tools/legacy/job-dashboard-golden.js [outDir]      (default tests/fixtures/legacy/jobs)
//
// Runs, from ../back-end (DIB4T_LEGACY_ROOT), unchanged, through the harness shared with the other job golden files
// (./job-harness.js: fixed clock 2026-03-15T10:20:30.123Z, TZ=UTC like the legacy production container):
//   - the Express handler of routes/job.js, with a recording `res` and express-async-errors' behavior;
//   - repositories/jobRepository.js's getValidTryOnFirstTestCountersByYear: its three
//     Job.find(filter).countDocuments(), where the filter is cast by Mongoose 5.13 itself (Query.prototype.cast: an
//     Invalid Date is the CastError the legacy route answers with Express's 500) and counted by an in-memory store
//     with MongoDB's semantics for the three conditions ($in of strings; $gte/$lte/$lt only match BSON dates, so a
//     missing, null or text startDate never matches; equality with the text "1" does not match the number 1).
// The store is this file's own 24 jobs (`jobs`; the shared budgets, tasks, entity and user are kept as they are).
// Every case is run twice: `legacy` as is, and `fixed` = the legacy code with D10 fixed (ADR-0005): the repository's
// source with its range replaced by the whole calendar year in Europe/Lisbon, [1 Jan 00:00, 1 Jan next year 00:00),
// of the year `new Date(`${year}-01-01`)` gives (in UTC), and still an Invalid Date where legacy has one (a text
// either date of which is invalid).
// `startDate` of each outcome is the cast filter the first count sends (the other two differ only in the value).
// Output: <outDir>/job-dashboard-golden.json (README-dashboard.md is hand-written). Two runs give identical files.

"use strict";

process.env.TZ = "UTC";

const path = require("path");
const fs = require("fs");
const Module = require("module");
const { REPO_ROOT, BACKEND, RealDate, IDS, KNOWN_IDS, NAMES, legacyRequire, contacted, j, bsonValue, oid, run, goldenHeader } = require("./job-harness");

const OUT_DIR = path.resolve(process.argv[2] || path.join(REPO_ROOT, "tests/fixtures/legacy/jobs"));

const mongoose = legacyRequire("mongoose");
const { Job } = legacyRequire("./models/job");
const JobRepository = legacyRequire("./repositories/jobRepository");

// ---------------------------------------------------------------- the dashboard's jobs
const day = (iso) => new RealDate(iso);
const dashJobs = [];
function dashJob(name, { state, startDate, validTry = "1", omit = [], patch = {} }) {
  const n = (dashJobs.length + 1).toString(16).padStart(2, "0");
  const hex = "5f1e9a2b3c4d5e6f708b00" + n;
  const named = (prefix, id) => {
    KNOWN_IDS.set(id, `{{${prefix}-${name}}}`);
    NAMES.set(`{{${prefix}-${name}}}`, id);
    return oid(id);
  };
  named("dash", hex);
  const doc = {
    _id: oid(hex),
    invoicedPercent: state === "Faturada" ? 100 : 0,
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
    budget: oid(IDS["budget-dev"]),
    createdBy: "Ana Administradora",
    dueDate: day("2030-02-10T00:00:00.000Z"),
    startDate,
    devStatistic: { numberOfHoursUsed: 0, budgetedHours: 20.5, deviation: 20.5, _id: named("dash-dev", "5f1e9a2b3c4d5e6f708c00" + n) },
    machiningStatistic: {
      numberOfHoursUsed: 0, budgetedHours: 15, deviation: 15,
      verificationAndShapeControl: "n/a", dimensionalVerificationAndControl: "n/a", analysisOfTheDegreeOfFinish: "n/a",
      _id: named("dash-mach", "5f1e9a2b3c4d5e6f708d00" + n),
    },
    assemblyStatistic: {
      numberOfHoursUsed: 0, budgetedHours: 10, deviation: 10,
      pneumaticCircuits: "n/a", electricalBoardsAndCircuits: "n/a", kinematics: "n/a", programmingFeaturesPLC: "n/a",
      operator_equipmentSafetyConditions: "n/a", visualInspection: "n/a", validTryOnFirstTest: validTry,
      _id: named("dash-asm", "5f1e9a2b3c4d5e6f708e00" + n),
    },
    priority: "Média",
    __v: 0,
    ...patch,
  };
  for (const key of omit) delete doc[key];
  dashJobs.push(doc);
}

const INV = "Para faturação";
const BILLED = "Faturada";
// 2026: the year of the fixed clock
dashJob("inv-feb", { state: INV, startDate: day("2026-02-02T08:00:00.000Z"), validTry: "1" });
dashJob("billed-jun", { state: BILLED, startDate: day("2026-06-15T10:00:00.000Z"), validTry: "2" });
dashJob("billed-aug", { state: BILLED, startDate: day("2026-08-01T00:30:00.000Z"), validTry: "3" });
dashJob("inv-jan1", { state: INV, startDate: day("2026-01-01T00:00:00.000Z"), validTry: "2" });
dashJob("billed-dec31-midnight", { state: BILLED, startDate: day("2026-12-31T00:00:00.000Z"), validTry: "3" }); // legacy $lte: in
dashJob("billed-dec31-noon", { state: BILLED, startDate: day("2026-12-31T12:00:00.000Z"), validTry: "1" }); // D10: legacy out
dashJob("inv-dec31-late", { state: INV, startDate: day("2026-12-31T23:59:59.999Z"), validTry: "2" }); // D10: legacy out
dashJob("billed-2027", { state: BILLED, startDate: day("2027-01-01T00:00:00.000Z"), validTry: "3" });
dashJob("billed-2025-last-ms", { state: BILLED, startDate: day("2025-12-31T23:59:59.999Z"), validTry: "1" });
// not counted: other states, other values or types, no statistic, no date
dashJob("assembly", { state: "Montagem", startDate: day("2026-03-01T00:00:00.000Z"), validTry: "1" });
dashJob("deleted", { state: "Apagada", startDate: day("2026-03-01T00:00:00.000Z"), validTry: "2" });
dashJob("dev", { state: "Desenvolvimento", startDate: day("2026-03-01T00:00:00.000Z"), validTry: "3" });
dashJob("lower-state", { state: "faturada", startDate: day("2026-03-01T00:00:00.000Z"), validTry: "1" });
dashJob("number-1", { state: BILLED, startDate: day("2026-04-01T00:00:00.000Z"), validTry: 1 });
dashJob("value-4", { state: INV, startDate: day("2026-04-01T00:00:00.000Z"), validTry: "4" });
dashJob("no-assembly", { state: BILLED, startDate: day("2026-04-01T00:00:00.000Z"), omit: ["assemblyStatistic"] });
dashJob("no-stats", { state: INV, startDate: day("2026-04-01T00:00:00.000Z"), omit: ["devStatistic", "machiningStatistic", "assemblyStatistic"] });
dashJob("start-null", { state: INV, startDate: null, validTry: "1" });
dashJob("start-missing", { state: INV, startDate: undefined, validTry: "1", omit: ["startDate"] });
dashJob("start-text", { state: BILLED, startDate: "2026-05-01T00:00:00.000Z", validTry: "2" });
// 1995: Lisbon on CET (UTC+1) all year, so its year is [1994-12-31T23:00Z, 1995-12-31T23:00Z)
dashJob("billed-1995-lisbon-new-year", { state: BILLED, startDate: day("1994-12-31T23:30:00.000Z"), validTry: "2" }); // D10: 1995
dashJob("billed-1995-jun", { state: BILLED, startDate: day("1995-06-01T00:00:00.000Z"), validTry: "1" });
dashJob("billed-1995-dec31", { state: BILLED, startDate: day("1995-12-31T22:00:00.000Z"), validTry: "3" }); // D10: in
dashJob("billed-1996-lisbon-new-year", { state: BILLED, startDate: day("1995-12-31T23:30:00.000Z"), validTry: "3" }); // D10: 1996

// ---------------------------------------------------------------- Job.find: Mongoose's cast, MongoDB's matching
let firstFilter;
const fieldOf = (doc, key) => key.split(".").reduce((v, k) => (v === null || v === undefined || typeof v !== "object" ? undefined : v[k]), doc);
const isDate = (v) => v instanceof RealDate;

function matchesCondition(value, cond) {
  if (cond !== null && typeof cond === "object" && !isDate(cond) && !Array.isArray(cond)) {
    for (const [op, arg] of Object.entries(cond)) {
      if (op === "$in") {
        if (!arg.some((a) => a === value)) return false;
      } else if (op === "$gte" || op === "$lte" || op === "$lt") {
        if (!isDate(arg)) throw new Error("unexpected range operand " + arg);
        if (!isDate(value)) return false; // MongoDB compares a date only with dates (missing, null, text: no match)
        const [x, y] = [value.getTime(), arg.getTime()];
        if ((op === "$gte" && !(x >= y)) || (op === "$lte" && !(x <= y)) || (op === "$lt" && !(x < y))) return false;
      } else throw new Error("unexpected operator " + op);
    }
    return true;
  }
  if (typeof cond !== "string") throw new Error("unexpected condition " + cond);
  return value === cond; // a stored number 1 is not the text "1"
}

Job.find = (filter) => {
  const query = mongoose.Model.find.call(Job, filter); // a real Mongoose query: its cast is the one the route's count sends
  return {
    countDocuments: async () => {
      const cast = query.cast(Job); // throws Mongoose's CastError for an Invalid Date
      if (firstFilter === undefined) firstFilter = bsonValue(cast.startDate);
      return dashJobs.filter((doc) => Object.entries(cast).every(([key, cond]) => matchesCondition(fieldOf(doc, key), cond))).length;
    },
  };
};

// ---------------------------------------------------------------- D10: the legacy repository with the year fixed
const lisbon = new Intl.DateTimeFormat("en-GB", {
  timeZone: "Europe/Lisbon", hourCycle: "h23", era: "short", year: "numeric", month: "numeric", day: "numeric", hour: "numeric", minute: "numeric", second: "numeric",
});
const utcMidnight = (year) => {
  const d = new RealDate(0);
  d.setUTCFullYear(year, 0, 1);
  return d.getTime();
};
/** The zone's offset (ms) at a UTC instant of the years 1–9999. */
function offsetAt(utc) {
  const p = Object.fromEntries(lisbon.formatToParts(new RealDate(utc)).map((x) => [x.type, x.value]));
  const wall = new RealDate(0);
  wall.setUTCFullYear(p.era === "BC" ? 1 - p.year : +p.year, +p.month - 1, +p.day); // 1 BC is the year 0
  wall.setUTCHours(+p.hour, +p.minute, +p.second, new RealDate(utc).getUTCMilliseconds());
  return wall.getTime() - utc;
}
/** 1 January 00:00 in Lisbon as UTC ms; a year outside 1–9999 takes the offset of the nearest such year's 1 January. */
function lisbonNewYear(year) {
  const near = Math.min(Math.max(year, 1), 9999);
  const guess = utcMidnight(near);
  const offset = offsetAt(guess - offsetAt(guess));
  return utcMidnight(year) - offset;
}
global.__dib4tD10 = {
  range(year, next) {
    const start = new Date(`${year}-01-01`);
    const end = new Date(`${year}-12-31`);
    if (isNaN(start.getTime()) || isNaN(end.getTime())) return new Date(NaN); // the legacy cast error stays
    return new Date(lisbonNewYear(start.getUTCFullYear() + (next ? 1 : 0)));
  },
};
function d10Repository() {
  const file = path.join(BACKEND, "repositories/jobRepository.js");
  let source = fs.readFileSync(file, "utf8");
  const replace = (from, to) => {
    const count = source.split(from).length - 1;
    if (count !== 3) throw new Error(`expected 3 × ${from}, found ${count}`);
    source = source.split(from).join(to);
  };
  replace("$gte: new Date(`${year}-01-01`), // Start of the year", "$gte: __dib4tD10.range(year, false),");
  replace("$lte: new Date(`${year}-12-31`), // End of the year", "$lt: __dib4tD10.range(year, true),");
  const m = new Module(file + "#d10", module);
  m.filename = file;
  m.paths = Module._nodeModulePaths(path.dirname(file));
  m._compile(source, file);
  return m.exports.getValidTryOnFirstTestCountersByYear;
}
const legacyCounters = JobRepository.getValidTryOnFirstTestCountersByYear;
const fixedCounters = d10Repository();

// ---------------------------------------------------------------- cases
const years = [
  // ISO years (the legacy UI sends `new Date().getFullYear()`)
  "2026", "2025", "2027", "1995", "1996", "1994", "2001", "1912", "9998", "9999", "+002026", "0000", "-000001", "+010000",
  // Invalid Dates: Mongoose's CastError, Express's 500
  "26", "2026x", "2026 ", "2026.5", "2026-01", "+275760", "275760",
  // texts only V8's fallback date parser reads (not ported: Application/Common/Validation/JsDate.cs)
  "abc", "0", " 2026", "10000", "-000000",
];

async function runCase(year, counters) {
  firstFilter = undefined;
  JobRepository.getValidTryOnFirstTestCountersByYear = counters;
  try {
    const outcome = await run({ method: "get", route: "/getValidTryOnFirstTestCountersByYear/:year", params: { year } });
    return firstFilter === undefined ? outcome : { ...outcome, startDate: firstFilter };
  } finally {
    JobRepository.getValidTryOnFirstTestCountersByYear = legacyCounters;
  }
}

// ---------------------------------------------------------------- write
(async () => {
  const golden = goldenHeader("tools/legacy/job-dashboard-golden.js");
  golden.jobs = dashJobs.map(bsonValue);
  for (const year of years) {
    golden.cases.push({
      section: "counters",
      year,
      method: "GET",
      path: "/api/job/getValidTryOnFirstTestCountersByYear/" + year,
      body: null,
      legacy: await runCase(year, legacyCounters),
      fixed: await runCase(year, fixedCounters),
    });
  }
  if (contacted.length) throw new Error("unexpected NAS/printer contact: " + contacted.join(" | "));

  fs.mkdirSync(OUT_DIR, { recursive: true });
  const file = path.join(OUT_DIR, "job-dashboard-golden.json");
  fs.writeFileSync(file, JSON.stringify(golden, null, 2) + "\n");
  const differs = golden.cases.filter((c) => j(c.legacy) !== j(c.fixed)).length;
  console.log(`wrote ${path.relative(REPO_ROOT, file)}: ${golden.cases.length} cases (${differs} changed by D10), ${dashJobs.length} jobs`);
  process.exit(0);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
