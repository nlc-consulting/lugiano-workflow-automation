using Dapper;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.Services.Pdf;
using Lugiano.Workflow.SyncService.Util;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Lugiano.Workflow.SyncService.Services;

// Multi-page patient chart-notes PDF for sharing with attorneys, IME reviewers,
// or carriers. Cover sheet + one page per note. Reuses the read-only patient
// detail queries (no new SQL paths), so data is consistent with what shows
// in the portal case detail.
public sealed class NotesPreviewService
{
    private readonly IPatientDetailQueries _detail;
    private readonly IChartNoteReadQueries _noteReads;
    private readonly ISourceDbConnectionFactory _sourceDb;
    private readonly ILogger<NotesPreviewService> _logger;

    public NotesPreviewService(
        IPatientDetailQueries detail,
        IChartNoteReadQueries noteReads,
        ISourceDbConnectionFactory sourceDb,
        ILogger<NotesPreviewService> logger)
    {
        _detail = detail;
        _noteReads = noteReads;
        _sourceDb = sourceDb;
        _logger = logger;
    }

    public async Task<NotesPdfData?> GetDataAsync(int patientId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        if (!_detail.IsConfigured) return null;

        var demo = await _detail.GetDemographicsAsync(patientId);
        if (demo is null) return null;

        var policies = await _detail.GetPoliciesAsync(patientId);
        var noteRows = await _detail.GetRecentNotesAsync(patientId, 100);

        // Apply optional date filter.
        var filtered = noteRows
            .Where(n => n.NoteDate.HasValue)
            .Where(n => from is null || n.NoteDate >= from)
            .Where(n => to   is null || n.NoteDate <= to)
            .OrderByDescending(n => n.NoteDate)
            .ToList();

        // Pull plain-text bodies for each note. Worker caches PlainText on
        // DoctorNote during sync, but the GetRecentNotesAsync ChartNoteRow
        // doesn't include text — we need a separate trip. For unsynced or
        // pre-cache rows, fall back to a live RTF read.
        var phoneNumber = await GetPrimaryPhoneAsync(patientId);
        var visitIds = filtered.Where(n => n.VisitId.HasValue).Select(n => n.VisitId!.Value).Distinct().ToList();
        var diagnosesByVisit = visitIds.Count == 0
            ? new Dictionary<int, List<string>>()
            : (await _detail.GetDiagnosesForVisitsAsync(visitIds))
                .GroupBy(d => d.AppointmentId)
                .ToDictionary(g => g.Key, g => g.Select(d => $"{d.Code} {d.Description}").ToList());

        // Signature image + signed timestamp per note, so the standalone notes
        // PDF carries the same "Electronically Signed" block as the fax/HCFA flow.
        var sigByNote = await GetSignaturesAsync(filtered.Select(n => n.Id).ToArray());

        var notes = new List<NotePageData>();
        foreach (var n in filtered)
        {
            string? text = null;
            IReadOnlyList<IReadOnlyList<RtfRun>>? rich = null;
            if (n.SoapPtr is int ptr and not 0)
            {
                try
                {
                    var rtf = await _noteReads.GetNoteRtfAsync(ptr);
                    text = RtfConverter.ToPlainText(rtf);
                    rich = RtfRichConverter.ToRuns(rtf);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Notes PDF: failed to read RTF for ptr {Ptr} (note {NoteId})", ptr, n.Id);
                }
            }
            var dxList = n.VisitId is int v && diagnosesByVisit.TryGetValue(v, out var list)
                ? list
                : new List<string>();
            sigByNote.TryGetValue(n.Id, out var sig);
            notes.Add(new NotePageData(n, text, dxList, rich, sig?.Image, sig?.SignedAt));
        }

        return new NotesPdfData(demo, policies, phoneNumber, notes);
    }

    private async Task<Dictionary<int, SigRow>> GetSignaturesAsync(int[] noteIds)
    {
        if (noteIds.Length == 0) return new Dictionary<int, SigRow>();
        await using var conn = _sourceDb.Create();
        return (await conn.QueryAsync<SigRow>(
            """
            SELECT SigTypeID AS NoteId, ImageBase64 AS Image, SigTimestamp AS SignedAt
            FROM   dbo.Signatures
            WHERE  SigType = 'CN'
              AND  SigTypeID IN @noteIds
              AND  ImageBase64 IS NOT NULL;
            """,
            new { noteIds })).ToDictionary(r => r.NoteId);
    }

    private async Task<string?> GetPrimaryPhoneAsync(int patientId)
    {
        await using var conn = _sourceDb.Create();
        return await conn.QuerySingleOrDefaultAsync<string?>(
            """
            SELECT TOP 1 Number FROM dbo.ContactInfos
            WHERE PatientID = @patientId ORDER BY ID;
            """,
            new { patientId });
    }

    public byte[] RenderPdf(NotesPdfData data)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var demo = data.Demographics;
        var primaryPolicy = data.Policies.FirstOrDefault();
        var oldest = data.Notes.Count > 0 ? data.Notes[^1].Row.NoteDate : null;
        var newest = data.Notes.Count > 0 ? data.Notes[0].Row.NoteDate : null;

