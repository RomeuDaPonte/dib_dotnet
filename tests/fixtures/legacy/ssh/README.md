# NAS SSH command golden files (from the legacy code)

Expected SSH command sequences for the production-only NAS folder operations (PLAN.md §5, §5.1): budget
create, clone, rename (PUT) and job create. They are **derived from the legacy code by running it**, not
captured: the NAS is not reachable from the development network and is never contacted.

`tools/legacy/ssh-golden.js` loads the **unchanged** legacy routers (`routes/budget.js`, `routes/job.js`) and
models in one Node 16 process, with a real `mongo:4.1.4` (throwaway database `dib4t_ssh_golden`, dropped before
every scenario and at the end; the `dib4t` database is never touched). Only three things are replaced:

| Replaced | By | Why |
|---|---|---|
| `ssh2-promise` | a recorder that runs each command on a **virtual NAS file system** and answers like `ssh2-promise` 1.0.1 `exec()`: one channel per call, cwd = the SSH user's home, resolves stdout on close, **rejects on the first stderr data** | no NAS; the virtual FS shows the folders that result and which command fails |
| `telnet-client` | a recorder of the label payloads | no printers |
| the clock | a virtual "now" per step, `TZ=Europe/Lisbon` | year in the paths, deterministic output |

`NODE_ENV` is set per step (`prod` or unset) because the routes read it at request time. The SSH credentials in
`config/default.json` are never read or written out.

## Re-generating

```sh
tools/legacy/legacy.sh ssh-golden   # starts the legacy mongo if needed (and stops it again); ~10 s
```

Deterministic: two runs give byte-identical files (checked 2026-11-14).

## Files

