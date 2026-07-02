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
    public DbSet<Doctor> Doctors => Set<Doctor>();
    public DbSet<CorrectionRequest> CorrectionRequests => Set<CorrectionRequest>();
    public DbSet<ScrubResult> ScrubResults => Set<ScrubResult>();
    public DbSet<EobScan> EobScans => Set<EobScan>();
    public DbSet<EobScanCheck> EobScanChecks => Set<EobScanCheck>();
    public DbSet<EobScanLineItem> EobScanLineItems => Set<EobScanLineItem>();

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
            // Idempotency: one DoctorNote per ChartNote. Filter NULL out so
            // portal-authored rows (ChartNoteId IS NULL) can coexist.
            e.HasIndex(x => x.ChartNoteId)
                .IsUnique()
                .HasFilter("[ChartNoteId] IS NOT NULL");
        });

        b.Entity<Doctor>(e =>
        {
            e.ToTable("Doctor");
            e.HasKey(x => x.Id);
            e.Property(x => x.FullName).HasMaxLength(200);
            e.Property(x => x.Credentials).HasMaxLength(100);
            e.Property(x => x.Npi).HasMaxLength(20);
            e.Property(x => x.Email).HasMaxLength(254); // RFC 5321 cap
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            // One row per ChiroTouch doctor ID (the link back to PSChiro).
            e.HasIndex(x => x.ChiroTouchDoctorId).IsUnique();
        });

        b.Entity<CorrectionRequest>(e =>
        {
            e.ToTable("CorrectionRequest");
            e.HasKey(x => x.Id);
            e.Property(x => x.State).HasMaxLength(40).IsRequired();
            e.Property(x => x.ReviewerEmail).HasMaxLength(254);
            e.Property(x => x.RecipientOverrideEmail).HasMaxLength(254);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            // Most lookups are "open requests for this note" — index supports that.
            e.HasIndex(x => new { x.DoctorNoteId, x.State });
            e.HasIndex(x => x.WorkflowCaseId);
        });

        b.Entity<ScrubResult>(e =>
        {
            e.ToTable("ScrubResult");
            e.HasKey(x => x.Id);
            e.Property(x => x.Verdict).HasMaxLength(40).IsRequired();
            e.Property(x => x.ModelUsed).HasMaxLength(80).IsRequired();
            e.Property(x => x.PromptVersion).HasMaxLength(40).IsRequired();
            e.Property(x => x.RanAt).HasDefaultValueSql("SYSUTCDATETIME()");
            // "Latest result for this note" is the hot lookup — index it.
            e.HasIndex(x => new { x.DoctorNoteId, x.RanAt });
        });

        b.Entity<EobScan>(e =>
        {
            e.ToTable("EobScan");
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceFilename).HasMaxLength(400).IsRequired();
            e.Property(x => x.StoredPdfPath).HasMaxLength(1000);
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.ModelUsed).HasMaxLength(80);
            e.Property(x => x.EstimatedCostUsd).HasColumnType("decimal(10,4)");
            e.Property(x => x.UploadedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            // Dashboard query is "most recent scans" — index supports that.
            e.HasIndex(x => x.UploadedAt);
            e.HasMany(x => x.Checks)
                .WithOne()
                .HasForeignKey(x => x.EobScanId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.LineItems)
                .WithOne()
                .HasForeignKey(x => x.EobScanId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EobScanCheck>(e =>
        {
            e.ToTable("EobScanCheck");
            e.HasKey(x => x.Id);
            e.Property(x => x.CheckNumber).HasMaxLength(60).IsRequired();
            e.Property(x => x.CheckDate).HasMaxLength(40);
            e.Property(x => x.Amount).HasColumnType("decimal(12,2)");
            e.Property(x => x.Payer).HasMaxLength(200);
            e.Property(x => x.Administrator).HasMaxLength(200);
            // Composite index covers "all checks for this scan" AND the
            // orchestrator's dedupe-by-(scan, page, check#) overlap handling.
            e.HasIndex(x => new { x.EobScanId, x.PageNumber });
        });

        b.Entity<EobScanLineItem>(e =>
        {
            e.ToTable("EobScanLineItem");
            e.HasKey(x => x.Id);
            e.Property(x => x.ClaimNumber).HasMaxLength(80);
            e.Property(x => x.PatientNameRaw).HasMaxLength(400);
            e.Property(x => x.BillNumber).HasMaxLength(80);
            e.Property(x => x.ServiceDate).HasMaxLength(40);
            e.Property(x => x.CheckNumber).HasMaxLength(60);
            e.Property(x => x.ProcedureCode).HasMaxLength(20).IsRequired();
            e.Property(x => x.BilledAmount).HasColumnType("decimal(12,2)");
            e.Property(x => x.AllowedAmount).HasColumnType("decimal(12,2)");
            e.Property(x => x.PaidAmount).HasColumnType("decimal(12,2)");
            e.Property(x => x.WriteOffAmount).HasColumnType("decimal(12,2)");
            e.Property(x => x.ReasonCodesJson).IsRequired();
            e.HasIndex(x => new { x.EobScanId, x.PageNumber });
        });
    }
}
