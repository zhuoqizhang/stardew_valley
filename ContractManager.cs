using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Quests;

namespace MyFirstMod
{
    /// <summary>
    /// Store of futures contracts, and owner of the player's quest-log sync for those contracts.
    /// Persistence to/from the save file goes through ToSaveData/LoadFromSaveData, which ModEntry calls
    /// on the Saving/SaveLoaded events; this class itself doesn't know about SMAPI's save-data API.
    /// </summary>
    public class ContractManager
    {
        // Tradable items; shared here so other classes don't each hardcode the raw id strings.
        public const string BeerItemId = "346";

        // Confirmed via Content/Strings/Objects.zh-CN and Content/Data/Objects (loaded directly through
        // MonoGame's ContentManager, not guessed): the object whose zh-CN PaleAle_Name is "淡啤酒" is
        // Data/Objects entry "303" (English name "Pale Ale", Category -26, same artisan-goods category as
        // Beer).
        public const string PaleAleItemId = "303";

        private readonly IMonitor monitor;
        private readonly IMultiplayerHelper multiplayerHelper;

        public List<FuturesContract> Contracts { get; } = new List<FuturesContract>();

        /// <summary>
        /// Fires whenever a signed contract is applied to local state (plan.md section 7) - either because
        /// this client is the host that just authoritatively created it (see SignContractAsHost) or because
        /// a ContractSignedMessage broadcast was received (see ApplyContractSignedFromNetwork). FuturesMenu
        /// subscribes to this while open to clear its "waiting for host" UI state and show the confirmed
        /// price/margin, for both the host's own (synchronous) and a farmhand's (asynchronous) signing flow.
        /// </summary>
        public event Action<FuturesContract> ContractSignedApplied;

        /// <summary>
        /// Host-only, purely in-memory bookkeeping (plan.md section 7, delivery flow, "方案1预先锁定式验证"):
        /// ContractIds currently reserved for a farmhand's in-flight delivery request, between
        /// TryLockNextDeliverableContractAsHost approving one and the matching DeliverConfirmed/DeliverFailed
        /// releasing it. Deliberately NOT a FuturesContract field or a new ContractStatus value - a locked
        /// contract's persisted Status stays Pending the whole time, so this "处理中" state can never leak
        /// into FuturesContractSaveData/the save file even by accident: there is no code path connecting
        /// this set to ToSaveData at all. A lock that outlives its request (host crash, farmhand disconnect
        /// mid-flow) simply vanishes on next process start, which is the correct behavior for a lock that
        /// should never survive a save/reload anyway.
        /// </summary>
        private readonly HashSet<string> lockedContractIds = new HashSet<string>();

        /// <summary>Fires on the requester's own client when the host approves a delivery request. See TryLockNextDeliverableContractAsHost/HandleDeliverRequestAsHost.</summary>
        public event Action<DeliverApprovedMessage> DeliverApprovedReceived;

        /// <summary>Fires on the requester's own client when the host rejects a delivery request (nothing eligible to deliver). See HandleDeliverRequestAsHost.</summary>
        public event Action<DeliverRejectedMessage> DeliverRejectedReceived;

        /// <summary>
        /// Default-notice mail bodies this session has queued: key = a Data/mail id passed to
        /// Game1.addMailForTomorrow (see QueueDefaultMail), value = the exact letter text to publish under
        /// that id. ModEntry's Content.AssetRequested handler copies this whole dictionary into Data/mail
        /// every time that asset (re)loads. Entries must land here (and the asset must be invalidated)
        /// before - or in the same tick as - the corresponding key is ever added to the player's
        /// mailbox/mailForTomorrow: GameLocation.mailbox() looks the key up in Data/mail and, on a miss,
        /// silently marks the mail read and discards it without showing anything, so there's no user-visible
        /// symptom if the ordering is wrong.
        ///
        /// Deliberately session-scoped (lives as long as this ContractManager instance, i.e. the whole game
        /// process) rather than cleared per save load: a key already durably added to a save's
        /// mailbox/mailForTomorrow before the player reloads or switches saves would otherwise lose its
        /// body text and hit exactly the silent-discard failure above. Leftover entries from a different
        /// save sitting here unused are harmless - Data/mail tolerates extra keys nobody's mailbox
        /// references.
        /// </summary>
        public Dictionary<string, string> PendingDefaultMail { get; } = new Dictionary<string, string>();

        public ContractManager(IMonitor monitor, IMultiplayerHelper multiplayerHelper)
        {
            this.monitor = monitor;
            this.multiplayerHelper = multiplayerHelper;
        }

        /// <summary>
        /// Host-only authoritative signing (plan.md section 7, "方案B请求-审批式"): computes AgreedPrice
        /// (GetNativeSellPrice) and DueDate (ResolveDueDate) itself from the host's own game state - never
        /// from client input - creates the contract, pays the margin (design doc 5.1/5.5: Pierre pays the
        /// margin to the player at signing), applies it to local state via ApplyContractSignedFromNetwork
        /// (the single method that actually mutates Contracts/the quest log, shared with every other
        /// client's receive path), then broadcasts the result to everyone. Callers (FuturesMenu's own click
        /// handler when Context.IsMainPlayer, or ModEntry's RequestSignContract message handler) are
        /// responsible for only calling this on the host - it does not re-check Context.IsMainPlayer itself,
        /// matching this codebase's existing pattern of gating multiplayer authority at the call site (see
        /// ModEntry's OnDayEnding/OnDayStarted).
        /// </summary>
        public void SignContractAsHost(string itemId, ContractDueDateKind dueDateKind, long requesterPlayerId)
        {
            SDate signedDate = SDate.Now();
            SDate dueDate = DateHelper.ResolveDueDate(dueDateKind, signedDate);
            int agreedPrice = GetNativeSellPrice(itemId);

            FuturesContract contract = new FuturesContract(itemId, agreedPrice, signedDate, dueDate);

            // Design doc 5.1/5.5: Pierre pays the margin to the player, not the other way around - this is
            // a payout, not a charge, so unlike a purchase there's no balance check to fail. Uses
            // contract.Margin (not a separate SettlementMath.ComputeMargin(agreedPrice) call) so the amount
            // actually paid out can never drift from the value SettleDefaults later forfeits back. Under a
            // shared wallet (plan.md section 7), Game1.player.Money on the HOST's own client already lands
            // in the one shared FarmerTeam.money pool regardless of who requested the contract - no
            // cross-player money API is needed (see plan.md 7's Money decompile findings).
            Game1.player.Money += contract.Margin;

            FuturesContractSaveData contractDto = ToSaveDataEntry(contract);
            ApplyContractSignedFromNetwork(contractDto);

            multiplayerHelper.SendMessage(
                new ContractSignedMessage { Contract = contractDto },
                MultiplayerMessageTypes.ContractSigned,
                playerIDs: null);

            monitor?.Log($"ContractManager: [HOST] signed contract [{contract.ContractId}] for requester {requesterPlayerId}, item={itemId} price={agreedPrice}G due={DateHelper.FormatChineseDate(dueDate)}. Broadcast ContractSigned to all clients. Total contracts: {Contracts.Count}", LogLevel.Info);
        }

