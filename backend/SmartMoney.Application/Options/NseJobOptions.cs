namespace SmartMoney.Application.Options;

public sealed class NseJobOptions
{
    public bool Enabled { get; set; } = true;
    public string StartAtIst { get; set; } = "20:05"; // 8:05 PM
    public string EndAtIst { get; set; } = "22:00";   // 10:00 PM
    public int RetryMinutes { get; set; } = 15;
    public int ExpectedParticipantRowsPerDay { get; set; } = 4;

    /// <summary>Maximum number of PCR/VIX fetch attempts (H3). Default: 6 retries × 10 min = 60 min window.</summary>
    public int PcrVixMaxRetries { get; set; } = 6;

    /// <summary>Delay in minutes between PCR/VIX fetch retries (H3).</summary>
    public int PcrVixRetryMinutes { get; set; } = 10;
}