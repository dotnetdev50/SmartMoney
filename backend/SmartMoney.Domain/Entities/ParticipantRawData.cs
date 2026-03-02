using SmartMoney.Domain.Enums;

namespace SmartMoney.Domain.Entities;

public class ParticipantRawData
{
    public Guid Id { get; set; }

    public DateTime Date { get; set; }

    public ParticipantType Participant { get; set; }

    public double FuturesNet { get; set; }

    public double FuturesChange { get; set; }

    public double PutOiChange { get; set; }

    public double CallOiChange { get; set; }
}