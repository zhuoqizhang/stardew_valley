using System;
using System.Collections.Generic;

namespace MyFirstMod
{
    public enum Season
    {
        Spring,
        Summer,
        Fall,
        Winter
    }

    /// <summary>
    /// Pure logic for the daily market-price simulation (plan.md section 6), split out so it has zero
    /// dependency on Game1/SMAPI/StardewValley types - same rationale as <see cref="SettlementMath"/>.
    /// Every layer described in plan.md 6.2 is its own testable static function; <see cref="ComputeDailyPrice"/>
    /// is the aggregate that chains them in the documented order.
    ///
    /// Transitional note (plan.md 6.6): this class is wired into ContractManager's daily price-history
    /// update, NOT into ContractManager.GetMarketPrice - the latter still returns the item's real native
    /// sell price until the simulation is verified and the user explicitly asks for the switch.
    /// </summary>
    public static class PriceModel
    {
        // ---- Layer 1: mean reversion ----
        public const double MeanReversionK = 0.2;

        // ---- Layer 2: independent noise ----
        public const double NoiseMinMagnitude = 0.04;
        public const double NoiseMaxMagnitude = 0.05;

        // ---- Layer 3: weekly cycle (plan.md 6.2 table) ----
        public const double WeeklyOffsetMonday = -0.03;
        public const double WeeklyOffsetTuesday = -0.015;
        public const double WeeklyOffsetWednesday = 0.0;
        public const double WeeklyOffsetThursday = 0.015;
        public const double WeeklyOffsetFriday = 0.03;
        public const double WeeklyOffsetSaturday = 0.015;
        public const double WeeklyOffsetSunday = -0.015;

        // ---- Layer 4: festival pulse ----
        public const int FestivalWindowDaysBefore = 3;
        public const int FestivalWindowDaysAfter = 2;

        public const double FestivalPulseDayBefore3 = 0.02;
        public const double FestivalPulseDayBefore2 = 0.04;
        public const double FestivalPulseDayBefore1 = 0.07;
        public const double FestivalPulseDayOf = 0.10;
        public const double FestivalPulseDayAfter1 = 0.03;
        public const double FestivalPulseDayAfter2 = -0.05;

        // The 8 vanilla Stardew Valley festivals, as (season, day-of-month). Confirmed vanilla calendar
        // dates - not decompiled (these are content-table facts, not uncertain API behavior).
        public const int EggFestivalDay = 13; // Spring
        public const int FlowerDanceDay = 24; // Spring
        public const int LuauDay = 11; // Summer
        public const int DanceOfMoonlightJelliesDay = 28; // Summer
        public const int StardewValleyFairDay = 16; // Fall ("秋季集市") - also a trend anchor, see layer 7
        public const int SpiritsEveDay = 27; // Fall
        public const int FestivalOfIceDay = 8; // Winter
        public const int FeastOfWinterStarDay = 25; // Winter ("盛宴节"/"冬日盛宴") - also a trend anchor

        public static readonly IReadOnlyList<(Season Season, int Day)> Festivals = new (Season, int)[]
        {
            (Season.Spring, EggFestivalDay),
            (Season.Spring, FlowerDanceDay),
            (Season.Summer, LuauDay),
            (Season.Summer, DanceOfMoonlightJelliesDay),
            (Season.Fall, StardewValleyFairDay),
            (Season.Fall, SpiritsEveDay),
            (Season.Winter, FestivalOfIceDay),
            (Season.Winter, FeastOfWinterStarDay),
        };

        private static readonly IReadOnlyDictionary<int, double> FestivalPulseByDayDelta = new Dictionary<int, double>
        {
            [-3] = FestivalPulseDayBefore3,
            [-2] = FestivalPulseDayBefore2,
            [-1] = FestivalPulseDayBefore1,
            [0] = FestivalPulseDayOf,
            [1] = FestivalPulseDayAfter1,
            [2] = FestivalPulseDayAfter2,
        };

        // ---- Layer 5: extreme events (jumps) ----
        public const double JumpProbability = 0.08;
        public const double JumpMinMagnitude = 0.25;
        public const double JumpMaxMagnitude = 0.40;

        // ---- Layer 6: season multiplier ----
        public const double SeasonMultiplierSpring = 1.0;
        public const double SeasonMultiplierSummer = 1.1;
        public const double SeasonMultiplierFall = 1.0;
        public const double SeasonMultiplierWinter = 0.9;

        // ---- Layer 7: long trend (dual anchor) ----
        public const int DaysPerYear = 112; // 4 seasons x 28 days
        public const int TrendAnchorFallFairDay = FallSeasonStartDayOfYear + StardewValleyFairDay; // dayOfYear 72 (Fall 16)
        public const int TrendAnchorWinterStarDay = WinterSeasonStartDayOfYear + FeastOfWinterStarDay; // dayOfYear 109 (Winter 25)
        public const double TrendDailyRate = 0.015;

