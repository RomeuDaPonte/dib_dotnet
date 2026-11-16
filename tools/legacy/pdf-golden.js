#!/usr/bin/env node
// Phase 3 step 3 (PLAN.md §7.1 "PDF", §10): golden files for the new app's budget PDF, written by the
// UNCHANGED legacy code: back-end/helpers/budgetPdfGenerator.js (the document definition) laid out by the
// legacy pdfmake 0.1.37 bundle (back-end/pdfmake/pdfmake.js: pdfmake + pdfkit + fontkit + linebreak).
//
//   tools/legacy/legacy.sh pdf-golden
//   node tools/legacy/pdf-golden.js [assetsDir] [goldenDir]
//
// Offline and deterministic: nothing is started, no database (the anonymized production fixture is read with
// the legacy backup reader and hydrated with the legacy Mongoose models), TZ=UTC like the legacy production
// container. Output:
//
//   <assetsDir> (src/Dib4t.Infrastructure/Pdf/Assets): the inputs the new generator embeds, taken from the
//     legacy sources byte for byte: Roboto-Regular.ttf / Roboto-Medium.ttf (pdfmake/vfs_fonts.js), logo.jpg
//     (helpers/logoForPdf.js), unicode-ranges.txt (per code point: the UAX #14 class of the bundled `linebreak`
//     trie, the general category and the OpenType script tag fontkit's `unicode-properties` give it).
//   <goldenDir> (tests/fixtures/legacy/pdf-layout):
//     line-breaks.json  the bundled LineBreaker's breaks for test strings
//     shaping.json      fontkit's glyphs and advances (EmbeddedFont.layout, the call pdfmake measures with)
//     layouts.json      the laid-out pages (pdfmake's own page items) of the 11 reference samples and of
//                       synthetic budgets for the edge cases (long texts, page breaks, numbers, missing fields)
//     digests.json      a SHA-256 of the laid-out pages of EVERY budget of the anonymized fixture
//                       (canonical text below; the .NET test builds the same text)
//
// Canonical page text (digests.json and the .NET side): numbers with 6 decimals (Number#toFixed), texts as
// UTF-16 code units in hex, one item per line:
//   P <page>
//   L <x> <y> <height>            a text line, then per inline:  I <x> <width> <font> <size> <hex>
//   V line <x1> <y1> <x2> <y2> <lineWidth> <lineColor> | V rect <x> <y> <w> <h> <color>
//   M <x> <y> <w> <h>             the logo

"use strict";

process.env.TZ = "UTC";

const path = require("path");
const fs = require("fs");
const zlib = require("zlib");
const crypto = require("crypto");
const Module = require("module");

const REPO_ROOT = path.resolve(__dirname, "../..");
const BACKEND = path.resolve(process.env.DIB4T_LEGACY_ROOT || path.join(REPO_ROOT, ".."), "back-end");
const ASSETS_DIR = path.resolve(process.argv[2] || path.join(REPO_ROOT, "src/Dib4t.Infrastructure/Pdf/Assets"));
const GOLDEN_DIR = path.resolve(process.argv[3] || path.join(REPO_ROOT, "tests/fixtures/legacy/pdf-layout"));
const DIBBAK = path.join(REPO_ROOT, "tests/fixtures/legacy/prod-2026-11-11.anon.dibbak");
const MANIFEST = path.join(REPO_ROOT, "tests/fixtures/legacy/pdf/manifest.json");

const legacyRequire = Module.createRequire(path.join(BACKEND, "index.js"));

// ---------------------------------------------------------------- legacy code (unchanged)
const pdfMake = legacyRequire("./pdfmake/pdfmake"); // the instance routes/budget.js uses
pdfMake.vfs = legacyRequire("./pdfmake/vfs_fonts").pdfMake.vfs;
const { generatePdf } = legacyRequire("./helpers/budgetPdfGenerator");
const { logo } = legacyRequire("./helpers/logoForPdf");
const { parseBackup } = legacyRequire("./helpers/backupFormat");
const mongoose = legacyRequire("mongoose");
const { Budget } = legacyRequire("./models/budget");
const { Entity } = legacyRequire("./models/entity");
const { User } = legacyRequire("./models/user");
const BSON = legacyRequire("bson"); // 1.1.6
const bson = new BSON();

