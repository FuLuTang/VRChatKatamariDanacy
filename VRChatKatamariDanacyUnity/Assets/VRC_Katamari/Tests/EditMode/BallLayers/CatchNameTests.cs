using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;
using VRCKatamari.Hub;

namespace VRCKatamari.Tests
{
    public class CatchNameTests
    {
        private const float Tolerance = 0.0001f;

        [Test]
        public void TimingMatchesTheWlkFrameCounts()
        {
            Assert.AreEqual(2f / 30f, CatchNameMath.CharSeconds, Tolerance);
            Assert.AreEqual(3.5f, CatchNameMath.HoldSeconds, Tolerance);
            Assert.AreEqual(5, CatchNameMath.ShortNameMaxChars);
        }

        [TestCase(0f, 4, 1)]
        [TestCase(0.07f, 4, 2)]
        [TestCase(0.25f, 4, 4)]
        [TestCase(10f, 4, 4)]
        [TestCase(0f, 0, 0)]
        [TestCase(-0.1f, 4, 0)]
        public void CharactersAppearOneEveryTwoFrames(float elapsed, int length, int expected)
        {
            Assert.AreEqual(expected, CatchNameMath.VisibleCharacters(elapsed, length));
        }

        [Test]
        public void NonFiniteTimeShowsNothing()
        {
            Assert.AreEqual(0, CatchNameMath.VisibleCharacters(float.NaN, 4));
            Assert.IsFalse(CatchNameMath.IsNameShown(float.NaN, 4));
        }

        [Test]
        public void NameStaysForTheHoldAfterTheLastCharacter()
        {
            float typed = 4 * CatchNameMath.CharSeconds;
            Assert.IsTrue(CatchNameMath.IsNameShown(typed + 3.49f, 4));
            Assert.IsFalse(CatchNameMath.IsNameShown(typed + 3.51f, 4));
            Assert.IsFalse(CatchNameMath.IsNameShown(0f, 0));
        }

        [TestCase(1, true)]
        [TestCase(5, true)]
        [TestCase(6, false)]
        public void FiveCharactersOrFewerUseTheShortLayout(int length, bool expected)
        {
            Assert.AreEqual(expected, CatchNameMath.UsesShortLayout(length));
        }

        [TestCase(0f, 0f)]
        [TestCase(1f, 300f)]
        [TestCase(1.5f, 270f)]
        [TestCase(6f, 0f)]
        public void ModelTurnsSixtyDegreesPerSecondClockwise(float elapsed, float expected)
        {
            Assert.AreEqual(expected, CatchNameMath.SpinDegrees(elapsed), Tolerance);
        }

        [Test]
        public void ANewerFrameAlwaysReplacesAndTheSameFrameKeepsTheHigherPriority()
        {
            Assert.IsTrue(CatchNameMath.Replaces(false, 5, 0));
            Assert.IsFalse(CatchNameMath.Replaces(true, 1, 0));
            Assert.IsTrue(CatchNameMath.Replaces(true, 0, 0));
            Assert.IsTrue(CatchNameMath.Replaces(true, 0, 1));
        }

        [Test]
        public void NameFollowsTheLanguageAndFallsBackToEnglish()
        {
            Assert.AreEqual("闹钟", CatchNameMath.PickName(HubSettingsMath.LangZh, "闹钟", "Alarm Clock", "メザマシドケイ"));
            Assert.AreEqual("Alarm Clock", CatchNameMath.PickName(HubSettingsMath.LangEn, "闹钟", "Alarm Clock", "メザマシドケイ"));
            Assert.AreEqual("メザマシドケイ", CatchNameMath.PickName(HubSettingsMath.LangJa, "闹钟", "Alarm Clock", "メザマシドケイ"));
            Assert.AreEqual("Supermarket", CatchNameMath.PickName(HubSettingsMath.LangZh, "", "Supermarket", "スーパーマーケット"));
            Assert.AreEqual("闹钟", CatchNameMath.PickName(HubSettingsMath.LangJa, "闹钟", null, ""));
            Assert.AreEqual("", CatchNameMath.PickName(HubSettingsMath.LangZh, null, null, null));
        }

