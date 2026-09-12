using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using System;
using System.Linq;
using MyFirstMod.Patches;

namespace MyFirstMod
{
    public class ModEntry : Mod
    {
        private Random rng = new Random();

        // Key ContractManager's data is filed under via Helper.Data.WriteSaveData/ReadSaveData; shared
        // between the write and read side so they can't drift apart into two different strings.
        private const string ContractsSaveDataKey = "FuturesContracts";

        // Same pattern as ContractsSaveDataKey, for PriceModel's daily price history (plan.md section 6).
        private const string MarketPriceSaveDataKey = "MarketPriceHistory";

        private ContractManager contractManager;

        public override void Entry(IModHelper helper)
        {
            contractManager = new ContractManager(Monitor, helper.Multiplayer);
            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.GameLoop.Saving += OnSaving;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.DayEnding += OnDayEnding;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.Content.AssetRequested += OnAssetRequested;
            helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
            helper.Events.Multiplayer.PeerConnected += OnPeerConnected;

            PierreShopPatch.Monitor = Monitor;
            PierreShopPatch.ContractManager = contractManager;
            try
            {
                Harmony harmony = new Harmony(ModManifest.UniqueID);
                PierreShopPatch.Apply(harmony);
                Monitor.Log("Applied Pierre shop-entry patch.", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Failed to apply Pierre shop-entry patch. Pierre's shop will fall back to vanilla behavior. Error: {ex}", LogLevel.Error);
            }

            Monitor.Log("MyFirstMod loaded!", LogLevel.Info);
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady) return;

            if (e.Button == SButton.F6)
            {
                if (Game1.activeClickableMenu == null)
                {
                    Game1.activeClickableMenu = new FuturesMenu(Monitor, contractManager);
                }
                return;
            }
        }

        /// <summary>
        /// Fires while a save is being written to disk. Snapshots ContractManager.Contracts into the current
        /// save slot's data.
        ///
        /// Multiplayer stop-the-bleeding fix: decompiling SMAPI's DataHelper confirms
        /// Helper.Data.WriteSaveData throws InvalidOperationException whenever !Context.IsOnHostComputer
        /// ("Save files are stored on the main player's computer.") - a remote farmhand (a different
        /// physical computer than the host) would crash here every single time a save happens. Gating on the
        /// exact same condition SMAPI itself checks means this never fires when it would throw, while
        /// single-player, hosting, and local split-screen (all IsOnHostComputer == true) keep working exactly
        /// as before. This does NOT make farmhands' contract data persist or sync anywhere yet - see plan.md
        /// section 7 (multiplayer architecture, pending part 2's design).
        /// </summary>
        private void OnSaving(object sender, SavingEventArgs e)
        {
            if (!Context.IsOnHostComputer)
            {
                Monitor.Log("ModEntry: skipping futures/price save-data write - not on the host's computer (remote farmhand). See plan.md section 7.", LogLevel.Trace);
                return;
            }

            Helper.Data.WriteSaveData(ContractsSaveDataKey, contractManager.ToSaveData());
            Helper.Data.WriteSaveData(MarketPriceSaveDataKey, contractManager.ToMarketPriceSaveData());
        }

        /// <summary>
        /// Fires once per save load (title screen "Continue"/"Load", including after a full game restart).
        /// Restores ContractManager.Contracts from this save slot's data (null for a brand-new save, or the
        /// mod's first run on an existing one - LoadFromSaveData treats that as "start empty", not an error).
        /// The [DIAG] dump/mismatch check below predates persistence (added to confirm the quest-log vs
        /// contract-list gap this was built to fix); kept as a standing regression check - if it ever fires
        /// "Mismatch confirmed" again after this change, persistence broke.
        ///
        /// Multiplayer stop-the-bleeding fix: same reasoning as OnSaving - Helper.Data.ReadSaveData also
        /// throws when !Context.IsOnHostComputer, so a remote farmhand would crash here on every save load.
        /// Skipping the read for a farmhand leaves ContractManager's lists at their empty constructor
        /// defaults; this means a farmhand currently sees no futures contracts or price history at all
        /// (stale/empty, not synced) - the known limitation plan.md section 7 will address, not a bug in
        /// this fix. The [DIAG] mismatch check is skipped too for a farmhand: their own Game1.player.questLog
        /// persists normally (it's vanilla per-Farmer save data, untouched by this gate), so comparing it
        /// against an intentionally-empty Contracts list would always "mismatch" for the wrong reason and
        /// mask the real regression check this diagnostic exists for.
        /// </summary>
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            if (!Context.IsOnHostComputer)
            {
                Monitor.Log("ModEntry: skipping futures/price save-data read - not on the host's computer (remote farmhand). Futures contracts and price history will appear empty on this client until plan.md section 7's sync is built.", LogLevel.Trace);
                return;
            }

