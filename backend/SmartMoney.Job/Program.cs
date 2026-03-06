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

await MainAsync();

static async Task MainAsync()
{
    var istNow = ToIst(DateTimeOffset.UtcNow);
    var today = istNow.Date;

    // NSE participant OI (fao_participant_oi_*.csv) is published on D+1, not D.
    // Always target the previous trading day to guarantee data availability.
    var targetDate = GetTargetDate(today);

    Console.WriteLine($"[H0] IST now: {istNow:HH:mm}. targetDate={targetDate:yyyy-MM-dd}");

    if (IsWeekend(today))
    {
        Console.WriteLine($"[H1] Today ({today:yyyy-MM-dd}, {today.DayOfWeek}) is a weekend. Skipping job.");
        return;
    }

    var to = targetDate;
    var from = to.AddDays(-90);

    var services = ConfigureServices();
    var sp = services.BuildServiceProvider();
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("SmartMoney.Job");

    log.LogInformation("[H0] IST now: {IstNow:HH:mm}. targetDate={TargetDate}",
        istNow, targetDate.ToString("yyyy-MM-dd"));

    await MigrateDbAsync(sp);

    var repoRoot = FindRepoRoot();
    var publicDataDir = Path.Combine(repoRoot, "frontend", "public", "data");
    var publicMarketTodayPath = Path.Combine(publicDataDir, "market_today.json");

    bool participantDataAlreadyExported = await CheckExistingExportAsync(publicMarketTodayPath, targetDate, log);

    object ingestResult = "skipped – participant data already exported today";

    if (!participantDataAlreadyExported)
    {
        (_, _) = await IngestAndRunPipelineAsync(sp, from, to, log);
    }

    var (marketToday, history) = await ExportOrReuseParticipantDataAsync(
        sp, participantDataAlreadyExported, publicDataDir, publicMarketTodayPath, targetDate, log);

    await FetchAndPatchPcrVixAsync(
        sp, marketToday, history, publicDataDir, publicMarketTodayPath, log);

    log.LogInformation("DONE");
}

static DateTime GetTargetDate(DateTime today)
{
    // NSE participant OI data for day N is published on day N+1 (next business day).
    // Always target the previous trading day so data is guaranteed to be available.
    var targetDate = today.AddDays(-1);
    while (IsWeekend(targetDate))
        targetDate = targetDate.AddDays(-1);
    return targetDate;
}

static bool IsWeekend(DateTime date) =>
    date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

static ServiceCollection ConfigureServices()
{
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

    var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "jobdb.sqlite");
    services.AddDbContext<SmartMoneyDbContext>(opt =>
        opt.UseSqlite($"Data Source={dbPath}"));

    // NseOptions: register with defaults defined in the class.
    // Canonical NSE endpoint configuration lives in the application/library only.
    // Runtime overrides must be applied at the API/hosting project, not here.
    services.AddOptions<NseOptions>();

    services.Configure<NseJobOptions>(o =>
    {
        if (bool.TryParse(Environment.GetEnvironmentVariable("JOB_ENABLED") ?? "", out var b)) o.Enabled = b;
        o.StartAtIst = Environment.GetEnvironmentVariable("JOB_START_AT_IST") ?? o.StartAtIst;
        o.EndAtIst = Environment.GetEnvironmentVariable("JOB_END_AT_IST") ?? o.EndAtIst;
        if (int.TryParse(Environment.GetEnvironmentVariable("JOB_RETRY_MINUTES") ?? "", out var rm)) o.RetryMinutes = rm;
        if (int.TryParse(Environment.GetEnvironmentVariable("JOB_EXPECTED_PARTICIPANT_ROWS_PER_DAY") ?? "", out var ep)) o.ExpectedParticipantRowsPerDay = ep;
    });

    services.AddHttpClient<FoBhavCopyService>();
    services.AddHttpClient<OpBhavCopyService>();
    services.AddHttpClient<PrPcrService>();
    services.AddHttpClient<VixFetchService>();
    services.AddHttpClient<CsvIngestionService>();
    services.AddScoped<DailyPipelineService>();

    return services;
}

static async Task MigrateDbAsync(ServiceProvider sp)
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SmartMoneyDbContext>();
    await db.Database.MigrateAsync();
}

