# Approved images of the new app's budget PDF (D22, D23)

ADR-0005: for the reference budgets the new app prints differently on purpose, the visual diff compares against
approved images of the new app instead of the legacy PDF. Rendered with PDFium (PDFtoImage) at 100 DPI, one PNG per
page (`<sample>-p<n>.png`).

| Sample | Why it differs from the legacy PDF |
|---|---|
| `02-revision` | D22: the payment condition wraps; the VAT line moves down and the footer moves up to stay above the bottom margin. |
| `05-most-lines` | D22: the client name wraps (same client as 10); the address block and the grey box move/grow. |
| `09-utc-date-differs` | D23: "Data do Documento: 25/5/2022" (Lisbon) instead of 24/5/2022 (UTC). |
| `10-longest-texts` | D22: the client name wraps; the address block and the grey box move/grow. |

**Status: candidates generated 2026-11-16, owner approval pending.** Until approved they guard against regressions.
To regenerate after an intended change: `DIB4T_PDF_APPROVE=1 dotnet test --project tests/Dib4t.Infrastructure.Tests
--filter-class "Dib4t.Infrastructure.Tests.Pdf.BudgetPdfNewAppTests"` (run twice: the first run writes the files), look
at the images, commit them. Every other sample must stay pixel-identical to the legacy PDF (`BudgetPdfNewAppTests`).