// A second copy of the same bundle with webpack's require exposed, only to READ its internal data
// (the linebreak trie, unicode-properties); nothing of it is used to lay out.
const bundleFile = path.join(BACKEND, "pdfmake/pdfmake.js");
const bundleSource = fs.readFileSync(bundleFile, "utf8");
const ENTRY = "return __webpack_require__(__webpack_require__.s = 123);";
if (!bundleSource.includes(ENTRY)) throw new Error("unexpected pdfmake bundle: webpack entry not found");
const legacyGlobalPdfMake = global.pdfMake; // createPdf reads global.pdfMake (fonts, vfs): keep the legacy instance's
const inspected = new Module(bundleFile + "#inspect", module);
inspected.filename = bundleFile;
inspected.paths = Module._nodeModulePaths(path.dirname(bundleFile));
inspected._compile(bundleSource.replace(ENTRY, "global.__pdfmakeRequire = __webpack_require__; " + ENTRY), bundleFile);
const wr = global.__pdfmakeRequire;
delete global.__pdfmakeRequire;
global.pdfMake = legacyGlobalPdfMake;

const LineBreaker = wr(79);
const classes = wr(133); // UAX #14 class constants
const unicode = wr(269); // unicode-properties
const UnicodeTrie = wr(45);
const base64 = wr(132);
const trieData = /data = base64\.toByteArray\("([^"]+)"\);\s*classTrie = new UnicodeTrie\(data\);/.exec(bundleSource);
if (!trieData) throw new Error("linebreak trie data not found in the bundle");
const classTrie = new UnicodeTrie(base64.toByteArray(trieData[1]));
const CLASS_NAMES = Object.keys(classes).filter((k) => typeof classes[k] === "number").sort((a, b) => classes[a] - classes[b]);

// ---------------------------------------------------------------- helpers
const n6 = (v) => Number(v).toFixed(6);
const hex = (s) => Array.from({ length: s.length }, (_, i) => s.charCodeAt(i).toString(16).padStart(4, "0")).join("");
const sha256 = (data) => crypto.createHash("sha256").update(data).digest("hex");
// Big files are written one case per line (diffable, not indented).
const writeJson = (file, value, listKey) => {
  if (!listKey) return fs.writeFileSync(file, JSON.stringify(value, null, 1) + "\n");
  const { [listKey]: list, ...head } = value;
  const lines = list.map((item) => JSON.stringify(item));
  fs.writeFileSync(file, JSON.stringify(head).replace(/}$/, `,"${listKey}":[\n`) + lines.join(",\n") + "\n]}\n");
};

/** Lays a document out with the legacy pdfmake; resolves with pdfmake's own pages (doc._pdfMakePages). */
function layout(docDefinition) {
  return new Promise((resolve, reject) => {
    try {
      pdfMake.createPdf(docDefinition)._createDoc({}, (buffer, pages) => resolve({ buffer, pages }));
    } catch (e) {
      reject(e);
    }
  });
}

function dumpPages(pages) {
  return pages.map((page) => ({
    items: page.items.map((entry) => {
      const it = entry.item;
      switch (entry.type) {
        case "line":
          return {
            type: "line", x: it.x, y: it.y, height: it.getHeight(),
            inlines: it.inlines.map((i) => ({ text: i.text, x: i.x, width: i.width, font: i.font.name, size: i.fontSize })),
          };
        case "vector":
          return it.type === "line"
            ? { type: "vector", shape: "line", x1: it.x1, y1: it.y1, x2: it.x2, y2: it.y2, lineWidth: it.lineWidth, lineColor: it.lineColor }
            : { type: "vector", shape: it.type, x: it.x, y: it.y, w: it.w, h: it.h, color: it.color };
        case "image":
          return { type: "image", x: it.x, y: it.y, w: it._width, h: it._height };
        default:
          throw new Error("unexpected page item " + entry.type);
      }
    }),
  }));
}

function canonical(dumped) {
  const out = [];
  dumped.forEach((page, p) => {
    out.push(`P ${p}`);
    for (const it of page.items) {
      if (it.type === "line") {
        out.push(`L ${n6(it.x)} ${n6(it.y)} ${n6(it.height)}`);
        for (const i of it.inlines) out.push(`I ${n6(i.x)} ${n6(i.width)} ${i.font} ${n6(i.size)} ${hex(i.text)}`);
      } else if (it.type === "vector" && it.shape === "line") {
        out.push(`V line ${n6(it.x1)} ${n6(it.y1)} ${n6(it.x2)} ${n6(it.y2)} ${n6(it.lineWidth)} ${it.lineColor}`);
      } else if (it.type === "vector") {
        out.push(`V ${it.shape} ${n6(it.x)} ${n6(it.y)} ${n6(it.w)} ${n6(it.h)} ${it.color}`);
      } else {
        out.push(`M ${n6(it.x)} ${n6(it.y)} ${n6(it.w)} ${n6(it.h)}`);
      }
    }
  });
  return out.join("\n") + "\n";
}

