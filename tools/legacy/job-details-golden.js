#!/usr/bin/env node
// Phase 4 step 3 (PLAN.md §6, §7.3, §10): golden file for the job details page's routes, derived FROM THE LEGACY CODE
// by running the route handlers themselves:
//   PATCH statisticsDates/:jobId, PATCH register/:jobId, PATCH singleProperty/:jobId, PATCH updateVerifications/:jobId.
// Nothing is started: no database, no network, no legacy server, no SSH, no printer.
//
//   node tools/legacy/job-details-golden.js [outDir]      (default tests/fixtures/legacy/jobs)
//
// The harness (./job-harness.js, shared with job-board-golden.js) runs, from ../back-end (DIB4T_LEGACY_ROOT),
// unchanged: the Express handlers of routes/job.js with a recording `res` (a thrown error is a 500 by Express),
// validations/jobValidation.js (Joi 14.3.1, joi-objectid), models/job.js (setStatisticsDates,
// updateRegisteredValue, updateMachiningVerificatons, updateAssemblyVerifications) with Mongoose 5.13's casts,
// `validate()` and `$__delta()`, on the in-memory store of the jobs board golden file (the same 15 job shapes).
//
// Sections with a decided fix are run twice: as is (`legacy`) and with the fix applied to the legacy model (`fixed`,
// what the new app implements):
//   - statisticsDates: D17 (ADR-0005), a job without `jobStatisticsDates` gets a new one, as setStatistics does;
//   - register: D18 (ADR-0005), `consultancy` sets devStatistic.numberOfHoursUsed like `development`;
//   - updateVerifications: the statistic's `validatedBy` is the signed-in user's name (PLAN.md §7.3: the server
//     sets it, legacy stores what the client sends).
// A fixed clock (2026-03-15T10:20:30.123Z, TZ=UTC like the legacy production container).
//
// Output: <outDir>/job-details-golden.json (README-details.md is hand-written). Two runs give identical files.

"use strict";

process.env.TZ = "UTC";

const path = require("path");
const fs = require("fs");
const { REPO_ROOT, legacyRequire, contacted, j, answer, jobShapes, run, goldenHeader } = require("./job-harness");

const OUT_DIR = path.resolve(process.argv[2] || path.join(REPO_ROOT, "tests/fixtures/legacy/jobs"));
const { Job } = legacyRequire("./models/job");
const { JobStatisticsDates } = legacyRequire("./models/jobStatisticsDates");
const USER_NAME = "Ana Administradora"; // the harness's req.user.name

// ---------------------------------------------------------------- the decided fixes, on the legacy model
function patch(name, replacement) {
  return () => {
    const original = Job.prototype[name];
    Job.prototype[name] = replacement(original);
    return () => {
      Job.prototype[name] = original;
    };
  };
}

const FIXES = {
  // D17: `if (!this.jobStatisticsDates) this.jobStatisticsDates = new JobStatisticsDates();` as in setStatistics
  statisticsDates: [
    patch("setStatisticsDates", (original) =>
      function setStatisticsDates(forJobState, newDate) {
        if (!this.jobStatisticsDates) this.jobStatisticsDates = new JobStatisticsDates();
        return original.call(this, forJobState, newDate);
      }),
  ],
  // D18: consultancy is development
  register: [
    patch("updateRegisteredValue", (original) =>
      function updateRegisteredValue(newValue, jobTaskType) {
        return original.call(this, newValue, jobTaskType === "consultancy" ? "development" : jobTaskType);
      }),
  ],
  // the verifier is the signed-in user (the client's validatedBy is not trusted)
  verifications: [
    patch("updateMachiningVerificatons", () =>
      function updateMachiningVerificatons(requestBody) {
        this.machiningStatistic = { ...requestBody.machiningStatistic, validatedBy: USER_NAME };
      }),
    patch("updateAssemblyVerifications", () =>
      function updateAssemblyVerifications(requestBody) {
        this.assemblyStatistic = { ...requestBody.assemblyStatistic, validatedBy: USER_NAME };
      }),
  ],
};

