using Dapper;
using Lugiano.Workflow.SyncService.ChiroTouch;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Lugiano.Workflow.SyncService.Services;

// Insurance Payment Tracer — the AR follow-up letter sent to carriers asking
// about the status of claims that were billed but never paid back. Mirrors
// ChiroTouch's Insurance Payment Tracer (Claim History → Charges Billed But
// Not Paid → Tracer button).
//
// Data model:
//   A "batch" = (PatientID, InsPolID, BilledDate) — all charges that went out
//   to a specific payer on a specific day. CT batches one HCFA per appointment
//   but stamps them with the same date, so a tracer covers everything that day.
//   "Unpaid" = BilledCharges row exists but PaidDate IS NULL.
public sealed class TracerPreviewService
{
    private readonly ISourceDbConnectionFactory _sourceDb;
    private readonly ILogger<TracerPreviewService> _logger;

    public TracerPreviewService(ISourceDbConnectionFactory sourceDb, ILogger<TracerPreviewService> logger)
    {
        _sourceDb = sourceDb;
        _logger = logger;
    }

    // Lists outstanding-AR batches for a patient (one row per bill date+payer),
    // newest first. Drives the portal Tracer page's expandable rows.
    public async Task<IReadOnlyList<TracerBatch>> ListBatchesAsync(int patientId, CancellationToken ct = default)
    {
        await using var conn = _sourceDb.Create();
        var rows = await conn.QueryAsync<TracerBatchRow>(
            """
            SELECT CAST(bc.BilledDate AS date) AS BilledDate,
                   bc.InsPolID                 AS InsPolId,
                   ip.InsCoName                AS PayerName,
                   COUNT(*)                    AS LineCount,
                   SUM(t.TranAmt)              AS TotalAmount
            FROM   dbo.BilledCharges bc
            JOIN   dbo.Transactions  t  ON t.ID  = bc.ChargeTranID
            JOIN   dbo.InsPolicies   ip ON ip.ID = bc.InsPolID
            WHERE  t.PatID       = @patientId
              AND  bc.PaidDate IS NULL
            GROUP BY CAST(bc.BilledDate AS date), bc.InsPolID, ip.InsCoName
            ORDER BY CAST(bc.BilledDate AS date) DESC;
            """,
            new { patientId });

        return rows.Select(r => new TracerBatch(
            r.BilledDate, r.InsPolId, r.PayerName, r.LineCount, r.TotalAmount)).ToList();
    }

    // Pulls the rendering data for one batch (all the charge details for a
    // specific bill date + payer). Patient + payer header come along too.
    public async Task<TracerData?> GetBatchAsync(int patientId, DateTime billedDate, CancellationToken ct = default)
    {
        await using var conn = _sourceDb.Create();

        // Patient + payer header — pull from the InsPolicies row this batch
        // was billed under (one batch is always one payer).
        var header = await conn.QueryFirstOrDefaultAsync<TracerHeader>(
            """
            SELECT TOP 1
              p.ID                    AS PatientId,
              p.AccountNo             AS AccountNo,
              p.FirstName             AS PatientFirst,
              p.LastName              AS PatientLast,
              p.Address               AS PatientAddress,
              p.City                  AS PatientCity,
              p.State                 AS PatientState,
              p.Zip                   AS PatientZip,
              ip.InsCoName            AS PayerName,
              ip.CompanyAddress       AS PayerAddress,
              ip.CompanyCity          AS PayerCity,
              ip.CompanyState         AS PayerState,
              ip.CompanyZip           AS PayerZip,
              ip.InsuredIDNo          AS InsuredIdNo
            FROM   dbo.BilledCharges bc
            JOIN   dbo.Transactions t  ON t.ID  = bc.ChargeTranID
            JOIN   dbo.InsPolicies  ip ON ip.ID = bc.InsPolID
            JOIN   dbo.Patients     p  ON p.ID  = t.PatID
            WHERE  t.PatID       = @patientId
              AND  CAST(bc.BilledDate AS date) = @billedDate
              AND  bc.PaidDate IS NULL;
            """,
            new { patientId, billedDate = billedDate.Date });
        if (header is null) return null;

        // Diagnoses scoped to THIS batch's appointments only, matching CT's tracer
        // (only DXs on the traced visits, not lifetime DX history). Inner subquery
        // mirrors GetAppointmentIdsForBatchAsync so the two stay in lockstep.
        var diagnoses = (await conn.QueryAsync<TracerDx>(
            """
            SELECT DISTINCT d.Code, d.Description
            FROM   dbo.Diagnoses d
            WHERE  d.AppointmentID IN (
                SELECT DISTINCT t.ApptID
                FROM   dbo.BilledCharges bc
                JOIN   dbo.Transactions  t ON t.ID = bc.ChargeTranID
                WHERE  t.PatID       = @patientId
                  AND  CAST(bc.BilledDate AS date) = @billedDate
                  AND  bc.PaidDate IS NULL
                  AND  t.ApptID IS NOT NULL
            )
              AND  d.Code IS NOT NULL AND d.Code <> ''
            ORDER BY d.Code;
            """,
            new { patientId, billedDate = billedDate.Date })).ToList();

        // Charge lines. Injury date is Patients.CurInjuryDate (one per patient,
        // the active case).
        var lines = (await conn.QueryAsync<TracerLine>(
            """
            SELECT t.TranDate    AS DateOfService,
                   p.CurInjuryDate AS InjuryDate,
                   t.Code        AS Code,
                   t.Description AS Description,
                   t.TranAmt     AS Charge
            FROM   dbo.BilledCharges bc
            JOIN   dbo.Transactions t ON t.ID = bc.ChargeTranID
            JOIN   dbo.Patients p ON p.ID = t.PatID
            WHERE  t.PatID       = @patientId
              AND  CAST(bc.BilledDate AS date) = @billedDate
              AND  bc.PaidDate IS NULL
            ORDER BY t.TranDate, t.ID;
            """,
            new { patientId, billedDate = billedDate.Date })).ToList();

        var total = lines.Sum(l => l.Charge);
        return new TracerData(header, diagnoses, lines, billedDate.Date, total);
    }

