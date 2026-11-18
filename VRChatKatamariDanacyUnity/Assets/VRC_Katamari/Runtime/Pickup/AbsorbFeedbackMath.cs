namespace VRCKatamari.Pickup
{
    /// <summary>Local feedback tuning, independent of the pickup ledger and Unity state.</summary>
    public static class AbsorbFeedbackMath
    {
        public const float AudioWindowSeconds = 0.060f;
        public const float HapticIntervalSeconds = 0.080f;
        public const int VoiceCount = 4;

        public static bool IsDue(float now, float deadline) { return now >= deadline; }

        // Reserve mix headroom even at full volume; three more pickups reach the cap.
        public static float AudioGain(int count)
        {
            if (count <= 0) return 0f;
            if (count >= 4) return 0.24f;
            return 0.18f + 0.02f * (count - 1);
        }

        public static bool PreferAudioClass(int count, int currentClass, int currentBand,
            int candidateClass, int candidateBand)
        {
            return count == 0 || (candidateClass > 0 && (currentClass == 0 || candidateBand > currentBand));
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
