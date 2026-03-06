using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartMoney.Application.Options;
using SmartMoney.Application.Services;
using SmartMoney.Infrastructure.Persistence;
using SmartMoney.Job.Export;
using System.Text.Json;

namespace SmartMoney.Job
{
    internal static class Program
    {
        private static async Task Main()
        {
            await MainAsync();
        }

        private static DateTimeOffset ToIst(DateTimeOffset utc)
        {
            return utc.ToOffset(TimeSpan.FromHours(5.5));
        }

        private static bool TryParseDateExact(string value, out DateTime result) =>
            DateTime.TryParseExact(value, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out result);

        /// <summary>Carries PCR fetch results without requiring ref/out parameters on async methods.</summary>
        sealed record PcrState(double? Pcr, double? PcrVolume, double? BankniftyPcr, double? BankniftyPcrVolume);

        // ---- IST helpers ----

        private static async Task MainAsync()
        {
            var istNow = ToIst(DateTimeOffset.UtcNow);
            var today = istNow.Date;

            const int NsePublishHourIst = 20;
            bool isBeforeNsePublish = istNow.Hour < NsePublishHourIst;
            var targetDate = GetTargetDate(today, isBeforeNsePublish);

            Console.WriteLine($"[H0] IST now: {istNow:HH:mm}. isBeforeNsePublish={isBeforeNsePublish}. targetDate={targetDate:yyyy-MM-dd}");

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
            var jobOpts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NseJobOptions>>().Value;

            log.LogInformation("[H0] IST now: {IstNow:HH:mm}. isBeforeNsePublish={Flag}. targetDate={TargetDate}",
                istNow, isBeforeNsePublish, targetDate.ToString("yyyy-MM-dd"));

            await MigrateDbAsync(sp);

            var repoRoot = FindRepoRoot();
            var publicDataDir = Path.Combine(repoRoot, "frontend", "public", "data");
            var publicMarketTodayPath = Path.Combine(publicDataDir, "market_today.json");

            bool participantDataAlreadyExported = await CheckExistingExportAsync(publicMarketTodayPath, targetDate, log);

            if (!participantDataAlreadyExported)
            {
                (_, _) = await IngestAndRunPipelineAsync(sp, from, to, log);
            }

            var (marketToday, history) = await ExportOrReuseParticipantDataAsync(
                sp, participantDataAlreadyExported, publicDataDir, publicMarketTodayPath, targetDate, log);

            await FetchAndPatchPcrVixAsync(
                sp, marketToday, history, publicDataDir, publicMarketTodayPath, jobOpts, log);

            log.LogInformation("DONE");
        }

        private static DateTime GetTargetDate(DateTime today, bool isBeforeNsePublish)
        {
            var targetDate = isBeforeNsePublish ? today.AddDays(-1) : today;
            while (IsWeekend(targetDate))
                targetDate = targetDate.AddDays(-1);
            return targetDate;
        }

        private static bool IsWeekend(DateTime date) =>
            date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        private static ServiceCollection ConfigureServices()
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

            // NseOptions: canonical defaults live in the class itself (NseOptions.cs).
            // The job must NOT override NSE endpoint URLs — per CONTRIBUTING.md.
            services.AddOptions<NseOptions>();

            // NseJobOptions: job scheduling and retry flags only.
            services.Configure<NseJobOptions>(o =>
            {
                if (bool.TryParse(Environment.GetEnvironmentVariable("JOB_ENABLED"), out var b)) o.Enabled = b;
                o.StartAtIst = Environment.GetEnvironmentVariable("JOB_START_AT_IST") ?? o.StartAtIst;
                o.EndAtIst = Environment.GetEnvironmentVariable("JOB_END_AT_IST") ?? o.EndAtIst;
                if (int.TryParse(Environment.GetEnvironmentVariable("PCR_VIX_MAX_RETRIES"), out var mr)) o.PcrVixMaxRetries = mr;
                if (int.TryParse(Environment.GetEnvironmentVariable("PCR_VIX_RETRY_MINUTES"), out var pr)) o.PcrVixRetryMinutes = pr;
            });