        private const int FallSeasonStartDayOfYear = 2 * 28; // must match SeasonStartDayOfYear(Season.Fall)
        private const int WinterSeasonStartDayOfYear = 3 * 28; // must match SeasonStartDayOfYear(Season.Winter)

        // ---- Clamp ----
        public const double ClampLowerMultiplier = 0.4;
        public const double ClampUpperMultiplier = 2.2;

        // ---- Cross-item beta (plan.md 6.3) ----
        public const double BeerBeta = 1.0;
        public const double PaleAleBeta = 0.6;

        /// <summary>
        /// Per-day shared state (plan.md 6.2 layer 5 / 6.3): the jump event is decided once per day and
        /// shared by every item that day (each item scales it by its own beta in <see cref="ComputeDailyPrice"/>).
        /// Stamped with the day it was drawn for so a second same-day call (the second item) reuses it
        /// instead of drawing an independent, unshared jump.
        /// </summary>
        public readonly struct PriceRegime
        {
            public int LastAdvancedDayOfYear { get; }
            public bool JumpTriggeredToday { get; }
            public double JumpSignedMagnitudeToday { get; }

            public PriceRegime(int lastAdvancedDayOfYear, bool jumpTriggeredToday, double jumpSignedMagnitudeToday)
            {
                LastAdvancedDayOfYear = lastAdvancedDayOfYear;
                JumpTriggeredToday = jumpTriggeredToday;
                JumpSignedMagnitudeToday = jumpSignedMagnitudeToday;
            }

            /// <summary>Sentinel "no day advanced yet" regime, e.g. for a brand-new save with no price history.</summary>
            public static PriceRegime Initial => new PriceRegime(int.MinValue, false, 0.0);
        }

        /// <summary>Layer 1: pulls yesterday's price a fraction (<see cref="MeanReversionK"/>) of the way back toward the item's native base price.</summary>
        public static double ComputeMeanReversion(int yesterdayPrice, int nativeBasePrice)
        {
            return yesterdayPrice + MeanReversionK * (nativeBasePrice - yesterdayPrice);
        }

        /// <summary>Layer 2: an independent random fraction in [-0.05,-0.04] union [0.04,0.05], a fresh draw per item per day.</summary>
        public static double SampleIndependentNoise(Random rng)
        {
            double magnitude = NoiseMinMagnitude + rng.NextDouble() * (NoiseMaxMagnitude - NoiseMinMagnitude);
            double sign = rng.NextDouble() < 0.5 ? -1.0 : 1.0;
            return sign * magnitude;
        }

        /// <summary>Layer 3: deterministic weekday offset (plan.md 6.2 table) - systematically higher near Friday, lower near Monday.</summary>
        public static double ComputeWeeklyOffset(DayOfWeek dayOfWeek)
        {
            switch (dayOfWeek)
            {
                case DayOfWeek.Monday: return WeeklyOffsetMonday;
                case DayOfWeek.Tuesday: return WeeklyOffsetTuesday;
                case DayOfWeek.Wednesday: return WeeklyOffsetWednesday;
                case DayOfWeek.Thursday: return WeeklyOffsetThursday;
                case DayOfWeek.Friday: return WeeklyOffsetFriday;
                case DayOfWeek.Saturday: return WeeklyOffsetSaturday;
                case DayOfWeek.Sunday: return WeeklyOffsetSunday;
                default: throw new ArgumentOutOfRangeException(nameof(dayOfWeek));
            }
        }

        /// <summary>Converts a (season, day-of-month) pair into a 1..112 day-of-year, cyclic across years.</summary>
        public static int ComputeDayOfYear(Season season, int dayOfMonth)
        {
            return SeasonStartDayOfYear(season) + dayOfMonth;
        }

        private static int SeasonStartDayOfYear(Season season)
        {
            switch (season)
            {
                case Season.Spring: return 0;
                case Season.Summer: return 28;
                case Season.Fall: return 56;
                case Season.Winter: return 84;
                default: throw new ArgumentOutOfRangeException(nameof(season));
            }
        }

        /// <summary>Layer 4: deterministic festival pulse (plan.md 6.2 table) - 0 outside every festival's [-3,+2] day window.</summary>
        public static double ComputeFestivalPulse(int dayOfYear)
        {
            foreach ((Season season, int day) in Festivals)
            {
                int festivalDayOfYear = ComputeDayOfYear(season, day);
                int delta = dayOfYear - festivalDayOfYear;
                if (FestivalPulseByDayDelta.TryGetValue(delta, out double offset))
                {
                    return offset;
                }
            }

            return 0.0;
        }

        /// <summary>Layer 6: deterministic season multiplier (plan.md 6.2 table).</summary>
        public static double ComputeSeasonMultiplier(Season season)
        {
            switch (season)
            {
                case Season.Spring: return SeasonMultiplierSpring;
                case Season.Summer: return SeasonMultiplierSummer;
                case Season.Fall: return SeasonMultiplierFall;
                case Season.Winter: return SeasonMultiplierWinter;
                default: throw new ArgumentOutOfRangeException(nameof(season));
            }
        }

