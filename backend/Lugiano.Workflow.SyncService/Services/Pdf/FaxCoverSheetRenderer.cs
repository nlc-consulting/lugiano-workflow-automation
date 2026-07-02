using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Lugiano.Workflow.SyncService.Services.Pdf;

// Data to render one fax cover sheet. Composed by FaxService from (patient,
// carrier, practice-config, today's date). A record so the renderer stays
// stateless and the payload is unit-testable.
public sealed record FaxCoverSheetData(
    // TO block — the carrier we're faxing to.
    string ToCompany,
    string ToFax,
    string? ToAttention,        // optional "Claims Dept" etc; often null
    string? ToAddressLine1,
    string? ToCityStateZip,
    // FROM block — our practice info from config.
    string FromPractice,
    string FromAddressLine1,
    string FromCityStateZip,
    string FromPhone,
    string FromFax,
    // Fax context.
    DateTime SentDate,
    string Subject,             // "HCFA-1500 Claim" or "AR Follow-up (Tracer)"
    string PatientLine,         // "RE: Jane Doe (Acct 123456), DOS 06/23/2026"
    string PagesText            // "See attached" or "1 + 3 pages", caller's call
);

// Renders the required HIPAA fax cover sheet as the FIRST page of every
// outbound fax (HCFA + Tracer) so the confidentiality notice + recipient
// block are top-of-stack. It's a cover, not a footer, because intended-
// recipient handling matters BEFORE the recipient sees PHI: if the fax lands
// in the wrong tray, page 1 says "if this isn't you, stop and call us."
public static class FaxCoverSheetRenderer
{
    // Standard HIPAA confidentiality wording. Reviewed against typical
    // healthcare fax covers — no state-specific carve-outs. Change here (not
    // per-caller) if legal counsel requests specific wording later.
    public const string ConfidentialityNotice =
        "CONFIDENTIALITY NOTICE: The documents accompanying this fax " +
        "transmission may contain confidential health information that is " +
        "legally privileged. This information is intended only for the use " +
        "of the individual or entity named above. The authorized recipient " +
        "is prohibited from disclosing this information to any other party " +
        "unless required to do so by law or regulation and is required to " +
        "destroy the information after its stated need has been fulfilled. " +
        "If you are not the intended recipient, any disclosure, copying, " +
        "distribution, or action taken in reliance on the contents of these " +
        "documents is strictly prohibited. If you have received this fax in " +
        "error, please notify the sender immediately by telephone and " +
        "arrange for the return or destruction of these documents.";

    public static void AddCoverPageToDocument(IDocumentContainer doc, FaxCoverSheetData data)
    {
        doc.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(0.75f, Unit.Inch);
            page.DefaultTextStyle(x => x.FontSize(11).FontFamily("Arial"));

            page.Content().Column(col =>
            {
                col.Spacing(20);

                col.Item().AlignCenter().Text("FAX COVER SHEET")
                    .FontSize(24).Bold().LetterSpacing(0.02f);
                col.Item().LineHorizontal(1).LineColor(Colors.Grey.Darken1);

                // TO / FROM two-column block.
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(to =>
                    {
                        to.Item().Text("TO").FontSize(9).SemiBold().FontColor(Colors.Grey.Darken1);
                        to.Item().Text(data.ToCompany).FontSize(13).Bold();
                        if (!string.IsNullOrWhiteSpace(data.ToAttention))
                            to.Item().Text($"Attn: {data.ToAttention}");
                        if (!string.IsNullOrWhiteSpace(data.ToAddressLine1))
                            to.Item().Text(data.ToAddressLine1!);
                        if (!string.IsNullOrWhiteSpace(data.ToCityStateZip))
                            to.Item().Text(data.ToCityStateZip!);
                        to.Item().PaddingTop(4).Text($"Fax: {data.ToFax}").SemiBold();
                    });

                    row.ConstantItem(30);

                    row.RelativeItem().Column(from =>
                    {
                        from.Item().Text("FROM").FontSize(9).SemiBold().FontColor(Colors.Grey.Darken1);
                        from.Item().Text(data.FromPractice).FontSize(13).Bold();
                        from.Item().Text(data.FromAddressLine1);
                        from.Item().Text(data.FromCityStateZip);
                        from.Item().PaddingTop(4).Text($"Phone: {data.FromPhone}");
                        from.Item().Text($"Fax:   {data.FromFax}");
                    });
                });

                col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);

                // Metadata row: Date | Subject | Pages
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text("DATE").FontSize(9).SemiBold().FontColor(Colors.Grey.Darken1);
                        c.Item().Text(data.SentDate.ToString("MMMM d, yyyy"));
                    });
                    row.RelativeItem(2).Column(c =>
                    {
                        c.Item().Text("SUBJECT").FontSize(9).SemiBold().FontColor(Colors.Grey.Darken1);
                        c.Item().Text(data.Subject);
                    });
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text("PAGES").FontSize(9).SemiBold().FontColor(Colors.Grey.Darken1);
                        c.Item().Text(data.PagesText);
                    });
                });

                // RE: block for patient identification — exposes nothing more
                // than the HCFA/tracer inside already shows.
                col.Item().PaddingTop(6).Column(re =>
                {
                    re.Item().Text("RE").FontSize(9).SemiBold().FontColor(Colors.Grey.Darken1);
                    re.Item().Text(data.PatientLine);
                });

                // Spacer to push confidentiality block toward bottom-half.
                col.Item().PaddingVertical(20).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);

                // Confidentiality notice — the load-bearing HIPAA element.
                col.Item().Column(conf =>
                {
                    conf.Spacing(6);
                    conf.Item().Text("CONFIDENTIALITY NOTICE").FontSize(10).Bold().FontColor(Colors.Red.Darken2);
                    conf.Item().Text(ConfidentialityNotice).FontSize(10).LineHeight(1.35f);
                });
            });

            page.Footer().AlignCenter().Text(t =>
            {
                t.Span("Fax generated by Lugiano Workflow Portal · ").FontSize(8).FontColor(Colors.Grey.Darken1);
                t.Span(data.SentDate.ToString("yyyy-MM-dd HH:mm")).FontSize(8).FontColor(Colors.Grey.Darken1);
            });
        });
    }
}