/** The printed inputs (what budgetPdfGenerator.js reads), JSON-safe; `undefined` kept as a missing key. */
function inputsOf(b) {
  const pick = (o, keys) => (o == null ? o : Object.fromEntries(keys.filter((k) => o[k] !== undefined).map((k) => [k, o[k]])));
  return {
    budgetNumber: b.budgetNumber,
    revision: b.revision,
    date: b.date instanceof Date ? b.date.toISOString() : b.date,
    madeBy: pick(b.madeBy, ["name", "email"]),
    client: pick(b.client, ["name", "address", "location", "zipCode", "fiscalNumber"]),
    personInCharge: b.personInCharge,
    descriptive: b.descriptive,
    description: b.description,
    daysNeededToDoTheWork: b.daysNeededToDoTheWork,
    validity: b.validity,
    paymentMethod: b.paymentMethod,
    total: b.total,
  };
}

/** A budget as routes/budget.js hands it to generatePdf: findById(...).populate(["madeBy", "client"]). */
function hydrate(raw, entitiesById, usersById) {
  const budget = Budget.hydrate(raw);
  const plain = budget.toObject();
  plain.client = entitiesById.get(String(raw.client)) || null;
  plain.madeBy = usersById.get(String(raw.madeBy)) || null;
  plain.date = budget.date;
  return plain;
}

// ---------------------------------------------------------------- 1. assets
async function writeAssets() {
  fs.mkdirSync(ASSETS_DIR, { recursive: true });
  for (const name of ["Roboto-Regular.ttf", "Roboto-Medium.ttf"]) {
    fs.writeFileSync(path.join(ASSETS_DIR, name), Buffer.from(pdfMake.vfs[name], "base64"));
  }
  const m = /^data:image\/jpeg;base64,(.+)$/.exec(logo.image);
  if (!m) throw new Error("logoForPdf.js: not a base64 JPEG data URL");
  fs.writeFileSync(path.join(ASSETS_DIR, "logo.jpg"), Buffer.from(m[1], "base64"));

  // One line per range of code points with the same three values. Only what the port needs:
  // the UAX #14 class (raw trie value), the general category (marks, digits), the script tag.
  const scriptTags = await scriptTagMap();
  const lines = [
    "# Generated by tools/legacy/pdf-golden.js from the legacy pdfmake 0.1.37 bundle (do not edit).",
    "# <first> <last> <line break class> <general category> <fontkit's OpenType script tag; - Common/Inherited/Unknown; ? no tag>",
  ];
  let start = 0;
  let prev = null;
  const valueOf = (cp) => {
    const script = unicode.getScript(cp);
    // "-": skipped by fontkit's forString (Common, Inherited, Unknown); "?": a script fontkit has no tag for, where
    // forString stops and returns undefined (shaped like Unknown, but the rest of the string is not looked at).
    const tag = script === "Common" || script === "Inherited" || script === "Unknown" ? "-" : scriptTags[script] || "?";
    return `${CLASS_NAMES[classTrie.get(cp)] ?? classTrie.get(cp)} ${unicode.getCategory(cp)} ${tag}`;
  };
  for (let cp = 0; cp <= 0x110000; cp++) {
    const v = cp <= 0x10ffff ? valueOf(cp) : null;
    if (v !== prev) {
      if (prev !== null) lines.push(`${start.toString(16)} ${(cp - 1).toString(16)} ${prev}`);
      start = cp;
      prev = v;
    }
  }
  fs.writeFileSync(path.join(ASSETS_DIR, "unicode-ranges.txt"), lines.join("\n") + "\n");
  const hashes = {};
  for (const name of ["Roboto-Regular.ttf", "Roboto-Medium.ttf", "logo.jpg", "unicode-ranges.txt"]) {
    hashes[name] = sha256(fs.readFileSync(path.join(ASSETS_DIR, name)));
  }
  fs.mkdirSync(GOLDEN_DIR, { recursive: true });
  writeJson(path.join(GOLDEN_DIR, "assets.json"), hashes);
  return lines.length - 2;
}