        /// <summary>
        /// Layer 7 (plan.md 6.2): +1 (uptrend, approaching an anchor) or -1 (downtrend, just past an anchor).
        /// The two anchors - Fall Fair (dayOfYear 72) and Feast of the Winter Star (dayOfYear 109) - split
        /// the 112-day year into a 37-day gap and a 75-day gap; the direction flips at each anchor and again
        /// at the midpoint of whichever gap dayOfYear falls in, cyclically (handles year wraparound).
        /// </summary>
        public static int ComputeTrendDirection(int dayOfYear)
        {
            int gapA = TrendAnchorWinterStarDay - TrendAnchorFallFairDay; // 37
            int gapB = DaysPerYear - gapA; // 75

            int posFromA1 = Mod(dayOfYear - TrendAnchorFallFairDay, DaysPerYear);

            if (posFromA1 == 0)
            {
                return 1; // exactly on the Fall Fair anchor: still "arrived", counts as uptrend
            }

            if (posFromA1 <= gapA)
            {
                int halfGapA = gapA / 2;
                return posFromA1 <= halfGapA ? -1 : 1;
            }

            int posFromA2 = posFromA1 - gapA;
            int halfGapB = gapB / 2;
            return posFromA2 <= halfGapB ? -1 : 1;
        }

        private static int Mod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        /// <summary>Layer 5's per-day shared draw: 8% chance of a jump, magnitude 25%-40%, random sign. Called at most once per day (see <see cref="ComputeDailyPrice"/>'s regime reuse).</summary>
        private static PriceRegime DrawSharedJump(int dayOfYear, Random rng)
        {
            bool triggered = rng.NextDouble() < JumpProbability;
            if (!triggered)
            {
                return new PriceRegime(dayOfYear, false, 0.0);
            }

            double magnitude = JumpMinMagnitude + rng.NextDouble() * (JumpMaxMagnitude - JumpMinMagnitude);
            double sign = rng.NextDouble() < 0.5 ? -1.0 : 1.0;
            return new PriceRegime(dayOfYear, true, sign * magnitude);
        }

        private static int ClampPrice(int price, int nativeBasePrice)
        {
            int lower = (int)Math.Round(nativeBasePrice * ClampLowerMultiplier, MidpointRounding.AwayFromZero);
            int upper = (int)Math.Round(nativeBasePrice * ClampUpperMultiplier, MidpointRounding.AwayFromZero);
            return Math.Max(lower, Math.Min(upper, price));
        }

        /// <summary>
        /// Aggregate function (plan.md 6.2): chains all 7 layers over yesterday's price, in the documented
        /// order, and clamps the result. Call once per item per day, passing the regime returned by the
        /// PREVIOUS call that same day (or the previous day's final regime, for the first item) so the
        /// layer-5 jump is decided once and shared across items - each item still scales it by its own
        /// beta - while each item's layer-2 noise is drawn independently from the shared rng.
        /// </summary>
        public static (int NewPrice, PriceRegime NewRegime) ComputeDailyPrice(
            int yesterdayPrice,
            int nativeBasePrice,
            double beta,
            Season season,
            int dayOfMonth,
            DayOfWeek dayOfWeek,
            PriceRegime regime,
            Random rng)
        {
            int dayOfYear = ComputeDayOfYear(season, dayOfMonth);

            PriceRegime effectiveRegime = regime.LastAdvancedDayOfYear == dayOfYear
                ? regime
                : DrawSharedJump(dayOfYear, rng);

            double price = ComputeMeanReversion(yesterdayPrice, nativeBasePrice);

            price *= 1 + SampleIndependentNoise(rng);
            price *= 1 + ComputeWeeklyOffset(dayOfWeek);
            price *= 1 + ComputeFestivalPulse(dayOfYear);

            if (effectiveRegime.JumpTriggeredToday)
            {
                price *= 1 + effectiveRegime.JumpSignedMagnitudeToday * beta;
            }

            price *= ComputeSeasonMultiplier(season);
            price *= 1 + ComputeTrendDirection(dayOfYear) * TrendDailyRate;

            int roundedPrice = (int)Math.Round(price, MidpointRounding.AwayFromZero);
            int clampedPrice = ClampPrice(roundedPrice, nativeBasePrice);

            return (clampedPrice, effectiveRegime);
        }

        /// <summary>Converts SDate's lowercase SeasonKey ("spring"/"summer"/"fall"/"winter") into <see cref="Season"/>.</summary>
        public static Season ParseSeason(string seasonKey)
        {
            switch (seasonKey)
            {
                case "spring": return Season.Spring;
                case "summer": return Season.Summer;
                case "fall": return Season.Fall;
                case "winter": return Season.Winter;
                default: throw new ArgumentOutOfRangeException(nameof(seasonKey), seasonKey, "Unrecognized season key.");
            }
        }
    }
}