static async Task<bool> CheckExistingExportAsync(string publicMarketTodayPath, DateTime targetDate, ILogger log)
{
    if (!File.Exists(publicMarketTodayPath))
        return false;

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
                return true;
            }

            log.LogInformation("[H2] market_today.json has participant data but PCR/VIX are missing ({Date}). Skipping ingestion; will retry PCR/VIX fetch.", targetDate.ToString("yyyy-MM-dd"));
            return true;
        }
        else
        {
            log.LogInformation("[H2] market_today.json is for a different date than targetDate ({TargetDate}). Proceeding with full run.", targetDate.ToString("yyyy-MM-dd"));
        }
    }
    catch (Exception ex)
    {
        log.LogWarning("[H2] Could not read existing market_today.json: {Msg}. Proceeding.", ex.Message);
    }
    return false;
}

static async Task<(object ingestResult, object runResult)> IngestAndRunPipelineAsync(
    ServiceProvider sp, DateTime from, DateTime to, ILogger log)
{
    using var scope = sp.CreateScope();
    var ingestion = scope.ServiceProvider.GetRequiredService<CsvIngestionService>();
    var pipeline = scope.ServiceProvider.GetRequiredService<DailyPipelineService>();

    log.LogInformation("Ingesting range {From} → {To} (IST)", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
    var ingestResult = await ingestion.IngestParticipantOiRangeAsync(from, to, CancellationToken.None);

    log.LogInformation("Running pipeline range {From} → {To} (IST)", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
    var runResult = await pipeline.RunRangeAsync(from, to, CancellationToken.None);

    return (ingestResult, runResult);
}

static async Task<(MarketTodayDto marketToday, List<MarketHistoryPointDto> history)> ExportOrReuseParticipantDataAsync(
    ServiceProvider sp, bool participantDataAlreadyExported, string publicDataDir, string publicMarketTodayPath, DateTime targetDate, ILogger log)
{
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
            return (null!, null!);
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

        Directory.CreateDirectory(publicDataDir);
        await File.WriteAllTextAsync(publicMarketTodayPath,
            JsonSerializer.Serialize(marketToday, jsonOptsIntermediate));
        await File.WriteAllTextAsync(Path.Combine(publicDataDir, "market_history_30.json"),
            JsonSerializer.Serialize(history, jsonOptsIntermediate));

        log.LogInformation("[H4] Participant data exported (PCR/VIX pending). public: {PubDir}", publicDataDir);
    }
    else
    {
        log.LogInformation("[H4] Re-using previously exported participant data; will update PCR/VIX only.");
        var existingJson = await File.ReadAllTextAsync(publicMarketTodayPath);
        marketToday = JsonSerializer.Deserialize<MarketTodayDto>(existingJson)
            ?? throw new InvalidOperationException($"Failed to deserialize existing market_today.json from {publicMarketTodayPath}.");

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

    return (marketToday, history);
}

static async Task FetchAndPatchPcrVixAsync(
    ServiceProvider sp,
    MarketTodayDto marketToday,
    List<MarketHistoryPointDto> history,
    string publicDataDir,
    string publicMarketTodayPath,
    ILogger log)
{
    if (!TryParseMarketTodayDate(marketToday, log, out var pcrVixDate))
        return;

    using var scope = sp.CreateScope();
    var opBhavSvc = scope.ServiceProvider.GetRequiredService<OpBhavCopyService>();
    var prPcrSvc = scope.ServiceProvider.GetRequiredService<PrPcrService>();
    var bhavSvc = scope.ServiceProvider.GetRequiredService<FoBhavCopyService>();
    var vixSvc = scope.ServiceProvider.GetRequiredService<VixFetchService>();

    var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

    log.LogInformation("[H3] Fetching PCR/VIX for {Date}", pcrVixDate.ToString("yyyy-MM-dd"));

    var pcrResult = await FetchPcrFromSourcesAsync(opBhavSvc, prPcrSvc, bhavSvc, pcrVixDate, log);
    double? pcr = pcrResult.pcr;
    double? pcrVolume = pcrResult.pcrVolume;
    double? bankniftyPcr = pcrResult.bankniftyPcr;
    double? bankniftyPcrVolume = pcrResult.bankniftyPcrVolume;

    double? vix = await FetchVixFromServiceAsync(vixSvc, pcrVixDate);

    bool anyData = pcr.HasValue || vix.HasValue || pcrVolume.HasValue
                   || bankniftyPcr.HasValue || bankniftyPcrVolume.HasValue;
    if (anyData)
    {
        marketToday = PatchMarketToday(marketToday, pcr, vix, pcrVolume, bankniftyPcr, bankniftyPcrVolume);
        await SavePatchedJsonAsync(marketToday, history, publicDataDir, publicMarketTodayPath, jsonOpts);
        log.LogInformation(
            "[H3] JSON patched: PCR={Pcr}, PCRVol={PcrVol}, BNF={BnfPcr}, VIX={Vix}. public: {PubDir}",
            pcr, pcrVolume, bankniftyPcr, vix, publicDataDir);
    }
    else
    {
        log.LogWarning("[H3] No PCR/VIX data available for {Date}. Dashboard shows whatever is available.", pcrVixDate.ToString("yyyy-MM-dd"));
    }
}

static bool TryParseMarketTodayDate(MarketTodayDto marketToday, ILogger log, out DateTime pcrVixDate)
{
    if (!DateTime.TryParseExact(marketToday.date, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out pcrVixDate))
    {
        log.LogError("[H3] Cannot parse marketToday.date '{Date}' as yyyy-MM-dd. Skipping PCR/VIX fetch.", marketToday.date);
        return false;
    }
    return true;
}

static async Task<(double? pcr, double? pcrVolume, double? bankniftyPcr, double? bankniftyPcrVolume)> FetchPcrFromSourcesAsync(
    OpBhavCopyService opBhavSvc,
    PrPcrService prPcrSvc,
    FoBhavCopyService bhavSvc,
    DateTime pcrVixDate,
    ILogger log)
{
    // Try op-bhavcopy first
    var opResult = await opBhavSvc.FetchPcrAsync(pcrVixDate, CancellationToken.None);
    if (opResult is not null)
    {
        log.LogInformation(
            "[H3] PCR sourced from op-bhavcopy: OI={Pcr}, Vol={PcrVol}, BNF OI={BnfPcr}, BNF Vol={BnfVol}",
            opResult.NiftyPcrOi, opResult.NiftyPcrVolume, opResult.BankniftyPcrOi, opResult.BankniftyPcrVolume);

        return (opResult.NiftyPcrOi, opResult.NiftyPcrVolume, opResult.BankniftyPcrOi, opResult.BankniftyPcrVolume);
    }

    // Try PR file next
    var prResult = await prPcrSvc.FetchPcrAsync(pcrVixDate, CancellationToken.None);
    if (prResult is not null)
    {
        log.LogInformation(
            "[H3] PCR sourced from PR file: OI={Pcr}, Vol={PrcVol}, BNF OI={BnfPcr}, BNF Vol={BnfVol}",
            prResult.NiftyPcrOi, prResult.NiftyPcrVolume, prResult.BankniftyPcrOi, prResult.BankniftyPcrVolume);

        return (prResult.NiftyPcrOi, prResult.NiftyPcrVolume, prResult.BankniftyPcrOi, prResult.BankniftyPcrVolume);
    }

    // Fallback to FO bhavcopy (only NIFTY OI PCR)
    var bhavPcr = await bhavSvc.FetchPcrAsync(pcrVixDate, CancellationToken.None);
    if (bhavPcr.HasValue)
    {
        log.LogInformation("[H3] NIFTY OI PCR sourced from FO bhavcopy fallback: {Pcr}", bhavPcr);
        return (bhavPcr, null, null, null);
    }

    return (null, null, null, null);
}

static async Task<double?> FetchVixFromServiceAsync(VixFetchService vixSvc, DateTime pcrVixDate)
{
    return await vixSvc.FetchVixAsync(pcrVixDate, CancellationToken.None);
}

static async Task SavePatchedJsonAsync(
    MarketTodayDto marketToday,
    List<MarketHistoryPointDto> history,
    string publicDataDir,
    string publicMarketTodayPath,
    JsonSerializerOptions jsonOpts)
{
    Directory.CreateDirectory(publicDataDir);
    await File.WriteAllTextAsync(publicMarketTodayPath,
        JsonSerializer.Serialize(marketToday, jsonOpts));
    await File.WriteAllTextAsync(Path.Combine(publicDataDir, "market_history_30.json"),
        JsonSerializer.Serialize(history, jsonOpts));
}

static MarketTodayDto PatchMarketToday(
    MarketTodayDto marketToday,
    double? pcr,
    double? vix,
    double? pcrVolume,
    double? bankniftyPcr,
    double? bankniftyPcrVolume)
{
    return marketToday with
    {
        pcr = pcr,
        vix = vix,
        pcr_volume = pcrVolume,
        banknifty_pcr = bankniftyPcr,
        banknifty_pcr_volume = bankniftyPcrVolume,
    };
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