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
    string? explanation = null
);

public sealed record MarketHistoryPointDto(
    string date,
    double final_score,
    string regime
);
