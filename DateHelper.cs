using System;
using System.Collections.Generic;
using StardewModdingAPI.Utilities;

namespace MyFirstMod
{
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

        /// <summary>Formats a date as "春28日" for Chinese UI text.</summary>
        public static string FormatChineseDate(SDate date)
        {
            string seasonName = SeasonNamesZh.TryGetValue(date.SeasonKey, out string zh) ? zh : date.SeasonKey;
            return $"{seasonName}{date.Day}日";
        }
    }
}
