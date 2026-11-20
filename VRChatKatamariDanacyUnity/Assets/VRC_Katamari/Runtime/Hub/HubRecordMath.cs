namespace VRCKatamari.Hub
{
    /// <summary>
    /// Which finished round counts as a level's best, and the stored keys of a level's record.
    /// </summary>
    /// <remarks>
    /// A record is two confirmed facts of a settled round: the ball's collision diameter and
    /// the number of confirmed attachments (OPEN_DECISIONS: Cancer shows only diameter and
    /// count, no stars; scoring stays a detachable module, so nothing here reads stars).
    /// The record mode, not the clear type, chooses which fact sorts first.
    /// Free roam and aborted rounds are never recorded.
    /// </remarks>
    public static class HubRecordMath
    {
        public const int None = 0;
        public const int DiameterAndTotal = 1;
        public const int CategoryAndDiameter = 2;

        /// <summary>True when a settled round of this kind leaves a record.</summary>
        public static bool IsRecorded(int recordMode, bool aborted)
        {
            if (aborted) return false;
            return recordMode == DiameterAndTotal || recordMode == CategoryAndDiameter;
        }

        /// <summary>True when the new round beats the stored best (or there is none).</summary>
        public static bool IsBetter(int recordMode, float diameterM, int count,
            bool hasBest, float bestDiameterM, int bestCount)
        {
            if (!(diameterM >= 0f) || count < 0) return false;
            if (!hasBest) return true;
            if (recordMode == CategoryAndDiameter)
            {
                if (count != bestCount) return count > bestCount;
                return diameterM > bestDiameterM;
            }
            if (diameterM != bestDiameterM) return diameterM > bestDiameterM;
            return count > bestCount;
        }

        public const string FieldBestDiameter = "bestD";
        public const string FieldBestCount = "bestN";
        public const string FieldLastDiameter = "lastD";
        public const string FieldLastCount = "lastN";
        public const string FieldPlays = "plays";

        /// <summary>PlayerData key of one record field of a level, e.g. <c>vrck.rec._ref_cancer.bestD</c>.</summary>
        public static string StorageKey(string levelId, string field)
        {
            if (levelId == null || levelId.Length == 0 || field == null || field.Length == 0)
            {
                return "";
            }
            return "vrck.rec." + levelId + "." + field;
        }
    }
}