            services.AddHttpClient<BhavCopyService>();
            services.AddHttpClient<OpBhavCopyService>();
            services.AddHttpClient<PrPcrService>();
            services.AddHttpClient<FoBhavCopyService>();
            services.AddHttpClient<VixFetchService>();
            services.AddHttpClient<CsvIngestionService>();
            services.AddScoped<DailyPipelineService>();

            return services;
        }

        private static async Task MigrateDbAsync(ServiceProvider sp)
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartMoneyDbContext>();
            await db.Database.MigrateAsync();
        }

        private static async Task<bool> CheckExistingExportAsync(string publicMarketTodayPath, DateTime targetDate, ILogger log)
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
                        log.LogInformation("[H2] market_today.json already contains complete data with PCR and VIX ({Date}). Skipping job.",
                            targetDate.ToString("yyyy-MM-dd"));
                        return true;
                    }

                    log.LogInformation("[H2] market_today.json has participant data but PCR/VIX are missing ({Date}). Skipping ingestion; will retry PCR/VIX fetch.",
                        targetDate.ToString("yyyy-MM-dd"));
                    return true;
                }

                log.LogInformation("[H2] market_today.json is for a different date than targetDate ({TargetDate}). Proceeding with full run.",
                    targetDate.ToString("yyyy-MM-dd"));
            }
            catch (Exception ex)
            {
                log.LogWarning("[H2] Could not read existing market_today.json: {Msg}. Proceeding.", ex.Message);
            }
            return false;
        }

        private static async Task<(object ingestResult, object runResult)> IngestAndRunPipelineAsync(
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

        private static async Task<(MarketTodayDto marketToday, List<MarketHistoryPointDto> history)> ExportOrReuseParticipantDataAsync(
            ServiceProvider sp, bool participantDataAlreadyExported, string publicDataDir,
            string publicMarketTodayPath, DateTime targetDate, ILogger log)
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
                    ?? throw new InvalidOperationException(
                        $"Failed to deserialize existing market_today.json from {publicMarketTodayPath}.");

                var publicHistPath = Path.Combine(publicDataDir, "market_history_30.json");
                if (File.Exists(publicHistPath))
                {
                    var histJson = await File.ReadAllTextAsync(publicHistPath);
                    history = JsonSerializer.Deserialize<List<MarketHistoryPointDto>>(histJson) ?? [];
                }
                else
                {
                    history = [];
                }
            }

            return (marketToday, history);
        }

        private static async Task FetchAndPatchPcrVixAsync(
            ServiceProvider sp,
            MarketTodayDto marketToday,
            List<MarketHistoryPointDto> history,
            string publicDataDir,
            string publicMarketTodayPath,
            NseJobOptions jobOpts,
            ILogger log)
        {
            if (!TryParseDateExact(marketToday.date, out var pcrVixDate))
            {
                log.LogError("[H3] Cannot parse marketToday.date '{Date}' as yyyy-MM-dd. Skipping PCR/VIX fetch.",
                    marketToday.date);
                return;
            }

            // Do not attempt PCR/VIX fetch before StartAtIst (default 20:30 IST).
            var istNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5));
            if (TimeSpan.TryParse(jobOpts.StartAtIst, System.Globalization.CultureInfo.InvariantCulture, out var startAt)
                && istNow.TimeOfDay < startAt)
            {
                log.LogInformation(
                    "[H3] Skipping PCR/VIX fetch — IST now ({IstNow:HH:mm}) is before StartAtIst ({StartAtIst}). Will run after {StartAtHhmm}.",
                    istNow, jobOpts.StartAtIst, jobOpts.StartAtIst);
                return;
            }

            int maxRetries = jobOpts.PcrVixMaxRetries;
            int retryDelayMs = jobOpts.PcrVixRetryMinutes * 60 * 1000;

            using var scope = sp.CreateScope();
            var bhavSvc = scope.ServiceProvider.GetRequiredService<BhavCopyService>();
            var opBhavSvc = scope.ServiceProvider.GetRequiredService<OpBhavCopyService>();
            var prPcrSvc = scope.ServiceProvider.GetRequiredService<PrPcrService>();
            var foBhavSvc = scope.ServiceProvider.GetRequiredService<FoBhavCopyService>();
            var vixSvc = scope.ServiceProvider.GetRequiredService<VixFetchService>();
            var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

            // Seed from the existing JSON so we never overwrite a successfully-fetched value
            // with null just because a subsequent run cannot reach NSE (e.g. morning 403/404).
            double? pcr = marketToday.pcr,
                    pcrVolume = marketToday.pcr_volume,
                    bankniftyPcr = marketToday.banknifty_pcr,
                    bankniftyPcrVolume = marketToday.banknifty_pcr_volume,
                    vix = marketToday.vix;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                log.LogInformation("[H3] Fetching PCR/VIX for {Date}, attempt {Attempt}/{Max}",
                    pcrVixDate.ToString("yyyy-MM-dd"), attempt, maxRetries);

                if (pcr is null)
                {
                    var pcrState = new PcrState(pcr, pcrVolume, bankniftyPcr, bankniftyPcrVolume);
                    pcrState = await FetchPcrIfMissingAsync(pcrState, bhavSvc, opBhavSvc, prPcrSvc, foBhavSvc, pcrVixDate, log);
                    (pcr, pcrVolume, bankniftyPcr, bankniftyPcrVolume) =
                        (pcrState.Pcr, pcrState.PcrVolume, pcrState.BankniftyPcr, pcrState.BankniftyPcrVolume);
                }

                if (vix is null)
                    vix = await vixSvc.FetchVixAsync(pcrVixDate, CancellationToken.None);

                bool anyData = pcr.HasValue || vix.HasValue || pcrVolume.HasValue
                               || bankniftyPcr.HasValue || bankniftyPcrVolume.HasValue;
                if (anyData)
                {
                    marketToday = PatchMarketToday(marketToday, pcr, vix, pcrVolume, bankniftyPcr, bankniftyPcrVolume);
                    await SavePatchedJsonAsync(marketToday, history, publicDataDir, publicMarketTodayPath, jsonOpts);
                    log.LogInformation(
                        "[H3] JSON patched after attempt {Attempt}: PCR={Pcr}, PCRVol={PcrVol}, BNF={BnfPcr}, VIX={Vix}. public: {PubDir}",
                        attempt, pcr, pcrVolume, bankniftyPcr, vix, publicDataDir);
                }

                if (pcr.HasValue && vix.HasValue)
                {
                    log.LogInformation("[H3] All data complete on attempt {Attempt}. PCR={Pcr}, VIX={Vix}. Done.",
                        attempt, pcr, vix);
                    break;
                }

                if (attempt < maxRetries)
                {
                    log.LogWarning(
                        "[H3] PCR or VIX still missing after attempt {Attempt}/{Max}. Waiting {DelayMin} min before next attempt.",
                        attempt, maxRetries, retryDelayMs / 60000);
                    await Task.Delay(retryDelayMs, CancellationToken.None);
                }
                else
                {
                    log.LogWarning(
                        "[H3] Stopping after {Max} attempt(s). Final: PCR={Pcr}, VIX={Vix}. Dashboard shows whatever is available.",
                        maxRetries, pcr, vix);
                }
            }
        }

        private static async Task<PcrState> FetchPcrIfMissingAsync(
            PcrState current,
            BhavCopyService bhavSvc,
            OpBhavCopyService opBhavSvc,
            PrPcrService prPcrSvc,
            FoBhavCopyService foBhavSvc,
            DateTime date,
            ILogger log)
        {
            if (current.Pcr is not null)
                return current;

            var (pcr, vol, bnfPcr, bnfVol) = await FetchPcrFromSourcesAsync(
                bhavSvc, opBhavSvc, prPcrSvc, foBhavSvc, date, log);

            return new PcrState(pcr, vol, bnfPcr, bnfVol);
        }

        private static async Task<(double? pcr, double? pcrVolume, double? bankniftyPcr, double? bankniftyPcrVolume)> FetchPcrFromSourcesAsync(
            BhavCopyService bhavSvc,
            OpBhavCopyService opBhavSvc,
            PrPcrService prPcrSvc,
            FoBhavCopyService foBhavSvc,
            DateTime pcrVixDate,
            ILogger log)
        {
            // Source 1: UDiFF bhavcopy (primary — new NSE format)
            var bhavResult = await bhavSvc.FetchPcrAsync(pcrVixDate, CancellationToken.None);
            if (bhavResult is not null)
            {
                log.LogInformation(
                    "[H3] PCR sourced from UDiFF bhavcopy: OI={Pcr}, Vol={PcrVol}, BNF OI={BnfPcr}, BNF Vol={BnfVol}",
                    bhavResult.NiftyPcrOi, bhavResult.NiftyPcrVolume, bhavResult.BankniftyPcrOi, bhavResult.BankniftyPcrVolume);
                return (bhavResult.NiftyPcrOi, bhavResult.NiftyPcrVolume, bhavResult.BankniftyPcrOi, bhavResult.BankniftyPcrVolume);
            }

            // Source 2: op-bhavcopy ZIP (fo{DDMMYYYY}.zip → op{DDMMYY}.csv)
            var opResult = await opBhavSvc.FetchPcrAsync(pcrVixDate, CancellationToken.None);
            if (opResult is not null)
            {
                log.LogInformation(
                    "[H3] PCR sourced from op-bhavcopy: OI={Pcr}, Vol={PcrVol}, BNF OI={BnfPcr}, BNF Vol={BnfVol}",
                    opResult.NiftyPcrOi, opResult.NiftyPcrVolume, opResult.BankniftyPcrOi, opResult.BankniftyPcrVolume);
                return (opResult.NiftyPcrOi, opResult.NiftyPcrVolume, opResult.BankniftyPcrOi, opResult.BankniftyPcrVolume);
            }

            // Source 3: PR ZIP (PR{DDMMYY}.zip → pr{DDMMYYYY}.csv)
            var prResult = await prPcrSvc.FetchPcrAsync(pcrVixDate, CancellationToken.None);
            if (prResult is not null)
            {
                log.LogInformation(
                    "[H3] PCR sourced from PR file: OI={Pcr}, Vol={PcrVol}, BNF OI={BnfPcr}, BNF Vol={BnfVol}",
                    prResult.NiftyPcrOi, prResult.NiftyPcrVolume, prResult.BankniftyPcrOi, prResult.BankniftyPcrVolume);
                return (prResult.NiftyPcrOi, prResult.NiftyPcrVolume, prResult.BankniftyPcrOi, prResult.BankniftyPcrVolume);
            }

            // Source 4: FO bhavcopy CSV fallback (NIFTY OI only)
            var foPcr = await foBhavSvc.FetchPcrAsync(pcrVixDate, CancellationToken.None);
            if (foPcr.HasValue)
            {
                log.LogInformation("[H3] NIFTY OI PCR sourced from FO bhavcopy fallback: {Pcr}", foPcr);
                return (foPcr, null, null, null);
            }

            log.LogWarning("[H3] All PCR sources returned no data for {Date}.", pcrVixDate.ToString("yyyy-MM-dd"));
            return (null, null, null, null);
        }

        private static async Task SavePatchedJsonAsync(
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

        private static MarketTodayDto PatchMarketToday(
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

        private static string FindRepoRoot()
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
    }
}