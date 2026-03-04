namespace SmartMoney.Domain.Entities;

/// <summary>
/// Holds Put-Call Ratio values derived from the NSE PR (options bhavcopy) file.
/// Both Open-Interest PCR and Volume PCR are provided for NIFTY and BANKNIFTY.
/// </summary>
/// <param name="NiftyPcrOi">PCR (OI) for NIFTY = Total Put OI / Total Call OI.</param>
/// <param name="NiftyPcrVolume">PCR (Volume) for NIFTY = Total Put Volume / Total Call Volume.</param>
/// <param name="BankniftyPcrOi">PCR (OI) for BANKNIFTY = Total Put OI / Total Call OI.</param>
/// <param name="BankniftyPcrVolume">PCR (Volume) for BANKNIFTY = Total Put Volume / Total Call Volume.</param>
public sealed record PrPcrResult(
    double? NiftyPcrOi,
    double? NiftyPcrVolume,
    double? BankniftyPcrOi,
    double? BankniftyPcrVolume
);
