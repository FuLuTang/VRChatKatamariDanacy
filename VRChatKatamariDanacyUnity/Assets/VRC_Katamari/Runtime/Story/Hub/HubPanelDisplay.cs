using UdonSharp;
using UnityEngine;
using TMPro;
using VRCKatamari.Hub;

namespace VRCKatamari.Story.Hub
{
    /// <summary>
    /// Writes every text of the hub's wall panels - level cards, how-to-play, controls,
    /// settings rows, credits - in the player's language, and rewrites them when a setting
    /// or a record changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Display only: it never starts, returns or changes a setting. The words are
    /// <see cref="HubPanelTextMath"/>'s; the values come from <see cref="HubSettingsHost"/>
    /// and <see cref="HubRecordBook"/>, which call <c>OnHubSettingsChanged</c> /
    /// <c>OnHubRecordsChanged</c> on this behaviour. The per-level goal arrays are written by
    /// the hub assembler from the same plan that fills the round controller's table, in
    /// launcher order. Every text reference is optional; a missing one is skipped. The texts are
    /// TextMeshPro labels on the boards' world-space canvases (HUB_ROOM.md); writing a label
    /// only when its string changed keeps TMP mesh rebuilds to real changes.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HubPanelDisplay : UdonSharpBehaviour
    {
        [SerializeField] private HubSettingsHost settings;
        [SerializeField] private HubRecordBook records;

        [Header("Level cards (launcher order)")]
        [SerializeField] private string[] levelNames;
        [SerializeField] private int[] levelGoalTypes;
        [SerializeField] private float[] levelTargetDiameters;
        [SerializeField] private int[] levelTargetCounts;
        [SerializeField] private float[] levelTimeLimits;
        [SerializeField] private TextMeshProUGUI[] levelNameTexts;
        [SerializeField] private TextMeshProUGUI[] levelGoalTexts;
        [SerializeField] private TextMeshProUGUI[] levelRecordTexts;
        [SerializeField] private TextMeshProUGUI[] levelStartTexts;

        [Header("Panel texts")]
        [SerializeField] private TextMeshProUGUI levelsTitle;
        [SerializeField] private TextMeshProUGUI helpTitle;
        [SerializeField] private TextMeshProUGUI howToText;
        [SerializeField] private TextMeshProUGUI controlsTitle;
        [SerializeField] private TextMeshProUGUI controlsVrText;
        [SerializeField] private TextMeshProUGUI controlsDesktopText;
        [SerializeField] private TextMeshProUGUI settingsTitle;
        [SerializeField] private TextMeshProUGUI settingsHintText;
        [SerializeField] private TextMeshProUGUI creditsText;

        [Tooltip("Desktop dash key label (DesktopMoveInput reads Shift); empty prints a neutral 'dash key'.")]
        [SerializeField] private string desktopDashKey = "Shift";

        [Header("Settings rows (HubSettingsMath id order)")]
        [SerializeField] private TextMeshProUGUI[] settingLabelTexts;
        [SerializeField] private TextMeshProUGUI[] settingValueTexts;

        private void Start()
        {
            Redraw();
        }

        public void OnHubSettingsChanged()
        {
            Redraw();
        }

        public void OnHubRecordsChanged()
        {
            Redraw();
        }

        public void Redraw()
        {
            int lang = settings != null ? settings.GetLanguage() : HubSettingsMath.LangZh;
            int mode = settings != null ? settings.GetControlMode() : 0;

            Set(levelsTitle, HubPanelTextMath.Text(HubPanelTextMath.TitleLevels, lang));
            Set(helpTitle, HubPanelTextMath.Text(HubPanelTextMath.TitleHelp, lang));
            Set(howToText, HubPanelTextMath.Text(HubPanelTextMath.HowToBody, lang));
            Set(controlsTitle, HubPanelTextMath.Text(HubPanelTextMath.ControlsHeader, lang));
            Set(controlsVrText, HubPanelTextMath.ControlsBody(lang, true, mode, desktopDashKey));
            Set(controlsDesktopText, HubPanelTextMath.ControlsBody(lang, false, mode, desktopDashKey));
            Set(settingsTitle, HubPanelTextMath.Text(HubPanelTextMath.TitleSettings, lang));
            Set(settingsHintText, HubPanelTextMath.Text(HubPanelTextMath.SettingsHint, lang));
            Set(creditsText, HubPanelTextMath.Text(HubPanelTextMath.Credits, lang) + " · "
                + HubPanelTextMath.Text(HubPanelTextMath.Version, lang));

            int levels = levelNames == null ? 0 : levelNames.Length;
            for (int i = 0; i < levels; i++)
            {
                int goal = PickInt(levelGoalTypes, i);
                SetAt(levelNameTexts, i, levelNames[i]);
                SetAt(levelGoalTexts, i, HubPanelTextMath.GoalLine(goal, PickFloat(levelTargetDiameters, i),
                    PickInt(levelTargetCounts, i), PickFloat(levelTimeLimits, i), lang));
                SetAt(levelStartTexts, i, HubPanelTextMath.Text(HubPanelTextMath.StartTag, lang));
                bool recorded = HubRecordMath.IsRecorded(goal, false);
                bool has = records != null && records.HasRecord(i);
                SetAt(levelRecordTexts, i, HubPanelTextMath.RecordLines(recorded, has,
                    has ? records.GetBestDiameterM(i) : 0f, has ? records.GetBestCount(i) : 0,
                    has ? records.GetLastDiameterM(i) : 0f, has ? records.GetLastCount(i) : 0,
                    has ? records.GetPlays(i) : 0, lang));
            }

            for (int id = 0; id < HubSettingsMath.Count; id++)
            {
                SetAt(settingLabelTexts, id, HubPanelTextMath.SettingLabel(id, lang));
                int step = settings != null ? settings.GetSetting(id) : HubSettingsMath.Default(id);
                SetAt(settingValueTexts, id, HubPanelTextMath.SettingValue(id, step, lang));
            }
        }

        private static void Set(TextMeshProUGUI text, string value)
        {
            if (text == null) return;
            if (text.text != value) text.text = value;
        }

        private static void SetAt(TextMeshProUGUI[] texts, int index, string value)
        {
            if (texts == null || index < 0 || index >= texts.Length) return;
            Set(texts[index], value);
        }

        private static int PickInt(int[] values, int index)
        {
            if (values == null || index < 0 || index >= values.Length) return 0;
            return values[index];
        }

        private static float PickFloat(float[] values, int index)
        {
            if (values == null || index < 0 || index >= values.Length) return 0f;
            return values[index];
        }
    }
}
