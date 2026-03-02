namespace SmartMoney.Domain.Entities;

public class JobRunLog
{
    public Guid Id { get; set; }
    public string JobName { get; set; } = "";
    public DateTime Date { get; set; }             // trading date the job targeted (IST date)
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}