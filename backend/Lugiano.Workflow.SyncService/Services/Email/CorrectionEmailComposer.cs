using System.Text;
using Lugiano.Workflow.SyncService.Workflow.Models;

namespace Lugiano.Workflow.SyncService.Services.Email;

// Builds the kickback email body. Uses patient initials + DOB + date of
// service so the message is HIPAA-friendlier than including full PHI.
public static class CorrectionEmailComposer
{
    public sealed record PatientContext(
        string? FirstName,
        string? LastName,
        DateTime? DateOfBirth,
        DateTime? DateOfService);

    public static EmailMessage Compose(
        string recipientEmail,
        string? doctorName,
        PatientContext patient,
        IReadOnlyList<string> missingItems,
        string? reviewerComments,
        int roundNumber)
    {
        var subject = BuildSubject(patient, roundNumber);
        var body = BuildBody(doctorName, patient, missingItems, reviewerComments, roundNumber);
        return new EmailMessage(
            ToEmail: recipientEmail,
            ToName: doctorName,
            Subject: subject,
            PlainTextBody: body);
    }

    private static string BuildSubject(PatientContext patient, int round)
    {
        var initials = Initials(patient);
        var date = patient.DateOfService?.ToString("M/d/yyyy") ?? "recent visit";
        var roundSuffix = round > 1 ? $" (round {round})" : string.Empty;
        return $"Note correction needed — patient {initials}, {date}{roundSuffix}";
    }

    private static string BuildBody(
        string? doctorName,
        PatientContext patient,
        IReadOnlyList<string> missingItems,
        string? reviewerComments,
        int round)
    {
        var sb = new StringBuilder();
        var greeting = string.IsNullOrWhiteSpace(doctorName) ? "Doctor" : doctorName;
        sb.AppendLine($"Hi {greeting},");
        sb.AppendLine();
        sb.AppendLine("A chart note needs your attention before billing can proceed:");
        sb.AppendLine();
        sb.AppendLine($"  Patient:         {Initials(patient)}");
        if (patient.DateOfBirth is { } dob)
            sb.AppendLine($"  DOB:             {dob:M/d/yyyy}");
        if (patient.DateOfService is { } dos)
            sb.AppendLine($"  Date of service: {dos:M/d/yyyy}");
        if (round > 1)
            sb.AppendLine($"  Review round:    {round}");
        sb.AppendLine();

        if (missingItems.Count > 0)
        {
            sb.AppendLine("Items flagged:");
            foreach (var item in missingItems)
                sb.AppendLine($"  • {item}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(reviewerComments))
        {
            sb.AppendLine("Reviewer notes:");
            sb.AppendLine(reviewerComments.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("Please update the note in ChiroTouch and it will be re-reviewed automatically on the next sync.");
        sb.AppendLine();
        sb.AppendLine("Thank you,");
        sb.AppendLine("Lugiano Medical billing team");
        return sb.ToString();
    }

    private static string Initials(PatientContext p)
    {
        var f = string.IsNullOrWhiteSpace(p.FirstName) ? "?" : p.FirstName.Trim()[..1].ToUpperInvariant();
        var l = string.IsNullOrWhiteSpace(p.LastName) ? "?" : p.LastName.Trim()[..1].ToUpperInvariant();
        return $"{f}.{l}.";
    }
}
