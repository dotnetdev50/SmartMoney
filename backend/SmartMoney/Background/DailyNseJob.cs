using Microsoft.Extensions.Options;
using SmartMoney.Application.Options;
using SmartMoney.Application.Services;

namespace SmartMoney.Api.Background;

public sealed class DailyNseJob(
    IServiceScopeFactory scopeFactory,
    ILogger<DailyNseJob> log,
    IOptions<NseJobOptions> options) : BackgroundService
{
    private readonly NseJobOptions _opt = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled)
        {
            log.LogInformation("DailyNseJob is disabled.");
            return;
        }

        log.LogInformation("DailyNseJob started. StartAtIst={Start} EndAtIst={End} RetryMinutes={Retry}",
            _opt.StartAtIst, _opt.EndAtIst, _opt.RetryMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var istNow = ToIst(DateTimeOffset.UtcNow);
                var nextRun = NextStartTimeIst(istNow, ParseTime(_opt.StartAtIst));
                var delay = nextRun - istNow;

                log.LogInformation("Next job window starts at {NextRunIst} (in {Delay})",
                    nextRun.ToString("yyyy-MM-dd HH:mm:ss zzz"), delay);

                await Task.Delay(delay, stoppingToken);

                await RunWindowAsync(stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                log.LogError(ex, "DailyNseJob loop error.");
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
        }
    }

    private async Task RunWindowAsync(CancellationToken ct)
    {
        var istNow = ToIst(DateTimeOffset.UtcNow);
        var date = GetTargetTradingDateIst(istNow.Date);

        var weekend = istNow.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        // Weekend behavior:
        // - Do not download from NSE.
        // - If last trading day raw exists but market_bias not computed yet, compute once.
        if (weekend)
        {
            using var scope = scopeFactory.CreateScope();
            var ingestion = scope.ServiceProvider.GetRequiredService<CsvIngestionService>();
            var pipeline = scope.ServiceProvider.GetRequiredService<DailyPipelineService>();

            if (await pipeline.IsMarketBiasPresentAsync(date, ct))
            {
                log.LogInformation("Weekend: market_bias already present for {Date}.", date.ToString("yyyy-MM-dd"));
                return;
            }

            var rawPresentWeekend = await ingestion.IsRawDataPresentAsync(date, _opt.ExpectedParticipantRowsPerDay, ct);
            if (!rawPresentWeekend)
            {
                log.LogInformation("Weekend: raw data not present for {Date}. Skipping.", date.ToString("yyyy-MM-dd"));
                return;
            }

            var runWeekend = await pipeline.RunAsync(date, ct);
            log.LogInformation("Weekend pipeline run: Success={Success} Note={Note}", runWeekend.Success, runWeekend.Note);
            return;
        }

        // Weekday behavior: retry window (8pm/9pm NSE availability)
        var startAt = ParseTime(_opt.StartAtIst);
        var endAt = ParseTime(_opt.EndAtIst);

        var windowStart = new DateTimeOffset(date.Year, date.Month, date.Day, startAt.Hours, startAt.Minutes, 0, istNow.Offset);
        var windowEnd = new DateTimeOffset(date.Year, date.Month, date.Day, endAt.Hours, endAt.Minutes, 0, istNow.Offset);

        var now = ToIst(DateTimeOffset.UtcNow);
        if (now < windowStart) now = windowStart;

        while (now <= windowEnd && !ct.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var ingestion = scope.ServiceProvider.GetRequiredService<CsvIngestionService>();
            var pipeline = scope.ServiceProvider.GetRequiredService<DailyPipelineService>();

            // If already computed, stop.
            if (await pipeline.IsMarketBiasPresentAsync(date, ct))
            {
                log.LogInformation("market_bias already present for {Date}. No action.", date.ToString("yyyy-MM-dd"));
                return;
            }

            // If raw exists, skip download.
            var rawPresent = await ingestion.IsRawDataPresentAsync(date, _opt.ExpectedParticipantRowsPerDay, ct);

            if (!rawPresent)
            {
                try
                {
                    log.LogInformation("Attempt ingest for {Date} IST at {Time}",
                        date.ToString("yyyy-MM-dd"), now.ToString("HH:mm"));

                    var ingest = await ingestion.IngestParticipantOiAsync(date, ct);

                    log.LogInformation("Ingest done: Inserted={Inserted} Updated={Updated} Note={Note}",
                        ingest.Inserted, ingest.Updated, ingest.Note);
                }
                catch (FileNotFoundException ex)
                {
                    // Holiday / no trading day -> do not retry all night
                    log.LogWarning(ex, "NSE file not found for {Date}. Treating as holiday/non-trading day.",
                        date.ToString("yyyy-MM-dd"));
                    return;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Ingest attempt failed. Will retry if window allows.");
                }

                rawPresent = await ingestion.IsRawDataPresentAsync(date, _opt.ExpectedParticipantRowsPerDay, ct);
            }
            else
            {
                log.LogInformation("Raw data already present for {Date}. Skipping download.", date.ToString("yyyy-MM-dd"));
            }

            if (rawPresent)
            {
                var run = await pipeline.RunAsync(date, ct);
                log.LogInformation("Pipeline run: Success={Success} Note={Note}", run.Success, run.Note);

                if (run.Success && await pipeline.IsMarketBiasPresentAsync(date, ct))
                    return;

                // If it’s still warming up, no reason to retry tonight
                if (!run.Success && run.Note.Contains("insufficient history", StringComparison.OrdinalIgnoreCase))
                    return;
            }

            await Task.Delay(TimeSpan.FromMinutes(_opt.RetryMinutes), ct);
            now = ToIst(DateTimeOffset.UtcNow);
        }

        log.LogInformation("Job window ended for {Date} without completing.", date.ToString("yyyy-MM-dd"));
    }

    private static DateTimeOffset ToIst(DateTimeOffset utc)
        => utc.ToOffset(TimeSpan.FromHours(5.5));

    private static TimeSpan ParseTime(string hhmm)
    {
        // "20:05"
        var parts = hhmm.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var h = int.Parse(parts[0]);
        var m = int.Parse(parts[1]);
        return new TimeSpan(h, m, 0);
    }

    private static DateTimeOffset NextStartTimeIst(DateTimeOffset istNow, TimeSpan startAtIst)
    {
        var todayStart = new DateTimeOffset(istNow.Year, istNow.Month, istNow.Day,
            startAtIst.Hours, startAtIst.Minutes, 0, istNow.Offset);

        return istNow < todayStart ? todayStart : todayStart.AddDays(1);
    }

    private static DateTime GetTargetTradingDateIst(DateTime istDate)
    {
        // If weekend, use last Friday
        if (istDate.DayOfWeek == DayOfWeek.Saturday) return istDate.AddDays(-1); // Friday
        if (istDate.DayOfWeek == DayOfWeek.Sunday) return istDate.AddDays(-2);   // Friday
        return istDate;
    }
}