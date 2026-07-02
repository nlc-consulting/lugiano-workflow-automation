namespace Lugiano.Workflow.SyncService.ChiroTouch;

// Maps a provider to a canonical office label for the workflow dashboard.
//
// ChiroTouch encodes the office in the provider's CREDENTIAL SUFFIX — the token
// after the last comma in FullName. Each office gets its own Doctor record per
// person: "Roger Saias, DC, CC" is Center City, "…, DC, NB" is North Broad, etc.
// This is the reliable signal — FacilityStreet2 is blank on most of these office
// records, so address-based matching mis-collapses everything into Main.
//
// Suffix → office (confirmed against note volume):
//   BS = Butler Street (main)   CC = Center City        NB = North Broad
//   LA = Lebanon Avenue         SP = South Philadelphia  WA = Woodland
// Unknown/blank suffix → Other. Add new codes here as offices come online.
public static class OfficeResolver
{
    public const string Main = "PA Pain & Rehab (Main)";
    public const string CenterCity = "Center City";
    public const string Other = "Other / Unassigned";

    private static readonly IReadOnlyDictionary<string, string> BySuffix =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BS"] = Main,                  // 537 West Butler — main office
            ["CC"] = CenterCity,            // 1528 Walnut St
            ["NB"] = "North Broad",         // 5700 N. Broad St
            ["LA"] = "Lebanon Avenue",      // 6522 Lebanon Ave
            ["SP"] = "South Philadelphia",  // 1801 South 20th St
            ["WA"] = "Woodland",            // Woodland Avenue
        };

    // Canonical labels in dashboard display order — also the filter choices.
    public static readonly IReadOnlyList<string> All = new[]
    {
        CenterCity, Main, "North Broad", "Lebanon Avenue", "South Philadelphia", "Woodland", Other,
    };

    public static string Resolve(string? providerFullName)
    {
        var suffix = SuffixOf(providerFullName);
        return suffix is not null && BySuffix.TryGetValue(suffix, out var office) ? office : Other;
    }

    // Token after the last comma, e.g. "Roger Saias, DC, CC" -> "CC".
    private static string? SuffixOf(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return null;
        var idx = fullName.LastIndexOf(',');
        if (idx < 0 || idx >= fullName.Length - 1) return null;
        var suffix = fullName[(idx + 1)..].Trim();
        return suffix.Length == 0 ? null : suffix;
    }
}
