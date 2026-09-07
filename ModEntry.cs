using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.GameData.Objects;
using System;
using System.Linq;
using MyFirstMod.Patches;

namespace MyFirstMod
{
    public class ModEntry : Mod
    {
        private Random rng = new Random();
        private const int BeerBasePrice = 200; // vanilla base price for reference

        // Key ContractManager's data is filed under via Helper.Data.WriteSaveData/ReadSaveData; shared
        // between the write and read side so they can't drift apart into two different strings.
        private const string ContractsSaveDataKey = "FuturesContracts";

        private ContractManager contractManager;

        public override void Entry(IModHelper helper)
        {
            contractManager = new ContractManager(Monitor);
            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.GameLoop.Saving += OnSaving;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.DayEnding += OnDayEnding;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.Content.AssetRequested += OnAssetRequested;

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

            if (e.Button == SButton.F5)
            {
                var data = Game1.objectData;
                if (data.TryGetValue(ContractManager.BeerItemId, out ObjectData beerData))
                {
                    double factor = 0.75 + rng.NextDouble() * 0.5;
                    int newPrice = Math.Max(1, (int)Math.Round(BeerBasePrice * factor));
                    beerData.Price = newPrice;

                    Helper.GameContent.InvalidateCache("Data/Objects");

                    // Also update any beer already sitting in the player's inventory
                    foreach (var item in Game1.player.Items)
                    {
                        if (item is StardewValley.Object obj && obj.ItemId == ContractManager.BeerItemId)
                        {
                            obj.Price = newPrice;
                        }
                    }

                    Monitor.Log($"Beer price changed to: {newPrice}g (factor {factor:F2})", LogLevel.Info);
                }
            }
        }

        /// <summary>Fires while a save is being written to disk. Snapshots ContractManager.Contracts into the current save slot's data.</summary>
        private void OnSaving(object sender, SavingEventArgs e)
        {
            Helper.Data.WriteSaveData(ContractsSaveDataKey, contractManager.ToSaveData());
        }

        /// <summary>
        /// Fires once per save load (title screen "Continue"/"Load", including after a full game restart).
        /// Restores ContractManager.Contracts from this save slot's data (null for a brand-new save, or the
        /// mod's first run on an existing one - LoadFromSaveData treats that as "start empty", not an error).
        /// The [DIAG] dump/mismatch check below predates persistence (added to confirm the quest-log vs
        /// contract-list gap this was built to fix); kept as a standing regression check - if it ever fires
        /// "Mismatch confirmed" again after this change, persistence broke.
        /// </summary>
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            List<FuturesContractSaveData> saveData = Helper.Data.ReadSaveData<List<FuturesContractSaveData>>(ContractsSaveDataKey);
            contractManager.LoadFromSaveData(saveData);

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
        /// </summary>
        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            contractManager.SettleDefaults(SDate.Now());

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
        /// without needing a restart. Calls ContractManager.RebuildQuestLog() as the design doc 6.5
        /// self-heal: makes the quest log match Contracts every morning regardless of how it got out of
        /// sync, on top of the immediate updates SignContract/FulfillContract already do. The [DIAG] count
        /// log predates this (added to diagnose the persistence gap); a same-session drop in this count not
        /// explained by deliveries/defaults would mean Contracts is being cleared by something other than a
        /// restart - a different, unrelated bug from what that diagnostic was built to find.
        /// </summary>
        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            contractManager.RebuildQuestLog();
            Monitor.Log($"[DIAG] DayStarted: ContractManager.Contracts count = {contractManager.Contracts.Count}", LogLevel.Info);
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