/** fontkit's Unicode script name → OpenType tag, read from fontkit itself (GlyphRun.script of one code point). */
async function scriptTagMap() {
  const firstCodePoint = new Map();
  for (let cp = 0; cp <= 0x10ffff; cp++) {
    if (cp >= 0xd800 && cp <= 0xdfff) continue;
    const s = unicode.getScript(cp);
    if (!firstCodePoint.has(s)) firstCodePoint.set(s, cp);
  }
  const font = (await freshFonts())["Roboto-Regular"].font;
  const map = {};
  for (const [script, cp] of firstCodePoint) map[script] = font.layout(String.fromCodePoint(cp)).script;
  return map;
}

/** The EmbeddedFont objects of a fresh pdfmake document (the font objects pdfmake measures with). */
async function freshFonts() {
  const { pages } = await layout({ content: [{ text: " " }, { text: " ", bold: true }] });
  const fonts = {};
  for (const p of pages) for (const e of p.items) if (e.type === "line") for (const i of e.item.inlines) fonts[i.font.name] = i.font;
  return fonts;
}

// ---------------------------------------------------------------- 2. line breaks
const BREAK_STRINGS = [
  "", "a", "a b", "a  b", "a\nb", "a\r\nb", "a\rb", "a\n\nb", "\n", "a\n", " a", "a ",
  "auto-estrada", "a - b", "a/b", "a / b", "1/2", "http://www.dib4t.com/x?y=1", "geral@dib4t.com",
  "(a) [b] {c}", "a(b)c", "«a» \"b\" 'c'", "1.5 m", "1,5€", "€ 12", "12 €", "12%", "R$ 10", "nº 12", "Nº 22-0691 | R 4",
  "a.b", "a,b", "a;b", "a:b", "a!b", "a?b", "...", "a...b", "a—b", "a–b", "a‐b", "a\u00ADb", "a\u00A0b",
  "a\u200Bb", "a\u2060b", "a\u2011b", "a\tb", "a    b", "ç ã é", "1.º", "2.ª", "x²", "CO₂", "µm", "Ø 20",
  "\uF0A7 item", "• item", "- item", "1) item", "a😀b", "日本語", "abc123def", "123abc", "aaaa-", "-aaaa", "a--b",
  "x:\\NAS\\Obras", "D:\\NAS\\Obras\\2022", "#1", "a&b", "a+b", "a=b", "a<b>c", "a_b", "a*b", "a~b", "a|b",
];

function lineBreaks() {
  return BREAK_STRINGS.map((text) => {
    const breaker = new LineBreaker(text);
    const breaks = [];
    let bk;
    while ((bk = breaker.nextBreak())) breaks.push([bk.position, bk.required]);
    return { text, breaks };
  });
}

// ---------------------------------------------------------------- 3. shaping
async function shapingStrings(samples) {
  const list = [
    "AVATAR", "fi", "fl", "ff", "ffi", "ffl", "fj", "ft", "Tê", "ação", "a\u0303", "e\u0301", "o\u0308", "n\u0303", "c\u0327",
    "A\u030A", "i\u0307", "i\u0301", "j\u0301", "\uF0A7", "\uF0A7x", "₂", "CO₂", "m³", "    ", "\u200B", "a\u200Bb", "\u00AD", "a\u00ADb",
    "-", "a-b", "To.", "\u00A0", "1\u20442", "12\u204434", "x\u2044", "\u2044", "😀", "a\uFE0F", "\uFE0F", "Ωμ", "Привет", "αβγ",
    "AV AW AY Ta Te To Tr Tu Tw Ty Va Ve Vo Wa We Wo Ya Yo LT LV LW LY", "P.", "F.", "T,", "V,", "\"A\"", "'A'", "«A»",
    "1.234,56 €", "4143.75 €", "222000 €", "Nº 22-0691 ", "Nº 25-0573 | R 4", "Data do Documento: 2/11/2022",
    "................................................................................................................................................................................................................",
  ];
  // Every character the fonts map, alone (cmap coverage) and after "A" and "f" (kerning/ligature contexts).
  const font = (await freshFonts())["Roboto-Regular"].font;
  for (const cp of font.characterSet) {
    if (cp >= 0xd800 && cp <= 0xdfff) continue;
    const c = String.fromCodePoint(cp);
    list.push(c, "A" + c, "f" + c, c + "a");
  }
  // Every chunk the reference samples measure.
  for (const s of samples) for (const page of s.pages) for (const it of page.items) if (it.type === "line") for (const i of it.inlines) list.push(i.text);
  return [...new Set(list)];
}

