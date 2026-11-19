using VRCKatamari.Hub;
using VRCKatamari.Story;

namespace VRCKatamari.Story.Hub
{
    /// <summary>
    /// Every word the hub room shows, in Simplified Chinese, English and Japanese, plus the
    /// small formatters that turn records, goals and setting values into panel lines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One table, looked up by a key constant and a language id
    /// (<see cref="HubSettingsMath.LangZh"/> / <c>LangEn</c> / <c>LangJa</c>). An unknown
    /// language falls back to Chinese, an unknown key to an empty string. Pure functions, so
    /// a missing translation is caught by NUnit (every key must have three non-empty
    /// strings), not in a headset.
    /// </para>
    /// <para>
    /// Controls wording follows Docs/01_GAME_DESIGN.md section 4: the item key is written as
    /// "Jump" (never a controller model's button name). The desktop dash key is not decided
    /// yet; <see cref="ControlsBody"/> takes its label as an argument and prints a neutral
    /// placeholder while it is empty.
    /// </para>
    /// </remarks>
    public static class HubPanelTextMath
    {
        public const int TitleLevels = 0;
        public const int TitleHelp = 1;
        public const int TitleSettings = 2;
        public const int HowToBody = 3;
        public const int ControlsHeader = 4;
        public const int StartTag = 5;
        public const int NoRecord = 6;
        public const int Best = 7;
        public const int Last = 8;
        public const int Plays = 9;
        public const int Version = 10;
        public const int On = 11;
        public const int Off = 12;
        public const int Tank = 13;
        public const int Simple = 14;
        public const int HapticsOff = 15;
        public const int HapticsWeak = 16;
        public const int HapticsNormal = 17;
        public const int PromptHub = 18;
        public const int PromptLoading = 19;
        public const int PromptPlaying = 20;
        public const int PromptResult = 21;
        public const int Credits = 22;
        public const int FreeRoamGoal = 23;
        public const int TimeLimit = 24;
        public const int GoalSize = 25;
        public const int GoalCount = 26;
        public const int Pieces = 27;
        public const int SettingsHint = 28;
        public const int PreviewPending = 29;
        public const int RecordsNoStars = 30;

        /// <summary>Number of text keys; keys are 0..KeyCount-1.</summary>
        public const int KeyCount = 31;

        private static string Pick(int lang, string zh, string en, string ja)
        {
            if (lang == HubSettingsMath.LangEn) return en;
            if (lang == HubSettingsMath.LangJa) return ja;
            return zh;
        }

        /// <summary>The fixed text of <paramref name="key"/> in <paramref name="lang"/>.</summary>
        public static string Text(int key, int lang)
        {
            if (key == TitleLevels) return Pick(lang, "选择关卡", "Choose a Level", "ステージをえらぶ");
            if (key == TitleHelp) return Pick(lang, "怎么玩", "How to Play", "あそびかた");
            if (key == TitleSettings) return Pick(lang, "设置", "Settings", "せってい");
            if (key == HowToBody)
            {
                return Pick(lang,
                    "· 滚动块魂，碰到比球小的东西就会粘上来\n"
                        + "· 粘得越多球越大，能吸的东西也越大\n"
                        + "· 限时内把球滚到目标大小；时间到自动结算\n"
                        + "· 结果显示 8 秒后自动回大厅，也可点「回大厅」「再来一次」\n"
                        + "· 菜单里的 Respawn = 放弃本局并回大厅",
                    "· Roll the katamari: anything smaller than it sticks\n"
                        + "· The more it picks up, the bigger it grows\n"
                        + "· Reach the target size before time runs out\n"
                        + "· Results stay 8 s, then you return here (or press Return / Retry)\n"
                        + "· Menu Respawn = give up the round and return here",
                    "· 塊魂をころがして、小さいものをくっつけよう\n"
                        + "· くっつけるほど大きくなり、大きなものも巻きこめる\n"
                        + "· 時間内に目標の大きさへ。時間切れで結果発表\n"
                        + "· 結果は 8 秒表示後に自動でロビーへ（ボタンでも可）\n"
                        + "· メニューの Respawn ＝ ギブアップしてロビーへ");
            }
            if (key == ControlsHeader) return Pick(lang, "操作", "Controls", "そうさ");
            if (key == StartTag) return Pick(lang, "开始 →", "Start →", "スタート →");
            if (key == NoRecord) return Pick(lang, "还没有记录", "No record yet", "記録なし");
            if (key == Best) return Pick(lang, "最好", "Best", "ベスト");
            if (key == Last) return Pick(lang, "上次", "Last", "前回");
            if (key == Plays) return Pick(lang, "已玩", "Played", "プレイ");
            if (key == Version) return Pick(lang, "开发版", "Development build", "開発版");
            if (key == On) return Pick(lang, "开", "On", "オン");
            if (key == Off) return Pick(lang, "关", "Off", "オフ");
            if (key == Tank) return Pick(lang, "原作双摇杆", "Classic (2 sticks)", "クラシック");
            if (key == Simple) return Pick(lang, "简单", "Simple", "かんたん");
            if (key == HapticsOff) return Pick(lang, "关", "Off", "オフ");
            if (key == HapticsWeak) return Pick(lang, "弱", "Weak", "よわ");
            if (key == HapticsNormal) return Pick(lang, "标准", "Normal", "ふつう");
            if (key == PromptHub)
            {
                return Pick(lang, "点墙上的关卡卡片开始 · 桌面：对准后左键 · VR：指向后扳机",
                    "Pick a level card on the wall · Desktop: left click · VR: point + trigger",
                    "壁のカードでスタート · PC：左クリック · VR：指してトリガー");
            }
            if (key == PromptLoading) return Pick(lang, "正在进入关卡…", "Entering the level…", "ステージへ移動中…");
            if (key == PromptPlaying) return Pick(lang, "关卡进行中", "Round in progress", "プレイ中");
            if (key == PromptResult)
            {
                return Pick(lang, "本局已结算，即将返回大厅；也可选「回大厅」或「再来一次」",
                    "Round over, returning soon; or press Return / Retry",
                    "結果発表。まもなくロビーへ（ボタンでも可）");
            }
            if (key == Credits)
            {
                return Pick(lang, "VRC塊魂 · 粉丝制作的 VRChat 世界，与官方无关",
                    "VRC Katamari · a fan-made VRChat world, not affiliated with the official games",
                    "VRC塊魂 · ファンメイドの VRChat ワールド（公式とは無関係）");
            }
            if (key == FreeRoamGoal) return Pick(lang, "自由练习 · 不计时", "Free practice · no timer", "フリー練習 · 時間制限なし");
            if (key == TimeLimit) return Pick(lang, "限时", "Time", "制限時間");
            if (key == GoalSize) return Pick(lang, "目标", "Goal", "目標");
            if (key == GoalCount) return Pick(lang, "目标件数", "Goal", "目標");
            if (key == Pieces) return Pick(lang, "件", "items", "個");
            if (key == SettingsHint)
            {
                return Pick(lang, "点左右箭头修改，自动保存", "Use the arrows to change · saved automatically",
                    "矢印で変更 · 自動保存");
            }
            if (key == PreviewPending) return Pick(lang, "预览", "Preview", "プレビュー");
            if (key == RecordsNoStars) return Pick(lang, "不评星", "no stars", "星なし");
            return "";
        }

