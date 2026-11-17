using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the contact rule: an item is taken when the shortest distance from the ball's
    /// centre to the item's oriented box (its own size, placed with its world rotation) is
    /// at most the ball's collision radius plus the contact margin - the item's collision
    /// model touching the ball's, not a bounding sphere around the item overlapping it.
    /// </summary>
    /// <remarks>
    /// The expected distances are worked by hand from the box geometry (a point beyond a
    /// face, an edge or a corner of an axis-aligned box), never from the code under test.
    /// </remarks>
    public class ItemContactTests
    {
        private const float Tol = 1e-5f;
        private static readonly string[] Allowed = new string[] { "stone" };

        private static float BoxDistance(Vector3 size, Quaternion rotation, Vector3 centre,
            Vector3 point)
        {
            float distance;
            Assert.IsTrue(PickupAbsorbMath.TryComputeBoxDistance(size, rotation, centre, point,
                out distance));
            return distance;
        }

        private static Quaternion Yaw(float degrees)
        {
            float half = degrees * Mathf.PI / 360f;
            return new Quaternion(0f, Mathf.Sin(half), 0f, Mathf.Cos(half));
        }

        [Test]
        public void FaceEdgeAndCornerRegionsMatchTheBoxGeometry()
        {
            // A 2 x 4 x 6 m box centred on the origin: half extents (1, 2, 3).
            Vector3 size = new Vector3(2f, 4f, 6f);
            Quaternion q = Quaternion.identity;
            // Face region: only x is outside -> distance 4 - 1 = 3.
            Assert.AreEqual(3f, BoxDistance(size, q, Vector3.zero, new Vector3(4f, 0f, 0f)), Tol);
            // Edge region: x and y are outside by 3 and 2 -> sqrt(9 + 4).
            Assert.AreEqual(Mathf.Sqrt(13f),
                BoxDistance(size, q, Vector3.zero, new Vector3(4f, 4f, 1f)), Tol);
            // Corner region: all three outside by 3, 2, 1 -> sqrt(14).
            Assert.AreEqual(Mathf.Sqrt(14f),
                BoxDistance(size, q, Vector3.zero, new Vector3(4f, 4f, 4f)), Tol);
            // Negative side mirrors it.
            Assert.AreEqual(3f, BoxDistance(size, q, Vector3.zero, new Vector3(-4f, 0f, 0f)), Tol);
            // The box is centred on the item's position, not on the origin.
            Assert.AreEqual(3f, BoxDistance(size, q, new Vector3(10f, 0f, 0f),
                new Vector3(14f, 0f, 0f)), Tol);
        }

        [Test]
        public void ACentreInsideOrOnTheBoxIsAtDistanceZero()
        {
            Vector3 size = new Vector3(2f, 4f, 6f);
            Quaternion q = Quaternion.identity;
            Assert.AreEqual(0f, BoxDistance(size, q, Vector3.zero, Vector3.zero), Tol);
            Assert.AreEqual(0f, BoxDistance(size, q, Vector3.zero, new Vector3(0.9f, -1.9f, 2.9f)), Tol);
            Assert.AreEqual(0f, BoxDistance(size, q, Vector3.zero, new Vector3(1f, 2f, 3f)), Tol);
        }

        [Test]
        public void RotatingTheBoxMovesItsFacesNotTheDistanceFormula()
        {
            // A 2 x 2 x 6 m box (half extents 1, 1, 3), the ball's centre at (10, 0, 0).
            Vector3 size = new Vector3(2f, 2f, 6f);
            Vector3 ball = new Vector3(10f, 0f, 0f);
            // Unturned: the 1 m half-width faces x -> 9 m.
            Assert.AreEqual(9f, BoxDistance(size, Quaternion.identity, Vector3.zero, ball), 1e-4f);
            // Yawed 90 degrees: the 3 m half-length now faces x -> 7 m.
            Assert.AreEqual(7f, BoxDistance(size, Yaw(90f), Vector3.zero, ball), 1e-4f);
            // Yawed 45 degrees: the nearest point is the corner (1, 0, 3) turned by 45 degrees,
            // which lies at x = (1 + 3)/sqrt(2); the ball sees the box edge there. Work it in
            // the box frame instead: the ball is at (10cos45, 0, -10sin45) = (7.0711, 0, -7.0711)
            // (rotating the point by -45 about y), so x is out by 6.0711 and z by 4.0711.
            float expected45 = Mathf.Sqrt(6.0710678f * 6.0710678f + 4.0710678f * 4.0710678f);
            Assert.AreEqual(expected45, BoxDistance(size, Yaw(45f), Vector3.zero, ball), 1e-3f);
            // Three positions of the same turn agree with the equivalent turned geometry:
            // yawing by 180 degrees leaves a centred box unchanged.
            Assert.AreEqual(9f, BoxDistance(size, Yaw(180f), Vector3.zero, ball), 1e-4f);
        }

        [Test]
        public void ANonUnitQuaternionIsNormalisedAndTheZeroQuaternionIsRefused()
        {
            Vector3 size = new Vector3(2f, 2f, 6f);
            Vector3 ball = new Vector3(10f, 0f, 0f);
            Quaternion unit = Yaw(90f);
            Quaternion scaled = new Quaternion(unit.x * 3f, unit.y * 3f, unit.z * 3f, unit.w * 3f);
            Assert.AreEqual(BoxDistance(size, unit, Vector3.zero, ball),
                BoxDistance(size, scaled, Vector3.zero, ball), 1e-4f);

            float distance;
            Assert.IsFalse(PickupAbsorbMath.TryComputeBoxDistance(size, new Quaternion(0f, 0f, 0f, 0f),
                Vector3.zero, ball, out distance));
            Assert.AreEqual(0f, distance);
            Assert.IsFalse(PickupAbsorbMath.TryComputeBoxDistance(size,
                new Quaternion(float.NaN, 0f, 0f, 1f), Vector3.zero, ball, out distance));
            Assert.IsFalse(PickupAbsorbMath.TryComputeBoxDistance(size,
                new Quaternion(float.PositiveInfinity, 0f, 0f, 1f), Vector3.zero, ball,
                out distance));
        }

        [Test]
        public void DegenerateSizesAreFlatBoxesAndBadInputIsRefused()
        {
            Quaternion q = Quaternion.identity;
            // A zero-thickness plate (paper): 2 x 0 x 2 m, the ball 3 m above its centre.
            Assert.AreEqual(3f, BoxDistance(new Vector3(2f, 0f, 2f), q, Vector3.zero,
                new Vector3(0f, 3f, 0f)), Tol);
            // A line (one edge only): 4 m long on x, the ball 3 m off its end along x.
            Assert.AreEqual(1f, BoxDistance(new Vector3(4f, 0f, 0f), q, Vector3.zero,
                new Vector3(3f, 0f, 0f)), Tol);
            // A point: the distance is the centre distance.
            Assert.AreEqual(5f, BoxDistance(Vector3.zero, q, Vector3.zero, new Vector3(3f, 4f, 0f)), Tol);

            float distance;
            Assert.IsFalse(PickupAbsorbMath.TryComputeBoxDistance(new Vector3(-1f, 1f, 1f), q,
                Vector3.zero, Vector3.zero, out distance));
            Assert.IsFalse(PickupAbsorbMath.TryComputeBoxDistance(new Vector3(float.NaN, 1f, 1f), q,
                Vector3.zero, Vector3.zero, out distance));
            Assert.IsFalse(PickupAbsorbMath.TryComputeBoxDistance(Vector3.one, q,
                new Vector3(float.NaN, 0f, 0f), Vector3.zero, out distance));
            Assert.IsFalse(PickupAbsorbMath.TryComputeBoxDistance(Vector3.one, q, Vector3.zero,
                new Vector3(0f, float.PositiveInfinity, 0f), out distance));
            // Edges so large their squares overflow float are unrepresentable, not "far".
            Assert.IsFalse(PickupAbsorbMath.TryComputeBoxDistance(Vector3.one, q, Vector3.zero,
                new Vector3(3e38f, 3e38f, 0f), out distance));
            Assert.AreEqual(0f, distance);
        }

        [Test]
        public void ASlenderItemApproachedFromTheSideIsNotTakenAtItsBoundingSphere()
        {
            // A toothbrush: 0.012 x 0.0155 x 0.18 m (the reference level's own size), lying
            // along z. The start ball's collision radius is 0.111 m. The item's old
            // bounding-sphere radius was 0.0905 m, so it used to be taken from 0.2015 m;
            // its flank is only 0.006 m from its centre, so from the side it is touched at
            // 0.117 m.
            Vector3 size = new Vector3(0.012f, 0.0155f, 0.18f);
            float ballR = 0.111f;

            // From the side (+x), 0.15 m away: the flank is 0.144 m from the ball's centre,
            // outside the 0.111 m radius -> not taken (the old rule took it from 0.2015 m).
            Assert.AreEqual(AbsorbOutcome.RejectOutOfReach,
                Evaluate(size, Quaternion.identity, new Vector3(0.15f, 0f, 0f), ballR));
            // 1 mm inside the flank contact distance: taken.
            Assert.AreEqual(AbsorbOutcome.Absorb,
                Evaluate(size, Quaternion.identity, new Vector3(0.116f, 0f, 0f), ballR));
            // 1 mm outside it: not.
            Assert.AreEqual(AbsorbOutcome.RejectOutOfReach,
                Evaluate(size, Quaternion.identity, new Vector3(0.118f, 0f, 0f), ballR));

            // Along its axis (+z) its end face is 0.09 m from its centre, so it is taken from
            // 0.201 m: the long axis reaches farther, as it should.
            Assert.AreEqual(AbsorbOutcome.Absorb,
                Evaluate(size, Quaternion.identity, new Vector3(0f, 0f, 0.200f), ballR));
            Assert.AreEqual(AbsorbOutcome.RejectOutOfReach,
                Evaluate(size, Quaternion.identity, new Vector3(0f, 0f, 0.202f), ballR));
            // Turned 90 degrees about y, the same item's long axis points along x.
            Assert.AreEqual(AbsorbOutcome.Absorb,
                Evaluate(size, Yaw(90f), new Vector3(0.200f, 0f, 0f), ballR));
            Assert.AreEqual(AbsorbOutcome.RejectOutOfReach,
                Evaluate(size, Yaw(90f), new Vector3(0f, 0f, 0.200f), ballR));
        }

        [Test]
        public void TheFirstTouchingStepPlacesTheBoxFaceAtTheBallSurface()
        {
            // The ball approaches the item along +x in 4 mm steps (a slow roll). The first
            // step that takes the item leaves the box face within one step of the ball's
            // surface: the gap (box distance minus the collision radius) is in (-4 mm, 0].
            Vector3 size = new Vector3(0.012f, 0.0155f, 0.18f);
            float ballR = 0.111f;
            float previousGap = 1f;
            for (int step = 0; step < 100; step++)
            {
                Vector3 itemPosition = new Vector3(0.25f - 0.004f * step, 0f, 0f);
                if (Evaluate(size, Quaternion.identity, itemPosition, ballR) != AbsorbOutcome.Absorb)
                {
                    previousGap = BoxDistance(size, Quaternion.identity, itemPosition, Vector3.zero) - ballR;
                    continue;
                }
                float gap = BoxDistance(size, Quaternion.identity, itemPosition, Vector3.zero) - ballR;
                Assert.LessOrEqual(gap, 1e-6f, "taken only when the box touches the ball");
                Assert.Greater(gap, -0.004f - 1e-6f, "and no later than one step after");
                Assert.Greater(previousGap, 0f, "the step before was a miss");
                return;
            }
            Assert.Fail("the item was never taken");
        }

        private static AbsorbOutcome Evaluate(Vector3 size, Quaternion rotation,
            Vector3 itemPosition, float ballRadius)
        {
            return PickupAbsorbMath.EvaluateAbsorb(true, "stone", Allowed, Allowed.Length, size,
                rotation, itemPosition, Vector3.zero, ballRadius, 0f,
                PickupAbsorbMath.DefaultVolumeK, ballRadius * 2f, 1000f);
        }
    }
}
