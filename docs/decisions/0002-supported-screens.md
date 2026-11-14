# ADR-0002: Phones, tablets and desktops are all supported

- Status: Accepted (owner, 2026-11-14)
- Answers: PLAN.md §14 question 2

## Decision

Every page must work on **phones, tablets and desktops**. The smallest supported width is **360 CSS px**
(phone in portrait). This confirms the assumption of PLAN.md §8.4 (C9).

## Consequences

- The §8.4 responsive matrix is the acceptance test for every page, from the first page of Phase 1: viewports
  360×640 (phone), 768×1024 (tablet), 1366×768, 1920×1080, 2560×1440, each at browser zoom 100/150/200 % and root
  font size 16/20/24 px.
- Navigation (§8.4 breakpoints): temporary (overlay) drawer below `md` (phones, small tablets), mini drawer on `md`,
  permanent drawer on `lg` and up.
- Wide tables (budgets, jobs, entities, the job cost table) need a phone layout (cards or horizontal scroll inside
  the table only, never page scroll), and every control must stay reachable by touch (≥ 44 px targets).
- The legacy layout bugs LB-01 … LB-15 (`docs/layout/legacy-layout-bugs.md`) are fixed, not ported.
