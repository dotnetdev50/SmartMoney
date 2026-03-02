using SmartMoney.Domain.Enums;

namespace SmartMoney.Domain.Entities;

public class MarketBias
{
    public Guid Id { get; set; }

    public DateTime Date { get; set; }

    public double RawBias { get; set; }

    public double FinalScore { get; set; }

    public double ShockScore { get; set; }

    public Regime Regime { get; set; }
}