namespace Lugiano.Workflow.SyncService.ChiroTouch.Models;

// READ-ONLY projection of dbo.ChartText (ChiroTouch). TextBody is RTF.
// Chunks are chained: follow NextPtr until it is 0. Never written.
public sealed class SourceChartText
{
    public int Ptr { get; set; }
    public string? TextBody { get; set; }
    public int NextPtr { get; set; }
}