    // Distinct PSChiro Appointment IDs that contributed billed-but-unpaid charges
    // to one batch. Used when the "Include HCFA" checkbox is on, to render one
    // HCFA per appointment after the batch's tracer page.
    public async Task<IReadOnlyList<int>> GetAppointmentIdsForBatchAsync(
        int patientId, DateTime billedDate, CancellationToken ct = default)
    {
        await using var conn = _sourceDb.Create();
        var ids = await conn.QueryAsync<int>(
            """
            SELECT DISTINCT t.ApptID
            FROM   dbo.BilledCharges bc
            JOIN   dbo.Transactions  t ON t.ID = bc.ChargeTranID
            WHERE  t.PatID       = @patientId
              AND  CAST(bc.BilledDate AS date) = @billedDate
              AND  bc.PaidDate IS NULL
              AND  t.ApptID IS NOT NULL
            ORDER BY t.ApptID;
            """,
            new { patientId, billedDate = billedDate.Date });
        return ids.ToList();
    }

    public byte[] RenderPdf(IReadOnlyList<TracerData> batches)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(doc =>
        {
            foreach (var batch in batches)
                AddBatchPageToDocument(doc, batch);
        }).GeneratePdf();
    }

    // Adds a single tracer batch page to an existing document. Caller owns
    // Document.Create + License setup. Used when interleaving tracer pages with
    // bundled HCFA forms.
    public void AddBatchPageToDocument(IDocumentContainer doc, TracerData batch)
    {
        doc.Page(page =>
                {
                    page.Size(PageSizes.Letter);
                    page.Margin(0.5f, Unit.Inch);
                    page.DefaultTextStyle(t => t.FontSize(10).FontFamily(Fonts.Calibri));

                    page.Header().Column(c =>
                    {
                        c.Item().AlignCenter().Text("Insurance Payment Tracer").FontSize(14).Bold();
                        c.Item().PaddingTop(6).Row(row =>
                        {
                            row.RelativeItem().Text(t =>
                            {
                                t.Span("Patient: ").Bold();
                                t.Span($"{batch.Header.PatientLast}, {batch.Header.PatientFirst}");
                            });
                            row.RelativeItem().AlignRight().Column(p =>
                            {
                                p.Item().Text(t =>
                                {
                                    t.Span("Claim Date: ").Bold();
                                    t.Span(batch.BilledDate.ToString("MM/dd/yyyy"));
                                });
                                p.Item().Text(t =>
                                {
                                    t.Span("Claim Amount: ").Bold();
                                    t.Span(batch.TotalAmount.ToString("C2"));
                                });
                            });
                        });
                    });

                    page.Content().Column(c =>
                    {
                        c.Spacing(10);

                        // Payor + Insured two-column block
                        c.Item().PaddingTop(8).Row(row =>
                        {
                            row.RelativeItem().Border(1).Padding(6).Column(p =>
                            {
                                p.Item().Text("Payor:").Bold();
                                p.Item().PaddingTop(2).Text(batch.Header.PayerName ?? "");
                                p.Item().Text(batch.Header.PayerAddress ?? "");
                                p.Item().Text(JoinNonEmpty(", ",
                                    batch.Header.PayerCity,
                                    JoinNonEmpty(" ", batch.Header.PayerState, batch.Header.PayerZip)));
                            });
                            row.ConstantItem(10);
                            row.RelativeItem().Border(1).Padding(6).Column(p =>
                            {
                                p.Item().Text("Insured:").Bold();
                                p.Item().PaddingTop(2).Text($"{batch.Header.PatientLast}, {batch.Header.PatientFirst}");
                                p.Item().Text(batch.Header.PatientAddress ?? "");
                                p.Item().Text(JoinNonEmpty(", ",
                                    batch.Header.PatientCity,
                                    JoinNonEmpty(" ", batch.Header.PatientState, batch.Header.PatientZip)));
                            });
                        });

                        // Diagnoses
                        if (batch.Diagnoses.Count > 0)
                        {
                            c.Item().PaddingTop(4).Column(p =>
                            {
                                p.Item().Row(row =>
                                {
                                    row.RelativeItem().Text("Diagnoses:").Bold();
                                    row.ConstantItem(180).AlignRight().Text(t =>
                                    {
                                        t.Span("ID #:  ").Bold();
                                        t.Span(batch.Header.InsuredIdNo ?? "");
                                    });
                                });
                                p.Item().PaddingTop(2).Column(dx =>
                                {
                                    foreach (var d in batch.Diagnoses)
                                        dx.Item().Text($"{d.Code} : {d.Description}");
                                });
                            });
                        }

                        // Charges table
                        c.Item().PaddingTop(8).Column(p =>
                        {
                            p.Item().Text("Charges:").Bold();
                            p.Item().PaddingTop(4).Table(tbl =>
                            {
                                tbl.ColumnsDefinition(cd =>
                                {
                                    cd.ConstantColumn(80);   // DOS
                                    cd.ConstantColumn(85);   // Injury Date
                                    cd.ConstantColumn(60);   // Code
                                    cd.RelativeColumn();     // Description
                                    cd.ConstantColumn(70);   // Charge
                                });
                                tbl.Header(h =>
                                {
                                    static void H(IContainer c, string txt, bool right = false)
                                    {
                                        var col = c.BorderBottom(1).PaddingBottom(3).PaddingTop(2);
                                        (right ? col.AlignRight() : col).Text(txt).FontSize(9).Bold();
                                    }
                                    H(h.Cell(), "Date");
                                    H(h.Cell(), "Injury Date:");
                                    H(h.Cell(), "Code");
                                    H(h.Cell(), "Description");
                                    H(h.Cell(), "Charge", right: true);
                                });
                                foreach (var ln in batch.Lines)
                                {
                                    tbl.Cell().Text(ln.DateOfService?.ToString("MM/dd/yyyy") ?? "");
                                    tbl.Cell().Text(ln.InjuryDate?.ToString("MM/dd/yyyy") ?? "");
                                    tbl.Cell().Text(ln.Code ?? "");
                                    tbl.Cell().Text(ln.Description ?? "");
                                    tbl.Cell().AlignRight().Text(ln.Charge.ToString("C2"));
                                }
                            });
                        });
                    });

                    page.Footer().Row(row =>
                    {
                        row.RelativeItem().Text(t =>
                        {
                            t.Span("Printed: ").FontSize(8).Bold().FontColor(Colors.Grey.Darken2);
                            t.Span(DateTime.Now.ToString("dddd, MMMM d, yyyy h:mm:ss tt"))
                                .FontSize(8).FontColor(Colors.Grey.Darken2);
                        });
                        row.ConstantItem(120).AlignRight().Text(t =>
                        {
                            t.Span("Page ").FontSize(8).FontColor(Colors.Grey.Darken2);
                            t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Darken2);
                            t.Span(" Of ").FontSize(8).FontColor(Colors.Grey.Darken2);
                            t.TotalPages().FontSize(8).FontColor(Colors.Grey.Darken2);
                        });
                    });
                });
    }

    private static string JoinNonEmpty(string sep, params string?[] parts) =>
        string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}