async function runFixed(spec, fixes) {
  const undo = fixes.map((install) => install());
  try {
    return await run(spec);
  } finally {
    undo.reverse().forEach((restore) => restore());
  }
}

// ---------------------------------------------------------------- cases
const cases = [];
const add = (section, spec) => cases.push({ section, ...spec });
const patchCase = (section, route) => (jobId, body) =>
  add(section, { method: "patch", route, params: { jobId }, body: body === undefined ? undefined : j(body) });

// PATCH statisticsDates/:jobId: the state dates column (the date inputs send "yyyy-MM-dd", "" when cleared)
const dates = patchCase("statisticsDates", "/statisticsDates/:jobId");
for (const forJobState of ["devEndedInDate", "onApprovalEndedInDate", "approvedEndedInDate", "machiningEndedInDate", "assemblyEndedInDate"]) {
  dates("{{job-machining}}", { forJobState, newDate: "2026-03-10" });
}
dates("{{job-machining}}", { forJobState: "devEndedInDate", newDate: "2026-03-01T10:00:00.000Z" }); // unchanged
dates("{{job-machining}}", { forJobState: "devEndedInDate", newDate: "2026-03-01" });
dates("{{job-machining}}", { forJobState: "devEndedInDate", newDate: "" });
dates("{{job-machining}}", { forJobState: "devEndedInDate", newDate: null });
dates("{{job-machining}}", { forJobState: "devEndedInDate" });
dates("{{job-machining}}", { forJobState: "devEndedInDate", newDate: "abc" });
dates("{{job-machining}}", { forJobState: "devEndedInDate", newDate: "2026-02-30" });
dates("{{job-machining}}", { forJobState: "devEndedInDate", newDate: 1773100800000 });
dates("{{job-machining}}", { forJobState: "devEndedInDate", newDate: "2026-03-10T12:30:00" });
dates("{{job-machining}}", { forJobState: "devEndedInDate", newDate: "10000-01-01" });
dates("{{job-machining}}", { forJobState: "forInvoicingEndedInDate", newDate: "2026-03-10" });
dates("{{job-machining}}", { forJobState: "devendedindate", newDate: "2026-03-10" });
dates("{{job-machining}}", { forJobState: "", newDate: "2026-03-10" });
dates("{{job-machining}}", { newDate: "2026-03-10" });
dates("{{job-machining}}", { forJobState: "devEndedInDate", newDate: "2026-03-10", extra: 1 });
dates("{{job-machining}}", {});
dates("{{job-dates-null}}", { forJobState: "devEndedInDate", newDate: "2026-03-10" });
dates("{{job-dates-null}}", { forJobState: "devEndedInDate", newDate: "" });
dates("{{job-invoicing}}", { forJobState: "assemblyEndedInDate", newDate: "2026-03-06" });
// no jobStatisticsDates yet (D17)
dates("{{job-dev}}", { forJobState: "devEndedInDate", newDate: "2026-03-10" });
dates("{{job-dev}}", { forJobState: "approvedEndedInDate", newDate: "" });
dates("{{job-dev}}", { forJobState: "devEndedInDate" });
dates("{{job-dev}}", { forJobState: "devEndedInDate", newDate: "abc" });
dates("{{job-old}}", { forJobState: "machiningEndedInDate", newDate: "2026-03-10" });
dates("{{job-assembly}}", { forJobState: "assemblyEndedInDate", newDate: "2026-03-10" });
dates("{{missing}}", { forJobState: "devEndedInDate", newDate: "2026-03-10" });
dates("{{missing}}", { forJobState: "outro", newDate: "2026-03-10" });
dates("abc", { forJobState: "devEndedInDate", newDate: "2026-03-10" });

