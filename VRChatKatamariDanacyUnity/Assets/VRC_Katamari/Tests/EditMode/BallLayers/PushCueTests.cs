using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the push-cue contract: the marker sits ahead of the ball along the GIVEN yaw,
    /// floats just above the ball's top, grows with the drawn diameter, bobs vertically
    /// only, and rejects invalid input atomically.
    /// </summary>
    /// <remarks>
    /// Expected values are hand calculations from the documented model
    /// position = centre + forward(yaw) * (radius + gap) + up * (radius + hover + bob),
    /// gap = gapBase + diameter * gapPerDiameter,
    /// length = lengthBase + diameter * lengthPerDiameter (clamped).
    /// The component's committed PROVISIONAL defaults are written as literals below so a
    /// changed default fails a test instead of silently moving the expectation. The 0.30 m
    /// and 2.00 m balls are the sizes the cue is judged at: a start-size ball must not be
    /// covered, and a grown one must still read. Yaw is degrees about +Y, where 0 faces +Z
    /// and forward is (sin yaw, 0, cos yaw); lengths are metres, +Y up.
    /// </remarks>
    public class PushCueTests
    {
        private const float Tolerance = 1e-5f;

        // The component's committed defaults. If these change, every expectation below
        // must be recalculated - that is the point of keeping them here.
        private const float GapBase = 0.35f;
        private const float GapPerDiameter = 0.45f;
        private const float Hover = 0.08f;
        private const float LengthBase = 0.10f;
        private const float LengthPerDiameter = 0.40f;
        private const float MinLength = 0.05f;
        private const float MaxLength = 2.50f;
        private const float BobAmplitude = 0.025f;
        private const float BobHz = 0.25f;

        // Diameters the cue is judged at: the start size, the two acceptance sizes, the
        // measured worst case the follow rig was tuned at, and the growth ceiling.
        private static readonly float[] TestDiameters = { 0.10f, 0.30f, 0.72f, 1.43f, 2.00f };

        private static bool Evaluate(Vector3 center, float diameter, float yawDegrees,
            float phaseSeconds, out Vector3 position, out float outYaw, out float length,
            float gapBase = GapBase, float gapPerDiameter = GapPerDiameter,
            float hover = Hover, float lengthBase = LengthBase, float lengthPer = LengthPerDiameter,
            float minLength = MinLength, float maxLength = MaxLength,
            float bobAmp = BobAmplitude, float bobHz = BobHz)
        {
            return PushCueMath.TryEvaluate(center, diameter, yawDegrees, phaseSeconds,
                gapBase, gapPerDiameter, hover, lengthBase, lengthPer, minLength, maxLength,
                bobAmp, bobHz, out position, out outYaw, out length);
        }

        private static void AssertRejected(Vector3 center, float diameter, float yawDegrees,
            float phaseSeconds, float gapBase = GapBase, float gapPerDiameter = GapPerDiameter,
            float hover = Hover, float lengthBase = LengthBase, float lengthPer = LengthPerDiameter,
            float minLength = MinLength, float maxLength = MaxLength,
            float bobAmp = BobAmplitude, float bobHz = BobHz)
        {
            Vector3 position;
            float outYaw;
            float length;
            bool accepted = PushCueMath.TryEvaluate(center, diameter, yawDegrees, phaseSeconds,
                gapBase, gapPerDiameter, hover, lengthBase, lengthPer, minLength, maxLength,
                bobAmp, bobHz, out position, out outYaw, out length);
            Assert.IsFalse(accepted, "invalid input was accepted");
            Assert.AreEqual(Vector3.zero, position);
            Assert.AreEqual(0f, outYaw);
            Assert.AreEqual(0f, length);
        }

        [Test]
        public void CueSitsAheadAlongThePushYaw()
        {
            // A ball at (2, 0.15, -3), 0.30 m across. r = 0.15,
            // gap = 0.35 + 0.30 * 0.45 = 0.485, so the centre offset is 0.635 m.
            // Height above the centre is r + hover = 0.23 m; length is 0.10 + 0.12 = 0.22.
            Vector3 center = new Vector3(2f, 0.15f, -3f);
            Vector3[] expectedDirection = { new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 0f),
                new Vector3(0f, 0f, -1f), new Vector3(-1f, 0f, 0f) };
            float[] yawDegrees = { 0f, 90f, 180f, 270f };
            for (int i = 0; i < yawDegrees.Length; i++)
            {
                Vector3 position;
                float outYaw;
                float length;
                Assert.IsTrue(Evaluate(center, 0.30f, yawDegrees[i], 0f,
                    out position, out outYaw, out length));
                Assert.AreEqual(center.x + expectedDirection[i].x * 0.635f, position.x, Tolerance);
                Assert.AreEqual(center.z + expectedDirection[i].z * 0.635f, position.z, Tolerance);
                Assert.AreEqual(center.y + 0.23f, position.y, Tolerance);
                Assert.AreEqual(yawDegrees[i], outYaw, Tolerance);
                Assert.AreEqual(0.22f, length, Tolerance);
            }
        }

        [Test]
        public void CueHeightIsBallTopPlusHoverAndIgnoresYaw()
        {
            // Height is measured from the ball's TOP (centre + radius), not from the ground:
            // y = centre.y + radius + hover, whatever the yaw. A 0.30 m ball therefore floats
            // 0.23 m above its centre and 0.38 m above the ground when it rests on it.
            Vector3 center = new Vector3(-1.5f, 0.15f, 4f);
            float[] yawDegrees = { 0f, 37f, 90f, 180f, 271f, 359.9f };
            for (int i = 0; i < yawDegrees.Length; i++)
            {
                Vector3 position;
                float outYaw;
                float length;
                Assert.IsTrue(Evaluate(center, 0.30f, yawDegrees[i], 0f,
                    out position, out outYaw, out length));
                Assert.AreEqual(center.y + 0.15f + Hover, position.y, Tolerance);
            }
        }

        [Test]
        public void CommittedDefaultsAtTheSmallAndLargeBalls()
        {
            Vector3 center = new Vector3(0f, 0.15f, 0f);

            // 0.30 m ball: gap 0.485, offset 0.635, height 0.23, length 0.22.
            Vector3 smallPosition;
            float smallYaw;
            float smallLength;
            Assert.IsTrue(Evaluate(center, 0.30f, 0f, 0f,
                out smallPosition, out smallYaw, out smallLength));
            Assert.AreEqual(0.635f, smallPosition.z, Tolerance);
            Assert.AreEqual(0.23f, smallPosition.y - center.y, Tolerance);
            Assert.AreEqual(0.22f, smallLength, Tolerance);

            // 2.00 m ball: gap 1.25, offset 2.25, height 1.08, length 0.90.
            Vector3 largePosition;
            float largeYaw;
            float largeLength;
            Assert.IsTrue(Evaluate(center, 2.00f, 0f, 0f,
                out largePosition, out largeYaw, out largeLength));
            Assert.AreEqual(2.25f, largePosition.z, Tolerance);
            Assert.AreEqual(1.08f, largePosition.y - center.y, Tolerance);
            Assert.AreEqual(0.90f, largeLength, Tolerance);

            // The 0.22 m arrow is shorter than the 0.30 m ball and sits 0.485 m in front of
            // its surface; the 0.90 m arrow is 1.25 m in front of the 2.00 m ball's surface.
            Assert.Less(smallLength, 0.30f);
            Assert.Greater(largeLength, smallLength);
        }

        [Test]
        public void SpacingHeightAndLengthAreStrictlyMonotoneInDiameter()
        {
            float previousOffset = -1f;
            float previousLength = -1f;
            float previousHeight = -1f;
            for (int i = 0; i < TestDiameters.Length; i++)
            {
                float diameter = TestDiameters[i];
                Vector3 position;
                float outYaw;
                float length;
                Assert.IsTrue(Evaluate(new Vector3(0f, diameter * 0.5f, 0f), diameter,
                    123f, 0f, out position, out outYaw, out length));

                float height = position.y - diameter * 0.5f;
                float offset = Mathf.Sqrt(position.x * position.x + position.z * position.z);
                // The cue is measured from the ball's centre, and the ball is at the origin
                // in x/z here, so the horizontal distance is the whole offset.
                Assert.AreEqual(diameter * 0.5f + GapBase + diameter * GapPerDiameter,
                    offset, Tolerance);
                Assert.AreEqual(diameter * 0.5f + Hover, height, Tolerance);
                Assert.AreEqual(LengthBase + diameter * LengthPerDiameter, length, Tolerance);

                Assert.Greater(offset, previousOffset, "spacing must grow with the ball");
                Assert.Greater(length, previousLength, "length must grow with the ball");
                Assert.Greater(height, previousHeight, "the cue must rise with the ball");
                previousOffset = offset;
                previousLength = length;
                previousHeight = height;
            }
        }

        [Test]
        public void BobIsVerticalBoundedAndCentredOnTheBaseHeight()
        {
            // 0.25 Hz has a 4 s period: sin peaks at t = 1 s and troughs at t = 3 s.
            Vector3 center = new Vector3(3f, 0.36f, -2f);
            Vector3 rest;
            float restYaw;
            float restLength;
            Assert.IsTrue(Evaluate(center, 0.72f, 45f, 0f,
                out rest, out restYaw, out restLength));

            Vector3 peak;
            float peakYaw;
            float peakLength;
            Assert.IsTrue(Evaluate(center, 0.72f, 45f, 1f,
                out peak, out peakYaw, out peakLength));
            Assert.AreEqual(rest.x, peak.x, Tolerance);
            Assert.AreEqual(rest.z, peak.z, Tolerance);
            Assert.AreEqual(BobAmplitude, peak.y - rest.y, Tolerance);

            Vector3 trough;
            float troughYaw;
            float troughLength;
            Assert.IsTrue(Evaluate(center, 0.72f, 45f, 3f,
                out trough, out troughYaw, out troughLength));
            Assert.AreEqual(-BobAmplitude, trough.y - rest.y, Tolerance);

            // Horizontal pose and length never move with the phase.
            Assert.AreEqual(restLength, peakLength, Tolerance);
            Assert.AreEqual(restLength, troughLength, Tolerance);

            // Sweep two full periods: the bob stays inside its amplitude.
            for (int sample = 0; sample < 200; sample++)
            {
                float phase = sample * 0.04f;
                Vector3 position;
                float outYaw;
                float length;
                Assert.IsTrue(Evaluate(center, 0.72f, 45f, phase,
                    out position, out outYaw, out length));
                Assert.LessOrEqual(Mathf.Abs(position.y - rest.y), BobAmplitude + Tolerance);
            }
        }

        [Test]
        public void YawIsWrappedIntoTheCanonicalRange()
        {
            Vector3 center = new Vector3(0f, 0.15f, 0f);
            float[] inputs = { 370f, -10f, 720f, 359.999f, -400f };
            float[] expected = { 10f, 350f, 0f, 359.999f, 320f };
            for (int i = 0; i < inputs.Length; i++)
            {
                Vector3 position;
                float outYaw;
                float length;
                Assert.IsTrue(Evaluate(center, 0.30f, inputs[i], 0f,
                    out position, out outYaw, out length));
                Assert.AreEqual(expected[i], outYaw, Tolerance);
                Assert.GreaterOrEqual(outYaw, 0f);
                Assert.Less(outYaw, 360f);
            }
        }

        [Test]
        public void ZeroDiameterKeepsTheConstantGapAndHover()
        {
            // A motor that reports no drawn ball yet is still a legal pose: gap and hover
            // are the constant terms and the length falls back to its base.
            Vector3 position;
            float outYaw;
            float length;
            Assert.IsTrue(Evaluate(new Vector3(1f, 0f, 1f), 0f, 0f, 0f,
                out position, out outYaw, out length));
            Assert.AreEqual(1f, position.x, Tolerance);
            Assert.AreEqual(1f + GapBase, position.z, Tolerance);
            Assert.AreEqual(Hover, position.y, Tolerance);
            Assert.AreEqual(LengthBase, length, Tolerance);
        }

        [Test]
        public void LengthRailsClampExtremeDiameters()
        {
            // A deliberately linear length law (base 0, 1 per metre) with tight rails: the
            // law is clamped, never extrapolated, so a tiny ball still gets a visible arrow
            // and a huge one cannot paint the screen.
            Vector3 position;
            float outYaw;
            float length;

            Assert.IsTrue(Evaluate(Vector3.zero, 0.01f, 0f, 0f, out position, out outYaw,
                out length, GapBase, GapPerDiameter, Hover, 0f, 1f, 0.05f, 0.90f));
            Assert.AreEqual(0.05f, length, Tolerance);

            Assert.IsTrue(Evaluate(Vector3.zero, 5f, 0f, 0f, out position, out outYaw,
                out length, GapBase, GapPerDiameter, Hover, 0f, 1f, 0.05f, 0.90f));
            Assert.AreEqual(0.90f, length, Tolerance);

            Assert.IsTrue(Evaluate(Vector3.zero, 0.30f, 0f, 0f, out position, out outYaw,
                out length, GapBase, GapPerDiameter, Hover, 0f, 1f, 0.05f, 0.90f));
            Assert.AreEqual(0.30f, length, Tolerance);
        }

        [Test]
        public void InvalidInputIsRejectedAtomically()
        {
            Vector3 center = new Vector3(0f, 0.15f, 0f);

            // Non-finite geometry.
            AssertRejected(new Vector3(float.NaN, 0f, 0f), 0.30f, 0f, 0f);
            AssertRejected(new Vector3(0f, float.PositiveInfinity, 0f), 0.30f, 0f, 0f);
            AssertRejected(center, float.NaN, 0f, 0f);
            AssertRejected(center, float.NegativeInfinity, 0f, 0f);
            AssertRejected(center, 0.30f, float.NaN, 0f);
            AssertRejected(center, 0.30f, 0f, float.NaN);

            // Negative radius and negative tuning terms are data errors, not clamps.
            AssertRejected(center, -0.30f, 0f, 0f);
            AssertRejected(center, 0.30f, 0f, 0f, -GapBase);
            AssertRejected(center, 0.30f, 0f, 0f, GapBase, -GapPerDiameter);
            AssertRejected(center, 0.30f, 0f, 0f, GapBase, GapPerDiameter, -Hover);
            AssertRejected(center, 0.30f, 0f, 0f, GapBase, GapPerDiameter, Hover, -LengthBase);
            AssertRejected(center, 0.30f, 0f, 0f, GapBase, GapPerDiameter, Hover, LengthBase,
                -LengthPerDiameter);
            AssertRejected(center, 0.30f, 0f, 0f, GapBase, GapPerDiameter, Hover, LengthBase,
                LengthPerDiameter, MinLength, MaxLength, -BobAmplitude);
            AssertRejected(center, 0.30f, 0f, 0f, GapBase, GapPerDiameter, Hover, LengthBase,
                LengthPerDiameter, MinLength, MaxLength, BobAmplitude, -BobHz);

            // A length rail conflict, and a zero/inverted rail.
            AssertRejected(center, 0.30f, 0f, 0f, GapBase, GapPerDiameter, Hover, LengthBase,
                LengthPerDiameter, 0f, MaxLength);
            AssertRejected(center, 0.30f, 0f, 0f, GapBase, GapPerDiameter, Hover, LengthBase,
                LengthPerDiameter, MinLength, 0.01f);

            // A frequency so large the rate itself overflows is refused, not sampled.
            AssertRejected(center, 0.30f, 0f, 0f, GapBase, GapPerDiameter, Hover, LengthBase,
                LengthPerDiameter, MinLength, MaxLength, BobAmplitude, float.MaxValue);
        }

        [Test]
        public void DeterministicLongSequenceStaysFiniteAndOrdered()
        {
            // 1000 deterministic samples across the whole size range: the pose is always
            // finite, the yaw stays canonical, the length stays inside its rails, the cue
            // stays above the ball's bottom, and the spacing never shrinks as the ball grows.
            Vector3 center = new Vector3(-4f, 0.05f, 7f);
            float previousOffset = -1f;
            for (int i = 0; i < 1000; i++)
            {
                float t = i / 999f;
                float diameter = 0.05f + t * 2.15f;
                float yaw = i * 7f;
                float phase = i * 0.037f;
                Vector3 position;
                float outYaw;
                float length;
                Assert.IsTrue(Evaluate(center, diameter, yaw, phase,
                    out position, out outYaw, out length));
                Assert.IsTrue(IsFinite(position.x) && IsFinite(position.y) && IsFinite(position.z));
                Assert.IsTrue(IsFinite(outYaw) && IsFinite(length));
                Assert.GreaterOrEqual(outYaw, 0f);
                Assert.Less(outYaw, 360f);
                Assert.GreaterOrEqual(length, MinLength - Tolerance);
                Assert.LessOrEqual(length, MaxLength + Tolerance);

                float bottom = center.y - diameter * 0.5f;
                Assert.Greater(position.y, bottom,
                    "the cue must float above the ball's bottom, i.e. above the ground");

                float offset = Mathf.Sqrt(
                    (position.x - center.x) * (position.x - center.x) +
                    (position.z - center.z) * (position.z - center.z));
                Assert.AreEqual(diameter * 0.5f + GapBase + diameter * GapPerDiameter,
                    offset, 1e-4f);
                Assert.GreaterOrEqual(offset, previousOffset - 1e-5f,
                    "spacing must not shrink as the ball grows");
                previousOffset = offset;
            }
        }

        private static bool IsFinite(float value)
        {
            return value >= float.MinValue && value <= float.MaxValue;
        }
    }
}
