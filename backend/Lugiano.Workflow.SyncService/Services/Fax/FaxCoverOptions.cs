namespace Lugiano.Workflow.SyncService.Services.Fax;

// Practice-side info printed in the FROM block of every fax cover sheet.
// Bound from the "FaxCover" appsettings section so the practice can update
// address/phone without a code change.
public sealed class FaxCoverOptions
{
    public const string SectionName = "FaxCover";

    // Defaults tuned to the current PA Pain & Rehab site; override in
    // appsettings for other deployments.
    public string PracticeName { get; set; } = "PA Pain & Rehabilitation";
    public string AddressLine1 { get; set; } = "6522 Lebanon Ave";
    public string CityStateZip  { get; set; } = "Philadelphia, PA 19151";
    public string Phone         { get; set; } = "(215) 709-4040";
    public string Fax           { get; set; } = "(215) 709-4041";
}
