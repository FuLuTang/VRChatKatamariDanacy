using VRCKatamari.Input;

namespace VRCKatamari.Hub
{
    /// <summary>
    /// The player settings the hub panel offers: ids, ranges, defaults, stepping, the stored
    /// key of each, and the conversion of a stored step into the value its consumer uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every setting is stored as a small integer step (deadzone 4 = 0.20, volume 8 = 80 %),
    /// so the panel, the store and every consumer agree on one discrete value and a stored
    /// number can never be a value the panel cannot show. The list follows
    /// Docs/01_GAME_DESIGN.md sections 4, 7 and 8 (input, audio, comfort, three languages);
    /// Docs/Design/StoryMode/HUB_ROOM.md says which consumer reads each one.
    /// </para>
    /// <para>
    /// Pure functions only (no Udon, no engine state), so the rules are NUnit-tested and the
    /// Udon host <see cref="HubSettingsHost"/> stays a thin store.
    /// </para>
    /// </remarks>
    public static class HubSettingsMath
    {
        public const int ControlMode = 0;
        public const int SwapHands = 1;
        public const int Deadzone = 2;
        public const int TurnSensitivity = 3;
        public const int MusicVolume = 4;
        public const int EffectsVolume = 5;
        public const int VoiceVolume = 6;
        public const int Haptics = 7;
        public const int LowEffects = 8;
        public const int ReduceAutoRotate = 9;
        public const int Language = 10;
        public const int PushCue = 11;

        /// <summary>Number of settings; ids are 0..Count-1.</summary>
        public const int Count = 12;

        public const int LangZh = 0;
        public const int LangEn = 1;
        public const int LangJa = 2;

        public const float DeadzoneStepSize = 0.05f;
        public const float TurnSensitivityStepSize = 0.1f;

        public static bool IsValidId(int id)
        {
            return id >= 0 && id < Count;
        }

        public static int Min(int id)
        {
            if (id == Deadzone) return 1;
            if (id == TurnSensitivity) return 5;
            return 0;
        }

        public static int Max(int id)
        {
            if (id == ControlMode || id == SwapHands || id == LowEffects
                || id == ReduceAutoRotate || id == PushCue) return 1;
            if (id == Deadzone) return 10;
            if (id == TurnSensitivity) return 15;
            if (id == MusicVolume || id == EffectsVolume || id == VoiceVolume) return 10;
            if (id == Haptics) return 2;
            if (id == Language) return 2;
            return 0;
        }

        /// <summary>Default step. Language has no fixed default: <see cref="LanguageFromCode"/>.</summary>
        public static int Default(int id)
        {
            if (id == Deadzone) return DeadzoneStep(TankStickMath.DefaultDeadzone);
            if (id == ControlMode) return TankStickMath.ModeTank;
            if (id == TurnSensitivity) return 10; // 1.0
            if (id == MusicVolume || id == EffectsVolume || id == VoiceVolume) return 8;
            if (id == Haptics) return 2;
            if (id == Language) return LangZh;
            if (id == PushCue) return 1;
            return 0;
        }

        /// <summary>
        /// Choices that are a set rather than a scale (on/off, mode, language) wrap around;
        /// scales stop at their ends, so a held press never jumps from loudest to silent.
        /// </summary>
        public static bool Wraps(int id)
        {
            return id == ControlMode || id == SwapHands || id == LowEffects
                || id == ReduceAutoRotate || id == Language || id == PushCue;
        }

        public static int Clamp(int id, int value)
        {
            if (!IsValidId(id)) return 0;
            int min = Min(id);
            int max = Max(id);
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>The value after one press of <paramref name="delta"/> (+1 / -1).</summary>
        public static int Stepped(int id, int value, int delta)
        {
            if (!IsValidId(id)) return 0;
            int current = Clamp(id, value);
            if (!Wraps(id)) return Clamp(id, current + delta);
            int min = Min(id);
            int span = Max(id) - min + 1;
            int offset = (current - min + delta) % span;
            if (offset < 0) offset += span;
            return min + offset;
        }

        /// <summary>PlayerData key of a setting; one key per setting, never renamed.</summary>
        public static string StorageKey(int id)
        {
            if (id == ControlMode) return "vrck.set.controlMode";
            if (id == SwapHands) return "vrck.set.swapHands";
            if (id == Deadzone) return "vrck.set.deadzone";
            if (id == TurnSensitivity) return "vrck.set.turnSensitivity";
            if (id == MusicVolume) return "vrck.set.musicVolume";
            if (id == EffectsVolume) return "vrck.set.effectsVolume";
            if (id == VoiceVolume) return "vrck.set.voiceVolume";
            if (id == Haptics) return "vrck.set.haptics";
            if (id == LowEffects) return "vrck.set.lowEffects";
            if (id == ReduceAutoRotate) return "vrck.set.reduceAutoRotate";
            if (id == Language) return "vrck.set.language";
            if (id == PushCue) return "vrck.set.pushCue";
            return "";
        }

        /// <summary>
        /// True when this build has a consumer that acts on the setting. Not shown to players
        /// (every row is adjustable and saved); it is the in-code list of what is still to be
        /// wired, kept equal to the table in Docs/Design/StoryMode/HUB_ROOM.md.
        /// </summary>
        public static bool HasConsumer(int id)
        {
            return id == ControlMode || id == SwapHands || id == Deadzone
                || id == TurnSensitivity || id == Language || id == EffectsVolume || id == Haptics
                || id == PushCue;
        }

        /// <summary>The nearest deadzone step for a deadzone value (0.2 = step 4).</summary>
        public static int DeadzoneStep(float value)
        {
            if (!(value >= 0f) || value > 1f) return Min(Deadzone);
            return Clamp(Deadzone, (int)(value / DeadzoneStepSize + 0.5f));
        }

        public static float DeadzoneValue(int step)
        {
            return Clamp(Deadzone, step) * DeadzoneStepSize;
        }

        public static float TurnSensitivityValue(int step)
        {
            return Clamp(TurnSensitivity, step) * TurnSensitivityStepSize;
        }

        /// <summary>Volume 0..1 for a volume setting's step.</summary>
        public static float Volume01(int id, int step)
        {
            if (id != MusicVolume && id != EffectsVolume && id != VoiceVolume) return 0f;
            return Clamp(id, step) / 10f;
        }

        /// <summary>Haptics strength 0 (off), 0.5 (weak), 1 (normal).</summary>
        public static float Haptics01(int step)
        {
            return Clamp(Haptics, step) * 0.5f;
        }

        /// <summary>
        /// The panel language for a VRChat client language code (<c>VRCPlayerApi.GetCurrentLanguage</c>):
        /// Japanese and Chinese codes map to their table, anything else to English.
        /// </summary>
        public static int LanguageFromCode(string code)
        {
            if (code == null || code.Length < 2) return LangEn;
            string head = code.Substring(0, 2).ToLower();
            if (head == "ja") return LangJa;
            if (head == "zh") return LangZh;
            return LangEn;
        }
    }
}