        /// <summary>
        /// Farmhand-side entry point (plan.md section 7): sends a request to the host and returns
        /// immediately without touching any local state - no contract is created, no money changes hands,
        /// here. The eventual result (or lack thereof, if the host never responds) arrives later as a
        /// ContractSignedMessage broadcast, applied via ApplyContractSignedFromNetwork and surfaced through
        /// ContractSignedApplied.
        /// </summary>
        public void RequestSignContract(string itemId, ContractDueDateKind dueDateKind)
        {
            RequestSignContractMessage message = new RequestSignContractMessage
            {
                ItemId = itemId,
                DueDateKind = dueDateKind,
                RequesterPlayerId = Game1.player.UniqueMultiplayerID
            };

            multiplayerHelper.SendMessage(message, MultiplayerMessageTypes.RequestSignContract, playerIDs: null);

            monitor?.Log($"ContractManager: requested sign contract item={itemId} dueDateKind={dueDateKind} from host.", LogLevel.Info);
        }

        /// <summary>
        /// Applies a signed contract to local state - called on EVERY client, including the host itself (see
        /// SignContractAsHost), never only on farmhands. This is the single place Contracts/the quest log
        /// actually get mutated for a newly-signed contract, so the host's own local application and every
        /// other client's received-broadcast application can never drift into two different code paths.
        ///
        /// Idempotent by ContractId: safe to call more than once for the same contract (e.g. if
        /// Helper.Multiplayer's own self-delivery semantics for a playerIDs:null broadcast ever include
        /// echoing the message back to its own sender - not something this code depends on either way being
        /// true, since this check makes a duplicate a no-op rather than a double-add).
        /// </summary>
        public void ApplyContractSignedFromNetwork(FuturesContractSaveData contractDto)
        {
            if (Contracts.Any(c => c.ContractId == contractDto.ContractId))
            {
                return;
            }

            FuturesContract contract = new FuturesContract(
                contractDto.ContractId,
                contractDto.ItemId,
                contractDto.AgreedPrice,
                SDate.FromDaysSinceStart(contractDto.SignedDateDaysSinceStart),
                SDate.FromDaysSinceStart(contractDto.DueDateDaysSinceStart),
                contractDto.Status,
                contractDto.QuestId);

            Contracts.Add(contract);
            SyncQuestGroupAfterSigning(contract);

            monitor?.Log($"ContractManager: applied signed contract [{contract.ContractId}] item={contract.ItemId} price={contract.AgreedPrice}G due={DateHelper.FormatChineseDate(contract.DueDate)} to local state. Total contracts: {Contracts.Count}", LogLevel.Info);

            ContractSignedApplied?.Invoke(contract);
        }

        /// <summary>
        /// Design doc 5.1's quest-log sync, extracted so both SignContractAsHost's local application (via
        /// ApplyContractSignedFromNetwork) and every other client's received-broadcast application share the
        /// exact same quest-creation logic: contracts that share the same item and due date are merged into
        /// one quest entry showing "xN" rather than spawning one quest per contract. Recomputes QuestId
        /// itself from contract.ItemId/DueDate rather than trusting whatever was on the wire (same
        /// "recompute deterministic values locally" principle as ResolveDueDate/GetNativeSellPrice) -
        /// harmless since it's a pure function of those two fields either way.
        /// </summary>
        private void SyncQuestGroupAfterSigning(FuturesContract contract)
        {
            string groupQuestId = $"MyFirstMod.Futures.{contract.ItemId}.{contract.DueDate.DaysSinceStart}";
            contract.QuestId = groupQuestId;
            int countInGroup = CountPending(contract.ItemId, contract.DueDate);
            string dateText = DateHelper.FormatChineseDate(contract.DueDate);
            string itemDisplayName = GetItemDisplayName(contract.ItemId);

            Quest quest = Game1.player.questLog.FirstOrDefault(q => q.id.Value == groupQuestId);
            if (quest == null)
            {
                quest = new Quest();
                quest.id.Value = groupQuestId;
                quest.accepted.Value = true;
                quest.questType.Value = Quest.type_basic;
                Game1.player.questLog.Add(quest);
            }

            quest.questTitle = $"{itemDisplayName}期货 - {dateText}交割";
            quest.questDescription = BuildQuestDescription(itemDisplayName, dateText, countInGroup);
            quest.daysLeft.Value = Math.Max(0, contract.DueDate.DaysSinceStart - contract.SignedDate.DaysSinceStart);
        }

        /// <summary>Counts still-open contracts for the same item and due date, e.g. to show a merged "xN" quest-log entry.</summary>
        public int CountPending(string itemId, SDate dueDate)
        {
            return Contracts.Count(c => c.ItemId == itemId && c.DueDate.Equals(dueDate) && c.Status == ContractStatus.Pending);
        }