// PATCH register/:jobId: the cost table's "Registo" column (numbers from the job, texts once typed)
const register = patchCase("register", "/register/:jobId");
const rowTypes = ["development", "assembly", "machining", "automation", "components", "others", "custoNaoQualidade", "materials"];
for (const taskType of rowTypes) register("{{job-machining}}", { newRegisteredValue: "12", taskType });
for (const taskType of rowTypes) register("{{job-machining}}", { newRegisteredValue: 0, taskType }); // the stored values
register("{{job-machining}}", { newRegisteredValue: 12.5, taskType: "development" });
register("{{job-machining}}", { newRegisteredValue: "7.25", taskType: "components" });
register("{{job-machining}}", { newRegisteredValue: "", taskType: "development" });
register("{{job-machining}}", { newRegisteredValue: "", taskType: "automation" });
register("{{job-machining}}", { newRegisteredValue: null, taskType: "others" });
register("{{job-machining}}", { taskType: "development" });
register("{{job-machining}}", { taskType: "materials" });
register("{{job-machining}}", { newRegisteredValue: "abc", taskType: "development" });
register("{{job-machining}}", { newRegisteredValue: "abc", taskType: "components" });
register("{{job-machining}}", { newRegisteredValue: -3, taskType: "assembly" });
register("{{job-machining}}", { newRegisteredValue: "1e3", taskType: "others" });
register("{{job-machining}}", { newRegisteredValue: " 12 ", taskType: "machining" });
register("{{job-machining}}", { newRegisteredValue: true, taskType: "components" });
register("{{job-machining}}", { newRegisteredValue: [5], taskType: "components" });
register("{{job-machining}}", { newRegisteredValue: { v: 5 }, taskType: "automation" });
register("{{job-machining}}", { newRegisteredValue: "12", taskType: "consultancy" }); // D18
register("{{job-machining}}", { newRegisteredValue: "12", taskType: "Development" });
register("{{job-machining}}", { newRegisteredValue: "12", taskType: "outro" });
register("{{job-machining}}", { newRegisteredValue: "12" });
register("{{job-machining}}", {});
register("{{job-onapproval}}", { newRegisteredValue: "18", taskType: "development" }); // unchanged
register("{{job-onapproval}}", { newRegisteredValue: "120.5", taskType: "components" }); // unchanged
register("{{job-old}}", { newRegisteredValue: "5", taskType: "custoNaoQualidade" }); // stored null
register("{{job-old}}", { newRegisteredValue: "5", taskType: "automation" });
register("{{job-no-stats}}", { newRegisteredValue: "5", taskType: "development" });
register("{{job-no-stats}}", { newRegisteredValue: "5", taskType: "consultancy" });
register("{{job-no-stats}}", { newRegisteredValue: "5", taskType: "automation" });
register("{{job-machining-no-stat}}", { newRegisteredValue: "5", taskType: "machining" });
register("{{job-machining-no-stat}}", { newRegisteredValue: "5", taskType: "assembly" });
register("{{job-billed}}", { newRegisteredValue: "5", taskType: "others" });
register("{{missing}}", { newRegisteredValue: "5", taskType: "others" });
register("abc", { newRegisteredValue: "5", taskType: "others" });

