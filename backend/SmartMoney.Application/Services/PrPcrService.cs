using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMoney.Application.Options;
using SmartMoney.Domain.Entities;
using System.Globalization;
using System.IO.Compression;

namespace SmartMoney.Application.Services;

/// <summary>
/// Downloads the NSE PR (options bhavcopy) ZIP for a given date, parses the CSV inside,
/// and computes Put-Call Ratios for NIFTY and BANKNIFTY.
///
/// Source URL pattern:
///   {PrBaseUrl}/pr{DDMMYYYY}.zip  (e.g. https://nsearchives.nseindia.com/content/fo/pr04032026.zip)
///
/// The ZIP contains a file named pr{DDMMYYYY}.csv with columns:
///   SYMBOL, EXPIRY_DT, OPTION_TYP, STRIKE_PR, OPEN, HIGH, LOW, CLOSE, SETTLE_PR,
///   CONTRACTS, VAL_INLAKH, OPEN_INT, CHG_IN_OI, TIMESTAMP
///
/// Formulae:
///   PCR (OI)     = Sum(OPEN_INT  for PE) / Sum(OPEN_INT  for CE)
///   PCR (Volume) = Sum(CONTRACTS for PE) / Sum(CONTRACTS for CE)
/// </summary>
public sealed class PrPcrService(
    HttpClient http,
    IOptions<NseOptions> options,
    ILogger<PrPcrService> logger)
{
    private readonly NseOptions _opt = options.Value;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    /// <summary>
    /// Fetches PCR values for the given date.
    /// Returns a <see cref="PrPcrResult"/> with individual nulls for any symbol whose data
    /// could not be computed (holiday, 404, or insufficient rows).
    /// Never throws.
    /// </summary>
    public async Task<PrPcrResult?> FetchPcrAsync(DateTime date, CancellationToken ct)
    {
        var url = BuildPrUrl(date);
        logger.LogInformation("Fetching NSE PR file PCR for {Date} from {Url}", date.ToString("yyyy-MM-dd"), url);

        try
        {
            await using var zipStream = await DownloadAsync(url, ct);
            return await ParsePcrFromZipAsync(zipStream, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogWarning("NSE PR file not found (404) for {Date} — likely holiday or data not yet published.", date.ToString("yyyy-MM-dd"));
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to fetch/parse NSE PR file for {Date}: {Msg}", date.ToString("yyyy-MM-dd"), ex.Message);
            return null;
        }
    }

    private string BuildPrUrl(DateTime date)
    {
        // Filename format: pr{DDMMYYYY}.zip  e.g. pr04032026.zip
        var dd   = date.ToString("dd",   CultureInfo.InvariantCulture);
        var mm   = date.ToString("MM",   CultureInfo.InvariantCulture);
        var yyyy = date.ToString("yyyy", CultureInfo.InvariantCulture);
        var file = $"pr{dd}{mm}{yyyy}.zip";
        return $"{_opt.PrBaseUrl.TrimEnd('/')}/{file}";
    }

    private async Task<Stream> DownloadAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/zip,application/octet-stream,*/*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        req.Headers.TryAddWithoutValidation("Referer", "https://www.nseindia.com/");

        var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var ms = new MemoryStream();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        await s.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    private async Task<PrPcrResult?> ParsePcrFromZipAsync(Stream zipStream, CancellationToken ct)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            logger.LogWarning("No CSV entry found in NSE PR ZIP.");
            return null;
        }

        logger.LogInformation("Parsing PR CSV entry: {Name}", entry.Name);
        await using var csvStream = entry.Open();
        return await ComputePcrFromCsvAsync(csvStream, ct);
    }

    private async Task<PrPcrResult?> ComputePcrFromCsvAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream);

        // First line = header
        var headerLine = await reader.ReadLineAsync(ct);
        if (headerLine is null) return null;

        var headers = headerLine.Split(',')
            .Select(h => h.Trim().Trim('"').ToUpperInvariant())
            .ToList();

        var symbolIdx    = headers.IndexOf("SYMBOL");
        var optTypeIdx   = headers.IndexOf("OPTION_TYP");
        var contractsIdx = headers.IndexOf("CONTRACTS");
        var openIntIdx   = headers.IndexOf("OPEN_INT");

        if (symbolIdx < 0 || optTypeIdx < 0 || contractsIdx < 0 || openIntIdx < 0)
        {
            logger.LogWarning("PR CSV missing required columns (SYMBOL/OPTION_TYP/CONTRACTS/OPEN_INT). Found: {H}", headerLine);
            return null;
        }

        var maxIdx = Math.Max(openIntIdx, Math.Max(symbolIdx, Math.Max(optTypeIdx, contractsIdx)));

        // Accumulators per symbol
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

            var symbol  = cols[symbolIdx].Trim().Trim('"').ToUpperInvariant();
            var optType = cols[optTypeIdx].Trim().Trim('"').ToUpperInvariant();

            bool isNifty      = symbol.Equals("NIFTY",     StringComparison.OrdinalIgnoreCase);
            bool isBankNifty  = symbol.Equals("BANKNIFTY", StringComparison.OrdinalIgnoreCase);

            if (!isNifty && !isBankNifty) continue;

            var oiStr  = cols[openIntIdx].Trim().Trim('"').Replace(",", "");
            var volStr = cols[contractsIdx].Trim().Trim('"').Replace(",", "");

            double.TryParse(oiStr,  NumberStyles.Any, CultureInfo.InvariantCulture, out var oi);
            double.TryParse(volStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var vol);

            bool isPut  = optType.Equals("PE", StringComparison.OrdinalIgnoreCase);
            bool isCall = optType.Equals("CE", StringComparison.OrdinalIgnoreCase);

            if (isNifty)
            {
                if (isPut)  { niftyPutOi  += oi; niftyPutVol  += vol; }
                else if (isCall) { niftyCallOi += oi; niftyCallVol += vol; }
            }
            else // BANKNIFTY
            {
                if (isPut)  { bankNiftyPutOi  += oi; bankNiftyPutVol  += vol; }
                else if (isCall) { bankNiftyCallOi += oi; bankNiftyCallVol += vol; }
            }
        }

        var niftyPcrOi     = ComputeRatio(niftyPutOi,     niftyCallOi,     "NIFTY OI");
        var niftyPcrVol    = ComputeRatio(niftyPutVol,    niftyCallVol,    "NIFTY Volume");
        var bnfPcrOi       = ComputeRatio(bankNiftyPutOi, bankNiftyCallOi, "BANKNIFTY OI");
        var bnfPcrVol      = ComputeRatio(bankNiftyPutVol, bankNiftyCallVol, "BANKNIFTY Volume");

        logger.LogInformation(
            "PR PCR — NIFTY OI: {NIO}, Vol: {NV} | BANKNIFTY OI: {BIO}, Vol: {BV}",
            niftyPcrOi, niftyPcrVol, bnfPcrOi, bnfPcrVol);

        return new PrPcrResult(niftyPcrOi, niftyPcrVol, bnfPcrOi, bnfPcrVol);
    }

    private double? ComputeRatio(double putValue, double callValue, string label)
    {
        if (callValue <= 0)
        {
            logger.LogWarning("PR PCR: Call value is zero for {Label} — skipping. Put={Put}", label, putValue);
            return null;
        }
        return Math.Round(putValue / callValue, 4);
    }
}
