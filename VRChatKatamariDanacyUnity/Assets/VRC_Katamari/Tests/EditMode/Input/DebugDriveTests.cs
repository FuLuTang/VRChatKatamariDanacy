using NUnit.Framework;
using VRCKatamari.Input;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the DEV-ONLY drive override's pure math: axis clamping and the
    /// auto-expiry test <see cref="DesktopMoveInput"/> polls in Update.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Expectations are hand calculations against the two documented contracts: the motor's
    /// own [-1, 1] input range, and a duration that starts counting when the override is
    /// switched on, with 0 meaning "until cleared". Nothing here builds a scene, a motor or a
    /// Play session - the behaviour's wiring (gates, pushes, log lines) is not reachable from
    /// EditMode, and is listed as unverified in Docs/Design/ControlIntent/DEBUG_DRIVE.md.
    /// </para>
    /// <para>
    /// There is one refused case per input that could hide an operator typo: a non-finite axis
    /// must be refused rather than clamped to a limit (the motor treats a refused frame as
    /// "keep the last axes", so a repaired NaN would steer), and a garbage timer must not stop
    /// drive in the middle of a run.
    /// </para>
    /// </remarks>
    public class DebugDriveTests
    {
        private const float Tolerance = 1e-5f;

        [Test]
        public void ClampAxesLeavesUnitRangeValuesUnchanged()
        {
            float x;
            float z;

            // Inside the range the value passes through untouched, signs included.
            Assert.IsTrue(DebugDriveMath.TryClampAxes(0.5f, -0.25f, out x, out z));
            Assert.AreEqual(0.5f, x, Tolerance);
            Assert.AreEqual(-0.25f, z, Tolerance);

            // Both limits are inside the range, not outside it.
            Assert.IsTrue(DebugDriveMath.TryClampAxes(1f, -1f, out x, out z));
            Assert.AreEqual(1f, x, Tolerance);
            Assert.AreEqual(-1f, z, Tolerance);
        }

        [Test]
        public void ClampAxesClampsBeyondUnitRangeWithSignSymmetry()
        {
            float x;
            float z;

            // 2.5 and -3 are one step past the limits, so both round to the wall they cross.
            Assert.IsTrue(DebugDriveMath.TryClampAxes(2.5f, -3f, out x, out z));
            Assert.AreEqual(1f, x, Tolerance);
            Assert.AreEqual(-1f, z, Tolerance);

            // The mirror case: the clamp is not one-sided.
            Assert.IsTrue(DebugDriveMath.TryClampAxes(-7f, 0.75f, out x, out z));
            Assert.AreEqual(-1f, x, Tolerance);
            Assert.AreEqual(0.75f, z, Tolerance);
        }

        [Test]
        public void ClampAxesRefusesNonFiniteAndWritesZeroOnEveryPath()
        {
            float x;
            float z;

            // NaN on either axis refuses the whole frame; the caller keeps its old axes.
            Assert.IsFalse(DebugDriveMath.TryClampAxes(float.NaN, 0.5f, out x, out z));
            Assert.AreEqual(0f, x, Tolerance);
            Assert.AreEqual(0f, z, Tolerance);

            Assert.IsFalse(DebugDriveMath.TryClampAxes(0.5f, float.NaN, out x, out z));
            Assert.AreEqual(0f, x, Tolerance);
            Assert.AreEqual(0f, z, Tolerance);

            // Both infinities are refused too: clamping them to +-1 would steer a ball.
            Assert.IsFalse(DebugDriveMath.TryClampAxes(float.PositiveInfinity, 0f, out x, out z));
            Assert.AreEqual(0f, x, Tolerance);
            Assert.AreEqual(0f, z, Tolerance);

            Assert.IsFalse(DebugDriveMath.TryClampAxes(0f, float.NegativeInfinity, out x, out z));
            Assert.AreEqual(0f, x, Tolerance);
            Assert.AreEqual(0f, z, Tolerance);
        }

        [Test]
        public void ZeroOrNegativeDurationNeverExpires()
        {
            // 0 is the shipped default ("until the operator clears it"), so even a huge
            // elapsed time is not an expiry.
            Assert.IsFalse(DebugDriveMath.HasExpired(10f, 1000000f, 0f));
            Assert.IsFalse(DebugDriveMath.HasExpired(10f, 1000000f, -5f));
        }

        [Test]
        public void ExpiryStartsAtTheDurationAndNotBefore()
        {
            // A 5 s window switched on at t=10 is still running at t=14.99, over at t=15.
            Assert.IsFalse(DebugDriveMath.HasExpired(10f, 14.99f, 5f));
            Assert.IsTrue(DebugDriveMath.HasExpired(10f, 15f, 5f));
            Assert.IsTrue(DebugDriveMath.HasExpired(10f, 15.01f, 5f));
        }

        [Test]
        public void ExpiryRefusesGarbageTimersAndFutureStarts()
        {
            // A NaN or infinite timer must not end a running acceptance drive.
            Assert.IsFalse(DebugDriveMath.HasExpired(float.NaN, 20f, 5f));
            Assert.IsFalse(DebugDriveMath.HasExpired(10f, float.PositiveInfinity, 5f));
            Assert.IsFalse(DebugDriveMath.HasExpired(10f, 20f, float.NaN));
            Assert.IsFalse(DebugDriveMath.HasExpired(10f, 20f, float.PositiveInfinity));

            // A start reading after now has a negative elapsed, which cannot have expired.
            Assert.IsFalse(DebugDriveMath.HasExpired(10f, 9f, 5f));
        }
    }
}
