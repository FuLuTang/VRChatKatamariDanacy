using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Riding;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the landing candidates and the "can a person stand here" rule for leaving the
    /// ball. Positions are hand calculations: heading 0 faces +Z, heading 90 faces +X.
    /// </summary>
    public class RideExitTests
    {
        private const float Tolerance = 1e-4f;

        // Ball centre (2, 0.5, 3), radius 0.4: the surface is 0.4 away, the landing spot a
        // further 0.9, so 1.3 m from the centre; the probe starts 0.4 + 1.0 above the centre.
        private static readonly Vector3 Center = new Vector3(2f, 0.5f, 3f);

        [Test]
        public void HeadingZero_PutsCandidatesBehindRightLeftAndInFront()
        {
            Vector3 origin;
            float length;
            float ground;

            Assert.IsTrue(RideExitMath.TryExitProbe(Center, 0.4f, 0f, 0, out origin, out length,
                out ground));
            Assert.AreEqual(2f, origin.x, Tolerance);
            Assert.AreEqual(3f - 1.3f, origin.z, Tolerance);
            Assert.AreEqual(0.5f + 0.4f + 1.0f, origin.y, Tolerance);
            Assert.AreEqual(0.1f, ground, Tolerance);

            Assert.IsTrue(RideExitMath.TryExitProbe(Center, 0.4f, 0f, 1, out origin, out length,
                out ground));
            Assert.AreEqual(2f + 1.3f, origin.x, Tolerance);
            Assert.AreEqual(3f, origin.z, Tolerance);

            Assert.IsTrue(RideExitMath.TryExitProbe(Center, 0.4f, 0f, 2, out origin, out length,
                out ground));
            Assert.AreEqual(2f - 1.3f, origin.x, Tolerance);

            Assert.IsTrue(RideExitMath.TryExitProbe(Center, 0.4f, 0f, 3, out origin, out length,
                out ground));
            Assert.AreEqual(3f + 1.3f, origin.z, Tolerance);
        }

        [Test]
        public void HeadingNinety_TurnsTheCandidatesWithTheBall()
        {
            Vector3 origin;
            float length;
            float ground;
            // Facing +X: behind is -X, right is -Z (looking along +X with +Y up).
            Assert.IsTrue(RideExitMath.TryExitProbe(Center, 0.4f, 90f, 0, out origin,
                out length, out ground));
            Assert.AreEqual(2f - 1.3f, origin.x, Tolerance);
            Assert.AreEqual(3f, origin.z, Tolerance);
            Assert.IsTrue(RideExitMath.TryExitProbe(Center, 0.4f, 90f, 1, out origin,
                out length, out ground));
            Assert.AreEqual(2f, origin.x, Tolerance);
            Assert.AreEqual(3f - 1.3f, origin.z, Tolerance);
        }

        [Test]
        public void Probe_ReachesBelowTheExpectedGround()
        {
            Vector3 origin;
            float length;
            float ground;
            Assert.IsTrue(RideExitMath.TryExitProbe(Center, 0.4f, 0f, 0, out origin, out length,
                out ground));
            // From y 1.9 (centre 0.5 + radius 0.4 + lift 1.0) down to ground 0.1 is 1.8,
            // plus 2.5 below it.
            Assert.AreEqual(1.8f + 2.5f, length, Tolerance);
            Assert.AreEqual(ground - 2.5f, origin.y - length, Tolerance);
        }

        [Test]
        public void Probe_RefusesBadIndexAndBadInput()
        {
            Vector3 origin;
            float length;
            float ground;
            Assert.IsFalse(RideExitMath.TryExitProbe(Center, 0.4f, 0f, -1, out origin,
                out length, out ground));
            Assert.IsFalse(RideExitMath.TryExitProbe(Center, 0.4f, 0f,
                RideExitMath.CandidateCount, out origin, out length, out ground));
            Assert.IsFalse(RideExitMath.TryExitProbe(Center, -0.4f, 0f, 0, out origin,
                out length, out ground));
            Assert.IsFalse(RideExitMath.TryExitProbe(new Vector3(float.NaN, 0f, 0f), 0.4f, 0f,
                0, out origin, out length, out ground));
            Assert.IsFalse(RideExitMath.TryExitProbe(Center, 0.4f, float.NaN, 0, out origin,
                out length, out ground));
            Assert.AreEqual(0f, length);
        }

        [Test]
        public void Standable_AcceptsFlatGroundAtTheBallsRestingPlane()
        {
            Assert.IsTrue(RideExitMath.IsStandable(1f, 0.1f, 0.1f));
            // A 40 degree slope has normal.y = cos 40 = 0.766, still walkable.
            Assert.IsTrue(RideExitMath.IsStandable(0.766f, 0.1f, 0.1f));
            // A kerb 0.5 m above and a ditch 1.4 m below are still inside the limits.
            Assert.IsTrue(RideExitMath.IsStandable(1f, 0.6f, 0.1f));
            Assert.IsTrue(RideExitMath.IsStandable(1f, -1.3f, 0.1f));
        }

        [Test]
        public void Standable_RefusesWallsTablesAndPits()
        {
            // 60 degree slope: normal.y = 0.5.
            Assert.IsFalse(RideExitMath.IsStandable(0.5f, 0.1f, 0.1f));
            // A table top 0.7 m above the plane, and a pit 1.6 m below it.
            Assert.IsFalse(RideExitMath.IsStandable(1f, 0.8f, 0.1f));
            Assert.IsFalse(RideExitMath.IsStandable(1f, -1.6f, 0.1f));
            Assert.IsFalse(RideExitMath.IsStandable(float.NaN, 0.1f, 0.1f));
            Assert.IsFalse(RideExitMath.IsStandable(1f, float.NaN, 0.1f));
        }

        [Test]
        public void FacingYaw_PointsAtTheBall()
        {
            // From the origin to +Z is yaw 0, to +X is 90, to -X is 270, to -Z is 180.
            Assert.AreEqual(0f, RideExitMath.FacingYawDegrees(Vector3.zero,
                new Vector3(0f, 5f, 2f)), 1e-3f);
            Assert.AreEqual(90f, RideExitMath.FacingYawDegrees(Vector3.zero,
                new Vector3(2f, 0f, 0f)), 1e-3f);
            Assert.AreEqual(270f, RideExitMath.FacingYawDegrees(Vector3.zero,
                new Vector3(-2f, 0f, 0f)), 1e-3f);
            Assert.AreEqual(180f, RideExitMath.FacingYawDegrees(Vector3.zero,
                new Vector3(0f, 0f, -2f)), 1e-3f);
            // The same horizontal point has no direction.
            Assert.AreEqual(0f, RideExitMath.FacingYawDegrees(Vector3.zero,
                new Vector3(0f, 3f, 0f)), 1e-6f);
        }

        [Test]
        public void StandPose_LiftsTheGroundPointAndFacesTheBall()
        {
            Vector3 position;
            float yaw;
            // Ground point 1.3 m behind (-Z) the ball centre (2, 0.5, 3): the rider stands 5 cm
            // above it and looks along +Z at the ball.
            Assert.IsTrue(RideExitMath.TryStandPose(new Vector3(2f, 0.1f, 1.7f), Center,
                out position, out yaw));
            Assert.AreEqual(2f, position.x, Tolerance);
            Assert.AreEqual(0.1f + RideExitMath.StandLiftMeters, position.y, Tolerance);
            Assert.AreEqual(1.7f, position.z, Tolerance);
            Assert.AreEqual(0f, yaw, Tolerance);

            Assert.IsFalse(RideExitMath.TryStandPose(new Vector3(float.NaN, 0f, 0f), Center,
                out position, out yaw));
            Assert.AreEqual(Vector3.zero, position);
        }
    }
}
