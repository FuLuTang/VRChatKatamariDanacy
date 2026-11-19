using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallMotor;
using VRCKatamari.Motion;

namespace VRCKatamari.Tests
{
    public class DashMathTests
    {
        private const float Step = 0.02f;
        private const float BaseSpeed = 4f;
        private const float SmallDiameterM = 0.3f;

        private static float[] FreshState()
        {
            float[] state = new float[DashMath.StateLength];
            Assert.IsTrue(DashMath.TryReset(state));
            return state;
        }

        private static bool Advance(float[] state, int steps, bool hitWall = false,
            float diameterM = SmallDiameterM)
        {
            bool autoLaunched = false;
            for (int i = 0; i < steps; i++)
            {
                Assert.IsTrue(DashMath.TryStep(state, Step, diameterM, hitWall, out bool fired));
                if (fired) Assert.IsFalse(autoLaunched, "单次长按只可自动出射一次");
                autoLaunched |= fired;
            }
            return autoLaunched;
        }

        [TestCase(59.999f, 3)]
        [TestCase(60f, 4)]
        [TestCase(299.999f, 4)]
        [TestCase(300f, 5)]
        public void DashTiersUseWlkWheelSpinTable(float diameterM, int spinGachas)
        {
            Assert.AreEqual(spinGachas, DashMath.SpinGachasFor(diameterM));
            Assert.AreEqual(spinGachas * 14f / 30f, DashMath.SpinSecondsFor(diameterM), 1e-6f);
            Assert.AreEqual((spinGachas + 4f) * 14f / 30f, DashMath.ChargeSecondsFor(diameterM), 1e-6f);
        }

        [TestCase(0.035f, 27f / 9f)]
        [TestCase(0.2f, 82f / 33f)]
        [TestCase(0.3f, (82f + 160f / 3f) / (33f + 64f / 3f))]
        [TestCase(12f, 2966f / 957f)]
        [TestCase(400f, (62000f + 38000f / 11f) / (20000f + 20280f / 11f))]
        public void DashSpeedUsesRatioOfIndependentlyInterpolatedColumns(float diameterM, float scale)
        {
            DashMath.FullDriveScales(diameterM, out float speed, out _);
            Assert.AreEqual(scale, speed, 1e-6f);
        }

        [Test]
        public void ChargingUsesOrdinaryCoastAndReachesSpinWithoutSpendingLife()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Vector3 velocity = Vector3.forward * BaseSpeed;
            for (int i = 1; i <= 70; i++)
            {
                Assert.IsTrue(DashMath.IsCharging(state));
                Assert.IsTrue(BallMotionMath.TryStep(0f, 0f, 0f, 0f, velocity, Step, 1f,
                    BaseSpeed, 5f, 8f, 1.2f, 1.3f, 2f, 110f, 1.2f,
                    out _, out Vector3 next, out _));
                if (i == 1) Assert.AreEqual(4f * Mathf.Exp(-1.2f * Step) - 0.026f, next.z, 1e-6f);
                if (i < 65) Assert.Greater(next.z, 0f);
                else Assert.AreEqual(Vector3.zero, next);
                velocity = next;
                Assert.IsFalse(Advance(state, 1));
                Assert.AreEqual(i == 70, DashMath.IsSpinning(state, SmallDiameterM));
            }
            Assert.AreEqual(3f, DashMath.EnergySeconds(state), 1e-6f);
        }

