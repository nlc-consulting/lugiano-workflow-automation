using Lugiano.Workflow.SyncService;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.Services;
using Lugiano.Workflow.SyncService.Services.Email;
using Lugiano.Workflow.SyncService.Services.EobScanning;
using Lugiano.Workflow.SyncService.Services.Scrubbing;
using Lugiano.Workflow.SyncService.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// HTTP host + portal CORS origins are config-driven (Hosting:Url, Hosting:AllowedOrigins)
// so prod can override without code changes. Defaults match current dev setup.
var hostingUrl = builder.Configuration["Hosting:Url"] ?? "http://localhost:5100";
builder.WebHost.UseUrls(hostingUrl);

// Bump Kestrel body-size cap to 300MB so EOB scan uploads (65MB non-lockbox,
// 250MB LM mail on heavy days) can land. Default is 30MB — a scan upload
// past that just hangs the client. Controller-level [RequestSizeLimit] alone
// doesn't override the server-level cap.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 300L * 1024 * 1024);
// FormOptions governs multipart parsing — also has a 128MB default cap.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 300L * 1024 * 1024;
    o.ValueLengthLimit = int.MaxValue;
});

var allowedOrigins = builder.Configuration.GetSection("Hosting:AllowedOrigins").Get<string[]>()
                     ?? new[] { "http://localhost:5173" };
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(allowedOrigins)
     .AllowAnyHeader()
     .AllowAnyMethod()
     .WithExposedHeaders("Content-Range")));

// JWT bearer auth — validates the same login tokens portal-api (Nest) issues,
// signed with the shared HS256 secret (Jwt:Secret == portal-api JWT_SECRET).
// Nest sets no issuer/audience, so we validate signature + lifetime only.
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException(
        "Jwt:Secret is not configured (must equal portal-api JWT_SECRET).");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromMinutes(2),
        };
        // Browser-opened resources (PDF previews via window.open) can't send an
        // Authorization header — also accept the JWT from ?access_token=.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (string.IsNullOrEmpty(ctx.Token))
                {
                    var qt = ctx.Request.Query["access_token"].ToString();
                    if (!string.IsNullOrEmpty(qt)) ctx.Token = qt;
                }
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization(options =>
{
    // Lock every endpoint down by default so no controller can forget [Authorize].
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddControllers();

var workerOptions = builder.Configuration.GetSection(WorkerOptions.SectionName).Get<WorkerOptions>()
                    ?? new WorkerOptions();
builder.Services.AddSingleton(workerOptions);

// WorkflowAutomation DB (ours): EF Core code-first.
builder.Services.AddDbContextFactory<WorkflowDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("WorkflowAutomation")));

// ChiroTouch DB (source): Dapper, read-only (lugiano_ro).
builder.Services.AddSingleton<ISourceDbConnectionFactory, SourceDbConnectionFactory>();
builder.Services.AddSingleton<IInsuranceReadQueries, InsuranceReadQueries>();
builder.Services.AddSingleton<IChartNoteReadQueries, ChartNoteReadQueries>();
builder.Services.AddSingleton<IPatientDetailQueries, PatientDetailQueries>();
builder.Services.AddSingleton<IPatientStatusQueries, PatientStatusQueries>();
builder.Services.AddSingleton<IDoctorReadQueries, DoctorReadQueries>();

// ChiroTouch DB (write): separate lugiano_rw account, INSERT-only on the 3
// writeback tables (ChartText, ChartNotes, Signatures). Intentionally a
// distinct factory + service so reads can never accidentally write.
builder.Services.AddSingleton<ISourceDbWriteConnectionFactory, SourceDbWriteConnectionFactory>();
builder.Services.AddSingleton<IPSChiroWriteService, PSChiroWriteService>();
builder.Services.AddSingleton<EobPreviewService>();
builder.Services.AddSingleton<EobPostingService>();
builder.Services.AddSingleton<BillChargesService>();
builder.Services.AddSingleton<HcfaPreviewService>();
builder.Services.AddSingleton<NotesPreviewService>();
builder.Services.AddSingleton<TracerPreviewService>();

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
// The "anthropic" client is shared with EobScan — bumped to 5min because PDF
// extraction over a 15-page slice typically takes 40-100s, occasionally more
// on retries. Chart-note scrubs still complete in 5-15s so the longer ceiling
// doesn't affect their happy path.
builder.Services.AddHttpClient("anthropic", c =>
{
    c.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddSingleton<IScrubber, ClaudeScrubber>();
builder.Services.AddSingleton<ScrubOrchestrator>();

// EOB scanner — replaces the DS mail-scan vendor. Claude Vision over the
// scanned PDF, fans out 15-page chunks (2-page overlap) in parallel, dedupes,
// persists straight into the DB so staff work the data inside the portal
// instead of round-tripping through an xlsx import.
builder.Services.AddSingleton<IClaudeEobExtractor, ClaudeEobExtractor>();
builder.Services.AddSingleton<EobScanService>();

// Documo cloud fax — typed HttpClient + options binding. Used by FaxService
// to send HCFA + tracer PDFs straight to carrier fax inboxes.
builder.Services.Configure<Lugiano.Workflow.SyncService.Services.Fax.DocumoOptions>(
    builder.Configuration.GetSection("Documo"));
builder.Services.AddHttpClient<Lugiano.Workflow.SyncService.Services.Fax.DocumoFaxClient>();
builder.Services.AddScoped<Lugiano.Workflow.SyncService.Services.Fax.FaxService>();

// Background poller (ChiroTouch -> WorkflowAutomation), runs alongside the HTTP API.
builder.Services.AddHostedService<Worker>();

var app = builder.Build();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

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
