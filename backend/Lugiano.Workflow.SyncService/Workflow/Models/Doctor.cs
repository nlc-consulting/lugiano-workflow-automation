namespace Lugiano.Workflow.SyncService.Workflow.Models;

public sealed class Doctor
{
    public int Id { get; set; }
    public int ChiroTouchDoctorId { get; set; }
    public string? FullName { get; set; }
    public string? Credentials { get; set; }
    public string? Npi { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
