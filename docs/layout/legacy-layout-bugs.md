# Legacy layout bugs: "before" evidence and C9 checklist

> Phase 0 (PLAN.md §10). Recorded 2026-11-13 against the **unchanged** legacy front-end
> (`tools/legacy/legacy.sh up --fresh && legacy.sh seed && legacy.sh layout`).
> Numbers per case: [legacy-layout-matrix.md](legacy-layout-matrix.md) (generated).
> Screenshots: `artifacts/legacy-layout/<page>/<viewport>_z<zoom>_f<font>.jpg`, browsable in
> `artifacts/legacy-layout/index.html` (not committed: about 20 MB; regenerate with the commands above, about 4 min).

**Matrix (§8.4):** 11 pages × 5 viewports (360×640, 768×1024, 1366×768, 1920×1080, 2560×1440) ×
zoom 100/150/200 % × root font 16/20/24 px = **495 cases; 190 have at least one measured issue**.
The metrics only see controls and text. Image/background problems (LB-02) and content hidden inside
inner scroll areas were found by looking at the screenshots.

Each item is a checkbox for the new UI. It is ticked when the same page passes the same cases in the
E2E layout matrix (§11). "Rule" points to the §8.4 rule that fixes it.

## Shell (all `/landing/*` pages)

- [ ] **LB-01 Permanent drawer on small screens.** The 240 px drawer is always open, also on phones.
  At 360 px the content gets about 120 px. From 150 % zoom on 360×640 the page scrolls horizontally by
  74 px (H74 on every page), and at 200 % the menu items themselves are outside the viewport (O6–O7).
  Evidence: `budgets/360x640_z100_f16.jpg` (table reduced to its warning icons), every page at 360×640 150/200 %.
  Cause: `landing.jsx:88` `variant="permanent"`, `landingStyles.js` `height: 100vh`, absolute AppBar.
  Rule 4 (temporary drawer below `md`, mini on `md`, sticky AppBar).
- [ ] **LB-01b AppBar name truncated to "U."** on phones (`*/360x640_*`). Rule 4/9.

## Login (`/`)

- [ ] **LB-02 Image column cropped.** The image box is `height: 100vh; width: 100vh`, so its width
  follows the window height: on portrait/tablet screens only the bottom-left of the background shows and
  the logo + "WebApp" disappear (`login/768x1024_z100_f16.jpg`). On phones the image column is hidden
  (acceptable). Not caught by the metrics (no control/text involved). Cause: `account/login.css`
  `.gridClass`. Rule 7.

## Budget details (`/landing/budgets/{id}`): 44/45 cases

- [ ] **LB-03 Header grid drawn over the task rows.** Even on a normal laptop (1366×768, 100 %, 16 px)
  the header block (client, descriptive, margin, totals, payment method, validity) overlaps the task
  list: inputs on top of each other (X21–X29) and text over text (T5–T10), e.g. `M / Pagamento ↔ €`,
  `C / Margem ↔ Qtd`, `Validade Proposta ↔ 132`, task description over `Qtd`/`Unit`. Only 2560×1440 at
  100 %/16 px is clean. Evidence: `budget-details/1366x768_z100_f16.jpg`. Cause: fixed-height header
  boxes, `fontSize: 30` totals (`budgetGeneralDetails.jsx:313–352`). Rules 1, 2, 3, 9.
- [ ] **LB-04 Totals in px.** The 30 px total/margin text does not follow the font-size setting and
  collides with neighbours when the rest grows. Rule 1.

## Job details (`/landing/job/{id}`): 42/45 cases

- [ ] **LB-05 Cost table text collisions.** Column headers and values overlap (`Orçamento`, `Registo`,
  `Desvio`, `Margem` over the amounts; `€` over numbers) in 42/45 cases, already at 1366×768 100 %
  (T15) and at 1920×1080 from 20 px font. Evidence: `job-details/1366x768_z100_f24.jpg`.
  Cause: `jobDetails.css` (`grid-template-rows: 12vh 65vh`, `height: 4vh`, fixed `rem` widths). Rules 1, 2, 3.
- [ ] **LB-06 Observations box over the state dates.** The comments field overlaps the
  `devEndedInDate`/`onApprovalEndedInDate` date inputs (X1–X3 in 20 cases). Cause: `position: absolute`
  boxes (`jobDetails.css:186,193`, `jobDetailsHead.jsx:168`). Rule 3.
- [ ] **LB-07 Page cut at a fixed height.** Content below the `65vh` row (verifications "Controlo",
  "Verificação") is cut off instead of the page growing. Evidence: `job-details/1366x768_z100_f24.jpg`. Rule 2.
- [ ] **LB-08 Header boxes don't wrap.** Order identification and "Identificação oficial" are cut at
  the right edge; inline `width: 60px` (`jobDetailsHead.jsx:108`). Rule 3.

## Jobs board (`/landing/job`): 39/45 cases

- [ ] **LB-09 State tabs cut off and unreachable.** The 8 state tabs don't scroll and don't wrap: tabs
  are cut (C6–C21) or completely outside the card (O1–O7, mostly "Faturadas", "Para faturação",
  "Todas"), already at 1366×768 100 % and at 1920×1080 from 150 % zoom. Evidence:
  `jobs/1920x1080_z150_f20.jpg` (first tab cut, "Faturadas" missing). Rule 5 (scrollable tabs) / 3.
- [ ] **LB-10 Row action column cut off** on the right; the table card is cut at the bottom (pagination
  half visible) because of fixed heights. Same evidence. Cause: `tableStyles.js` `minWidth: 750`,
  `jobStyle.js` fixed `48px` icon columns. Rules 2, 5.
- [ ] **LB-11 "More" menu buttons overlap** each other at 24 px font (X2 in 15 cases). Cause: fixed
  48 px icon columns. Rule 1.

## Settings (`/landing/settings`): 29/45 cases

- [ ] **LB-12 Settings tabs cut off.** Tabs (Utilizadores, Preços, M/Pagamento, Impressoras, Base de
  dados, Info) are cut or outside the card from 768 px, and on laptops from 150 % zoom.
  Evidence: `settings/768x1024_z100_f16.jpg`. Rule 5 / 3.
- [ ] **LB-13 "Add user" button cut at the right edge** (same evidence). Cause: `settingsStyles.js`
  `minWidth: 650`. Rules 3, 5.

## Lists (budgets, entities, job receivings) and simple pages (home, dashboard, readings)

- [ ] **LB-14** No issue of their own in the metrics. They fail only with the shell (LB-01) at
  360×640 ≥ 150 %. Tables are clipped inside their card on phones without a visible scroll hint
  (`budgets/360x640_z100_f16.jpg`). Rule 5 (scroll container + card list on phones).
- [ ] **LB-15 Dashboard chart has a fixed 600 px height** (`dashboardIndex.jsx:34`). It does not grow
  on large screens or shrink on short ones. Rule 8.

## How the new UI is checked

The new app runs the same matrix and checks (§8.4 "Verification", §11 item 8) on every page. All items
above must be ticked before Phase 6. The legacy numbers stay here as the baseline.
