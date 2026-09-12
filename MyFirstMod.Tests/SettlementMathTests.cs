using MyFirstMod;
using Xunit;

namespace MyFirstMod.Tests
{
    /// <summary>
    /// Covers the boundary scenarios the user specified for the margin-direction reversal (plan.md 5.5:
    /// Pierre pays the margin to the player at signing; SettleDefaults forfeits it back on default, summed
    /// with the price-gap penalty into one total before clamping).
    ///
    /// These tests exercise SettlementMath directly rather than ContractManager.SignContract/SettleDefaults
    /// themselves: those two methods touch Game1.player, Game1.player.questLog, and ItemRegistry.Create,
    /// all of which need a loaded game/content manager and can't be driven from a plain unit test without
    /// one. SettlementMath is the exact arithmetic both methods delegate to (see its own header comment and
    /// ContractManager's call sites) - not a parallel reimplementation - so these tests cover the real
    /// money-math behavior even though they can't call SignContract/SettleDefaults by name.
    /// </summary>
    public class SettlementMathTests
    {
        // ---- Scenario 1: normal default, balance comfortably covers margin + price-gap penalty ----
        [Fact]
        public void ComputeDefaultDeduction_SufficientBalance_DeductsMarginPlusPenaltyInFull()
        {
            int margin = 45;
            int priceGapPenalty = 20;
            int playerMoney = 1000;

            int actualDeduction = SettlementMath.ComputeDefaultDeduction(margin, priceGapPenalty, playerMoney);

            Assert.Equal(65, actualDeduction);
            Assert.Equal(935, playerMoney - actualDeduction);
        }

        // ---- Scenario 2: price-gap penalty is 0 (market price didn't rise enough) - only margin is owed ----
        [Fact]
        public void ComputePriceGapPenalty_MarketPriceTooLow_ClampsToZero()
        {
            // marketPrice * 1.10 = 44, agreedPrice = 50 -> raw would be -6, must clamp to 0.
            int priceGapPenalty = SettlementMath.ComputePriceGapPenalty(marketPrice: 40, agreedPrice: 50);

            Assert.Equal(0, priceGapPenalty);
        }

        [Fact]
        public void ComputeDefaultDeduction_ZeroPriceGapPenalty_OnlyMarginIsDeducted()
        {
            int margin = 5;
            int priceGapPenalty = SettlementMath.ComputePriceGapPenalty(marketPrice: 40, agreedPrice: 50);

            int actualDeduction = SettlementMath.ComputeDefaultDeduction(margin, priceGapPenalty, playerMoney: 1000);

            Assert.Equal(0, priceGapPenalty);
            Assert.Equal(5, actualDeduction);
        }

        // ---- Scenario 3: balance can't cover margin + price-gap penalty combined ----
        // This is the case most likely to be implemented wrong: clamping margin and the price-gap penalty
        // SEPARATELY (Min(money, margin) + Min(money, penalty)) would report 50 + 30 = 80 here, which
        // exceeds the player's real balance of 60 - a variant of going into debt. The correct behavior sums
        // margin + penalty into one total FIRST, then clamps once.
        [Fact]
        public void ComputeDefaultDeduction_InsufficientBalance_ClampsToPlayersFullBalance_NotSeparateClamps()
        {
            int margin = 50;
            int priceGapPenalty = 30;
            int playerMoney = 60;

            int actualDeduction = SettlementMath.ComputeDefaultDeduction(margin, priceGapPenalty, playerMoney);

            // Correct: Min(60, 50+30) = 60. Buggy "separate clamps" would give 80.
            Assert.Equal(60, actualDeduction);
            Assert.True(actualDeduction <= playerMoney, "actual deduction must never exceed the player's balance");
            Assert.Equal(0, playerMoney - actualDeduction);
        }

        // ---- Scenario 4: player has 0 money - must not throw, must not go negative ----
        [Fact]
        public void ComputeDefaultDeduction_ZeroBalance_DeductsNothing_NoException()
        {
            int margin = 45;
            int priceGapPenalty = 10;
            int playerMoney = 0;

            int actualDeduction = SettlementMath.ComputeDefaultDeduction(margin, priceGapPenalty, playerMoney);

            Assert.Equal(0, actualDeduction);
        }

        // ---- Scenario 5: multiple contracts default the same day; each settlement must use the running
        // balance left AFTER the previous contract's deduction, not the original balance for every contract.
        // This simulates ContractManager.SettleDefaults' foreach loop (call -> subtract -> next call). ----
        [Fact]
        public void ComputeDefaultDeduction_SequentialContractsSameDay_EachUsesUpdatedRunningBalance()
        {
            int money = 100;

            // Contract A: margin 30 + penalty 20 = 50 owed; balance (100) covers it in full.
            int deductionA = SettlementMath.ComputeDefaultDeduction(margin: 30, priceGapPenalty: 20, playerMoney: money);
            money -= deductionA;

            Assert.Equal(50, deductionA);
            Assert.Equal(50, money);

            // Contract B: margin 40 + penalty 30 = 70 owed, but only 50 is left after contract A.
            // Must clamp against the POST-A balance (50), not the original 100.
            int deductionB = SettlementMath.ComputeDefaultDeduction(margin: 40, priceGapPenalty: 30, playerMoney: money);
            money -= deductionB;

            Assert.Equal(50, deductionB);
            Assert.Equal(0, money);
        }

        // ---- Scenario 6: SignContract's payout amount must match the contract's own Margin field ----
        // FuturesContract.Margin and ContractManager.SignContract's payout (Game1.player.Money +=
        // contract.Margin) both resolve through this single function (see FuturesContract.cs and
        // ContractManager.SignContract), so there is no separate "amount paid" computation that could
        // drift from the field - this test locks down the shared formula both call sites depend on.
        [Theory]
        [InlineData(450, 45)]  // 450 * 0.10 = 45.0, no rounding ambiguity
        [InlineData(200, 20)]  // 200 * 0.10 = 20.0
        [InlineData(451, 45)]  // 451 * 0.10 = 45.1 -> rounds down to 45
        public void ComputeMargin_MatchesExpectedTenPercent(int agreedPrice, int expectedMargin)
        {
            Assert.Equal(expectedMargin, SettlementMath.ComputeMargin(agreedPrice));
        }

        // ---- Scenario 7: plan.md 5.5's 2026-09-13 clarification - margin is a prepayment of AgreedPrice,
        // not an independent bonus. Delivery must pay AgreedPrice minus the margin already paid at signing,
        // not the full AgreedPrice (which would let the player net AgreedPrice + Margin overall). ----
        [Theory]
        [InlineData(450, 45, 405)]
        [InlineData(200, 20, 180)]
        public void ComputeDeliveryPayout_SubtractsMarginFromAgreedPrice(int agreedPrice, int margin, int expectedPayout)
        {
            Assert.Equal(expectedPayout, SettlementMath.ComputeDeliveryPayout(agreedPrice, margin));
        }

        [Fact]
        public void ComputeDeliveryPayout_PlusMarginAlreadyPaid_EqualsAgreedPrice()
        {
            // The whole point of the margin-as-prepayment model: margin paid at signing + payout paid at
            // delivery must sum to exactly AgreedPrice - not AgreedPrice + Margin (the bug this fixes).
            int agreedPrice = 300;
            int margin = SettlementMath.ComputeMargin(agreedPrice);

            int payout = SettlementMath.ComputeDeliveryPayout(agreedPrice, margin);

            Assert.Equal(agreedPrice, margin + payout);
        }
    }
}
