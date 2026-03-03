namespace SmartMoney.Job.Export;

public static class MarketNarrative
{
    public static (string biasLabel, string strength) ScoreLabel(double score)
    {
        var abs = Math.Abs(score);

        var strength =
            abs >= 70 ? "Strong" :
            abs >= 40 ? "Moderate" :
            abs >= 20 ? "Mild" : "Neutral";

        var biasLabel =
            score >= 40 ? "Bullish" :
            score <= -40 ? "Bearish" : "Neutral";

        return (biasLabel, strength);
    }

    public static string ParticipantLabel(double bias)
    {
        var abs = Math.Abs(bias);

        if (abs >= 2.0) return bias > 0 ? "Strong Bullish" : "Strong Bearish";
        if (abs >= 1.0) return bias > 0 ? "Bullish" : "Bearish";
        if (abs >= 0.4) return bias > 0 ? "Mild Bullish" : "Mild Bearish";
        return "Neutral";
    }

    public static string Explanation(
        string regime,
        double shockScore,
        IReadOnlyList<ParticipantDto> participants,
        double finalScore)
    {
        var intro = regime == "SHOCK"
            ? $"Shock regime detected (shock={shockScore:0.0})."
            : $"Normal regime (shock={shockScore:0.0}).";

        var driver = participants
            .OrderByDescending(p => Math.Abs(p.bias))
            .FirstOrDefault();

        if (driver is null)
            return intro;

        var scoreLine = finalScore >= 0 ? "Composite bias is bullish." : "Composite bias is bearish.";

        return $"{intro} Top driver: {driver.name} ({driver.bias:0.00}, {driver.label}). {scoreLine}";
    }
}