async function shaping(strings) {
  // Fresh fonts (a fresh pdfmake document): fontkit caches glyphs per font object, and the code points a glyph
  // carries are those of its first lookup; the .NET test replays the strings in the same order on fresh fonts.
  const fonts = await freshFonts();
  const result = {};
  for (const name of ["Roboto-Regular", "Roboto-Medium"]) {
    const font = fonts[name];
    result[name] = {
      unitsPerEm: font.font.unitsPerEm,
      ascender: font.ascender,
      descender: font.descender,
      lineGap: font.lineGap,
      lineHeight12: font.lineHeight(12),
      cases: strings.map((text) => {
        const run = font.layout(text);
        return {
          text,
          glyphs: run.glyphs.map((g) => g.id),
          advances: run.positions.map((p) => p.xAdvance),
          offsets: run.positions.some((p) => p.xOffset || p.yOffset) ? run.positions.map((p) => [p.xOffset, p.yOffset]) : undefined,
          width12: font.widthOfString(text, 12),
        };
      }),
    };
  }
  return result;
}

// ---------------------------------------------------------------- 4. layouts
function synthetic(base) {
  const long = (s, n) => Array.from({ length: n }, () => s).join(" ");
  const cases = [
    ["long-client-name", { client: { ...base.client, name: "Sociedade de Construções Metalomecânicas do Centro Litoral, Lda." } }],
    ["very-long-client-name", { client: { ...base.client, name: long("Nome Muito Comprido", 8) } }],
    ["long-client-address", { client: { ...base.client, address: long("Rua da Indústria Metalúrgica", 4) } }],
    ["long-client-location-zip", { client: { ...base.client, location: long("Marinha Grande", 4), zipCode: "2430-528" } }],
    ["long-fiscal-number", { client: { ...base.client, fiscalNumber: long("PT514509376", 6) } }],
    ["long-person-in-charge", { personInCharge: long("Engenheiro Responsável", 4) }],
    ["long-descriptive", { descriptive: long("Molde de injeção para peça técnica", 4) }],
    ["long-author", { madeBy: { name: long("Utilizador Com Nome Comprido", 5), email: "utilizador.com.um.email.muito.comprido@dib4t.local" } }],
    ["long-payment", { paymentMethod: long("30 dias após receção da fatura, com 50% na adjudicação", 2) }],
    ["very-long-payment", { paymentMethod: long("Pagamento faseado conforme acordado", 8) }],
    ["long-everything", {
      client: { name: long("Cliente Comprido", 6), address: long("Morada Comprida", 6), location: long("Localidade", 6), zipCode: "1234-567", fiscalNumber: "123456789" },
      personInCharge: long("Pessoa", 10), descriptive: long("Descritivo", 12), paymentMethod: long("Condição", 14),
    }],
    ["no-space-word", { description: "x".repeat(400), descriptive: "Y".repeat(80) }],
    ["huge-total", { total: 123456789012.34 }],
    ["tiny-total", { total: 0.1 + 0.2 }],
    ["negative-total", { total: -15 }],
    ["zero-total", { total: 0 }],
    ["exp-total", { total: 1e21 }],
    ["missing-total", { total: undefined }],
    ["days-0", { daysNeededToDoTheWork: 0 }],
    ["days-8", { daysNeededToDoTheWork: 8 }],
    ["days-fraction", { daysNeededToDoTheWork: 10.5 }],
    ["validity-fraction", { validity: 0.5 }],
    ["revision-12", { revision: 12 }],
    ["empty-description", { description: "" }],
    ["missing-description", { description: undefined }],
    ["crlf-description", { description: "Linha 1\r\nLinha 2\r\n\r\nLinha 4\rLinha 5\n\tTab\t\tTabs" }],
    ["one-page-full", { description: long("Texto de enchimento para encher a primeira página.", 72) }],
    ["two-pages", { description: long("Texto de enchimento para passar para a segunda página.", 90) }],
    ["three-pages", { description: long("Texto de enchimento para chegar à terceira página do documento.", 220) }],
    ["many-lines", { description: Array.from({ length: 120 }, (_, i) => `Linha ${i + 1}`).join("\n") }],
    ["justify-last-line", { description: long("palavra", 60) + "\nfim" }],
    ["special-chars", { description: "\uF0A7 item com bala\n• outro\nCO₂ e m³\na\u0303o\u0303 combinado\nfi fl ffi\nsoft\u00ADhyphen zero\u200Bwidth\n«aspas» “curvas”" }],
    ["missing-client-fields", { client: { name: "Só Nome" } }],
    ["date-lisbon-midnight", { date: new Date("2022-05-24T23:30:00.000Z") }],
    ["date-winter", { date: new Date("2023-01-01T00:30:00.000Z") }],
  ];
  return cases.map(([id, patch]) => {
    const b = { ...base, ...patch };
    for (const k of Object.keys(patch)) if (patch[k] === undefined) delete b[k];
    return { id, budget: b };
  });
}

