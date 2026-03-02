using SmartMoney.Domain.Entities;
using SmartMoney.Domain.Enums;

namespace SmartMoney.Application.Services;

public class BiasEngineService(
    NormalizationService normalizationService,
    ParticipantScoreCalculator scoreCalculator)
{
    public DailyBiasResult CalculateBias(
        List<ParticipantDailyPosition> historicalData,
        ParticipantDailyPosition today)
    {
        var result = new DailyBiasResult
        {
            Date = today.Date
        };

        var participants = new[]
        {
            ParticipantType.FII,
            ParticipantType.DII,
            ParticipantType.Retail,
            ParticipantType.Pro
        };

        foreach (var participant in participants)
        {
            var history = historicalData
                .Where(x => x.Participant == participant)
                .ToList();

            var futuresNetScore = normalizationService.Normalize(
                history.Select(x => x.FuturesNet).ToList(),
                today.FuturesNet);

            var futuresChangeScore = normalizationService.Normalize(
                history.Select(x => x.FuturesChange).ToList(),
                today.FuturesChange);

            var putScore = normalizationService.Normalize(
                history.Select(x => x.PutWriting).ToList(),
                today.PutWriting);

            var callScore = normalizationService.Normalize(
                history.Select(x => x.CallWriting).ToList(),
                today.CallWriting);

            var participantScore = scoreCalculator.CalculateScore(
                futuresNetScore,
                futuresChangeScore,
                putScore,
                callScore);

            result.ParticipantScores[participant] = participantScore;
        }

        // Smart money weighted bias
        result.OverallBias =
            (result.ParticipantScores[ParticipantType.FII] * 0.4) +
            (result.ParticipantScores[ParticipantType.Pro] * 0.3) +
            (result.ParticipantScores[ParticipantType.DII] * 0.2) +
            (result.ParticipantScores[ParticipantType.Retail] * 0.1);

        return result;
    }
}