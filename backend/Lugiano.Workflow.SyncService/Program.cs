using Lugiano.Workflow.SyncService;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.Services;
using Lugiano.Workflow.SyncService.Workflow;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Serve HTTP here (and the background poller runs in the same process).
builder.WebHost.UseUrls("http://localhost:5100");

// CORS for the Vite dev origin; expose Content-Range so react-admin reads totals.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173")
     .AllowAnyHeader()
     .AllowAnyMethod()
     .WithExposedHeaders("Content-Range")));

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

builder.Services.AddSingleton<SyncStateService>();
builder.Services.AddSingleton<WorkflowCaseService>();
builder.Services.AddSingleton<InsuranceSyncService>();
builder.Services.AddSingleton<ChartNoteSyncService>();

// Background poller (ChiroTouch -> WorkflowAutomation), runs alongside the HTTP API.
builder.Services.AddHostedService<Worker>();

var app = builder.Build();
app.UseCors();

// Prototype convenience: create/upgrade the WorkflowAutomation schema on startup.
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<WorkflowDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

app.MapControllers();
app.Run();
