using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMoney.Application.Options;
using SmartMoney.Domain.Entities;
using SmartMoney.Domain.Enums;
using SmartMoney.Infrastructure.Persistence;

using System.Globalization;

namespace SmartMoney.Application.Services;

public sealed class CsvIngestionService(
    SmartMoneyDbContext db,
    HttpClient http,
    IOptions<NseOptions> options,
    ILogger<CsvIngestionService> logger)
{
    private readonly NseOptions _opt = options.Value;

    public async Task<bool> IsRawDataPresentAsync(DateTime date, int expectedRows, CancellationToken ct)
    {
        date = date.Date;
        var count = await db.ParticipantRawData.CountAsync(x => x.Date == date, ct);
        return count >= expectedRows;
    }

    public async Task<IngestionResult> IngestParticipantOiAsync(DateTime date, CancellationToken ct)
    {
        date = date.Date;

        var url = BuildParticipantOiUrl(date);
        await using var csvStream = await DownloadAsync(url, ct);

        var parsed = await ParseParticipantOiCsvAsync(csvStream, ct);

        foreach (var r in parsed)
            r.Date = date;

        if (parsed.Count == 0)
            return new IngestionResult(date, url, Inserted: 0, Updated: 0, Note: "No participant rows parsed.");

        // Load previous day per participant (latest < date)
        var participants = parsed.Select(x => x.Participant).Distinct().ToList();

        var prevByParticipant = await db.ParticipantRawData
            .AsNoTracking()
            .Where(x => x.Date < date && participants.Contains(x.Participant))
            .GroupBy(x => x.Participant)
            .Select(g => g.OrderByDescending(x => x.Date).First())
            .ToListAsync(ct);

        var prevMap = prevByParticipant.ToDictionary(x => x.Participant, x => x);

        // Compute changes using prev stored nets (we store PutOiNet/CallOiNet in PutOiChange/CallOiChange? No.
        // We store PutOiNet in PutOiChange? No. We store net + change separately:
        //   FuturesNet = net
        //   FuturesChange = change
        //   PutOiChange = change in net writing proxy
        //   CallOiChange = change in net writing proxy
        //
        // So we need today's nets too; store nets via FuturesNet only; for options we only store changes in schema.
        // Phase-1: we treat PutOiChange/CallOiChange as the *net* change series (delta of writing proxy), which is exactly what engine needs.
        // If later you want store absolute option net too, we extend schema in Phase-2.

        foreach (var row in parsed)
        {
            if (prevMap.TryGetValue(row.Participant, out var prev))
            {
                row.FuturesChange = row.FuturesNet - prev.FuturesNet;

                // We need prev option proxy nets; we reconstruct from prev stored changes is not possible.
                // So Phase-1: we approximate prev option net proxies as 0 and keep daily net proxy as "change".
                // Better: store option proxy net into a hidden field? Not available.
                // Therefore: we treat PutOiChange/CallOiChange as today's option proxy net (not delta) for V1.
                // Engine uses rolling stats anyway; it still works.
            }
            else
            {
                row.FuturesChange = 0;
            }
        }

        // Upsert for date: delete existing date rows then insert parsed
        var existing = await db.ParticipantRawData
            .Where(x => x.Date == date)
            .ToListAsync(ct);

        if (existing.Count > 0)
        {
            db.ParticipantRawData.RemoveRange(existing);
            await db.SaveChangesAsync(ct);
        }

        await db.ParticipantRawData.AddRangeAsync(parsed, ct);
        var inserted = await db.SaveChangesAsync(ct);

        return new IngestionResult(date, url, Inserted: inserted, Updated: existing.Count, Note: "OK");
    }

    private string BuildParticipantOiUrl(DateTime date)
    {
        var ddMMyyyy = date.ToString("ddMMyyyy", CultureInfo.InvariantCulture);
        var file = _opt.ParticipantOiTemplate.Replace("{ddMMyyyy}", ddMMyyyy, StringComparison.OrdinalIgnoreCase);
        return $"{_opt.ArchivesBaseUrl.TrimEnd('/')}/{file}";
    }

    private async Task<Stream> DownloadAsync(string url, CancellationToken ct)
    {
        // NSE often blocks “plain” requests. Use headers that resemble a browser.
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        req.Headers.TryAddWithoutValidation("Accept", "text/csv,*/*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        req.Headers.TryAddWithoutValidation("Referer", "https://www.nseindia.com/");

        var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new FileNotFoundException("NSE CSV not found (holiday/non-trading day).");

        var ms = new MemoryStream();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        await s.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    private async Task<List<ParticipantRawData>> ParseParticipantOiCsvAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream);

        // 1) Find the actual header line (skip title/preamble)
        string? headerLine = null;
        for (int i = 0; i < 25; i++)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) break;

            // NSE file header always contains "Client Type" and "Future Index Long"
            if (line.IndexOf("Client Type", StringComparison.OrdinalIgnoreCase) >= 0 &&
                line.IndexOf("Future Index Long", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                headerLine = line;
                break;
            }
        }

        if (headerLine is null)
            return new List<ParticipantRawData>();

        // 2) Build a normalized header index map (handles trailing spaces)
        var headers = SplitCsvLine(headerLine);
        var idx = BuildHeaderIndexNormalized(headers);

        // 3) Parse data rows
        var rows = new List<ParticipantRawData>();

        string? line2;
        while ((line2 = await reader.ReadLineAsync()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line2)) continue;

            var cols = SplitCsvLine(line2);
            if (cols.Count < headers.Count) continue;

            var clientType = GetByAliases(cols, idx, "Client Type");

            // Skip TOTAL row
            if (clientType.Equals("TOTAL", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryMapParticipant(clientType, out var participant))
                continue;

            if (!TryGetDoubleByAliases(cols, idx, out var futLong, "Future Index Long")) continue;
            if (!TryGetDoubleByAliases(cols, idx, out var futShort, "Future Index Short")) continue;

            if (!TryGetDoubleByAliases(cols, idx, out var putLong, "Option Index Put Long")) continue;
            if (!TryGetDoubleByAliases(cols, idx, out var putShort, "Option Index Put Short")) continue;

            // Note: in NSE CSV, "Option Index Call Long" and "Option Index Call Short" exist
            if (!TryGetDoubleByAliases(cols, idx, out var callLong, "Option Index Call Long")) continue;
            if (!TryGetDoubleByAliases(cols, idx, out var callShort, "Option Index Call Short")) continue;

            var futuresNet = futLong - futShort;

            // writing proxy = short - long
            var putProxyNet = putShort - putLong;
            var callProxyNet = callShort - callLong;

            rows.Add(new ParticipantRawData
            {
                Id = Guid.NewGuid(),
                Date = DateTime.MinValue, // set in ingest method
                Participant = participant,
                FuturesNet = futuresNet,
                FuturesChange = 0,        // set in ingest method
                PutOiChange = putProxyNet,
                CallOiChange = callProxyNet
            });
        }
        logger.LogInformation("Parsed rows: {Count}. Participants: {Participants}",
             rows.Count, string.Join(",", rows.Select(x => x.Participant).Distinct()));
        return rows;
    }

    // ---------- helpers (add/keep inside service) ----------

    private static Dictionary<string, int> BuildHeaderIndexNormalized(List<string> headers)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Count; i++)
        {
            var key = NormalizeHeader(headers[i]);
            if (!map.ContainsKey(key))
                map[key] = i;
        }
        return map;
    }

    private static string NormalizeHeader(string h)
    {
        h = h.Trim().Trim('\uFEFF');
        var chars = h.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    private static string GetByAliases(List<string> cols, Dictionary<string, int> idx, params string[] aliases)
    {
        foreach (var a in aliases)
        {
            var key = NormalizeHeader(a);
            if (idx.TryGetValue(key, out var i) && i < cols.Count)
                return cols[i].Trim();
        }
        return "";
    }

    private static bool TryGetDoubleByAliases(List<string> cols, Dictionary<string, int> idx, out double value, params string[] aliases)
    {
        value = 0;
        foreach (var a in aliases)
        {
            var key = NormalizeHeader(a);
            if (idx.TryGetValue(key, out var i) && i < cols.Count)
            {
                var raw = cols[i].Replace(",", "").Trim();
                if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                    return true;
            }
        }
        return false;
    }

    private static bool TryMapParticipant(string raw, out ParticipantType participant)
    {
        participant = default;
        raw = raw.Trim();

        if (raw.Equals("FII", StringComparison.OrdinalIgnoreCase)) { participant = ParticipantType.FII; return true; }
        if (raw.Equals("DII", StringComparison.OrdinalIgnoreCase)) { participant = ParticipantType.DII; return true; }
        if (raw.Equals("PRO", StringComparison.OrdinalIgnoreCase) || raw.Equals("Pro", StringComparison.OrdinalIgnoreCase))
        { participant = ParticipantType.Pro; return true; }
        if (raw.Equals("CLIENT", StringComparison.OrdinalIgnoreCase) || raw.Equals("Client", StringComparison.OrdinalIgnoreCase))
        { participant = ParticipantType.Retail; return true; }

        return false;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var cur = new System.Text.StringBuilder();
        var inQuotes = false;

        int i = 0;
        while (i < line.Length)
        {
            var ch = line[i];
            if (ch == '"')
            {
                // handle doubled quotes "" inside quoted field
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cur.Append('"');
                    i += 2; // advance past both quotes
                    continue;
                }

                inQuotes = !inQuotes;
                i++;
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                result.Add(cur.ToString());
                cur.Clear();
                i++;
                continue;
            }

            cur.Append(ch);
            i++;
        }

        result.Add(cur.ToString());
        return result;
    }

    public async Task<object> IngestParticipantOiRangeAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        from = from.Date;
        to = to.Date;

        var ok = new List<object>();
        var failed = new List<object>();
        var skipped = 0;

        for (var d = from; d <= to; d = d.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();

            // Skip weekends
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                skipped++;
                continue;
            }

            try
            {
                var r = await IngestParticipantOiAsync(d, ct);
                if (r.Inserted == 0 && r.Updated == 0 && r.Note.Contains("No participant rows", StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add(new { date = d.ToString("yyyy-MM-dd"), reason = r.Note, url = r.Url });
                }
                else
                {
                    ok.Add(new { date = d.ToString("yyyy-MM-dd"), r.Inserted, r.Updated });
                }
            }
            catch (Exception ex)
            {
                // NSE will fail on holidays (no file), occasional blocks, etc.
                failed.Add(new { date = d.ToString("yyyy-MM-dd"), reason = ex.Message });
            }
        }

        return new
        {
            from = from.ToString("yyyy-MM-dd"),
            to = to.ToString("yyyy-MM-dd"),
            skippedWeekends = skipped,
            successDays = ok.Count,
            failedDays = failed.Count,
            ok,
            failed
        };
    }
}

public sealed record IngestionResult(DateTime Date, string Url, int Inserted, int Updated, string Note);