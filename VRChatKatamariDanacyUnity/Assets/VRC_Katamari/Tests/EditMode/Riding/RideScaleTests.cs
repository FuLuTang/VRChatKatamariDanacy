using NUnit.Framework;
using VRCKatamari.Riding;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the rider-size rules: the seated waist is level with the top of the ball at every
    /// ball size, the eye height that gives it is proportional to the diameter, requests are
    /// clamped to what VRChat supports, and the smoothing and debounce behave. Hand values: with
    /// the eye <c>up = 1.779558 r</c> above the centre (sin 18 / sin 10), the waist ratio 0.3125 and
    /// the seated-eye ratio 0.4878, the seated eye above the seat is
    /// <c>m = (up - r) / 0.6875 = 1.133902 r</c> and the eye height <c>E = m / 0.4878 =
    /// 1.16225 D</c>.
    /// </summary>
    public class RideScaleTests
    {
        private const float Ratio = RideScaleMath.SeatedEyeToEyeHeightRatio;

        // D, expected E (hand: 1.16225 D), seat height over the ground (hand: (up - m + r), 0.8228 D).
        [TestCase(0.222f, 0.2580f, 0.1827f)]
        [TestCase(0.5f, 0.5811f, 0.4114f)]
        [TestCase(0.8f, 0.9298f, 0.6583f)]
        [TestCase(2f, 2.3245f, 1.6457f)]
        [TestCase(20f, 23.245f, 16.457f)]
        public void EyeHeight_IsProportionalToTheDiameter(float diameter, float expectedEye,
            float expectedSeatHeight)
        {
            float eye;
            bool clamped;
            Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(diameter, Ratio, out eye,
                out clamped));
            Assert.IsFalse(clamped);
            Assert.AreEqual(expectedEye, eye, expectedEye * 1e-3f);

            float radius = diameter * 0.5f;
            float up;
            float back;
            Assert.IsTrue(RideViewMath.TryEyeOffset(radius, out up, out back));
            float eyeAboveSeat = Ratio * eye;
            // The waist (0.3125 of the seated eye above the seat) is level with the ball's top:
            // exactly one diameter above the ball's lowest point.
            float waist = RideScaleMath.WaistAboveBallBottom(radius, up, eyeAboveSeat);
            Assert.AreEqual(diameter, waist, diameter * 1e-3f);
            // The seat floats above the ground by 0.8228 D.
            float seatHeight = radius + up - eyeAboveSeat;
            Assert.AreEqual(expectedSeatHeight, seatHeight, expectedSeatHeight * 1e-3f);
            Assert.Greater(seatHeight, 0f);
        }

        [Test]
        public void EyeAboveSeat_HandValueAtUnitRadius()
        {
            Assert.AreEqual(1.133902f, RideScaleMath.EyeAboveSeatForRadius(1f), 1e-4f);
            Assert.AreEqual(0f, RideScaleMath.EyeAboveSeatForRadius(float.NaN));
            Assert.AreEqual(0f, RideScaleMath.EyeAboveSeatForRadius(-1f));
        }

        [Test]
        public void EyeHeight_IsClampedToThePlatformRange()
        {
            float eye;
            bool clamped;
            // 0.1 / 1.16225 = 0.086 m is the smallest ball the platform can match.
            Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(0.05f, Ratio, out eye,
                out clamped));
            Assert.AreEqual(0.1f, eye, 1e-6f);
            Assert.IsTrue(clamped);
            Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(0.09f, Ratio, out eye,
                out clamped));
            Assert.IsFalse(clamped);
            // The 20 m ball cap needs 23 m, a quarter of the 100 m ceiling.
            Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(200f, Ratio, out eye,
                out clamped));
            Assert.AreEqual(100f, eye, 1e-4f);
            Assert.IsTrue(clamped);
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        public void EyeHeight_RefusesBadDiameters(float diameter)
        {
            float eye;
            bool clamped;
            Assert.IsFalse(RideScaleMath.TryEyeHeightForDiameter(diameter, Ratio, out eye,
                out clamped));
            Assert.AreEqual(0f, eye);
        }

        [Test]
        public void EyeHeight_FollowsAMeasuredSeatedEyeRatio()
        {
            // An avatar whose seated head is 0.7 of its eye height needs a smaller avatar for the
            // same seated eye above the seat: E = m / ratio = 1.133902 r / 0.7 at r = 1.
            float eye;
            bool clamped;
            Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(2f, 0.7f, out eye, out clamped));
            Assert.AreEqual(1.133902f / 0.7f, eye, 1e-3f);
            Assert.IsFalse(RideScaleMath.TryEyeHeightForDiameter(2f, 0f, out eye, out clamped));
        }

        [Test]
        public void SeatedEyeRatio_FallsBackToNominalOutsideTheBand()
        {
            Assert.AreEqual(0.7f, RideScaleMath.ResolveSeatedEyeRatio(0.7f), 1e-6f);
            Assert.AreEqual(RideScaleMath.SeatedEyeToEyeHeightRatio,
                RideScaleMath.ResolveSeatedEyeRatio(0f), 1e-6f);
            Assert.AreEqual(RideScaleMath.SeatedEyeToEyeHeightRatio,
                RideScaleMath.ResolveSeatedEyeRatio(1.4f), 1e-6f);
            Assert.AreEqual(RideScaleMath.SeatedEyeToEyeHeightRatio,
                RideScaleMath.ResolveSeatedEyeRatio(float.NaN), 1e-6f);
        }

        [Test]
        public void WaistReferenceIsTheTopOfTheBall()
        {
            // The scene's one switch: 1 is the top, 0.5 the centre.
            Assert.AreEqual(1f, RideScaleMath.WaistBallHeightFraction);
        }

        [TestCase(0.178f, 0.178f)]
        [TestCase(0.05f, 0.1f)]
        [TestCase(500f, 100f)]
        public void Target_IsClampedToTheSupportedRange(float requested, float expected)
        {
            float target;
            Assert.IsTrue(RideScaleMath.TryResolveTarget(requested, out target));
            Assert.AreEqual(expected, target, 1e-5f);
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void Target_ZeroOrBadIsRefused(float requested)
        {
            float target;
            Assert.IsFalse(RideScaleMath.TryResolveTarget(requested, out target));
            Assert.AreEqual(0f, target);
        }

        [Test]
        public void IsApplied_UsesAFiveFractionTolerance()
        {
            Assert.IsTrue(RideScaleMath.IsApplied(0.178f, 0.178f));
            Assert.IsTrue(RideScaleMath.IsApplied(0.185f, 0.178f));
            Assert.IsFalse(RideScaleMath.IsApplied(1.7f, 0.178f));
            Assert.IsFalse(RideScaleMath.IsApplied(0f, 0.178f));
            Assert.IsFalse(RideScaleMath.IsApplied(float.NaN, 0.178f));
            Assert.IsFalse(RideScaleMath.IsApplied(0.178f, 0f));
        }

        [Test]
        public void Smoothing_FollowsTheUsualLowPass()
        {
            // alpha = 1 - exp(-2 pi * 5 * 0.02) = 1 - exp(-0.62832) = 0.46651.
            Assert.AreEqual(1f + 0.4665f, RideScaleMath.SmoothEyeHeight(1f, 2f, 0.02f, 5f), 1e-3f);
            // Zero hertz or a zero step holds, a bad input returns the current value.
            Assert.AreEqual(1f, RideScaleMath.SmoothEyeHeight(1f, 2f, 0.02f, 0f));
            Assert.AreEqual(1f, RideScaleMath.SmoothEyeHeight(1f, 2f, 0f, 5f));
            Assert.AreEqual(1f, RideScaleMath.SmoothEyeHeight(1f, float.NaN, 0.02f, 5f));
            // It converges and never overshoots.
            float value = 1f;
            for (int i = 0; i < 100; i++) value = RideScaleMath.SmoothEyeHeight(value, 2f, 0.02f, 5f);
            Assert.AreEqual(2f, value, 1e-3f);
        }

        [Test]
        public void Debounce_SendsOnlyAHalfPercentStep()
        {
            const float Step = RideScaleMath.DefaultStepFraction;
            Assert.IsTrue(RideScaleMath.ShouldApplyEyeHeight(0f, 1f, Step));
            Assert.IsFalse(RideScaleMath.ShouldApplyEyeHeight(10f, 10.04f, Step));
            Assert.IsTrue(RideScaleMath.ShouldApplyEyeHeight(10f, 10.06f, Step));
            Assert.IsTrue(RideScaleMath.ShouldApplyEyeHeight(10f, 9.9f, Step));
            Assert.IsFalse(RideScaleMath.ShouldApplyEyeHeight(10f, float.NaN, Step));
            Assert.IsFalse(RideScaleMath.ShouldApplyEyeHeight(10f, 0f, Step));
        }

        [Test]
        public void Growth_FromTheStartingBallToTheCap_NeedsAboutNineHundredSteps()
        {
            // ln(20 / 0.222) / ln(1.005) = 4.50 / 0.004988 = 902 steps of half a percent for the
            // whole growth, so each send is a barely visible change.
            float sent = 0.258f;
            int steps = 0;
            float target = 23.245f;
            while (sent < target)
            {
                float next = sent * (1f + RideScaleMath.DefaultStepFraction * 1.0001f);
                Assert.IsTrue(RideScaleMath.ShouldApplyEyeHeight(sent, next,
                    RideScaleMath.DefaultStepFraction));
                sent = next;
                steps++;
            }
            Assert.That(steps, Is.InRange(880, 920));
        }

        [Test]
        public void RestoreTarget_KeepsARealHeightAndOtherwiseLeavesTheSizeAlone()
        {
            Assert.AreEqual(1.7f, RideScaleMath.RestoreTarget(1.7f), 1e-5f);
            Assert.AreEqual(0f, RideScaleMath.RestoreTarget(0f));
            Assert.AreEqual(0f, RideScaleMath.RestoreTarget(float.NaN));
        }
    }
}