        [Test]
        public void ModelFitsItsLongestEdgeIntoTheDisc()
        {
            Assert.AreEqual(0.375f, CatchNameMath.FitScale(new Vector3(-1f, -0.5f, 0f),
                new Vector3(1f, 0.5f, 0.2f), 0.75f), Tolerance);
            Assert.AreEqual(0f, CatchNameMath.FitScale(Vector3.zero, Vector3.zero, 0.75f));
            Assert.AreEqual(0f, CatchNameMath.FitScale(Vector3.zero, new Vector3(float.NaN, 1f, 1f), 0.75f));
        }

        [Test]
        public void RotatedBoxReachSwapsAxesForAQuarterTurn()
        {
            float h = Mathf.Sqrt(0.5f);
            Vector3 reach = CatchNameMath.RotatedHalfExtents(new Quaternion(0f, h, 0f, h), new Vector3(1f, 2f, 3f));
            Assert.AreEqual(3f, reach.x, Tolerance);
            Assert.AreEqual(2f, reach.y, Tolerance);
            Assert.AreEqual(1f, reach.z, Tolerance);
        }

        [TestCase(0f, 0f)]
        [TestCase(0.15f, 0.5f)]
        [TestCase(0.3f, 1f)]
        [TestCase(-1f, 1f)]
        public void CatchLineCollapsesWithinAThirdOfASecond(float elapsed, float expected)
        {
            Assert.AreEqual(expected, CatchNameMath.LineProgress(elapsed), Tolerance);
        }

        [Test]
        public void PanelFacesAwayFromTheEyeAlongTheSightLine()
        {
            Vector3 down = CatchNameMath.FacingRotation(0f, -15f) * Vector3.forward;
            Assert.AreEqual(0f, down.x, Tolerance);
            Assert.AreEqual(-Mathf.Sin(15f * Mathf.Deg2Rad), down.y, Tolerance);
            Assert.AreEqual(Mathf.Cos(15f * Mathf.Deg2Rad), down.z, Tolerance);
            Vector3 right = CatchNameMath.FacingRotation(90f, 0f) * Vector3.forward;
            Assert.AreEqual(1f, right.x, Tolerance);
            Assert.AreEqual(0f, right.y, Tolerance);
            Assert.AreEqual(0f, right.z, Tolerance);
        }

        [TestCase(1.64f, 1.4f)]
        [TestCase(0.82f, 0.7f)]
        [TestCase(0f, 1.4f)]
        public void DistanceScalesWithTheRiderEyeHeight(float eyeHeight, float expected)
        {
            Assert.AreEqual(expected, CatchNameMath.ScaledDistance(1.4f, eyeHeight), Tolerance);
        }

        [Test]
        public void SharedFollowPoseAcceptsAPlacementBelowAndLeftOfTheSightLine()
        {
            Vector3 position;
            Quaternion rotation;
            float scale;
            Assert.IsTrue(PushCueMath.TryEvaluate(Vector3.zero, CatchNameMath.DefaultYawOffsetDegrees, 0f,
                1.4f, CatchNameMath.DefaultElevationDegrees, CatchNameMath.DefaultAngularSizeDegrees,
                out position, out rotation, out scale));
            Assert.AreEqual(1.4f * Mathf.Sin(-15f * Mathf.Deg2Rad), position.y, Tolerance);
            Assert.Less(position.x, 0f);
            Assert.AreEqual(1.4f, position.magnitude, Tolerance);
        }

        [TestCase(-90f)]
        [TestCase(90f)]
        [TestCase(float.NaN)]
        [TestCase(float.NegativeInfinity)]
        public void SharedFollowPoseStillRejectsVerticalAndNonFiniteElevation(float elevation)
        {
            Vector3 position;
            Quaternion rotation;
            float scale;
            Assert.IsFalse(PushCueMath.TryEvaluate(Vector3.zero, 0f, 0f, 1.4f, elevation, 7f,
                out position, out rotation, out scale));
            Assert.AreEqual(0f, scale);
        }

        [Test]
        public void CatchNameSettingDefaultsOnAndStepsBothWays()
        {
            int id = HubSettingsMath.CatchName;
            Assert.AreEqual("vrck.set.catchName", HubSettingsMath.StorageKey(id));
            Assert.AreEqual(1, HubSettingsMath.Default(id));
            Assert.AreEqual(0, HubSettingsMath.Stepped(id, 1, 1));
            Assert.AreEqual(1, HubSettingsMath.Stepped(id, 0, -1));
            Assert.IsTrue(HubSettingsMath.HasConsumer(id));
        }
    }
}
