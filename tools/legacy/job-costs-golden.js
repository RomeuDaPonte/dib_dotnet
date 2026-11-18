#!/usr/bin/env node
// Phase 4 step 3 (PLAN.md §7.3, §8, §10): golden file for the job details page's cost summary and cost table, derived
// FROM THE LEGACY FRONT-END CODE by running it in Node:
//   - front-end/src/_helpers/taskHelper.js (calculateTasksTotals, calculateTasksTotalWithoutMargin,
//     calculatePriceForTask, calculateDeviation, calculateDeviationInPercent, calculateTotalDeviation,
//     calculateTotalDeviationPercent, getSumOfAllExpenses), verbatim;
//   - front-end/src/components/jobDetais/table/customHooks/useTaskHoursRowState.js (the rows' state), verbatim,
//     with a React stand-in whose useEffect runs at once (the rows' steady state after each render);
// both turned into CommonJS by the front-end's own Babel (@babel/plugin-transform-modules-commonjs), nothing else
// changed. The glue of the components (the JSX of costsTableHeade.jsx, gridContent's JobResultsProvider and the
// *Row.jsx renderers: which values they pass and how they print them) is transcribed below, quoted from the files.
// The values a row holds once typed are texts (TextField type="number": "12", "", "-1"), stored ones numbers.
// Nothing is started. Output: tests/fixtures/legacy/jobs/job-costs-golden.json. Two runs give identical files.
//
//   node tools/legacy/job-costs-golden.js [outDir]

"use strict";

const path = require("path");
const fs = require("fs");
const Module = require("module");

const REPO_ROOT = path.resolve(__dirname, "../..");
const FRONTEND = path.resolve(process.env.DIB4T_LEGACY_ROOT || path.join(REPO_ROOT, ".."), "front-end");
const OUT_DIR = path.resolve(process.argv[2] || path.join(REPO_ROOT, "tests/fixtures/legacy/jobs"));
const frontRequire = Module.createRequire(path.join(FRONTEND, "package.json"));
const babel = frontRequire("@babel/core");
const commonjs = frontRequire.resolve("@babel/plugin-transform-modules-commonjs");

// ---------------------------------------------------------------- load the legacy modules (ES modules → CommonJS)
let hookReact = null; // the React stand-in of the current hook call
const loaded = new Map();
function load(file) {
  const full = path.join(FRONTEND, "src", file);
  if (loaded.has(full)) return loaded.get(full);
  const code = babel.transformSync(fs.readFileSync(full, "utf8"), { filename: full, babelrc: false, configFile: false, plugins: [commonjs] }).code;
  const m = { exports: {} };
  const localRequire = (request) => {
    if (request === "react") return { useState: (init) => hookReact.useState(init), useEffect: (fn) => hookReact.useEffect(fn) };
    return load(path.relative(path.join(FRONTEND, "src"), path.resolve(path.dirname(full), request)) + (request.endsWith(".js") ? "" : ".js"));
  };
  new Function("exports", "require", "module", code)(m.exports, localRequire, m);
  loaded.set(full, m.exports);
  return m.exports;
}
const helper = load("_helpers/taskHelper.js");
const useTaskHoursRowState = load("components/jobDetais/table/customHooks/useTaskHoursRowState.js").default;

/** useTaskHoursRowState(...) once its effect ran: the row state the component renders. */
function rowState(...args) {
  let state;
  hookReact = {
    useState: (init) => {
      state = init;
      return [state, (v) => (state = v)];
    },
    useEffect: (fn) => fn(),
  };
  useTaskHoursRowState(...args);
  return state;
}

// ---------------------------------------------------------------- the page (transcribed glue)
// usePrices: `prices.others = 1; prices.components = 1;` on the GET /api/prices answer.
const withPagePrices = (prices) => ({ ...prices, others: 1, components: 1 });
const cls = (value) => (value >= 0 ? "profit" : "loss"); // `${x >= 0 ? `profit` : `loss`}`

function page({ tasks, job, budgetTotal, prices: apiPrices }) {
  const prices = withPagePrices(apiPrices);
  // JobResultsProvider: `if (!job || tasks.length < 1) return;` (keeps the initial zeros)
  let jobResults = { totalDeviationPercent: 0, totaolDeviationEuros: 0, totalRegisteredExpenses: 0 };
  let crash = null;
  if (tasks.length >= 1) {
    try {
      jobResults = results(tasks, job, prices);
    } catch (e) {
      crash = `${e.name}: ${e.message}`; // the effect throws: React unmounts the page (a blank screen)
    }
  }
  if (crash) return { crash };
  return render({ tasks, job, budgetTotal, prices, jobResults });
}

function results(tasks, job, prices) {
  {
    const totaolDeviationEuros = helper.calculateTotalDeviation(tasks, job, prices);
    return {
      totalDeviationPercent: helper.calculateTotalDeviationPercent(totaolDeviationEuros, tasks),
      totaolDeviationEuros,
      totalRegisteredExpenses: helper.getSumOfAllExpenses(job, tasks, prices),
    };
  }
}