// PATCH singleProperty/:jobId: the header (delivery date, billed %, order identification, official) and observations
const single = patchCase("singleProperty", "/singleProperty/:jobId");
const prop = (job, propertyName, value) => single(`{{job-${job}}}`, value === undefined ? { propertyName } : { propertyName, value });
prop("assembly", "deliveryDate", "2030-03-02");
prop("assembly", "deliveryDate", "");
prop("assembly", "deliveryDate", null);
prop("assembly", "deliveryDate", undefined);
prop("assembly", "deliveryDate", "abc");
prop("assembly", "deliveryDate", "2030-03-02T10:00:00.000Z");
prop("invoicing", "deliveryDate", "2026-03-05"); // unchanged
prop("invoicing", "deliveryDate", "2026-03-09");
prop("dev", "deliveryDate", "2030-03-02");
prop("machining", "deliveryDate", "");
prop("billed", "deliveryDate", "2026-02-20");
prop("dev", "orderIdentification", "ENC-2030/002");
prop("dev", "orderIdentification", "  ENC  ");
prop("dev", "orderIdentification", "ENC-2030/001"); // unchanged
prop("dev", "orderIdentification", "");
prop("dev", "orderIdentification", null);
prop("dev", "orderIdentification", undefined);
prop("dev", "orderIdentification", 5);
prop("dev", "orderIdentification", { a: 1 });
prop("dev", "isOfficialOrderIdentification", true);
prop("dev", "isOfficialOrderIdentification", false); // unchanged
prop("dev", "isOfficialOrderIdentification", "true");
prop("dev", "isOfficialOrderIdentification", "TRUE");
prop("dev", "isOfficialOrderIdentification", 1);
prop("dev", "isOfficialOrderIdentification", null);
prop("dev", "isOfficialOrderIdentification", undefined);
prop("dev", "comments", "Cliente pediu alteração da cor.");
prop("dev", "comments", "");
prop("dev", "comments", null);
prop("dev", "comments", undefined);
prop("dev", "comments", 5);
prop("dev", "comments", ["a"]);
prop("assembly", "comments", "Aguardar peças."); // unchanged
prop("dev", "invoicedPercent", "50");
prop("dev", "invoicedPercent", 50);
prop("dev", "invoicedPercent", "");
prop("dev", "invoicedPercent", null);
prop("dev", "invoicedPercent", undefined);
prop("dev", "invoicedPercent", "abc");
prop("dev", "invoicedPercent", 150);
prop("dev", "invoicedPercent", -5);
prop("dev", "invoicedPercent", 0); // unchanged
prop("invoicing", "invoicedPercent", "100");
prop("old", "comments", "Nota");
prop("old", "invoicedPercent", "10");
prop("dev", "state", "Apagada");
prop("dev", "finalPrice", 1);
prop("dev", "DeliveryDate", "2030-03-02");
single("{{job-dev}}", { propertyName: "comments", value: "x", extra: 1 });
single("{{job-assembly}}", { propertyName: "deliveryDate", value: "2030-03-02", extra: 1 });
single("{{job-dev}}", { propertyName: "deliveryDate", value: "2030-03-02", extra: 1 });
single("{{job-dev}}", { value: "x" });
single("{{job-dev}}", {});
single("{{missing}}", { propertyName: "comments", value: "x" });
single("abc", { propertyName: "comments", value: "x" });

