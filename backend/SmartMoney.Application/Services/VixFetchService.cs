using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMoney.Application.Options;
using System.Globalization;

namespace SmartMoney.Application.Services;

/// <summary>
/// Fetches the India VIX closing value from the NSE archives CSV.
/// The CSV at <see cref="NseOptions.VixArchiveUrl"/> contains the full history:
///   Date,Open,High,Low,Close,Prev Close,Change
///   27-Feb-2026,15.1325,15.365,14.8875,15.1675,...
/// </summary>
public sealed class VixFetchService(
    HttpClient http,
    IOptions<NseOptions> options,
    ILogger<VixFetchService> logger)
{
    private readonly NseOptions _opt = options.Value;

    /// <summary>
    /// Returns the India VIX closing value for <paramref name="date"/>, or null on failure.
    /// Never throws.
    /// </summary>
    public async Task<double?> FetchVixAsync(DateTime date, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _opt.VixArchiveUrl);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Accept", "text/csv,text/plain,*/*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.nseindia.com/");

            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var content = await resp.Content.ReadAsStringAsync(ct);
            var vix = ParseVixClose(content, date);

            if (vix.HasValue)
                logger.LogInformation("India VIX for {Date}: {Vix}", date.ToString("yyyy-MM-dd"), vix.Value);
            else
                logger.LogWarning("India VIX value not found in CSV for {Date} — will fall back to null.", date.ToString("yyyy-MM-dd"));

            return vix;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to fetch India VIX for {Date}: {Msg}", date.ToString("yyyy-MM-dd"), ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Parses the VIX close value from the full-history CSV for the given date.
    /// Handles both "DD-Mon-YYYY" (e.g. "27-Feb-2026") and "D-Mon-YYYY" (e.g. "7-Feb-2026").
    /// </summary>
    private static double? ParseVixClose(string csv, DateTime date)
    {
        // NSE VIX CSV date format: dd-MMM-yyyy (e.g. "27-Feb-2026")
        var targetDate = date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture); // always two-digit day

        var lines = csv.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // Fast prefix match on date token
            var comma = line.IndexOf(',');
            if (comma <= 0) continue;

            var datePart = line[..comma].Trim();
            if (!datePart.Equals(targetDate, StringComparison.OrdinalIgnoreCase)) continue;

            var cols = line.Split(',');
            // cols: Date, Open, High, Low, Close, Prev Close, Change
            if (cols.Length < 5) continue;

            var closeStr = cols[4].Trim();
            if (double.TryParse(closeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var vix))
                return Math.Round(vix, 2);
        }

        return null;
    }
}
