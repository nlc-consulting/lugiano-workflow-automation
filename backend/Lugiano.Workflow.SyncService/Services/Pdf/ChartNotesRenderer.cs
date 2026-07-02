using Lugiano.Workflow.SyncService.Util;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Lugiano.Workflow.SyncService.Services.Pdf;

// Patient/practice context shared by every page of a chart-note run. Mirrors
// the strip ChiroTouch prints atop each chart-note page.
public sealed record ChartNoteHeaderCtx(
    string PatientDisplayName,
    string AccountNo,
    DateTime? PatientBirthDate,
    string? InsCoName,
    string? PolicyNo,
    string? InsuredId,
    // Multi-line practice block (name / street / city / phone / fax), already
    // newline-separated. Rendered top-right.
    string? FacilityBlock);

// One renderable note: its date, provider, body (colored runs preferred, plain
// text fallback) and signature.
public sealed record RenderableNote(
    DateTime? NoteDate,
    string? ProviderName,
    IReadOnlyList<IReadOnlyList<RtfRun>>? RichBody,
    string? PlainTextFallback,
    string? SignatureImageBase64,
    string? SignedProviderName,
    DateTime? SignedAt);

// Single source of truth for the printed/faxed chart-note layout. Every flow that
// emits notes (HCFA packet, standalone notes PDF, …) calls this so the format and
// the signature block are identical everywhere — matching ChiroTouch's output.
public static class ChartNotesRenderer
{
    private static string Fmt(DateTime? d) => d?.ToString("MM/dd/yyyy") ?? "—";

    public static void AddNotePages(IDocumentContainer doc, ChartNoteHeaderCtx ctx, IEnumerable<RenderableNote> notes)
    {
        foreach (var note in notes)
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(0.6f, Unit.Inch);
                page.DefaultTextStyle(t => t.FontSize(10).LineHeight(1.3f).FontFamily(Fonts.Calibri));

                // Header strip — repeats on every physical page of this note, so
                // multi-page notes carry the patient context like ChiroTouch.
                page.Header().Element(c => Header(c, ctx, note));

                page.Content().PaddingTop(6).Element(c => Body(c, note));

                page.Footer().Element(Footer);
            });
        }
    }

    private static void Header(IContainer container, ChartNoteHeaderCtx ctx, RenderableNote note) =>
        container.Column(c =>
        {
            c.Item().Row(row =>
            {
                row.RelativeItem().Column(p =>
                {
                    p.Item().Text("Chart Notes").FontSize(13).Bold();
                    p.Item().PaddingTop(2).Text(ctx.PatientDisplayName).FontSize(11);
                });
                row.ConstantItem(230).AlignRight().Column(p =>
                {
                    var lines = (ctx.FacilityBlock ?? string.Empty)
                        .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines.Take(5))
                        p.Item().Text(line.Trim()).FontSize(9);
                });
            });

            c.Item().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Black);

            // Patient / insurance info bar (three columns each row).
            c.Item().PaddingTop(4).Row(row =>
            {
                row.RelativeItem().Text(t => Pair(t, "Patient: ", ctx.PatientDisplayName));
                row.RelativeItem().Text(t => Pair(t, "Acct #: ", ctx.AccountNo));
                row.RelativeItem().Text(t => Pair(t, "DOB: ", Fmt(ctx.PatientBirthDate)));
            });
            c.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Text(t => Pair(t, "Ins Co: ", ctx.InsCoName ?? "—"));
                row.RelativeItem().Text(t => Pair(t, "Pol #: ", ctx.PolicyNo ?? "—"));
                row.RelativeItem().Text(t => Pair(t, "Insured ID: ", ctx.InsuredId ?? "—"));
            });
            c.Item().PaddingTop(2).LineHorizontal(0.5f).LineColor(Colors.Grey.Medium);

            // Date + provider strip (light grey, like ChiroTouch's provider band).
            c.Item().Background(Colors.Grey.Lighten3).Padding(3).Row(row =>
            {
                row.RelativeItem().Text(t => Pair(t, "Date  ", Fmt(note.NoteDate)));
                row.RelativeItem().Text(t => Pair(t, "Provider:  ", note.ProviderName ?? "—"));
            });
            c.Item().PaddingBottom(2).LineHorizontal(1).LineColor(Colors.Black);
        });

    private static void Body(IContainer container, RenderableNote note) =>
        container.Column(col =>
        {
            if (note.RichBody is { Count: > 0 })
            {
                foreach (var para in note.RichBody)
                {
                    if (para.Count == 0)
                    {
                        col.Item().Height(8); // blank line
                        continue;
                    }
                    col.Item().PaddingBottom(3).Text(t =>
                    {
                        foreach (var run in para)
                        {
                            var span = t.Span(run.Text).FontColor(run.ColorHex);
                            if (run.Bold) span.Bold();
                        }
                    });
                }
            }
            else
            {
                col.Item().Text(note.PlainTextFallback
                    ?? "(No note body available — RTF could not be read.)");
            }

            col.Item().Element(c => Signature(c, note));
        });

    // "Electronically Signed" + signature image + provider / signed timestamp,
    // matching ChiroTouch's footer signature exactly.
    private static void Signature(IContainer container, RenderableNote note)
    {
        byte[]? sig = null;
        if (!string.IsNullOrWhiteSpace(note.SignatureImageBase64))
        {
            try { sig = Convert.FromBase64String(note.SignatureImageBase64); }
            catch { sig = null; }
        }

        container.PaddingTop(28).Row(row =>
        {
            row.RelativeItem().AlignBottom().Text("Electronically Signed").FontSize(9);
            row.ConstantItem(320).Column(p =>
            {
                if (sig is not null) p.Item().AlignRight().Height(38).Image(sig).FitHeight();
                else p.Item().Height(38);
                p.Item().LineHorizontal(0.75f).LineColor(Colors.Black);
                p.Item().PaddingTop(2).Text(t =>
                {
                    t.Span(note.SignedProviderName ?? note.ProviderName ?? "—").FontSize(8);
                    if (note.SignedAt is DateTime ts)
                    {
                        t.Span("  ").FontSize(8);
                        t.Span(ts.ToString("M/d/yyyy h:mm tt")).FontSize(8);
                    }
                });
            });
        });
    }

    private static void Footer(IContainer container) =>
        container.Row(row =>
        {
            row.RelativeItem().Text(t =>
            {
                t.Span("Printed:  ").FontSize(8).Bold().FontColor(Colors.Grey.Darken1);
                t.Span(DateTime.Now.ToString("dddd, MMMM d, yyyy h:mm:ss tt"))
                    .FontSize(8).FontColor(Colors.Grey.Darken1);
            });
            row.RelativeItem().AlignRight().Text(t =>
            {
                t.Span("Page ").FontSize(8).FontColor(Colors.Grey.Darken1);
                t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Darken1);
                t.Span(" Of ").FontSize(8).FontColor(Colors.Grey.Darken1);
                t.TotalPages().FontSize(8).FontColor(Colors.Grey.Darken1);
            });
        });

    private static void Pair(QuestPDF.Fluent.TextDescriptor t, string label, string value)
    {
        t.Span(label).FontSize(9).Bold();
        t.Span(value).FontSize(9);
    }
}
