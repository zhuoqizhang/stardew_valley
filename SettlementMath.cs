using System;

namespace MyFirstMod
{
    /// <summary>
    /// Pure money math for futures signing/settlement (design doc 1.2/2.1/5.1/5.4/5.5), split out from
    /// <see cref="ContractManager"/> and <see cref="FuturesContract"/> so it has zero dependency on
    /// Game1/SMAPI/StardewValley types. ContractManager's own methods can't be exercised in a plain unit
    /// test without a running game instance (they touch Game1.player, Game1.player.questLog, and
    /// ItemRegistry.Create, all of which need a loaded game/content manager) - this class exists so the
    /// actual arithmetic they rely on can still be unit-tested directly. ContractManager and
    /// FuturesContract are both thin callers of these methods, not independent reimplementations, so the
    /// two can't drift apart.
    /// </summary>
    public static class SettlementMath
    {
        /// <summary>Design doc 1.2/2.1: the margin is 10% of the agreed price, rounded to the nearest gold.</summary>
        public static int ComputeMargin(int agreedPrice)
        {
            return (int)Math.Round(agreedPrice * 0.10);
        }

        /// <summary>
        /// Design doc 1.2: the price-gap penalty a defaulted contract owes on top of the margin -
        /// max(0, marketPrice * 1.10 - agreedPrice), rounded to the nearest gold. Floored at 0 rather than
        /// going negative: if the market price hasn't risen enough above the agreed price, there's no
        /// price-gap penalty (the margin forfeiture from <see cref="ComputeMargin"/> still applies via
        /// <see cref="ComputeDefaultDeduction"/>).
        /// </summary>
        public static int ComputePriceGapPenalty(int marketPrice, int agreedPrice)
        {
            return Math.Max(0, (int)Math.Round(marketPrice * 1.10 - agreedPrice, MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// Design doc 5.5 (2026-09-13 clarification: margin is a prepayment of part of the agreed price,
        /// not an independent bonus kept on top): a successfully delivered contract pays the agreed price
        /// MINUS the margin already paid to the player at signing (SignContractAsHost), so the two payouts
        /// together - margin now, this at delivery - sum to exactly AgreedPrice, never more. Deliberately
        /// takes margin as a parameter rather than recomputing ComputeMargin(agreedPrice) internally,
        /// mirroring ComputeDefaultDeduction's own style - the caller already has (or can cheaply derive)
        /// margin, so there's one obvious place callers get it from rather than two ways to compute the
        /// same number.
        /// </summary>
        public static int ComputeDeliveryPayout(int agreedPrice, int margin)
        {
            return agreedPrice - margin;
        }

        /// <summary>
        /// Design doc 5.4/5.5 default-settlement deduction: forfeits the margin (paid TO the player by
        /// Pierre at signing, not held in escrow) plus the price-gap penalty. The two are summed into one
        /// total BEFORE clamping to the player's current money - deliberately not two separate
        /// Min(playerMoney, ...) clamps - so the combined deduction never exceeds what the player actually
        /// has and never drives their money negative. Clamping margin and the price-gap penalty separately
        /// would let each individual clamp report "not deducting more than the player has" while their sum
        /// still does.
        /// </summary>
        public static int ComputeDefaultDeduction(int margin, int priceGapPenalty, int playerMoney)
        {
            int totalOwed = margin + priceGapPenalty;
            return Math.Min(playerMoney, totalOwed);
        }
    }
}
