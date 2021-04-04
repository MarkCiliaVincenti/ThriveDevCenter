namespace ThriveDevCenter.Shared
{
    using System;
    using System.Globalization;

    public static class ValuePrintHelpers
    {
        public const float KIBIBYTE = 1024;
        public const float MEBIBYTE = KIBIBYTE * KIBIBYTE;

        public static string BytesToMiB(this float number, int decimals = 2, bool suffix = true)
        {
            var result = Math.Round((number / MEBIBYTE), decimals).ToString(CultureInfo.CurrentCulture);

            if (!suffix)
                return result;

            return result + " MiB";
        }

        public static string BytesToMiB(this long number, int decimals = 2, bool suffix = true)
        {
            return ((float)number).BytesToMiB(decimals, suffix);
        }

        public static string BytesToMiB(this int number, int decimals = 2, bool suffix = true)
        {
            return ((float)number).BytesToMiB(decimals, suffix);
        }
    }
}
