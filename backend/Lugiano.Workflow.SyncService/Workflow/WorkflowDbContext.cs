using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Lugiano.Workflow.SyncService.Workflow;

// Code-first context for the WorkflowAutomation database (our system, read/write).
// ChiroTouch is NOT modeled here — it is read-only and accessed via Dapper.
public sealed class WorkflowDbContext : DbContext
{
    public WorkflowDbContext(DbContextOptions<WorkflowDbContext> options) : base(options) { }

    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<WorkflowCase> WorkflowCases => Set<WorkflowCase>();
    public DbSet<WorkflowEvent> WorkflowEvents => Set<WorkflowEvent>();
    public DbSet<DoctorNote> DoctorNotes => Set<DoctorNote>();

    // SQL Server datetime2 doesn't store DateTimeKind. We always write UTC, so stamp
    // reads as UTC — this makes the JSON include the 'Z' so clients convert correctly.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcNullableDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter()
            : base(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc)) { }
    }

    private sealed class UtcNullableDateTimeConverter : ValueConverter<DateTime?, DateTime?>
    {
        public UtcNullableDateTimeConverter()
            : base(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v) { }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SyncState>(e =>
        {
            e.ToTable("SyncState");
            e.HasKey(x => x.Id);
            e.Property(x => x.SyncKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasIndex(x => x.SyncKey).IsUnique();
        });

        b.Entity<WorkflowCase>(e =>
        {
            e.ToTable("WorkflowCase");
            e.HasKey(x => x.Id);
            e.Property(x => x.FirstName).HasMaxLength(200);
            e.Property(x => x.LastName).HasMaxLength(200);
            e.Property(x => x.CurrentState).HasMaxLength(60).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            // One workflow case per patient (MVP simplification).
            e.HasIndex(x => x.PatientId).IsUnique();

            // Navigation properties (#1) — relationship owned from the parent side.
            e.HasMany(x => x.Events)
                .WithOne()
                .HasForeignKey(x => x.WorkflowCaseId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.DoctorNotes)
                .WithOne()
                .HasForeignKey(x => x.WorkflowCaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WorkflowEvent>(e =>
        {
            e.ToTable("WorkflowEvent");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(60).IsRequired();
            e.Property(x => x.SourceSystem).HasMaxLength(50).IsRequired();
            e.Property(x => x.SourceTable).HasMaxLength(128).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            // Idempotency: one event per source row.
            e.HasIndex(x => new { x.SourceTable, x.SourceRecordId }).IsUnique();
        });

        b.Entity<DoctorNote>(e =>
        {
            e.ToTable("DoctorNote");
            e.HasKey(x => x.Id);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            // Idempotency: one DoctorNote per ChartNote.
            e.HasIndex(x => x.ChartNoteId).IsUnique();
        });
    }
}
