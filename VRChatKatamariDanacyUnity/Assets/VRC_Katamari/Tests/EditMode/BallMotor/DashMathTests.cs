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
            Assert.AreEqual((spinGachas - 1) * 0.2f, DashMath.SpinSecondsFor(diameterM), 1e-6f);
            Assert.AreEqual((spinGachas + 3f) * 0.2f, DashMath.ChargeSecondsFor(diameterM), 1e-6f);
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
            for (int i = 1; i <= 20; i++)
            {
                Assert.IsTrue(DashMath.IsCharging(state));
                Assert.IsTrue(BallMotionMath.TryStep(0f, 0f, 0f, 0f, velocity, Step, 1f,
                    BaseSpeed, 5f, 8f, 0f, 1.9f, 2.9f, 110f, 1.2f,
                    out _, out Vector3 next, out _));
                // 0.4 s 的起转点早于 A2 的完全滑停（2.12 s）。
                Assert.AreEqual(4f - i * 1.9f * Step, next.z, 1e-5f);
                velocity = next;
                Assert.IsFalse(Advance(state, 1));
                Assert.AreEqual(i == 20, DashMath.IsSpinning(state, SmallDiameterM));
            }
            Assert.AreEqual(3f, DashMath.EnergySeconds(state), 1e-6f);
        }

        [Test]
        public void BeforeSpinReleaseDoesNotFireOrPreventNextCharge()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Assert.IsFalse(Advance(state, 19));
            Assert.AreEqual(DashMath.ReleaseBeforeSpin, DashMath.ReleaseCharge(state, SmallDiameterM));
            Assert.IsFalse(TakeLaunch(state, 0f, out _));
            Assert.IsTrue(DashMath.TryBeginCharge(state));
        }

        [Test]
        public void ReleaseAfterSpinLaunchesAtSixtySevenPercentAlongCurrentHeading()
        {
            float[] state = FreshState();
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Assert.IsFalse(Advance(state, 20));
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
            Assert.IsFalse(Advance(state, 59));
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
            // 模拟出射后已部分失速；即使正常滚阻在寿命内将其降至零，
            // 冲刺状态也不能因零速退出。
            Vector3 velocity = Vector3.forward * 2f;
            DashMath.DriveScales(state, SmallDiameterM, out float speed, out float acceleration);
            for (int i = 0; i < 99; i++)
            {
                Assert.IsTrue(BallMotionMath.TryStep(0f, 0f, 0f, 0f, velocity, Step, 1f,
                    BaseSpeed * speed, 5f * acceleration, 8f, 0f, 1.9f, 2.9f, 110f, 1.2f,
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
            Advance(state, 20);
            Assert.AreEqual(DashMath.ReleaseHelpDash, DashMath.ReleaseCharge(state, SmallDiameterM));
            DashMath.DriveScales(state, SmallDiameterM, out speed, out acceleration);
            Assert.AreEqual(2.4907975f, speed, 1e-6f);
            Assert.AreEqual(4.333333f / 1.1f, acceleration, 1e-6f);
            Assert.AreEqual(speed * 0.67f, DashMath.LaunchSpeedScale(state, speed), 1e-6f);
            Assert.IsTrue(BallMotionMath.TryStep(1f, 0f, 0f, 0f, Vector3.zero, Step, 1f,
                BaseSpeed * speed, 5f * acceleration, 8f, 0f, 1.9f, 2.9f, 110f, 1.2f,
                out _, out Vector3 next, out _));
            Assert.AreEqual(5f * acceleration * Step, next.z, 1e-6f);
        }

        private const float R = 0.15f;
        private static readonly Vector3 WallNormal = new Vector3(0f, 0f, -1f);

        [Test]
        public void HeadOnWallEndsDashEvenWhenRecordedDepthIsZeroAndClimbJustTookTheFace()
        {
            // 实测：9.96 m/s 撞边界面，速度归零；记录深度 0、爬墙通道同一步接管该面。
            Assert.IsTrue(DashMath.IsDashWallHit(WallNormal, 90f, 3f, R,
                new Vector3(0f, 0f, 9.96f), Vector3.zero, false));
        }

        [Test]
        public void WallThatWasAlreadyBeingClimbedBeforeThisStepIsNotAFreshHit()
        {
            Assert.IsFalse(DashMath.IsDashWallHit(WallNormal, 90f, 3f, R,
                new Vector3(0f, 0f, 9.96f), Vector3.zero, true));
        }

        [Test]
        public void GrazingAlongAWallIsNotAHit()
        {
            // 几乎平行于墙：法向分量只有 0.3 m/s，占 3%。
            Assert.IsFalse(DashMath.IsDashWallHit(WallNormal, 90f, 3f, R,
                new Vector3(9.9f, 0f, 0.3f), new Vector3(9.9f, 0f, 0f), false));
            // 斜擦：法向 3 m/s 占约 30%，且被导向而不是被挡住。
            Assert.IsFalse(DashMath.IsDashWallHit(WallNormal, 90f, 3f, R,
                new Vector3(9.5f, 0f, 3f), new Vector3(9.5f, 0f, 2.5f), false));
        }

        [Test]
        public void WalkableSlopeAndLowStepAreNotWalls()
        {
            // 55° 坡：法线偏上，由坡道通道处理。
            Vector3 slope = new Vector3(0f, 0.57f, -0.82f);
            Assert.IsFalse(DashMath.IsDashWallHit(slope, 55f, 3f, R,
                new Vector3(0f, 0f, 9f), new Vector3(0f, 0f, 5f), false));
            // 低矮台阶（顶面不高于球半径）：滚越。
            Assert.IsFalse(DashMath.IsDashWallHit(WallNormal, 90f, 0.10f, R,
                new Vector3(0f, 0f, 9f), Vector3.zero, false));
            // 没有顶面（无限高）的墙仍是墙。
            Assert.IsTrue(DashMath.IsDashWallHit(WallNormal, 90f, float.MaxValue, R,
                new Vector3(0f, 0f, 9f), new Vector3(0f, 0f, 1f), false));
        }

        [Test]
        public void SeparatingOrNonFiniteInputIsNotAHit()
        {
            Assert.IsFalse(DashMath.IsDashWallHit(WallNormal, 90f, 3f, R,
                new Vector3(0f, 0f, -6f), Vector3.zero, false));
            Assert.IsFalse(DashMath.IsDashWallHit(WallNormal, float.NaN, 3f, R,
                new Vector3(0f, 0f, 6f), Vector3.zero, false));
        }

        [TestCase(0.3f, 0.4f, 1.2f)]
        [TestCase(100f, 0.6f, 1.4f)]
        [TestCase(500f, 0.8f, 1.6f)]
        public void ChargeTimesFollowFiveInputsPerSecond(float diameterM, float spin, float full)
        {
            Assert.AreEqual(spin, DashMath.SpinSecondsFor(diameterM), 1e-5f);
            Assert.AreEqual(full, DashMath.ChargeSecondsFor(diameterM), 1e-5f);
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
            Advance(state, 20);
            DashMath.Cancel(state);
            Assert.AreEqual(DashMath.ReleaseNone, DashMath.ReleaseCharge(state, SmallDiameterM));
            Assert.IsTrue(DashMath.TryBeginCharge(state));
            Assert.IsTrue(Advance(state, 60));
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
            Assert.IsTrue(Advance(state, 60));
            Assert.IsTrue(TakeLaunch(state, 0f, out _));
            return state;
        }
    }
}
