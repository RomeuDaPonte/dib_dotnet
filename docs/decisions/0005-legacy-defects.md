# ADR-0005: Legacy defects D1–D24: what the new app does

- Status: Accepted (owner, 2026-11-14; D24 added 2026-11-16)
- Answers: PLAN.md §14 question 3 and the open "Decide" items of §9

The owner **accepted every proposed fix** in PLAN.md §9 and decided the open items below. All changes are made in
the new app only (the old app is not touched, C10) and none needs a database change: data written by the new app
stays readable by the legacy model (§4.3).

## Summary

| # | Decision |
|---|---|
| D1 | Fix: `PATCH /job/state/:jobId` uses `jobId` |
| D2 | Fix: delete by id; not in the UI |
| D3, D19 | Fix: source NAS folder from revision 0; `/share/Obras` destination keeps the legacy formula |
| D4 | Real `test -d` check on absolute paths; a failure logs a warning (same user-visible outcome) |
| D5 | Folder rename gated by the environment like the other NAS calls |
| D6 | Default task created before the NAS step |
| D7 | Intended validation applied |
| D8 | `POST /api/task` implemented as intended |
| D9 | Regex search kept; invalid regex → 400 |
| **D10** | **Fix** (owner): whole calendar year, see below |
| D11 | Keep: jobs without priority read as low, no icon |
| D12 | No token expiry (kept); own key in the new app; users log in once (no other API consumers, ADR-0004) |
| D13 | Explicit reload instead of `total + 1` |
| D14 | "Lembrar" checkbox removed |
| D15 | Keep |
| D16 | Plain-text `500 "Something failed."` + `x-request-id`; stack trace only in the log store |
| D17 | Fix: create the statistics sub-document |
| **D18** | **Fix** (owner): consultancy hours counted as intended, see below |
| D20 | Fix: correct shell quoting |
| **D21** | Keep existing paths; **reject `/` in new descriptives** (owner), see below |
| **D22** | **Fix** (owner): PDF blocks grow downward, see below |
| **D23** | **Fix** (owner): PDF date in Europe/Lisbon, see below |
| **D24** | **Keep** (owner, 2026-11-16): importing a budget's tasks from its own number deletes them, exactly as legacy (found in Phase 3 step 2) |

## D10: dashboard counts the whole year

Legacy counts jobs with `startDate` in `[{year}-01-01T00:00Z, {year}-12-31T00:00Z]`, so 31 December is left out
(`repositories/jobRepository.js`). The new app uses the **whole calendar year in Europe/Lisbon**:
`[1 Jan {year} 00:00 Lisbon, 1 Jan {year+1} 00:00 Lisbon)` (end exclusive). 1 January is winter time, so the start is
the same instant as in legacy; only jobs started on 31 December are added. The legacy comparison (§11)
allow-lists the dashboard counters for data with jobs started on 31 December. Unit tests: 31 Dec 23:59 Lisbon is
counted; 1 Jan 00:00 Lisbon of the next year is not.

## D18: registered consultancy hours

Legacy `job.updateRegisteredValue(value, "consultancy")` matches no `case` (`case development || consultancy` only
matches `development`), so the value is lost (`models/job.js`). The new app treats `consultancy` **exactly like
`development`**: it sets `devStatistic.numberOfHoursUsed = value`. This is what the rest of legacy already assumes:
the job cost table has a single "development" row that adds consultancy tasks to the development budget
(`front-end/src/_helpers/taskHelper.js`), so there is no separate consultancy value to overwrite. The legacy
UI only sends `development` (and nothing else calls the API, ADR-0004), so the fix makes the API consistent rather
than changing what users see today.

## D21: `/` in a budget descriptive

- **New or changed descriptives must not contain `/`.** Create (`POST /api/budget`), clone and edit
  (`PUT /api/budget/:id`) (`patchProperty` cannot change it), answer **400** with the plain-text message
  **`O descritivo não pode conter o carácter "/".`** The dialog validates the same rule before sending.
- Existing budgets that already have a `/` (0 in the production data of 2026-11-11) stay valid: they open, get new
  revisions and jobs, and their NAS paths keep the legacy (nested) form so the folders are found. Only changing the
  descriptive *to* a value with `/` is rejected.

## D22: long texts in the budget PDF grow downward

Legacy places every text at a fixed position, so a value that wraps overlaps the next line (client name over the
address, payment condition over the VAT line; samples `02`, `10` in `tests/fixtures/legacy/pdf/`). The new
generator keeps the legacy positions as the **starting** positions and lets a block push what is under it:

- **Client block** (grey box): if a line wraps, every following line of the block (address, location/zip, NIF,
  ATT.) moves down by the extra height, and the grey box grows by the same amount. If the box would then reach the
  separator line (legacy y = 280 + 40), the separator, "Descritivo", "Designação técnica" and the description move
  down by the overflow.
- **Company block** and **"Descritivo"** line: same rule (a wrapped line pushes the lines below it).
- **Footer**: if the payment condition (or any footer line) wraps, the lines below it move down. If the footer would
  then pass the bottom margin, the **whole footer moves up** by the overflow, so it stays on one page. "Total" keeps its legacy
  offset to the VAT line (5 pt above it) instead of a fixed y.
- Texts that fit are drawn **exactly** where legacy draws them, so budgets without long values stay pixel-identical.

Acceptance: the visual diff of PLAN.md §7.1 stays pixel-identical (within tolerance) for every reference PDF
**except `02` and `10`**, whose new expected images are reviewed and approved once by the owner and then kept as
fixtures. Unit tests: wrapped client name, wrapped payment condition, footer that would pass the bottom margin.

## D23: document date in Europe/Lisbon

"Data do Documento" is printed as `d/M/yyyy` of `budget.date` in **Europe/Lisbon** (legacy: the server's local
time, UTC in the container). This changes 37 of the 3,397 real budgets (created 23:00–24:00 UTC in summer time):
they now show the Lisbon day, e.g. sample `09` shows `25/5/2022` instead of `24/5/2022`.

- One setting for the business time zone, **`App:TimeZone`** (default `Europe/Lisbon`), used for the PDF date, the
  D10 year range and the log cleanup (`Logs:Cleanup:TimeZone` defaults to it). The container clock stays UTC; dates
  are stored in UTC as before.
- The visual diff for `09` expects `25/5/2022` (allow-listed against the legacy reference).
- Owner check N5 (time zone of the legacy container) is **no longer needed**: the new app does not depend on it.
- Unchanged on purpose (parity): the budget number year (`YY`) and the NAS folder year keep the legacy rules
  (§5, §7.1); folder names must keep matching the folders legacy created.
