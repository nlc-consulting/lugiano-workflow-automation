using Dapper;
using Lugiano.Workflow.SyncService.ChiroTouch;

namespace Lugiano.Workflow.SyncService.Services;

// Marks all unbilled service charges on a visit as "billed" by INSERTing a
// BilledCharges row per Transaction. Mirrors ChiroTouch's paper-claim flow:
//   - One row per service Transaction (TranType='C', TranSubType='SV')
//   - ChargeTranID + InsPolID + BilledDate populated
//   - Other columns (ClaimLineID, PaymentTranID, AppliedAmt, PaidDate) left
//     NULL — matches 84% of unpaid + 99% of paid BilledCharges rows in CT prod
//     (ClaimLines linkage is only for the electronic 837 flow, not paper HCFA).
//
// Used by the post-fax test button. Eventually fires automatically after fax
// delivery confirmation; for now a human clicks it.
public sealed class BillChargesService
{
    private readonly ISourceDbWriteConnectionFactory _writeDb;
    private readonly ILogger<BillChargesService> _logger;

    public BillChargesService(ISourceDbWriteConnectionFactory writeDb, ILogger<BillChargesService> logger)
    {
        _writeDb = writeDb;
        _logger = logger;
    }

    public bool IsConfigured => _writeDb.IsConfigured;

    public async Task<BillResult> BillVisitAsync(
        int patientId, int appointmentId, CancellationToken ct = default)
    {
        await using var conn = _writeDb.Create();
        await conn.OpenAsync(ct);

        // Bill against the primary policy (Seq=1), the same predicate HCFA's
        // renderer uses for Box 11 / Box 1a. Must stay aligned — billing a
        // different InsPolID than the form prints would point the tracer +
        // carrier follow-up at the wrong policy. Termination date intentionally
        // NOT filtered: CT treats Seq=1 as active regardless, HCFA prints it as-is.
        var insPolId = await conn.QuerySingleOrDefaultAsync<int?>(
            """
            SELECT TOP 1 ID
            FROM   dbo.InsPolicies
            WHERE  PatientID = @patientId
              AND  Seq = 1
            ORDER BY ID;
            """,
            new { patientId });
        if (insPolId is null)
            throw new InvalidOperationException(
                $"No primary (Seq=1) insurance policy found for patient {patientId}.");

        // Find every service charge on this visit that isn't already on a
        // BilledCharges row. LEFT JOIN + IS NULL is the unbilled filter CT
        // itself uses in vwChargesToBeBilled.
        var unbilled = (await conn.QueryAsync<int>(
            """
            SELECT t.ID
            FROM   dbo.Transactions t
            LEFT JOIN dbo.BilledCharges bc ON bc.ChargeTranID = t.ID
            WHERE  t.PatID       = @patientId
              AND  t.ApptID      = @appointmentId
              AND  t.TranType    = 'C'
              AND  t.TranSubType = 'SV'
              AND  bc.ID IS NULL
            ORDER BY t.ID;
            """,
            new { patientId, appointmentId })).ToList();
        if (unbilled.Count == 0)
            throw new InvalidOperationException(
                $"No unbilled service charges found on appointment {appointmentId}. Either already billed or no charges on this visit.");

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            int rows = 0;
            foreach (var tranId in unbilled)
            {
                rows += await conn.ExecuteAsync(
                    """
                    INSERT INTO dbo.BilledCharges (ChargeTranID, InsPolID, BilledDate)
                    VALUES (@tranId, @insPolId, GETDATE());
                    """,
                    new { tranId, insPolId = insPolId.Value },
                    transaction: tx);
            }
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Bill charges: patient {PatientId} appt {AppointmentId} insPol {InsPolId} -> {Rows} BilledCharges row(s).",
                patientId, appointmentId, insPolId.Value, rows);

            return new BillResult(
                PatientId: patientId,
                AppointmentId: appointmentId,
                InsPolId: insPolId.Value,
                BilledTranIds: unbilled,
                Count: rows);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}

public sealed record BillResult(
    int PatientId,
    int AppointmentId,
    int InsPolId,
    IReadOnlyList<int> BilledTranIds,
    int Count);
