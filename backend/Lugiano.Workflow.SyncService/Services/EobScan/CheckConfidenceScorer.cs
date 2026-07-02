using System.Text.RegularExpressions;
using Lugiano.Workflow.SyncService.Workflow.Models;

namespace Lugiano.Workflow.SyncService.Services.EobScanning;

// Post-extraction pass tagging each check row High/Medium/Low confidence,
// using patterns from the 7/1/2026 reconciliation (scan $33,961.57 vs
// biller-audited $31,185.63; dropping 6 hallucinations totalling $2,344.50
// lands the clean total at the real-received amount).
//
// Rules are intentionally SPECIFIC (asterisk/decimal/dollar-sign) not general,
// so legitimate checks with unusual real formats survive (e.g. State Farm's
// "1 13 173843 J" with spaces is REAL — dedupe handles header-vs-MICR).
public static class CheckConfidenceScorer
{
    public const string High = "High";
    public const string Medium = "Medium";
    public const string Low = "Low";

    // Patterns proven to indicate a hallucinated / misread check number:
    //   $253.18       — the AMOUNT read as a check number (dollar sign prefix)
    //   5268.8        — decimal in check number = a Total field misread
    //   600883*20004  — asterisk = OCR corruption on a near-blank page
    //   $XX.XX shape  — check# format matches money format
    private static readonly Regex CheckNumHasDollarSign = new(@"\$", RegexOptions.Compiled);
    private static readonly Regex CheckNumHasDecimal = new(@"^\d+\.\d+$", RegexOptions.Compiled);
    private static readonly Regex CheckNumHasAsterisk = new(@"\*", RegexOptions.Compiled);

    // Score checks WITHOUT the isolation rule — same as Score(checks, null).
    // Kept for callers that only have the checks list (e.g. tests, callers that
    // haven't loaded line items yet).
    public static void Score(List<EobScanCheck> checks) => Score(checks, null);

