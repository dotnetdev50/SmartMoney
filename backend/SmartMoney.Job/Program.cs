using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartMoney.Application.Options;
using SmartMoney.Application.Services;
using SmartMoney.Infrastructure.Persistence;
using SmartMoney.Job.Export;
using System.Text.Json;

// ---- IST helpers ----
static DateTimeOffset ToIst(DateTimeOffset utc) => utc.ToOffset(TimeSpan.FromHours(5.5));

var istNow = ToIst(DateTimeOffset.UtcNow);
var today = istNow.Date; // calendar date in IST

// H1: Skip weekends
if (today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
{
    Console.WriteLine($"[H1] Today ({today:yyyy-MM-dd}, {today.DayOfWeek}) is a weekend. Skipping job.");
    return;
}

// Working range for ingest (last 90 days → ensures enough history for 20-day window)
var to = today;
var from = to.AddDays(-90);

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

// Options used by ingestion services
services.Configure<NseOptions>(o =>
{
    // FO Bhavcopy base (PCR)
    o.FoBhavCopyBaseUrl = "https://archives.nseindia.com/content/historical/DERIVATIVES/";
    // India VIX full-history CSV
    o.VixArchiveUrl = "https://archives.nseindia.com/content/indices/hist_vix_data.csv";
});

services.Configure<NseJobOptions>(o =>
{
    o.Enabled = true;
    o.ExpectedParticipantRowsPerDay = 4;
});

// Register services (each gets its own named HttpClient)
services.AddHttpClient<CsvIngestionService>();
services.AddHttpClient<FoBhavCopyService>();
services.AddHttpClient<VixFetchService>();
services.AddScoped<DailyPipelineService>();

var sp = services.BuildServiceProvider();
var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("SmartMoney.Job");
var jobOpts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NseJobOptions>>().Value;

// ---- Migrate DB ----
using (var scope = sp.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SmartMoneyDbContext>();
    await db.Database.MigrateAsync();
}

// ---- H2: Skip if today's data is already exported ----
var repoRoot = FindRepoRoot();
var publicDataDir = Path.Combine(repoRoot, "frontend", "public", "data");
var publicMarketTodayPath = Path.Combine(publicDataDir, "market_today.json");

if (File.Exists(publicMarketTodayPath))
{
    try
    {
        var existingJson = await File.ReadAllTextAsync(publicMarketTodayPath);
        var existing = JsonSerializer.Deserialize<JsonElement>(existingJson);
        if (existing.TryGetProperty("date", out var dateEl) &&
            dateEl.GetString() == today.ToString("yyyy-MM-dd"))
        {
            log.LogInformation("[H2] market_today.json already contains today's data ({Date}). Skipping computation.", today.ToString("yyyy-MM-dd"));
            return;
        }
    }
    catch (Exception ex)
    {
        log.LogWarning("[H2] Could not read existing market_today.json: {Msg}. Proceeding.", ex.Message);
    }
}

// ---- Ingest range + pipeline range ----
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

// ---- Fetch PCR and VIX (H3: retry up to configured limit if not yet published) ----
int maxPcrVixRetries = jobOpts.PcrVixMaxRetries;
int retryDelayMs = jobOpts.PcrVixRetryMinutes * 60 * 1000;

double? pcr = null;
double? vix = null;

using (var scope = sp.CreateScope())
{
    var bhavSvc = scope.ServiceProvider.GetRequiredService<FoBhavCopyService>();
    var vixSvc = scope.ServiceProvider.GetRequiredService<VixFetchService>();

    for (int attempt = 1; attempt <= maxPcrVixRetries; attempt++)
    {
        log.LogInformation("[H3] Fetching PCR/VIX, attempt {Attempt}/{Max}", attempt, maxPcrVixRetries);

        if (pcr is null)
            pcr = await bhavSvc.FetchPcrAsync(today, CancellationToken.None);

        if (vix is null)
            vix = await vixSvc.FetchVixAsync(today, CancellationToken.None);

        if (pcr.HasValue && vix.HasValue)
        {
            log.LogInformation("[H3] PCR={Pcr}, VIX={Vix} fetched successfully on attempt {Attempt}.", pcr, vix, attempt);
            break;
        }

        if (attempt < maxPcrVixRetries)
        {
            log.LogWarning("[H3] PCR or VIX not yet available. Waiting {Delay} min before retry {Next}/{Max}.",
                retryDelayMs / 60000, attempt + 1, maxPcrVixRetries);
            await Task.Delay(retryDelayMs, CancellationToken.None);
        }
        else
        {
            log.LogWarning("[H3] PCR/VIX not fetched after {Max} attempts. PCR={Pcr}, VIX={Vix}. Continuing with nulls.", maxPcrVixRetries, pcr, vix);
        }
    }
}

// ---- Export JSON for the Vue dashboard (H4: fallback to last available day) ----
using (var scope = sp.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SmartMoneyDbContext>();

    // H4: Use the most recent available market bias (may be a prior trading day if today's data not ingested)
    var latest = await db.MarketBiases
        .AsNoTracking()
        .OrderByDescending(x => x.Date)
        .FirstOrDefaultAsync();

    if (latest is null)
    {
        log.LogError("[H4] No market_bias rows produced. Exiting with failure.");
        Environment.ExitCode = 1;
        return;
    }

    var exportDate = latest.Date;

    if (exportDate != today)
    {
        log.LogWarning("[H4] Today's data ({Today}) not available. Falling back to last available: {ExportDate}.",
            today.ToString("yyyy-MM-dd"), exportDate.ToString("yyyy-MM-dd"));
    }

    var metrics = await db.ParticipantMetrics
        .AsNoTracking()
        .Where(x => x.Date == exportDate)
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
        date: exportDate.ToString("yyyy-MM-dd"),
        final_score: latest.FinalScore,
        regime: regime,
        shock_score: latest.ShockScore,
        participants: participants,
        bias_Label: biasLabel,
        strength: strength,
        explanation: explanation,
        pcr: pcr,
        vix: vix
    );

    var fromHist = exportDate.AddDays(-29);
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

    var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

    // Write to dist/data/ (served by GitHub Pages this run)
    var distDir = Path.Combine(repoRoot, "frontend", "dist", "data");
    Directory.CreateDirectory(distDir);

    await File.WriteAllTextAsync(Path.Combine(distDir, "market_today.json"),
        JsonSerializer.Serialize(marketToday, jsonOpts));
    await File.WriteAllTextAsync(Path.Combine(distDir, "market_history_30.json"),
        JsonSerializer.Serialize(history, jsonOpts));
    await File.WriteAllTextAsync(Path.Combine(distDir, "job_ingest_result.json"),
        JsonSerializer.Serialize(ingestResult, jsonOpts));
    await File.WriteAllTextAsync(Path.Combine(distDir, "job_run_result.json"),
        JsonSerializer.Serialize(runResult, jsonOpts));

    // Also write to public/data/ so it's committable and used by H2 on next run
    Directory.CreateDirectory(publicDataDir);

    await File.WriteAllTextAsync(publicMarketTodayPath,
        JsonSerializer.Serialize(marketToday, jsonOpts));
    await File.WriteAllTextAsync(Path.Combine(publicDataDir, "market_history_30.json"),
        JsonSerializer.Serialize(history, jsonOpts));

    log.LogInformation("Exported JSON to dist: {DistDir} and public: {PubDir}", distDir, publicDataDir);
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