using NUnit.Framework;
using VRCKatamari.Riding;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the rider-size rules: the eye height that puts the measured hips on the top of the
    /// ball is proportional to the diameter and inversely proportional to kappa, requests are
    /// clamped to what VRChat supports, a size changed from outside is recovered, and the
    /// smoothing and the rate limit behave. Hand values: with the eye <c>up = 1.779558 r</c>
    /// above the centre (sin 18 / sin 10) the eye is <c>0.779558 r</c> above the ball's top, so
    /// the eye height is <c>E = 0.779558 r / kappa</c>.
    /// </summary>
    public class RideScaleTests
    {
        // D, kappa, expected E = 0.779558 * D / 2 / kappa.
        [TestCase(0.222f, 0.4f, 0.21633f)]
        [TestCase(0.5f, 0.35f, 0.55683f)]
        [TestCase(2f, 0.2f, 3.89779f)]
        [TestCase(2f, 0.35f, 2.22731f)]
        [TestCase(2f, 0.5f, 1.55912f)]
        [TestCase(20f, 0.35f, 22.2731f)]
        public void EyeHeight_IsTheEyeAboveTheBallTopOverKappa(float diameter, float kappa,
            float expectedEye)
        {
            float eye;
            bool clamped;
            Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(diameter, kappa, out eye,
                out clamped));
            Assert.IsFalse(clamped);
            Assert.AreEqual(expectedEye, eye, expectedEye * 1e-3f);

            // With the hips kappa * E below the eye, they are one diameter above the ball's lowest
            // point: the ball's top.
            float radius = diameter * 0.5f;
            float up;
            float back;
            Assert.IsTrue(RideViewMath.TryEyeOffset(radius, out up, out back));
            float waist = RideScaleMath.WaistAboveBallBottom(radius, up, kappa * eye);
            Assert.AreEqual(diameter, waist, diameter * 1e-3f);
        }

        [Test]
        public void EyeAboveWaist_HandValueAtUnitRadius()
        {
            Assert.AreEqual(0.779558f, RideScaleMath.EyeAboveWaistForRadius(1f), 1e-4f);
            Assert.AreEqual(0f, RideScaleMath.EyeAboveWaistForRadius(float.NaN));
            Assert.AreEqual(0f, RideScaleMath.EyeAboveWaistForRadius(-1f));
        }

        [Test]
        public void EyeHeight_IsLinearInTheDiameterAtAnyKappa()
        {
            float unit;
            float other;
            bool clamped;
            Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(1f, 0.3f, out unit, out clamped));
            foreach (float diameter in new[] { 0.222f, 0.5f, 0.8f, 2f, 20f })
            {
                Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(diameter, 0.3f, out other,
                    out clamped));
                Assert.AreEqual(unit * diameter, other, unit * diameter * 1e-4f);
            }
        }

        [Test]
        public void EyeHeight_IsClampedToThePlatformRange()
        {
            float eye;
            bool clamped;
            // kappa 0.35: 0.779558 * 0.025 / 0.35 = 0.0557 m, under the 0.1 m floor.
            Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(0.05f, 0.35f, out eye,
                out clamped));
            Assert.AreEqual(RideScaleMath.MinSupportedEyeHeightMeters, eye, 1e-6f);
            Assert.IsTrue(clamped);
            // 0.779558 * 100 / 0.35 = 222.7 m, over the 100 m ceiling.
            Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(200f, 0.35f, out eye,
                out clamped));
            Assert.AreEqual(RideScaleMath.MaxSupportedEyeHeightMeters, eye, 1e-4f);
            Assert.IsTrue(clamped);
            // Inside the range nothing is clamped.
            Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(20f, 0.35f, out eye, out clamped));
            Assert.IsFalse(clamped);
        }

        [TestCase(0f, 0.35f)]
        [TestCase(-1f, 0.35f)]
        [TestCase(float.NaN, 0.35f)]
        [TestCase(2f, 0f)]
        [TestCase(2f, -0.2f)]
        [TestCase(2f, float.NaN)]
        public void EyeHeight_RefusesBadInputs(float diameter, float kappa)
        {
            float eye;
            bool clamped;
            Assert.IsFalse(RideScaleMath.TryEyeHeightForDiameter(diameter, kappa, out eye,
                out clamped));
            Assert.AreEqual(0f, eye);
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
        public void ForeignEyeHeight_IsReappliedUntilItIsOursAgain()
        {
            // We sent 0.8; the player's own dial (or another avatar) makes it 1.7.
            Assert.AreEqual(RideScaleMath.EventOurs,
                RideScaleMath.ClassifyEyeHeightEvent(0.8f, 0.8f, 0));
            Assert.AreEqual(RideScaleMath.EventOurs,
                RideScaleMath.ClassifyEyeHeightEvent(0.81f, 0.8f, 2));
            Assert.AreEqual(RideScaleMath.EventReapply,
                RideScaleMath.ClassifyEyeHeightEvent(1.7f, 0.8f, 0));
            Assert.AreEqual(RideScaleMath.EventReapply,
                RideScaleMath.ClassifyEyeHeightEvent(1.7f, 0.8f, 2));
            // Not a number or not loaded: not ours either.
            Assert.AreEqual(RideScaleMath.EventReapply,
                RideScaleMath.ClassifyEyeHeightEvent(float.NaN, 0.8f, 0));
            Assert.AreEqual(RideScaleMath.EventReapply,
                RideScaleMath.ClassifyEyeHeightEvent(0f, 0.8f, 0));
            // The same again too many times in a row: stop fighting.
            Assert.AreEqual(RideScaleMath.EventGiveUp,
                RideScaleMath.ClassifyEyeHeightEvent(1.7f, 0.8f,
                    RideScaleMath.MaxConsecutiveReapply));

            // The whole exchange: the platform takes the re-applied size, the next event is ours.
            float platform = 1.7f;
            const float Sent = 0.8f;
            int reapplied = 0;
            for (int i = 0; i < 10; i++)
            {
                int kind = RideScaleMath.ClassifyEyeHeightEvent(platform, Sent, reapplied);
                if (kind == RideScaleMath.EventOurs) break;
                Assert.AreEqual(RideScaleMath.EventReapply, kind);
                reapplied++;
                platform = Sent;
            }
            Assert.AreEqual(1, reapplied);
            Assert.IsTrue(RideScaleMath.IsApplied(platform, Sent));
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
            for (int i = 0; i < 100; i++)
            {
                value = RideScaleMath.SmoothEyeHeight(value, 2f, 0.02f, 5f);
                Assert.LessOrEqual(value, 2f);
            }
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
        public void RateLimit_NeedsBothTheStepAndTheInterval()
        {
            const float Step = RideScaleMath.DefaultStepFraction;
            const float Interval = RideScaleMath.DefaultMinIntervalSeconds;
            Assert.IsFalse(RideScaleMath.ShouldSendEyeHeight(10f, 11f, Step, 0.05f, Interval));
            Assert.IsTrue(RideScaleMath.ShouldSendEyeHeight(10f, 11f, Step, 0.1f, Interval));
            Assert.IsFalse(RideScaleMath.ShouldSendEyeHeight(10f, 10.01f, Step, 5f, Interval));
            Assert.IsFalse(RideScaleMath.ShouldSendEyeHeight(10f, 11f, Step, float.NaN, Interval));
            Assert.IsFalse(RideScaleMath.ShouldSendEyeHeight(10f, 11f, Step, 1f, -1f));
        }

        [Test]
        public void SmoothedFollow_SendsAtMostTenTimesASecondAndSettlesWithoutOvershoot()
        {
            // The ball jumps: the target eye height goes from 1 to 2 and stays. 60 frames a second
            // for 10 seconds, the station's own loop.
            const float Dt = 1f / 60f;
            float sent = 1f;
            float smooth = 1f;
            float lastSend = -10f;
            int sends = 0;
            float previous = sent;
            for (int frame = 0; frame < 600; frame++)
            {
                float time = frame * Dt;
                smooth = RideScaleMath.SmoothEyeHeight(smooth, 2f, Dt,
                    RideScaleMath.DefaultFilterHz);
                if (!RideScaleMath.ShouldSendEyeHeight(sent, smooth,
                    RideScaleMath.DefaultStepFraction, time - lastSend,
                    RideScaleMath.DefaultMinIntervalSeconds))
                {
                    continue;
                }
                sent = smooth;
                lastSend = time;
                sends++;
                Assert.GreaterOrEqual(sent, previous);
                Assert.LessOrEqual(sent, 2f);
                previous = sent;
            }
            Assert.LessOrEqual(sends, 101);
            Assert.AreEqual(2f, sent, 2f * RideScaleMath.DefaultStepFraction * 1.01f);
        }

        [Test]
        public void Growth_FromTheStartingBallToTheCap_NeedsAboutNineHundredSteps()
        {
            // The eye height is linear in the diameter, so the ratio is 20 / 0.222 = 90.09:
            // ln(90.09) / ln(1.005) = 4.50 / 0.004988 = 902 steps of half a percent for the whole
            // growth, so each send is a barely visible change.
            float start;
            float end;
            bool clamped;
            Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(0.222f, 0.4f, out start, out clamped));
            Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(20f, 0.4f, out end, out clamped));
            float sent = start;
            int steps = 0;
            while (sent < end)
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
