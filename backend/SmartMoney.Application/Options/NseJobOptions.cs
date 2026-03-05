namespace SmartMoney.Application.Options;

public sealed class NseJobOptions
{
    public bool Enabled { get; set; } = true;
    public string StartAtIst { get; set; } = "20:30"; // 8:30 PM
    public string EndAtIst { get; set; } = "22:00";   // 10:00 PM
    public int RetryMinutes { get; set; } = 15;
    public int ExpectedParticipantRowsPerDay { get; set; } = 4;
}
