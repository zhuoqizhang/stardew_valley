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

        public List<FuturesContract> Contracts { get; } = new List<FuturesContract>();

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

        public ContractManager(IMonitor monitor)
        {
            this.monitor = monitor;
        }

        /// <summary>
        /// Signs a new, independent contract (design doc 5.1) and syncs the quest log: contracts that
        /// share the same item and due date are merged into one quest entry showing "xN" rather than
        /// spawning one quest per contract. Also pays the contract's margin to the player up front (design
        /// doc 5.5: Pierre pays the margin to the player at signing, not the other way around - see
        /// SettleDefaults for where it gets forfeited back on default).
        /// </summary>
        public FuturesContract SignContract(string itemId, int agreedPrice, SDate signedDate, SDate dueDate)
        {
            FuturesContract contract = new FuturesContract(itemId, agreedPrice, signedDate, dueDate);

            // Design doc 5.1/5.5: Pierre pays the margin to the player, not the other way around - this is
            // a payout, not a charge, so unlike a purchase there's no balance check to fail. Uses
            // contract.Margin (not a separate SettlementMath.ComputeMargin(agreedPrice) call) so the amount
            // actually paid out can never drift from the value SettleDefaults later forfeits back.
            Game1.player.Money += contract.Margin;

            Contracts.Add(contract);

            // Group key: same item + same due date share one quest, not a per-contract id.
            string groupQuestId = $"MyFirstMod.Futures.{itemId}.{dueDate.DaysSinceStart}";
            contract.QuestId = groupQuestId;
            int countInGroup = CountPending(itemId, dueDate);
            string dateText = DateHelper.FormatChineseDate(dueDate);
            string itemDisplayName = GetItemDisplayName(itemId);

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
            quest.daysLeft.Value = Math.Max(0, dueDate.DaysSinceStart - signedDate.DaysSinceStart);

            monitor?.Log($"ContractManager: signed contract [{contract.ContractId}] -> quest group [{groupQuestId}] (x{countInGroup}). Paid margin={contract.Margin}G to player (moneyAfter={Game1.player.Money}G). Total contracts: {Contracts.Count}", LogLevel.Info);
            foreach (FuturesContract c in Contracts)
            {
                monitor?.Log($"  - id={c.ContractId} item={c.ItemId} price={c.AgreedPrice}G due={DateHelper.FormatChineseDate(c.DueDate)} status={c.Status} quest={c.QuestId}", LogLevel.Info);
            }

            return contract;
        }

        /// <summary>Counts still-open contracts for the same item and due date, e.g. to show a merged "xN" quest-log entry.</summary>
        public int CountPending(string itemId, SDate dueDate)
        {
            return Contracts.Count(c => c.ItemId == itemId && c.DueDate.Equals(dueDate) && c.Status == ContractStatus.Pending);
        }

        /// <summary>
        /// Marks a contract Fulfilled and syncs its quest group (design doc 6.5) via
        /// <see cref="SyncQuestGroupAfterSettlement"/>, the same helper SettleDefaults uses.
        /// </summary>
        public void FulfillContract(FuturesContract contract)
        {
            contract.Status = ContractStatus.Fulfilled;
            int remainingInGroup = SyncQuestGroupAfterSettlement(contract);

            monitor?.Log($"ContractManager: fulfilled contract [{contract.ContractId}] item={contract.ItemId} price={contract.AgreedPrice}G; quest group [{contract.QuestId}] remaining={remainingInGroup}", LogLevel.Info);
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
        /// runs RebuildQuestLog (the design doc 6.5 self-heal), and if default settlement also ran there,
        /// the two handlers' relative order would be unspecified - the rebuild could see a contract that's
        /// about to default as still Pending. Settling the evening before guarantees next morning's rebuild
        /// always reads the already-final Defaulted status.
        /// </summary>
        public void SettleDefaults(SDate today)
        {
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
        /// Design doc 6.5 settlement quest-sync rule, shared by FulfillContract and SettleDefaults: if
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
        /// Design doc 6.5 self-heal: rebuilds every futures quest-log entry from Contracts, the single
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
        /// Placeholder fixed market prices, deliberately different from each item's current AgreedPrice
        /// (Beer 45G, Pale Ale 55G) so default-settlement price-gap math (SettleDefaults) has a nonzero
        /// delta to test against instead of masking bugs behind equal numbers. Not a real floating market.
        /// </summary>
        public int GetMarketPrice(string itemId)
        {
            if (itemId == BeerItemId)
            {
                return 50;
            }

            if (itemId == PaleAleItemId)
            {
                return 65;
            }

            return 0;
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
            return Contracts.Select(c => new FuturesContractSaveData
            {
                ContractId = c.ContractId,
                ItemId = c.ItemId,
                AgreedPrice = c.AgreedPrice,
                SignedDateDaysSinceStart = c.SignedDate.DaysSinceStart,
                DueDateDaysSinceStart = c.DueDate.DaysSinceStart,
                Status = c.Status,
                QuestId = c.QuestId
            }).ToList();
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
    }
}