        /// <summary>
        /// Marks a contract Fulfilled and syncs its quest group (design doc 8.5) via
        /// <see cref="SyncQuestGroupAfterSettlement"/>, the same helper SettleDefaults uses.
        /// </summary>
        public void FulfillContract(FuturesContract contract)
        {
            contract.Status = ContractStatus.Fulfilled;
            int remainingInGroup = SyncQuestGroupAfterSettlement(contract);

            monitor?.Log($"ContractManager: fulfilled contract [{contract.ContractId}] item={contract.ItemId} price={contract.AgreedPrice}G; quest group [{contract.QuestId}] remaining={remainingInGroup}", LogLevel.Info);
        }

        /// <summary>
        /// Host-only authoritative FIFO arbitration (plan.md section 7, "方案1预先锁定式验证"): finds the
        /// earliest-signed still-Pending, not-already-locked contract for itemId due today, and reserves it
        /// (see lockedContractIds) so a second concurrent request for the same item can't also claim it.
        /// Deliberately does NOT check inventory stock - a request only ever originates from a click on an
        /// item slot that already exists in the requester's own inventory (see FuturesMenu), so "do they
        /// have the item" is inherently already true by the time this runs; the only thing left to decide is
        /// which contract, among possibly several, this particular claim should count against.
        ///
        /// Used both for the host's own click (called directly, synchronously, no network round-trip - same
        /// pattern as SignContractAsHost avoiding self-message reliance) and, via HandleDeliverRequestAsHost,
        /// for a farmhand's RequestDeliverMessage.
        /// </summary>
        public bool TryLockNextDeliverableContractAsHost(string itemId, out string contractId, out int agreedPrice)
        {
            SDate today = SDate.Now();
            FuturesContract contract = Contracts
                .Where(c => c.Status == ContractStatus.Pending && c.ItemId == itemId && c.DueDate.Equals(today) && !lockedContractIds.Contains(c.ContractId))
                .OrderBy(c => c.SignedDate.DaysSinceStart)
                .FirstOrDefault();

            if (contract == null)
            {
                contractId = null;
                agreedPrice = 0;
                return false;
            }

            lockedContractIds.Add(contract.ContractId);
            contractId = contract.ContractId;
            agreedPrice = contract.AgreedPrice;
            return true;
        }

        /// <summary>
        /// Host-only: handles a farmhand's RequestDeliverMessage by attempting the same FIFO lock the host's
        /// own click uses (TryLockNextDeliverableContractAsHost), then replying to that requester only -
        /// DeliverApprovedMessage on success, DeliverRejectedMessage ("NoEligibleContract") if nothing
        /// qualifies (none due today for that item, or every candidate is already locked by someone else's
        /// in-flight request).
        /// </summary>
        public void HandleDeliverRequestAsHost(string itemId, long requesterPlayerId)
        {
            if (TryLockNextDeliverableContractAsHost(itemId, out string contractId, out int agreedPrice))
            {
                multiplayerHelper.SendMessage(
                    new DeliverApprovedMessage { ContractId = contractId, AgreedPrice = agreedPrice },
                    MultiplayerMessageTypes.DeliverApproved,
                    playerIDs: new[] { requesterPlayerId });

                monitor?.Log($"ContractManager: [HOST] locked contract [{contractId}] for delivery by player {requesterPlayerId}, item={itemId} price={agreedPrice}G.", LogLevel.Info);
            }
            else
            {
                multiplayerHelper.SendMessage(
                    new DeliverRejectedMessage { ItemId = itemId, Reason = "NoEligibleContract" },
                    MultiplayerMessageTypes.DeliverRejected,
                    playerIDs: new[] { requesterPlayerId });

                monitor?.Log($"ContractManager: [HOST] rejected delivery request from player {requesterPlayerId}, item={itemId} - no eligible contract.", LogLevel.Info);
            }
        }

        /// <summary>
        /// Farmhand-side entry point (plan.md section 7): asks the host whether there's a contract to
        /// fulfill for itemId. Sends no local inventory/money change - the result arrives later as
        /// DeliverApprovedReceived or DeliverRejectedReceived.
        /// </summary>
        public void RequestDeliver(string itemId)
        {
            RequestDeliverMessage message = new RequestDeliverMessage
            {
                ItemId = itemId,
                RequesterPlayerId = Game1.player.UniqueMultiplayerID
            };

            multiplayerHelper.SendMessage(message, MultiplayerMessageTypes.RequestDeliver, playerIDs: null);

            monitor?.Log($"ContractManager: requested deliver item={itemId} from host.", LogLevel.Info);
        }

        /// <summary>Raises DeliverApprovedReceived - called by ModEntry's message dispatch when this client receives a DeliverApprovedMessage targeted at it.</summary>
        public void NotifyDeliverApproved(DeliverApprovedMessage message)
        {
            DeliverApprovedReceived?.Invoke(message);
        }

        /// <summary>Raises DeliverRejectedReceived - called by ModEntry's message dispatch when this client receives a DeliverRejectedMessage targeted at it.</summary>
        public void NotifyDeliverRejected(DeliverRejectedMessage message)
        {
            DeliverRejectedReceived?.Invoke(message);
        }

        /// <summary>
        /// Farmhand-side: reports that an approved delivery actually succeeded locally (item consumed,
        /// money credited) - the host finalizes the lock into a real Fulfilled status upon receiving this.
        /// </summary>
        public void ConfirmDeliver(string contractId)
        {
            multiplayerHelper.SendMessage(new DeliverConfirmedMessage { ContractId = contractId }, MultiplayerMessageTypes.DeliverConfirmed, playerIDs: null);
            monitor?.Log($"ContractManager: confirmed delivery of contract [{contractId}] to host.", LogLevel.Info);
        }

        /// <summary>
        /// Farmhand-side: reports that an approved delivery could NOT be completed locally (defensive
        /// re-check found the item gone) - the host releases its tentative lock so the next requester in
        /// line can claim the same contract.
        /// </summary>
        public void ReportDeliverFailed(string contractId)
        {
            multiplayerHelper.SendMessage(new DeliverFailedMessage { ContractId = contractId }, MultiplayerMessageTypes.DeliverFailed, playerIDs: null);
            monitor?.Log($"ContractManager: reported delivery failure for contract [{contractId}] to host.", LogLevel.Warn);
        }

