namespace SmartMoney.Contracts.Market
{
    public sealed class MarketTodayResponse
    {
        public string Index { get; set; } = "NIFTY";
        public string Date { get; set; } = "";
        public string AsOfDate { get; set; } = "";
        public double Final_Score { get; set; }
        public string Bias_Label { get; set; } = "";
        public string Strength { get; set; } = "";
        public string Regime { get; set; } = "";
        public double Shock_Score { get; set; }

        public List<ParticipantBiasDto> Participants { get; set; } = new();
        public string Explanation { get; set; } = "";
    }

    public sealed class ParticipantBiasDto
    {
        public string Name { get; set; } = "";
        public double Bias { get; set; }
        public string Label { get; set; } = "";
    }
}