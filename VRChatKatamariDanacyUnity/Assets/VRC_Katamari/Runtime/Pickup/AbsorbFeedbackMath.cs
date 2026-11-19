namespace VRCKatamari.Pickup
{
    /// <summary>Local feedback tuning, independent of the pickup ledger and Unity state.</summary>
    public static class AbsorbFeedbackMath
    {
        public const int ScreamCooldownFrames = 90;
        public const int OriginalFramesPerSecond = 30;
        public const float ScreamCooldownSeconds = (float)ScreamCooldownFrames / OriginalFramesPerSecond;
        public const int ScreamVoiceCount = 2;
        public const int VoiceCount = 1 + ScreamVoiceCount;
        public const float RollInGain = 0.18f;
        public const float ScreamGain = 0.18f;
        public const float HapticIntervalSeconds = 0.080f;
        public const int RollInLayer = 1;
        public const int ScreamLayer = 2;

        public static bool IsDue(float now, float deadline) { return now >= deadline; }

        public static int PlaybackLayers(int rollInClip, int screamClip, bool screamReady, int screamSlot)
        {
            int layers = rollInClip >= 0 ? RollInLayer : 0;
            if (screamClip >= 0 && screamReady && screamSlot >= 0 && screamSlot < ScreamVoiceCount)
                layers |= ScreamLayer;
            return layers;
        }

        public static int FreeScreamSlot(bool firstBusy, bool secondBusy)
        {
            if (!firstBusy) return 0;
            return secondBusy ? -1 : 1;
        }

        public static float NextScreamTime(float now) { return now + ScreamCooldownSeconds; }

        // 卷入片段（编号 0）在前，正叫声编号升序排列；同一编号的变体紧邻。
        public static int FirstScreamClip(int[] clipClasses, int screamId)
        {
            if (clipClasses == null || screamId <= 0) return -1;
            int low = 0;
            int high = clipClasses.Length;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (clipClasses[mid] < screamId) low = mid + 1;
                else high = mid;
            }
            return low < clipClasses.Length && clipClasses[low] == screamId ? low : -1;
        }

        // Relative linear size: volume-equivalent edge divided by the ball's diameter.
        public static int SizeBand(float relativeSize)
        {
            if (relativeSize >= 0.30f) return 2;
            if (relativeSize >= 0.15f) return 1;
            return 0;
        }

        public static float HapticDuration(int band)
        {
            return band >= 2 ? 0.035f : band == 1 ? 0.028f : 0.020f;
        }

        public static float HapticAmplitude(int band, float setting)
        {
            if (!(setting > 0f)) return 0f;
            if (setting > 1f) setting = 1f;
            return (band >= 2 ? 0.35f : band == 1 ? 0.27f : 0.20f) * setting;
        }

        // A private sequence avoids consuming Unity's shared gameplay RNG.
        public static int NextRandomState(int state) { return (state * 25173 + 13849) & 65535; }

        // Pick over eligible ranks only: skip the previous clip if there is an alternative.
        public static int ClipRank(int state, int count, int previousRank)
        {
            if (count <= 0) return -1;
            if (count == 1) return 0;
            if (previousRank < 0 || previousRank >= count) return (state * count) >> 16;
            int rank = (state * (count - 1)) >> 16;
            return rank >= previousRank ? rank + 1 : rank;
        }
    }
}
