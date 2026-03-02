using SmartMoney.Domain.Entities;

namespace SmartMoney.Application.Services;

public class ParticipantScoreCalculator
{
    public double CalculateScore(
        double futuresNetScore,
        double futuresChangeScore,
        double putWritingScore,
        double callWritingScore)
    {
        // Weight logic (simple for beginners)
        double futuresWeight = 0.4;
        double changeWeight = 0.2;
        double putWeight = 0.2;
        double callWeight = 0.2;

        var score =
            (futuresNetScore * futuresWeight) +
            (futuresChangeScore * changeWeight) +
            (putWritingScore * putWeight) -
            (callWritingScore * callWeight);

        return Math.Max(-100, Math.Min(100, score));
    }
}