            List<FuturesContractSaveData> saveData = Helper.Data.ReadSaveData<List<FuturesContractSaveData>>(ContractsSaveDataKey);
            contractManager.LoadFromSaveData(saveData);

            MarketPriceSaveData marketPriceSaveData = Helper.Data.ReadSaveData<MarketPriceSaveData>(MarketPriceSaveDataKey);
            contractManager.LoadMarketPriceSaveData(marketPriceSaveData);

            var futuresQuests = Game1.player.questLog
                .Where(q => q.id.Value != null && q.id.Value.StartsWith("MyFirstMod.Futures."))
                .ToList();

            Monitor.Log($"[DIAG] SaveLoaded: questLog has {futuresQuests.Count} futures quest(s):", LogLevel.Info);
            foreach (var quest in futuresQuests)
            {
                Monitor.Log($"[DIAG]   quest id={quest.id.Value} title=\"{quest.questTitle}\" desc=\"{quest.questDescription}\"", LogLevel.Info);
            }

            Monitor.Log($"[DIAG] SaveLoaded: ContractManager.Contracts has {contractManager.Contracts.Count} entrie(s):", LogLevel.Info);
            foreach (FuturesContract contract in contractManager.Contracts)
            {
                Monitor.Log($"[DIAG]   contract id={contract.ContractId} item={contract.ItemId} price={contract.AgreedPrice}G due={DateHelper.FormatChineseDate(contract.DueDate)} status={contract.Status} quest={contract.QuestId}", LogLevel.Info);
            }

            if (futuresQuests.Count > 0 && contractManager.Contracts.Count < futuresQuests.Count)
            {
                Monitor.Log("[DIAG] Mismatch confirmed at SaveLoaded: questLog remembers more futures commitments than ContractManager.Contracts currently holds.", LogLevel.Warn);
            }
        }

        /// <summary>
        /// Fires the evening a day ends, before the next day's DayStarted. Design doc 5.4/6.5: default
        /// settlement (ContractManager.SettleDefaults) must run here rather than on DayStarted, because
        /// DayStarted already does the daily quest-log rebuild (RebuildQuestLog) - if both ran on
        /// DayStarted their relative order would be unspecified, and the rebuild could read a contract
        /// that's about to default as still Pending. Settling the evening before guarantees next morning's
        /// rebuild always sees the already-final (Defaulted) status.
        ///
        /// Multiplayer stop-the-bleeding fix: decompiling SMAPI confirms GameLoop.DayEnding is raised
        /// unconditionally from a callback on vanilla Game1.newDayAfterFade, which every connected client
        /// runs locally (there's no IsMainPlayer gate inside SMAPI's own raise) - so without this guard,
        /// SettleDefaults would run once per connected client. Since Farmer.Money (decompile-confirmed via
        /// FarmerTeam.GetMoney) routes through the single shared, network-synced FarmerTeam.money field when
        /// wallets aren't separate, every client independently deducting the same contract's penalty would
        /// double (or N-times) the real deduction against that one shared pool. Context.IsMainPlayer (true
        /// for the logical host, regardless of which physical computer that is) ensures this runs exactly
        /// once per session. This does NOT make a farmhand's own locally-signed contracts get settled
        /// correctly yet - see plan.md section 7 (pending sync architecture, part 2).
        /// </summary>
        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            if (Context.IsMainPlayer)
            {
                contractManager.SettleDefaults(SDate.Now());
            }

