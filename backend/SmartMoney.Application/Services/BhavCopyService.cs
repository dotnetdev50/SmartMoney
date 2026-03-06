using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMoney.Application.Options;
using SmartMoney.Domain.Entities;
using System.Globalization;
using System.IO.Compression;

namespace SmartMoney.Application.Services;

/// <summary>
/// Downloads the NSE F&amp;O UDiFF Common Bhavcopy Final ZIP via the NSE Reports API
/// and computes Put-Call Ratios for NIFTY and BANKNIFTY across all expiries.
///
/// This is the PRIMARY PCR source (new NSE UDiFF format, supersedes op-bhavcopy and PR file).
///
/// API URL  : {BhavCopyReportsApiUrl}&amp;date=dd-MMM-yyyy&amp;type=equity&amp;mode=single
///            e.g. https://www.nseindia.com/api/reports?archives=[...]&amp;date=05-Mar-2026&amp;type=equity&amp;mode=single
/// ZIP name : BhavCopy_NSE_FO_0_0_0_{YYYYMMDD}_F_0000.zip  (e.g. BhavCopy_NSE_FO_0_0_0_20260305_F_0000.zip)
/// CSV name : BhavCopy_NSE_FO_0_0_0_{YYYYMMDD}_F_0000.csv
///
/// Key CSV columns:
///   TckrSymb   — ticker symbol (e.g. NIFTY, BANKNIFTY)
///   Optn       — option type: CE or PE
///   OpnIntrst  — Open Interest  → used for OI-based PCR
///   TtlTradgV  — Total Trading Volume → used for Volume-based PCR
///
/// PCR (OI)     = Sum(OpnIntrst  for PE) / Sum(OpnIntrst  for CE)  — all expiries
/// PCR (Volume) = Sum(TtlTradgV  for PE) / Sum(TtlTradgV  for CE)  — all expiries
/// </summary>
public sealed class BhavCopyService(
    HttpClient _,
    IOptions<NseOptions> options,
    ILogger<BhavCopyService> logger)
{
    private readonly NseOptions _opt = options.Value;

    private const string NseHomeUrl = "https://www.nseindia.com/";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    /// <summary>
    /// Fetches PCR values for NIFTY and BANKNIFTY for the given date.
    /// Returns null if data is unavailable (holiday / 404 / parse error). Never throws.
    /// </summary>
    public async Task<PrPcrResult?> FetchPcrAsync(DateTime date, CancellationToken ct)
    {
        // API date format: dd-MMM-yyyy  e.g. 05-Mar-2026
        var dateStr = date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);
        var apiBase = (_opt.BhavCopyReportsApiUrl ?? string.Empty).TrimEnd('&');
        var apiUrl = $"{apiBase}&date={dateStr}&type=equity&mode=single";

        logger.LogInformation("Fetching NSE UDiFF bhavcopy PCR for {Date} from {Url}",
            date.ToString("yyyy-MM-dd"), apiUrl);

        try
        {
            await using var zipStream = await DownloadWithSessionAsync(apiUrl, ct);
            return await ParsePcrFromZipAsync(zipStream, date, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogWarning("NSE UDiFF bhavcopy ZIP not found (404) for {Date} — likely holiday or data not yet published.",
                date.ToString("yyyy-MM-dd"));
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to fetch/parse NSE UDiFF bhavcopy for {Date}: {Msg}",
                date.ToString("yyyy-MM-dd"), ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Downloads the ZIP after priming NSE session cookies via the homepage.
    /// NSE uses Akamai bot-protection; a valid session is required.
    /// </summary>
    private async Task<Stream> DownloadWithSessionAsync(string url, CancellationToken ct)
    {
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

        sessionClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        sessionClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        sessionClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", NseHomeUrl);
        sessionClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        // Step 1: prime session cookies (Akamai bot-protection).
        logger.LogInformation("Priming NSE session via homepage for UDiFF bhavcopy download.");
        var homeResp = await sessionClient.GetAsync(NseHomeUrl, ct);
        logger.LogInformation("NSE homepage responded with HTTP {Status}.", (int)homeResp.StatusCode);

        // Step 2: download the ZIP with session cookies in place.
        sessionClient.DefaultRequestHeaders.Remove("Accept");
        sessionClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "application/zip,application/octet-stream,*/*");

        var resp = await sessionClient.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        var ms = new MemoryStream();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        await s.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    private async Task<PrPcrResult?> ParsePcrFromZipAsync(Stream zipStream, DateTime date, CancellationToken ct)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        // Expected CSV name: BhavCopy_NSE_FO_0_0_0_{YYYYMMDD}_F_0000.csv
        var yyyymmdd = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var expectedName = $"BhavCopy_NSE_FO_0_0_0_{yyyymmdd}_F_0000.csv";

        var entry = archive.Entries.FirstOrDefault(e =>
                        e.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                    ?? archive.Entries.FirstOrDefault(e =>
                        e.Name.StartsWith("BhavCopy_NSE_FO", StringComparison.OrdinalIgnoreCase) &&
                        e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            logger.LogWarning("No BhavCopy_NSE_FO*.csv entry found in ZIP for {Date}. Entries: {Entries}",
                date.ToString("yyyy-MM-dd"),
                string.Join(", ", archive.Entries.Select(e => e.Name)));
            return null;
        }

        logger.LogInformation("Parsing UDiFF bhavcopy CSV entry: {Name}", entry.Name);
        await using var csvStream = entry.Open();
        return await ComputePcrFromCsvAsync(csvStream, ct);
    }

    private async Task<PrPcrResult?> ComputePcrFromCsvAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream);

        var headerLine = await reader.ReadLineAsync(ct);
        if (headerLine is null) return null;

        var headers = headerLine.Split(',')
            .Select(h => h.Trim().Trim('"').ToUpperInvariant())
            .ToList();

        // Actual UDiFF column names (verified from live CSV):
        //   TckrSymb      → TCKRSYMB
        //   OptnTp        → OPTNTP       (was wrongly mapped to "OPTN")
        //   OpnIntrst     → OPNINTRST    ✓
        //   TtlTradgVol   → TTLTRADGVOL  (was wrongly mapped to "TTLTRADGV")
        //   Sgmt          → SGMT         ✓
        //   FinInstrmTp   → FININSTRMTP  (was wrongly mapped to "FININSTRM")
        var symbolIdx = headers.IndexOf("TCKRSYMB");
        var optnIdx = headers.IndexOf("OPTNTP");       // fixed: was "OPTN"
        var oiIdx = headers.IndexOf("OPNINTRST");
        var volIdx = headers.IndexOf("TTLTRADGVOL");  // fixed: was "TTLTRADGV"
        var sgmtIdx = headers.IndexOf("SGMT");
        var finInstIdx = headers.IndexOf("FININSTRMTP");  // fixed: was "FININSTRM"

        if (symbolIdx < 0 || optnIdx < 0 || oiIdx < 0 || volIdx < 0)
        {
            logger.LogWarning(
                "UDiFF bhavcopy CSV missing required columns. Expected: TckrSymb/OptnTp/OpnIntrst/TtlTradgVol. Found headers: {H}",
                headerLine);
            return null;
        }

        var maxIdx = new[] { symbolIdx, optnIdx, oiIdx, volIdx, sgmtIdx, finInstIdx }
            .Where(i => i >= 0).Max();

        double niftyPutOi = 0, niftyCallOi = 0;
        double niftyPutVol = 0, niftyCallVol = 0;
        double bankNiftyPutOi = 0, bankNiftyCallOi = 0;
        double bankNiftyPutVol = 0, bankNiftyCallVol = 0;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = line.Split(',');
            if (cols.Length <= maxIdx) continue;

            // Filter to F&O segment only
            if (sgmtIdx >= 0)
            {
                var sgmt = cols[sgmtIdx].Trim().Trim('"');
                if (!sgmt.Equals("FO", StringComparison.OrdinalIgnoreCase)) continue;
            }

            // Filter to index options only (IDO) — stock options (STO) are excluded.
            // NOTE: if FinInstrmTp column is absent or all rows are STO, PCR will be zero.
            if (finInstIdx >= 0)
            {
                var finInst = cols[finInstIdx].Trim().Trim('"');
                if (!finInst.Equals("IDO", StringComparison.OrdinalIgnoreCase)) continue;
            }

            var symbol = cols[symbolIdx].Trim().Trim('"').ToUpperInvariant();
            var optType = cols[optnIdx].Trim().Trim('"').ToUpperInvariant();

            bool isNifty = symbol.Equals("NIFTY", StringComparison.OrdinalIgnoreCase);
            bool isBankNifty = symbol.Equals("BANKNIFTY", StringComparison.OrdinalIgnoreCase);
            if (!isNifty && !isBankNifty) continue;

            bool isPut = optType.Equals("PE", StringComparison.OrdinalIgnoreCase);
            bool isCall = optType.Equals("CE", StringComparison.OrdinalIgnoreCase);
            if (!isPut && !isCall) continue;

            var oiStr = cols[oiIdx].Trim().Trim('"').Replace(",", "");
            var volStr = cols[volIdx].Trim().Trim('"').Replace(",", "");
            double.TryParse(oiStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var oi);
            double.TryParse(volStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var vol);

            if (isNifty)
            {
                if (isPut) { niftyPutOi += oi; niftyPutVol += vol; }
                else if (isCall) { niftyCallOi += oi; niftyCallVol += vol; }
            }
            else
            {
                if (isPut) { bankNiftyPutOi += oi; bankNiftyPutVol += vol; }
                else if (isCall) { bankNiftyCallOi += oi; bankNiftyCallVol += vol; }
            }
        }

        logger.LogInformation(
            "UDiFF raw totals — NIFTY Put OI: {NPO}, Call OI: {NCO}, Put Vol: {NPV}, Call Vol: {NCV} | " +
            "BANKNIFTY Put OI: {BPO}, Call OI: {BCO}, Put Vol: {BPV}, Call Vol: {BCV}",
            niftyPutOi, niftyCallOi, niftyPutVol, niftyCallVol,
            bankNiftyPutOi, bankNiftyCallOi, bankNiftyPutVol, bankNiftyCallVol);

        var niftyPcrOi = ComputeRatio(niftyPutOi, niftyCallOi, "NIFTY OI");
        var niftyPcrVol = ComputeRatio(niftyPutVol, niftyCallVol, "NIFTY Volume");
        var bnfPcrOi = ComputeRatio(bankNiftyPutOi, bankNiftyCallOi, "BANKNIFTY OI");
        var bnfPcrVol = ComputeRatio(bankNiftyPutVol, bankNiftyCallVol, "BANKNIFTY Volume");

        logger.LogInformation(
            "UDiFF bhavcopy PCR — NIFTY OI: {NIO}, Vol: {NV} | BANKNIFTY OI: {BIO}, Vol: {BV}",
            niftyPcrOi, niftyPcrVol, bnfPcrOi, bnfPcrVol);

        return new PrPcrResult(niftyPcrOi, niftyPcrVol, bnfPcrOi, bnfPcrVol);
    }

    private double? ComputeRatio(double putValue, double callValue, string label)
    {
        if (callValue <= 0)
        {
            logger.LogWarning("UDiFF bhavcopy PCR: Call value is zero for {Label}. Put={Put}", label, putValue);
            return null;
        }
        return Math.Round(putValue / callValue, 4);
    }
}