        /// <summary>The label of setting <paramref name="id"/>; the language row is always trilingual.</summary>
        public static string SettingLabel(int id, int lang)
        {
            if (id == HubSettingsMath.ControlMode) return Pick(lang, "VR 操作方式", "VR controls", "VR 操作");
            if (id == HubSettingsMath.SwapHands) return Pick(lang, "左右手互换", "Swap hands", "左右入れ替え");
            if (id == HubSettingsMath.Deadzone) return Pick(lang, "摇杆死区", "Stick deadzone", "スティック遊び");
            if (id == HubSettingsMath.TurnSensitivity) return Pick(lang, "转向灵敏度", "Turn sensitivity", "旋回感度");
            if (id == HubSettingsMath.MusicVolume) return Pick(lang, "音乐", "Music", "BGM");
            if (id == HubSettingsMath.EffectsVolume) return Pick(lang, "音效", "Sound effects", "効果音");
            if (id == HubSettingsMath.VoiceVolume) return Pick(lang, "提示音", "Cues", "お知らせ音");
            if (id == HubSettingsMath.Haptics) return Pick(lang, "振动", "Vibration", "振動");
            if (id == HubSettingsMath.LowEffects) return Pick(lang, "低特效", "Reduced effects", "エフェクト控えめ");
            if (id == HubSettingsMath.ReduceAutoRotate) return Pick(lang, "减弱自动转向", "Less auto-turn", "自動回転ひかえめ");
            if (id == HubSettingsMath.Language) return "语言 / Language / 言語";
            if (id == HubSettingsMath.PushCue) return Pick(lang, "推进方向箭头", "Push direction arrow", "方向ガイド");
            return "";
        }

        /// <summary>The shown value of setting <paramref name="id"/> at <paramref name="step"/>.</summary>
        public static string SettingValue(int id, int step, int lang)
        {
            int v = HubSettingsMath.Clamp(id, step);
            if (id == HubSettingsMath.ControlMode) return v == 1 ? Text(Simple, lang) : Text(Tank, lang);
            if (id == HubSettingsMath.SwapHands || id == HubSettingsMath.LowEffects
                || id == HubSettingsMath.ReduceAutoRotate || id == HubSettingsMath.PushCue)
            {
                return v == 1 ? Text(On, lang) : Text(Off, lang);
            }
            if (id == HubSettingsMath.Deadzone) return HubSettingsMath.DeadzoneValue(v).ToString("0.00");
            if (id == HubSettingsMath.TurnSensitivity)
            {
                return "×" + HubSettingsMath.TurnSensitivityValue(v).ToString("0.0");
            }
            if (id == HubSettingsMath.MusicVolume || id == HubSettingsMath.EffectsVolume
                || id == HubSettingsMath.VoiceVolume)
            {
                return (v * 10) + "%";
            }
            if (id == HubSettingsMath.Haptics)
            {
                if (v == 0) return Text(HapticsOff, lang);
                if (v == 1) return Text(HapticsWeak, lang);
                return Text(HapticsNormal, lang);
            }
            if (id == HubSettingsMath.Language)
            {
                if (v == HubSettingsMath.LangEn) return "English";
                if (v == HubSettingsMath.LangJa) return "日本語";
                return "简体中文";
            }
            return "";
        }

