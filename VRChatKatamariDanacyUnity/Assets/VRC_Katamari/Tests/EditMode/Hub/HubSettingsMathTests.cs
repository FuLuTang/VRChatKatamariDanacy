using System.Collections.Generic;
using NUnit.Framework;
using VRCKatamari.Hub;
using VRCKatamari.Input;
using VRCKatamari.Story;

namespace VRCKatamari.Tests
{
    public class HubSettingsMathTests
    {
        [Test]
        public void EverySettingHasAUniqueKeyAndADefaultInsideItsRange()
        {
            var keys = new HashSet<string>();
            for (int id = 0; id < HubSettingsMath.Count; id++)
            {
                string key = HubSettingsMath.StorageKey(id);
                Assert.IsFalse(string.IsNullOrEmpty(key), "id " + id);
                Assert.IsTrue(keys.Add(key), "duplicate key " + key);
                Assert.LessOrEqual(HubSettingsMath.Min(id), HubSettingsMath.Default(id));
                Assert.GreaterOrEqual(HubSettingsMath.Max(id), HubSettingsMath.Default(id));
                Assert.Less(HubSettingsMath.Min(id), HubSettingsMath.Max(id), "id " + id + " has one value");
            }
        }

        [Test]
        public void DefaultsMatchTheConsumersOwnDefaults()
        {
            Assert.AreEqual(TankStickMath.ModeTank, HubSettingsMath.Default(HubSettingsMath.ControlMode));
            Assert.AreEqual(TankStickMath.DefaultDeadzone,
                HubSettingsMath.DeadzoneValue(HubSettingsMath.Default(HubSettingsMath.Deadzone)), 1e-6f);
            Assert.AreEqual(0, HubSettingsMath.Default(HubSettingsMath.SwapHands));
            Assert.AreEqual(TankStickMath.DefaultTurnSensitivity, HubSettingsMath.TurnSensitivityValue(
                HubSettingsMath.Default(HubSettingsMath.TurnSensitivity)), 1e-6f);
        }

        [Test]
        public void ScalesStopAtTheirEnds()
        {
            int max = HubSettingsMath.Max(HubSettingsMath.MusicVolume);
            Assert.AreEqual(max, HubSettingsMath.Stepped(HubSettingsMath.MusicVolume, max, 1));
            Assert.AreEqual(0, HubSettingsMath.Stepped(HubSettingsMath.MusicVolume, 0, -1));
            Assert.AreEqual(1, HubSettingsMath.Stepped(HubSettingsMath.Deadzone, 1, -1));
        }

        [Test]
        public void ChoicesWrapBothWays()
        {
            Assert.AreEqual(HubSettingsMath.LangJa,
                HubSettingsMath.Stepped(HubSettingsMath.Language, HubSettingsMath.LangZh, -1));
            Assert.AreEqual(HubSettingsMath.LangZh,
                HubSettingsMath.Stepped(HubSettingsMath.Language, HubSettingsMath.LangJa, 1));
            Assert.AreEqual(1, HubSettingsMath.Stepped(HubSettingsMath.ControlMode, 0, 1));
            Assert.AreEqual(0, HubSettingsMath.Stepped(HubSettingsMath.ControlMode, 1, 1));
        }

        [Test]
        public void OutOfRangeStoredValuesAreClampedAndUnknownIdsAreInert()
        {
            Assert.AreEqual(10, HubSettingsMath.Clamp(HubSettingsMath.EffectsVolume, 99));
            Assert.AreEqual(0, HubSettingsMath.Clamp(HubSettingsMath.SwapHands, -5));
            Assert.AreEqual(0, HubSettingsMath.Stepped(-1, 3, 1));
            Assert.AreEqual("", HubSettingsMath.StorageKey(HubSettingsMath.Count));
        }

        [TestCase("ja", HubSettingsMath.LangJa)]
        [TestCase("ja-JP", HubSettingsMath.LangJa)]
        [TestCase("zh-CN", HubSettingsMath.LangZh)]
        [TestCase("zh-TW", HubSettingsMath.LangZh)]
        [TestCase("en", HubSettingsMath.LangEn)]
        [TestCase("ko", HubSettingsMath.LangEn)]
        [TestCase("", HubSettingsMath.LangEn)]
        [TestCase(null, HubSettingsMath.LangEn)]
        public void ClientLanguageMapsToAPanelLanguage(string code, int lang)
        {
            Assert.AreEqual(lang, HubSettingsMath.LanguageFromCode(code));
        }