        [Test]
        public void BeforeSpinReleaseDoesNotFireOrPreventNextCharge()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Assert.IsFalse(Advance(state, 69));
            Assert.AreEqual(DashMath.ReleaseBeforeSpin, DashMath.ReleaseCharge(state, SmallDiameterM));
            Assert.IsFalse(TakeLaunch(state, 0f, out _));
            Assert.IsTrue(DashMath.TryBeginCharge(state));
        }

        [Test]
        public void ReleaseAfterSpinLaunchesAtSixtySevenPercentAlongCurrentHeading()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Assert.IsFalse(Advance(state, 70));
            Assert.AreEqual(DashMath.ReleaseHelpDash, DashMath.ReleaseCharge(state, SmallDiameterM));
            Assert.IsTrue(TakeLaunch(state, 90f, out Vector3 launch));
            Assert.AreEqual(6.675337f, launch.x, 1e-5f);
            Assert.AreEqual(0f, launch.y, 1e-5f);
            Assert.AreEqual(0f, launch.z, 1e-5f);
            Assert.IsFalse(TakeLaunch(state, 90f, out _));
        }

        [Test]
        public void FullChargeLaunchesAutomaticallyAndReleaseDoesNotLaunchAgain()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Assert.IsFalse(Advance(state, 163));
            Assert.IsTrue(Advance(state, 1));
            Assert.IsFalse(DashMath.IsCharging(state));
            Assert.IsFalse(DashMath.IsSpinning(state, SmallDiameterM));
            Assert.IsTrue(DashMath.IsDashing(state));
            Assert.IsFalse(Advance(state, 1, true), "待出射时不能被零速终止或先耗掉寿命");
            Assert.AreEqual(3f, DashMath.EnergySeconds(state), 1e-6f);
            Assert.AreEqual(DashMath.ReleaseNone, DashMath.ReleaseCharge(state, SmallDiameterM));
            Assert.IsTrue(TakeLaunch(state, 0f, out Vector3 launch));
            Assert.AreEqual(9.96319f, launch.z, 1e-5f);
            Assert.IsFalse(TakeLaunch(state, 0f, out _));
        }

        [Test]
        public void DashEndsOnWallButNotWhenOrdinaryResistanceBringsItToRest()
        {
            float[] state = LaunchedState();
            Vector3 velocity = Vector3.forward * 9.96319f;
            DashMath.DriveScales(state, SmallDiameterM, out float speed, out float acceleration);
            for (int i = 0; i < 99; i++)
            {
                Assert.IsTrue(BallMotionMath.TryStep(0f, 0f, 0f, 0f, velocity, Step, 1f,
                    BaseSpeed * speed, 5f * acceleration, 8f, 1.2f, 1.3f, 2f, 110f, 1.2f,
                    out _, out Vector3 next, out _));
                Advance(state, 1);
                velocity = next;
            }
            Assert.AreEqual(Vector3.zero, velocity);
            Assert.IsTrue(DashMath.IsDashing(state));
            Advance(state, 1, true);
            Assert.IsFalse(DashMath.IsDashing(state));
            Assert.IsFalse(TakeLaunch(state, 0f, out _));
        }

        [Test]
        public void EarlyLaunchIsWeakerButUsesSameDashAccelerationAndSpeedCap()
        {
            float[] state = FreshState();
            DashMath.DriveScales(state, SmallDiameterM, out float speed, out float acceleration);
            Assert.AreEqual(1f, speed);
            Assert.AreEqual(1f, acceleration);
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Advance(state, 70);
            Assert.AreEqual(DashMath.ReleaseHelpDash, DashMath.ReleaseCharge(state, SmallDiameterM));
            DashMath.DriveScales(state, SmallDiameterM, out speed, out acceleration);
            Assert.AreEqual(2.4907975f, speed, 1e-6f);
            Assert.AreEqual(4.333333f / 1.1f, acceleration, 1e-6f);
            Assert.AreEqual(speed * 0.67f, DashMath.LaunchSpeedScale(state, speed), 1e-6f);
            Assert.IsTrue(BallMotionMath.TryStep(1f, 0f, 0f, 0f, Vector3.zero, Step, 1f,
                BaseSpeed * speed, 5f * acceleration, 8f, 1.2f, 1.3f, 2f, 110f, 1.2f,
                out _, out Vector3 next, out _));
            Assert.AreEqual(5f * acceleration * Step * Mathf.Exp(-1.2f * Step), next.z, 1e-6f);
        }

        [Test]
        public void WallRequiresInwardSpeedActuallyRemovedNotAParallelOrSeparatingTouch()
        {
            Vector3 normal = new Vector3(0f, 0f, -1f);
            Assert.IsTrue(DashMath.IsBlockedByWall(normal, new Vector3(2f, 0f, 6f), new Vector3(2f, 0f, 0f)));
            Assert.IsFalse(DashMath.IsBlockedByWall(normal, new Vector3(2f, 0f, 6f), new Vector3(0f, 0f, 6f)));
            Assert.IsFalse(DashMath.IsBlockedByWall(normal, new Vector3(2f, 0f, 0f), Vector3.zero));
            Assert.IsFalse(DashMath.IsBlockedByWall(normal, new Vector3(2f, 0f, -6f), Vector3.zero));
        }

        [Test]
        public void BackwardInputBrakesButNeutralDoesNot()
        {
            float[] state = LaunchedState();
            Assert.IsFalse(DashMath.TryBrake(state, 0f));
            Assert.IsFalse(DashMath.TryBrake(state, 1f));
            Assert.IsTrue(DashMath.TryBrake(state, -0.5f));
            Assert.IsFalse(DashMath.IsDashing(state));
        }

        [Test]
        public void DashUsesNinetyOriginalFramesThenHuffsForEightSeconds()
        {
            float[] state = LaunchedState();
            Assert.IsFalse(Advance(state, 149));
            Assert.IsTrue(DashMath.IsDashing(state));
            Assert.AreEqual(1f / 30f, DashMath.EnergySeconds(state), 1e-6f);
            Assert.IsFalse(Advance(state, 1));
            Assert.IsFalse(DashMath.IsDashing(state));
            Assert.IsTrue(DashMath.IsHuffed(state));
            Assert.IsFalse(DashMath.TryBeginCharge(state));
            Assert.IsFalse(Advance(state, 399));
            Assert.IsTrue(DashMath.IsHuffed(state));
            Assert.IsFalse(Advance(state, 1));
            Assert.IsFalse(DashMath.IsHuffed(state));
            Assert.AreEqual(3f, DashMath.EnergySeconds(state), 1e-6f);
            Assert.IsTrue(DashMath.TryBeginCharge(state));
        }

        [Test]
        public void DashRecoversOneLifePerTenOriginalFramesOutsideDashOrHuff()
        {
            float[] state = LaunchedState();
            Advance(state, 10); // 消耗 6 帧。
            Assert.IsTrue(DashMath.TryBrake(state, -1f));
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Advance(state, 16);
            Assert.AreEqual(84f / 30f, DashMath.EnergySeconds(state), 1e-6f);
            Advance(state, 1);
            Assert.AreEqual(85f / 30f, DashMath.EnergySeconds(state), 1e-6f);
        }

        [Test]
        public void GateCancellationStopsBothActiveChargeAndPendingLaunch()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Advance(state, 70);
            DashMath.Cancel(state);
            Assert.AreEqual(DashMath.ReleaseNone, DashMath.ReleaseCharge(state, SmallDiameterM));
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Assert.IsTrue(Advance(state, 164));
            DashMath.Cancel(state);
            Assert.IsFalse(DashMath.IsDashing(state));
            Assert.IsFalse(TakeLaunch(state, 0f, out _));
        }

        [Test]
        public void DashRejectsInvalidStatesAndDeltaWithoutCreatingLaunch()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Assert.IsFalse(DashMath.TryStep(state, float.NaN, SmallDiameterM, false, out _));
            Assert.AreEqual(0f, DashMath.ChargeSeconds(state));
            Assert.IsFalse(DashMath.TryBeginCharge(new float[1]));
            Assert.IsFalse(DashMath.TryReset(null));
            state[0] = float.NaN;
            Assert.IsFalse(DashMath.IsCharging(state));
            Assert.IsFalse(DashMath.IsDashing(state));
        }

        private static bool TakeLaunch(float[] state, float yaw, out Vector3 velocity)
        {
            DashMath.DriveScales(state, SmallDiameterM, out float speed, out _);
            return DashMath.TryTakeLaunch(state, yaw, BaseSpeed, speed, out velocity);
        }

        private static float[] LaunchedState()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Assert.IsTrue(Advance(state, 164));
            Assert.IsTrue(TakeLaunch(state, 0f, out _));
            return state;
        }
    }
}