        /// <summary>The controls table: VR column and desktop column, one line per action.</summary>
        public static string ControlsBody(int lang, bool vrColumn, int controlMode,
            string desktopDashKey)
        {
            string dashKey = desktopDashKey;
            if (dashKey == null || dashKey.Length == 0) dashKey = Pick(lang, "冲刺键", "Dash key", "ダッシュキー");
            if (vrColumn)
            {
                string move = controlMode == 1
                    ? Pick(lang, "移动：左摇杆滚动，右摇杆左右转", "Move: left stick rolls, right stick turns",
                        "移動：左スティックで転がし、右スティックで旋回")
                    : Pick(lang, "移动：两根摇杆一起推＝前进，一推一拉＝转弯",
                        "Move: both sticks forward = roll, one forward one back = turn",
                        "移動：両スティック前＝前進、前後逆＝旋回");
                return "VR\n" + move + "\n"
                    + Pick(lang, "冲刺：长按左扳机蓄力，到时自动冲出", "Dash: hold left trigger, launches when charged",
                        "ダッシュ：左トリガー長押しでため、ためきると自動発進") + "\n"
                    + Pick(lang, "道具：Jump 键", "Item: Jump", "アイテム：Jump") + "\n"
                    + Pick(lang, "下车：按住 Jump 1.5 秒", "Get off: hold Jump 1.5 s", "降りる：Jump 1.5 秒長押し") + "\n"
                    + Pick(lang, "选择：指向后扳机", "Select: point + trigger", "選択：指してトリガー");
            }
            return Pick(lang, "桌面", "Desktop", "PC") + "\n"
                + Pick(lang, "移动：W/S 前后，A/D 转向", "Move: W/S roll, A/D turn", "移動：W/S 前後、A/D 旋回") + "\n"
                + Pick(lang, "冲刺：", "Dash: ", "ダッシュ：") + dashKey + "\n"
                + Pick(lang, "道具：空格（Jump）", "Item: Space (Jump)", "アイテム：スペース（Jump）") + "\n"
                + Pick(lang, "下车：按住空格 1.5 秒", "Get off: hold Space 1.5 s", "降りる：スペース 1.5 秒長押し") + "\n"
                + Pick(lang, "选择：对准后鼠标左键", "Select: aim + left click", "選択：左クリック");
        }

        /// <summary>A ball diameter as players read it: centimetres under 1 m, else metres.</summary>
        public static string Diameter(float metres)
        {
            if (!(metres >= 0f)) return "-";
            if (metres < 1f) return ((int)(metres * 100f + 0.5f)) + " cm";
            return metres.ToString("0.00") + " m";
        }

        /// <summary>"7:00" for 420 s.</summary>
        public static string Clock(float seconds)
        {
            if (!(seconds >= 0f)) return "-";
            int total = (int)(seconds + 0.5f);
            int s = total % 60;
            return (total / 60) + ":" + (s < 10 ? "0" : "") + s;
        }

        /// <summary>The goal line of a level card.</summary>
        public static string GoalLine(int goalType, float targetDiameterM, int targetCount,
            float timeLimitSeconds, int lang)
        {
            if (goalType == (int)StoryGoalType.FreeRoam) return Text(FreeRoamGoal, lang);
            string goal = goalType == (int)StoryGoalType.CollectCategoryCount
                ? Text(GoalCount, lang) + " " + targetCount + " " + Text(Pieces, lang)
                : Text(GoalSize, lang) + " " + Diameter(targetDiameterM);
            return Text(TimeLimit, lang) + " " + Clock(timeLimitSeconds) + " · " + goal;
        }

        /// <summary>One record fact pair, "82 cm · 135 件".</summary>
        public static string RecordPair(float diameterM, int count, int lang)
        {
            return Diameter(diameterM) + " · " + count + " " + Text(Pieces, lang);
        }

        /// <summary>The record lines of a level card (best, last, plays), or "no record".</summary>
        public static string RecordLines(bool recorded, bool hasRecord, float bestDiameterM,
            int bestCount, float lastDiameterM, int lastCount, int plays, int lang)
        {
            if (!recorded) return "";
            if (!hasRecord) return Text(NoRecord, lang);
            return Text(Best, lang) + " " + RecordPair(bestDiameterM, bestCount, lang) + "\n"
                + Text(Last, lang) + " " + RecordPair(lastDiameterM, lastCount, lang)
                + "  ·  " + Text(Plays, lang) + " " + plays;
        }
    }
}
