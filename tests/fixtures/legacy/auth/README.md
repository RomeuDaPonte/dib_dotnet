# Login contract golden file (legacy code answers)

`auth-golden.json` is written by `tools/legacy/auth-golden.js` (`tools/legacy/legacy.sh auth-golden`). It runs the
**unchanged** legacy code from `../back-end` in Node 16; nothing is started (no database, no server, no network).
Deterministic: two runs give byte-identical files.

| Section | Legacy code that answered | Checked by |
|---|---|---|
| `loginValidation` | `validations/userValidation.js` `validateLogin` (Joi 14.3.1) on raw JSON bodies, parsed with `JSON.parse` like `express.json()` | `Dib4t.Application.Tests` `LoginValidatorTests`, `Dib4t.Server.Tests` `AccountEndpointTests` (over HTTP) |
| `email` | `Joi.string().email()` = isemail 3.2.0 with Joi's defaults | `LegacyEmailTests` (the isemail port, `Application/Common/Validation/LegacyEmail.cs`) |
| `bcrypt` | bcryptjs 2.4.3 `hashSync` with fixed salts (`$2a$` costs 4 and 10, `$2b$`), UTF-8 and the 72-byte limit | `Dib4t.Infrastructure.Tests` `BcryptPasswordHasherTests`; the cost-4 hash of `char-test-1` is the password of the API test users (`tests/Shared/TestUsers.cs`) |
| `jwt` | jsonwebtoken 8.5.1 `sign(payload + iat, key)` with a **test key** (never the legacy one) | `JwtTokenServiceTests`: the new app's token is byte for byte the same for the same key and time |

What this file shows about the legacy behavior (kept in the new app):

- Joi reports the first problem only: schema keys in schema order (presence → type → empty → rules in order), then
  the first unknown key (`"remember" is not allowed`). Unknown integer-like keys come first (`Object.keys` order).
- `null`, numbers, booleans, arrays and objects are all `"…" must be a string`; `""` is `"…" is not allowed to be empty`.
- Lengths are UTF-16 code units (`"😀😀a"` is 5 characters). Duplicate JSON keys: the last value wins.
- `__proto__` in the body is ignored (Joi copies the keys by assignment, which sets the prototype).
- isemail accepts single-label domains (`a@b`), quoted local parts and address literals, and rejects comments,
  folding white space, spaces inside quotes, `_` in domains and anything over the RFC lengths.
