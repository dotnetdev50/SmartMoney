namespace SmartMoney.Job.Export;

public sealed record ParticipantDto(
    string name,
    double bias,
    string? label = null
);

public sealed record MarketTodayDto(
    string index,
    string date,
    double final_score,
    string regime,
    double shock_score,
    IReadOnlyList<ParticipantDto> participants,
    string? bias_Label = null,
    string? strength = null,
    string? explanation = null,
    double? pcr = null,
    double? vix = null,
    double? pcr_volume = null,
    double? banknifty_pcr = null,
    double? banknifty_pcr_volume = null
);

public sealed record MarketHistoryPointDto(
    string date,
    double final_score,
    string regime
);
