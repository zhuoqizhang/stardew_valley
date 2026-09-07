namespace MyFirstMod
{
    /// <summary>
    /// Plain, Newtonsoft-friendly mirror of <see cref="FuturesContract"/> for
    /// Helper.Data.WriteSaveData/ReadSaveData (see ContractManager.ToSaveData/LoadFromSaveData).
    ///
    /// FuturesContract itself isn't a safe shape to hand to those methods directly: it has no
    /// parameterless constructor, its properties are read-only, and two of them are
    /// StardewModdingAPI.Utilities.SDate - a class whose only public constructors require a valid
    /// day/season/year and which SMAPI itself marks up for Newtonsoft's constructor-matching, not the
    /// default-constructor + settable-properties pattern IDataHelper's own docs ask for. Rather than rely on
    /// that working, dates here are stored as SDate.DaysSinceStart (a plain int) and converted back through
    /// the public SDate.FromDaysSinceStart factory when restoring.
    /// </summary>
    public class FuturesContractSaveData
    {
        public string ContractId { get; set; }
        public string ItemId { get; set; }
        public int AgreedPrice { get; set; }
        public int SignedDateDaysSinceStart { get; set; }
        public int DueDateDaysSinceStart { get; set; }
        public ContractStatus Status { get; set; }
        public string QuestId { get; set; }
    }
}
