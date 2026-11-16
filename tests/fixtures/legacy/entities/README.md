# Entity contract golden file (legacy code answers)

`entity-golden.json` is written by `tools/legacy/entity-golden.js` (`tools/legacy/legacy.sh entity-golden`). It runs
the **unchanged** legacy code from `../back-end` in Node 16; nothing is started (no database, no server, no network):
the Joi 14.3.1 validation of `validations/entityValidation.js`, then the Mongoose 5.13 `Entity` model built or
hydrated in memory, validated with `validateSync`, and the save read from `$__delta` (BSON types from the legacy
driver's own js-bson 1.1.6 serializer); the search RegExps of `repositories/entityRepository.js` built from each body.
Deterministic: two runs give byte-identical files.

| Section | Legacy code | Checked by |
|---|---|---|
| `createEntity` | `validateEntity`, `new Entity(req.body)`, save, `res.send(newEntity)` | `EntityGoldenTests` (Application), `EntityRepositoryTests` (stored documents, Infrastructure), `EntityGoldenEndpointTests` (HTTP) |
| `editEntity` | `validateEntity`, `entity.set({…7 fields})`, save delta, `_.pick` answer | same three |
| `search` | `new RegExp("^" + value, "i")` × 5, the driver's serializer | `EntityGoldenTests` (patterns), `EntityRepositoryTests` (on MongoDB 9.0.8), `EntityGoldenEndpointTests` |
| `read` | `Entity.hydrate(stored).toJSON()` (`GET /:id`, `getAll`) | `EntityGoldenEndpointTests` |
| `pathIds` | Mongoose `findById` cast | `EntityGoldenTests`, `EntityGoldenEndpointTests` |
| `entityTypes` | `getAllEntityTypes()` | `EntityGoldenTests` |

What this file shows about the legacy behavior (kept in the new app):

- One schema for create and edit: `name` and `paymentCondition` non-empty strings, `type` one of the three types
  (exact case), the other four fields `Joi.any()`; unknown keys (also `_id`, `__v`) are refused. `paymentCondition`
  is any text: it is not checked against the payment methods.
- The `Joi.any()` fields go through Mongoose's String cast: numbers become their JavaScript text (`1e21` →
  `"1e+21"`), booleans `"true"`/`"false"`, `null` is stored as `null`, an object whose `_id` is a non-empty string
  becomes that string (`{"_id":"abc"}` → `"abc"`; the same rule applies to `users.roles`); any other object or
  array fails the save (500, Express's error page; the new app: `"Something failed."`, D16).
- Create stores the body's keys in the body's order, `__v: 0` last; a missing optional field is not stored. The
  name is checked for duplicates (exact match) **before** the casts, so a duplicate name with a bad field is a 400.
- Edit assigns all seven fields: a field missing from the body is **removed** from the stored document (`$unset`),
  a value equal to the stored one is not written, nothing is sent when nothing differs; no `__v` change. The name
  is not checked for duplicates. The answer is `_.pick` of the 8 fields (no `__v`).
- Search: each value is JavaScript's `String(value)` — a missing key is `"undefined"`, `null` is `"null"`, arrays
  are joined with `,`, objects are `[object Object]`, an object with its own `toString` key throws (500) — and the
  text is a **regular expression** (prefix, case-insensitive), so `(`, `[`, `*`, `\` fail. JavaScript-invalid
  patterns also fail on MongoDB (checked on 9.0.8), and `\k` passes JavaScript but fails MongoDB's PCRE: a 500
  in both apps. A NUL character is refused by the driver (500).
- Path ids: 24 hex digits (any case) or any 12-character string cast to an ObjectId (no match → 400
  `"Entidade não encontrada"` / `"Entidade não existe na bd"`); anything else is a CastError (500). `GET
  /api/entity/search` is `GET /:id` with `"search"` (500).

Known differences (ADR-0004: no other API consumers; tests allow-list them):

- New entities are written and answered in the UI's field order (`name, fiscalNumber, address, zipCode, location,
  paymentCondition, type`), whatever the body's key order.
- A stored `null` text is answered like a missing one (legacy answers `null`). Neither UI sends `null`.
- Stored names and texts are NFC (PLAN.md §4.3 rule 2); legacy stores them as sent.
- Fields beyond the schema are not echoed by `GET /:id` / `getAll` (none in production). Stored numbers in text
  fields (never written by either app: Mongoose casts) are not supported on read.
