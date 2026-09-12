using System;
using MyFirstMod;
using Xunit;

namespace MyFirstMod.Tests
{
    /// <summary>
    /// Covers the scenarios the user specified for plan.md section 6's market-pricing model. Like
    /// SettlementMathTests, this exercises PriceModel directly - it has zero SMAPI/Game1 dependency (see
    /// its own header comment), so every layer's pure function can be driven from a plain unit test.
    /// </summary>
    public class PriceModelTests
    {
        // ---- Layer 1: mean reversion direction ----

        [Fact]
        public void ComputeMeanReversion_BelowBaseline_PullsUpTowardBaseline()
        {
            double reverted = PriceModel.ComputeMeanReversion(yesterdayPrice: 80, nativeBasePrice: 100);

            Assert.True(reverted > 80, "price below baseline must be pulled up");
            Assert.True(reverted < 100, "reversion is partial (k=0.2), must not overshoot the baseline");
            Assert.Equal(84.0, reverted, 5); // 80 + 0.2*(100-80) = 84
        }

        [Fact]
        public void ComputeMeanReversion_AboveBaseline_PullsDownTowardBaseline()
        {
            double reverted = PriceModel.ComputeMeanReversion(yesterdayPrice: 120, nativeBasePrice: 100);

            Assert.True(reverted < 120, "price above baseline must be pulled down");
            Assert.True(reverted > 100, "reversion is partial (k=0.2), must not overshoot the baseline");
            Assert.Equal(116.0, reverted, 5); // 120 + 0.2*(100-120) = 116
        }

        [Fact]
        public void ComputeMeanReversion_AtBaseline_StaysUnchanged()
        {
            Assert.Equal(100.0, PriceModel.ComputeMeanReversion(yesterdayPrice: 100, nativeBasePrice: 100), 5);
        }

        // ---- Clamp bounds ----

        [Theory]
        [InlineData(1)]      // extreme low - mean reversion alone can't recover it in one day
        [InlineData(100000)] // extreme high
        public void ComputeDailyPrice_ExtremeYesterdayPrice_NeverExceedsClampRange(int yesterdayPrice)
        {
            const int nativeBasePrice = 200;
            PriceModel.PriceRegime regime = PriceModel.PriceRegime.Initial;
            Random rng = new Random(1);

            // Winter 25 + Friday deliberately stacks the festival pulse, weekly offset and trend layers all
            // in whatever direction they push, so this exercises the clamp against every other layer firing
            // at once, not just mean reversion in isolation.
            (int price, _) = PriceModel.ComputeDailyPrice(yesterdayPrice, nativeBasePrice, PriceModel.BeerBeta, Season.Winter, 25, DayOfWeek.Friday, regime, rng);

            int lower = (int)Math.Round(nativeBasePrice * PriceModel.ClampLowerMultiplier);
            int upper = (int)Math.Round(nativeBasePrice * PriceModel.ClampUpperMultiplier);
            Assert.InRange(price, lower, upper);
        }

        [Fact]
        public void ComputeDailyPrice_ExtremeHighWithMaxJump_StillClampsToUpperBound()
        {
            const int nativeBasePrice = 200;
            int dayOfYear = PriceModel.ComputeDayOfYear(Season.Spring, 1); // no festival, no trend anchor nearby
            PriceModel.PriceRegime regime = new PriceModel.PriceRegime(dayOfYear, jumpTriggeredToday: true, jumpSignedMagnitudeToday: PriceModel.JumpMaxMagnitude);

            (int price, _) = PriceModel.ComputeDailyPrice(100000, nativeBasePrice, PriceModel.BeerBeta, Season.Spring, 1, DayOfWeek.Wednesday, regime, new Random(1));

            int upper = (int)Math.Round(nativeBasePrice * PriceModel.ClampUpperMultiplier);
            Assert.Equal(upper, price);
        }

        // ---- Layer 3: weekly cycle ----

        [Theory]
        [InlineData(DayOfWeek.Monday, -0.03)]
        [InlineData(DayOfWeek.Tuesday, -0.015)]
        [InlineData(DayOfWeek.Wednesday, 0.0)]
        [InlineData(DayOfWeek.Thursday, 0.015)]
        [InlineData(DayOfWeek.Friday, 0.03)]
        [InlineData(DayOfWeek.Saturday, 0.015)]
        [InlineData(DayOfWeek.Sunday, -0.015)]
        public void ComputeWeeklyOffset_MatchesParameterTable(DayOfWeek dayOfWeek, double expectedOffset)
        {
            Assert.Equal(expectedOffset, PriceModel.ComputeWeeklyOffset(dayOfWeek));
        }

