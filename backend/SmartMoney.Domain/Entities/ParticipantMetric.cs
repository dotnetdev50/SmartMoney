using SmartMoney.Domain.Enums;

namespace SmartMoney.Domain.Entities;

public class ParticipantMetric
{
    public Guid Id { get; set; }

    public DateTime Date { get; set; }

    public ParticipantType Participant { get; set; }

    public double FuturesZShort { get; set; }
    public double FuturesZLong { get; set; }

    public double PutZShort { get; set; }
    public double PutZLong { get; set; }

    public double CallZShort { get; set; }
    public double CallZLong { get; set; }

    public double ParticipantBias { get; set; }
}