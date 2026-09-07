using System;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace MyFirstMod.Patches
{
    /// <summary>
    /// Intercepts Pierre's SeedShop opening to offer a "Buy goods / Futures trading" choice before the
    /// vanilla ShopMenu appears.
    ///
    /// Both Robin's Carpenter shop and Pierre's SeedShop are triggered by a map tile's "Action" property
    /// (not by clicking the NPC sprite), and they are NOT the same base method: Robin's flow is a bespoke
    /// GameLocation.carpenters() that already shows a multi-choice dialogue before opening anything, while
    /// Pierre's SeedShop has no such step - it calls StardewValley.Utility.TryOpenShopMenu directly. So
    /// instead of patching a shared "click NPC" method (there isn't one), this patches TryOpenShopMenu
    /// itself with a Postfix, gated on shopId == "SeedShop". This runs only AFTER vanilla's own
    /// open/closed-hours/owner-presence checks already succeeded, so none of that logic is duplicated here,
    /// and it naturally leaves Robin's "Carpenter" shop id (and every other shop id) untouched.
    /// </summary>
    internal static class PierreShopPatch
    {
        private const string SeedShopId = "SeedShop";

        internal static IMonitor Monitor;
        internal static ContractManager ContractManager;

        // Guard against re-entrancy: choosing "Buy goods" re-calls TryOpenShopMenu so vanilla actually
        // opens the ShopMenu, which would otherwise trigger this same postfix again.
        private static bool isReopeningVanillaShop;

        internal static void Apply(Harmony harmony)
        {
            MethodInfo original = AccessTools.Method(
                typeof(Utility),
                nameof(Utility.TryOpenShopMenu),
                new[] { typeof(string), typeof(GameLocation), typeof(Rectangle?), typeof(int?), typeof(bool), typeof(bool), typeof(Action<string>) });

            harmony.Patch(original, postfix: new HarmonyMethod(typeof(PierreShopPatch), nameof(TryOpenShopMenu_Postfix)));
        }

        private static void TryOpenShopMenu_Postfix(
            string shopId,
            GameLocation location,
            Rectangle? ownerArea,
            int? maxOwnerY,
            bool forceOpen,
            bool playOpenSound,
            Action<string> showClosedMessage,
            bool __result)
        {
            if (isReopeningVanillaShop || !__result || shopId != SeedShopId || Game1.activeClickableMenu is not ShopMenu)
            {
                return;
            }

            // Vanilla just opened Pierre's shop successfully; swap it for our choice dialogue instead.
            Game1.activeClickableMenu = null;

            location.createQuestionDialogue(
                "你想做点什么？",
                new[]
                {
                    new Response("Buy", "购买商品"),
                    new Response("Futures", "期货交易"),
                    new Response("Leave", "算了"),
                },
                (Farmer who, string whichAnswer) =>
                {
                    switch (whichAnswer)
                    {
                        case "Buy":
                            isReopeningVanillaShop = true;
                            try
                            {
                                Utility.TryOpenShopMenu(shopId, location, ownerArea, maxOwnerY, forceOpen, playOpenSound, showClosedMessage);
                            }
                            finally
                            {
                                isReopeningVanillaShop = false;
                            }
                            break;

                        case "Futures":
                            Game1.activeClickableMenu = new FuturesMenu(Monitor, ContractManager);
                            break;
                    }
                });
        }
    }
}