        // ---- Layer 4: festival pulse ----

        [Theory]
        [InlineData(12, 0.0)]   // 4 days before Stardew Valley Fair (Fall 16) - outside the window
        [InlineData(13, 0.02)]  // 3 days before
        [InlineData(14, 0.04)]  // 2 days before
        [InlineData(15, 0.07)]  // 1 day before
        [InlineData(16, 0.10)]  // festival day - peak
        [InlineData(17, 0.03)]  // 1 day after
        [InlineData(18, -0.05)] // 2 days after
        [InlineData(19, 0.0)]   // 3 days after - outside the window
        public void ComputeFestivalPulse_MatchesWindowAroundStardewValleyFair(int fallDay, double expectedPulse)
        {
            int dayOfYear = PriceModel.ComputeDayOfYear(Season.Fall, fallDay);
            Assert.Equal(expectedPulse, PriceModel.ComputeFestivalPulse(dayOfYear));
        }

        [Fact]
        public void ComputeFestivalPulse_FarFromAnyFestival_IsZero()
        {
            int dayOfYear = PriceModel.ComputeDayOfYear(Season.Spring, 1); // nowhere near Egg Festival (Spring 13)'s window
            Assert.Equal(0.0, PriceModel.ComputeFestivalPulse(dayOfYear));
        }

        [Fact]
        public void Festivals_HasAllEightVanillaFestivals()
        {
            Assert.Equal(8, PriceModel.Festivals.Count);
        }

        // ---- Layer 7: trend direction, known anchors + cross-year wraparound ----

        [Theory]
        [InlineData(72, 1)]   // Fall Fair (anchor) itself: arrived, still counted as uptrend
        [InlineData(73, -1)]  // just past Fall Fair: downtrend begins
        [InlineData(90, -1)]  // last day of the short downtrend half (floor(37/2) = 18 days after the anchor)
        [InlineData(91, 1)]   // first day of the short uptrend half, now approaching the Winter Star anchor
        [InlineData(109, 1)]  // Feast of the Winter Star (anchor) itself: arrived, still uptrend
        [InlineData(110, -1)] // just past the Winter Star: downtrend begins (the long half)
        [InlineData(146, -1)] // last day of the long downtrend half (floor(75/2) = 37 days after the anchor)
        [InlineData(147, 1)]  // first day of the long uptrend half, approaching next year's Fall Fair
        public void ComputeTrendDirection_KnownAnchorDates_MatchesExpectedDirection(int dayOfYear, int expectedDirection)
        {
            Assert.Equal(expectedDirection, PriceModel.ComputeTrendDirection(dayOfYear));
        }

        [Fact]
        public void ComputeTrendDirection_YearBoundary_Day112AndNextYearDay1BothStayInSameDowntrendSegment()
        {
            // Winter 28 (dayOfYear 112, last day of the year) is 3 days after the Winter Star anchor (109);
            // day 1 of the following year is 4 days after it. Both must land in the same "just past the
            // anchor" downtrend half (halfGapB = 37), exercising the cyclic wraparound at the year boundary.
            Assert.Equal(-1, PriceModel.ComputeTrendDirection(112));
            Assert.Equal(-1, PriceModel.ComputeTrendDirection(1));
        }

        [Fact]
        public void ComputeTrendDirection_CrossYearWraparound_MatchesEquivalentDayInNextCycle()
        {
            // dayOfYear 35 and dayOfYear 35 + DaysPerYear (147, i.e. "day 35 of the following year") must
            // resolve to the identical direction - the model only cares about position within the yearly
            // cycle, not which year it is.
            int direction = PriceModel.ComputeTrendDirection(35);
            int directionNextCycle = PriceModel.ComputeTrendDirection(35 + PriceModel.DaysPerYear);

            Assert.Equal(direction, directionNextCycle);
        }

        // ---- Beta scaling: same shared shock, different betas ----

