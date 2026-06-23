using Dapper;
using Lugiano.Workflow.SyncService.ChiroTouch;

namespace Lugiano.Workflow.SyncService.Services;

// Applies an EOB to PSChiro for real. Re-runs the preview match on the
// uploaded xlsx (so the client can't tamper with the proposed updates) and
// writes every unambiguous match in a SINGLE transaction. Ambiguous +
// unmatched lines are returned as a report — no write attempted.
//
// Per-line write recipe (Primary insurance only — secondary/patient
// adjustments are future scope):
//   1. UPDATE Transactions
//        SET PriPaidAmt = PriPaidAmt + @paid,
//            WOAmt      = WOAmt      + @wo
//      WHERE ID = @tranId.
//   2. UPDATE BilledCharges
//        SET PaidDate   = GETDATE(),
//            AppliedAmt = @paid          -- per-charge payment slice
//      WHERE ChargeTranID = @tranId AND PaidDate IS NULL.
//      (BilledCharges row may be absent if the charge was never billed —
//       we still post to Transactions; the AR rollup will reflect it.)
//      Note: BilledCharges.AppliedAmt = paid only, NOT paid+writeoff.
//      Write-offs live on Transactions.WOAmt (which we already update).
//      Also leaving PaymentTranID NULL — populating it would require
//      INSERTing a payment Transactions row (TranType='P') and linking,
//      which we can add later for full ledger parity if needed.
//
// Idempotency guard: skip if the new PriPaidAmt would exceed TranAmt
// (over-payment) OR if BilledCharges.PaidDate is already set (already
// posted). Skipped rows ride out in the response as Skipped[].
public sealed class EobPostingService
{
    private readonly ISourceDbWriteConnectionFactory _writeDb;
    private readonly EobPreviewService _preview;
    private readonly ILogger<EobPostingService> _logger;

    public EobPostingService(
        ISourceDbWriteConnectionFactory writeDb,
        EobPreviewService preview,
        ILogger<EobPostingService> logger)
    {
        _writeDb = writeDb;
        _preview = preview;
        _logger = logger;
    }

    public bool IsConfigured => _writeDb.IsConfigured;

    public async Task<EobPostingResult> ApplyAsync(Stream xlsxStream, CancellationToken ct)
    {
        // Re-run the match so the writes always reflect the current
        // ChiroTouch state, not whatever the client thinks they saw.
        // Stream has to be re-readable — copy to a memory buffer.
        using var buffer = new MemoryStream();
        await xlsxStream.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var preview = await _preview.PreviewAsync(buffer, ct);

        var applied = new List<AppliedLine>();
        var skipped = new List<SkippedLine>();

        if (preview.Matched.Count == 0)
        {
            return new EobPostingResult(
                TotalLines: preview.TotalLines,
                Applied: applied,
                Skipped: skipped,
                Ambiguous: preview.Ambiguous,
                Unmatched: preview.Unmatched);
        }

        await using var conn = _writeDb.Create();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            foreach (var m in preview.Matched)
            {
                // Re-read the LIVE state for this charge inside the tx — guards
                // against a race where another process posted between preview
                // and apply. Cheap; single-row read.
                var live = await conn.QuerySingleOrDefaultAsync<LiveTx>(
                    """
                    SELECT t.ID                              AS TranId,
                           t.TranAmt                         AS TranAmt,
                           ISNULL(t.PriPaidAmt, 0)           AS PriPaidAmt,
                           ISNULL(t.WOAmt,      0)           AS WOAmt,
                           bc.ID                             AS BilledChargeId,
                           bc.PaidDate                       AS PaidDate
                    FROM   dbo.Transactions t
                    LEFT JOIN dbo.BilledCharges bc ON bc.ChargeTranID = t.ID
                    WHERE  t.ID = @tranId;
                    """,
                    new { tranId = m.Match.TranId }, transaction: tx);

                if (live is null)
                {
                    skipped.Add(new SkippedLine(m.Line, m.Match.TranId,
                        "Charge no longer exists in PSChiro."));
                    continue;
                }

                var newPaid = live.PriPaidAmt + m.Line.PaidAmount;
                var newWo   = live.WOAmt      + m.Line.WriteOffAmount;

                if (newPaid + newWo > live.TranAmt + 0.01m) // tolerance for cents rounding
                {
                    skipped.Add(new SkippedLine(m.Line, m.Match.TranId,
                        $"Over-payment guard: charge ${live.TranAmt:F2} would receive ${newPaid + newWo:F2} (paid+writeoff)."));
                    continue;
                }
                if (live.PaidDate is not null)
                {
                    skipped.Add(new SkippedLine(m.Line, m.Match.TranId,
                        $"Already posted on {live.PaidDate:yyyy-MM-dd} — nothing to do."));
                    continue;
                }

                await conn.ExecuteAsync(
                    """
                    UPDATE dbo.Transactions
                       SET PriPaidAmt = @paid,
                           WOAmt      = @wo
                     WHERE ID = @tranId;
                    """,
                    new { paid = newPaid, wo = newWo, tranId = m.Match.TranId },
                    transaction: tx);

                int bcRows = 0;
                if (live.BilledChargeId is int bcId)
                {
                    bcRows = await conn.ExecuteAsync(
                        """
                        UPDATE dbo.BilledCharges
                           SET PaidDate   = GETDATE(),
                               AppliedAmt = @paid
                         WHERE ID = @bcId
                           AND PaidDate IS NULL;
                        """,
                        new { paid = m.Line.PaidAmount, bcId },
                        transaction: tx);
                }

                applied.Add(new AppliedLine(
                    Line: m.Line,
                    TranId: m.Match.TranId,
                    BilledChargeId: live.BilledChargeId,
                    PriPaidAmtBefore: live.PriPaidAmt,
                    PriPaidAmtAfter: newPaid,
                    WOAmtBefore: live.WOAmt,
                    WOAmtAfter: newWo,
                    BilledChargeStamped: bcRows == 1));
            }

            await tx.CommitAsync(ct);
            _logger.LogInformation(
                "EOB posted: applied={Applied} skipped={Skipped} ambiguous={Ambiguous} unmatched={Unmatched}.",
                applied.Count, skipped.Count, preview.Ambiguous.Count, preview.Unmatched.Count);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return new EobPostingResult(
            TotalLines: preview.TotalLines,
            Applied: applied,
            Skipped: skipped,
            Ambiguous: preview.Ambiguous,
            Unmatched: preview.Unmatched);
    }

    private sealed class LiveTx
    {
        public int TranId { get; set; }
        public decimal TranAmt { get; set; }
        public decimal PriPaidAmt { get; set; }
        public decimal WOAmt { get; set; }
        public int? BilledChargeId { get; set; }
        public DateTime? PaidDate { get; set; }
    }
}

public sealed record AppliedLine(
    EobLine Line,
    int TranId,
    int? BilledChargeId,
    decimal PriPaidAmtBefore,
    decimal PriPaidAmtAfter,
    decimal WOAmtBefore,
    decimal WOAmtAfter,
    bool BilledChargeStamped);

public sealed record SkippedLine(EobLine Line, int TranId, string Reason);

public sealed record EobPostingResult(
    int TotalLines,
    IReadOnlyList<AppliedLine> Applied,
    IReadOnlyList<SkippedLine> Skipped,
    IReadOnlyList<AmbiguousLine> Ambiguous,
    IReadOnlyList<UnmatchedLine> Unmatched);
