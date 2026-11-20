using NUnit.Framework;
using VRCKatamari.Hub;
using VRCKatamari.Story;
using VRCKatamari.Story.Hub;

namespace VRCKatamari.Tests
{
    public class HubPanelTextMathTests
    {
        private static readonly int[] Languages =
            { HubSettingsMath.LangZh, HubSettingsMath.LangEn, HubSettingsMath.LangJa };

        [Test]
        public void EveryTextKeyHasThreeNonEmptyLanguages()
        {
            for (int key = 0; key < HubPanelTextMath.KeyCount; key++)
            {
                for (int i = 0; i < Languages.Length; i++)
                {
                    Assert.IsFalse(string.IsNullOrEmpty(HubPanelTextMath.Text(key, Languages[i])),
                        "key " + key + " lang " + Languages[i]);
                }
            }
            Assert.AreEqual("", HubPanelTextMath.Text(HubPanelTextMath.KeyCount, HubSettingsMath.LangEn));
        }

        [Test]
        public void EnglishAndJapaneseDifferFromChineseForTranslatedKeys()
        {
            Assert.AreNotEqual(HubPanelTextMath.Text(HubPanelTextMath.HowToBody, HubSettingsMath.LangZh),
                HubPanelTextMath.Text(HubPanelTextMath.HowToBody, HubSettingsMath.LangEn));
            Assert.AreNotEqual(HubPanelTextMath.Text(HubPanelTextMath.HowToBody, HubSettingsMath.LangZh),
                HubPanelTextMath.Text(HubPanelTextMath.HowToBody, HubSettingsMath.LangJa));
            Assert.AreEqual(HubPanelTextMath.Text(HubPanelTextMath.TitleLevels, HubSettingsMath.LangZh),
                HubPanelTextMath.Text(HubPanelTextMath.TitleLevels, 77), "unknown language falls back to Chinese");
        }

        [Test]
        public void EverySettingHasALabelAndAValueInEveryLanguage()
        {
            for (int id = 0; id < HubSettingsMath.Count; id++)
            {
                for (int i = 0; i < Languages.Length; i++)
                {
                    Assert.IsFalse(string.IsNullOrEmpty(HubPanelTextMath.SettingLabel(id, Languages[i])));
                    Assert.IsFalse(string.IsNullOrEmpty(HubPanelTextMath.SettingValue(id,
                        HubSettingsMath.Default(id), Languages[i])));
                }
            }
        }

        [Test]
        public void ValuesReadAsPlayersExpect()
        {
            Assert.AreEqual("80%", HubPanelTextMath.SettingValue(HubSettingsMath.MusicVolume, 8, 0));
            Assert.AreEqual("0.20", HubPanelTextMath.SettingValue(HubSettingsMath.Deadzone, 4, 0));
            Assert.AreEqual("日本語", HubPanelTextMath.SettingValue(HubSettingsMath.Language,
                HubSettingsMath.LangJa, HubSettingsMath.LangEn));
            Assert.AreEqual("Simple", HubPanelTextMath.SettingValue(HubSettingsMath.ControlMode, 1,
                HubSettingsMath.LangEn));
        }

        [TestCase(0.222f, "22 cm")]
        [TestCase(0.995f, "100 cm")]
        [TestCase(1.5f, "1.50 m")]
        [TestCase(-1f, "-")]
        public void DiametersUseCentimetresBelowAMetre(float metres, string text)
        {
            Assert.AreEqual(text, HubPanelTextMath.Diameter(metres));
        }

        [TestCase(420f, "7:00")]
        [TestCase(65f, "1:05")]
        [TestCase(float.NaN, "-")]
        public void ClockIsMinutesAndSeconds(float seconds, string text)
        {
            Assert.AreEqual(text, HubPanelTextMath.Clock(seconds));
        }

