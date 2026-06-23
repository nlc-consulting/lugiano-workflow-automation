using Dapper;
using Lugiano.Workflow.SyncService.ChiroTouch;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Lugiano.Workflow.SyncService.Services.Fax;

// Orchestrates the "send by fax" flow:
//   1. Resolve the carrier fax number from InsPolicies for the patient
//      (HCFA: scoped to the appointment's payer; tracer: scoped to each
//      batch's payer — one fax send per distinct destination).
//   2. Render the same PDF the preview endpoints already produce.
//   3. Hand it to DocumoFaxClient.
//
// Mirrors today's preview endpoints field-for-field so a fax send is
// indistinguishable from what the operator saw on screen.
public sealed class FaxService
{
    private readonly ISourceDbConnectionFactory _sourceDb;
    private readonly HcfaPreviewService _hcfa;
    private readonly TracerPreviewService _tracer;
    private readonly DocumoFaxClient _documo;
    private readonly ILogger<FaxService> _logger;

    public FaxService(
        ISourceDbConnectionFactory sourceDb,
        HcfaPreviewService hcfa,
        TracerPreviewService tracer,
        DocumoFaxClient documo,
        ILogger<FaxService> logger)
    {
        _sourceDb = sourceDb;
        _hcfa = hcfa;
        _tracer = tracer;
        _documo = documo;
        _logger = logger;
    }

    public bool IsConfigured => _documo.IsConfigured;

    // Fax a single HCFA (form + chart notes). Always uses fax-mode rendering
    // (overlay on) since the recipient is a carrier fax inbox.
    public async Task<DocumoSendResult> SendHcfaAsync(
        int patientId, int appointmentId, CancellationToken ct = default)
    {
        var data = await _hcfa.GetDataAsync(patientId, appointmentId, ct);
        if (data is null)
            throw new InvalidOperationException(
                $"No HCFA data for patient {patientId} / appointment {appointmentId}.");

        var toFax = await GetCarrierFaxForAppointmentAsync(patientId, appointmentId, ct);
        if (string.IsNullOrWhiteSpace(toFax))
            throw new InvalidOperationException(
                $"No carrier fax number on the active policy for patient {patientId}.");

        var pdf = _hcfa.RenderPdf(data, fax: true);
        return await _documo.SendAsync(
            toFax, pdf, $"hcfa-{patientId}-{appointmentId}.pdf", ct);
    }

    // Fax one or more tracer batches. One Documo send per distinct payer
    // fax number — batches sharing a fax go in the same PDF, separate
    // payers get separate sends. includeHcfa mirrors the preview endpoint.
    public async Task<IReadOnlyList<DocumoSendResult>> SendTracerAsync(
        int patientId, IEnumerable<DateTime> billDates, bool includeHcfa,
        CancellationToken ct = default)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        // Bundle each bill date with its batch data + (optional) HCFAs +
        // its carrier fax destination. Group by destination so one fax
        // covers all batches that share a payer.
        var bundles = new List<(TracerData Batch, List<HcfaData> Hcfas, string Fax)>();
        foreach (var d in billDates)
        {
            var batch = await _tracer.GetBatchAsync(patientId, d, ct);
            if (batch is null) continue;

            var fax = await GetCarrierFaxForInsPolAsync(batch.Header, ct);
            if (string.IsNullOrWhiteSpace(fax))
                throw new InvalidOperationException(
                    $"No carrier fax number for batch {d:yyyy-MM-dd} (payer: {batch.Header.PayerName ?? "?"}).");

            var hcfas = new List<HcfaData>();
            if (includeHcfa)
            {
                var apptIds = await _tracer.GetAppointmentIdsForBatchAsync(patientId, d, ct);
                foreach (var apptId in apptIds)
                {
                    var hd = await _hcfa.GetDataAsync(patientId, apptId, ct);
                    if (hd is not null) hcfas.Add(hd);
                }
            }
            bundles.Add((batch, hcfas, fax));
        }
        if (bundles.Count == 0)
            throw new InvalidOperationException("No billable batches found for the given dates.");

        var results = new List<DocumoSendResult>();
        foreach (var byFax in bundles.GroupBy(b => b.Fax))
        {
            var pdf = Document.Create(doc =>
            {
                foreach (var (batch, hcfas, _) in byFax)
                {
                    _tracer.AddBatchPageToDocument(doc, batch);
                    foreach (var hd in hcfas)
                        _hcfa.AddPagesToDocument(doc, hd, fax: true);
                }
            }).GeneratePdf();

            var name = $"tracer-{patientId}-{DateTime.Today:yyyyMMdd}.pdf";
            results.Add(await _documo.SendAsync(byFax.Key, pdf, name, ct));
        }
        return results;
    }

    // Carrier fax for a specific (patient, appointment). Joins from the
    // appointment to the patient's primary InsPolicy (first active row),
    // mirroring how the HCFA renderer resolves the payer block.
    // Column name: InsPolicies.CompanyFax — mirrors CompanyAddress /
    // CompanyCity / CompanyZip naming. Confirmed against the same row set
    // we already pull from. Adjust here if your CT version uses a different
    // column.
    private async Task<string?> GetCarrierFaxForAppointmentAsync(
        int patientId, int appointmentId, CancellationToken ct)
    {
        await using var conn = _sourceDb.Create();
        return await conn.QueryFirstOrDefaultAsync<string?>(
            """
            SELECT TOP 1 ip.CompanyFax
            FROM   dbo.Appointments a
            JOIN   dbo.InsPolicies  ip ON ip.PatientID = a.PatientID
            WHERE  a.ID = @appointmentId
              AND  a.PatientID = @patientId
              AND  ip.CompanyFax IS NOT NULL
              AND  LEN(ip.CompanyFax) >= 10
            ORDER BY ip.ID;
            """,
            new { patientId, appointmentId });
    }

    // Carrier fax for a tracer batch. Tracer batches are keyed by InsPolID,
    // but the TracerHeader doesn't carry it — re-pull from PSChiro using
    // the payer name + patient (the unique pair on an active policy row).
    private async Task<string?> GetCarrierFaxForInsPolAsync(
        TracerHeader header, CancellationToken ct)
    {
        await using var conn = _sourceDb.Create();
        return await conn.QueryFirstOrDefaultAsync<string?>(
            """
            SELECT TOP 1 ip.CompanyFax
            FROM   dbo.InsPolicies ip
            WHERE  ip.PatientID  = @patientId
              AND  ip.InsCoName  = @payerName
              AND  ip.CompanyFax IS NOT NULL
              AND  LEN(ip.CompanyFax) >= 10
            ORDER BY ip.ID;
            """,
            new { patientId = header.PatientId, payerName = header.PayerName });
    }
}
