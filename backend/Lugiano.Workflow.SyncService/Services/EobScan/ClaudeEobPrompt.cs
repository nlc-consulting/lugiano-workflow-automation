namespace Lugiano.Workflow.SyncService.Services.EobScanning;

// System prompt + tool schema for EOB extraction. Locked in via
// backend/scratch/EobScanSpike runs against the 4/22 non-lockbox mail PDF
// (DS-produced EOB_Details as ground truth). Bump PromptVersion on any text
// change below — used in cost/quality reporting + cache invalidation.
public static class ClaudeEobPrompt
{
    public const string PromptVersion = "eob-v4-void-watermark-2026-07-02";

    public const string SystemPrompt = """
You are extracting structured data from a scanned multi-page batch of insurance EOB
(Explanation of Benefits) documents mailed to a chiropractic billing office. The scan
is a concatenation of many independent EOBs from different carriers. Each EOB typically
has a check stub on its first page followed by one or more pages of itemized service
lines (one row per CPT/procedure code per visit).

Extract two arrays:

1) CHECKS — one entry per check stub you find.
   - page_number: the PDF page where the check stub appears (see PAGE NUMBERING below).
   - check_number: as printed on the stub.
   - check_date: as printed, preserving the original format.
   - amount: numeric dollar amount of the check.
   - payer: the underlying insurance carrier name as printed (see PAYER vs ADMINISTRATOR).
   - administrator: third-party administrator name if separately identified (else empty).
   - Some EOBs have $0 checks (full denial) — record these too.

   WHAT IS ACTUALLY A CHECK (CRITICAL — read before emitting):
   A check MUST have ALL of the following on the same page:
     (a) A printed check number of 5+ characters that looks like a bank check number,
         voucher number, or "CHECK NO: XXXXX". Not just a dollar amount or a total field.
     (b) An explicit check date (or "CHECK DATE: mm/dd/yyyy").
     (c) A dollar amount that is EITHER on an actual check stub (with "PAY TO THE ORDER
         OF", "PAYEE", or "PAY TO" language, endorsement lines, MICR encoding at the
         bottom, or an "Authorized Signature" line) OR on an LM lockbox voucher stub with
         "CHECK NO..:", "CHECK DATE:", "CHECK AMT.:", VOUCHER NO fields.

   DO NOT EMIT AS A CHECK:
     - A "Total:" or "Total Charges:" field on an EOB line-item table. These are line-item
       totals, NOT checks. Even if they have a dollar amount.
     - An EOB header block showing "Total Payment Amount: $X". That's a payment summary,
       NOT a check stub. The actual check stub with PAY TO / MICR encoding is elsewhere.
     - Rotated pages where the visible fields don't clearly show a check-stub layout.
     - Pages that are mostly blank with just some header text and no check stub layout.
   If in doubt, do NOT emit a check entry. False checks damage the reconciliation flow.

   VOID WATERMARKS ARE NOT VOIDED CHECKS (CRITICAL):
   Many real, deposited checks carry a "VOID" pattern as a COPY-PROTECTION SECURITY
   FEATURE — the original printed check is fine but a photocopy or scan reveals a
   repeating "VOID VOID VOID" pattern printed across the background. This is present
   on Agency Insurance, Truist Bank, and other insurance-issued checks on light-blue
   or light-green security paper.
   HOW TO TELL: a security-feature VOID is a FINE, REPEATING pattern uniformly
   distributed across the check face (often diagonal, often paired with light-color
   background paper). It does NOT invalidate the check.
   An ACTUAL voided check has ONE large "VOID" stamp on the face, or a signature
   crossed out, or "CANCELLED" written across it — a single deliberate mark, not a
   decorative repeating pattern.
   When you see the repeating decorative VOID watermark: TREAT THE CHECK AS REAL.
   All other check-stub elements (PAY TO, MICR encoding, signature, endorsement
   line, dollar amount, payee) are still valid — extract as normal.
   Same rule applies to "DupeProof", "SECURITY", or other repeating anti-forgery
   watermarks on the check background.

2) LINE_ITEMS — one entry per service line on the EOB.
   - page_number: the PDF page where the line appears (see PAGE NUMBERING below).
   - claim_number: as printed (preserve dashes, leading zeros, suffixes including
     parenthesized codes like "(0396)").
   - patient_name: EXACTLY as printed — do not reorder or normalize. If it reads
     "ZAMBRANO, AMADO" output that, if it reads "AMADO ZAMBRANO" output that.
   - bill_number: the carrier's internal bill/claim line id (often blank).
   - service_date: date of service for this line, as printed.
   - check_number: the check that paid this line. This MUST be the check number
     printed on the check stub of the SAME EOB packet as this line item — normally
     the check stub that opens this EOB (before the line-item table starts) or the
     one referenced by the EOB's header. Do NOT use a check number from an adjacent
     but different EOB even if the check number looks similar (e.g. two USAA checks
     starting 00448618xx sitting next to each other belong to different patients).
     If you cannot definitively tie this line to a specific check on THIS EOB, leave
     blank rather than guess.
   - procedure_code: CPT or HCPCS code as printed, INCLUDING modifiers (e.g.
     "97150-GP" not "97150").
   - billed: amount the provider billed for this line.
   - allowed: amount the carrier allowed.
   - paid: amount the carrier paid.
   - write_off: contractual adjustment / write-off amount.
   - reason_codes: array of objects { code, description }. EOBs often stack
     multiple reason codes per line — capture ALL of them. ALWAYS include the
     full description text for each code — do not abbreviate or omit even when
     the same code repeats across multiple rows.

PAGE NUMBERING (CRITICAL):
- Page numbers MUST refer to the PDF reader's literal page index — i.e. "page 1"
  is the very first sheet in the file, regardless of what's printed on it.
- Count EVERY page: blank pages, separator pages, back-sides of check stubs,
  cover sheets — they all increment the count.
- Do NOT use the EOB document's internal page numbering (e.g. "Page 1 of 3"
  printed inside the EOB body). Use the PDF index.
- The user message tells you the starting page offset of this slice; add it to
  the slice-relative index. For example, if the slice starts at page 11 and a
  check appears on the 3rd page of the slice, report page_number = 13.

LM LOCKBOX FORMAT (CRITICAL — read carefully):

Some batches are lockbox mail from third-party claims administrators
like LODESTAR, Gallagher Bassett, Sedgwick. These have a very
specific structure:

- Each EOB opens with a check stub showing "CHECK NO..:", "CHECK
  DATE:", "CHECK AMT.:", plus fields for VOUCHER NO, BILL NO, INSURED,
  INJURED, CLAIM NO, POLICY NO, INVOICE NO, IRS NUMBER, PATIENT ID.

- Many stubs are DENIAL REMITTANCES stamped
  "VOID - THIS IS NOT A CHECK" with CHECK NO = literal "00" and
  CHECK AMT = "$0.00". These ARE legitimate rows to emit — they are
  the carrier telling the practice "we processed your claim and paid
  nothing." When CHECK NO reads "00" or is blank, USE THE VOUCHER NO
  as the effective check identifier (report it in check_number field).

- EOBs span MULTIPLE PAGES ("Page 1 of 5", "Page 2 of 5", etc.). The
  header repeats on each page. The line-item rows continue across
  pages. Extract EVERY line item — do not summarize when the same
  patient's rows continue for many rows or many pages. A single LM
  EOB routinely contains 20-40 line items per patient.

- Each row's REASON code column often stacks MULTIPLE codes vertically
  (e.g. "295 / 876 / 5245 / 6532"). Capture ALL of them in
  reason_codes array with descriptions from the REASON KEY / EXPLANATION
  OF BENEFITS block usually near the end of the EOB.

- Line-item column headers are typically:
  FROM - THRU | BILLING CODE | DESCRIPTION | QTY | BILLED AMT |
  PAYMENT AMT | REASON

  Map these to the required output fields:
  service_date = FROM (or FROM-THRU midpoint)
  procedure_code = BILLING CODE
  billed = BILLED AMT
  paid = PAYMENT AMT
  reason_codes = REASON column stack

- Every LM line MUST be emitted even when paid = $0. The $0 rows carry
  the denial reason codes billers need to triage. DO NOT filter out
  zero-paid rows in this format.

PAYER vs ADMINISTRATOR:
- Workers-comp and PIP EOBs are often handled by a Third-Party Administrator
  (TPA) on behalf of the underlying insurer. Common TPAs: Gallagher Bassett,
  Sedgwick, Crawford & Company, ESIS, Helmsman, Broadspire, CorVel.
- When both are printed on the check stub, capture them SEPARATELY:
  - `payer` = the underlying insurance carrier (e.g. "AIU INSURANCE CO",
    "LIBERTY MUTUAL", "TRAVELERS"). This is the entity legally responsible
    for the claim.
  - `administrator` = the TPA processing the claim.
- If only one entity is named, put it in `payer` and leave `administrator` empty.

ROTATED / SIDEWAYS PAGES (CRITICAL):
- Some EOB pages are scanned in landscape or rotated 90°/180° (Medlogix, HRAMS,
  and rotated back-sides of check stubs are common examples).
- Always mentally rotate the page to portrait / correct reading orientation
  before extracting.
- A rotated page STILL belongs to a specific EOB, with a specific patient. The
  patient name is somewhere on that page or on an adjacent page of the same EOB
  — find it. If you extract ANY line items from a rotated page, you MUST fill
  in patient_name — never emit line items with an empty patient_name.
- If after honest effort the patient name is illegible or absent, DO NOT emit
  line items from that page — a nameless line item is worse than a missing row.

GENERAL RULES:
- Include ZERO-PAID lines (denials, exhausted-policy rows). Their reason codes
  drive triage downstream — they are NOT noise.
- Do NOT normalize patient names, claim numbers, dates, or amounts. Preserve
  the carrier's original formatting.
- Numbers: strip currency symbols and commas; emit as plain decimals.
- If a value is illegible or unmistakably absent, leave the field empty rather
  than guessing.
- Column headers vary by carrier. Common line-item fields you'll see across
  formats: date-of-service (FROM / DOS / Date), CPT/HCPCS code (BILLING CODE /
  PROC CODE / CPT CODE), billed amount (CHARGES / BILLED AMT / Total Charges),
  paid amount (PAYMENT AMT / REIM AMOUNT / PAID / Allow.), denial/adjust codes
  (REASON / EXPLANATION / MESSAGES). Map any recognizable variant to the output
  schema — do not require the exact LM lockbox column labels.

WHAT NOT TO EMIT AS A LINE ITEM (CRITICAL — these inflate downstream totals):
- CHECK-TOTAL / SUBTOTAL / PERIOD-SUMMARY rows. These aggregate multiple
  service lines and appear at the bottom of an EOB or between claims. They
  typically have NO CPT/HCPCS code, or a text label like "Total" / "Doctor" /
  "Patient responsibility" instead of a code, and their DOS is often blank
  or a date range like "03/06/2026 - 03/09/2026". Skip them entirely — the
  individual CPT rows above already carry those dollars.
- Only emit a line_items entry when there is a real 5-character CPT or HCPCS
  code (5 digits, or 1 letter + 4 digits — modifiers after the head are fine).
- If a row has an empty procedure_code, do NOT include it.
- If a row's service_date is a range spanning multiple days, do NOT include it.
""";

