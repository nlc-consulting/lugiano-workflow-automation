using Lugiano.Workflow.SyncService;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.Services;
using Lugiano.Workflow.SyncService.Services.Email;
using Lugiano.Workflow.SyncService.Services.Scrubbing;
using Lugiano.Workflow.SyncService.Workflow;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// HTTP host + portal CORS origins are config-driven (Hosting:Url, Hosting:AllowedOrigins)
// so prod can override without code changes. Defaults match current dev setup.
var hostingUrl = builder.Configuration["Hosting:Url"] ?? "http://localhost:5100";
builder.WebHost.UseUrls(hostingUrl);

var allowedOrigins = builder.Configuration.GetSection("Hosting:AllowedOrigins").Get<string[]>()
                     ?? new[] { "http://localhost:5173" };
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(allowedOrigins)
     .AllowAnyHeader()
     .AllowAnyMethod()
     .WithExposedHeaders("Content-Range")));

// PROTOTYPE: workflow service API is unauthenticated. Production must add
// authentication here (JWT bearer issued by portal-api, or service-to-service
// API key) and an [Authorize] policy on CasesController. Portal auth already
// exists in portal-api; reuse its issuer/audience.

builder.Services.AddControllers();

var workerOptions = builder.Configuration.GetSection(WorkerOptions.SectionName).Get<WorkerOptions>()
                    ?? new WorkerOptions();
builder.Services.AddSingleton(workerOptions);

// WorkflowAutomation DB (ours): EF Core code-first.
builder.Services.AddDbContextFactory<WorkflowDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("WorkflowAutomation")));

// ChiroTouch DB (source): Dapper, read-only.
builder.Services.AddSingleton<ISourceDbConnectionFactory, SourceDbConnectionFactory>();
builder.Services.AddSingleton<IInsuranceReadQueries, InsuranceReadQueries>();
builder.Services.AddSingleton<IChartNoteReadQueries, ChartNoteReadQueries>();
builder.Services.AddSingleton<IPatientDetailQueries, PatientDetailQueries>();
builder.Services.AddSingleton<IPatientStatusQueries, PatientStatusQueries>();
builder.Services.AddSingleton<IDoctorReadQueries, DoctorReadQueries>();

builder.Services.AddSingleton<SyncStateService>();
builder.Services.AddSingleton<WorkflowCaseService>();
builder.Services.AddSingleton<InsuranceSyncService>();
builder.Services.AddSingleton<ChartNoteSyncService>();
builder.Services.AddSingleton<DoctorSyncService>();
builder.Services.AddSingleton<CorrectionRequestService>();

// Email sender selection. SMTP if Email:Smtp:Host is set, otherwise the
// FileEmailSender that writes .eml files to disk. Wrapped with a dev-override
// decorator when Email:DevOverrideRecipient is configured — all outgoing
// emails route to that single address until the override is removed.
builder.Services.AddSingleton<IEmailSender>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    IEmailSender baseSender = !string.IsNullOrWhiteSpace(config["Email:Smtp:Host"])
        ? new SmtpEmailSender(config, loggerFactory.CreateLogger<SmtpEmailSender>())
        : new FileEmailSender(config, loggerFactory.CreateLogger<FileEmailSender>());

    var devOverride = config["Email:DevOverrideRecipient"];
    return !string.IsNullOrWhiteSpace(devOverride)
        ? new DevOverrideEmailSender(baseSender, devOverride, loggerFactory.CreateLogger<DevOverrideEmailSender>())
        : baseSender;
});

// Scrubbing: named HttpClient + Claude integration + orchestrator.
// Named client (vs typed) avoids captive-HttpClient issues when consumed by
// singletons — ClaudeScrubber resolves a fresh client per scrub via the factory.
builder.Services.AddHttpClient("anthropic", c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton<IScrubber, ClaudeScrubber>();
builder.Services.AddSingleton<ScrubOrchestrator>();

// Background poller (ChiroTouch -> WorkflowAutomation), runs alongside the HTTP API.
builder.Services.AddHostedService<Worker>();

var app = builder.Build();
app.UseCors();

// PROTOTYPE: migrate-on-startup is convenient for fast iteration. Production must
// set Database:MigrateOnStartup=false and run migrations as a deliberate deploy step
// (`dotnet ef database update` from CI, or a separate migrator job) — concurrent
// instances racing this on boot will corrupt the migration history.
var migrateOnStartup = builder.Configuration.GetValue<bool?>("Database:MigrateOnStartup") ?? true;
if (migrateOnStartup)
{
    using var scope = app.Services.CreateScope();
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<WorkflowDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

// Full doctor import at startup. Cheap (~327 active doctors), runs once per
// boot, populates emails where ChiroTouch has them and preserves reviewer-set
// emails on subsequent restarts. Failure here doesn't block the HTTP API.
using (var scope = app.Services.CreateScope())
{
    var doctorSync = scope.ServiceProvider.GetRequiredService<DoctorSyncService>();
    try { await doctorSync.ImportAllAsync(); }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Doctor import failed at startup; service will continue without it.");
    }
}

app.MapControllers();
app.Run();
