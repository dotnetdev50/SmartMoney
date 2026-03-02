namespace SmartMoney.Application.Options;

public sealed class NseOptions
{
    public string ArchivesBaseUrl { get; set; } = "https://archives.nseindia.com/content/nsccl/";
    public string ParticipantOiTemplate { get; set; } = "fao_participant_oi_{ddMMyyyy}.csv";
    public int RequestTimeoutSeconds { get; set; } = 30;
}