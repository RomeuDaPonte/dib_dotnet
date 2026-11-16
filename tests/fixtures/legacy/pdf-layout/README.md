# Budget PDF layout golden files (legacy pdfmake)

Written by `tools/legacy/legacy.sh pdf-golden` (`tools/legacy/pdf-golden.js`, offline, deterministic, ~90 s): the
**unchanged** `helpers/budgetPdfGenerator.js` laid out by the **unchanged** legacy bundle `pdfmake/pdfmake.js`
(pdfmake 0.1.37 + pdfkit + fontkit + linebreak), `TZ=UTC` as the legacy container. The .NET port
(`src/Dib4t.Infrastructure/Pdf`) is checked against them in `PdfLayoutGoldenTests`; the rendered result against the
legacy PDFs in `PdfVisualDiffTests` (tests/fixtures/legacy/pdf).

| File | Content |
|---|---|
| `line-breaks.json` | 77 strings → the bundled `LineBreaker`'s breaks (position, required). |
| `shaping.json` | ~6,000 strings per font (every mapped character alone and in context, ligature/mark/fraction/ignorable cases, every chunk of the 11 samples) → fontkit's glyph ids, advances (1/1000 em), offsets and `widthOfString(…, 12)`, through `EmbeddedFont.layout` on fresh fonts, in file order (fontkit's glyph cache is history-dependent). |
| `layouts.json` | The laid-out pages (pdfmake's own `page.items`: lines with inlines, vectors, image) of the 11 reference samples and 35 synthetic budgets (long texts in every block, page breaks, missing fields, numbers such as `1e+21`, CR/LF, special characters, dates around midnight). |
| `digests.json` | SHA-256 of the canonical page text (see the generator's header) of **every budget of the anonymized backup** (3,397). |
| `assets.json` | SHA-256 of the embedded assets. |

## What the port reproduces (found with these files)

- Words are cut where UAX #14 (the 2014 `linebreak` package) allows, tabs become 4 spaces, `\r\n`/`\n`/`\r` force a
  line; a word wider than the line is cut by its average character width.
- Text is shaped per chunk (each chunk ends after a space or tab): GSUB `ccmp` (e.g. `a` + U+0303 → `ã`), `liga`
  (`fi`, `fl`), fractions around U+2044, GPOS pair kerning; default ignorables (U+200B, U+00AD…) become a zero-width
  space glyph; a glyph without code points (.notdef) counts as a mark in Roboto-Medium (no GDEF), so its advance is 0
  there and 443/1000 em in Regular.
- Justified lines get the extra space between inlines; the last line of a paragraph and forced lines are not justified.
- pdfkit's width table starts with the .notdef advance **in font units** (908, not 443.36): a missing glyph's box
  pushes the rest of its word to the right (sample 05, "HX▯ ,"). Reproduced in the PDF's `/W`.
