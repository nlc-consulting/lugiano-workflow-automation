using Dapper;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.Services.Pdf;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Lugiano.Workflow.SyncService.Services.Fax;

// Orchestrates the "send by fax" flow: resolve carrier fax from InsPolicies
// (HCFA scoped to appointment payer; tracer scoped to each batch payer, one
// send per distinct destination), render the same PDF as the preview
// endpoints, hand to DocumoFaxClient. Mirrors preview field-for-field so a
// send is indistinguishable from what the operator saw on screen.
public sealed class FaxService
{
    private readonly ISourceDbConnectionFactory _sourceDb;
    private readonly HcfaPreviewService _hcfa;
    private readonly TracerPreviewService _tracer;
    private readonly DocumoFaxClient _documo;
    private readonly FaxCoverOptions _cover;
    private readonly ILogger<FaxService> _logger;

    public FaxService(
        ISourceDbConnectionFactory sourceDb,
        HcfaPreviewService hcfa,
        TracerPreviewService tracer,
        DocumoFaxClient documo,
        IOptions<FaxCoverOptions> coverOptions,
        ILogger<FaxService> logger)
    {
        _sourceDb = sourceDb;
        _hcfa = hcfa;
        _tracer = tracer;
        _documo = documo;
        _cover = coverOptions.Value;
        _logger = logger;
    }

    // Composed PDF plus the carrier fax destination. Preview only uses Pdf;
    // SendHcfaAsync validates ToFax before calling Documo — null means the
    // cover rendered with a placeholder but there's no real destination.
    public sealed record HcfaFaxBuildResult(byte[] Pdf, string? ToFax);

    // Carrier mailing block (company name + address) for the cover TO block.
    // Empty strings when unavailable — cover still renders with just the fax.
    private sealed record CarrierAddressRow(
        string? CompanyName,
        string? CompanyAddress,
        string? CompanyCity,
        string? CompanyState,
        string? CompanyZip);

