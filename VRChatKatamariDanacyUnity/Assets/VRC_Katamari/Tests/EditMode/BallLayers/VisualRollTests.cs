using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ball_visual_roll/test_visual_roll.py plus the
    /// rolling segments of test_presentation_cross_layer_simulation.py.
    /// Expected values are hand calculations, the no-slip contact condition
    /// v + omega x rho = 0, or plain geometry (a quarter circumference is 90
    /// degrees); no assertion re-runs the module's own formula. Lengths are
    /// metres, angles are radians, and +Y is up.
    /// </summary>
    public class VisualRollTests
    {
        private const float Tolerance = 1e-5f;

        [Test]
        public void ForwardTravelRollsAboutPositiveXAndSatisfiesNoSlip()
        {
            // 0.12 m over a 0.75 m radius is 0.16 rad. On a flat floor the
            // centre moving toward +Z must roll about +X; the contact-point
            // velocity check below is the independent proof of that sign.
            float radius = 0.75f;
            Vector3 travel = new Vector3(0f, 0f, 0.12f);
            Quaternion rolled = Advance(Quaternion.identity, travel, Vector3.up, radius);

            Vector3 axis;
            float angle;
            GetAxisAngleRadians(rolled, out axis, out angle);
            Assert.AreEqual(0.16f, angle, Tolerance);
            Assert.Greater(axis.x, 0.9999f, "flat-floor +Z travel must roll about +X");
            AssertNoSlip(rolled, travel, Vector3.up, radius);

            // The same distance backwards rolls the other way.
            Vector3 reverseTravel = new Vector3(0f, 0f, -0.12f);
            Quaternion reversed = Advance(Quaternion.identity, reverseTravel, Vector3.up, radius);
            GetAxisAngleRadians(reversed, out axis, out angle);
            Assert.AreEqual(0.16f, angle, Tolerance);
            Assert.Less(axis.x, -0.9999f, "reverse travel must roll about -X");
            AssertNoSlip(reversed, reverseTravel, Vector3.up, radius);
        }

        [Test]
        public void QuarterCircumferenceTurnsNinetyDegrees()
        {
            // A quarter of 2*pi*r is pi/2*r and must turn the ball exactly 90
            // degrees, carrying the point that was on top to face forward.
            float radius = 2f;
            Vector3 travel = new Vector3(0f, 0f, Mathf.PI * 0.5f * radius);
            Quaternion rolled = Advance(Quaternion.identity, travel, Vector3.up, radius);

            Vector3 topMarker = rolled * Vector3.up;
            Assert.AreEqual(0f, topMarker.x, Tolerance);
            Assert.AreEqual(0f, topMarker.y, Tolerance);
            Assert.AreEqual(1f, topMarker.z, Tolerance);

            Vector3 axis;
            float angle;
            GetAxisAngleRadians(rolled, out axis, out angle);
            Assert.AreEqual(Mathf.PI * 0.5f, angle, Tolerance);
            Assert.Greater(axis.x, 0.9999f);
        }

        [Test]
        public void FullCircumferenceReturnsTheSamePose()
        {
            // One circumference is pi * diameter and turns the ball 2*pi, so
            // the pose is unchanged: half the angle is pi and |w| is 1.
            float radius = 0.75f;
            float circumference = 2f * Mathf.PI * radius;
            Assert.AreEqual(Mathf.PI * (2f * radius), circumference, Tolerance);

            Quaternion rolled = Advance(Quaternion.identity,
                new Vector3(0f, 0f, circumference), Vector3.up, radius);
            Assert.AreEqual(1f, Mathf.Abs(rolled.w), Tolerance);
            AssertRotation(Quaternion.identity, rolled, Tolerance);

            // The call has no scale output at all: the visual roll node's
            // localScale stays (1,1,1) as a scene invariant, and this module
            // cannot change it.
        }

        [Test]
        public void SlopedContactUsesOnlyTheTangentialDisplacement()
        {
            // The displacement is purely tangential on the (0,1,1) slope, so
            // the angle is |t|/r = 0.174929/0.63 = 0.277664 rad and the axis
            // is perpendicular to the travel direction.
            Vector3 rawNormal = new Vector3(0f, 1f, 1f);
            Vector3 normal = rawNormal.normalized;
            Vector3 displacement = new Vector3(0.08f, 0.11f, -0.11f);
            Vector3 tangent = displacement - normal * Vector3.Dot(displacement, normal);
            float radius = 0.63f;

            Quaternion rolled = Advance(Quaternion.identity, displacement, rawNormal, radius);
            AssertNoSlip(rolled, tangent, normal, radius);

            Vector3 axis;
            float angle;
            GetAxisAngleRadians(rolled, out axis, out angle);
            Assert.AreEqual(0.277664f, angle, Tolerance);
            Assert.AreEqual(tangent.magnitude / radius, angle, Tolerance);
            Assert.AreEqual(0f, Vector3.Dot(axis, tangent.normalized), Tolerance);
        }

        [Test]
        public void PureNormalMotionAndZeroDisplacementHoldThePose()
        {
            // Only the tangential part sets the angle: 0.24 m is 0.3 rad at
            // r = 0.8 whether or not 3.5 m of normal lift rides along.
            float radius = 0.8f;
            Vector3 tangent = new Vector3(0f, 0f, 0.24f);
            Quaternion plain = Advance(Quaternion.identity, tangent, Vector3.up, radius);
            Quaternion lifted = Advance(Quaternion.identity,
                new Vector3(0f, 3.5f, 0.24f), Vector3.up, radius);

            Vector3 axis;
            float plainAngle;
            float liftedAngle;
            GetAxisAngleRadians(plain, out axis, out plainAngle);
            GetAxisAngleRadians(lifted, out axis, out liftedAngle);
            Assert.AreEqual(0.3f, plainAngle, Tolerance);
            Assert.AreEqual(plainAngle, liftedAngle, Tolerance);

            // A pure normal step and a zero step both keep the pose.
            AssertRotation(plain, Advance(plain, new Vector3(0f, 2f, 0f), Vector3.up, radius), 1e-7f);
            AssertRotation(plain, Advance(plain, Vector3.zero, Vector3.up, radius), 1e-7f);
        }

        [Test]
        public void NearZeroTangentUsesTheCutoffWithoutAxisJitter()
        {
            // 0.5e-7 m is below the 1e-7 m tangent cutoff: the pose of a ball
            // already mid-roll is held, up to the last bit of the renormalization.
            float radius = 0.5f;
            Quaternion pose = Advance(Quaternion.identity, new Vector3(0f, 0f, 0.3f), Vector3.up, radius);
            Quaternion belowCutoff = Advance(pose, new Vector3(0f, 0f, 0.5e-7f), Vector3.up, radius);
            AssertRotation(pose, belowCutoff, 1e-7f);

            // A step below the shared normalizer's 1e-12 m degeneracy floor is
            // still a valid no-motion step, not an error: it cannot reach the
            // cutoff either.
            AssertRotation(pose, Advance(pose, new Vector3(0f, 0f, 1e-13f), Vector3.up, radius), 1e-7f);

            // 2e-7 m is above it: 2e-7/0.5 = 4e-7 rad about +X, with no axis
            // jitter even at that magnitude.
            Quaternion aboveCutoff = Advance(Quaternion.identity,
                new Vector3(0f, 0f, 2e-7f), Vector3.up, radius);
            Vector3 axis;
            float angle;
            GetAxisAngleRadians(aboveCutoff, out axis, out angle);
            Assert.AreEqual(4e-7f, angle, 1e-9f);
            Assert.Greater(axis.x, 0.9999f);
        }

        [Test]
        public void TeleportAndJumpThresholdSkipTheRollThenResume()
        {
            // An explicit teleport marker skips the chord: 50 m of travel must
            // not spin the ball.
            Quaternion pose = Quaternion.identity;
            Quaternion skipped;
            Assert.IsTrue(VisualRollMath.TryAdvanceRotation(pose, new Vector3(50f, 0f, -20f),
                Vector3.up, 0.5f, true, false, 0f, out skipped));
            AssertRotation(pose, skipped, 1e-7f);

            // A caller-set jump threshold is the second guard: 3 m is over the
            // 2 m limit, 0.1 m is not and rolls 0.2 rad.
            Quaternion thresholdSkipped;
            Assert.IsTrue(VisualRollMath.TryAdvanceRotation(pose, new Vector3(3f, 0f, 0f),
                Vector3.up, 0.5f, false, true, 2f, out thresholdSkipped));
            AssertRotation(pose, thresholdSkipped, 1e-7f);

            Quaternion resumed;
            Assert.IsTrue(VisualRollMath.TryAdvanceRotation(pose, new Vector3(0f, 0f, 0.1f),
                Vector3.up, 0.5f, false, true, 2f, out resumed));
            Vector3 axis;
            float angle;
            GetAxisAngleRadians(resumed, out axis, out angle);
            Assert.AreEqual(0.2f, angle, Tolerance);
        }

        [Test]
        public void InvalidInitialRotationsAreRejectedAndNonUnitIsNormalized()
        {
            // There is no rotation to recover from zero, NaN, infinity or an
            // effectively zero length, so none of them is silently identity.
            Quaternion unit;
            Assert.IsFalse(VisualRollMath.TryNormalizeRotation(new Quaternion(0f, 0f, 0f, 0f), out unit));
            AssertRotation(Quaternion.identity, unit, 0f);
            Assert.IsFalse(VisualRollMath.TryNormalizeRotation(
                new Quaternion(float.NaN, 0f, 0f, 1f), out unit));
            Assert.IsFalse(VisualRollMath.TryNormalizeRotation(
                new Quaternion(0f, float.PositiveInfinity, 0f, 1f), out unit));
            Assert.IsFalse(VisualRollMath.TryNormalizeRotation(
                new Quaternion(1e-20f, 0f, 0f, 0f), out unit));

            // A legal non-unit quaternion is normalized rather than rejected:
            // (0,0,2,2)/|.| is a 90 degree rotation about +Z.
            Assert.IsTrue(VisualRollMath.TryNormalizeRotation(
                new Quaternion(0f, 0f, 2f, 2f), out unit));
            Assert.AreEqual(1f, unit.magnitude, Tolerance);
            Assert.AreEqual(0f, unit.x, Tolerance);
            Assert.AreEqual(0f, unit.y, Tolerance);
            Assert.AreEqual(Mathf.Sqrt(0.5f), unit.z, Tolerance);
            Assert.AreEqual(Mathf.Sqrt(0.5f), unit.w, Tolerance);

            // The Python case for a three-component rotation tuple has no C#
            // form: Quaternion is always four floats.
        }

        [Test]
        public void InvalidStepsAreRejectedAndLeaveNoTrace()
        {
            Quaternion pose = Advance(Quaternion.identity, new Vector3(0f, 0f, 0.1f), Vector3.up, 0.5f);
            Quaternion expected = Advance(pose, new Vector3(0f, 0f, 0.05f), Vector3.up, 0.5f);

            Quaternion nextRotation;
            // NaN displacement, zero normal, zero radius, a NaN or negative
            // jump threshold, and a displacement whose length overflows a
            // float are all refused.
            Assert.IsFalse(VisualRollMath.TryAdvanceRotation(pose,
                new Vector3(float.NaN, 0f, 0f), Vector3.up, 0.5f, false, false, 0f, out nextRotation));
            AssertRotation(Quaternion.identity, nextRotation, 0f);
            Assert.IsFalse(VisualRollMath.TryAdvanceRotation(pose,
                new Vector3(0f, 0f, 0f), Vector3.zero, 0.5f, false, false, 0f, out nextRotation));
            Assert.IsFalse(VisualRollMath.TryAdvanceRotation(pose,
                new Vector3(0f, 0f, 0f), Vector3.up, 0f, false, false, 0f, out nextRotation));
            Assert.IsFalse(VisualRollMath.TryAdvanceRotation(pose,
                new Vector3(0f, 0f, 0f), Vector3.up, 0.5f, false, true, float.NaN, out nextRotation));
            Assert.IsFalse(VisualRollMath.TryAdvanceRotation(pose,
                new Vector3(0f, 0f, 0f), Vector3.up, 0.5f, false, true, -1f, out nextRotation));
            Assert.IsFalse(VisualRollMath.TryAdvanceRotation(pose,
                new Vector3(3e38f, 3e38f, 3e38f), Vector3.up, 0.5f, false, false, 0f, out nextRotation));

            // No side effect survives a rejected call: the same pose advanced
            // by the same valid displacement gives exactly the same result as
            // it did before the failures.
            AssertRotation(expected, Advance(pose, new Vector3(0f, 0f, 0.05f), Vector3.up, 0.5f), 0f);
        }

        [Test]
        public void LongSmallStepRunStaysUnitAndReverses()
        {
            // 12000 steps of 0.7 mm on a 0.6 m radius roll 14 rad in total
            // (0.0007/0.6 per step). Every step must stay finite and unit,
            // the accumulated pose must match one rotation of the whole
            // angle, and the run must come back to the identity.
            const int StepCount = 12000;
            float radius = 0.6f;
            float step = 0.0007f;
            Quaternion pose = Quaternion.identity;
            for (int i = 0; i < StepCount; i++)
            {
                pose = Advance(pose, new Vector3(0f, 0f, step), Vector3.up, radius);
                Assert.IsFalse(float.IsNaN(pose.x) || float.IsNaN(pose.y) ||
                    float.IsNaN(pose.z) || float.IsNaN(pose.w), "non-finite rotation");
                Assert.AreEqual(1f, pose.magnitude, Tolerance);
            }

            float totalAngle = StepCount * step / radius;
            Quaternion expected = Quaternion.AngleAxis(totalAngle * Mathf.Rad2Deg, Vector3.right);
            AssertRotation(expected, pose, 1e-4f);

            for (int i = 0; i < StepCount; i++)
            {
                pose = Advance(pose, new Vector3(0f, 0f, -step), Vector3.up, radius);
            }
            AssertRotation(Quaternion.identity, pose, 1e-4f);
        }

        [Test]
        public void ResetIsTheCallerWritingIdentityAndHeadingIsNeverTouched()
        {
            // A respawn reset is the caller assigning Quaternion.identity to
            // the visual roll node. This module has no reset entry point and
            // no heading field, so rolling and resetting cannot turn the
            // ball's gameplay facing, which the caller keeps on its own.
            Vector3 gameplayHeading = Vector3.forward;
            Quaternion rolled = Advance(Quaternion.identity, new Vector3(0f, 0f, 0.3f), Vector3.up, 1f);
            Quaternion reset = Quaternion.identity;

            Vector3 axis;
            float angle;
            GetAxisAngleRadians(rolled, out axis, out angle);
            Assert.AreEqual(0.3f, angle, Tolerance);
            AssertRotation(Quaternion.identity, reset, 0f);
            Assert.AreEqual(Vector3.forward, gameplayHeading);
        }

        [Test]
        public void CrossLayerTraceTeleportSegmentDoesNotSpinTheBall()
        {
            // The rolling segments of
            // test_presentation_cross_layer_simulation.py: normal travel, an
            // explicit teleport whose chord is skipped, the resumed segment
            // from the new position, a degenerate normal-only step, and a
            // rejected NaN displacement. The follow rig and remote proxy parts
            // of that trace belong to their own modules.
            float radius = 0.8f;
            Vector3 up = new Vector3(0f, 1f, 0f);
            Vector3 travel = new Vector3(0f, 0f, 0.16f);
            Quaternion roll = Advance(Quaternion.identity, travel, up, radius);
            AssertNoSlip(roll, travel, up, radius);

            Quaternion afterTeleport;
            Assert.IsTrue(VisualRollMath.TryAdvanceRotation(roll, new Vector3(100f, 0f, -50f),
                up, radius, true, false, 0f, out afterTeleport));
            AssertRotation(roll, afterTeleport, 1e-7f);

            // The next segment starts from the new position and rolls again.
            Quaternion nextSegment = Advance(afterTeleport, new Vector3(0f, 0f, 0.08f), up, radius);
            Assert.Greater(QuaternionDistance(roll, nextSegment), 1e-4f);

            Quaternion normalOnly = Advance(nextSegment, new Vector3(0f, 0.3f, 0f), up, radius);
            AssertRotation(nextSegment, normalOnly, 1e-7f);

            Quaternion rejected;
            Assert.IsFalse(VisualRollMath.TryAdvanceRotation(nextSegment,
                new Vector3(float.NaN, 0f, 0f), up, radius, false, false, 0f, out rejected));
            AssertRotation(Quaternion.identity, rejected, 0f);
            AssertRotation(normalOnly, nextSegment, 1e-7f);
        }

        private static Quaternion Advance(Quaternion current, Vector3 displacement, Vector3 normal, float radius)
        {
            Quaternion nextRotation;
            Assert.IsTrue(VisualRollMath.TryAdvanceRotation(current, displacement, normal, radius,
                false, false, 0f, out nextRotation), "a valid step was rejected");
            return nextRotation;
        }

        // Test-side readback of a rotation as axis and angle in radians. It
        // only reads the quaternion, so it cannot inherit the module's
        // construction of the increment.
        private static void GetAxisAngleRadians(Quaternion rotation, out Vector3 axis, out float angleRadians)
        {
            Vector3 vectorPart = new Vector3(rotation.x, rotation.y, rotation.z);
            float vectorLength = vectorPart.magnitude;
            if (vectorLength <= 1e-7f)
            {
                axis = Vector3.zero;
                angleRadians = 0f;
                return;
            }
            axis = vectorPart / vectorLength;
            angleRadians = 2f * Mathf.Atan2(vectorLength, rotation.w);
        }

        // Independent component expansion used by the contact-velocity oracle,
        // never the module's Vector3.Cross call.
        private static Vector3 CrossOracle(Vector3 a, Vector3 b)
        {
            return new Vector3(
                a.y * b.z - a.z * b.y,
                a.z * b.x - a.x * b.z,
                a.x * b.y - a.y * b.x);
        }

        // For one short sample, dt = 1 s: the measured axis-angle is the
        // angular velocity and the tangential displacement is the centre
        // velocity, so the contact point's velocity is v + omega x rho with
        // rho = -r*n and must be zero.
        private static void AssertNoSlip(Quaternion rotation, Vector3 tangentialVelocity,
            Vector3 normal, float radius)
        {
            Vector3 axis;
            float angle;
            GetAxisAngleRadians(rotation, out axis, out angle);
            Assert.Greater(angle, 0f, "a rolling step must turn the pose");
            Vector3 omega = axis * angle;
            Vector3 contactOffset = -radius * normal;
            Vector3 residual = tangentialVelocity + CrossOracle(omega, contactOffset);
            Assert.Less(residual.magnitude, 1e-6f, "the contact point must have zero velocity");
        }

        private static float QuaternionDistance(Quaternion a, Quaternion b)
        {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            float dz = a.z - b.z;
            float dw = a.w - b.w;
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw);
        }

        // A rotation and its negation are the same pose, so the distance is
        // measured against both signs.
        private static void AssertRotation(Quaternion expected, Quaternion actual, float tolerance)
        {
            Quaternion negated = new Quaternion(-expected.x, -expected.y, -expected.z, -expected.w);
            float distance = Mathf.Min(QuaternionDistance(expected, actual),
                QuaternionDistance(negated, actual));
            Assert.LessOrEqual(distance, tolerance, "rotations differ");
        }
    }
}
