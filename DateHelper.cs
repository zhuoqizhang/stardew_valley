using System;
using System.Collections.Generic;
using StardewModdingAPI.Utilities;

namespace MyFirstMod
{
    /// <summary>
    /// Which due-date rule a signing request is asking for (design doc 5.1's normal GetNextNextFriday(), or
    /// FuturesMenu's "signed day + 1" test rows). Plan.md section 7: RequestSignContractMessage carries this
    /// "kind" selector instead of a computed due-date value, so the host (the only one who ever resolves it
    /// into an actual SDate via DateHelper.ResolveDueDate) never has to trust a date computed elsewhere.
    /// </summary>
    public enum ContractDueDateKind
    {
        NextNextFriday,
        SignedDatePlusOneDay
    }

    /// <summary>Date math helpers for futures contract due-date calculations.</summary>
    public static class DateHelper
    {
        private static readonly Dictionary<string, string> SeasonNamesZh = new Dictionary<string, string>
        {
            ["spring"] = "春",
            ["summer"] = "夏",
            ["fall"] = "秋",
            ["winter"] = "冬",
        };

        /// <summary>
        /// "下下周五"：从 <paramref name="from"/> 起，找到严格晚于它的下一个周五（"下周五"），
        /// 再加 7 天得到"下下周五"。验证过 spring/summer/fall/winter 全部 28 天起点，
        /// 结果始终落在 from 之后第 8～14 天、且必为周五（含跨年边界）。
        /// </summary>
        public static SDate GetNextNextFriday(SDate from)
        {
            SDate nextFriday = from.AddDays(1);
            while (nextFriday.DayOfWeek != DayOfWeek.Friday)
            {
                nextFriday = nextFriday.AddDays(1);
            }

            return nextFriday.AddDays(7);
        }

        /// <summary>Convenience overload that uses today's in-game date as the starting point.</summary>
        public static SDate GetNextNextFriday()
        {
            return GetNextNextFriday(SDate.Now());
        }

        /// <summary>
        /// Resolves a ContractDueDateKind against the CALLER's own signedDate - plan.md section 7's
        /// ContractManager.SignContractAsHost always passes the host's own SDate.Now(), never a
        /// client-supplied date, so this only ever computes a due date from trusted local game state.
        /// </summary>
        public static SDate ResolveDueDate(ContractDueDateKind dueDateKind, SDate signedDate)
        {
            switch (dueDateKind)
            {
                case ContractDueDateKind.NextNextFriday:
                    return GetNextNextFriday(signedDate);
                case ContractDueDateKind.SignedDatePlusOneDay:
                    return signedDate.AddDays(1);
                default:
                    throw new ArgumentOutOfRangeException(nameof(dueDateKind), dueDateKind, "Unrecognized due-date kind.");
            }
        }

        /// <summary>Formats a date as "春28日" for Chinese UI text.</summary>
        public static string FormatChineseDate(SDate date)
        {
            string seasonName = SeasonNamesZh.TryGetValue(date.SeasonKey, out string zh) ? zh : date.SeasonKey;
            return $"{seasonName}{date.Day}日";
        }
    }
}