function render({ tasks, job, budgetTotal, prices, jobResults }) {
  // CostsTableHead
  const budget = { total: budgetTotal };
  const totalSumTasks = helper.calculateTasksTotalWithoutMargin(tasks);
  const calculateMargin = () => parseFloat(((budget.total - totalSumTasks) * 100) / totalSumTasks).toFixed(0);
  const totalProfitPercent = () => Number(jobResults.totalDeviationPercent) + Number(calculateMargin());
  const previewedProfitInEuros = () => Number(budget.total) - totalSumTasks;
  const totalProfitInEuros = () => Number(previewedProfitInEuros()) + jobResults.totaolDeviationEuros;
  const header = {
    tasksTotal: `${totalSumTasks} €`,
    budgetTotal: `${budget.total} €`,
    registered: `${jobResults.totalRegisteredExpenses} €`,
    previewedProfit: { text: `${previewedProfitInEuros()} €`, class: cls(previewedProfitInEuros()) },
    deviation: { text: `${jobResults.totaolDeviationEuros} €`, class: cls(jobResults.totaolDeviationEuros) },
    totalProfit: { text: `${totalProfitInEuros()} €`, class: cls(totalProfitInEuros()) },
    margin: { text: `${calculateMargin()} %`, class: "profit" },
    deviationPercent: { text: `${jobResults.totalDeviationPercent} %`, class: cls(jobResults.totalDeviationPercent) },
    totalPercent: { text: `${totalProfitPercent()} %`, class: cls(totalProfitPercent()) },
  };

  // JobDetailsTablePrices: `const totals = calculateTasksTotals(tasks);`
  const totals = helper.calculateTasksTotals(tasks);
  // renderPercentDeviation (development, components, others, assembly, machining): `-` only for -Infinity;
  // AutomationRow prints the percentage always.
  const percent = (state, checked) =>
    checked && state.percentDeviation === -Infinity ? { text: "-", class: null } : { text: `${state.percentDeviation} %`, class: cls(state.deviation) };
  // DevelopmentRow / AssemblyRow / MachiningRow: field shown when `rowState.numberOfHoursUsed >= 0`
  const hoursRow = (label, hours, type, moneySpent) => {
    const state = rowState(hours, tasks, totals[type], type, moneySpent, prices);
    return { label, budgeted: `${totals[type]} €`, field: state.numberOfHoursUsed >= 0 ? state.numberOfHoursUsed : null, deviation: `${state.deviation} €`, percent: percent(state, true) };
  };
  // ComponentsRow / AutomationRow / OthersRow: `useTaskHoursRowState(0, tasks, budgetedValue, type, job?.x)` (no prices)
  const moneyRow = (label, value, type, checked) => {
    const state = rowState(0, tasks, totals[type], type, value);
    return { label, budgeted: `${totals[type]} €`, field: state.moneySpent >= 0 ? state.moneySpent : null, deviation: `${state.deviation} €`, percent: percent(state, checked) };
  };
  const rows = [
    hoursRow("Desenvolvimento", job.devStatistic && job.devStatistic.numberOfHoursUsed, "development", undefined),
    moneyRow("Componentes", job.components, "components", true),
    moneyRow("Automação", job.automation, "automation", false),
    hoursRow("Montagem", job.assemblyStatistic && job.assemblyStatistic.numberOfHoursUsed, "assembly", undefined),
    hoursRow("Maquinação", job.machiningStatistic && job.machiningStatistic.numberOfHoursUsed, "machining", job.materials),
    moneyRow("Outros", job.others, "others", true),
  ];
  return { header, rows, materials: job.materials, costNonQuality: job.costNonQuality };
}

// ---------------------------------------------------------------- scenarios
const PRICES = { automation: 1, consultancy: 40, development: 35, machining: 30, assembly: 25, margin: 30 };
const task = (taskType, quantity, unitPrice) => ({ taskType, quantity, unitPrice });
const baseTasks = [
  task("automation", 1, 850),
  task("development", 20, 35),
  task("development", 0.5, 35),
  task("machining", 15, 30),
  task("assembly", 10, 25),
  task("consultancy", 3, 40),
  task("components", 1, 1200.5),
];
const job = (patch = {}) => ({
  components: 0, automation: 0, materials: 0, others: 0, costNonQuality: 0,
  devStatistic: { numberOfHoursUsed: 0 }, machiningStatistic: { numberOfHoursUsed: 0 }, assemblyStatistic: { numberOfHoursUsed: 0 },
  ...patch,
});
const hours = (dev, mach, asm) => ({ devStatistic: { numberOfHoursUsed: dev }, machiningStatistic: { numberOfHoursUsed: mach }, assemblyStatistic: { numberOfHoursUsed: asm } });