// PATCH updateVerifications/:jobId: the checks, as the legacy page sends them (the job's statistics as read, with the
// changed answer and the user's name; for an assembly check its machiningStatistic is the assembly statistic too)
const verify = patchCase("verifications", "/updateVerifications/:jobId");
const shapeOf = (name) => jobShapes.find((s) => answer(s._id) === `{{job-${name}}}`);
const asRead = (name, key) => answer(shapeOf(name)[key]);
const machining = (name, patchValues) => ({ ...asRead(name, "machiningStatistic"), ...patchValues });
const assembly = (name, patchValues) => ({ ...asRead(name, "assemblyStatistic"), ...patchValues });
const machiningBody = (name, patchValues) => ({
  machiningStatistic: machining(name, { validatedBy: USER_NAME, ...patchValues }),
  assemblyStatistic: asRead(name, "assemblyStatistic"),
  updateMachiningVerificatons: true,
});
const assemblyBody = (name, patchValues) => {
  const changed = assembly(name, { validatedBy: USER_NAME, ...patchValues });
  return { machiningStatistic: changed, assemblyStatistic: changed, updateMachiningVerificatons: false };
};
verify("{{job-machining}}", machiningBody("machining", { verificationAndShapeControl: "Ok" }));
verify("{{job-machining}}", machiningBody("machining", { dimensionalVerificationAndControl: "n/a" }));
verify("{{job-machining}}", machiningBody("machining", { analysisOfTheDegreeOfFinish: "Nok" }));
verify("{{job-machining}}", machiningBody("machining", { validatedBy: "Outro Utilizador" }));
verify("{{job-machining}}", machiningBody("machining", { verificationAndShapeControl: "OK" }));
verify("{{job-machining}}", machiningBody("machining", { numberOfHoursUsed: "12" }));
verify("{{job-machining}}", machiningBody("machining", { extra: 1 }));
verify("{{job-machining}}", { ...machiningBody("machining", { verificationAndShapeControl: "Ok" }), updateMachiningVerificatons: "true" });
verify("{{job-machining}}", { ...machiningBody("machining", { verificationAndShapeControl: "Ok" }), updateMachiningVerificatons: 1 });
verify("{{job-machining}}", { ...machiningBody("machining", { verificationAndShapeControl: "Ok" }), updateMachiningVerificatons: undefined });
verify("{{job-machining}}", { updateMachiningVerificatons: true });
verify("{{job-machining}}", { machiningStatistic: { verificationAndShapeControl: "Ok" }, updateMachiningVerificatons: true });
verify("{{job-machining}}", {});
verify("{{job-assembly}}", assemblyBody("assembly", { pneumaticCircuits: "Ok" }));
verify("{{job-assembly}}", assemblyBody("assembly", { kinematics: "Nok" }));
verify("{{job-assembly}}", assemblyBody("assembly", { visualInspection: "n/a" })); // unchanged answer
verify("{{job-assembly}}", assemblyBody("assembly", { validTryOnFirstTest: "3" }));
verify("{{job-assembly}}", assemblyBody("assembly", { validTryOnFirstTest: "4" }));
verify("{{job-assembly}}", assemblyBody("assembly", { validatedBy: "Outro Utilizador", programmingFeaturesPLC: "Ok" }));
verify("{{job-old}}", machiningBody("old", { verificationAndShapeControl: "Nok" }));
verify("{{job-old}}", assemblyBody("old", { operator_equipmentSafetyConditions: "Ok" }));
verify("{{job-machining-no-stat}}", {
  machiningStatistic: { verificationAndShapeControl: "Ok", validatedBy: USER_NAME },
  assemblyStatistic: asRead("machining-no-stat", "assemblyStatistic"),
  updateMachiningVerificatons: true,
});
verify("{{missing}}", machiningBody("machining", { verificationAndShapeControl: "Ok" }));
verify("abc", machiningBody("machining", { verificationAndShapeControl: "Ok" }));

// ---------------------------------------------------------------- write
(async () => {
  const golden = goldenHeader("tools/legacy/job-details-golden.js");
  golden.fixes = {
    statisticsDates: "D17: a job without jobStatisticsDates gets a new one (as setStatistics does)",
    register: "D18: consultancy sets devStatistic.numberOfHoursUsed like development",
    verifications: "validatedBy of the replaced statistic is the signed-in user's name (PLAN.md §7.3)",
  };
  for (const c of cases) {
    const route = c.route.replace(/:([a-zA-Z]+)/g, (_m, key) => c.params[key]);
    const item = { section: c.section, method: c.method.toUpperCase(), path: "/api/job" + route, body: c.body === undefined ? null : c.body };
    item.legacy = await run(c);
    if (FIXES[c.section]) item.fixed = await runFixed(c, FIXES[c.section]);
    golden.cases.push(item);
  }
  if (contacted.length) throw new Error("unexpected NAS/printer contact: " + contacted.join(" | "));

  fs.mkdirSync(OUT_DIR, { recursive: true });
  const file = path.join(OUT_DIR, "job-details-golden.json");
  fs.writeFileSync(file, JSON.stringify(golden, null, 2) + "\n");
  const count = (s) => golden.cases.filter((c) => c.section === s).length;
  const changedByFix = golden.cases.filter((c) => c.fixed && j(c.fixed) !== j(c.legacy)).length;
  console.log(
    `wrote ${path.relative(REPO_ROOT, file)}: ${golden.cases.length} cases (statisticsDates ${count("statisticsDates")}, register ${count("register")}, ` +
      `singleProperty ${count("singleProperty")}, verifications ${count("verifications")}; ${changedByFix} changed by a decided fix)`,
  );
  process.exit(0);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