async function layouts(manifest, budgetsById) {
  const out = [];
  for (const s of manifest.samples) {
    const budget = budgetsById.get(s.budgetId);
    if (!budget) throw new Error("sample budget not in fixture: " + s.budgetId);
    const { buffer, pages } = await layout(generatePdf(budget));
    out.push({ id: s.file.replace(/\.pdf$/, ""), kind: "sample", budgetId: s.budgetId, inputs: inputsOf(budget), pages: dumpPages(pages), bytes: buffer.length });
  }
  const base = budgetsById.get(manifest.samples[0].budgetId);
  for (const c of synthetic(base)) {
    let result;
    try {
      const { pages } = await layout(generatePdf(c.budget));
      result = { pages: dumpPages(pages) };
    } catch (e) {
      result = { error: String(e && e.message ? e.message : e) };
    }
    out.push({ id: c.id, kind: "synthetic", inputs: inputsOf(c.budget), ...result });
  }
  return out;
}

// ---------------------------------------------------------------- main
async function main() {
  const t0 = Date.now();
  const ranges = await writeAssets();

  const backup = parseBackup(zlib.gunzipSync(fs.readFileSync(DIBBAK)));
  const docs = (name) => (backup.collections.find((c) => c.name === name)?.documents || []).map((d) => bson.deserialize(d, { promoteBuffers: true }));
  const entitiesById = new Map(docs("entities").map((e) => [String(e._id), Entity.hydrate(e).toObject()]));
  const usersById = new Map(docs("users").map((u) => [String(u._id), User.hydrate(u).toObject()]));
  const rawBudgets = docs("budgets");
  const budgetsById = new Map(rawBudgets.map((b) => [String(b._id), hydrate(b, entitiesById, usersById)]));

  const manifest = JSON.parse(fs.readFileSync(MANIFEST, "utf8"));
  fs.mkdirSync(GOLDEN_DIR, { recursive: true });

  const laidOut = await layouts(manifest, budgetsById);
  writeJson(path.join(GOLDEN_DIR, "layouts.json"), {
    generator: "tools/legacy/pdf-golden.js", timeZone: "UTC", canonical: "see the generator's header", cases: laidOut,
  }, "cases");
  writeJson(path.join(GOLDEN_DIR, "line-breaks.json"), { generator: "tools/legacy/pdf-golden.js", cases: lineBreaks() }, "cases");
  const strings = await shapingStrings(laidOut.filter((c) => c.kind === "sample"));
  const shaped = await shaping(strings);
  const shapingCases = [];
  for (const [font, data] of Object.entries(shaped)) {
    const { cases, ...metrics } = data;
    shapingCases.push({ font, metrics });
    for (const c of cases) shapingCases.push({ font, ...c });
  }
  writeJson(path.join(GOLDEN_DIR, "shaping.json"), { generator: "tools/legacy/pdf-golden.js", order: "per font, in this order, on fresh fonts", cases: shapingCases }, "cases");

  const digests = [];
  let failed = 0;
  for (const raw of rawBudgets) {
    const id = String(raw._id);
    const budget = budgetsById.get(id);
    try {
      const { pages } = await layout(generatePdf(budget));
      const dumped = dumpPages(pages);
      digests.push({
        id, pages: dumped.length, lines: dumped.reduce((n, p) => n + p.items.filter((i) => i.type === "line").length, 0),
        sha256: sha256(canonical(dumped)),
      });
    } catch (e) {
      failed++;
      digests.push({ id, error: String(e && e.message ? e.message : e) });
    }
  }
  writeJson(path.join(GOLDEN_DIR, "digests.json"), {
    generator: "tools/legacy/pdf-golden.js", fixture: path.relative(REPO_ROOT, DIBBAK), timeZone: "UTC", budgets: digests,
  }, "budgets");

  console.log(`assets: ${ranges} unicode ranges; layouts: ${laidOut.length}; shaping strings: ${strings.length}; ` +
    `digests: ${digests.length} (${failed} legacy errors); ${((Date.now() - t0) / 1000).toFixed(1)} s`);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
