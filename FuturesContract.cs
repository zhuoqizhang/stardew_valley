using System;
using StardewModdingAPI.Utilities;

namespace MyFirstMod
{
    public enum ContractStatus
    {
        Pending,
        Fulfilled,
        Defaulted
    }

    /// <summary>A single forward contract for a fixed quantity (always 1) of an item. See design doc section 2.1.</summary>
    public class FuturesContract
    {
        public string ContractId { get; }
        public string ItemId { get; }
        public int AgreedPrice { get; }

        /// <summary>
        /// The margin Pierre pays the player at signing (design doc 2.1/5.5), forfeited back on default
        /// (see ContractManager.SettleDefaults). Computed via SettlementMath.ComputeMargin - the same
        /// function ContractManager.SignContract's payout uses - so this field and the amount actually
        /// paid out can't drift apart.
        /// </summary>
        public int Margin { get; }
        public SDate SignedDate { get; }
        public SDate DueDate { get; }
        public ContractStatus Status { get; set; } = ContractStatus.Pending;

        /// <summary>Id of the Quest tracking this contract in the player's quest log; matches Quest.id.Value.</summary>
        public string QuestId { get; set; }

        public FuturesContract(string itemId, int agreedPrice, SDate signedDate, SDate dueDate)
            : this(Guid.NewGuid().ToString(), itemId, agreedPrice, signedDate, dueDate, ContractStatus.Pending, questId: null)
        {
        }

        /// <summary>
        /// Reconstructs a contract with a known identity - used only when restoring from save data
        /// (see ContractManager.LoadFromSaveData), where ContractId/Status/QuestId must be preserved exactly
        /// rather than regenerated. SignedDate/DueDate are still real SDate instances here; the save data
        /// itself stores them as plain DaysSinceStart ints and converts back via SDate.FromDaysSinceStart
        /// before calling this constructor, since SDate has no parameterless constructor Newtonsoft could
        /// round-trip on its own.
        /// </summary>
        public FuturesContract(string contractId, string itemId, int agreedPrice, SDate signedDate, SDate dueDate, ContractStatus status, string questId)
        {
            ContractId = contractId;
            ItemId = itemId;
            AgreedPrice = agreedPrice;
            Margin = SettlementMath.ComputeMargin(agreedPrice);
            SignedDate = signedDate;
            DueDate = dueDate;
            Status = status;
            QuestId = questId;
        }
    }
}
