using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartMoney.Application.Services;
using SmartMoney.Contracts.Market;
using SmartMoney.Domain.Enums;
using SmartMoney.Infrastructure.Persistence;

namespace SmartMoney.Controllers;

[ApiController]
[Route("api/market")]
public class MarketController(SmartMoneyDbContext db, MarketPresentationService present) : ControllerBase
{
    [HttpGet("today")]
    public async Task<IActionResult> GetToday(CancellationToken ct)
    {
        var date = DateTime.Today;

        var market = await db.MarketBiases
            .AsNoTracking()
            .OrderByDescending(x => x.Date)
            .FirstOrDefaultAsync(ct);

        if (market is null)
            return NotFound("No market_bias computed yet.");

        date = market.Date;

        var metrics = await db.ParticipantMetrics
            .AsNoTracking()
            .Where(x => x.Date == date)
            .ToListAsync(ct);

        var (label, strength) = present.DescribeFinalScore(market.FinalScore);

        var fii = metrics.FirstOrDefault(m => m.Participant == ParticipantType.FII);
        var explanation = present.BuildExplanation(date, market.Regime, metrics, fii);

        var response = new MarketTodayResponse
        {
            Index = "NIFTY",
            Date = date.ToString("yyyy-MM-dd"),
            AsOfDate = DateTime.Now.ToString("yyyy-MM-dd"),
            Final_Score = market.FinalScore,
            Bias_Label = label,
            Strength = strength,
            Regime = market.Regime == Regime.Shock ? "SHOCK" : "NORMAL",
            Shock_Score = market.ShockScore,
            Participants = [.. metrics
                .OrderBy(m => m.Participant) // stable
                .Select(m => new ParticipantBiasDto
                {
                    Name = m.Participant.ToString().ToUpperInvariant(),
                    Bias = m.ParticipantBias,
                    Label = present.DescribeParticipant(m.ParticipantBias)
                })],
            Explanation = explanation
        };

        return Ok(response);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        var from = DateTime.Today.AddDays(-days + 1);

        var rows = await db.MarketBiases
            .AsNoTracking()
            .Where(x => x.Date >= from)
            .OrderBy(x => x.Date)
            .Select(x => new
            {
                date = x.Date.ToString("yyyy-MM-dd"),
                final_score = x.FinalScore,
                regime = x.Regime.ToString().ToUpperInvariant()
            })
            .ToListAsync(ct);

        return Ok(rows);
    }
}