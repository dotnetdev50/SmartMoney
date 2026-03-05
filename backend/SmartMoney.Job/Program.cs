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

// H0: Before 8 PM IST, NSE data for today is not yet published.
// Target the previous trading day so we process real, available data.
const int NsePublishHourIst = 20; // NSE publishes end-of-day data at ~8:00 PM IST
bool isBeforeNsePublish = istNow.Hour < NsePublishHourIst;
var targetDate = isBeforeNsePublish ? today.AddDays(-1) : today;

// If targetDate rolled back to a weekend, roll back further to Friday
while (targetDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
    targetDate = targetDate.AddDays(-1);

// Console.WriteLine is used here because the DI logger is not yet available at this point.
Console.WriteLine($"[H0] IST now: {istNow:HH:mm}. isBeforeNsePublish={isBeforeNsePublish}. targetDate={targetDate:yyyy-MM-dd}");

// H1: Skip weekends – if today is a weekend AND time is >= 8 PM, there is no trading data.
// (The pre-8 PM case is already handled by H0 rolling back targetDate past weekends.)
if (today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
{
    Console.WriteLine($"[H1] Today ({today:yyyy-MM-dd}, {today.DayOfWeek}) is a weekend. Skipping job.");
    return;
}

// Working range for ingest (last 90 days → ensures enough history for 20-day window)
var to = targetDate;
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
    // FO Bhavcopy base (legacy PCR fallback – old format with SYMBOL/OPTION_TYP columns)
    o.FoBhavCopyBaseUrl = "https://nsearchives.nseindia.com/content/historical/DERIVATIVES/";
    // India VIX full-history CSV
    o.VixArchiveUrl = "https://nsearchives.nseindia.com/content/indices/hist_vix_data.csv";
    // Daily FO bhavcopy ZIP (fo{DDMMYYYY}.zip) containing op{DDMMYYYY}.csv – primary PCR source
    o.FoBhavZipBaseUrl = "https://nsearchives.nseindia.com/content/fo/";
});

services.Configure<NseJobOptions>(o =>
{
    o.Enabled = true;
    o.ExpectedParticipantRowsPerDay = 4;
});

// Register services (each gets its own named HttpClient)
services.AddHttpClient<CsvIngestionService>();
services.AddHttpClient<FoBhavCopyService>();
services.AddHttpClient<OpBhavCopyService>();
services.AddHttpClient<VixFetchService>();
services.AddHttpClient<PrPcrService>();
services.AddScoped<DailyPipelineService>();

var sp = services.BuildServiceProvider();
var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("SmartMoney.Job");
var jobOpts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NseJobOptions>>().Value;

log.LogInformation("[H0] IST now: {IstNow:HH:mm}. isBeforeNsePublish={Flag}. targetDate={TargetDate}",
    istNow, isBeforeNsePublish, targetDate.ToString("yyyy-MM-dd"));

// ---- Migrate DB ----
using (var scope = sp.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SmartMoneyDbContext>();
    await db.Database.MigrateAsync();
}

// ---- H2: Skip if target date's data is already exported with PCR and VIX present ----
var repoRoot = FindRepoRoot();
var publicDataDir = Path.Combine(repoRoot, "frontend", "public", "data");
var publicMarketTodayPath = Path.Combine(publicDataDir, "market_today.json");

// Track whether participant data was already written in a prior run for targetDate
bool participantDataAlreadyExported = false;