        [Test]
        public void GoalLineDependsOnTheLevelKind()
        {
            StringAssert.Contains("80 cm", HubPanelTextMath.GoalLine(
                (int)StoryGoalType.A, 0.8f, 0, 420f, "", HubSettingsMath.LangEn));
            StringAssert.Contains("7:00", HubPanelTextMath.GoalLine(
                (int)StoryGoalType.A, 0.8f, 0, 420f, "", HubSettingsMath.LangEn));
            StringAssert.Contains("30", HubPanelTextMath.GoalLine(
                (int)StoryGoalType.B, 0f, 30, 120f, "", HubSettingsMath.LangZh));
            Assert.AreEqual(HubPanelTextMath.Text(HubPanelTextMath.FreeRoamGoal, HubSettingsMath.LangJa),
                HubPanelTextMath.GoalLine((int)StoryGoalType.F, 0f, 0, 0f, "", HubSettingsMath.LangJa));
        }

        [TestCase(HubSettingsMath.LangZh, "螃蟹", "通关", "未通关")]
        [TestCase(HubSettingsMath.LangEn, "Crabs", "Clear", "Not cleared")]
        [TestCase(HubSettingsMath.LangJa, "カニ", "クリア", "失敗")]
        public void CrabProgressAndClearAreLocalized(int lang, string crab, string passed, string failed)
        {
            string goal = HubPanelTextMath.GoalLine((int)StoryGoalType.B, 0.8f, 10, 420f, "crab", lang);
            StringAssert.Contains("7:00", goal);
            StringAssert.Contains(crab + " 10", goal);
            StringAssert.DoesNotContain("80 cm", goal);
            Assert.AreEqual(crab + " 9/10", HubPanelTextMath.CountProgress("crab", 9, 10, lang));
            Assert.AreEqual(passed, HubPanelTextMath.ClearLabel(StoryRoundMath.ClearPassed, lang));
            Assert.AreEqual(failed, HubPanelTextMath.ClearLabel(StoryRoundMath.ClearFailed, lang));
        }

        [Test]
        public void RecordLinesShowBestLastAndPlaysWithoutStars()
        {
            Assert.AreEqual("", HubPanelTextMath.RecordLines(false, true, 1f, 1, 1f, 1, 1, 0));
            Assert.AreEqual(HubPanelTextMath.Text(HubPanelTextMath.NoRecord, 1),
                HubPanelTextMath.RecordLines(true, false, 0f, 0, 0f, 0, 0, 1));
            string line = HubPanelTextMath.RecordLines(true, true, 0.82f, 135, 0.6f, 90, 3,
                HubSettingsMath.LangEn);
            StringAssert.Contains("Best 82 cm · 135 items", line);
            StringAssert.Contains("Last 60 cm · 90 items", line);
            StringAssert.Contains("Played 3", line);
            StringAssert.DoesNotContain("★", line);
        }

        [Test]
        public void ControlsNameTheDecidedInputsAndKeepTheDashKeyPlaceholder()
        {
            string vr = HubPanelTextMath.ControlsBody(HubSettingsMath.LangEn, true, 0, "");
            StringAssert.Contains("left trigger", vr);
            StringAssert.Contains("Jump", vr);
            string vrSimple = HubPanelTextMath.ControlsBody(HubSettingsMath.LangEn, true, 1, "");
            StringAssert.Contains("left stick rolls", vrSimple);
            string pc = HubPanelTextMath.ControlsBody(HubSettingsMath.LangEn, false, 0, "");
            StringAssert.Contains("Dash key", pc);
            StringAssert.Contains("Dash: Shift", HubPanelTextMath.ControlsBody(HubSettingsMath.LangEn, false, 0, "Shift"));
        }

        [Test]
        public void EveryPhaseHasAPromptKey()
        {
            Assert.AreEqual(HubPanelTextMath.PromptHub, HubRoomMath.PromptKey(HubRoomMath.PhaseHub));
            Assert.AreEqual(HubPanelTextMath.PromptLoading, HubRoomMath.PromptKey(HubRoomMath.PhaseLoading));
            Assert.AreEqual(HubPanelTextMath.PromptPlaying, HubRoomMath.PromptKey(HubRoomMath.PhasePlaying));
            Assert.AreEqual(HubPanelTextMath.PromptResult, HubRoomMath.PromptKey(HubRoomMath.PhaseResult));
        }
    }
}
