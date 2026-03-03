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
    public string FoBhavCopyBaseUrl { get; set; } = "https://archives.nseindia.com/content/historical/DERIVATIVES/";

    /// <summary>
    /// Full-history India VIX CSV from NSE archives.
    /// Format: Date,Open,High,Low,Close,Prev Close,Change
    /// </summary>
    public string VixArchiveUrl { get; set; } = "https://archives.nseindia.com/content/indices/hist_vix_data.csv";
}