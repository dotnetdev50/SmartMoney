namespace SmartMoney.Application.Options;

public sealed class NseOptions
{
    public string ArchivesBaseUrl { get; set; } = "https://archives.nseindia.com/content/nsccl/";
    public string ParticipantOiTemplate { get; set; } = "fao_participant_oi_{ddMMyyyy}.csv";
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// NSE Reports API used to download the F&amp;O UDiFF Common Bhavcopy Final ZIP (primary PCR source).
    /// Full URL: {BhavCopyReportsApiUrl}&amp;date=dd-MMM-yyyy&amp;type=equity&amp;mode=single
    /// Downloaded ZIP  : BhavCopy_NSE_FO_0_0_0_{YYYYMMDD}_F_0000.zip
    /// CSV inside ZIP  : BhavCopy_NSE_FO_0_0_0_{YYYYMMDD}_F_0000.csv
    /// Key columns     : TckrSymb, Optn (CE/PE), OpnIntrst, TtlTradgV
    /// Requires NSE session cookies (prime homepage first).
    /// </summary>
    public string BhavCopyReportsApiUrl { get; set; } =
        "https://www.nseindia.com/api/reports?archives=%5B%7B%22name%22%3A%22F%26O%20-%20UDiFF%20Common%20Bhavcopy%20Final%20(zip)%22%2C%22type%22%3A%22archives%22%2C%22category%22%3A%22derivatives%22%2C%22section%22%3A%22equity%22%7D%5D";

    /// <summary>
    /// Base URL for the legacy FO Bhavcopy (last-resort fallback only).
    /// Full URL pattern: {FoBhavCopyBaseUrl}/{YYYY}/{MMM}/fo{DD}{MMM}{YYYY}bhav.csv.zip
    /// </summary>
    public string FoBhavCopyBaseUrl { get; set; } = "https://nsearchives.nseindia.com/content/historical/DERIVATIVES/";

    /// <summary>
    /// NSE VIX historical data API (primary VIX source).
    /// Full URL: {VixApiBaseUrl}?from=dd-MM-yyyy&amp;to=dd-MM-yyyy&amp;csv=true
    /// Typical usage is a range window (for example, last 30 days) and then pick target date row.
    /// Response columns: Date,Open,High,Low,Close,Prev Close,Change,%Change
    /// Date format in response: DD-MMM-YYYY uppercase (e.g. 06-MAR-2026).
    /// Requires NSE session cookies (prime homepage first).
    /// </summary>
    public string VixApiBaseUrl { get; set; } = "https://www.nseindia.com/api/historicalOR/vixhistory";

    /// <summary>
    /// Full-history India VIX CSV from NSE archives (fallback when VIX API is unavailable).
    /// Date format in file: DD-MMM-YYYY (e.g. 05-Mar-2026). Columns: Date,Open,High,Low,Close,...
    /// </summary>
    public string VixArchiveUrl { get; set; } = "https://nsearchives.nseindia.com/content/indices/hist_vix_data.csv";

    /// <summary>
    /// Base URL for the NSE F&amp;O daily bhavcopy ZIP (op-bhavcopy, secondary PCR fallback).
    /// ZIP filename pattern : fo{DDMMYYYY}.zip  (e.g. fo05032026.zip — 8-digit year)
    /// CSV inside ZIP       : op{DDMMYY}.csv    (e.g. op040326.csv  — 6-digit year)
    /// Source page          : https://www.nseindia.com/all-reports-derivatives
    /// Requires NSE session cookies (prime homepage first).
    /// </summary>
    public string FoBhavZipBaseUrl { get; set; } = "https://nsearchives.nseindia.com/content/fo/";

    /// <summary>
    /// Base URL for NSE PR (options bhavcopy) ZIP files (tertiary PCR fallback).
    /// ZIP filename pattern : PR{DDMMYY}.zip   (e.g. PR040326.zip — 6-digit year)
    /// CSV inside ZIP       : pr{DDMMYYYY}.csv (e.g. pr04032026.csv — 8-digit year)
    /// Source page          : https://www.nseindia.com/all-reports-derivatives
    /// </summary>
    public string PrBaseUrl { get; set; } = "https://nsearchives.nseindia.com/content/fo/";
}