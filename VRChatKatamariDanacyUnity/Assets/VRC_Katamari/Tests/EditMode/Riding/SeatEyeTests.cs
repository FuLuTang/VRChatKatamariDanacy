using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Riding;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the seat-vs-eye contract: the follow frame gives the EYE point, the station
    /// carries the avatar's ROOT, so the seat offset is the measured head-above-seat vector
    /// negated in the rig frame. The numbers below are hand calculations from that model,
    /// not values read back out of the implementation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scenario is the defect being fixed: a seat placed ON the eye point, an avatar whose
    /// seated head sits 0.55 m above the seat, and therefore a real camera 0.55 m too high.
    /// Applying the offset must put the head back on the eye point exactly - the tests assert
    /// that closed loop (move the seat, put the head on it, compare with the eye point) rather
    /// than only comparing the offset with an expected vector.
    /// </para>
    /// <para>
    /// Only the static math class is touched, so a scene and a behaviour are not needed. The
    /// seat's rotation is taken to be the rig's rotation here; a station can be rotated
    /// relative to its rig, which changes only the diagnostic seat-frame number, never the
    /// offset, because the offset is computed from world positions.
    /// </para>
    /// </remarks>
    public class SeatEyeTests
    {
        private const float Tolerance = 1e-5f;

        // The behavior's committed defaults, written as literals so a changed default fails a
        // test instead of silently moving the expectation.
        private const float VerticalLimit = 1f;
        private const float HorizontalLimit = 0.35f;
        private const float FilterHz = 8f;

        // A seated head is roughly half a metre above the seat; 0.55 m matches the "viewpoint
        // too high" report and is the value every expectation below is built on.
        private const float SeatedEyeHeight = 0.55f;

        // Test-only stand-in for one scene: the caller-owned state the math refuses to hold.
        private sealed class Rig
        {
            public Vector3 EyePoint;
            public Quaternion Rotation;
            public Vector3 SeatLocalOffset;
            public Vector3 SeatToHead;

            public Vector3 SeatPosition()
            {
                return EyePoint + Rotation * SeatLocalOffset;
            }

            public Vector3 HeadPosition()
            {
                return SeatPosition() + SeatToHead;
            }

            public bool Sample(out Vector3 headInRig, out Vector3 headInSeat, out int reason)
            {
                return SeatEyeMath.TrySampleHeadOffset(HeadPosition(), SeatPosition(),
                    Rotation, Rotation, out headInRig, out headInSeat, out reason);
            }

            public bool Compute(Vector3 headInRig, Vector3 previousOffset, bool verticalOnly,
                float filterHz, float dtSeconds, bool resetToTarget,
                out Vector3 offset, out float uncompensated, out float residual, out int reason)
            {
                return SeatEyeMath.TryComputeSeatOffset(headInRig, previousOffset,
                    VerticalLimit, HorizontalLimit, verticalOnly, filterHz, dtSeconds,
                    resetToTarget, out offset, out uncompensated, out residual, out reason);
            }

            // One measured sample from a rig whose seat is where it is, then one exact move.
            public bool MeasureAndSolve(out Vector3 offset, out float eyeAboveSeat,
                out float uncompensated, out int reason)
            {
                Vector3 headInRig;
                Vector3 headInSeat;
                float residual;
                offset = Vector3.zero;
                eyeAboveSeat = 0f;
                uncompensated = 0f;
                if (!Sample(out headInRig, out headInSeat, out reason)) return false;
                eyeAboveSeat = headInSeat.y;
                if (!Compute(headInRig, SeatLocalOffset, false, FilterHz, 1f / 90f, true,
                    out offset, out uncompensated, out residual, out reason))
                {
                    return false;
                }
                return true;
            }

            // After the seat moves, the avatar is rigidly carried, so the head keeps its
            // offset from the seat. This is the number the fix is judged on.
            public Vector3 HeadPositionAfter(Vector3 offset)
            {
                return EyePoint + Rotation * offset + SeatToHead;
            }
        }

        private static Rig MakeRig()
        {
            Rig rig = new Rig();
            rig.EyePoint = new Vector3(2f, 1.5f, -3f);
            rig.Rotation = Quaternion.identity;
            rig.SeatLocalOffset = Vector3.zero; // the defect: the seat is ON the eye point
            rig.SeatToHead = new Vector3(0f, SeatedEyeHeight, 0f);
            return rig;
        }

        [Test]
        public void ASeatOnTheEyePointIsLoweredByTheSeatedEyeHeight()
        {
            Rig rig = MakeRig();
            Vector3 offset;
            float eyeAboveSeat;
            float uncompensated;
            int reason;

            Assert.IsTrue(rig.MeasureAndSolve(out offset, out eyeAboveSeat, out uncompensated,
                out reason), "reason=" + reason);

            // The seat drops by exactly the seated eye height; nothing is dropped by limits.
            Assert.AreEqual(0f, offset.x, Tolerance);
            Assert.AreEqual(-SeatedEyeHeight, offset.y, Tolerance);
            Assert.AreEqual(0f, offset.z, Tolerance);
            Assert.AreEqual(0f, uncompensated, Tolerance);

            // The number a play tester reads out of the log.
            Assert.AreEqual(SeatedEyeHeight, eyeAboveSeat, Tolerance);

            // The eye lands on the eye point: the whole point of the layer.
            Assert.AreEqual(0f, Vector3.Distance(rig.HeadPositionAfter(offset), rig.EyePoint),
                Tolerance);
        }

        [Test]
        public void TheOffsetIsTakenInTheRigFrameWhenTheRigIsTurned()
        {
            Rig rig = MakeRig();
            rig.Rotation = Quaternion.Euler(0f, 90f, 0f);
            // A head 0.30 m to the world +x of the seat: at yaw 90 that is the rig's own
            // +z (the rig's forward points at world +x), so the offset is (0, -h, -0.30).
            rig.SeatToHead = new Vector3(0.30f, SeatedEyeHeight, 0f);

            Vector3 headInRig;
            Vector3 headInSeat;
            int reason;
            Assert.IsTrue(rig.Sample(out headInRig, out headInSeat, out reason), "reason=" + reason);
            Assert.AreEqual(0f, headInRig.x, Tolerance);
            Assert.AreEqual(SeatedEyeHeight, headInRig.y, Tolerance);
            Assert.AreEqual(0.30f, headInRig.z, Tolerance);

            Vector3 offset;
            float eyeAboveSeat;
            float uncompensated;
            int computeReason;
            Assert.IsTrue(rig.MeasureAndSolve(out offset, out eyeAboveSeat, out uncompensated,
                out computeReason), "reason=" + computeReason);
            Assert.AreEqual(0f, offset.x, Tolerance);
            Assert.AreEqual(-SeatedEyeHeight, offset.y, Tolerance);
            Assert.AreEqual(-0.30f, offset.z, Tolerance);
            Assert.AreEqual(0f, Vector3.Distance(rig.HeadPositionAfter(offset), rig.EyePoint),
                Tolerance);
        }

        [Test]
        public void VerticalOnlyLeavesTheMeasuredHorizontalErrorAlone()
        {
            Rig rig = MakeRig();
            rig.SeatToHead = new Vector3(0.20f, SeatedEyeHeight, 0.10f);

            Vector3 headInRig;
            Vector3 headInSeat;
            int reason;
            Assert.IsTrue(rig.Sample(out headInRig, out headInSeat, out reason), "reason=" + reason);

            Vector3 offset;
            float uncompensated;
            float residual;
            Assert.IsTrue(rig.Compute(headInRig, rig.SeatLocalOffset, true, FilterHz, 1f / 90f,
                true, out offset, out uncompensated, out residual, out reason), "reason=" + reason);

            Assert.AreEqual(0f, offset.x, Tolerance);
            Assert.AreEqual(-SeatedEyeHeight, offset.y, Tolerance);
            Assert.AreEqual(0f, offset.z, Tolerance);

            // The dropped horizontal part is reported, not hidden: 0.20 / 0.10 m is 0.2236 m.
            float expected = Mathf.Sqrt(0.20f * 0.20f + 0.10f * 0.10f);
            Assert.AreEqual(expected, uncompensated, Tolerance);
            Assert.AreEqual(0f, residual, Tolerance);
        }

        [Test]
        public void TheVerticalLimitBoundsTheCorrectionAndReportsWhatIsLeft()
        {
            Rig rig = MakeRig();
            // A head 1.40 m above the seat is past the guard, so the guard must bite.
            rig.SeatToHead = new Vector3(0f, 1.40f, 0f);

            Vector3 offset;
            float eyeAboveSeat;
            float uncompensated;
            int reason;
            Assert.IsTrue(rig.MeasureAndSolve(out offset, out eyeAboveSeat, out uncompensated,
                out reason), "reason=" + reason);

            Assert.AreEqual(-VerticalLimit, offset.y, Tolerance);
            Assert.AreEqual(1.40f - VerticalLimit, uncompensated, Tolerance);

            // What is left is a real eye error, and it is the only error: the seat moved by
            // the limit and the head is exactly that much above the eye point.
            Assert.AreEqual(uncompensated,
                Vector3.Distance(rig.HeadPositionAfter(offset), rig.EyePoint), Tolerance);
        }

        [Test]
        public void TheHorizontalLimitScalesTheOffsetInsteadOfClippingIt()
        {
            Rig rig = MakeRig();
            // Horizontal head error 0.30 / 0.40 m = 0.50 m, over the 0.35 m guard.
            rig.SeatToHead = new Vector3(0.30f, SeatedEyeHeight, 0.40f);

            Vector3 offset;
            float eyeAboveSeat;
            float uncompensated;
            int reason;
            Assert.IsTrue(rig.MeasureAndSolve(out offset, out eyeAboveSeat, out uncompensated,
                out reason), "reason=" + reason);

            // Direction preserved, length clamped to the guard: 0.35 * (0.30, 0.40) / 0.50.
            Assert.AreEqual(-0.21f, offset.x, Tolerance);
            Assert.AreEqual(-SeatedEyeHeight, offset.y, Tolerance);
            Assert.AreEqual(-0.28f, offset.z, Tolerance);
            Assert.AreEqual(0.15f, uncompensated, Tolerance);
            Assert.AreEqual(uncompensated,
                Vector3.Distance(rig.HeadPositionAfter(offset), rig.EyePoint), Tolerance);
        }

        [Test]
        public void AFlatHorizontalGuardCompensatesNoHorizontalError()
        {
            Rig rig = MakeRig();
            rig.SeatToHead = new Vector3(0.30f, SeatedEyeHeight, 0f);

            Vector3 headInRig;
            Vector3 headInSeat;
            int reason;
            Assert.IsTrue(rig.Sample(out headInRig, out headInSeat, out reason), "reason=" + reason);

            Vector3 offset;
            float uncompensated;
            float residual;
            Assert.IsTrue(SeatEyeMath.TryComputeSeatOffset(headInRig, rig.SeatLocalOffset,
                VerticalLimit, 0f, false, FilterHz, 1f / 90f, true, out offset,
                out uncompensated, out residual, out reason), "reason=" + reason);

            Assert.AreEqual(0f, offset.x, Tolerance);
            Assert.AreEqual(-SeatedEyeHeight, offset.y, Tolerance);
            Assert.AreEqual(0f, offset.z, Tolerance);
            Assert.AreEqual(0.30f, uncompensated, Tolerance);
        }

        [Test]
        public void TheGlideUsesTheDocumentedAlphaAndNeverOvershoots()
        {
            // alpha = 1 - exp(-2*pi*8*(1/90)) = 0.42794...
            float alpha = 1f - Mathf.Exp(-2f * Mathf.PI * FilterHz * (1f / 90f));
            Assert.AreEqual(0.4279f, alpha, 1e-4f);

            Rig rig = MakeRig();
            Vector3 headInRig;
            Vector3 headInSeat;
            int reason;
            Assert.IsTrue(rig.Sample(out headInRig, out headInSeat, out reason), "reason=" + reason);

            Vector3 offset = rig.SeatLocalOffset;
            float previousDistance = SeatedEyeHeight;
            for (int step = 0; step < 60; step++)
            {
                Vector3 next;
                float uncompensated;
                float residual;
                Assert.IsTrue(rig.Compute(headInRig, offset, false, FilterHz, 1f / 90f, false,
                    out next, out uncompensated, out residual, out reason), "reason=" + reason);
                offset = next;

                float distance = Mathf.Abs(offset.y + SeatedEyeHeight);
                Assert.LessOrEqual(distance, previousDistance + Tolerance,
                    "the glide must not move away from its target");
                Assert.GreaterOrEqual(offset.y, -SeatedEyeHeight - Tolerance,
                    "the glide must not overshoot the target");
                previousDistance = distance;
            }

            Assert.AreEqual(-SeatedEyeHeight, offset.y, 0.001f);
        }

        [Test]
        public void TheFirstGlideStepIsTheTargetTimesAlpha()
        {
            Rig rig = MakeRig();
            Vector3 headInRig;
            Vector3 headInSeat;
            int reason;
            Assert.IsTrue(rig.Sample(out headInRig, out headInSeat, out reason), "reason=" + reason);

            float alpha = 1f - Mathf.Exp(-2f * Mathf.PI * FilterHz * (1f / 90f));
            Vector3 offset;
            float uncompensated;
            float residual;
            Assert.IsTrue(rig.Compute(headInRig, Vector3.zero, false, FilterHz, 1f / 90f, false,
                out offset, out uncompensated, out residual, out reason), "reason=" + reason);

            Assert.AreEqual(-SeatedEyeHeight * alpha, offset.y, Tolerance);
            // The residual reported is the distance still to glide, not an eye error.
            Assert.AreEqual(SeatedEyeHeight * (1f - alpha), residual, Tolerance);
        }

        [Test]
        public void ZeroHertzHoldsThePreviousOffset()
        {
            Rig rig = MakeRig();
            Vector3 headInRig;
            Vector3 headInSeat;
            int reason;
            Assert.IsTrue(rig.Sample(out headInRig, out headInSeat, out reason), "reason=" + reason);

            Vector3 previous = new Vector3(0.01f, -0.02f, 0.03f);
            Vector3 offset;
            float uncompensated;
            float residual;
            Assert.IsTrue(rig.Compute(headInRig, previous, false, 0f, 1f / 90f, false,
                out offset, out uncompensated, out residual, out reason), "reason=" + reason);

            Assert.AreEqual(previous.x, offset.x, Tolerance);
            Assert.AreEqual(previous.y, offset.y, Tolerance);
            Assert.AreEqual(previous.z, offset.z, Tolerance);
            Assert.AreEqual(Vector3.Distance(previous,
                new Vector3(0f, -SeatedEyeHeight, 0f)), residual, Tolerance);
        }

        [Test]
        public void AMeasurementIsAFixedPointEvenWithACompensationApplied()
        {
            // The measurement is taken with the seat wherever it is, and it must not depend on
            // that: after an avatar change the rider is already sitting on a moved seat, and
            // the re-measure has to return the same offset, not one that creeps every ride.
            Rig rig = MakeRig();
            Vector3 firstOffset;
            float eyeAboveSeat;
            float uncompensated;
            int reason;
            Assert.IsTrue(rig.MeasureAndSolve(out firstOffset, out eyeAboveSeat, out uncompensated,
                out reason), "reason=" + reason);

            rig.SeatLocalOffset = firstOffset;
            Assert.AreEqual(0f, Vector3.Distance(rig.HeadPosition(), rig.EyePoint), Tolerance);

            Vector3 secondOffset;
            Assert.IsTrue(rig.MeasureAndSolve(out secondOffset, out eyeAboveSeat, out uncompensated,
                out reason), "reason=" + reason);
            Assert.AreEqual(firstOffset.x, secondOffset.x, 1e-4f);
            Assert.AreEqual(firstOffset.y, secondOffset.y, 1e-4f);
            Assert.AreEqual(firstOffset.z, secondOffset.z, 1e-4f);
        }

        [Test]
        public void AnAuthoredSeatOffsetIsAbsorbedNotAddedTwice()
        {
            // A scene that places the seat 0.20 m behind the eye point: the measured head is
            // 0.55 m above that seat, so the ABSOLUTE offset is still "0.55 m below the eye
            // point" - not the authored (0, 0, -0.20) plus another -0.55 in z.
            Rig rig = MakeRig();
            rig.SeatLocalOffset = new Vector3(0f, 0f, -0.20f);
            rig.SeatToHead = new Vector3(0f, SeatedEyeHeight, 0f);

            Vector3 offset;
            float eyeAboveSeat;
            float uncompensated;
            int reason;
            Assert.IsTrue(rig.MeasureAndSolve(out offset, out eyeAboveSeat, out uncompensated,
                out reason), "reason=" + reason);

            Assert.AreEqual(0f, offset.x, Tolerance);
            Assert.AreEqual(-SeatedEyeHeight, offset.y, Tolerance);
            Assert.AreEqual(0f, offset.z, Tolerance);
            Assert.AreEqual(0f, Vector3.Distance(rig.HeadPositionAfter(offset), rig.EyePoint),
                Tolerance);
        }

        [Test]
        public void TheMeanIsOneFixedSampleOfTheSum()
        {
            Vector3 sample = new Vector3(0.02f, -0.41f, 0.07f);
            Vector3 sum = sample * 4f;
            Vector3 mean;
            int reason;
            Assert.IsTrue(SeatEyeMath.TryMeanHeadOffset(sum, 4, out mean, out reason),
                "reason=" + reason);
            Assert.AreEqual(sample.x, mean.x, Tolerance);
            Assert.AreEqual(sample.y, mean.y, Tolerance);
            Assert.AreEqual(sample.z, mean.z, Tolerance);
        }

        [Test]
        public void RefusalsNameACauseAndChangeNothing()
        {
            Vector3 headInRig;
            Vector3 headInSeat;
            int reason;

            // 1: a non-finite head datum.
            Assert.IsFalse(SeatEyeMath.TrySampleHeadOffset(
                new Vector3(float.NaN, 1f, 0f), Vector3.zero, Quaternion.identity,
                Quaternion.identity, out headInRig, out headInSeat, out reason));
            Assert.AreEqual(SeatEyeMath.ReasonNotFinite, reason);
            Assert.AreEqual(Vector3.zero, headInRig);

            // 2: a rotation that cannot be inverted.
            Assert.IsFalse(SeatEyeMath.TrySampleHeadOffset(
                new Vector3(0f, 1f, 0f), Vector3.zero, new Quaternion(0f, 0f, 0f, 0f),
                Quaternion.identity, out headInRig, out headInSeat, out reason));
            Assert.AreEqual(SeatEyeMath.ReasonBadRotation, reason);

            // 5: nothing to average.
            Vector3 mean;
            Assert.IsFalse(SeatEyeMath.TryMeanHeadOffset(Vector3.zero, 0, out mean, out reason));
            Assert.AreEqual(SeatEyeMath.ReasonNoSamples, reason);

            Vector3 offset;
            float uncompensated;
            float residual;

            // 3: an unusable filter input.
            Vector3 previous = new Vector3(1f, 2f, 3f);
            Assert.IsFalse(SeatEyeMath.TryComputeSeatOffset(Vector3.zero, previous,
                VerticalLimit, HorizontalLimit, false, float.NaN, 1f / 90f, false,
                out offset, out uncompensated, out residual, out reason));
            Assert.AreEqual(SeatEyeMath.ReasonBadFilter, reason);

            // A negative time step is refusable the same way.
            Assert.IsFalse(SeatEyeMath.TryComputeSeatOffset(Vector3.zero, previous,
                VerticalLimit, HorizontalLimit, false, FilterHz, -1f, false,
                out offset, out uncompensated, out residual, out reason));
            Assert.AreEqual(SeatEyeMath.ReasonBadFilter, reason);

            // 4: an unusable limit.
            Assert.IsFalse(SeatEyeMath.TryComputeSeatOffset(Vector3.zero, previous,
                -1f, HorizontalLimit, false, FilterHz, 1f / 90f, false,
                out offset, out uncompensated, out residual, out reason));
            Assert.AreEqual(SeatEyeMath.ReasonBadLimit, reason);

            // 1 again, on the caller-owned previous offset: a refused frame writes nothing, so
            // the caller's own offset comes back unchanged.
            Assert.IsFalse(SeatEyeMath.TryComputeSeatOffset(Vector3.zero,
                new Vector3(float.PositiveInfinity, 0f, 0f), VerticalLimit, HorizontalLimit,
                false, FilterHz, 1f / 90f, false,
                out offset, out uncompensated, out residual, out reason));
            Assert.AreEqual(SeatEyeMath.ReasonNotFinite, reason);
            // The refused call handed the caller's own (illegal) offset straight back.
            Assert.IsTrue(offset.x > 1e30f);
        }
    }
}