        return Document.Create(doc =>
        {
            // -------- COVER PAGE --------
            doc.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(0.75f, Unit.Inch);
                page.DefaultTextStyle(t => t.FontSize(10).FontFamily(Fonts.Calibri));

                page.Header().Column(c =>
                {
                    c.Item().Text("PATIENT CHART NOTES").FontSize(18).Bold();
                    c.Item().PaddingBottom(4).LineHorizontal(1).LineColor(Colors.Black);
                });

                page.Content().PaddingTop(12).Column(c =>
                {
                    c.Spacing(14);

                    c.Item().Text($"{demo.LastName}, {demo.FirstName}").FontSize(20).Bold();

                    // Patient demographics block
                    c.Item().Row(row =>
                    {
                        row.RelativeItem().Element(Block("Patient", new[]
                        {
                            ("Account #", $"{demo.PatientId}"),
                            ("DOB", "—"),
                            ("Sex", demo.Sex ?? "—"),
                            ("Phone", data.PrimaryPhone ?? "—"),
                        }));
                        row.ConstantItem(20);
                        row.RelativeItem().Element(Block("Address", new[]
                        {
                            ("Street", demo.Address ?? "—"),
                            ("City", demo.City ?? "—"),
                            ("State", demo.State ?? "—"),
                            ("ZIP", demo.Zip ?? "—"),
                        }));
                    });

                    // Case + provider
                    c.Item().Row(row =>
                    {
                        row.RelativeItem().Element(Block("Case", new[]
                        {
                            ("Case type", demo.CaseType ?? "—"),
                            ("Injury date", demo.CurInjuryDate?.ToString("MM/dd/yyyy") ?? "—"),
                            ("Primary doctor", demo.PrimaryDoctor ?? "—"),
                        }));
                        row.ConstantItem(20);
                        row.RelativeItem().Element(Block("Insurance", new[]
                        {
                            ("Carrier", primaryPolicy?.Insurer ?? "—"),
                            ("Coverage", primaryPolicy?.CoverageType ?? "—"),
                            ("Effective", primaryPolicy?.EffectiveDate?.ToString("MM/dd/yyyy") ?? "—"),
                            ("Terminates", primaryPolicy?.TerminationDate?.ToString("MM/dd/yyyy") ?? "—"),
                        }));
                    });

                    // Notes summary
                    c.Item().PaddingTop(8).Border(1).Padding(10).Column(p =>
                    {
                        p.Item().Text("CHART NOTES").FontSize(11).Bold();
                        p.Item().PaddingTop(4).Text(t =>
                        {
                            t.Span($"{data.Notes.Count}").Bold();
                            t.Span(" notes included");
                            if (oldest.HasValue && newest.HasValue)
                            {
                                t.Span("  ·  ");
                                t.Span($"{oldest.Value:MM/dd/yyyy}").Bold();
                                t.Span(" through ");
                                t.Span($"{newest.Value:MM/dd/yyyy}").Bold();
                            }
                        });
                    });
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Generated ").FontSize(8).FontColor(Colors.Grey.Darken1);
                    t.Span(DateTime.Now.ToString("MM/dd/yyyy h:mm tt")).FontSize(8).FontColor(Colors.Grey.Darken1).Bold();
                });
            });

            // -------- ONE PAGE PER NOTE (shared ChiroTouch-matching renderer) --------
            var ctx = new ChartNoteHeaderCtx(
                PatientDisplayName: $"{demo.LastName}, {demo.FirstName}",
                AccountNo: demo.PatientId.ToString(),
                PatientBirthDate: null,
                InsCoName: primaryPolicy?.Insurer,
                PolicyNo: null,
                InsuredId: null,
                FacilityBlock: null);

            ChartNotesRenderer.AddNotePages(doc, ctx, data.Notes.Select(n => new RenderableNote(
                NoteDate: n.Row.NoteDate,
                ProviderName: n.Row.Doctor,
                RichBody: n.RichBody,
                PlainTextFallback: n.PlainText,
                SignatureImageBase64: n.SignatureImageBase64,
                SignedProviderName: n.Row.Doctor,
                SignedAt: n.SignedAt)));
        }).GeneratePdf();
    }

    // Small two-column "label : value" stack used on the cover page.
    private static Action<IContainer> Block(string title, IEnumerable<(string Label, string Value)> rows) =>
        container => container.Border(1).Padding(8).Column(c =>
        {
            c.Item().PaddingBottom(4).Text(title).FontSize(9).Bold().FontColor(Colors.Grey.Darken2);
            foreach (var (label, value) in rows)
            {
                c.Item().Row(row =>
                {
                    row.ConstantItem(95).Text(label).FontSize(9).FontColor(Colors.Grey.Darken1);
                    row.RelativeItem().Text(value).FontSize(10);
                });
            }
        });
}

public sealed record NotePageData(
    ChartNoteRow Row, string? PlainText, List<string> Diagnoses,
    IReadOnlyList<IReadOnlyList<RtfRun>>? RichBody = null,
    string? SignatureImageBase64 = null,
    DateTime? SignedAt = null);

public sealed record NotesPdfData(
    PatientDemographics Demographics,
    IReadOnlyList<InsurancePolicyRow> Policies,
    string? PrimaryPhone,
    IReadOnlyList<NotePageData> Notes);
