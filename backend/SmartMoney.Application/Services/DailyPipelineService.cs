using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartMoney.Domain.Entities;
using SmartMoney.Domain.Enums;
using SmartMoney.Infrastructure.Persistence;

namespace SmartMoney.Application.Services;

public sealed class DailyPipelineService(SmartMoneyDbContext db, ILogger<DailyPipelineService> log)
{
    private const int ShortWindow = 5;
    private const int LongWindow = 20;
    private const double ShockThreshold = 1.5;

    public async Task<bool> IsMarketBiasPresentAsync(DateTime date, CancellationToken ct)
    {
        date = date.Date;
        return await db.MarketBiases.AnyAsync(x => x.Date == date, ct);
    }

    public async Task<PipelineRunResult> RunAsync(DateTime date, CancellationToken ct)
    {
        date = date.Date;

        // 1) Load last 20 days raw including today
        var raw = await db.ParticipantRawData
            .AsNoTracking()
            .Where(x => x.Date <= date)
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        if (raw.Count == 0)
            return new PipelineRunResult(date, false, "No raw data found.");

        // 2) Group by participant
        var grouped = raw
            .GroupBy(x => x.Participant)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 3) Compute per participant metrics for 'date'
        var metricsForDate = new List<ParticipantMetric>();
        var participantBias = new Dictionary<ParticipantType, double>();

        // First compute divergence and shock score (regime depends on all participants)
        double shockScore = 0;

        // store z values to reuse
        var zStore = new Dictionary<ParticipantType, ZPack>();

        foreach (var (participant, series) in grouped)
        {
            var todayRow = series.Count > 0 && series[series.Count - 1].Date == date
                ? series[series.Count - 1]
                : null;
            if (todayRow is null) continue;

            // Use only last LongWindow points up to date
            var window = series.Where(x => x.Date <= date).TakeLast(LongWindow).ToList();
            if (window.Count < LongWindow)
            {
                log.LogInformation("Not enough data for {Participant}: {Count}/{Need}", participant, window.Count, LongWindow);
                continue;
            }

            // Build indicator series from raw
            var futures = window.Select(x => x.FuturesChange).ToList();
            var puts = window.Select(x => x.PutOiChange).ToList();
            var calls = window.Select(x => x.CallOiChange).ToList();

            var fz = ComputeZShortLong(futures);
            var pz = ComputeZShortLong(puts);
            var cz = ComputeZShortLong(calls);

            var z = new ZPack(fz.Short, fz.Long, pz.Short, pz.Long, cz.Short, cz.Long);
            zStore[participant] = z;

            // divergence = sum of abs(short-long) across signals
            var divergence =
                Math.Abs(fz.Short - fz.Long) +
                Math.Abs(pz.Short - pz.Long) +
                Math.Abs(cz.Short - cz.Long);

            shockScore += ParticipantWeight(participant) * divergence;
        }

        var regime = shockScore > ShockThreshold ? Regime.Shock : Regime.Normal;

        // 4) Now compute bias and persist metrics rows (idempotent overwrite for date)
        foreach (var (participant, z) in zStore)
        {
            var fEff = Blend(z.FuturesShort, z.FuturesLong, regime);
            var pEff = Blend(z.PutShort, z.PutLong, regime);
            var cEff = Blend(z.CallShort, z.CallLong, regime);

            // For V1: futures = directional, put writing = bullish, call writing = bearish
            // We subtract call component.
            var bias =
                0.5 * fEff +
                0.3 * pEff -
                0.2 * cEff;

            participantBias[participant] = bias;

            metricsForDate.Add(new ParticipantMetric
            {
                Id = Guid.NewGuid(),
                Date = date,
                Participant = participant,

                FuturesZShort = z.FuturesShort,
                FuturesZLong = z.FuturesLong,
                PutZShort = z.PutShort,
                PutZLong = z.PutLong,
                CallZShort = z.CallShort,
                CallZLong = z.CallLong,

                ParticipantBias = bias
            });
        }