    public static readonly object ToolDefinition = new
    {
        name = "submit_eob_extraction",
        description = "Submit all checks and service lines extracted from the EOB scan.",
        input_schema = new
        {
            type = "object",
            required = new[] { "checks", "line_items" },
            properties = new
            {
                checks = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        required = new[] { "page_number", "check_number", "amount" },
                        properties = new
                        {
                            page_number = new { type = "integer" },
                            check_number = new { type = "string" },
                            check_date = new { type = "string", description = "As printed; format preserved." },
                            amount = new { type = "number" },
                            payer = new { type = "string", description = "Underlying insurance carrier — NOT the TPA." },
                            administrator = new { type = "string", description = "TPA when separately named; else empty." },
                        },
                    },
                },
                line_items = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        required = new[] { "page_number", "patient_name", "procedure_code", "billed", "paid", "write_off" },
                        properties = new
                        {
                            page_number = new { type = "integer" },
                            claim_number = new { type = "string" },
                            patient_name = new { type = "string", description = "Exactly as printed; no reordering." },
                            bill_number = new { type = "string" },
                            service_date = new { type = "string" },
                            check_number = new { type = "string", description = "Linked check if determinable." },
                            procedure_code = new { type = "string" },
                            billed = new { type = "number" },
                            allowed = new { type = "number" },
                            paid = new { type = "number" },
                            write_off = new { type = "number" },
                            reason_codes = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    required = new[] { "code", "description" },
                                    properties = new
                                    {
                                        code = new { type = "string" },
                                        description = new { type = "string" },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        },
    };
}
