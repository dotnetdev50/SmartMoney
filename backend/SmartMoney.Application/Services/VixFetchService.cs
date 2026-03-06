using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMoney.Application.Options;
using System.Globalization;
using System.Text.Json;

namespace SmartMoney.Application.Services;

/// <summary>
/// Fetches the India VIX closing value from NSE.
///
/// Strategy (in order):
///   1. NSE VIX API with csv=true
///      URL: {VixApiBaseUrl}?from=dd-MM-yyyy&amp;to=dd-MM-yyyy&amp;csv=true
///      e.g. https://www.nseindia.com/api/historicalOR/vixhistory?from=06-03-2026&amp;to=06-03-2026&amp;csv=true
///      Returns CSV directly. Requires NSE session cookies (homepage primed first).
///      Response columns: Date,Open,High,Low,Close,Prev Close,Change,%Change
///      Date format: DD-MAR-YYYY in uppercase (e.g. 06-MAR-2026)
///
///   2. Archive CSV fallback — <see cref="NseOptions.VixArchiveUrl"/>
///      Full-history CSV, no session required.
///      Date format: DD-MMM-YYYY (e.g. 05-Mar-2026, title-case)
/// </summary>
public sealed class VixFetchService(
    HttpClient http,
    IOptions<NseOptions> options,
    ILogger<VixFetchService> logger)
{
    private readonly NseOptions _opt = options.Value;

    private const string NseHomeUrl = "https://www.nseindia.com/";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    /// <summary>
    /// Returns the India VIX closing value for <paramref name="date"/>, or null on failure.
    /// Never throws.
    /// </summary>
    public async Task<double?> FetchVixAsync(DateTime date, CancellationToken ct)
    {
        var apiVix = await FetchVixFromApiCsvAsync(date, ct);
        if (apiVix.HasValue)
        {
            logger.LogInformation("India VIX for {Date} via NSE API (csv=true): {Vix}",
                date.ToString("yyyy-MM-dd"), apiVix.Value);
            return apiVix;
        }

        logger.LogWarning("NSE VIX API returned null for {Date}. Falling back to archive CSV.",
            date.ToString("yyyy-MM-dd"));
        return await FetchVixFromArchiveCsvAsync(date, ct);
    }

    /// <summary>
    /// Downloads VIX data as CSV from the NSE API using the confirmed &amp;csv=true parameter.
    /// Primes session cookies via NSE homepage first (required for Akamai bot-protection).
    /// URL pattern: {VixApiBaseUrl}?from=dd-MM-yyyy&amp;to=dd-MM-yyyy&amp;csv=true
    /// </summary>
    private async Task<double?> FetchVixFromApiCsvAsync(DateTime date, CancellationToken ct)
    {
        try
        {
            // API date format: dd-MM-yyyy  e.g. 06-03-2026
            var dateStr = date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
            var apiBase = (_opt.VixApiBaseUrl ?? "https://www.nseindia.com/api/historicalOR/vixhistory").TrimEnd('/');
            var apiUrl = $"{apiBase}?from={dateStr}&to={dateStr}&csv=true";

            var cookieContainer = new System.Net.CookieContainer();
            var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true,
                AllowAutoRedirect = true,
            };

            using var sessionClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(_opt.RequestTimeoutSeconds > 0 ? _opt.RequestTimeoutSeconds : 30)
            };

            // Step 1: prime session cookies via NSE homepage (Akamai bot-protection).
            AddNseHeaders(sessionClient, acceptHtml: true);
            logger.LogInformation("Priming NSE session via homepage for VIX API (csv=true) call.");
            var homeResp = await sessionClient.GetAsync(NseHomeUrl, ct);
            logger.LogInformation("NSE homepage responded with HTTP {Status}.", (int)homeResp.StatusCode);

            // Step 2: download the CSV with session cookies in place.
            AddNseHeaders(sessionClient, acceptHtml: false);
            logger.LogInformation("Fetching VIX CSV from {Url}", apiUrl);
            var resp = await sessionClient.GetAsync(apiUrl, ct);

            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("NSE VIX API returned HTTP {Status} for {Date}.",
                    (int)resp.StatusCode, date.ToString("yyyy-MM-dd"));
                return null;
            }

            var payload = await resp.Content.ReadAsStringAsync(ct);

            var csvVix = ParseVixFromCsv(payload, date, source: "API csv=true");
            if (csvVix.HasValue)
                return csvVix;

            return ParseVixFromJson(payload, date, source: "API JSON fallback");
        }
        catch (Exception ex)
        {
            logger.LogWarning("NSE VIX API (csv=true) fetch failed for {Date}: {Msg}",
                date.ToString("yyyy-MM-dd"), ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Downloads the full-history VIX archive CSV and extracts the value for the given date.
    /// No session cookies required.
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

            var csv = await resp.Content.ReadAsStringAsync(ct);
            return ParseVixFromCsv(csv, date, source: "archive CSV");
        }
        catch (Exception ex)
        {
            logger.LogWarning("Archive CSV VIX fetch failed for {Date}: {Msg}",
                date.ToString("yyyy-MM-dd"), ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Parses the VIX Close value from a CSV string for the given date.
    ///
    /// Handles both casings NSE uses across sources:
    ///   API csv=true  : DD-MMM-YYYY uppercase  (e.g. "06-MAR-2026")
    ///   Archive CSV   : DD-MMM-YYYY title-case (e.g. "06-Mar-2026")
    ///   Both          : single-digit day variant (e.g. "6-MAR-2026")
    ///
    /// Columns: Date, Open, High, Low, Close, Prev Close, Change [, %Change]
    /// Close is at index 4.
    /// </summary>
    private double? ParseVixFromCsv(string csv, DateTime date, string source)
    {
        // Build all date string variants to match regardless of casing.
        var twoDigit = date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture); // 06-Mar-2026
        var oneDigit = date.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture); // 6-Mar-2026

        foreach (var rawLine in csv.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var cols = line.Split(',');
            // Columns: Date(0), Open(1), High(2), Low(3), Close(4), Prev Close(5), Change(6)
            if (cols.Length < 5) continue;

            // Some NSE CSV responses can include quoted values.
            var datePart = cols[0].Trim().Trim('"');

            // OrdinalIgnoreCase handles both "MAR" and "Mar" casing.
            if (!datePart.Equals(twoDigit, StringComparison.OrdinalIgnoreCase) &&
                !datePart.Equals(oneDigit, StringComparison.OrdinalIgnoreCase)) continue;

            var closeStr = cols[4].Trim().Trim('"');
            if (double.TryParse(closeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var vix))
            {
                var result = Math.Round(vix, 2);
                logger.LogInformation("India VIX for {Date} via {Source}: {Vix}",
                    date.ToString("yyyy-MM-dd"), source, result);
                return result;
            }
        }

        logger.LogWarning("India VIX not found in {Source} for {Date}.", source, date.ToString("yyyy-MM-dd"));
        return null;
    }

    private static void AddNseHeaders(HttpClient client, bool acceptHtml)
    {
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            acceptHtml
                ? "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
                : "text/csv,text/plain,application/json,*/*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", NseHomeUrl);
    }

    private double? ParseVixFromJson(string payload, DateTime date, string source)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);

            IEnumerable<JsonElement> rows = [];
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                rows = doc.RootElement.EnumerateArray();
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                    rows = dataEl.EnumerateArray();
                else
                    rows = [doc.RootElement];
            }

            foreach (var row in rows)
            {
                if (row.ValueKind != JsonValueKind.Object) continue;

                if (!TryGetJsonString(row, out var rowDate,
                        "Date", "date", "TIMESTAMP", "timestamp", "HistoricalDate", "historicalDate"))
                    continue;

                if (!IsSameDate(rowDate, date)) continue;

                if (!TryGetJsonDouble(row, out var vix,
                        "Close", "close", "CLOSE", "EOD_CLOSE_INDEX_VAL", "eod_close_index_val"))
                    continue;

                var result = Math.Round(vix, 2);
                logger.LogInformation("India VIX for {Date} via {Source}: {Vix}",
                    date.ToString("yyyy-MM-dd"), source, result);
                return result;
            }
        }
        catch
        {
            // Ignore JSON parse failures; caller already has CSV and archive fallbacks.
        }

        return null;
    }

    private static bool TryGetJsonString(JsonElement obj, out string value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var el)) continue;
            if (el.ValueKind is JsonValueKind.String)
            {
                value = el.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            }

            if (el.ValueKind is JsonValueKind.Number)
            {
                value = el.GetRawText();
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetJsonDouble(JsonElement obj, out double value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var el)) continue;

            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value))
                return true;

            if (el.ValueKind == JsonValueKind.String)
            {
                var s = (el.GetString() ?? string.Empty).Trim().Trim('"').Replace(",", "");
                if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                    return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool IsSameDate(string rawDate, DateTime expected)
    {
        var value = rawDate.Trim().Trim('"');

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) &&
            parsed.Date == expected.Date)
            return true;

        var formats = new[] { "dd-MMM-yyyy", "d-MMM-yyyy", "dd-MM-yyyy", "yyyy-MM-dd" };
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out parsed) && parsed.Date == expected.Date)
            return true;

        return false;
    }
}