    private async Task<CarrierAddressRow?> GetCarrierAddressForAppointmentAsync(
        int patientId, int appointmentId, CancellationToken ct)
    {
        await using var conn = _sourceDb.Create();
        return await conn.QueryFirstOrDefaultAsync<CarrierAddressRow>(
            """
            SELECT TOP 1
                   ip.InsCoName       AS CompanyName,
                   ip.CompanyAddress  AS CompanyAddress,
                   ip.CompanyCity     AS CompanyCity,
                   ip.CompanyState    AS CompanyState,
                   ip.CompanyZip      AS CompanyZip
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

    // Patient identity for the cover RE: block — name + AccountNo so the
    // recipient can file to the right patient without opening the HCFA.
    private async Task<string> BuildPatientLineAsync(int patientId, DateTime? dos, CancellationToken ct)
    {
        await using var conn = _sourceDb.Create();
        var row = await conn.QueryFirstOrDefaultAsync<(string? FirstName, string? LastName, int? AccountNo)>(
            "SELECT FirstName, LastName, AccountNo FROM dbo.Patients WHERE ID = @patientId",
            new { patientId });
        var name = $"{row.FirstName ?? "?"} {row.LastName ?? "?"}".Trim();
        var acct = row.AccountNo.HasValue ? $" (Acct {row.AccountNo.Value})" : "";
        var dosStr = dos.HasValue ? $", DOS {dos.Value:MM/dd/yyyy}" : "";
        return $"{name}{acct}{dosStr}";
    }

    private FaxCoverSheetData BuildCoverData(
        CarrierAddressRow? carrier, string toFax, string subject, string patientLine, string pagesText)
    {
        var addressLine = carrier?.CompanyAddress;
        var cityStateZip = string.Join(", ",
            new[] { carrier?.CompanyCity, carrier?.CompanyState }
                .Where(s => !string.IsNullOrWhiteSpace(s)))
            + (string.IsNullOrWhiteSpace(carrier?.CompanyZip) ? "" : $"  {carrier!.CompanyZip}");
        return new FaxCoverSheetData(
            ToCompany: carrier?.CompanyName ?? "Insurance Carrier",
            ToFax: toFax,
            ToAttention: null,
            ToAddressLine1: string.IsNullOrWhiteSpace(addressLine) ? null : addressLine,
            ToCityStateZip: string.IsNullOrWhiteSpace(cityStateZip) ? null : cityStateZip,
            FromPractice: _cover.PracticeName,
            FromAddressLine1: _cover.AddressLine1,
            FromCityStateZip: _cover.CityStateZip,
            FromPhone: _cover.Phone,
            FromFax: _cover.Fax,
            SentDate: DateTime.Now,
            Subject: subject,
            PatientLine: patientLine,
            PagesText: pagesText);
    }

    public bool IsConfigured => _documo.IsConfigured;

    // The EXACT bytes faxed for a single HCFA (cover page 1 + HCFA form +
    // chart notes). The ONE compose path shared by SendHcfaAsync and the
    // Preview endpoint, so on-screen preview is byte-for-byte what gets faxed.
    // Cover ALWAYS renders even with no carrier fax on file, so the biller can
    // preview the letterhead/confidentiality block; missing fax shows
    // "[NO FAX ON FILE]" and SendHcfaAsync guards on it before calling Documo.
    public async Task<HcfaFaxBuildResult> BuildHcfaFaxPdfAsync(
        int patientId, int appointmentId,
        bool calibrate = false, float dx = 0, float dy = 0,
        CancellationToken ct = default)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var data = await _hcfa.GetDataAsync(patientId, appointmentId, ct);
        if (data is null)
            throw new InvalidOperationException(
                $"No HCFA data for patient {patientId} / appointment {appointmentId}.");

        var toFax = await GetCarrierFaxForAppointmentAsync(patientId, appointmentId, ct);
        var carrier = await GetCarrierAddressForAppointmentAsync(patientId, appointmentId, ct);
        var patientLine = await BuildPatientLineAsync(patientId, null, ct);
        var faxForCover = string.IsNullOrWhiteSpace(toFax) ? "[NO FAX ON FILE]" : toFax!;
        var cover = BuildCoverData(
            carrier, faxForCover,
            subject: "HCFA-1500 Claim Submission",
            patientLine: patientLine,
            pagesText: "See attached");

        var pdf = Document.Create(doc =>
        {
            FaxCoverSheetRenderer.AddCoverPageToDocument(doc, cover);
            _hcfa.AddPagesToDocument(doc, data, calibrate, dx, dy, fax: true);
        }).GeneratePdf();

        return new HcfaFaxBuildResult(pdf, toFax);  // ToFax may be null — caller decides
    }

    // Fax a single HCFA (form + chart notes). Always fax-mode rendering
    // (overlay on) since the recipient is a carrier fax inbox. Prepends the
    // HIPAA cover sheet so every outbound fax opens with the recipient block
    // + confidentiality notice before any PHI. Composition delegated to
    // BuildHcfaFaxPdfAsync so Preview sees the exact same bytes.
    public async Task<DocumoSendResult> SendHcfaAsync(
        int patientId, int appointmentId, CancellationToken ct = default)
    {
        var built = await BuildHcfaFaxPdfAsync(patientId, appointmentId, ct: ct);
        if (string.IsNullOrWhiteSpace(built.ToFax))
            throw new InvalidOperationException(
                $"No carrier fax number on the active policy for patient {patientId}.");
        return await _documo.SendAsync(
            built.ToFax!, built.Pdf, $"hcfa-{patientId}-{appointmentId}.pdf", ct);
    }

    // Fax one or more tracer batches. One Documo send per distinct payer
    // fax number — batches sharing a fax go in the same PDF, separate
    // payers get separate sends. includeHcfa mirrors the preview endpoint.
    public async Task<IReadOnlyList<DocumoSendResult>> SendTracerAsync(
        int patientId, IEnumerable<DateTime> billDates, bool includeHcfa,
        CancellationToken ct = default)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        // Bundle each bill date with its batch, optional HCFAs, and carrier
        // fax. Group by destination so one fax covers all batches sharing a payer.
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

        var patientLine = await BuildPatientLineAsync(patientId, null, ct);

        var results = new List<DocumoSendResult>();
        foreach (var byFax in bundles.GroupBy(b => b.Fax))
        {
            // Carrier address for the cover TO field. All batches in this group
            // share the fax (hence the carrier), so look up the first payer name.
            CarrierAddressRow? carrier = null;
            var firstBatchPayer = byFax.First().Batch.Header.PayerName;
            if (!string.IsNullOrWhiteSpace(firstBatchPayer))
            {
                await using var conn = _sourceDb.Create();
                carrier = await conn.QueryFirstOrDefaultAsync<CarrierAddressRow>(
                    """
                    SELECT TOP 1 InsCoName AS CompanyName, CompanyAddress AS CompanyAddress,
                           CompanyCity AS CompanyCity, CompanyState AS CompanyState, CompanyZip AS CompanyZip
                    FROM   dbo.InsPolicies
                    WHERE  PatientID = @patientId AND InsCoName = @payerName
                    ORDER BY ID;
                    """,
                    new { patientId, payerName = firstBatchPayer });
            }

            var cover = BuildCoverData(
                carrier, byFax.Key,
                subject: "AR Follow-up (Tracer)",
                patientLine: patientLine,
                pagesText: "See attached");

            var pdf = Document.Create(doc =>
            {
                FaxCoverSheetRenderer.AddCoverPageToDocument(doc, cover);
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

    // Carrier fax for a specific (patient, appointment). Joins appointment to
    // the patient's primary InsPolicy (first active row), mirroring how the
    // HCFA renderer resolves the payer block. Column InsPolicies.CompanyFax
    // confirmed against the same row set — adjust if your CT version differs.
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
