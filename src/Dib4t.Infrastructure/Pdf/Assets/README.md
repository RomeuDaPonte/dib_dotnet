# Budget PDF assets (embedded)

Written by `tools/legacy/legacy.sh pdf-golden` (`tools/legacy/pdf-golden.js`) from the legacy sources, byte for byte;
their SHA-256 is in `tests/fixtures/legacy/pdf-layout/assets.json` and checked by `PdfLayoutGoldenTests`. Do not edit.

| File | From | Notes |
|---|---|---|
| `Roboto-Regular.ttf`, `Roboto-Medium.ttf` | `back-end/pdfmake/vfs_fonts.js` (pdfmake 0.1.37) | Roboto by Google, Apache License 2.0. Medium is pdfmake's "bold". Regular has a GDEF table, Medium has none (this changes fontkit's mark handling: see `FontkitShaper.cs`). |
| `logo.jpg` | `back-end/helpers/logoForPdf.js` | 386 × 360 RGB JPEG, embedded as-is (DCT). |
| `unicode-ranges.txt` | the bundle's `linebreak` trie and `unicode-properties` | Per code point range: UAX #14 class, general category, fontkit's OpenType script tag (`-` skipped by `forString`, `?` no tag). The legacy tables, not .NET's (they are older). |
