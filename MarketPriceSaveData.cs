using System.Collections.Generic;

namespace MyFirstMod
{
    /// <summary>
    /// Plain, Newtonsoft-friendly DTO for Helper.Data.WriteSaveData/ReadSaveData holding PriceModel's daily
    /// output (plan.md section 6) - see ContractManager.ToMarketPriceSaveData/LoadMarketPriceSaveData.
    /// Same rationale as FuturesContractSaveData: a plain class with settable properties and a parameterless
    /// constructor, not PriceModel.PriceRegime itself (a readonly struct), so Newtonsoft's
    /// constructor/property matching has something it can round-trip reliably.
    /// </summary>
    public class MarketPriceSaveData
    {
        public List<int> BeerPriceHistory { get; set; } = new List<int>();
        public List<int> PaleAlePriceHistory { get; set; } = new List<int>();

        public int RegimeLastAdvancedDayOfYear { get; set; }
        public bool RegimeJumpTriggeredToday { get; set; }
        public double RegimeJumpSignedMagnitudeToday { get; set; }
    }
}