    // Score checks WITH the isolation rule enabled.
    //
    // isolationLineCheckNumbers is the set of check_number values that appear on
    // line items in the same scan. When provided, any check whose check number
    // has ZERO matching line items gets flagged Low with reason "no line items
    // reference this check". Rationale: real checks always pay for real service
    // lines; a "check" with no downstream lines is almost certainly a page
    // misread (Explanation of Bill Review, endorsement backer, CC page). Caught
    // ACE $268.84 (bill-review page) and GEICO $87.58 (legal cc page) on 7/1
    // where syntactic patterns alone missed them.
    //
    // Pass null to disable the isolation check (backward compatible).
    public static void Score(List<EobScanCheck> checks, HashSet<string>? isolationLineCheckNumbers)
    {
        if (checks is null || checks.Count == 0) return;

        // ---- Pass 1: pattern rules on the check number ----
        foreach (var c in checks)
        {
            var cn = (c.CheckNumber ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(cn))
            {
                c.Confidence = Low;
                c.HallucinationReason = "empty check number";
                continue;
            }
            if (CheckNumHasDollarSign.IsMatch(cn))
            {
                c.Confidence = Low;
                c.HallucinationReason = "dollar-sign in check number (amount misread as check#)";
                continue;
            }
            if (CheckNumHasDecimal.IsMatch(cn))
            {
                c.Confidence = Low;
                c.HallucinationReason = "decimal-only check number (Total field misread)";
                continue;
            }
            if (CheckNumHasAsterisk.IsMatch(cn))
            {
                c.Confidence = Low;
                c.HallucinationReason = "asterisk in check number (OCR corruption)";
                continue;
            }
            // Check# equals the amount as text — the "Total misread as check#"
            // pattern that slips past the decimal regex when there's no decimal.
            if (decimal.TryParse(cn, out var cnAsNum) && cnAsNum == c.Amount)
            {
                c.Confidence = Low;
                c.HallucinationReason = "check number equals amount";
                continue;
            }
            // Check numbers < 4 chars are almost never real (real ones ~5-12 digits).
            if (cn.Length < 4)
            {
                c.Confidence = Low;
                c.HallucinationReason = $"check number too short ({cn.Length} chars)";
                continue;
            }

            // Provisionally High; may downgrade in the dedup pass.
            c.Confidence = High;
            c.HallucinationReason = null;
        }

        // ---- Pass 2: duplicate detection ----
        // Duplicates = same amount, same/adjacent page, and whitespace-stripped
        // check numbers match. Handles the STATE FARM header-vs-MICR double-emit
        // ("113172377J" vs "1 13 172377 J"). Keep first, mark second Low.
        for (int i = 0; i < checks.Count; i++)
        {
            var a = checks[i];
            if (a.Confidence == Low) continue;  // already flagged
            for (int j = i + 1; j < checks.Count; j++)
            {
                var b = checks[j];
                if (b.Confidence == Low) continue;
                if (a.Amount != b.Amount) continue;
                if (Math.Abs(a.PageNumber - b.PageNumber) > 1) continue;
                var normA = NormalizeCheckNum(a.CheckNumber);
                var normB = NormalizeCheckNum(b.CheckNumber);
                if (normA != normB) continue;
                b.Confidence = Low;
                b.HallucinationReason = $"duplicate of check on page {a.PageNumber} #{a.CheckNumber} (MICR/header double-emit)";
            }
        }

        // ---- Pass 2b: isolation check (line-item cross-reference) ----
        // A real check pays for real service lines. If NO line item in the same
        // scan references this check's number, the "check" is almost certainly
        // a page misread — most often an "Explanation of Review" or "Bill
        // Review" page where a payment amount was mistaken for a check stub, or
        // an endorsement backer / CC page picking up unrelated numbers.
        if (isolationLineCheckNumbers is not null)
        {
            foreach (var c in checks)
            {
                if (c.Confidence == Low) continue;  // already flagged
                var cn = NormalizeCheckNum(c.CheckNumber);
                if (string.IsNullOrEmpty(cn)) continue;
                if (!isolationLineCheckNumbers.Contains(cn))
                {
                    c.Confidence = Low;
                    c.HallucinationReason = "no line items reference this check number (isolated)";
                }
            }
        }

        // ---- Pass 3: Medium tier for minor OCR noise ----
        // Downgrade High → Medium when the check number has spaces (State Farm
        // "1 13 172377 J" style) — still legit, still counts toward clean total,
        // but flagged for the biller to sanity-check on ingest.
        foreach (var c in checks)
        {
            if (c.Confidence != High) continue;
            var cn = c.CheckNumber ?? string.Empty;
            if (cn.Contains(' '))
            {
                c.Confidence = Medium;
            }
        }
    }

    // Strip whitespace + uppercase so header-spacing and MICR no-space forms compare equal.
    private static string NormalizeCheckNum(string? cn) =>
        string.IsNullOrEmpty(cn) ? "" : new string(cn.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    // Totals sliced by tier — for the export ("raw vs clean total") and the dashboard chip.
    public static CheckTotals ComputeTotals(IEnumerable<EobScanCheck> checks)
    {
        decimal high = 0, medium = 0, low = 0;
        int hC = 0, mC = 0, lC = 0;
        foreach (var c in checks)
        {
            switch (c.Confidence)
            {
                case High:   high   += c.Amount; hC++; break;
                case Medium: medium += c.Amount; mC++; break;
                case Low:    low    += c.Amount; lC++; break;
                default:     high   += c.Amount; hC++; break; // unscored ⇒ treat as high
            }
        }
        return new CheckTotals(hC, mC, lC, high, medium, low);
    }
}

// Snapshot of tier-sliced totals for a scan. CleanTotal (High+Medium) is what
// the biller uses to reconcile against bank deposits. RawTotal includes Low
// tier (i.e. the raw extractor output before hallucination filtering) — useful
// for auditing the delta.
public sealed record CheckTotals(
    int HighCount,
    int MediumCount,
    int LowCount,
    decimal HighAmount,
    decimal MediumAmount,
    decimal LowAmount)
{
    public int TotalCount => HighCount + MediumCount + LowCount;
    public decimal RawTotal => HighAmount + MediumAmount + LowAmount;
    public decimal CleanTotal => HighAmount + MediumAmount;
}