const scenarios = [];
const add = (name, spec) => scenarios.push({ name, prices: PRICES, budgetTotal: 4500, tasks: baseTasks, job: job(), ...spec });
add("new job (nothing registered)", {});
add("hours and costs registered", { job: job({ ...hours(22, 14, 12), components: 1150, automation: 900, materials: 120, others: 35, costNonQuality: 50 }) });
add("hours typed as texts", { job: job({ ...hours("22", "14.5", "12"), components: "1150", automation: "900", materials: "120", others: "35", costNonQuality: "50" }) });
add("emptied inputs", { job: job({ ...hours("", "", ""), components: "", automation: "", materials: "", others: "", costNonQuality: "" }) });
add("invalid and negative texts", { job: job({ ...hours("-1", "abc", "1e2"), components: "abc", automation: "-5", others: "0", costNonQuality: "abc" }) });
add("stored null cost (imported job)", { job: job({ ...hours(5, 5, 5), costNonQuality: null }) });
add("no statistics (imported job)", { job: job({ devStatistic: undefined, machiningStatistic: undefined, assemblyStatistic: undefined }) });
add("no tasks", { tasks: [], job: job({ ...hours(10, 10, 10), components: 100 }) });
add("only consultancy (development row)", { tasks: [task("consultancy", 4, 40)], budgetTotal: 200, job: job(hours(5, 0, 0)) });
add("hours without tasks of the type (hourly price)", { tasks: [task("components", 2, 99.99)], budgetTotal: 300, job: job({ ...hours(3, 2.5, 1.25), others: 10 }) });
add("loss: budget below the tasks", { budgetTotal: 2000, job: job({ ...hours(40, 30, 20), components: 1500, automation: 1000, materials: 400 }) });
add("fractional prices", {
  tasks: [task("development", 2.5, 33.33), task("development", 1, 41.07), task("machining", 7.75, 28.6), task("assembly", 3.3, 22.22), task("others", 1, 19.99), task("automation", 2, 0.1), task("automation", 1, 0.2)],
  budgetTotal: 777.77,
  job: job({ ...hours(3.7, 8.1, 2.9), automation: 0.3, others: 20.01 }),
});
add("zero budget total", { budgetTotal: 0, job: job(hours(1, 1, 1)) });
add("large values", { tasks: [task("development", 1200, 45.5), task("machining", 800, 38), task("components", 1, 250000)], budgetTotal: 400000, job: job({ ...hours(1300, 750, 0), components: 260000, materials: 15000 }) });
add("deviation exactly zero", { tasks: [task("development", 10, 35), task("components", 1, 100)], budgetTotal: 1000, job: job({ ...hours(10, 0, 0), components: 100 }) });

// seeded pseudo-random scenarios (mulberry32), values as the page holds them (numbers stored, texts typed)
let seed = 20261118;
const rand = () => {
  seed |= 0; seed = (seed + 0x6d2b79f5) | 0;
  let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
  t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
};
const pick = (list) => list[Math.floor(rand() * list.length)];
const types = ["automation", "consultancy", "development", "machining", "assembly", "others", "components"];
const amount = (max, decimals) => Math.round(rand() * max * 10 ** decimals) / 10 ** decimals;
const value = (max) => {
  const v = amount(max, pick([0, 0, 1, 2]));
  return pick(["number", "number", "text"]) === "text" ? String(v) : v;
};
for (let i = 0; i < 120; i++) {
  const tasks = Array.from({ length: Math.floor(rand() * 9) }, () => task(pick(types), amount(40, pick([0, 1, 2])), amount(200, pick([0, 2]))));
  add(`random ${i + 1}`, {
    tasks,
    budgetTotal: amount(20000, 2),
    prices: { automation: 1, consultancy: amount(60, 0), development: amount(60, 1), machining: amount(60, 0), assembly: amount(60, 2), margin: 30 },
    job: job({ ...hours(value(80), value(80), value(80)), components: value(3000), automation: value(2000), materials: value(800), others: value(500), costNonQuality: value(300) }),
  });
}

// ---------------------------------------------------------------- write
const golden = {
  generatedBy: "tools/legacy/job-costs-golden.js",
  versions: { node: process.version, babel: babel.version },
  sources: ["front-end/src/_helpers/taskHelper.js", "front-end/src/components/jobDetais/table/customHooks/useTaskHoursRowState.js"],
  // `fixed`: where the legacy page crashes (a job without statistics), the legacy code on the job with 0 hours for a
  // missing statistic, which the new page implements
  scenarios: scenarios.map((s) => {
    const legacy = page(s);
    if (!legacy.crash) return { ...s, page: legacy };
    const zero = { numberOfHoursUsed: 0 };
    const fixedJob = { ...s.job, devStatistic: s.job.devStatistic || zero, machiningStatistic: s.job.machiningStatistic || zero, assemblyStatistic: s.job.assemblyStatistic || zero };
    return { ...s, page: legacy, fixed: page({ ...s, job: fixedJob }) };
  }),
};
fs.mkdirSync(OUT_DIR, { recursive: true });
const file = path.join(OUT_DIR, "job-costs-golden.json");
// undefined (a missing statistic) is written as the JSON text "undefined" so the test can tell it from null
fs.writeFileSync(file, JSON.stringify(golden, (k, v) => (v === undefined ? "__undefined__" : v), 2) + "\n");
console.log(`wrote ${path.relative(REPO_ROOT, file)}: ${scenarios.length} scenarios`);