        /// <summary>
        /// Host-only: a requester confirmed their approved delivery succeeded - releases the tentative lock,
        /// applies the Fulfilled status locally (via ApplyContractFulfilledFromNetwork, the single mutating
        /// method also used by every other client's received broadcast), then broadcasts the result.
        /// </summary>
        public void HandleDeliverConfirmedAsHost(string contractId)
        {
            lockedContractIds.Remove(contractId);
            ApplyContractFulfilledFromNetwork(contractId);

            multiplayerHelper.SendMessage(new ContractFulfilledMessage { ContractId = contractId }, MultiplayerMessageTypes.ContractFulfilled, playerIDs: null);

            monitor?.Log($"ContractManager: [HOST] finalized contract [{contractId}] as Fulfilled. Broadcast ContractFulfilled to all clients.", LogLevel.Info);
        }

        /// <summary>Host-only: a requester reported their approved delivery failed - releases the tentative lock back to Pending with no other state change, so the next FIFO-eligible request can claim it.</summary>
        public void HandleDeliverFailedAsHost(string contractId)
        {
            lockedContractIds.Remove(contractId);
            monitor?.Log($"ContractManager: [HOST] released lock on contract [{contractId}] - requester reported delivery failure.", LogLevel.Warn);
        }

        /// <summary>
        /// Applies a Fulfilled status to local state - called on EVERY client, including the host itself
        /// (see HandleDeliverConfirmedAsHost), never only on farmhands. Same "single mutating method shared
        /// by the host's local application and every other client's received-broadcast application" pattern
        /// as ApplyContractSignedFromNetwork. Idempotent: a contract that's unknown (shouldn't normally
        /// happen once full-state sync exists - plan.md section 7 phase 4) or already resolved (not
        /// Pending) is left untouched rather than re-processed.
        /// </summary>
        public void ApplyContractFulfilledFromNetwork(string contractId)
        {
            FuturesContract contract = Contracts.FirstOrDefault(c => c.ContractId == contractId);
            if (contract == null || contract.Status != ContractStatus.Pending)
            {
                return;
            }

            FulfillContract(contract);
        }

        /// <summary>
        /// Design doc 5.4/5.5's default settlement: every still-Pending contract whose DueDate is
        /// <paramref name="today"/> is marked Defaulted and charged the margin (forfeited back - it was
        /// paid TO the player by Pierre at signing, see SignContract) plus the price-gap penalty from
        /// design doc 1.2 - max(0, marketPrice * 1.10 - AgreedPrice), rounded to the nearest gold. The two
        /// amounts are summed into one total BEFORE clamping to the player's current money
        /// (SettlementMath.ComputeDefaultDeduction), not two separate clamps, so a shortfall never drives
        /// gold negative and the combined deduction never exceeds what the player actually has.
        ///
        /// Called from ModEntry's GameLoop.DayEnding handler rather than DayStarted: DayStarted already
        /// runs RebuildQuestLog (the design doc 8.5 self-heal), and if default settlement also ran there,
        /// the two handlers' relative order would be unspecified - the rebuild could see a contract that's
        /// about to default as still Pending. Settling the evening before guarantees next morning's rebuild
        /// always reads the already-final Defaulted status.
        ///
        /// Clears lockedContractIds before querying dueToday (plan.md section 7, delivery flow): a contract
        /// the host has tentatively approved for delivery (TryLockNextDeliverableContractAsHost) but hasn't
        /// yet received DeliverConfirmed/DeliverFailed for is still Status == Pending, but judging it as
        /// defaulted here would be a real money-correctness bug, not just a UI glitch - a delivery that's
        /// about to succeed (the requester already has DeliverApproved and is about to consume the item and
        /// get paid) would ALSO get charged the margin-forfeiture + price-gap penalty for defaulting on the
        /// same contract it's in the middle of fulfilling. Simply excluding locked contracts from dueToday
        /// isn't enough on its own, though: a lock only ever gets released by the requester sending
        /// DeliverConfirmed/DeliverFailed back (see HandleDeliverConfirmedAsHost/HandleDeliverFailedAsHost) -
        /// there's no timeout, so a requester who disconnects/crashes between receiving DeliverApproved and
        /// replying would leave that lock in place forever, permanently exempting that one contract from
        /// ever being judged (an exploitable "immune to default" loophole, not just an edge case).
        ///
        /// The fix: unconditionally clear the whole set right here, every time SettleDefaults runs, before
        /// dueToday is computed. This is safe precisely because SettleDefaults only runs once per night
        /// (DayEnding) while every legitimate lock is expected to resolve (confirmed or failed) within a
        /// fraction of a second of being granted, during the day - by the time night falls, any lock still
        /// standing is definitionally abandoned, not "still in flight". Clearing here means an abandoned
        /// lock can survive at most until the end of the day it was created on, never longer: the contract
        /// falls back to being judged as an ordinary Pending contract in the very same SettleDefaults call
        /// that just released it, so a stuck lock can never be used to dodge a default indefinitely.
        /// </summary>
        public void SettleDefaults(SDate today)
        {
            lockedContractIds.Clear();

            List<FuturesContract> dueToday = Contracts
                .Where(c => c.Status == ContractStatus.Pending && c.DueDate.Equals(today))
                .ToList();

            List<DefaultSettlementRecord> settlementRecords = new List<DefaultSettlementRecord>();

            foreach (FuturesContract contract in dueToday)
            {
                int marketPrice = GetMarketPrice(contract.ItemId);
                int priceGapPenalty = SettlementMath.ComputePriceGapPenalty(marketPrice, contract.AgreedPrice);

                // Margin + price-gap penalty are summed into one total inside ComputeDefaultDeduction
                // BEFORE clamping to the player's money - not two separate Min(Game1.player.Money, ...)
                // clamps here - so the combined deduction never exceeds what the player actually has.
                int actualDeduction = SettlementMath.ComputeDefaultDeduction(contract.Margin, priceGapPenalty, Game1.player.Money);
                Game1.player.Money -= actualDeduction;

                contract.Status = ContractStatus.Defaulted;
                int remainingInGroup = SyncQuestGroupAfterSettlement(contract);

                settlementRecords.Add(new DefaultSettlementRecord
                {
                    ItemId = contract.ItemId,
                    AgreedPrice = contract.AgreedPrice,
                    MarketPrice = marketPrice,
                    PriceGapPenalty = priceGapPenalty,
                    ActualDeduction = actualDeduction,
                    Margin = contract.Margin
                });

                monitor?.Log($"ContractManager: DEFAULTED contract [{contract.ContractId}] item={contract.ItemId} agreedPrice={contract.AgreedPrice}G marketPrice={marketPrice}G priceGapPenalty={priceGapPenalty}G marginForfeited={contract.Margin}G totalOwed={contract.Margin + priceGapPenalty}G actualDeduction={actualDeduction}G moneyAfter={Game1.player.Money}G; quest group [{contract.QuestId}] remaining={remainingInGroup}", LogLevel.Warn);
            }

            if (dueToday.Count > 0)
            {
                monitor?.Log($"ContractManager: default settlement complete for {DateHelper.FormatChineseDate(today)} - {dueToday.Count} contract(s) defaulted.", LogLevel.Info);
                QueueDefaultMail(today, settlementRecords);

                // Plan.md section 7 phase 4 ("全量同步"): broadcast the authoritative post-default state to
                // every client. Only when something actually defaulted - if dueToday was empty, nothing
                // changed that other clients don't already have (a signing/delivery elsewhere already gets
                // its own targeted broadcast independently of this), so there's nothing new to catch up on.
                BroadcastContractStateSync(targetPlayerIds: null);
            }
        }