        if (metricsForDate.Count == 0)
            return new PipelineRunResult(date, false, "No metrics produced (likely insufficient history).");

        // 5) Composite market bias + tanh scaling
        var marketRaw = participantBias.Sum(kvp => ParticipantWeight(kvp.Key) * kvp.Value);

        // scale [-100..100] feel; tanh keeps it bounded
        var finalScore = Math.Tanh(marketRaw / 2.0) * 100.0;

        // 6) Persist: delete existing date rows then insert (simple idempotency)
        var existingMetrics = await db.ParticipantMetrics.Where(x => x.Date == date).ToListAsync(ct);
        if (existingMetrics.Count > 0) db.ParticipantMetrics.RemoveRange(existingMetrics);

        var existingMarket = await db.MarketBiases.Where(x => x.Date == date).ToListAsync(ct);
        if (existingMarket.Count > 0) db.MarketBiases.RemoveRange(existingMarket);

        await db.ParticipantMetrics.AddRangeAsync(metricsForDate, ct);

        await db.MarketBiases.AddAsync(new MarketBias
        {
            Id = Guid.NewGuid(),
            Date = date,
            RawBias = marketRaw,
            FinalScore = finalScore,
            Regime = regime,
            ShockScore = shockScore
        }, ct);

        await db.SaveChangesAsync(ct);

        return new PipelineRunResult(date, true, $"OK. Metrics={metricsForDate.Count}, Regime={regime}, Final={finalScore:F1}");
    }

    public async Task<object> RunRangeAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        from = from.Date;
        to = to.Date;

        var ok = new List<object>();
        var failed = new List<object>();
        var skipped = 0;

        for (var d = from; d <= to; d = d.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();

            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                skipped++;
                continue;
            }

            try
            {
                var r = await RunAsync(d, ct);
                if (!r.Success)
                {
                    failed.Add(new { date = d.ToString("yyyy-MM-dd"), reason = r.Note });
                }
                else
                {
                    ok.Add(new { date = d.ToString("yyyy-MM-dd"), note = r.Note });
                }
            }
            catch (Exception ex)
            {
                failed.Add(new { date = d.ToString("yyyy-MM-dd"), reason = ex.Message });
            }
        }

        return new
        {
            from = from.ToString("yyyy-MM-dd"),
            to = to.ToString("yyyy-MM-dd"),
            skippedWeekends = skipped,
            successDays = ok.Count,
            failedDays = failed.Count,
            ok,
            failed
        };
    }

    // ---- math helpers ----

    private static (double Short, double Long) ComputeZShortLong(List<double> values)
    {
        var shortVals = values.TakeLast(ShortWindow).ToList();
        var longVals = values.TakeLast(LongWindow).ToList();
        return (Z(shortVals), Z(longVals));
    }

    private static double Z(List<double> values)
    {
        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        var std = Math.Sqrt(variance);
        const double epsilon = 1e-8;
        if (Math.Abs(std) < epsilon) return 0;
        return (values.Count > 0 ? values[^1] : 0 - mean) / std;
    }

    private static double Blend(double shortZ, double longZ, Regime regime)
        => regime == Regime.Shock
            ? 0.7 * shortZ + 0.3 * longZ
            : 0.7 * longZ + 0.3 * shortZ;

    private static double ParticipantWeight(ParticipantType p)
        => p switch
        {
            ParticipantType.FII => 0.4,
            ParticipantType.Pro => 0.3,
            ParticipantType.DII => 0.2,
            ParticipantType.Retail => 0.1,
            _ => 0.0
        };

    private sealed record ZPack(
        double FuturesShort, double FuturesLong,
        double PutShort, double PutLong,
        double CallShort, double CallLong
    );
}

public sealed record PipelineRunResult(DateTime Date, bool Success, string Note);