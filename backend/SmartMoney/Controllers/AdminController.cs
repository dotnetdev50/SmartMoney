using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartMoney.Application.Services;
using SmartMoney.Infrastructure.Persistence;

namespace SmartMoney.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController(CsvIngestionService ingestion) : ControllerBase
{
    [HttpPost("ingest/participant-oi")]
    public async Task<IActionResult> Ingest([FromQuery] DateTime date, CancellationToken ct)
    {
        var result = await ingestion.IngestParticipantOiAsync(date, ct);
        return Ok(result);
    }

    [HttpPost("ingest/range")]
    public async Task<IActionResult> IngestRange([FromQuery] DateTime from, [FromQuery] DateTime to, CancellationToken ct)
    {
        from = from.Date;
        to = to.Date;
        if (to < from) return BadRequest("to must be >= from");

        var results = await ingestion.IngestParticipantOiRangeAsync(from, to, ct);
        return Ok(results);
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run([FromQuery] DateTime date, [FromServices] DailyPipelineService pipeline, CancellationToken ct)
    {
        var result = await pipeline.RunAsync(date, ct);
        return Ok(result);
    }

    [HttpPost("run/range")]
    public async Task<IActionResult> RunRange([FromQuery] DateTime from, [FromQuery] DateTime to, [FromServices] DailyPipelineService pipeline, CancellationToken ct)
    {
        from = from.Date;
        to = to.Date;
        if (to < from) return BadRequest("to must be >= from");

        var result = await pipeline.RunRangeAsync(from, to, ct);
        return Ok(result);
    }

    [HttpPost("bootstrap")]
    public async Task<IActionResult> Bootstrap(
        [FromServices] DailyPipelineService pipeline,
        [FromQuery] int days = 45,
        CancellationToken ct = default)
    {
        days = Math.Clamp(days, 10, 365);

        var to = DateTime.Today.Date;
        var from = to.AddDays(-days + 1);

        // 1) Ingest raw
        var ingest = await ingestion.IngestParticipantOiRangeAsync(from, to, ct);

        // 2) Run pipeline (metrics + market_bias)
        var run = await pipeline.RunRangeAsync(from, to, ct);

        return Ok(new
        {
            from = from.ToString("yyyy-MM-dd"),
            to = to.ToString("yyyy-MM-dd"),
            ingest,
            run
        });
    }

    [HttpGet("job/status")]
    public async Task<IActionResult> JobStatus([FromServices] SmartMoneyDbContext db, CancellationToken ct)
    {
        var last = await db.JobRunLogs
            .AsNoTracking()
            .Where(x => x.JobName == "DailyNseJob")
            .OrderByDescending(x => x.StartedAtUtc)
            .FirstOrDefaultAsync(ct);

        var latestMarket = await db.MarketBiases
            .AsNoTracking()
            .OrderByDescending(x => x.Date)
            .FirstOrDefaultAsync(ct);

        return Ok(new
        {
            lastRun = last is null ? null : new
            {
                last.Date,
                last.StartedAtUtc,
                last.CompletedAtUtc,
                last.Success,
                last.Message
            },
            latestComputedDate = latestMarket?.Date.ToString("yyyy-MM-dd"),
            latestFinalScore = latestMarket?.FinalScore
        });
    }
}