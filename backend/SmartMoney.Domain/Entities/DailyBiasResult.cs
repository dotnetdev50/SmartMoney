using SmartMoney.Domain.Enums;

namespace SmartMoney.Domain.Entities;

public class DailyBiasResult
{
    public DateTime Date { get; set; }

    public Dictionary<ParticipantType, double> ParticipantScores { get; set; }
        = [];

    public double OverallBias { get; set; }
}