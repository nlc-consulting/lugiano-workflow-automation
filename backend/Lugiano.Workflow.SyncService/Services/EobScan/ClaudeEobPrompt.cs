namespace Lugiano.Workflow.SyncService.Services.EobScanning;

// System prompt + tool schema for EOB extraction. Locked in via the
// backend/scratch/EobScanSpike validation runs against the 4/22
// non-lockbox mail PDF (with DS-produced EOB_Details as ground truth).
// Bump PromptVersion any time text below changes — used in cost/quality
// reporting + cache invalidation.
public static class ClaudeEobPrompt
{
    public const string PromptVersion = "eob-v1-2026-06-30";

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

2) LINE_ITEMS — one entry per service line on the EOB.
   - page_number: the PDF page where the line appears (see PAGE NUMBERING below).
   - claim_number: as printed (preserve dashes, leading zeros, suffixes including
     parenthesized codes like "(0396)").
   - patient_name: EXACTLY as printed — do not reorder or normalize. If it reads
     "ZAMBRANO, AMADO" output that, if it reads "AMADO ZAMBRANO" output that.
   - bill_number: the carrier's internal bill/claim line id (often blank).
   - service_date: date of service for this line, as printed.
   - check_number: the check that paid this line, if you can determine it from
     context (same EOB / same payer). Leave blank if unsure.
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

GENERAL RULES:
- Include ZERO-PAID lines (denials, exhausted-policy rows). Their reason codes
  drive triage downstream — they are NOT noise.
- Do NOT normalize patient names, claim numbers, dates, or amounts. Preserve
  the carrier's original formatting.
- Numbers: strip currency symbols and commas; emit as plain decimals.
- If a value is illegible or unmistakably absent, leave the field empty rather
  than guessing.

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
