using System.Collections.Generic;

namespace MyFirstMod
{
    /// <summary>
    /// Message-type string constants for Helper.Multiplayer.SendMessage/OnModMessageReceived (see
    /// ModEntry.OnModMessageReceived's dispatch switch). Plan.md section 7 (host-authoritative sync,
    /// pending) documents the full protocol these correspond to; grouped here as named constants rather
    /// than inline string literals so a typo at a call site is a compile error, not a silently-ignored
    /// message.
    /// </summary>
    public static class MultiplayerMessageTypes
    {
        /// <summary>Farmhand -> host: "I'd like to sign this contract." Host is the sole authority that actually creates a FuturesContract; see RequestSignContractMessage.</summary>
        public const string RequestSignContract = "RequestSignContract";

        /// <summary>Host -> everyone (including the original requester): "This contract now exists." See ContractSignedMessage.</summary>
        public const string ContractSigned = "ContractSigned";

        /// <summary>Farmhand -> host: "I clicked this item in my inventory, is there a contract for me to fulfill?" See RequestDeliverMessage.</summary>
        public const string RequestDeliver = "RequestDeliver";

        /// <summary>Host -> requester only: "Yes - here's the FIFO-earliest Pending contract I've tentatively locked for you; consume your item now." See DeliverApprovedMessage.</summary>
        public const string DeliverApproved = "DeliverApproved";

        /// <summary>Host -> requester only: "No eligible contract" (none due today for that item, or the last one was just claimed by someone else). See DeliverRejectedMessage.</summary>
        public const string DeliverRejected = "DeliverRejected";

        /// <summary>Farmhand -> host: "I consumed the item and got paid - please finalize this contract as Fulfilled." See DeliverConfirmedMessage.</summary>
        public const string DeliverConfirmed = "DeliverConfirmed";

        /// <summary>Farmhand -> host: "I was approved, but the item was gone by the time I went to consume it - release your lock." See DeliverFailedMessage.</summary>
        public const string DeliverFailed = "DeliverFailed";

        /// <summary>Host -> everyone: a locked contract was just finalized as Fulfilled. See ContractFulfilledMessage. Deliberately a small targeted update (same pattern as ContractSigned), not the full-state ContractStateSync reserved for defaults/price updates/new-peer catch-up (plan.md section 7 phase 4).</summary>
        public const string ContractFulfilled = "ContractFulfilled";

        /// <summary>Host -> everyone: full-state snapshot (all contracts + price history), sent after any change and to a newly-connected peer. See ContractStateSyncMessage.</summary>
        public const string ContractStateSync = "ContractStateSync";
    }

    /// <summary>
    /// Farmhand -> host (plan.md section 7, signing flow, chosen design "方案B请求-审批式"): a request to
    /// sign a new futures contract. The host is the sole authority that actually creates the
    /// FuturesContract, prices it, dates it, and pays the margin - this message only carries what the host
    /// needs to decide that (which item, which due-date rule, who's asking); the requester does NOT create
    /// any local state before receiving the corresponding ContractSignedMessage back.
    ///
    /// Deliberately does NOT carry AgreedPrice or a computed due-date value - see ContractDueDateKind's own
    /// doc comment (DateHelper.cs). This keeps "host is the sole authority" true with no exception: nothing
    /// about price or date is ever trusted from the wire, only recomputed by the host from its own game state.
    /// </summary>
    public class RequestSignContractMessage
    {
        public string ItemId { get; set; }
        public ContractDueDateKind DueDateKind { get; set; }
        public long RequesterPlayerId { get; set; }
    }

    /// <summary>Host -> everyone (plan.md section 7): a contract was just created. Wraps the same DTO shape ContractManager.ToSaveData already produces per-contract, so no separate field list to keep in sync.</summary>
    public class ContractSignedMessage
    {
        public FuturesContractSaveData Contract { get; set; }
    }

    /// <summary>Farmhand -> host (plan.md section 7, delivery flow, chosen design "方案1预先锁定式验证"): "I clicked this item in my inventory - is there a contract for me to fulfill?" No local inventory/money change has happened yet when this is sent.</summary>
    public class RequestDeliverMessage
    {
        public string ItemId { get; set; }
        public long RequesterPlayerId { get; set; }
    }

    /// <summary>Host -> requester only: the host found and tentatively locked the FIFO-earliest eligible contract. The requester should now actually consume 1 item and credit AgreedPrice, then reply with DeliverConfirmedMessage (or DeliverFailedMessage on failure).</summary>
    public class DeliverApprovedMessage
    {
        public string ContractId { get; set; }
        public int AgreedPrice { get; set; }
    }

    /// <summary>Host -> requester only: no eligible contract exists for ItemId (none due today, or the last one was already claimed by someone else). Requester should show a "nothing to deliver" message and must not touch its inventory/money.</summary>
    public class DeliverRejectedMessage
    {
        public string ItemId { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Farmhand -> host: the approved delivery succeeded locally (item consumed, money credited) - host should finalize the locked contract as Fulfilled and broadcast the updated state.</summary>
    public class DeliverConfirmedMessage
    {
        public string ContractId { get; set; }
    }

    /// <summary>Farmhand -> host: the approved delivery could NOT be completed locally (e.g. the item was gone by the time of the defensive re-check) - host should release its tentative lock back to Pending so the next requester can claim it.</summary>
    public class DeliverFailedMessage
    {
        public string ContractId { get; set; }
    }

    /// <summary>Host -> everyone: the identified contract was just finalized as Fulfilled. Carries only the id - every client should already know the contract's other fields from the earlier ContractSignedMessage broadcast, so there's nothing else to send.</summary>
    public class ContractFulfilledMessage
    {
        public string ContractId { get; set; }
    }

    /// <summary>
    /// Host -> everyone (plan.md section 7, chosen design "全量同步"): the complete authoritative state,
    /// sent after any change (sign/fulfill/default/daily price update) and once to any newly-connected peer
    /// (via PeerConnected). Reuses the same DTOs ContractManager.ToSaveData/ToMarketPriceSaveData already
    /// produce for save-file persistence - one source of truth for "what does a full snapshot look like",
    /// not a second parallel shape to keep in sync.
    /// </summary>
    public class ContractStateSyncMessage
    {
        public List<FuturesContractSaveData> Contracts { get; set; }
        public MarketPriceSaveData MarketPrices { get; set; }
    }
}