        /// <summary>Per-contract numbers SettleDefaults already computed, captured only so QueueDefaultMail can build a mail body from them without recomputing (and possibly drifting from) the real deduction.</summary>
        private class DefaultSettlementRecord
        {
            public string ItemId;
            public int AgreedPrice;
            public int MarketPrice;
            public int PriceGapPenalty;
            public int ActualDeduction;
            public int Margin;
        }

        /// <summary>
        /// Builds tonight's default-notice letter and queues it via Game1.addMailForTomorrow. Merges every
        /// contract that defaulted for the same DueDate into one letter regardless of item variety (a wider
        /// grouping than the item+due-date quest-log grouping the rest of this class uses - see design doc
        /// discussion), with one sub-section per item variety inside the letter body so the player can still
        /// see a per-variety breakdown - final numbers only (差价罚金/没收保证金), no formula; the
        /// max(0, marketPrice*1.10-AgreedPrice) breakdown stays in the DEFAULTED log line SettleDefaults
        /// already writes per contract, it's just not shown to the player in-letter.
        ///
        /// Body lines are joined with SpriteText.newLine ('^'), not '\n'/Environment.NewLine: decompiling
        /// SpriteText (the bitmap font LetterViewerMenu draws mail through, not a regular SpriteFont) shows
        /// '^' is the only character it treats as a line break (checked via s[i] == '^' in drawString,
        /// positionOfNextSpace, and getStringBrokenIntoSectionsOfHeight alike); a literal Environment.NewLine
        /// is explicitly stripped out (s.Replace(Environment.NewLine, "")) before any of that runs, and a
        /// bare '\n' isn't recognized as a break either - both just collapse the text into one unbroken run,
        /// which is exactly the "one giant paragraph" bug this replaced.
        ///
        /// Also confirmed while chasing that bug: SpriteText.IsSpecialCharacter treats '=' (along with
        /// '&lt;','>','@','$','`','+') as one of a small set of ASCII symbols drawn from a fixed-grid Latin
        /// glyph sheet (getSourceRectForChar: index = c - 32) instead of the localized character map - not a
        /// missing-glyph fallback, but a real lookup that happens to land on unrelated leftover art (a purple
        /// star) at '='s grid slot, since no vanilla text ever uses a literal '=' there. Avoid '=' (and the
        /// other IsSpecialCharacter symbols) in any player-facing SpriteText string for that reason - this
        /// letter no longer uses '=' now that the formula line is gone, which is what sidesteps it here.
        ///
        /// The mail id is "MyFirstMod.Default.{DueDate.DaysSinceStart}" - DueDate alone is enough to be
        /// unique, since a given due date can only trigger one SettleDefaults batch (its contracts all flip
        /// to Defaulted the first time, so they'd fail the Status == Pending filter on any later day). This
        /// matters because Game1.addMailForTomorrow silently no-ops for a key the save has ever used before
        /// (Farmer.hasOrWillReceiveMail) - reusing a key across different default nights would drop every
        /// letter after the first without any error.
        /// </summary>
        private void QueueDefaultMail(SDate dueDate, List<DefaultSettlementRecord> records)
        {
            List<string> lines = new List<string>
            {
                $"{DateHelper.FormatChineseDate(dueDate)}期货违约通知",
                ""
            };

            int marginTotal = 0;
            int nominalPenaltyTotal = 0;
            int actualDeductionTotal = 0;

            foreach (IGrouping<string, DefaultSettlementRecord> group in records.GroupBy(r => r.ItemId))
            {
                string itemName = GetItemDisplayName(group.Key);
                int count = group.Count();
                int groupNominalPenalty = group.Sum(r => r.PriceGapPenalty);
                int groupMargin = group.Sum(r => r.Margin);

                lines.Add($"{itemName} x{count}");
                lines.Add($"  差价罚金：{groupNominalPenalty}G");
                lines.Add($"  没收保证金：{groupMargin}G");
                lines.Add("");

                marginTotal += groupMargin;
                nominalPenaltyTotal += groupNominalPenalty;
                actualDeductionTotal += group.Sum(r => r.ActualDeduction);
            }

            // actualDeductionTotal already sums each contract's ComputeDefaultDeduction result, which is
            // margin+priceGapPenalty combined and clamped - so it alone IS the real total deducted; adding
            // marginTotal again here would double-count the margin (this was correct under the old
            // direction, where actualDeductionTotal only ever held the price-gap portion - see plan.md 5.5).
            int nominalTotalOwed = marginTotal + nominalPenaltyTotal;
            lines.Add($"合计扣款：{actualDeductionTotal}G");
            if (actualDeductionTotal < nominalTotalOwed)
            {
                lines.Add($"（因金币不足，实际只扣除了{actualDeductionTotal}G，理论应扣{nominalTotalOwed}G）");
            }

            string mailKey = $"MyFirstMod.Default.{dueDate.DaysSinceStart}";
            PendingDefaultMail[mailKey] = string.Join(SpriteText.newLine.ToString(), lines);
            Game1.addMailForTomorrow(mailKey);

            monitor?.Log($"ContractManager: queued default-notice mail [{mailKey}] for due date {DateHelper.FormatChineseDate(dueDate)} - {records.Count} contract(s) across {records.Select(r => r.ItemId).Distinct().Count()} item(s), tallied loss {actualDeductionTotal}G.", LogLevel.Info);
        }

