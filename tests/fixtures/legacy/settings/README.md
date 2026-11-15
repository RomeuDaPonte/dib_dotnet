# Settings contract golden file (legacy code answers)

`settings-golden.json` is written by `tools/legacy/settings-golden.js` (`tools/legacy/legacy.sh settings-golden`). It
runs the **unchanged** legacy code from `../back-end` in Node 16; nothing is started (no database, no server, no
network): Joi 14.3.1 validations, then the Mongoose 5.13 models built or hydrated in memory, validated with
`validateSync`, and the save read from `$__delta` (BSON types from the legacy driver's own js-bson 1.1.6 serializer).
Deterministic: two runs give byte-identical files.

| Section | Legacy code | Checked by |
|---|---|---|
| `createUser` | `validateUser`, `_.pick`, `new User(…)`, bcrypt hash, save | `SettingsGoldenTests` (Application), `LegacySavesTests` (stored documents, Infrastructure), `SettingsGoldenEndpointTests` (HTTP) |
| `editUser` | `validateEditarUser`, `user.update(body)`, password re-hash, save delta | same three |
| `prices` | `validatePrices`, `prices.set({…})`, save delta | same three |
| `createPrinter`, `editPrinter` | `validatePrinterSettings`, joi-objectid id check, `new PrinterSettings` / update | same three |
| `createPaymentMethod` | `validatePaymentMethod`, `new PaymentMethod` | same three |
| `pathIds` | Mongoose `findById` cast of path ids | `SettingsGoldenTests`, `SettingsGoldenEndpointTests` |
| `ipPattern`, `roles` | Joi's `ip()` pattern source, `getAllRoles()` | `SettingsGoldenTests` |

What this file shows about the legacy behavior (kept in the new app):

- Numeric **texts** are accepted and stored as numbers (`"32.5"` → Double 32.5, `" 12 "` → Int32 12): the legacy UI
  sends the dialog's text fields as typed. Joi refuses texts whose digits do not survive the conversion
  ("must be a safe number": `"1E2"`, `"1.50"`, `"007"`, more than 15–17 significant digits).
- `roles` may hold anything Mongoose can cast to a string: `null` stays, numbers and booleans become their text,
  objects/arrays fail the save (500). A JSON **string** that parses to an array passes Joi but is stored as a
  one-element array holding that string.
- Edit: a missing `fieldOfCompetencies` fails the save (`required`, 500); a missing `roles` removes the stored array
  (`$unset`, no `__v` change); a name under 3 or an e-mail under 10 characters passes Joi and fails the save. The
  password is re-hashed only when it is truthy; a truthy non-string password is a bcryptjs error (500).
- A save writes only what differs (numbers compared as numbers: stored Int32 10 = 10.0 keeps its type); replacing the
  roles adds `$inc: { __v: 1 }` (also when `__v` is missing); a field missing from the stored document (an imported
  user without `fieldOfCompetencies`) is written even when the value equals the default.
- Path ids: 24 hex digits (any case) or any 12-character string cast to an ObjectId (no match → 404); anything else
  is a CastError (500). Printer routes check the id with joi-objectid first (400 text).
- Printer documents are stored in the body's key order; the new app always uses the UI's (`ip, port, templateName`).
