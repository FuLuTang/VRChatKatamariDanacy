using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ball_ground_contact/test_ground_contact.py.
    /// Expected values are hand calculations or geometric invariants; none of the
    /// assertions re-runs the implementation formula. Lengths are metres in world
    /// space with +Y up, matching the Python model.
    /// </summary>
    public class GroundContactTests
    {
        private const float Tolerance = 1e-5f;

        // Convenience holder so the tests read like the Python result dataclass;
        // it is test-only and wraps a single TrySolve call.
        private sealed class ContactResult
        {
            public bool Accepted;
            public float SupportHeight;
            public Vector3 CenterPosition;
            public float CenterNormalCoordinate;
            public Vector3 PlaneAnchor;
            public float AppliedNormalCorrection;
            public float NormalSpeedPerturbation;
            public bool LiftClamped;
            public bool DropClamped;
        }

        private static ContactResult Solve(
            float coreRadius,
            Vector3[] localProxyPoints,
            Vector3 rawPlaneNormal,
            float rawPlaneOffset,
            Vector3 currentCenter,
            float maxLiftPerStep = 100f,
            float maxDropPerStep = 100f,
            float requestedNormalSpeedPerturbation = 0f,
            float maxNormalSpeedPerturbation = 100f,
            Quaternion? localToWorldRotation = null)
        {
            Quaternion rotation = localToWorldRotation.HasValue
                ? localToWorldRotation.Value
                : Quaternion.identity;
            ContactResult result = new ContactResult();
            result.Accepted = GroundContactMath.TrySolve(
                coreRadius,
                rotation,
                localProxyPoints,
                rawPlaneNormal,
                rawPlaneOffset,
                currentCenter,
                maxLiftPerStep,
                maxDropPerStep,
                requestedNormalSpeedPerturbation,
                maxNormalSpeedPerturbation,
                out result.SupportHeight,
                out result.CenterPosition,
                out result.CenterNormalCoordinate,
                out result.PlaneAnchor,
                out result.AppliedNormalCorrection,
                out result.NormalSpeedPerturbation,
                out result.LiftClamped,
                out result.DropClamped);
            return result;
        }

        [Test]
        public void EmptyProxyReducesToSphere()
        {
            // h = max(r) = 2; target coordinate = d + h = 2, so the center sits
            // 2 m above the plane through the origin.
            ContactResult result = Solve(2f, new Vector3[0], Vector3.up, 0f, Vector3.zero);
            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(2f, result.SupportHeight, Tolerance);
            AssertVector(new Vector3(0f, 2f, 0f), result.CenterPosition);
            Assert.AreEqual(2f, result.CenterNormalCoordinate, Tolerance);
            AssertVector(Vector3.zero, result.PlaneAnchor);
        }

        [Test]
        public void ProxyExtensionRaisesSupport()
        {
            // -dot(+Y, (0,-3.5,0)) = 3.5 > r = 2, so the rod end defines support.
            ContactResult result = Solve(2f, new[] { new Vector3(0f, -3.5f, 0f) },
                Vector3.up, 0f, Vector3.zero);
            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(3.5f, result.SupportHeight, Tolerance);
            AssertVector(new Vector3(0f, 3.5f, 0f), result.CenterPosition);
        }

        [Test]
        public void RelativeExtrusionShrinksAsCoreGrows()
        {
            // Same 3.5 m rod end: extrusion past the core is 3.5 - r = 2.5 at r = 1
            // and 0.5 at r = 3.
            ContactResult small = Solve(1f, new[] { new Vector3(0f, -3.5f, 0f) },
                Vector3.up, 0f, Vector3.zero);
            ContactResult large = Solve(3f, new[] { new Vector3(0f, -3.5f, 0f) },
                Vector3.up, 0f, Vector3.zero);
            Assert.AreEqual(2.5f, small.SupportHeight - 1f, Tolerance);
            Assert.AreEqual(0.5f, large.SupportHeight - 3f, Tolerance);
        }

        [Test]
        public void UpwardCorrectionIsCapped()
        {
            // Requested lift is 5 m (rod end at -5 m); only 1.25 m is allowed.
            ContactResult result = Solve(2f, new[] { new Vector3(0f, -5f, 0f) },
                Vector3.up, 0f, Vector3.zero, maxLiftPerStep: 1.25f);
            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(5f, result.SupportHeight, Tolerance);
            AssertVector(new Vector3(0f, 1.25f, 0f), result.CenterPosition);
            Assert.AreEqual(1.25f, result.AppliedNormalCorrection, Tolerance);
            Assert.IsTrue(result.LiftClamped);
            Assert.IsFalse(result.DropClamped);
        }

        [Test]
        public void DetachedSupportCanFallWithDropCap()
        {
            // Center is 3.5 m up while the geometry only supports 1 m, so the
            // requested -2.5 m drop is cut to -0.4 m.
            ContactResult result = Solve(1f, new Vector3[0], Vector3.up, 0f,
                new Vector3(2f, 3.5f, 4f), maxDropPerStep: 0.4f);
            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(1f, result.SupportHeight, Tolerance);
            AssertVector(new Vector3(2f, 3.1f, 4f), result.CenterPosition);
            Assert.AreEqual(-0.4f, result.AppliedNormalCorrection, Tolerance);
            Assert.IsTrue(result.DropClamped);
            Assert.IsFalse(result.LiftClamped);
        }

        [Test]
        public void DetachedSupportEventuallyReachesCurrentGeometry()
        {
            // With a 10 m drop cap the full -2.5 m correction applies and the
            // center lands exactly on the current 1 m support.
            ContactResult result = Solve(1f, new Vector3[0], Vector3.up, 0f,
                new Vector3(2f, 3.5f, 4f), maxDropPerStep: 10f);
            Assert.IsTrue(result.Accepted);
            AssertVector(new Vector3(2f, 1f, 4f), result.CenterPosition);
            Assert.IsFalse(result.DropClamped);
        }

        [Test]
        public void SpeedPerturbationIsSymmetricAndCapped()
        {
            ContactResult negative = Solve(2f, new Vector3[0], Vector3.up, 0f, Vector3.zero,
                requestedNormalSpeedPerturbation: -9f, maxNormalSpeedPerturbation: 2f);
            // The Python case only exercised the negative side; the symmetric cap
            // also has to hold for a positive request.
            ContactResult positive = Solve(2f, new Vector3[0], Vector3.up, 0f, Vector3.zero,
                requestedNormalSpeedPerturbation: 9f, maxNormalSpeedPerturbation: 2f);
            Assert.AreEqual(-2f, negative.NormalSpeedPerturbation, Tolerance);
            Assert.AreEqual(2f, positive.NormalSpeedPerturbation, Tolerance);
        }

        [Test]
        public void RawNormalAndPlaneOffsetAreNormalizedTogether()
        {
            // n_raw = 2*Y and d_raw = 4 normalize to Y and 2, so the anchor is
            // (0,2,0) and the 1 m sphere center is at 2 + 1 = 3 m.
            ContactResult result = Solve(1f, new Vector3[0], new Vector3(0f, 2f, 0f), 4f,
                Vector3.zero);
            Assert.IsTrue(result.Accepted);
            AssertVector(new Vector3(0f, 3f, 0f), result.CenterPosition);
            AssertVector(new Vector3(0f, 2f, 0f), result.PlaneAnchor);
            Assert.AreEqual(1f, result.SupportHeight, Tolerance);
        }

        [Test]
        public void SlopeCorrectionIsAlongNormalAndPreservesTangent()
        {
            // n = (1,1,0)/sqrt(2). The start (5,-5,7) has normal coordinate 0, so
            // the whole 4/sqrt(2) support becomes the correction; the tangential
            // part of the start stays put.
            Vector3 normal = new Vector3(1f, 1f, 0f);
            ContactResult result = Solve(0.5f, new[] { new Vector3(-2f, -2f, 0f) },
                normal, 0f, new Vector3(5f, -5f, 7f));
            float expectedSupport = 4f / Mathf.Sqrt(2f);
            Assert.IsTrue(result.Accepted);
            Assert.AreEqual(expectedSupport, result.SupportHeight, Tolerance);
            Assert.AreEqual(expectedSupport, result.CenterNormalCoordinate, Tolerance);
            Assert.AreEqual(10f, result.CenterPosition.x - result.CenterPosition.y, Tolerance);
            Assert.AreEqual(7f, result.CenterPosition.z, Tolerance);
            Assert.AreEqual(7f, result.CenterPosition.x, Tolerance);
            Assert.AreEqual(-3f, result.CenterPosition.y, Tolerance);
            AssertVector(Vector3.zero, result.PlaneAnchor);
        }

        [Test]
        public void PlaneAnchorIsNotClaimedAsProxyContactPoint()
        {
            // Same case as the Python test: the anchor is d*n = (0,2,0), the
            // plane's closest point to the origin. The API reports no contact
            // point, and the anchor stays at the origin's foot on the plane even
            // when the ball starts elsewhere (center (5,3,7) below).
            ContactResult flat = Solve(1f, new Vector3[0], new Vector3(0f, 2f, 0f), 4f,
                Vector3.zero);
            ContactResult shifted = Solve(1f, new Vector3[0], Vector3.up, 0f,
                new Vector3(5f, 3f, 7f));
            AssertVector(new Vector3(0f, 2f, 0f), flat.PlaneAnchor);
            AssertVector(Vector3.zero, shifted.PlaneAnchor);
            // Requested drop is 1 - 3 = -2 m, so the center settles at (5,1,7).
            AssertVector(new Vector3(5f, 1f, 7f), shifted.CenterPosition);
        }

        [Test]
        public void QuaternionDirectionProbeMapsLocalAxesToWorld()
        {
            // Unity 2022.3 direction probes. +90 deg about +Y takes local +Z to
            // world +X; the Python host probe produced the same +X, but this test
            // is now run against Unity's own Quaternion.
            Quaternion yaw = Quaternion.AngleAxis(90f, Vector3.up);
            AssertVector(Vector3.right, yaw * Vector3.forward);

            // +90 deg about +Z takes local -X to world -Y, so a point that is
            // horizontal under identity becomes a 2 m downward support point.
            ContactResult rotated = Solve(1f, new[] { new Vector3(-2f, 0f, 0f) },
                Vector3.up, 0f, Vector3.zero,
                localToWorldRotation: Quaternion.AngleAxis(90f, Vector3.forward));
            ContactResult unrotated = Solve(1f, new[] { new Vector3(-2f, 0f, 0f) },
                Vector3.up, 0f, Vector3.zero);
            Assert.AreEqual(2f, rotated.SupportHeight, Tolerance);
            Assert.AreEqual(1f, unrotated.SupportHeight, Tolerance);
        }

        [Test]
        public void InvalidRotationScaleAndNaNAreRejected()
        {
            // The matrix port rejected wrong shape, scale, reflection and NaN.
            // Shape and reflection cannot be expressed as a Quaternion at all;
            // scale and NaN remain reachable and must be rejected, not normalized.
            Assert.IsFalse(Solve(1f, new Vector3[0], Vector3.up, 0f, Vector3.zero,
                localToWorldRotation: new Quaternion(0f, 0f, 0f, 2f)).Accepted);
            Assert.IsFalse(Solve(1f, new Vector3[0], Vector3.up, 0f, Vector3.zero,
                localToWorldRotation: new Quaternion(0f, 0f, 0f, 1.001f)).Accepted);
            Assert.IsFalse(Solve(1f, new Vector3[0], Vector3.up, 0f, Vector3.zero,
                localToWorldRotation: new Quaternion(float.NaN, 0f, 0f, 1f)).Accepted);
        }

        [Test]
        public void BadGeometryAndNegativeCapsAreRejected()
        {
            // Zero plane normal, non-finite proxy point, negative drop cap, zero
            // quaternion (the Python rotation_matrix_from_quaternion((0,0,0,0))
            // case) and negative core radius all fail without committing output.
            Assert.IsFalse(Solve(2f, new Vector3[0], Vector3.zero, 0f, Vector3.zero).Accepted);
            Assert.IsFalse(Solve(2f, new[] { new Vector3(0f, float.PositiveInfinity, 0f) },
                Vector3.up, 0f, Vector3.zero).Accepted);
            Assert.IsFalse(Solve(2f, new Vector3[0], Vector3.up, 0f, Vector3.zero,
                maxDropPerStep: -0.1f).Accepted);
            Assert.IsFalse(Solve(2f, new Vector3[0], Vector3.up, 0f, Vector3.zero,
                localToWorldRotation: new Quaternion(0f, 0f, 0f, 0f)).Accepted);
            Assert.IsFalse(Solve(-1f, new Vector3[0], Vector3.up, 0f, Vector3.zero).Accepted);
            Assert.IsFalse(Solve(2f, null, Vector3.up, 0f, Vector3.zero).Accepted);
        }

        [Test]
        public void NonFiniteIntermediateIsRejected()
        {
            // The Python case used 1.1e308 doubles. The float equivalent is a
            // finite 3e38 component whose dot with the unit (1,1,1)/sqrt(3)
            // normal overflows to infinity; the call must fail, not clamp.
            ContactResult result = Solve(1f, new[] { new Vector3(3e38f, 3e38f, 3e38f) },
                new Vector3(1f, 1f, 1f), 0f, Vector3.zero);
            Assert.IsFalse(result.Accepted);
            Assert.AreEqual(0f, result.SupportHeight, Tolerance);
            AssertVector(Vector3.zero, result.CenterPosition);
        }

        private static void AssertVector(Vector3 expected, Vector3 actual)
        {
            Assert.AreEqual(expected.x, actual.x, Tolerance, "vector x");
            Assert.AreEqual(expected.y, actual.y, Tolerance, "vector y");
            Assert.AreEqual(expected.z, actual.z, Tolerance, "vector z");
        }
    }
}