        /// <summary>
        /// Design doc 8.5 settlement quest-sync rule, shared by FulfillContract and SettleDefaults: if
        /// other Pending contracts remain in the same item+due-date group, only the "xN" count in the quest
        /// description is updated; if this was the last one, the quest entry is removed from the quest log
        /// entirely. Returns the remaining-Pending count for the caller's own logging.
        /// </summary>
        private int SyncQuestGroupAfterSettlement(FuturesContract contract)
        {
            Quest quest = Game1.player.questLog.FirstOrDefault(q => q.id.Value == contract.QuestId);
            int remainingInGroup = CountPending(contract.ItemId, contract.DueDate);

            if (quest != null)
            {
                if (remainingInGroup > 0)
                {
                    quest.questDescription = BuildQuestDescription(GetItemDisplayName(contract.ItemId), DateHelper.FormatChineseDate(contract.DueDate), remainingInGroup);
                }
                else
                {
                    Game1.player.questLog.Remove(quest);
                }
            }

            return remainingInGroup;
        }

        /// <summary>
        /// Design doc 8.5 self-heal: rebuilds every futures quest-log entry from Contracts, the single
        /// source of truth, each morning (see ModEntry's DayStarted handler). SignContract/FulfillContract
        /// already keep the quest log in sync immediately when they run; this is the backstop for cases
        /// where the two could otherwise drift - e.g. contract data restored from save data whose quest
        /// text was last written before a restart, per the persistence gap this mod hit earlier.
        /// </summary>
        public void RebuildQuestLog()
        {
            var pendingGroups = Contracts
                .Where(c => c.Status == ContractStatus.Pending)
                .GroupBy(c => new { c.ItemId, Day = c.DueDate.DaysSinceStart })
                .ToList();

            HashSet<string> liveQuestIds = new HashSet<string>();

            foreach (var group in pendingGroups)
            {
                string itemId = group.Key.ItemId;
                int day = group.Key.Day;

                FuturesContract first = group.First();
                string groupQuestId = $"MyFirstMod.Futures.{itemId}.{day}";
                liveQuestIds.Add(groupQuestId);

                string dateText = DateHelper.FormatChineseDate(first.DueDate);
                string itemDisplayName = GetItemDisplayName(itemId);
                int count = group.Count();

                Quest quest = Game1.player.questLog.FirstOrDefault(q => q.id.Value == groupQuestId);
                if (quest == null)
                {
                    quest = new Quest();
                    quest.id.Value = groupQuestId;
                    quest.accepted.Value = true;
                    quest.questType.Value = Quest.type_basic;
                    quest.daysLeft.Value = Math.Max(0, day - first.SignedDate.DaysSinceStart);
                    Game1.player.questLog.Add(quest);
                }

                quest.questTitle = $"{itemDisplayName}期货 - {dateText}交割";
                quest.questDescription = BuildQuestDescription(itemDisplayName, dateText, count);
            }

            List<Quest> staleQuests = Game1.player.questLog
                .Where(q => q.id.Value != null && q.id.Value.StartsWith("MyFirstMod.Futures.") && !liveQuestIds.Contains(q.id.Value))
                .ToList();
            foreach (Quest quest in staleQuests)
            {
                Game1.player.questLog.Remove(quest);
            }

            monitor?.Log($"ContractManager: rebuilt quest log - {pendingGroups.Count} live group(s), removed {staleQuests.Count} stale quest(s).", LogLevel.Info);
        }

        /// <summary>
        /// Plan.md 6.6 (2026-09-14: price engine switched on): the "market price" SettleDefaults' price-gap
        /// penalty (design doc 1.2) is computed against - now the most recent entry in
        /// BeerPriceHistory/PaleAlePriceHistory (today's PriceModel-simulated price, appended once per day
        /// by <see cref="UpdateDailyPrices"/>), not the item's fixed native sell price.
        ///
        /// Falls back to GetNativeSellPrice if that item's history is still empty - this shouldn't normally
        /// happen (UpdateDailyPrices runs on every DayStarted, strictly before any DayEnding settlement
        /// could see that day's contracts as due), but a brand-new save/host whose very first day somehow
        /// reaches a default before its first DayStarted tick would otherwise have no simulated price to
        /// read yet. Falling back to the native price (rather than 0 or throwing) keeps the price-gap
        /// penalty a sane, always-computable number in that edge case instead of crashing or handing out a
        /// nonsensical penalty based on a market price of zero.
        ///
        /// Deliberately does NOT affect AgreedPrice: that's still locked at signing time via
        /// GetNativeSellPrice (SignContractAsHost) and never changes for the life of a contract - only the
        /// "market price" used to judge how far a defaulted contract's agreed price has drifted uses the
        /// simulated value.
        /// </summary>
        public int GetMarketPrice(string itemId)
        {
            if (itemId == BeerItemId && BeerPriceHistory.Count > 0)
            {
                return BeerPriceHistory[BeerPriceHistory.Count - 1];
            }

            if (itemId == PaleAleItemId && PaleAlePriceHistory.Count > 0)
            {
                return PaleAlePriceHistory[PaleAlePriceHistory.Count - 1];
            }

            return GetNativeSellPrice(itemId);
        }

