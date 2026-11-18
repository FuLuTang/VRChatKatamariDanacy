using NUnit.Framework;
using VRCKatamari.Input;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for <see cref="TankStickMath"/>: the two-stick tank scheme, the
    /// simple scheme and the deadzone.
    /// </summary>
    /// <remarks>
    /// Expected values are hand calculations from the mapping table in
    /// Docs/Design/ControlIntent/README.md. Deadzone 0 is used where the test is about
    /// the mapping itself, so the numbers stay readable.
    /// </remarks>
    public class TankStickTests
    {
        private const float Tolerance = 1e-5f;

        private static void AssertTank(float lx, float ly, float rx, float ry,
            float expectedForward, float expectedTurn, float expectedLateral)
        {
            float forward;
            float turn;
            float lateral;
            Assert.IsTrue(TankStickMath.TryMap(TankStickMath.ModeTank, lx, ly, rx, ry, 0f,
                out forward, out turn, out lateral));
            Assert.AreEqual(expectedForward, forward, Tolerance, "forward");
            Assert.AreEqual(expectedTurn, turn, Tolerance, "turn");
            Assert.AreEqual(expectedLateral, lateral, Tolerance, "lateral");
        }

        [Test]
        public void TankBothSticksForwardOrBackRollsStraight()
        {
            AssertTank(0f, 1f, 0f, 1f, 1f, 0f, 0f);
            AssertTank(0f, -1f, 0f, -1f, -1f, 0f, 0f);
        }

        [Test]
        public void TankOppositeSticksSpinOnTheSpot()
        {
            // Left forward + right back turns right (turn +1 is clockwise from above).
            AssertTank(0f, 1f, 0f, -1f, 0f, 1f, 0f);
            AssertTank(0f, -1f, 0f, 1f, 0f, -1f, 0f);
        }

        [Test]
        public void TankOneStickAloneCurvesAroundTheOtherSide()
        {
            // Left stick alone swings right; right stick alone swings left.
            AssertTank(0f, 1f, 0f, 0f, 0.5f, 0.5f, 0f);
            AssertTank(0f, 0f, 0f, 1f, 0.5f, -0.5f, 0f);
        }

        [Test]
        public void TankBothSticksSidewaysStrafes()
        {
            AssertTank(1f, 0f, 1f, 0f, 0f, 0f, 1f);
            AssertTank(-1f, 0f, -1f, 0f, 0f, 0f, -1f);
            // One stick sideways strafes at half rate, never turns.
            AssertTank(1f, 0f, 0f, 0f, 0f, 0f, 0.5f);
        }

        [Test]
        public void TankDiagonalForwardRollsAndCurves()
        {
            // Both sticks up-right: forward plus strafe to the right, no spin.
            float d = 0.70710678f;
            AssertTank(d, d, d, d, d, 0f, d);
        }

        [Test]
        public void SimpleLeftStickRollsInAnyDirectionAndRightXTurns()
        {
            float forward;
            float turn;
            float lateral;
            Assert.IsTrue(TankStickMath.TryMap(TankStickMath.ModeSimple, 0.5f, -0.5f, 0.75f, 1f,
                0f, out forward, out turn, out lateral));
            Assert.AreEqual(-0.5f, forward, Tolerance);
            Assert.AreEqual(0.5f, lateral, Tolerance);
            Assert.AreEqual(0.75f, turn, Tolerance);

            // The right stick's y is not a move axis in the simple scheme.
            Assert.IsTrue(TankStickMath.TryMap(TankStickMath.ModeSimple, 0f, 0f, 0f, 1f,
                0f, out forward, out turn, out lateral));
            Assert.AreEqual(0f, forward, Tolerance);
            Assert.AreEqual(0f, turn, Tolerance);
            Assert.AreEqual(0f, lateral, Tolerance);
        }

        [Test]
        public void DeadzoneZeroesSmallTiltAndRescalesTheRest()
        {
            float x;
            float y;
            Assert.IsTrue(TankStickMath.TryApplyRadialDeadzone(0.1f, 0.1f, 0.2f, out x, out y));
            Assert.AreEqual(0f, x, Tolerance);
            Assert.AreEqual(0f, y, Tolerance);

            // Full tilt stays full; halfway past the edge is half.
            Assert.IsTrue(TankStickMath.TryApplyRadialDeadzone(0f, 1f, 0.2f, out x, out y));
            Assert.AreEqual(1f, y, Tolerance);
            Assert.IsTrue(TankStickMath.TryApplyRadialDeadzone(0f, -0.6f, 0.2f, out x, out y));
            Assert.AreEqual(-0.5f, y, Tolerance);

            // A corner reading longer than 1 is capped at magnitude 1, direction kept.
            Assert.IsTrue(TankStickMath.TryApplyRadialDeadzone(1f, 1f, 0.2f, out x, out y));
            Assert.AreEqual(0.70710678f, x, Tolerance);
            Assert.AreEqual(0.70710678f, y, Tolerance);

            float a;
            Assert.IsTrue(TankStickMath.TryApplyAxisDeadzone(-0.6f, 0.2f, out a));
            Assert.AreEqual(-0.5f, a, Tolerance);
            Assert.IsTrue(TankStickMath.TryApplyAxisDeadzone(0.2f, 0.2f, out a));
            Assert.AreEqual(0f, a, Tolerance);
        }

        [Test]
        public void RestingSticksInsideTheDeadzoneProduceNoMotion()
        {
            float forward;
            float turn;
            float lateral;
            Assert.IsTrue(TankStickMath.TryMap(TankStickMath.ModeTank, 0.05f, 0.15f, -0.1f, -0.1f,
                TankStickMath.DefaultDeadzone, out forward, out turn, out lateral));
            Assert.AreEqual(0f, forward);
            Assert.AreEqual(0f, turn);
            Assert.AreEqual(0f, lateral);
        }

        [Test]
        public void InvalidInputIsRefusedWithZeroOutputs()
        {
            float forward;
            float turn;
            float lateral;
            Assert.IsFalse(TankStickMath.TryMap(TankStickMath.ModeTank, float.NaN, 1f, 0f, 1f,
                0f, out forward, out turn, out lateral));
            Assert.AreEqual(0f, forward);
            Assert.AreEqual(0f, turn);
            Assert.AreEqual(0f, lateral);
            Assert.IsFalse(TankStickMath.TryMap(TankStickMath.ModeSimple, 0f, 1f, 0f,
                float.PositiveInfinity, 0f, out forward, out turn, out lateral));
            Assert.IsFalse(TankStickMath.TryMap(7, 0f, 1f, 0f, 1f, 0f,
                out forward, out turn, out lateral));
            Assert.IsFalse(TankStickMath.TryMap(TankStickMath.ModeTank, 0f, 1f, 0f, 1f, 1f,
                out forward, out turn, out lateral));
            Assert.IsFalse(TankStickMath.TryMap(TankStickMath.ModeTank, 0f, 1f, 0f, 1f, -0.1f,
                out forward, out turn, out lateral));
            Assert.AreEqual(0f, forward);
        }

        [Test]
        public void SwappingTankSticksReversesTurnButPreservesTranslation()
        {
            AssertSettingsMap(TankStickMath.ModeTank, true, 0f, 1f,
                0.6f, 0.8f, -0.5f, 0f, 0.4f, -0.4f, 0.05f);
        }

        [Test]
        public void SwappingSimpleSticksMakesPhysicalRightRollAndPhysicalLeftXTurn()
        {
            AssertSettingsMap(TankStickMath.ModeSimple, true, 0f, 1f,
                -0.4f, 1f, 0.3f, 0.5f, 0.5f, -0.4f, 0.3f);
        }

        [TestCase(TankStickMath.ModeTank)]
        [TestCase(TankStickMath.ModeSimple)]
        public void ConfiguredDeadzoneReplacesTheDefault(int mode)
        {
            AssertSettingsMap(mode, false, 0.5f, 1f,
                0f, 0.4f, 0.4f, 0f, 0f, 0f, 0f);
            AssertSettingsMap(mode, false, 0.2f, 1f,
                0f, 0.4f, 0.4f, 0f, mode == TankStickMath.ModeTank ? 0.125f : 0.25f,
                mode == TankStickMath.ModeTank ? 0.125f : 0.25f,
                mode == TankStickMath.ModeTank ? 0.125f : 0f);
        }

        [TestCase(TankStickMath.ModeTank, 0.5f, 0.375f)]
        [TestCase(TankStickMath.ModeTank, 1.5f, 1f)]
        [TestCase(TankStickMath.ModeSimple, 0.5f, 0.375f)]
        [TestCase(TankStickMath.ModeSimple, 1.5f, 1f)]
        public void TurnSensitivityScalesOnlyTurnAndClampsBothSigns(int mode, float scale, float turn)
        {
            bool tank = mode == TankStickMath.ModeTank;
            for (int sign = -1; sign <= 1; sign += 2)
            {
                AssertSettingsMap(mode, false, 0f, scale,
                    0.25f, tank ? 0.5f * sign : 0.5f,
                    tank ? 0f : 0.75f * sign, tank ? -1f * sign : 0f,
                    tank ? -0.25f * sign : 0.5f, turn * sign, tank ? 0.125f : 0.25f);
            }
        }

        private static void AssertSettingsMap(int mode, bool swapHands, float deadzone,
            float sensitivity, float lx, float ly, float rx, float ry,
            float expectedForward, float expectedTurn, float expectedLateral)
        {
            Assert.IsTrue(TankStickMath.TryMapWithSettings(mode, lx, ly, rx, ry, swapHands,
                deadzone, sensitivity, out float forward, out float turn, out float lateral));
            Assert.AreEqual(expectedForward, forward, Tolerance, "forward");
            Assert.AreEqual(expectedTurn, turn, Tolerance, "turn");
            Assert.AreEqual(expectedLateral, lateral, Tolerance, "lateral");
        }

        [Test]
        public void OutOfRangeSticksAreClamped()
        {
            AssertTank(0f, 3f, 0f, 3f, 1f, 0f, 0f);
        }
    }
}