        [Test]
        public void ConvertedValuesFollowTheSteps()
        {
            Assert.AreEqual(0.5f, HubSettingsMath.Volume01(HubSettingsMath.VoiceVolume, 5), 1e-6f);
            Assert.AreEqual(0f, HubSettingsMath.Volume01(HubSettingsMath.Deadzone, 5));
            Assert.AreEqual(0.5f, HubSettingsMath.Haptics01(1), 1e-6f);
            Assert.AreEqual(4, HubSettingsMath.DeadzoneStep(0.2f));
            Assert.AreEqual(HubSettingsMath.Min(HubSettingsMath.Deadzone), HubSettingsMath.DeadzoneStep(float.NaN));
        }

        [Test]
        public void ActiveSettingConsumersMatchTheHubContract()
        {
            for (int id = 0; id < HubSettingsMath.Count; id++)
            {
                bool expected = id == HubSettingsMath.ControlMode || id == HubSettingsMath.SwapHands
                    || id == HubSettingsMath.Deadzone || id == HubSettingsMath.TurnSensitivity
                    || id == HubSettingsMath.Language || id == HubSettingsMath.EffectsVolume
                    || id == HubSettingsMath.Haptics || id == HubSettingsMath.PushCue;
                Assert.AreEqual(expected, HubSettingsMath.HasConsumer(id), "id " + id);
            }
        }
    }

    public class HubRecordMathTests
    {
        private const int Size = (int)StoryGoalType.ReachTargetDiameter;
        private const int Count = (int)StoryGoalType.CollectCategoryCount;
        private const int Free = (int)StoryGoalType.FreeRoam;

        [Test]
        public void FreeRoamAndAbortedRoundsAreNotRecorded()
        {
            Assert.IsTrue(HubRecordMath.IsRecorded(Size, false));
            Assert.IsTrue(HubRecordMath.IsRecorded(Count, false));
            Assert.IsFalse(HubRecordMath.IsRecorded(Free, false));
            Assert.IsFalse(HubRecordMath.IsRecorded(Size, true));
        }

        [Test]
        public void SizeLevelsRankByDiameterThenCount()
        {
            Assert.IsTrue(HubRecordMath.IsBetter(Size, 0.5f, 1, false, 0f, 0));
            Assert.IsTrue(HubRecordMath.IsBetter(Size, 0.8f, 10, true, 0.7f, 200));
            Assert.IsFalse(HubRecordMath.IsBetter(Size, 0.6f, 500, true, 0.7f, 200));
            Assert.IsTrue(HubRecordMath.IsBetter(Size, 0.7f, 201, true, 0.7f, 200));
            Assert.IsFalse(HubRecordMath.IsBetter(Size, 0.7f, 200, true, 0.7f, 200));
        }

        [Test]
        public void CountLevelsRankByCountThenDiameter()
        {
            Assert.IsTrue(HubRecordMath.IsBetter(Count, 0.1f, 31, true, 2f, 30));
            Assert.IsFalse(HubRecordMath.IsBetter(Count, 3f, 29, true, 2f, 30));
            Assert.IsTrue(HubRecordMath.IsBetter(Count, 2.1f, 30, true, 2f, 30));
        }

        [Test]
        public void InvalidFactsNeverBecomeABest()
        {
            Assert.IsFalse(HubRecordMath.IsBetter(Size, float.NaN, 1, false, 0f, 0));
            Assert.IsFalse(HubRecordMath.IsBetter(Size, 1f, -1, false, 0f, 0));
        }

        [Test]
        public void KeysAreNamespacedPerLevelAndField()
        {
            Assert.AreEqual("vrck.rec._ref_cancer.bestD",
                HubRecordMath.StorageKey("_ref_cancer", HubRecordMath.FieldBestDiameter));
            Assert.AreEqual("", HubRecordMath.StorageKey("", HubRecordMath.FieldPlays));
            Assert.AreEqual("", HubRecordMath.StorageKey("a", null));
        }
    }
}