if (File.Exists(publicMarketTodayPath))
{
    try
    {
        var existingJson = await File.ReadAllTextAsync(publicMarketTodayPath);
        var existing = JsonSerializer.Deserialize<JsonElement>(existingJson);
        if (existing.TryGetProperty("date", out var dateEl) &&
            dateEl.GetString() == targetDate.ToString("yyyy-MM-dd"))
        {
            bool hasPcr = existing.TryGetProperty("pcr", out var pcrEl) && pcrEl.ValueKind != JsonValueKind.Null;
            bool hasVix = existing.TryGetProperty("vix", out var vixEl) && vixEl.ValueKind != JsonValueKind.Null;

            if (hasPcr && hasVix)
            {
                log.LogInformation("[H2] market_today.json already contains complete data with PCR and VIX ({Date}). Skipping job.", targetDate.ToString("yyyy-MM-dd"));
                return;
            }

            // Participant data is present but PCR/VIX are still missing — skip re-ingestion and
            // only retry the PCR/VIX fetch so we can fill in the missing values.
            participantDataAlreadyExported = true;
            log.LogInformation("[H2] market_today.json has participant data but PCR/VIX are missing ({Date}). Skipping ingestion; will retry PCR/VIX fetch.", targetDate.ToString("yyyy-MM-dd"));
        }
        else
        {
            // Existing JSON is for a different date than targetDate — perform a full run for targetDate.
            log.LogInformation("[H2] market_today.json is for a different date than targetDate ({TargetDate}). Proceeding with full run.", targetDate.ToString("yyyy-MM-dd"));
        }
    }
    catch (Exception ex)
    {
        log.LogWarning("[H2] Could not read existing market_today.json: {Msg}. Proceeding.", ex.Message);
    }
}

// ---- Ingest range + pipeline range (skipped when participant data is already present) ----
object ingestResult = "skipped – participant data already exported today";
object runResult = "skipped – participant data already exported today";

