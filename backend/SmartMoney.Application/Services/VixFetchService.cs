using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMoney.Application.Options;
using System.Globalization;
using System.Text.Json;

namespace SmartMoney.Application.Services;

/// <summary>
/// Fetches the India VIX closing value from NSE.
/// Primary method: NSE JSON API at <c>https://www.nseindia.com/api/historicalOR/vixhistory</c>
/// with cookie/session priming (GET homepage first).
/// Fallback: full-history CSV at <see cref="NseOptions.VixArchiveUrl"/>.
/// </summary>
public sealed class VixFetchService(
    HttpClient http,
    IOptions<NseOptions> options,
    ILogger<VixFetchService> logger)
{
    private readonly NseOptions _opt = options.Value;

    private const string NseHomeUrl = "https://www.nseindia.com/";
    private const string NseVixApiTemplate = "https://www.nseindia.com/api/historicalOR/vixhistory?from={0}&to={1}";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    /// <summary>
    /// Returns the India VIX closing value for <paramref name="date"/>, or null on failure.
    /// Never throws.
    /// </summary>
    public async Task<double?> FetchVixAsync(DateTime date, CancellationToken ct)
    {
        // Try NSE API first (requires session/cookie priming)
        var apiVix = await FetchVixFromNseApiAsync(date, ct);
        if (apiVix.HasValue)
        {
            logger.LogInformation("India VIX for {Date} via NSE API: {Vix}", date.ToString("yyyy-MM-dd"), apiVix.Value);
            return apiVix;
        }

        logger.LogWarning("NSE API VIX fetch returned null for {Date}. Falling back to archives CSV.", date.ToString("yyyy-MM-dd"));

        // Fallback: full-history CSV from NSE archives
        return await FetchVixFromArchiveCsvAsync(date, ct);
    }

    /// <summary>
    /// Fetches VIX from the NSE JSON API endpoint, priming the session by visiting the NSE
    /// homepage first so that the required cookies are set before the API call.
    /// The API requires valid NSE session cookies; without them it returns 401/403 or empty data.
    /// </summary>
    private async Task<double?> FetchVixFromNseApiAsync(DateTime date, CancellationToken ct)
    {
        try
        {
            // NSE API date format: dd-MM-yyyy
            var dateStr = date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
            var apiUrl = string.Format(CultureInfo.InvariantCulture, NseVixApiTemplate, dateStr, dateStr);

            // Use a dedicated CookieContainer-aware HttpClient so the session cookies set by
            // the homepage response are automatically attached to the subsequent API call.
            var cookieContainer = new System.Net.CookieContainer();
            var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true,
                AllowAutoRedirect = true,
            };

            // Batch jobs run infrequently — creating a short-lived HttpClient here is intentional
            // and avoids leaking cookies across unrelated service calls.
            using var sessionClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(_opt.RequestTimeoutSeconds > 0 ? _opt.RequestTimeoutSeconds : 30)
            };

            AddNseHeaders(sessionClient, acceptHtml: true);

            // Step 1: Visit NSE homepage to obtain session cookies (Akamai bot-protection).
            logger.LogInformation("Priming NSE session via homepage for VIX API call.");
            var homeResp = await sessionClient.GetAsync(NseHomeUrl, ct);
            logger.LogInformation("NSE homepage responded with HTTP {Status}.", (int)homeResp.StatusCode);

            // Step 2: Call the VIX history API with the cookies obtained above.
            AddNseHeaders(sessionClient, acceptHtml: false);
            var apiResp = await sessionClient.GetAsync(apiUrl, ct);

            if (!apiResp.IsSuccessStatusCode)
            {
                logger.LogWarning("NSE VIX API returned HTTP {Status} for {Date}.", (int)apiResp.StatusCode, date.ToString("yyyy-MM-dd"));
                return null;
            }

            var json = await apiResp.Content.ReadAsStringAsync(ct);
            return ParseVixFromApiJson(json, date);
        }
        catch (Exception ex)
        {
            logger.LogWarning("NSE VIX API fetch failed for {Date}: {Msg}", date.ToString("yyyy-MM-dd"), ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Fetches VIX from the full-history CSV hosted at <see cref="NseOptions.VixArchiveUrl"/>.
    /// This is the fallback path when the NSE API is unavailable.
    /// </summary>
    private async Task<double?> FetchVixFromArchiveCsvAsync(DateTime date, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _opt.VixArchiveUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "text/csv,text/plain,*/*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            req.Headers.TryAddWithoutValidation("Referer", NseHomeUrl);

            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var content = await resp.Content.ReadAsStringAsync(ct);
            var vix = ParseVixClose(content, date);

            if (vix.HasValue)
                logger.LogInformation("India VIX for {Date} via archives CSV: {Vix}", date.ToString("yyyy-MM-dd"), vix.Value);
            else
                logger.LogWarning("India VIX not found in archives CSV for {Date}.", date.ToString("yyyy-MM-dd"));

            return vix;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Archives CSV VIX fetch failed for {Date}: {Msg}", date.ToString("yyyy-MM-dd"), ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Parses the VIX close value from the NSE JSON API response.
    /// Expected format:
    /// <code>{ "data": [ { "EOD_TIMESTAMP": "04-Mar-2026", "EOD_CLOSE_INDEX_VAL": "15.17", ... } ] }</code>
    /// </summary>
    private double? ParseVixFromApiJson(string json, DateTime date)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataArray) ||
                dataArray.ValueKind != JsonValueKind.Array)
            {
                logger.LogWarning("NSE VIX API JSON missing 'data' array for {Date}. Raw: {Json}",
                    date.ToString("yyyy-MM-dd"), json.Length > 200 ? json[..200] : json);
                return null;
            }

            // The API returns data sorted newest-first; find the entry matching our date.
            // InvariantCulture produces English 3-letter month abbreviations (Jan, Feb, Mar…)
            // which match the NSE "EOD_TIMESTAMP" format (e.g. "04-Mar-2026").
            // OrdinalIgnoreCase handles any case differences defensively.
            var targetDate = date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);

            foreach (var item in dataArray.EnumerateArray())
            {
                if (!item.TryGetProperty("EOD_TIMESTAMP", out var ts)) continue;
                var tsStr = ts.GetString() ?? "";

                // Timestamps from NSE API are in "04-Mar-2026" format.
                if (!tsStr.Equals(targetDate, StringComparison.OrdinalIgnoreCase)) continue;

                if (!item.TryGetProperty("EOD_CLOSE_INDEX_VAL", out var closeEl)) continue;

                var closeStr = closeEl.ValueKind == JsonValueKind.String
                    ? closeEl.GetString() ?? ""
                    : closeEl.GetRawText();

                if (double.TryParse(closeStr.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var vix))
                    return Math.Round(vix, 2);
            }

            logger.LogWarning("NSE VIX API: no entry found for {Date} in {Count} records.",
                date.ToString("yyyy-MM-dd"), dataArray.GetArrayLength());
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to parse NSE VIX API JSON for {Date}: {Msg}", date.ToString("yyyy-MM-dd"), ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Parses the VIX close value from the full-history CSV for the given date.
    /// Handles both "DD-Mon-YYYY" (e.g. "27-Feb-2026") and "D-Mon-YYYY" (e.g. "7-Feb-2026").
    /// </summary>
    private static double? ParseVixClose(string csv, DateTime date)
    {
        // NSE VIX CSV date format: dd-MMM-yyyy (e.g. "27-Feb-2026") or d-MMM-yyyy (e.g. "7-Feb-2026")
        var targetDateTwoDigit = date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);
        var targetDateOneDigit = date.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture);

        var lines = csv.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // Fast prefix match on date token
            var comma = line.IndexOf(',');
            if (comma <= 0) continue;

            var datePart = line[..comma].Trim();
            if (!datePart.Equals(targetDateTwoDigit, StringComparison.OrdinalIgnoreCase) &&
                !datePart.Equals(targetDateOneDigit, StringComparison.OrdinalIgnoreCase)) continue;

            var cols = line.Split(',');
            // cols: Date, Open, High, Low, Close, Prev Close, Change
            if (cols.Length < 5) continue;

            var closeStr = cols[4].Trim();
            if (double.TryParse(closeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var vix))
                return Math.Round(vix, 2);
        }

        return null;
    }

    /// <summary>
    /// Adds standard NSE browser headers to the given <see cref="HttpClient"/>.
    /// </summary>
    private static void AddNseHeaders(HttpClient client, bool acceptHtml)
    {
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            acceptHtml ? "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
                       : "application/json,text/plain,*/*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", NseHomeUrl);
    }
}
