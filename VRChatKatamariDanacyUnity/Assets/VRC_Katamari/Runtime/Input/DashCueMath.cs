namespace VRCKatamari.Input
{
    /// <summary>冲刺提示的纯逻辑：从马达状态的变化得出该放哪一个提示。只影响表现，不改玩法状态。</summary>
    public static class DashCueMath
    {
        public const int PhaseIdle = 0;
        public const int PhaseCharging = 1;
        public const int PhaseSpinning = 2;
        public const int PhaseDashing = 3;

        public const int CueNone = 0;
        public const int CueChargeStart = 1;
        public const int CueSpin = 2;
        public const int CueLaunch = 3;
        public const int CueStop = 4; // 蓄力被松开（空转前）或取消：停掉蓄力音。

        // 手柄震动：{时长 s, 振幅}，振幅再乘设置里的触感强度。
        public const float StartDuration = 0.04f;
        public const float StartAmplitude = 0.2f;
        public const float SpinDuration = 0.08f;
        public const float SpinAmplitude = 0.5f;
        public const float LaunchDuration = 0.15f;
        public const float LaunchAmplitude = 0.9f;
        public const float RefuseDuration = 0.03f;
        public const float RefuseAmplitude = 0.35f;
        public const float RefuseGapSeconds = 0.12f;

        // 三段音的音高（同一个 clip 时用来区分）。
        public const float StartPitch = 0.8f;
        public const float SpinPitch = 1.0f;
        public const float LaunchPitch = 1.25f;

        public static int PhaseOf(bool charging, bool spinning, bool dashing)
        {
            if (charging) return spinning ? PhaseSpinning : PhaseCharging;
            return dashing ? PhaseDashing : PhaseIdle;
        }

        /// <summary>上一帧阶段 → 本帧阶段对应的提示；没有变化或不该提示时 CueNone。</summary>
        public static int CueFor(int previousPhase, int phase)
        {
            if (phase == previousPhase) return CueNone;
            if (phase == PhaseCharging && previousPhase == PhaseIdle) return CueChargeStart;
            if (phase == PhaseSpinning && previousPhase == PhaseCharging) return CueSpin;
            if (phase == PhaseDashing && (previousPhase == PhaseCharging || previousPhase == PhaseSpinning))
                return CueLaunch;
            if (phase == PhaseIdle && (previousPhase == PhaseCharging || previousPhase == PhaseSpinning))
                return CueStop;
            return CueNone;
        }

        public static float HapticAmplitude(int cue, float setting)
        {
            if (!(setting > 0f)) return 0f;
            if (setting > 1f) setting = 1f;
            if (cue == CueChargeStart) return StartAmplitude * setting;
            if (cue == CueSpin) return SpinAmplitude * setting;
            if (cue == CueLaunch) return LaunchAmplitude * setting;
            return 0f;
        }

        public static float HapticDuration(int cue)
        {
            if (cue == CueChargeStart) return StartDuration;
            if (cue == CueSpin) return SpinDuration;
            if (cue == CueLaunch) return LaunchDuration;
            return 0f;
        }

        public static float Pitch(int cue)
        {
            if (cue == CueChargeStart) return StartPitch;
            if (cue == CueSpin) return SpinPitch;
            if (cue == CueLaunch) return LaunchPitch;
            return 1f;
        }
    }
}
