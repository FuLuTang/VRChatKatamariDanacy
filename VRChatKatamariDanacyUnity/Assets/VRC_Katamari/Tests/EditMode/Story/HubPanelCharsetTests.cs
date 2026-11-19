using System;
using System.IO;
using NUnit.Framework;
using VRCKatamari.Hub;
using VRCKatamari.Story;
using VRCKatamari.Story.Hub;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Every character the hub panels can print is in <c>Tools/hubart/hub_panel_charset.txt</c>,
    /// the set the panels' TextMesh Pro font asset is generated from (HUB_PANEL_PREFAB.md). A
    /// word added to <see cref="HubPanelTextMath"/> without rerunning
    /// <c>Tools/hubart/build_hub_font.py</c> fails here instead of drawing a square on the wall.
    /// </summary>
    public class HubPanelCharsetTests
    {
        private const string CharsetPath = "Tools/hubart/hub_panel_charset.txt";

        private static readonly int[] Languages =
            { HubSettingsMath.LangZh, HubSettingsMath.LangEn, HubSettingsMath.LangJa };

        [Test]
        public void EveryPanelStringIsCoveredByTheCharset()
        {
            string path = FindRepoFile(CharsetPath);
            Assert.IsNotNull(path, CharsetPath + " not found from the test directory");
            string charset = File.ReadAllText(path).TrimEnd('\n', '\r');
            string missing = "";
            foreach (int lang in Languages)
            {
                for (int key = 0; key < HubPanelTextMath.KeyCount; key++)
                    missing += Missing(charset, HubPanelTextMath.Text(key, lang), missing);
                for (int id = 0; id < HubSettingsMath.Count; id++)
                {
                    missing += Missing(charset, HubPanelTextMath.SettingLabel(id, lang), missing);
                    for (int step = HubSettingsMath.Min(id); step <= HubSettingsMath.Max(id); step++)
                        missing += Missing(charset, HubPanelTextMath.SettingValue(id, step, lang), missing);
                }
                for (int mode = 0; mode < 2; mode++)
                {
                    missing += Missing(charset, HubPanelTextMath.ControlsBody(lang, true, mode, ""), missing);
                    missing += Missing(charset, HubPanelTextMath.ControlsBody(lang, false, mode, "Shift"), missing);
                }
                missing += Missing(charset, HubPanelTextMath.RecordLines(true, true, 1.5f, 135, 0.61f, 90, 3, lang), missing);
                missing += Missing(charset, HubPanelTextMath.RecordLines(true, false, 0f, 0, 0f, 0, 0, lang), missing);
                missing += Missing(charset, HubPanelTextMath.GoalLine((int)StoryGoalType.FreeRoam, 0f, 0, 0f, lang), missing);
                missing += Missing(charset, HubPanelTextMath.GoalLine((int)StoryGoalType.CollectCategoryCount, 0f, 12, 120f, lang), missing);
                missing += Missing(charset, HubPanelTextMath.GoalLine(0, 0.8f, 0, 420f, lang), missing);
            }
            missing += Missing(charset, "◀▶", missing);
            // The result card's two buttons use the same font asset (HUB_PANEL_PREFAB.md, 结算卡).
            missing += Missing(charset, StoryRetryButton.Label, missing);
            missing += Missing(charset, HubReturnButton.Label, missing);
            Assert.AreEqual("", missing, "characters missing from " + CharsetPath
                + "; run python3 Tools/hubart/build_hub_font.py and regenerate the font asset");
        }

        private static string Missing(string charset, string text, string already)
        {
            string result = "";
            if (text == null) return result;
            foreach (char ch in text)
            {
                if (ch < 0x20) continue;
                if (charset.IndexOf(ch) < 0 && already.IndexOf(ch) < 0 && result.IndexOf(ch) < 0)
                    result += ch;
            }
            return result;
        }

        private static string FindRepoFile(string relative)
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(directory); i++)
            {
                string candidate = Path.Combine(directory, relative);
                if (File.Exists(candidate)) return candidate;
                directory = Path.GetDirectoryName(directory);
            }
            string current = Directory.GetCurrentDirectory();
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(current); i++)
            {
                string candidate = Path.Combine(current, relative);
                if (File.Exists(candidate)) return candidate;
                current = Path.GetDirectoryName(current);
            }
            return null;
        }
    }
}
