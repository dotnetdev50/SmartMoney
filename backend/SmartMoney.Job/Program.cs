using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartMoney.Application.Options;
using SmartMoney.Application.Services;
using SmartMoney.Infrastructure.Persistence;
using SmartMoney.Job.Export;
using System.Text.Json;

static DateTimeOffset ToIst(DateTimeOffset utc) => utc.ToOffset(TimeSpan.FromHours(5.5));

var istNow = ToIst(DateTimeOffset.UtcNow);
var to = istNow.Date;
var from = to.AddDays(-90); // enough to have >= 20 trading days + cushion

// ---- DI container ----
var services = new ServiceCollection();

services.AddLogging(b =>
{
    b.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    });
    b.SetMinimumLevel(LogLevel.Information);
});

// Use SQLite file (ephemeral in runner)
var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "jobdb.sqlite");
services.AddDbContext<SmartMoneyDbContext>(opt =>
    opt.UseSqlite($"Data Source={dbPath}"));

// Options used by your ingestion service
services.Configure<NseOptions>(o =>
{
    // if you have options for base URL etc, set them here.
    // If your ingestion builds full archive URL internally, you can keep this empty.
});

// Job options (not strictly required here, but safe)
services.Configure<NseJobOptions>(o =>
{
    o.Enabled = true;
    o.ExpectedParticipantRowsPerDay = 4;
});

// Your services
services.AddHttpClient<CsvIngestionService>();  // CsvIngestionService should accept HttpClient via DI
services.AddScoped<DailyPipelineService>();

var sp = services.BuildServiceProvider();
var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("SmartMoney.Job");

using (var scope = sp.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SmartMoneyDbContext>();
    await db.Database.MigrateAsync();
}

// ---- Run bootstrap: ingest range then pipeline range ----
object ingestResult;
object runResult;

using (var scope = sp.CreateScope())
{
    var ingestion = scope.ServiceProvider.GetRequiredService<CsvIngestionService>();
    var pipeline = scope.ServiceProvider.GetRequiredService<DailyPipelineService>();

    log.LogInformation("Ingesting range {From} → {To} (IST)", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
    ingestResult = await ingestion.IngestParticipantOiRangeAsync(from, to, CancellationToken.None);

    log.LogInformation("Running pipeline range {From} → {To} (IST)", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
    runResult = await pipeline.RunRangeAsync(from, to, CancellationToken.None);
}

// ---- Export JSON for the Vue dashboard ----
using (var scope = sp.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SmartMoneyDbContext>();

    var latest = await db.MarketBiases
        .AsNoTracking()
        .OrderByDescending(x => x.Date)
        .FirstOrDefaultAsync();

    if (latest is null)
    {
        log.LogError("No market_bias rows produced. Exiting with failure.");
        Environment.ExitCode = 1;
        return;
    }

    var date = latest.Date;

    var metrics = await db.ParticipantMetrics
        .AsNoTracking()
        .Where(x => x.Date == date)
        .OrderBy(x => x.Participant)
        .ToListAsync();

    var participants = metrics
        .Select(m =>
        {
            var name = m.Participant.ToString().ToUpperInvariant();
            var label = MarketNarrative.ParticipantLabel(m.ParticipantBias);
            return new ParticipantDto(name, m.ParticipantBias, label);
        })
        .OrderBy(p => p.name)
        .ToList();

    var (biasLabel, strength) = MarketNarrative.ScoreLabel(latest.FinalScore);

    var regime = latest.Regime.ToString().ToUpperInvariant();

    var explanation = MarketNarrative.Explanation(
        regime,
        latest.ShockScore,
        participants,
        latest.FinalScore);

    var marketToday = new MarketTodayDto(
        index: "NIFTY",
        date: date.ToString("yyyy-MM-dd"),
        final_score: latest.FinalScore,
        regime: regime,
        shock_score: latest.ShockScore,
        participants: participants,
        bias_Label: biasLabel,
        strength: strength,
        explanation: explanation
    );

    var fromHist = date.AddDays(-29);
    var historyRaw = await db.MarketBiases
        .AsNoTracking()
        .Where(x => x.Date >= fromHist)
        .OrderBy(x => x.Date)
        .ToListAsync();

    var history = historyRaw
        .Select(x => new MarketHistoryPointDto(
            date: x.Date.ToString("yyyy-MM-dd"),
            final_score: x.FinalScore,
            regime: x.Regime.ToString().ToUpperInvariant()
        ))
        .ToList();

    // Output folder that will be deployed (Vue dist)
    var repoRoot = FindRepoRoot();
    var outDir = Path.Combine(repoRoot, "frontend", "dist", "data");
    Directory.CreateDirectory(outDir);

    var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

    await File.WriteAllTextAsync(Path.Combine(outDir, "market_today.json"),
        JsonSerializer.Serialize(marketToday, jsonOpts));

    await File.WriteAllTextAsync(Path.Combine(outDir, "market_history_30.json"),
        JsonSerializer.Serialize(history, jsonOpts));

    // Optional: write job summary artifacts
    await File.WriteAllTextAsync(Path.Combine(outDir, "job_ingest_result.json"),
        JsonSerializer.Serialize(ingestResult, jsonOpts));

    await File.WriteAllTextAsync(Path.Combine(outDir, "job_run_result.json"),
        JsonSerializer.Serialize(runResult, jsonOpts));

    log.LogInformation("Exported JSON to {Dir}", outDir);
}
static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        var hasFrontend = Directory.Exists(Path.Combine(dir.FullName, "frontend"));
        var hasBackend = Directory.Exists(Path.Combine(dir.FullName, "backend"));
        if (hasFrontend && hasBackend) return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Repo root not found (expected /frontend and /backend).");
}

log.LogInformation("DONE");