        /// <summary>The item's real, unmodified vanilla sell price (Data/Objects' Price field via a fresh quality-0 instance) - the "P0" anchor plan.md 6.2/6.4 refers to. Unknown item ids return 0, same as GetMarketPrice always has.</summary>
        public static int GetNativeSellPrice(string itemId)
        {
            return (ItemRegistry.Create("(O)" + itemId) as StardewValley.Object)?.Price ?? 0;
        }

        /// <summary>Maximum number of days of price history kept per item (plan.md section 6) before the oldest entries are dropped, so the save file doesn't grow unbounded.</summary>
        public const int MaxPriceHistoryLength = 90;

        /// <summary>Daily simulated price history for Beer (plan.md section 6), oldest first, capped at <see cref="MaxPriceHistoryLength"/> entries. The last entry is also what GetMarketPrice returns (plan.md 6.6); the full history is kept for a future price chart, which doesn't exist yet.</summary>
        public List<int> BeerPriceHistory { get; } = new List<int>();

        /// <summary>Same as <see cref="BeerPriceHistory"/>, for Pale Ale.</summary>
        public List<int> PaleAlePriceHistory { get; } = new List<int>();

        private PriceModel.PriceRegime marketRegime = PriceModel.PriceRegime.Initial;

        /// <summary>
        /// Plan.md section 6's daily market-price update: computes today's simulated price for Beer and
        /// Pale Ale via PriceModel and appends both to their history. Beer is computed first so the shared
        /// layer-5 jump (see PriceModel.PriceRegime) is drawn on that call and reused, not redrawn, for Pale
        /// Ale's call the same day - call order matters for both the sharing and for fixed-seed
        /// reproducibility (see PriceModelTests).
        ///
        /// Plan.md 6.6: this is what feeds GetMarketPrice - the entry appended here today is exactly what
        /// GetMarketPrice will return for the rest of the day, until tomorrow's call appends the next one.
        /// Intended to be called once per day from ModEntry's DayStarted handler, strictly before any
        /// DayEnding settlement that day could read GetMarketPrice.
        /// </summary>
        public void UpdateDailyPrices(SDate today, Random rng)
        {
            int beerNativePrice = GetNativeSellPrice(BeerItemId);
            int paleAleNativePrice = GetNativeSellPrice(PaleAleItemId);

            int yesterdayBeerPrice = BeerPriceHistory.Count > 0 ? BeerPriceHistory[BeerPriceHistory.Count - 1] : beerNativePrice;
            int yesterdayPaleAlePrice = PaleAlePriceHistory.Count > 0 ? PaleAlePriceHistory[PaleAlePriceHistory.Count - 1] : paleAleNativePrice;

            Season season = PriceModel.ParseSeason(today.SeasonKey);

            (int beerPrice, PriceModel.PriceRegime regimeAfterBeer) = PriceModel.ComputeDailyPrice(
                yesterdayBeerPrice, beerNativePrice, PriceModel.BeerBeta, season, today.Day, today.DayOfWeek, marketRegime, rng);

            (int paleAlePrice, PriceModel.PriceRegime regimeAfterPaleAle) = PriceModel.ComputeDailyPrice(
                yesterdayPaleAlePrice, paleAleNativePrice, PriceModel.PaleAleBeta, season, today.Day, today.DayOfWeek, regimeAfterBeer, rng);

            marketRegime = regimeAfterPaleAle;

            AppendPriceHistory(BeerPriceHistory, beerPrice);
            AppendPriceHistory(PaleAlePriceHistory, paleAlePrice);

            monitor?.Log($"ContractManager: PriceModel daily update for {DateHelper.FormatChineseDate(today)} - beer={beerPrice}G paleAle={paleAlePrice}G (now live via GetMarketPrice - see plan.md 6.6).", LogLevel.Trace);

            // Plan.md section 7 phase 4 ("全量同步"): unlike SettleDefaults, this runs unconditionally -
            // every call appends a fresh day's price to history, so there's always something new for other
            // clients to catch up on.
            BroadcastContractStateSync(targetPlayerIds: null);
        }

        private static void AppendPriceHistory(List<int> history, int price)
        {
            history.Add(price);
            if (history.Count > MaxPriceHistoryLength)
            {
                history.RemoveAt(0);
            }
        }

        /// <summary>Snapshots the price history and market regime into the DTO Helper.Data.WriteSaveData expects.</summary>
        public MarketPriceSaveData ToMarketPriceSaveData()
        {
            return new MarketPriceSaveData
            {
                BeerPriceHistory = new List<int>(BeerPriceHistory),
                PaleAlePriceHistory = new List<int>(PaleAlePriceHistory),
                RegimeLastAdvancedDayOfYear = marketRegime.LastAdvancedDayOfYear,
                RegimeJumpTriggeredToday = marketRegime.JumpTriggeredToday,
                RegimeJumpSignedMagnitudeToday = marketRegime.JumpSignedMagnitudeToday
            };
        }

        /// <summary>Restores price history and market regime from what Helper.Data.ReadSaveData returned for the just-loaded save. Null (new save, or mod's first run on it) resets to empty history and the initial regime.</summary>
        public void LoadMarketPriceSaveData(MarketPriceSaveData saveData)
        {
            BeerPriceHistory.Clear();
            PaleAlePriceHistory.Clear();

            if (saveData == null)
            {
                marketRegime = PriceModel.PriceRegime.Initial;
                monitor?.Log("ContractManager: no saved market-price data found for this save (new save, or mod's first run on it). Starting with empty price history.", LogLevel.Info);
                return;
            }

            BeerPriceHistory.AddRange(saveData.BeerPriceHistory);
            PaleAlePriceHistory.AddRange(saveData.PaleAlePriceHistory);
            marketRegime = new PriceModel.PriceRegime(saveData.RegimeLastAdvancedDayOfYear, saveData.RegimeJumpTriggeredToday, saveData.RegimeJumpSignedMagnitudeToday);

            monitor?.Log($"ContractManager: restored market-price history from save data - beer {BeerPriceHistory.Count} day(s), pale ale {PaleAlePriceHistory.Count} day(s).", LogLevel.Info);
        }

