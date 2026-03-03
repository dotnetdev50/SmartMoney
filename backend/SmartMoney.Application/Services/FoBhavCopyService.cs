using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMoney.Application.Options;
using System.Globalization;
using System.IO.Compression;

namespace SmartMoney.Application.Services;

/// <summary>
/// Downloads the NSE FO Bhavcopy ZIP for a given date, parses the CSV inside,
/// and computes the Put-Call Ratio (PCR) for NIFTY index options.
/// PCR = Sum(OPEN_INT for PE) / Sum(OPEN_INT for CE)
/// </summary>
public sealed class FoBhavCopyService(
    HttpClient http,
    IOptions<NseOptions> options,
    ILogger<FoBhavCopyService> logger)
{
    private readonly NseOptions _opt = options.Value;

    private const string NiftyInstrument = "OPTIDX";
    private const string NiftySymbol = "NIFTY";

    /// <summary>
    /// Fetches PCR for the given date. Returns null if data unavailable (holiday/404/parse error).
    /// </summary>
    public async Task<double?> FetchPcrAsync(DateTime date, CancellationToken ct)
    {
        var url = BuildBhavCopyUrl(date);
        logger.LogInformation("Fetching FO bhavcopy PCR for {Date} from {Url}", date.ToString("yyyy-MM-dd"), url);

        try
        {
            await using var zipStream = await DownloadAsync(url, ct);
            return await ParsePcrFromZipAsync(zipStream, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogWarning("FO bhavcopy not found (404) for {Date} — likely holiday or data not yet published.", date.ToString("yyyy-MM-dd"));
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to fetch/parse FO bhavcopy for {Date}: {Msg}", date.ToString("yyyy-MM-dd"), ex.Message);
            return null;
        }
    }

    private string BuildBhavCopyUrl(DateTime date)
    {
        // Example: .../DERIVATIVES/2026/FEB/fo27FEB2026bhav.csv.zip
        var dd = date.ToString("dd", CultureInfo.InvariantCulture);
        var mmm = date.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant();
        var yyyy = date.ToString("yyyy", CultureInfo.InvariantCulture);
        var file = $"fo{dd}{mmm}{yyyy}bhav.csv.zip";
        return $"{_opt.FoBhavCopyBaseUrl.TrimEnd('/')}/{yyyy}/{mmm}/{file}";
    }

    private async Task<Stream> DownloadAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
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

    private async Task<double?> ParsePcrFromZipAsync(Stream zipStream, CancellationToken ct)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            logger.LogWarning("No CSV entry found in bhavcopy ZIP.");
            return null;
        }

        await using var csvStream = entry.Open();
        return await ComputePcrFromCsvAsync(csvStream, ct);
    }

    private async Task<double?> ComputePcrFromCsvAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream);

        // First line = header
        var headerLine = await reader.ReadLineAsync(ct);
        if (headerLine is null) return null;

        var headers = headerLine.Split(',')
            .Select(h => h.Trim().Trim('"').ToUpperInvariant())
            .ToList();

        var instrIdx   = headers.IndexOf("INSTRUMENT");
        var symbolIdx  = headers.IndexOf("SYMBOL");
        var optTypeIdx = headers.IndexOf("OPTION_TYP");
        var openIntIdx = headers.IndexOf("OPEN_INT");

        if (instrIdx < 0 || symbolIdx < 0 || optTypeIdx < 0 || openIntIdx < 0)
        {
            logger.LogWarning("Bhavcopy CSV missing required columns (INSTRUMENT/SYMBOL/OPTION_TYP/OPEN_INT). Found: {H}", headerLine);
            return null;
        }

        double putOi = 0, callOi = 0;
        var maxIdx = Math.Max(openIntIdx, Math.Max(instrIdx, Math.Max(symbolIdx, optTypeIdx)));

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = line.Split(',');
            if (cols.Length <= maxIdx) continue;

            var instr   = cols[instrIdx].Trim().Trim('"');
            var symbol  = cols[symbolIdx].Trim().Trim('"');
            var optType = cols[optTypeIdx].Trim().Trim('"');

            if (!instr.Equals(NiftyInstrument, StringComparison.OrdinalIgnoreCase)) continue;
            if (!symbol.Equals(NiftySymbol, StringComparison.OrdinalIgnoreCase)) continue;

            var oiStr = cols[openIntIdx].Trim().Trim('"').Replace(",", "");
            if (!double.TryParse(oiStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var oi)) continue;

            if (optType.Equals("PE", StringComparison.OrdinalIgnoreCase))
                putOi += oi;
            else if (optType.Equals("CE", StringComparison.OrdinalIgnoreCase))
                callOi += oi;
        }

        if (callOi <= 0)
        {
            logger.LogWarning("Call OI is zero or negative — cannot compute PCR. Put OI={PutOi}, Call OI={CallOi}", putOi, callOi);
            return null;
        }

        var pcr = Math.Round(putOi / callOi, 4);
        logger.LogInformation("PCR computed: {Pcr} (Put OI={PutOi:N0}, Call OI={CallOi:N0})", pcr, putOi, callOi);
        return pcr;
    }
}