        [Fact]
        public void ComputeDailyPrice_BetaScaling_SameSharedShock_ProducesDifferentMagnitudeSameDirection()
        {
            const int nativeBasePrice = 200;
            const int yesterdayPrice = 200;
            int dayOfYear = PriceModel.ComputeDayOfYear(Season.Spring, 5); // away from any festival window
            PriceModel.PriceRegime jumpRegime = new PriceModel.PriceRegime(dayOfYear, jumpTriggeredToday: true, jumpSignedMagnitudeToday: 0.30);
            PriceModel.PriceRegime noJumpRegime = new PriceModel.PriceRegime(dayOfYear, jumpTriggeredToday: false, jumpSignedMagnitudeToday: 0.0);

            // Fresh same-seed Random per call: since the regime already carries today's jump decision,
            // ComputeDailyPrice never draws for the jump here - the first (and only) draw is layer 2's
            // noise, so an identical seed makes every non-jump layer identical across all three calls,
            // isolating the jump/beta term as the only source of difference.
            (int priceBeer, _) = PriceModel.ComputeDailyPrice(yesterdayPrice, nativeBasePrice, PriceModel.BeerBeta, Season.Spring, 5, DayOfWeek.Wednesday, jumpRegime, new Random(42));
            (int pricePaleAle, _) = PriceModel.ComputeDailyPrice(yesterdayPrice, nativeBasePrice, PriceModel.PaleAleBeta, Season.Spring, 5, DayOfWeek.Wednesday, jumpRegime, new Random(42));
            (int priceNoJump, _) = PriceModel.ComputeDailyPrice(yesterdayPrice, nativeBasePrice, PriceModel.BeerBeta, Season.Spring, 5, DayOfWeek.Wednesday, noJumpRegime, new Random(42));

            Assert.True(priceBeer > priceNoJump, "beta=1.0 with a positive shared shock must push the price up");
            Assert.True(pricePaleAle > priceNoJump, "beta=0.6 with the same positive shared shock must also push the price up (same direction)");
            Assert.True(priceBeer > pricePaleAle, "beta=1.0 must be pushed further than beta=0.6 by the same shared shock (different magnitude)");
        }

        // ---- Fixed-seed reproducibility ----

        [Fact]
        public void ComputeDailyPrice_FixedSeed_ProducesIdenticalResultsAcrossRuns()
        {
            PriceModel.PriceRegime regime = PriceModel.PriceRegime.Initial;

            (int price1, PriceModel.PriceRegime regime1) = PriceModel.ComputeDailyPrice(200, 200, PriceModel.BeerBeta, Season.Summer, 10, DayOfWeek.Tuesday, regime, new Random(12345));
            (int price2, PriceModel.PriceRegime regime2) = PriceModel.ComputeDailyPrice(200, 200, PriceModel.BeerBeta, Season.Summer, 10, DayOfWeek.Tuesday, regime, new Random(12345));

            Assert.Equal(price1, price2);
            Assert.Equal(regime1.JumpTriggeredToday, regime2.JumpTriggeredToday);
            Assert.Equal(regime1.JumpSignedMagnitudeToday, regime2.JumpSignedMagnitudeToday);
            Assert.Equal(regime1.LastAdvancedDayOfYear, regime2.LastAdvancedDayOfYear);
        }

        [Fact]
        public void ComputeDailyPrice_SameDaySecondCall_ReusesRegimeInsteadOfRedrawingJump()
        {
            // Simulates ContractManager.UpdateDailyPrices calling Beer then Pale Ale the same day: the
            // second call must receive the exact PriceRegime the first call returned, unchanged (the shared
            // jump for the day was already decided), not draw an independent one.
            PriceModel.PriceRegime regime = PriceModel.PriceRegime.Initial;
            Random rng = new Random(7);

            (_, PriceModel.PriceRegime regimeAfterFirst) = PriceModel.ComputeDailyPrice(200, 200, PriceModel.BeerBeta, Season.Spring, 5, DayOfWeek.Wednesday, regime, rng);
            (_, PriceModel.PriceRegime regimeAfterSecond) = PriceModel.ComputeDailyPrice(200, 200, PriceModel.PaleAleBeta, Season.Spring, 5, DayOfWeek.Wednesday, regimeAfterFirst, rng);

            Assert.Equal(regimeAfterFirst.JumpTriggeredToday, regimeAfterSecond.JumpTriggeredToday);
            Assert.Equal(regimeAfterFirst.JumpSignedMagnitudeToday, regimeAfterSecond.JumpSignedMagnitudeToday);
            Assert.Equal(regimeAfterFirst.LastAdvancedDayOfYear, regimeAfterSecond.LastAdvancedDayOfYear);
        }
    }
}