// Public records (used by controller serialization)
public sealed record TracerBatch(
    DateTime BilledDate,
    int InsPolId,
    string? PayerName,
    int LineCount,
    decimal TotalAmount);

public sealed record TracerData(
    TracerHeader Header,
    IReadOnlyList<TracerDx> Diagnoses,
    IReadOnlyList<TracerLine> Lines,
    DateTime BilledDate,
    decimal TotalAmount);

// Internal row shapes for Dapper.
public sealed class TracerBatchRow
{
    public DateTime BilledDate { get; set; }
    public int InsPolId { get; set; }
    public string? PayerName { get; set; }
    public int LineCount { get; set; }
    public decimal TotalAmount { get; set; }
}

public sealed class TracerHeader
{
    public int PatientId { get; set; }
    public int? AccountNo { get; set; }
    public string? PatientFirst { get; set; }
    public string? PatientLast { get; set; }
    public string? PatientAddress { get; set; }
    public string? PatientCity { get; set; }
    public string? PatientState { get; set; }
    public string? PatientZip { get; set; }
    public string? PayerName { get; set; }
    public string? PayerAddress { get; set; }
    public string? PayerCity { get; set; }
    public string? PayerState { get; set; }
    public string? PayerZip { get; set; }
    public string? InsuredIdNo { get; set; }
}

public sealed class TracerDx
{
    public string? Code { get; set; }
    public string? Description { get; set; }
}

public sealed class TracerLine
{
    public DateTime? DateOfService { get; set; }
    public DateTime? InjuryDate { get; set; }
    public string? Code { get; set; }
    public string? Description { get; set; }
    public decimal Charge { get; set; }
}