| File | Content |
|---|---|
| `ssh-commands.json` | 22 scenarios. Per step: request, response (status + text; `expressErrorPage` = Express's default HTML error page, D16), the ordered **events** (`db` writes, `ssh` commands, `printer` payloads, `error`), DB counts after the step, NAS folders created/removed. |
| `folder-name-chars.json` | **Counts only** over the anonymized production fixture (`../prod-2026-11-11.anon.dibbak`; the anonymizer keeps spaces, punctuation and accents, so the counts equal the original's): how many client names and descriptives contain characters that matter for these commands. |

Each `ssh` event:

```jsonc
{
  "type": "ssh",
  "command": "mkdir -p /share/Orçamentos/2026/'Metalúrgica Silva & Filhos, Lda.'/'26-0001 - Máquina de corte'",
  "argv": [["mkdir", "-p", "/share/Orçamentos/2026/Metalúrgica Silva & Filhos, Lda./26-0001 - Máquina de corte"]],
  "ops": [{ "op": "mkdir-p", "path": "/share/Orçamentos/2026/Metalúrgica Silva & Filhos, Lda./26-0001 - Máquina de corte" }],
  "outcome": "resolves"           // or "rejects (stderr)" with "stderr", or "parseError" for broken quoting
}
```

- `command`: the exact string legacy passes to `exec()` (the **legacy** wire format).
- `argv`: how `sh` splits it (quotes removed; nothing expanded; `shellExpansions` lists any `$`, backtick, glob,
  … that `sh` would expand outside single quotes; none occur in these scenarios).
- `ops`: the effective operations with **absolute** paths after `cd` and relative-path resolution. **This is the
  contract for the new app's `INasFileShare`** (PLAN.md §5 "SSH safety"): it must produce the same `ops`
  (same folders, same order), while quoting arguments correctly instead of copying `command`.

## The contract per operation (as legacy does it)

Notation: `P(y, desc) = /share/Orçamentos/{y}/{client.name}/{budgetNumber} - {desc}`, `Q(y, desc)` the same under
`/share/Obras`. Names are used byte for byte: no trimming, no Unicode normalization.

**Budget create** (`POST /api/budget`) and **clone** (`POST /api/budget/clone/:id`), prod only, after `Budget.save`
(create: before the default task is saved; clone: the copied tasks are already saved). `y` = server-local current
year at the time of the request, `desc` = the new budget's descriptive.

1. `mkdir -p P` · 2. `mkdir -p P/Geometria` · 3. `mkdir -p P/Documentos`
4. `if test -d P; then echo 1; else echo 0; fi` → `parseInt(stdout) == 1`, else `500 "Erro ao criar pasta"`
   (create: the budget stays **without its default task**, D6).

**Budget rename** (`PUT /api/budget/:id`), **not gated by prod** (D5), only when `revision == 0` and the
descriptive changes, **before** `Budget.save`: `cd /;mv P(year of budget.date, old) P(year of budget.date, new)`.
If it rejects, the request is a 500 and the edit is **not saved**. (Outside prod the NAS cannot be reached, so the
real call fails too, after `ssh2-promise`'s reconnect attempts.)

**Job create** (`POST /api/job`), prod only, after `Budget.save` (`hasGenerateJob`) and `Job.save`. `b` = the
budget the job is made from (any revision), `y = year of b.date`, `desc = b.descriptive`:

0. `b.updateFolderPath(rev0.descriptive)`: no-op for revision 0 or an equal descriptive; otherwise a
   `ReferenceError` → 500, nothing below runs (D3).
1. `mkdir -p Q/CAD` · 2. `mkdir -p Q/CAD/Equipamento` · 3. `mkdir -p Q/CAM`
4. `cd /;mv P/Documentos Q/"Informações Técnicas"` (written as `share/…` relative to `/`)
5. `cd /;mv P/Geometria Q/CAD/Artigo`
6. 7. `if test -d share/Obras/…/"Informações Técnicas"…` and `…/CAD/Artigo…`: **not awaited** and relative to the
   SSH user's home, so they test a wrong path and the result is ignored (D4). "Problema ao mover pastas" never
   happens.
8. Labels: every `producao` printer, then every `maquinacao` printer, one after the other.

Anything else in `P` stays under `/share/Orçamentos`; `P` itself is not removed.

**Label payloads** (recorded in the job scenarios, `printer` events, with `wireHex`): both templates start with
**ESC (0x1B)**, i.e. `ESC i a 3` (switch the printer to template mode), which is invisible in the source. Wire bytes
= `UTF8(template + "\n")`.

## Findings (now in PLAN.md §9)

| Scenario | What happens in legacy | Real data (`folder-name-chars.json`) | PLAN |
|---|---|---|---|
| `job-create-rev1-later-year` | Revision made in a later year than revision 0: the move uses the **revision's** year, the folder is under revision 0's year → `mv` fails → 500 after the job is saved; empty `Q/CAD`, `Q/CAM` left behind; no labels | 21 revisions, **8 jobs** made from them | D19 |
| `job-create-rev1-renamed` | Revision with another descriptive than revision 0 → `ReferenceError` → 500 after the job is saved | 11 revisions, **5 jobs** made from them | D3 |
| `budget-create-apostrophe-*` | `'` in client name or descriptive breaks the quoting → `sh` syntax error → 500; budget saved without task, no folder | 0 | D20 |
| `budget-create-slash-in-descriptive`, `budget-rename-slash`, `job-create-slash-in-descriptive` | `/` in the descriptive makes **nested** folders; renaming to a name with `/` fails (500, edit not saved) | 0 | D21 |
| `budget-create-quotes-and-spaces` | Leading/trailing and double spaces are kept in the folder name | **33** descriptives with leading/trailing spaces, 3 with double spaces | §5 (no trimming) |
| `budget-create-shell-chars-in-client` | `$`, backticks, `( )` inside single quotes are literal: safe in legacy, and must stay literal in the new app | 0 | §5 |
| `budget-create-non-nfc-client` | Non-NFC names are passed through unchanged | 0 (in names/descriptives) | §5 |
| `job-create-*` | `test -d` checks are relative to the SSH home and not awaited | — | D4 |
| `budget-rename-rev0-dev` | Rename runs SSH outside prod | — | D5 |

## Limits

- The virtual file system follows POSIX `mkdir -p`/`mv`/`test -d` semantics. It does not model NAS-specific
  behavior (permissions, SMB name mangling, case-insensitivity, Unicode normalization of the file system).
  The real NAS is checked by the owner on site (PLAN.md §5.1, check **N2**).
- The SSH user's home (`/home/ssh-user` here) is a placeholder; the real one only matters for D4's wrong
  relative checks.
- Label bytes are recorded at the `send()` call; the real `telnet-client` then waits 2000 ms for a reply
  (PLAN.md §5, "Wire behavior").
