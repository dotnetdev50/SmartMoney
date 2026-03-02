using SmartMoney.Domain.Enums;

namespace SmartMoney.Domain.Entities;

public class ParticipantDailyPosition
{
    public Guid Id { get; set; }

    public DateTime Date { get; set; }

    public string IndexSymbol { get; set; } = "NIFTY";

    public ParticipantType Participant { get; set; }

    // Futures
    public double FuturesNet { get; set; }

    public double FuturesChange { get; set; }

    // Options Writing
    public double PutWriting { get; set; }

    public double CallWriting { get; set; }
}