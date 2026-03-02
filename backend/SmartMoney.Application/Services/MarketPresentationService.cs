using SmartMoney.Domain.Entities;
using SmartMoney.Domain.Enums;

namespace SmartMoney.Application.Services;

public sealed class MarketPresentationService
{
    public (string Label, string Strength) DescribeFinalScore(double finalScore)
    {
        var abs = Math.Abs(finalScore);

        if (abs < 15) return ("Neutral", "Weak");

        var dir = finalScore >= 0 ? "Bullish" : "Bearish";

        if (abs < 35) return (dir, "Mild");
        if (abs < 60) return (dir, "Moderate");
        return (dir, "Strong");
    }

    public string DescribeParticipant(double participantBias)
    {
        var abs = Math.Abs(participantBias);

        if (abs < 0.5) return "Neutral";
        var dir = participantBias >= 0 ? "Bullish" : "Bearish";

        if (abs < 1.2) return $"Mild {dir}";
        if (abs < 2.5) return $"{dir}";
        return $"Strong {dir}";
    }

    public string BuildExplanation(
        DateTime date,
        Regime regime,
        IReadOnlyList<ParticipantMetric> metrics,
        ParticipantMetric? fii)
    {
        // Simple, deterministic, rule-based.

        var lines = new List<string>();

        // Regime line
        lines.Add(regime == Regime.Shock
            ? "Regime is SHOCK: short-term signals are diverging strongly from the 20-day baseline."
            : "Regime is NORMAL: signals are broadly aligned with the 20-day baseline.");

        // Participant driver: pick max abs bias
        var driver = metrics
            .OrderByDescending(m => Math.Abs(m.ParticipantBias))
            .FirstOrDefault();

        if (driver is not null)
        {
            var dir = driver.ParticipantBias >= 0 ? "bullish" : "bearish";
            lines.Add($"{driver.Participant} is the strongest driver today ({DescribeParticipant(driver.ParticipantBias)}).");
            lines.Add(ExplainSignals(driver, dir));
        }

        // Add one FII line if present (users care)
        if (fii is not null)
        {
            var dir = fii.ParticipantBias >= 0 ? "bullish" : "bearish";
            lines.Add($"FII stance: {DescribeParticipant(fii.ParticipantBias)}. {ExplainSignals(fii, dir)}");
        }

        return string.Join(" ", lines.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string ExplainSignals(ParticipantMetric m, string directionWord)
    {
        // Rules on Z-short (fast) with fallback to Z-long.
        // Note: Put writing bullish; Call writing bearish; Futures sign = direction.
        var parts = new List<string>();

        // Futures
        var f = PickZ(m.FuturesZShort, m.FuturesZLong);
        if (f >= 1.5) parts.Add("aggressively added long futures");
        else if (f <= -1.5) parts.Add("aggressively added short futures");

        // Put writing proxy
        var p = PickZ(m.PutZShort, m.PutZLong);
        if (p >= 1.5) parts.Add("heavy put writing");
        else if (p <= -1.5) parts.Add("put unwinding");

        // Call writing proxy
        var c = PickZ(m.CallZShort, m.CallZLong);
        if (c >= 1.5) parts.Add("heavy call writing");
        else if (c <= -1.5) parts.Add("call covering");

        if (parts.Count == 0)
            return $"Signals are mixed but overall tilt is {directionWord}.";

        // join
        return $"Key signals: {string.Join(", ", parts)}.";
    }

    private static double PickZ(double zShort, double zLong)
    {
        // Prefer short if it's extreme, else long.
        if (Math.Abs(zShort) >= 1.5) return zShort;
        return zLong;
    }
}