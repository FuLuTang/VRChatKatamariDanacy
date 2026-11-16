namespace VRCKatamari.Pickup
{
    /// <summary>
    /// The D-12 phase seed of one prop: a deterministic 32-bit hash of the level id and the
    /// prop's item id, with no randomness, no time and no dependence on load order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source of truth: Docs/Design/Levels/LEVEL_FORMAT_V2.md section 6.7.5 ("省略时导入器派生
    /// phase = frac(hash32(level.id + "/" + itemId) / 2^32)；确定性，同一份数据每次一样。
    /// 禁止 Math.random()"). The hash is the one definition of that seed: the Unity importer
    /// hands the result to <see cref="MotionMath"/>'s Reset* methods, and the editor's motion
    /// preview compiles this same file (Tools/motion_sim) instead of re-deriving it, so the
    /// browser, the importer and the game can never disagree about a prop's phase.
    /// </para>
    /// <para>
    /// The hash is FNV-1a over the UTF-16 code units of <c>levelId + "/" + itemId</c>. Hashing
    /// code units rather than an encoded byte array keeps this file free of System.Text and of
    /// any allocation, which is what lets the same code run in the Udon runtime, and it is
    /// still a total, platform-independent function: for the ASCII level and item ids the
    /// format allows, one code unit is one byte, so it equals the UTF-8 byte hash; for any
    /// other string it is a fixed function of the UTF-16 text, never of the machine.
    /// </para>
    /// <para>
    /// A null argument is treated as the empty string so a missing id can never throw during an
    /// import; a called-out null is a data bug the importer reports, not a reason to spend a
    /// frame on an exception.
    /// </para>
    /// </remarks>
    public static class PhaseSeed
    {
        // The FNV-1a 32-bit parameters (Landay/McDowell). 32 bits is what the format specifies
        // and what MotionMath.Draw01 consumes, so no wider accumulator is used.
        private const uint FnvOffsetBasis = 2166136261u;
        private const uint FnvPrime = 16777619u;

        /// <summary>
        /// The unsigned 32-bit hash32(levelId + "/" + itemId) of section 6.7.5.
        /// </summary>
        public static uint Hash32(string levelId, string itemId)
        {
            uint hash = FnvOffsetBasis;
            hash = HashString(hash, levelId);
            hash = HashCharacter(hash, '/');
            hash = HashString(hash, itemId);
            return hash;
        }

        /// <summary>
        /// The seed <see cref="MotionMath"/> takes: the same 32 bits reinterpreted as an int
        /// (section 6.7.5's "reduced to an int"). Every bit pattern is legal, including the
        /// negative halves of the range.
        /// </summary>
        public static int From(string levelId, string itemId)
        {
            return unchecked((int)Hash32(levelId, itemId));
        }

        private static uint HashString(uint hash, string value)
        {
            if (string.IsNullOrEmpty(value)) return hash;
            for (int index = 0; index < value.Length; index++)
            {
                hash = HashCharacter(hash, value[index]);
            }
            return hash;
        }

        // FNV-1a: multiply, then xor the next unit in.
        private static uint HashCharacter(uint hash, char value)
        {
            unchecked
            {
                hash = hash * FnvPrime;
                hash = hash ^ (uint)value;
            }
            return hash;
        }
    }
}
