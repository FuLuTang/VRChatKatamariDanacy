using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the prop-to-OBB bake the v2 prop solids are built from
    /// (CONTACT_MODEL_V2 §4A.4/§5.2, stage S4b): the prop's LOCAL size column rotated once
    /// by its world rotation, never the world AABB rotated a second time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bug this file guards is the radial-proxy double rotation that
    /// <c>RadialProxyLocalBoundsTests</c> pinned for the shell: <c>propBounds</c> is already
    /// measured after the prop was turned, so rotating it again by the prop's rotation puts a
    /// yaw-90 chair's long axis on the wrong axis. The expected numbers below are hand
    /// calculations for a 1.6 x 0.35 x 0.6 prop turned 90 deg about +Y, not re-runs of the
    /// implementation.
    /// </para>
    /// <para>
    /// The fallback to the world AABB is stated on purpose: a scene without the
    /// <c>propLocalBounds</c> column must produce exactly the axis-aligned box the v1 path
    /// builds, so the two models cannot disagree about a prop when the column is missing.
    /// </para>
    /// </remarks>
    public class PropObbBakeTests
    {
        private const float Tolerance = 1e-4f;

        [Test]
        public void TurnedPropUsesItsOwnAxesNotTheWorldAabb()
        {
            // A 1.6 m long prop yawed 30 deg: its own +X is (cos30, 0, -sin30), so the half
            // extent along it stays 0.8 m. Rotating the WORLD AABB (0.6928 x ... wider, 1.6
            // long on Z) a second time would put 0.8 on a world axis instead.
            Vector3 position = new Vector3(3f, 0.2f, -2f);
            Quaternion rotation = Quaternion.AngleAxis(30f, Vector3.up);
            Vector3 axisX;
            Vector3 axisY;
            Vector3 axisZ;
            Vector3 halfExtents;
            int source = PropObbBakeMath.TryBakeProp(position, rotation, true,
                new Vector3(1.6f, 0.35f, 0.6f), new Vector3(1.6856f, 0.35f, 1.3196f),
                out axisX, out axisY, out axisZ, out halfExtents);

            Assert.AreEqual(PropObbBakeMath.SourceLocalAxes, source,
                "the local column is the true-OBB source");
            AssertVector(new Vector3(0.8f, 0.175f, 0.3f), halfExtents,
                "half extents are half the LOCAL sizes");
            float c = Mathf.Cos(30f * Mathf.Deg2Rad);
            float s2 = Mathf.Sin(30f * Mathf.Deg2Rad);
            AssertVector(new Vector3(c, 0f, -s2), axisX, "local +X, turned 30 deg about +Y");
            AssertVector(new Vector3(0f, 1f, 0f), axisY, "local +Y stays up");
            AssertVector(new Vector3(s2, 0f, c), axisZ, "local +Z, turned 30 deg about +Y");

            // The tightness claim: one corner of the world AABB lies outside the true OBB by
            // more than 5% on some axis, so the two are genuinely different shapes.
            // World AABB half extents for this box: |c|*0.8 + |s|*0.3 = 0.8428 on X and
            // |s|*0.8 + |c|*0.3 = 0.6598 on Z (hand calculation).
            Vector3 worldAabbCorner = new Vector3(position.x + 0.8428f, position.y + 0.175f,
                position.z + 0.6598f);
            Vector3 local = ObbContactMath.ToLocal(worldAabbCorner, position, axisX, axisY, axisZ);
            float worst = Mathf.Abs(local.x) / halfExtents.x;
            float alongY = Mathf.Abs(local.y) / halfExtents.y;
            float alongZ = Mathf.Abs(local.z) / halfExtents.z;
            if (alongY > worst) worst = alongY;
            if (alongZ > worst) worst = alongZ;
            Assert.Greater(worst, 1.05f,
                "the world AABB's corner is outside the true OBB (the boxes differ)");
        }

        [Test]
        public void UnturnedPropMatchesTheV1AxisAlignedBox()
        {
            // With no rotation the two columns agree, and the bake must reproduce the box
            // BlockingPropMath.TryBoxFromCentreAndExtents builds for the v1 sweep.
            Vector3 axisX;
            Vector3 axisY;
            Vector3 axisZ;
            Vector3 halfExtents;
            int source = PropObbBakeMath.TryBakeProp(new Vector3(1f, 2f, 3f), Quaternion.identity,
                true, new Vector3(2f, 1f, 0.5f), new Vector3(2f, 1f, 0.5f),
                out axisX, out axisY, out axisZ, out halfExtents);

            Assert.AreEqual(PropObbBakeMath.SourceLocalAxes, source, "identity rotation is still the local column");
            AssertVector(new Vector3(1f, 0.5f, 0.25f), halfExtents, "half of the full sizes");
            AssertVector(new Vector3(1f, 0f, 0f), axisX, "world X");
            AssertVector(new Vector3(0f, 1f, 0f), axisY, "world Y");
            AssertVector(new Vector3(0f, 0f, 1f), axisZ, "world Z");
        }

        [Test]
        public void MissingLocalRowFallsBackToTheWorldBox()
        {
            Vector3 axisX;
            Vector3 axisY;
            Vector3 axisZ;
            Vector3 halfExtents;
            int source = PropObbBakeMath.TryBakeProp(new Vector3(0f, 1f, 0f),
                Quaternion.AngleAxis(35f, Vector3.up), false, Vector3.zero,
                new Vector3(1.2f, 0.4f, 0.8f), out axisX, out axisY, out axisZ,
                out halfExtents);

            Assert.AreEqual(PropObbBakeMath.SourceWorldAabb, source,
                "no local column: the world AABB is the box, exactly as in v1");
            AssertVector(new Vector3(0.6f, 0.2f, 0.4f), halfExtents, "half of the world AABB");
            // The rotation is deliberately NOT applied: the AABB was measured after the turn.
            AssertVector(new Vector3(1f, 0f, 0f), axisX, "the fallback stays on the world axes");
        }

        [Test]
        public void AnUnusableLocalRowFallsBackAndANonFiniteOneIsRefused()
        {
            Vector3 axisX;
            Vector3 axisY;
            Vector3 axisZ;
            Vector3 halfExtents;

            // A negative extent is a data error, not a box; the world column still is one.
            int source = PropObbBakeMath.TryBakeProp(Vector3.zero, Quaternion.identity, true,
                new Vector3(-1f, 1f, 1f), new Vector3(1f, 1f, 1f), out axisX, out axisY,
                out axisZ, out halfExtents);
            Assert.AreEqual(PropObbBakeMath.SourceWorldAabb, source, "negative local row is skipped");

            // A non-finite position refuses both columns.
            source = PropObbBakeMath.TryBakeProp(new Vector3(float.NaN, 0f, 0f),
                Quaternion.identity, true, new Vector3(1f, 1f, 1f), new Vector3(1f, 1f, 1f),
                out axisX, out axisY, out axisZ, out halfExtents);
            Assert.AreEqual(PropObbBakeMath.SourceNone, source, "a NaN centre has no box");
            AssertVector(Vector3.zero, halfExtents, "a refusal writes zero, never half a box");

            // A zero-length quaternion is not a rotation, so the local column is unusable.
            source = PropObbBakeMath.TryBakeProp(Vector3.zero, new Quaternion(0f, 0f, 0f, 0f),
                true, new Vector3(1f, 1f, 1f), new Vector3(2f, 2f, 2f), out axisX, out axisY,
                out axisZ, out halfExtents);
            Assert.AreEqual(PropObbBakeMath.SourceWorldAabb, source, "a zero quaternion is not a rotation");
        }

        [Test]
        public void ZeroExtentRowsAreStillBoxes()
        {
            // The v1 box builder accepts a zero extent (BlockingPropMath documents a point
            // box as legal), so the v2 bake must not be stricter about the same data.
            Vector3 axisX;
            Vector3 axisY;
            Vector3 axisZ;
            Vector3 halfExtents;
            int source = PropObbBakeMath.TryBakeProp(Vector3.zero, Quaternion.identity, true,
                new Vector3(1f, 0f, 1f), new Vector3(1f, 0f, 1f), out axisX, out axisY,
                out axisZ, out halfExtents);
            Assert.AreEqual(PropObbBakeMath.SourceLocalAxes, source, "a flat prop is still a box");
            AssertVector(new Vector3(0.5f, 0f, 0.5f), halfExtents, "the zero axis stays zero");
        }

        private static void AssertVector(Vector3 expected, Vector3 actual, string message)
        {
            Assert.AreEqual(expected.x, actual.x, Tolerance, message + " (x)");
            Assert.AreEqual(expected.y, actual.y, Tolerance, message + " (y)");
            Assert.AreEqual(expected.z, actual.z, Tolerance, message + " (z)");
        }
    }
}
