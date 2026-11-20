namespace VRCKatamari.BallPresentation
{
    /// <summary>
    /// Which original core skin the ball wears: the skin numbers, the saved choice and the
    /// arrow stepping over the skins this build actually has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A skin is named by the original core's number (core_01 .. core_29; the original ships
    /// no 26 or 27), never by its position in a list. The stored choice therefore keeps its
    /// meaning when the set of assembled skins changes. <c>available</c> is the ascending list
    /// of numbers the core skin assembler installed (<see cref="BallView"/>'s skin table);
    /// null or empty means "not known here" and the full original range is used.
    /// </para>
    /// <para>Pure functions only, so the rules are NUnit-tested without a scene.</para>
    /// </remarks>
    public static class BallSkinMath
    {
        public const int FirstNumber = 1;
        public const int LastNumber = 29;

        /// <summary>core_01, the skin the ball wore before skins became switchable.</summary>
        public const int DefaultNumber = 1;

        /// <summary>True for the numbers the original game has: 1..29 without 26 and 27.</summary>
        public static bool IsOriginalNumber(int number)
        {
            return number >= FirstNumber && number <= LastNumber && number != 26 && number != 27;
        }

        /// <summary>Position of <paramref name="number"/> in <paramref name="available"/>, or -1.</summary>
        public static int IndexOf(int[] available, int number)
        {
            if (available == null) return -1;
            for (int i = 0; i < available.Length; i++)
            {
                if (available[i] == number) return i;
            }
            return -1;
        }

        /// <summary>
        /// A usable skin table: at least one entry, every entry an original number, strictly
        /// ascending (so no duplicates).
        /// </summary>
        public static bool IsValidList(int[] available)
        {
            if (available == null || available.Length == 0) return false;
            for (int i = 0; i < available.Length; i++)
            {
                if (!IsOriginalNumber(available[i])) return false;
                if (i > 0 && available[i] <= available[i - 1]) return false;
            }
            return true;
        }

        /// <summary>
        /// The skin to wear for a stored (or default) choice: the choice itself when available,
        /// otherwise the default skin, otherwise the first available one.
        /// </summary>
        public static int Normalize(int[] available, int stored)
        {
            if (available == null || available.Length == 0)
                return IsOriginalNumber(stored) ? stored : DefaultNumber;
            if (IndexOf(available, stored) >= 0) return stored;
            if (IndexOf(available, DefaultNumber) >= 0) return DefaultNumber;
            return available[0];
        }

        /// <summary>
        /// The skin after one arrow press: the next (+) or previous (-) available number,
        /// wrapping at both ends. A current choice that is not available first normalizes.
        /// </summary>
        public static int Stepped(int[] available, int current, int delta)
        {
            int step = delta > 0 ? 1 : (delta < 0 ? -1 : 0);
            if (available == null || available.Length == 0)
            {
                int number = IsOriginalNumber(current) ? current : DefaultNumber;
                if (step == 0) return number;
                for (int i = 0; i <= LastNumber; i++)
                {
                    number += step;
                    if (number > LastNumber) number = FirstNumber;
                    if (number < FirstNumber) number = LastNumber;
                    if (IsOriginalNumber(number)) return number;
                }
                return DefaultNumber;
            }
            int index = IndexOf(available, current);
            if (index < 0) return Normalize(available, current);
            if (step == 0) return current;
            int count = available.Length;
            int next = (index + step) % count;
            if (next < 0) next += count;
            return available[next];
        }

        /// <summary>The panel text of a skin number: "No.01".</summary>
        public static string Label(int number)
        {
            return "No." + (number >= 0 && number < 10 ? "0" : "") + number;
        }
    }
}
