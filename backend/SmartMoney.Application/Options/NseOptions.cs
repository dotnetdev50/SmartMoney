namespace SmartMoney.Application.Options;

public sealed class NseOptions
{
    public string ArchivesBaseUrl { get; set; } = "https://archives.nseindia.com/content/nsccl/";
    public string ParticipantOiTemplate { get; set; } = "fao_participant_oi_{ddMMyyyy}.csv";
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Base URL for FO Bhavcopy ZIP files.
    /// Full URL pattern: {FoBhavCopyBaseUrl}/{YYYY}/{MMM}/fo{DD}{MMM}{YYYY}bhav.csv.zip
    /// </summary>
    public string FoBhavCopyBaseUrl { get; set; } = "https://nsearchives.nseindia.com/content/historical/DERIVATIVES/";

    /// <summary>
    /// Full-history India VIX CSV from NSE archives.
    /// Format: Date,Open,High,Low,Close,Prev Close,Change
    /// </summary>
    public string VixArchiveUrl { get; set; } = "https://nsearchives.nseindia.com/content/indices/hist_vix_data.csv";

    /// <summary>
    /// Base URL for NSE PR (options bhavcopy) ZIP files.
    /// Full URL pattern: {PrBaseUrl}/pr{DDMMYYYY}.zip
    /// These ZIP archives contain a CSV file named pr{DDMMYYYY}.csv with options OI and volume data.
    /// </summary>
    public string PrBaseUrl { get; set; } = "https://nsearchives.nseindia.com/content/fo/";
}