            // Data/mail may already be cached from earlier this session (the game itself reads it every
            // night during its own overnight mail processing, well before the player can reach a physical
            // mailbox). Invalidating here forces the next load - which happens later this same night - to
            // pick up any new ContractManager.PendingDefaultMail entries SettleDefaults just queued via
            // OnAssetRequested below.
            //
            // Deliberately NOT InvalidateCache("Data/mail") - confirmed by decompiling SMAPI
            // (GameContentHelper.InvalidateCache(string)) that the string overload matches the FULL cached
            // asset name (IAssetName.IsEquivalentTo(..., useBaseName: false)), not the locale-stripped name.
            // In a non-English game the actually-cached entry is locale-suffixed (e.g. "Data/mail.zh-CN"),
            // so "Data/mail" never matches it and the call silently invalidates 0 entries - the exact bug
            // that caused queued default-notice mail to never reach the player (verified against this
            // session's own SMAPI log: every InvalidateCache("Data/mail") call logged "Invalidated 0 cache
            // entries", while GameLocation.mailbox() still consumed/removed the mailbox key and displayed
            // nothing, since Data/mail's cached copy was stuck at whatever it held before any mail existed).
            // The predicate overload matches on NameWithoutLocale instead, the same property OnAssetRequested
            // already keys off, so it actually reaches the locale-specific cached copy.
            Helper.GameContent.InvalidateCache(asset => asset.NameWithoutLocale.IsEquivalentTo("Data/mail"));
        }

        /// <summary>
        /// Fires every in-game day - after loading a save (right after SaveLoaded) and after sleeping,
        /// without needing a restart. Calls ContractManager.RebuildQuestLog() as the design doc 8.5
        /// self-heal: makes the quest log match Contracts every morning regardless of how it got out of
        /// sync, on top of the immediate updates SignContract/FulfillContract already do. The [DIAG] count
        /// log predates this (added to diagnose the persistence gap); a same-session drop in this count not
        /// explained by deliveries/defaults would mean Contracts is being cleared by something other than a
        /// restart - a different, unrelated bug from what that diagnostic was built to find.
        ///
        /// RebuildQuestLog is deliberately left ungated: it only touches this client's own local
        /// Game1.player.questLog and has no money/shared-state side effects, so it's harmless (and, in
        /// principle, still correct) for every client to run it against their own local Contracts.
        /// </summary>
        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            contractManager.RebuildQuestLog();
            Monitor.Log($"[DIAG] DayStarted: ContractManager.Contracts count = {contractManager.Contracts.Count}", LogLevel.Info);

            // Multiplayer stop-the-bleeding fix: same DayEnding/DayStarted-fires-per-client fact as
            // OnDayEnding applies here too. Beyond just avoiding duplicate work, UpdateDailyPrices consumes
            // "rng" (a plain, unsynchronized System.Random - see the "rng" field above) to decide the shared
            // layer-5 jump event; if every client called it with their own independently-advancing Random,
            // each client would compute a DIFFERENT simulated price for the same day. Gating on
            // Context.IsMainPlayer keeps this authoritative-once-per-session, same as SettleDefaults. Plan.md
            // section 6 already notes this history doesn't feed GetMarketPrice yet; now it also doesn't run
            // on farmhands at all - their price history stays empty until plan.md section 7's sync exists.
            if (Context.IsMainPlayer)
            {
                contractManager.UpdateDailyPrices(SDate.Now(), rng);
            }
        }

        /// <summary>
        /// Plan.md section 7 (host-authoritative multiplayer sync, phase 1: infrastructure only). Dispatches
        /// an incoming Helper.Multiplayer message to the handler for its Type. Every case below is
        /// currently a stub - the actual signing (phase 2), delivery (phase 3), and full-state-sync (phase
        /// 4) logic isn't implemented yet, so this only proves the plumbing (subscription, mod-ID filtering,
        /// type dispatch) is wired correctly ahead of those phases.
        ///
        /// Filters on e.FromModID first: ModMessageReceived fires for every mod's messages on this
        /// connection, not just ours (see ModMessageReceivedEventArgs's own doc comment - "mods should check
        /// FromModID" since Type alone isn't guaranteed unique across mods).
        /// </summary>
        private void OnModMessageReceived(object sender, ModMessageReceivedEventArgs e)
        {
            if (e.FromModID != ModManifest.UniqueID)
            {
                return;
            }

            switch (e.Type)
            {
                case MultiplayerMessageTypes.RequestSignContract:
                    // Host-only: a farmhand (or this same client, if a playerIDs:null send ever
                    // self-delivers) is asking to sign a contract. Never act on this unless we're actually
                    // the authoritative host - see SignContractAsHost's own doc comment on why the guard
                    // lives here at the call site rather than inside ContractManager.
                    if (Context.IsMainPlayer)
                    {
                        RequestSignContractMessage request = e.ReadAs<RequestSignContractMessage>();
                        contractManager.SignContractAsHost(request.ItemId, request.DueDateKind, request.RequesterPlayerId);
                    }
                    break;

                case MultiplayerMessageTypes.ContractSigned:
                    // Every client (including the host, harmlessly - see ApplyContractSignedFromNetwork's
                    // ContractId dedupe) applies the confirmed contract to its own local state.
                    {
                        ContractSignedMessage confirmed = e.ReadAs<ContractSignedMessage>();
                        contractManager.ApplyContractSignedFromNetwork(confirmed.Contract);
                    }
                    break;

                case MultiplayerMessageTypes.RequestDeliver:
                    // Host-only: a farmhand is asking whether there's a contract to fulfill for an item they
                    // already have in hand. See TryLockNextDeliverableContractAsHost's own doc comment for
                    // why no inventory check happens here.
                    if (Context.IsMainPlayer)
                    {
                        RequestDeliverMessage deliverRequest = e.ReadAs<RequestDeliverMessage>();
                        contractManager.HandleDeliverRequestAsHost(deliverRequest.ItemId, deliverRequest.RequesterPlayerId);
                    }
                    break;

                case MultiplayerMessageTypes.DeliverApproved:
                    // Requester-side only (the network layer only delivers this to the targeted playerIDs -
                    // see MultiplayerHelper's own routing, so no extra "is this for me" check is needed
                    // here): let FuturesMenu (if open) consume the item and credit the money.
                    {
                        DeliverApprovedMessage approved = e.ReadAs<DeliverApprovedMessage>();
                        contractManager.NotifyDeliverApproved(approved);
                    }
                    break;

                case MultiplayerMessageTypes.DeliverRejected:
                    // Requester-side only: let FuturesMenu (if open) show "nothing to deliver" feedback.
                    {
                        DeliverRejectedMessage rejected = e.ReadAs<DeliverRejectedMessage>();
                        contractManager.NotifyDeliverRejected(rejected);
                    }
                    break;

                case MultiplayerMessageTypes.DeliverConfirmed:
                    // Host-only: a requester's approved delivery succeeded locally - finalize the lock as Fulfilled and broadcast.
                    if (Context.IsMainPlayer)
                    {
                        DeliverConfirmedMessage confirmedDelivery = e.ReadAs<DeliverConfirmedMessage>();
                        contractManager.HandleDeliverConfirmedAsHost(confirmedDelivery.ContractId);
                    }
                    break;

                case MultiplayerMessageTypes.DeliverFailed:
                    // Host-only: a requester's approved delivery could not be completed locally - release the tentative lock.
                    if (Context.IsMainPlayer)
                    {
                        DeliverFailedMessage failedDelivery = e.ReadAs<DeliverFailedMessage>();
                        contractManager.HandleDeliverFailedAsHost(failedDelivery.ContractId);
                    }
                    break;

                case MultiplayerMessageTypes.ContractFulfilled:
                    // Every client (including the host, harmlessly - see ApplyContractFulfilledFromNetwork's status guard) applies the finalized status to its own local state.
                    {
                        ContractFulfilledMessage fulfilled = e.ReadAs<ContractFulfilledMessage>();
                        contractManager.ApplyContractFulfilledFromNetwork(fulfilled.ContractId);
                    }
                    break;

                case MultiplayerMessageTypes.ContractStateSync:
                    // Every client (including the host - see ApplyContractStateSyncFromNetwork's own doc
                    // comment on why it never needs to apply its own broadcast) wholesale-replaces local
                    // Contracts/price history and rebuilds the quest log from the snapshot.
                    {
                        ContractStateSyncMessage sync = e.ReadAs<ContractStateSyncMessage>();
                        contractManager.ApplyContractStateSyncFromNetwork(sync);
                    }
                    break;

                default:
                    Monitor.Log($"ModEntry: received unrecognized MyFirstMod multiplayer message type '{e.Type}' from player {e.FromPlayerID}.", LogLevel.Warn);
                    break;
            }
        }

        /// <summary>
        /// Plan.md section 7 phase 4: when a peer's connection is approved by the game (confirmed via
        /// decompile in phase 1 - this fires after PeerContextReceived, once the joining player's Farmer
        /// actually exists), the host proactively sends them a full ContractStateSyncMessage so a mid-session
        /// join doesn't have to wait for the next default/price-update broadcast to see contracts and price
        /// history that already existed before they connected.
        ///
        /// Gated on Context.IsMainPlayer regardless of whether this event turns out to fire only on the host
        /// or on every connected client (phase 1 left this unconfirmed) - only the host should ever be the
        /// one sending a state snapshot, so the guard is correct either way.
        /// </summary>
        private void OnPeerConnected(object sender, PeerConnectedEventArgs e)
        {
            if (!Context.IsMainPlayer)
            {
                return;
            }

            contractManager.SendFullStateSyncTo(e.Peer.PlayerID);
        }

        /// <summary>
        /// Injects ContractManager.PendingDefaultMail into Data/mail every time that asset loads (title
        /// screen, save load, or after the InvalidateCache call in OnDayEnding). This is the standard SMAPI
        /// 4.x way to add/modify content assets - the older IAssetEditor/Helper.Content.AssetEditors API
        /// this mod's design doc referenced during research is gone as of this SMAPI version. Each call just
        /// re-applies the dictionary's current contents, so it's safe to fire before any default has ever
        /// happened (nothing to add yet) or many times across a session (idempotent overwrite of the same
        /// keys).
        /// </summary>
        private void OnAssetRequested(object sender, AssetRequestedEventArgs e)
        {
            if (!e.NameWithoutLocale.IsEquivalentTo("Data/mail"))
            {
                return;
            }

            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, string>().Data;
                foreach (var entry in contractManager.PendingDefaultMail)
                {
                    data[entry.Key] = entry.Value;
                }
            });
        }
    }
}