        private static string GetItemDisplayName(string itemId)
        {
            return ItemRegistry.Create("(O)" + itemId)?.DisplayName ?? itemId;
        }

        private static string BuildQuestDescription(string itemDisplayName, string dateText, int count)
        {
            return $"已与皮埃尔约定，需在{dateText}交割{itemDisplayName} x{count}";
        }

        /// <summary>Snapshots Contracts into the plain DTO shape Helper.Data.WriteSaveData expects, converting each SDate to its DaysSinceStart int.</summary>
        public List<FuturesContractSaveData> ToSaveData()
        {
            return Contracts.Select(ToSaveDataEntry).ToList();
        }

        /// <summary>Converts a single FuturesContract into its save/wire DTO shape - shared by ToSaveData's per-contract mapping and SignContractAsHost's ContractSignedMessage payload, so the two can't drift into two different field lists.</summary>
        private static FuturesContractSaveData ToSaveDataEntry(FuturesContract c)
        {
            return new FuturesContractSaveData
            {
                ContractId = c.ContractId,
                ItemId = c.ItemId,
                AgreedPrice = c.AgreedPrice,
                SignedDateDaysSinceStart = c.SignedDate.DaysSinceStart,
                DueDateDaysSinceStart = c.DueDate.DaysSinceStart,
                Status = c.Status,
                QuestId = c.QuestId
            };
        }

        /// <summary>
        /// Replaces Contracts with what Helper.Data.ReadSaveData returned for the just-loaded save,
        /// converting each stored DaysSinceStart back into an SDate via the verified SDate.FromDaysSinceStart
        /// factory. Always clears the current list first - even on a brand-new save (saveData null, becomes
        /// an empty list) - so contracts from a previously loaded save in the same game process can't bleed
        /// into a different save the player switches to without restarting.
        /// </summary>
        public void LoadFromSaveData(List<FuturesContractSaveData> saveData)
        {
            Contracts.Clear();
            if (saveData == null)
            {
                monitor?.Log("ContractManager: no saved futures-contract data found for this save (new save, or mod's first run on it). Starting with an empty contract list.", LogLevel.Info);
                return;
            }

            foreach (FuturesContractSaveData data in saveData)
            {
                Contracts.Add(new FuturesContract(
                    data.ContractId,
                    data.ItemId,
                    data.AgreedPrice,
                    SDate.FromDaysSinceStart(data.SignedDateDaysSinceStart),
                    SDate.FromDaysSinceStart(data.DueDateDaysSinceStart),
                    data.Status,
                    data.QuestId));
            }

            monitor?.Log($"ContractManager: restored {Contracts.Count} contract(s) from save data.", LogLevel.Info);
        }

        /// <summary>
        /// Host-only (plan.md section 7 phase 4, chosen design "全量同步"): builds the current authoritative
        /// snapshot (reusing ToSaveData/ToMarketPriceSaveData - the exact same shape the save file uses, not
        /// a second parallel one) and sends it as one ContractStateSyncMessage. targetPlayerIds mirrors
        /// Helper.Multiplayer.SendMessage's own convention: null broadcasts to everyone (SettleDefaults/
        /// UpdateDailyPrices), a specific array targets just that player (SendFullStateSyncTo, for a
        /// newly-connected peer).
        /// </summary>
        private void BroadcastContractStateSync(long[] targetPlayerIds)
        {
            ContractStateSyncMessage message = new ContractStateSyncMessage
            {
                Contracts = ToSaveData(),
                MarketPrices = ToMarketPriceSaveData()
            };

            multiplayerHelper.SendMessage(message, MultiplayerMessageTypes.ContractStateSync, playerIDs: targetPlayerIds);
        }

        /// <summary>Host-only: sends a full state snapshot to one specific player - used when a peer connects mid-session (plan.md section 7 phase 4) so they don't have to wait for the next default/price-update broadcast to catch up on contracts/prices that already existed before they joined.</summary>
        public void SendFullStateSyncTo(long playerId)
        {
            BroadcastContractStateSync(new[] { playerId });
            monitor?.Log($"ContractManager: [HOST] sent full ContractStateSync to newly-connected player {playerId}.", LogLevel.Info);
        }

        /// <summary>
        /// Every client (including the host, which never needs to apply its own broadcast - SettleDefaults/
        /// UpdateDailyPrices/SendFullStateSyncTo already mutated its Contracts/price history directly and in
        /// place before broadcasting): wholesale-replaces local Contracts and price history from a received
        /// ContractStateSyncMessage. Reuses LoadFromSaveData/LoadMarketPriceSaveData rather than
        /// reimplementing the same reconstruction a third time - the "clear and rebuild from a DTO list"
        /// logic is identical whether the DTOs came from the save file or the network. Their own log lines
        /// say "save data" for that reason; the wrapping log line below is what actually describes this as a
        /// network sync.
        ///
        /// Always calls RebuildQuestLog() afterwards, unconditionally - this is the concrete mechanism
        /// behind "任务栏可见性方案A" (plan.md section 7): once every client's Contracts reflects the same
        /// authoritative list, the existing, already-per-client RebuildQuestLog naturally shows every
        /// pending contract farm-wide in each player's own quest log, not just ones they personally signed.
        /// RebuildQuestLog is already designed to be safe to call redundantly (it's the existing DayStarted
        /// self-heal), so there's no need to diff old vs. new Contracts first to decide whether to call it -
        /// every full sync just calls it, matching how it's already used elsewhere in this codebase.
        /// </summary>
        public void ApplyContractStateSyncFromNetwork(ContractStateSyncMessage message)
        {
            LoadFromSaveData(message.Contracts);
            LoadMarketPriceSaveData(message.MarketPrices);
            RebuildQuestLog();

            monitor?.Log($"ContractManager: applied full ContractStateSync from network - {Contracts.Count} contract(s), quest log rebuilt.", LogLevel.Info);
        }
    }
}