if (!participantDataAlreadyExported)
{
    using var scope = sp.CreateScope();
    var ingestion = scope.ServiceProvider.GetRequiredService<CsvIngestionService>();
    var pipeline = scope.ServiceProvider.GetRequiredService<DailyPipelineService>();

    log.LogInformation("Ingesting range {From} → {To} (IST)", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
    ingestResult = await ingestion.IngestParticipantOiRangeAsync(from, to, CancellationToken.None);

    log.LogInformation("Running pipeline range {From} → {To} (IST)", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
    runResult = await pipeline.RunRangeAsync(from, to, CancellationToken.None);
}

// ---- Export participant data immediately so JSON files are updated regardless of PCR/VIX ----
// (H4: fallback to last available day)
MarketTodayDto marketToday;
List<MarketHistoryPointDto> history;

if (!participantDataAlreadyExported)
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SmartMoneyDbContext>();

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

    if (exportDate != targetDate)
    {
        log.LogWarning("[H4] targetDate's data ({TargetDate}) not available. Falling back to last available: {ExportDate}.",
            targetDate.ToString("yyyy-MM-dd"), exportDate.ToString("yyyy-MM-dd"));
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
    var explanation = MarketNarrative.Explanation(regime, latest.ShockScore, participants, latest.FinalScore);

    // Write participant data now with pcr/vix as null; they will be filled in below if available.
    marketToday = new MarketTodayDto(
        index: "NIFTY",
        date: exportDate.ToString("yyyy-MM-dd"),
        final_score: latest.FinalScore,
        regime: regime,
        shock_score: latest.ShockScore,
        participants: participants,
        bias_Label: biasLabel,
        strength: strength,
        explanation: explanation,
        pcr: null,
        vix: null
    );

    var fromHist = exportDate.AddDays(-29);
    var historyRaw = await db.MarketBiases
        .AsNoTracking()
        .Where(x => x.Date >= fromHist)
        .OrderBy(x => x.Date)
        .ToListAsync();

    history = historyRaw
        .Select(x => new MarketHistoryPointDto(
            date: x.Date.ToString("yyyy-MM-dd"),
            final_score: x.FinalScore,
            regime: x.Regime.ToString().ToUpperInvariant()
        ))
        .ToList();

    var jsonOptsIntermediate = new JsonSerializerOptions { WriteIndented = true };

    // Write participant data immediately so it is available even if PCR/VIX fetch times out.
    // Only write to public/data/ — the frontend build (npm run build) copies these files into
    // dist/data/, so a single source of truth in public/ is sufficient.
    Directory.CreateDirectory(publicDataDir);
    await File.WriteAllTextAsync(publicMarketTodayPath,
        JsonSerializer.Serialize(marketToday, jsonOptsIntermediate));
    await File.WriteAllTextAsync(Path.Combine(publicDataDir, "market_history_30.json"),
        JsonSerializer.Serialize(history, jsonOptsIntermediate));

    log.LogInformation("[H4] Participant data exported (PCR/VIX pending). public: {PubDir}", publicDataDir);
}
else
{
    // Participant data already exported — read the existing snapshot so we can later patch PCR/VIX.
    log.LogInformation("[H4] Re-using previously exported participant data; will update PCR/VIX only.");
    var existingJson = await File.ReadAllTextAsync(publicMarketTodayPath);
    marketToday = JsonSerializer.Deserialize<MarketTodayDto>(existingJson)
        ?? throw new InvalidOperationException($"Failed to deserialize existing market_today.json from {publicMarketTodayPath}.");

    // Reconstruct history from the public file (written in a prior run).
    var publicHistPath = Path.Combine(publicDataDir, "market_history_30.json");
    if (File.Exists(publicHistPath))
    {
        var histJson = await File.ReadAllTextAsync(publicHistPath);
        history = JsonSerializer.Deserialize<List<MarketHistoryPointDto>>(histJson) ?? new();
    }
    else
    {
        history = new();
    }
}

// ---- Fetch PCR and VIX (H3: up to PcrVixMaxRetries attempts, write partial data after each) ----
// Use pcrVixDate derived from the actual exported data date (marketToday.date), NOT `today`.
// This ensures PCR/VIX match the participant data date, especially when H4 fallback is active
// and exportDate is the prior trading day rather than today.
if (!DateTime.TryParseExact(marketToday.date, "yyyy-MM-dd",
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var pcrVixDate))
{
    log.LogError("[H3] Cannot parse marketToday.date '{Date}' as yyyy-MM-dd. Skipping PCR/VIX fetch.", marketToday.date);
    return;
}

int maxPcrVixRetries = jobOpts.PcrVixMaxRetries;
int retryDelayMs     = jobOpts.PcrVixRetryMinutes * 60 * 1000;

double? pcr                = null;
double? pcrVolume          = null;
double? bankniftyPcr       = null;
double? bankniftyPcrVolume = null;
double? vix                = null;

using (var scope = sp.CreateScope())
{
    var opBhavSvc = scope.ServiceProvider.GetRequiredService<OpBhavCopyService>();
    var prPcrSvc  = scope.ServiceProvider.GetRequiredService<PrPcrService>();
    var bhavSvc   = scope.ServiceProvider.GetRequiredService<FoBhavCopyService>();
    var vixSvc    = scope.ServiceProvider.GetRequiredService<VixFetchService>();

    var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

    for (int attempt = 1; attempt <= maxPcrVixRetries; attempt++)
    {
        log.LogInformation("[H3] Fetching PCR/VIX for {Date}, attempt {Attempt}/{Max}",
            pcrVixDate.ToString("yyyy-MM-dd"), attempt, maxPcrVixRetries);

        // Primary PCR source: NSE op-bhavcopy (op{DDMMYYYY}.csv from fo{DDMMYYYY}.zip)
        // Aggregates CE and PE across ALL expiries for NIFTY and BANKNIFTY.
        if (pcr is null)
        {
            var opResult = await opBhavSvc.FetchPcrAsync(pcrVixDate, CancellationToken.None);
            if (opResult is not null)
            {
                pcr                = opResult.NiftyPcrOi;
                pcrVolume          = opResult.NiftyPcrVolume;
                bankniftyPcr       = opResult.BankniftyPcrOi;
                bankniftyPcrVolume = opResult.BankniftyPcrVolume;
                log.LogInformation(
                    "[H3] PCR sourced from op-bhavcopy: OI={Pcr}, Vol={PcrVol}, BNF OI={BnfPcr}, BNF Vol={BnfVol}",
                    pcr, pcrVolume, bankniftyPcr, bankniftyPcrVolume);
            }
        }

        // Secondary PCR source: NSE PR file (OI + Volume PCR for NIFTY and BANKNIFTY)
        if (pcr is null)
        {
            var prResult = await prPcrSvc.FetchPcrAsync(pcrVixDate, CancellationToken.None);
            if (prResult is not null)
            {
                pcr                = prResult.NiftyPcrOi;
                pcrVolume          = prResult.NiftyPcrVolume;
                bankniftyPcr       = prResult.BankniftyPcrOi;
                bankniftyPcrVolume = prResult.BankniftyPcrVolume;
                log.LogInformation(
                    "[H3] PCR sourced from PR file: OI={Pcr}, Vol={PcrVol}, BNF OI={BnfPcr}, BNF Vol={BnfVol}",
                    pcr, pcrVolume, bankniftyPcr, bankniftyPcrVolume);
            }
        }

        // Tertiary fallback: FO Bhavcopy (NIFTY OI PCR only)
        if (pcr is null)
        {
            pcr = await bhavSvc.FetchPcrAsync(pcrVixDate, CancellationToken.None);
            if (pcr.HasValue)
                log.LogInformation("[H3] NIFTY OI PCR sourced from FO bhavcopy fallback: {Pcr}", pcr);
        }

        if (vix is null)
            vix = await vixSvc.FetchVixAsync(pcrVixDate, CancellationToken.None);

        // Write whatever is available right now so the dashboard shows partial data immediately.
        // This covers the case where e.g. PCR is obtained on attempt 1 but VIX is still pending.
        bool anyData = pcr.HasValue || vix.HasValue || pcrVolume.HasValue
                       || bankniftyPcr.HasValue || bankniftyPcrVolume.HasValue;
        if (anyData)
        {
            marketToday = marketToday with
            {
                pcr                  = pcr,
                vix                  = vix,
                pcr_volume           = pcrVolume,
                banknifty_pcr        = bankniftyPcr,
                banknifty_pcr_volume = bankniftyPcrVolume,
            };

            Directory.CreateDirectory(publicDataDir);
            await File.WriteAllTextAsync(publicMarketTodayPath,
                JsonSerializer.Serialize(marketToday, jsonOpts));
            await File.WriteAllTextAsync(Path.Combine(publicDataDir, "market_history_30.json"),
                JsonSerializer.Serialize(history, jsonOpts));

            log.LogInformation(
                "[H3] JSON patched after attempt {Attempt}: PCR={Pcr}, PCRVol={PcrVol}, BNF={BnfPcr}, VIX={Vix}. public: {PubDir}",
                attempt, pcr, pcrVolume, bankniftyPcr, vix, publicDataDir);
        }

        // All data complete — no need to retry further.
        if (pcr.HasValue && vix.HasValue)
        {
            log.LogInformation("[H3] All data complete on attempt {Attempt}. PCR={Pcr}, VIX={Vix}. Done.",
                attempt, pcr, vix);
            break;
        }

        if (attempt < maxPcrVixRetries)
        {
            log.LogWarning(
                "[H3] PCR or VIX still missing after attempt {Attempt}/{Max}. Waiting {Delay} min before next attempt.",
                attempt, maxPcrVixRetries, retryDelayMs / 60000);
            await Task.Delay(retryDelayMs, CancellationToken.None);
        }
        else
        {
            log.LogWarning(
                "[H3] Stopping after {Max} attempt(s). Final: PCR={Pcr}, VIX={Vix}. Dashboard shows whatever is available.",
                maxPcrVixRetries, pcr, vix);
        }
    }
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