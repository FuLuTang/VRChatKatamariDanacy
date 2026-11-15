using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ball_terrain_support/test_ramp_support.py.
    /// Lengths are metres in world space (+Y up). Expected values are hand
    /// calculations or geometric invariants, never a re-run of the implementation
    /// formula. The assembly layer (KatamariBallMotor) cannot be instantiated in
    /// EditMode, so every case here exercises SupportSurfaceMath, which is where
    /// the design puts the whole ramp decision (P3 of
    /// Docs/Design/BallMotor/TERRAIN_SUPPORT.md).
    ///
    /// Conventions used below: a ramp's normal is (0, cos(slope), -sin(slope)),
    /// so the downhill direction is +Z and the plane height falls as z grows;
    /// <c>center</c> is a point on the walking plane (TryBakeRamp's output), not
    /// the box centre.
    /// </summary>
    public class RampSupportTests
    {
        private const float Tolerance = 1e-5f;
        private const float MaxSlopeDegrees = 45f;

        private static readonly Vector3 Up = new Vector3(0f, 1f, 0f);

        // The same empty tables the P1/P2 tests pass.
        private static Vector3[] NoVectors()
        {
            return new Vector3[0];
        }

        private static float[] NoFloats()
        {
            return new float[0];
        }

        private static Vector3 RampNormal(float slopeDegrees)
        {
            float radians = slopeDegrees * Mathf.Deg2Rad;
            return new Vector3(0f, Mathf.Cos(radians), -Mathf.Sin(radians));
        }

        private sealed class Surface
        {
            public bool Ok;
            public int Id;
            public Vector3 Point;
            public Vector3 Normal;
            public float Height;
            public int Candidates;
        }

        private static bool[] MaskFor(Vector3[] maxima, int boxCount, float reference,
            float stepLimit)
        {
            bool[] mask = new bool[boxCount];
            for (int i = 0; i < boxCount; i++)
            {
                mask[i] = SupportSurfaceMath.IsStepWithinLimit(maxima[i].y, reference, stepLimit);
            }
            return mask;
        }

        private static Surface QueryWithProps(Vector3 probe, float reference, float stepLimit,
            Vector3[] boxMinima, Vector3[] boxMaxima, int boxCount,
            Vector3[] rampCenters, Vector3[] rampNormals, Vector3[] rampRights,
            float[] rampHalfWidths, float[] rampHalfRuns, int rampCount,
            Vector3[] propMinima, Vector3[] propMaxima, int propCount)
        {
            Surface surface = new Surface();
            surface.Ok = SupportSurfaceMath.TryResolveSupport(probe, reference, stepLimit,
                Up, 0f, boxMinima, boxMaxima,
                MaskFor(boxMaxima, boxCount, reference, stepLimit), boxCount,
                rampCenters, rampNormals, rampRights, rampHalfWidths, rampHalfRuns,
                rampCount, propMinima, propMaxima, propCount, out surface.Id,
                out surface.Point, out surface.Normal, out surface.Height,
                out surface.Candidates);
            return surface;
        }

        private static Surface Query(Vector3 probe, float reference, float stepLimit,
            Vector3[] boxMinima, Vector3[] boxMaxima, int boxCount,
            Vector3[] rampCenters, Vector3[] rampNormals, Vector3[] rampRights,
            float[] rampHalfWidths, float[] rampHalfRuns, int rampCount)
        {
            return QueryWithProps(probe, reference, stepLimit, boxMinima, boxMaxima,
                boxCount, rampCenters, rampNormals, rampRights, rampHalfWidths,
                rampHalfRuns, rampCount, NoVectors(), NoVectors(), 0);
        }

        private static Surface QueryOneRamp(Vector3 probe, float reference, float stepLimit,
            Vector3 center, Vector3 normal, Vector3 right, float halfWidth, float halfRun)
        {
            return Query(probe, reference, stepLimit, NoVectors(), NoVectors(), 0,
                new Vector3[] { center }, new Vector3[] { normal }, new Vector3[] { right },
                new float[] { halfWidth }, new float[] { halfRun }, 1);
        }

        private static Surface QueryNoRamps(Vector3 probe, float reference, float stepLimit)
        {
            return Query(probe, reference, stepLimit, NoVectors(), NoVectors(), 0,
                NoVectors(), NoVectors(), NoVectors(), NoFloats(), NoFloats(), 0);
        }

        // ---------------------------------------------------------------- 1 ----
        [Test]
        public void RampHeightMatchesThePlaneEquation()
        {
            Vector3 normal = RampNormal(30f);
            Vector3 center = new Vector3(0f, 0.5f, 0f);
            Vector3 right = new Vector3(1f, 0f, 0f);
            float tan30 = Mathf.Tan(30f * Mathf.Deg2Rad);

            Surface atCenter = QueryOneRamp(new Vector3(0f, 2f, 0f), 0f, 10f,
                center, normal, right, 0.5f, 4f);
            Assert.IsTrue(atCenter.Ok);
            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId, atCenter.Id);
            Assert.AreEqual(0.5f, atCenter.Height, Tolerance);
            Assert.AreEqual(0.5f, atCenter.Point.y, Tolerance);
            Assert.AreEqual(0f, atCenter.Point.x, Tolerance);
            Assert.AreEqual(0f, atCenter.Point.z, Tolerance);
            Assert.AreEqual(normal.x, atCenter.Normal.x, Tolerance);
            Assert.AreEqual(normal.y, atCenter.Normal.y, Tolerance);
            Assert.AreEqual(normal.z, atCenter.Normal.z, Tolerance);

            // This normal tilts toward -Z, so the plane rises toward +Z: one metre
            // up-slope (+Z) is 0.5 + tan(30) metres high.
            Surface uphill = QueryOneRamp(new Vector3(0f, 2f, 1f), 0f, 10f,
                center, normal, right, 0.5f, 4f);
            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId, uphill.Id);
            Assert.AreEqual(0.5f + tan30, uphill.Height, Tolerance);
            Assert.AreEqual(1f, uphill.Point.z, Tolerance);
            Assert.AreEqual(uphill.Height, uphill.Point.y, Tolerance);

            // Downhill is -Z: half a metre down-slope is 0.5 - tan(30)/2.
            Surface downhill = QueryOneRamp(new Vector3(0f, 2f, -0.5f), 0f, 10f,
                center, normal, right, 0.5f, 4f);
            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId, downhill.Id);
            Assert.AreEqual(0.5f - tan30 * 0.5f, downhill.Height, Tolerance);
        }

        // ---------------------------------------------------------------- 2 ----
        [Test]
        public void RampFootprintRejectsPointsOutsideItsHorizontalRectangle()
        {
            Vector3 normal = RampNormal(30f);
            Vector3 center = new Vector3(0f, 1f, 0f);
            Vector3 right = new Vector3(1f, 0f, 0f);

            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId,
                QueryOneRamp(new Vector3(0.5f, 2f, 0f), 0f, 10f, center, normal, right,
                    0.5f, 1f).Id, "right edge is inclusive");
            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId,
                QueryOneRamp(new Vector3(-0.5f, 2f, 0f), 0f, 10f, center, normal, right,
                    0.5f, 1f).Id, "left edge is inclusive");
            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId,
                QueryOneRamp(new Vector3(0f, 2f, 1f), 0f, 10f, center, normal, right,
                    0.5f, 1f).Id, "downhill edge is inclusive");
            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId,
                QueryOneRamp(new Vector3(0f, 2f, -1f), 0f, 10f, center, normal, right,
                    0.5f, 1f).Id, "uphill edge is inclusive");

            // One millimetre outside each axis falls back to the ground (the ramp
            // is above the floor here, so "not the ramp id" is the whole assertion).
            Assert.AreEqual(SupportSurfaceMath.GroundSurfaceId,
                QueryOneRamp(new Vector3(0.5001f, 2f, 0f), 0f, 10f, center, normal, right,
                    0.5f, 1f).Id);
            Assert.AreEqual(SupportSurfaceMath.GroundSurfaceId,
                QueryOneRamp(new Vector3(0f, 2f, 1.0001f), 0f, 10f, center, normal, right,
                    0.5f, 1f).Id);

            // The footprint is measured in the horizontal frame: a probe at the same
            // horizontal offset but above the plane is still inside.
            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId,
                QueryOneRamp(new Vector3(0.25f, 20f, 0.5f), 0f, 10f, center, normal, right,
                    0.5f, 1f).Id);
        }

        // ---------------------------------------------------------------- 3 ----
        [Test]
        public void ARampSteeperThanTheLimitIsRefusedWithAReason()
        {
            Vector3 steep = RampNormal(58f);
            Vector3 unitNormal;
            Vector3 unitRight;
            float slope;
            int code;

            Assert.IsFalse(SupportSurfaceMath.TryValidateRamp(Vector3.zero, steep,
                new Vector3(1f, 0f, 0f), 1f, 1f, MaxSlopeDegrees,
                out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalSlopeTooSteep, code);
            Assert.AreEqual(58f, slope, 0.01f, "the measured slope is reported with the refusal");
            Assert.AreEqual(0f, unitNormal.y, Tolerance, "a refusal writes no surface");
            Assert.AreEqual("", SupportSurfaceMath.RampRefusalReason(SupportSurfaceMath.RampRefusalNone));
            Assert.IsTrue(SupportSurfaceMath.RampRefusalReason(code).Length > 0);

            // 45.1 is over the rail; 44.9 is inside it. The equality case is avoided
            // because cos/sin round-trip through float would make a 45.0000x reading
            // ambiguous, and the rule is "> limit", not "== limit".
            Assert.IsFalse(SupportSurfaceMath.TryValidateRamp(Vector3.zero, RampNormal(45.1f),
                new Vector3(1f, 0f, 0f), 1f, 1f, MaxSlopeDegrees,
                out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalSlopeTooSteep, code);
            Assert.IsTrue(SupportSurfaceMath.TryValidateRamp(Vector3.zero, RampNormal(44.9f),
                new Vector3(1f, 0f, 0f), 1f, 1f, MaxSlopeDegrees,
                out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalNone, code);
            Assert.AreEqual(44.9f, slope, 0.01f);

            // A different configured rail re-tunes the same predicate, and capacity is
            // the one ramp predicate the motor uses before it validates anything.
            Assert.IsTrue(SupportSurfaceMath.TryValidateRamp(Vector3.zero, RampNormal(58f),
                new Vector3(1f, 0f, 0f), 1f, 1f, 60f,
                out unitNormal, out unitRight, out slope, out code));
            Assert.IsTrue(SupportSurfaceMath.IsRampCountAllowed(4));
            Assert.IsFalse(SupportSurfaceMath.IsRampCountAllowed(5));
            Assert.IsFalse(SupportSurfaceMath.IsRampCountAllowed(-1));
        }

        // ---------------------------------------------------------------- 4 ----
        [Test]
        public void ARampWithANonUpwardNormalIsRefused()
        {
            Vector3 unitNormal;
            Vector3 unitRight;
            float slope;
            int code;

            Assert.IsFalse(SupportSurfaceMath.TryValidateRamp(Vector3.zero,
                new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), 1f, 1f, MaxSlopeDegrees,
                out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalNormalNotUpward, code);

            // The design's boundary is inclusive: n.y <= 0.05 is refused.
            Vector3 nearlyUp = new Vector3(0f, 0.05f, 0.99875f);
            Assert.IsFalse(SupportSurfaceMath.TryValidateRamp(Vector3.zero, nearlyUp,
                new Vector3(1f, 0f, 0f), 1f, 1f, MaxSlopeDegrees,
                out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalNormalNotUpward, code);

            // A flat plane has no downhill direction: it is a box top, not a ramp.
            Assert.IsFalse(SupportSurfaceMath.TryValidateRamp(Vector3.zero, Up,
                new Vector3(1f, 0f, 0f), 1f, 1f, MaxSlopeDegrees,
                out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalDegenerateAxis, code);

            // A right axis that projects to nothing horizontally is degenerate too.
            Assert.IsFalse(SupportSurfaceMath.TryValidateRamp(Vector3.zero, RampNormal(30f),
                Up, 1f, 1f, MaxSlopeDegrees,
                out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalDegenerateAxis, code);
        }

        // ---------------------------------------------------------------- 5 ----
        [Test]
        public void BakeFromATransformedBoxYieldsTheExpectedPlane()
        {
            // The shipped board shape: 0.35 m wide, 0.04 m thick, 1.3984 m along the run.
            Vector3 size = new Vector3(0.35f, 0.04f, 1.3984f);
            Vector3 boxCenter = new Vector3(2f, 1f, 3f);
            Vector3 boxNormal = RampNormal(30f);
            Vector3 boxRight = new Vector3(1f, 0f, 0f);
            Vector3 walk;
            Vector3 normal;
            Vector3 right;
            float halfWidth;
            float halfRun;
            float slope;
            int code;

            Assert.IsTrue(SupportSurfaceMath.TryBakeRamp(boxCenter, boxNormal, boxRight,
                size, MaxSlopeDegrees, out walk, out normal, out right, out halfWidth,
                out halfRun, out slope, out code));

            // The walking face is the +Y face: half a thickness along the normal.
            Assert.AreEqual(boxCenter.x, walk.x, Tolerance);
            Assert.AreEqual(boxCenter.y + boxNormal.y * size.y * 0.5f, walk.y, Tolerance);
            Assert.AreEqual(boxCenter.z + boxNormal.z * size.y * 0.5f, walk.z, Tolerance);
            Assert.AreEqual(boxNormal.x, normal.x, Tolerance);
            Assert.AreEqual(boxNormal.y, normal.y, Tolerance);
            Assert.AreEqual(boxNormal.z, normal.z, Tolerance);
            Assert.AreEqual(0f, right.y, Tolerance, "the stored right is horizontal");
            Assert.AreEqual(1f, right.x, Tolerance);
            Assert.AreEqual(0.175f, halfWidth, Tolerance);
            Assert.AreEqual(size.z * 0.5f * Mathf.Cos(30f * Mathf.Deg2Rad), halfRun, Tolerance);
            Assert.AreEqual(30f, slope, 0.01f);

            // 45 degrees at a different non-unit scale.
            Vector3 steepSize = new Vector3(2f, 0.1f, 2f);
            Vector3 steepNormal = RampNormal(45f);
            Assert.IsTrue(SupportSurfaceMath.TryBakeRamp(new Vector3(-1f, 0.5f, 0.25f),
                steepNormal, boxRight, steepSize, MaxSlopeDegrees,
                out walk, out normal, out right, out halfWidth, out halfRun, out slope, out code));
            Assert.AreEqual(1f, halfWidth, Tolerance);
            Assert.AreEqual(1f * Mathf.Cos(45f * Mathf.Deg2Rad), halfRun, Tolerance);
            Assert.AreEqual(45f, slope, 0.01f);
            Assert.AreEqual(steepNormal.y, normal.y, Tolerance);

            // A box whose walking face is below the mid-plane would sink the ball by
            // half the thickness; the bake must never report the box centre.
            Assert.AreNotEqual(boxCenter.y, walk.y);
        }

        // ---------------------------------------------------------------- 6 ----
        [Test]
        public void HalfRunUsesTheHorizontalProjection()
        {
            // The shipped ramp_to_bed board: 10.7 degrees, 1.3984 m along the slope.
            float slopeDegrees = 10.7f;
            Vector3 size = new Vector3(0.35f, 0.04f, 1.3984f);
            Vector3 walk;
            Vector3 normal;
            Vector3 right;
            float halfWidth;
            float halfRun;
            float slope;
            int code;

            Assert.IsTrue(SupportSurfaceMath.TryBakeRamp(Vector3.zero, RampNormal(slopeDegrees),
                new Vector3(1f, 0f, 0f), size, MaxSlopeDegrees, out walk, out normal,
                out right, out halfWidth, out halfRun, out slope, out code));

            float tiltedHalfLength = size.z * 0.5f;
            float expected = tiltedHalfLength * Mathf.Cos(slopeDegrees * Mathf.Deg2Rad);
            Assert.AreEqual(expected, halfRun, Tolerance);
            Assert.Less(halfRun, tiltedHalfLength, "the horizontal run is shorter than the board");
            Assert.AreEqual(0.687f, halfRun, 0.001f);
        }

        // ---------------------------------------------------------------- 7 ----
        [Test]
        public void CapacityRefusesTheFifthRampAndKeepsThePreviousFour()
        {
            Assert.IsTrue(SupportSurfaceMath.IsRampCountAllowed(4));
            Assert.IsFalse(SupportSurfaceMath.IsRampCountAllowed(5));

            // Five registrations, but the query reads exactly four: the fifth (which
            // would win at 0.5 m) must never be selected.
            Vector3[] centers = new Vector3[5];
            Vector3[] normals = new Vector3[5];
            Vector3[] rights = new Vector3[5];
            float[] widths = new float[5];
            float[] runs = new float[5];
            for (int i = 0; i < 5; i++)
            {
                centers[i] = new Vector3(0f, 0.1f * (i + 1), 0f);
                normals[i] = RampNormal(30f);
                rights[i] = new Vector3(1f, 0f, 0f);
                widths[i] = 5f;
                runs[i] = 5f;
            }

            Surface four = Query(new Vector3(0f, 2f, 0f), 0f, 10f, NoVectors(), NoVectors(),
                0, centers, normals, rights, widths, runs, 4);
            Assert.IsTrue(four.Ok);
            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId + 3, four.Id);
            Assert.AreEqual(0.4f, four.Height, Tolerance);
            Assert.AreEqual(5, four.Candidates);

            Surface one = Query(new Vector3(0f, 2f, 0f), 0f, 10f, NoVectors(), NoVectors(),
                0, centers, normals, rights, widths, runs, 1);
            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId, one.Id);
            Assert.AreEqual(0.1f, one.Height, Tolerance);
            Assert.AreEqual(2, one.Candidates);
        }

        // ---------------------------------------------------------------- 8 ----
        [Test]
        public void TheRampFootMeetsTheFloorWithoutAStep()
        {
            // A 30 degree board that rises 0.70 m: horizontal run 0.70 / tan(30) =
            // 1.2124 m. The plane rises toward +Z, so with the centre at mid-run the
            // foot is at z = 0 and the top at z = run.
            float run = 0.70f / Mathf.Tan(30f * Mathf.Deg2Rad);
            Vector3 center = new Vector3(0f, 0.35f, run * 0.5f);
            Vector3 normal = RampNormal(30f);
            float halfRun = run * 0.5f;

            Surface atFoot = QueryOneRamp(new Vector3(0f, 2f, 0f), 0f, 10f,
                center, normal, new Vector3(1f, 0f, 0f), 0.175f, halfRun);
            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId, atFoot.Id);
            Assert.AreEqual(0f, atFoot.Height, 0.001f, "the foot lands on the floor");

            Surface justInside = QueryOneRamp(new Vector3(0f, 2f, 0.005f), 0f, 10f,
                center, normal, new Vector3(1f, 0f, 0f), 0.175f, halfRun);
            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId, justInside.Id);
            Assert.Less(justInside.Height, 0.005f,
                "the foot is below the minimum step height, so it is not a step");
            Assert.IsFalse(SupportSurfaceMath.IsStepCrossing(justInside.Height, 0.005f));

            Surface justOutside = QueryOneRamp(new Vector3(0f, 2f, -0.01f), 0f, 10f,
                center, normal, new Vector3(1f, 0f, 0f), 0.175f, halfRun);
            Assert.AreEqual(SupportSurfaceMath.GroundSurfaceId, justOutside.Id);
            Assert.AreEqual(0f, justOutside.Height, Tolerance);
        }

        // ---------------------------------------------------------------- 9 ----
        [Test]
        public void ARampRegisteredAsABoxWouldBeAWallAtItsFoot()
        {
            // The same 30 degree board as its enclosing AABB. Its top is 0.70 +
            // 0.04*cos(30) = 0.7346 m, far above the 1.10 m ball's 0.275 m step
            // limit, so the box is not a support candidate at all: in the sweep it is
            // a wall, which is exactly why the host keeps ramps out of staticBoxes.
            Vector3[] minima = { new Vector3(-0.175f, 0f, 0f) };
            Vector3[] maxima = { new Vector3(0.175f, 0.7346f, 1.2124f) };
            Surface asBox = Query(new Vector3(0f, 2f, 0.2f), 0f, 0.275f, minima, maxima, 1,
                NoVectors(), NoVectors(), NoVectors(), NoFloats(), NoFloats(), 0);
            Assert.AreEqual(SupportSurfaceMath.GroundSurfaceId, asBox.Id);
            Assert.AreEqual(1, asBox.Candidates, "the box top is outside the step band");

            // The ramp's own table has no such wall: the same probe is on the slope.
            float run = 0.70f / Mathf.Tan(30f * Mathf.Deg2Rad);
            Surface asRamp = QueryOneRamp(new Vector3(0f, 2f, 0.2f), 0f, 0.275f,
                new Vector3(0f, 0.35f, run * 0.5f), RampNormal(30f),
                new Vector3(1f, 0f, 0f), 0.175f, run * 0.5f);
            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId, asRamp.Id);
            Assert.Greater(asRamp.Height, 0f);
        }

        // --------------------------------------------------------------- 10 ----
        [Test]
        public void AnEmptyRampTableLeavesThePreRampSurface()
        {
            // The motor's switch passes rampCount = 0 when terrain support is off, so
            // this is the pure-math half of "off is bit-for-bit the pre-P3 result":
            // a registered ramp that the count excludes changes nothing.
            Vector3 center = new Vector3(0f, 0.5f, 0f);
            Vector3 normal = RampNormal(30f);
            Surface withCountZero = Query(new Vector3(0f, 2f, 0f), 0f, 0.275f,
                NoVectors(), NoVectors(), 0, new Vector3[] { center },
                new Vector3[] { normal }, new Vector3[] { new Vector3(1f, 0f, 0f) },
                new float[] { 1f }, new float[] { 1f }, 0);
            Surface withEmptyTable = QueryNoRamps(new Vector3(0f, 2f, 0f), 0f, 0.275f);

            Assert.IsTrue(withCountZero.Ok);
            Assert.AreEqual(withEmptyTable.Id, withCountZero.Id);
            Assert.AreEqual(withEmptyTable.Height, withCountZero.Height, 0f);
            Assert.AreEqual(withEmptyTable.Point, withCountZero.Point);
            Assert.AreEqual(withEmptyTable.Normal, withCountZero.Normal);
            Assert.AreEqual(withEmptyTable.Candidates, withCountZero.Candidates);
            Assert.AreEqual(SupportSurfaceMath.GroundSurfaceId, withCountZero.Id);
        }

        // --------------------------------------------------------------- 11 ----
        [Test]
        public void TheHighestSurfaceWinsAcrossBoxesRampsAndProps()
        {
            Vector3[] boxMinima = { new Vector3(-1f, 0f, -1f) };
            Vector3[] boxMaxima = { new Vector3(1f, 0.3f, 1f) };
            Vector3[] propMinima = { new Vector3(-1f, 0f, -1f) };
            Vector3[] propMaxima = { new Vector3(1f, 0.32f, 1f) };
            Vector3[] rampCenters = { new Vector3(0f, 0.35f, 0f) };
            Vector3[] rampNormals = { RampNormal(30f) };
            Vector3[] rampRights = { new Vector3(1f, 0f, 0f) };
            float[] rampWidths = { 5f };
            float[] rampRuns = { 5f };

            Surface rampWins = QueryWithProps(new Vector3(0f, 2f, 0f), 0f, 1f, boxMinima,
                boxMaxima, 1, rampCenters, rampNormals, rampRights, rampWidths, rampRuns, 1,
                propMinima, propMaxima, 1);
            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId, rampWins.Id);
            Assert.AreEqual(0.35f, rampWins.Height, Tolerance);

            rampCenters[0] = new Vector3(0f, 0.25f, 0f);
            Surface propWins = QueryWithProps(new Vector3(0f, 2f, 0f), 0f, 1f, boxMinima,
                boxMaxima, 1, rampCenters, rampNormals, rampRights, rampWidths, rampRuns, 1,
                propMinima, propMaxima, 1);
            Assert.AreEqual(SupportSurfaceMath.FirstPropSurfaceId, propWins.Id);
            Assert.AreEqual(0.32f, propWins.Height, Tolerance);

            // Equal heights: a box wins a tie against a ramp, and a ramp wins a tie
            // against a prop (scene geometry before per-scan props).
            boxMaxima[0] = new Vector3(1f, 0.32f, 1f);
            rampCenters[0] = new Vector3(0f, 0.32f, 0f);
            Surface boxTie = QueryWithProps(new Vector3(0f, 2f, 0f), 0f, 1f, boxMinima,
                boxMaxima, 1, rampCenters, rampNormals, rampRights, rampWidths, rampRuns, 1,
                propMinima, propMaxima, 1);
            Assert.AreEqual(1, boxTie.Id);

            boxMaxima[0] = new Vector3(1f, 0.3f, 1f);
            Surface rampTie = QueryWithProps(new Vector3(0f, 2f, 0f), 0f, 1f, boxMinima,
                boxMaxima, 1, rampCenters, rampNormals, rampRights, rampWidths, rampRuns, 1,
                propMinima, propMaxima, 1);
            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId, rampTie.Id);
            Assert.AreEqual(0.32f, rampTie.Height, Tolerance);

            // A ramp outside its footprint loses to whatever is under the probe.
            rampCenters[0] = new Vector3(10f, 0.35f, 0f);
            Surface outside = QueryWithProps(new Vector3(0f, 2f, 0f), 0f, 1f, boxMinima,
                boxMaxima, 1, rampCenters, rampNormals, rampRights, rampWidths, rampRuns, 1,
                propMinima, propMaxima, 1);
            Assert.AreEqual(SupportSurfaceMath.FirstPropSurfaceId, outside.Id);
            Assert.AreEqual(0.32f, outside.Height, Tolerance);
        }

        // --------------------------------------------------------------- 12 ----
        [Test]
        public void RampSurfaceIdsNeverCollideWithBoxOrPropIds()
        {
            Assert.IsTrue(SupportSurfaceMath.IsRampSurfaceId(
                SupportSurfaceMath.FirstRampSurfaceId));
            Assert.IsTrue(SupportSurfaceMath.IsRampSurfaceId(
                SupportSurfaceMath.FirstRampSurfaceId + SupportSurfaceMath.MaxRampCount - 1));
            Assert.IsFalse(SupportSurfaceMath.IsRampSurfaceId(
                SupportSurfaceMath.FirstRampSurfaceId - 1));
            Assert.IsFalse(SupportSurfaceMath.IsRampSurfaceId(
                SupportSurfaceMath.FirstRampSurfaceId + SupportSurfaceMath.MaxRampCount));
            Assert.IsFalse(SupportSurfaceMath.IsRampSurfaceId(SupportSurfaceMath.GroundSurfaceId));
            Assert.IsFalse(SupportSurfaceMath.IsRampSurfaceId(1));
            Assert.IsFalse(SupportSurfaceMath.IsRampSurfaceId(
                SupportSurfaceMath.FirstPropSurfaceId));
            Assert.AreEqual(0, SupportSurfaceMath.RampSurfaceIndex(
                SupportSurfaceMath.FirstRampSurfaceId));
            Assert.AreEqual(3, SupportSurfaceMath.RampSurfaceIndex(
                SupportSurfaceMath.FirstRampSurfaceId + 3));
            Assert.AreEqual(-1, SupportSurfaceMath.RampSurfaceIndex(1));
        }

        // --------------------------------------------------------------- 13 ----
        [Test]
        public void NonFiniteRampInputIsRefusedAndNothingIsWritten()
        {
            Vector3 unitNormal;
            Vector3 unitRight;
            float slope;
            int code;

            Assert.IsFalse(SupportSurfaceMath.TryValidateRamp(
                new Vector3(float.NaN, 0f, 0f), RampNormal(30f), new Vector3(1f, 0f, 0f),
                1f, 1f, MaxSlopeDegrees, out unitNormal, out unitRight, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalInvalidInput, code);
            Assert.AreEqual(0f, unitNormal.y, Tolerance);
            Assert.AreEqual(0f, unitRight.x, Tolerance);

            Assert.IsFalse(SupportSurfaceMath.TryBakeRamp(Vector3.zero, RampNormal(30f),
                new Vector3(1f, 0f, 0f), new Vector3(0.35f, float.NaN, 1.4f),
                MaxSlopeDegrees, out Vector3 walk, out Vector3 normal, out Vector3 right,
                out float halfWidth, out float halfRun, out slope, out code));
            Assert.AreEqual(SupportSurfaceMath.RampRefusalInvalidInput, code);
            Assert.AreEqual(Vector3.zero, walk);
            Assert.AreEqual(0f, halfWidth, Tolerance);
            Assert.AreEqual(0f, halfRun, Tolerance);

            Vector3[] minima = { new Vector3(-1f, 0f, -1f) };
            Vector3[] maxima = { new Vector3(1f, 0.3f, 1f) };
            Surface badCenter = Query(new Vector3(0f, 2f, 0f), 0f, 1f, minima, maxima, 1,
                new Vector3[] { new Vector3(float.NaN, 0f, 0f) },
                new Vector3[] { RampNormal(30f) }, new Vector3[] { new Vector3(1f, 0f, 0f) },
                new float[] { 1f }, new float[] { 1f }, 1);
            Assert.IsFalse(badCenter.Ok);
            Assert.AreEqual(SupportSurfaceMath.NoSurfaceId, badCenter.Id);
            Assert.AreEqual(Vector3.zero, badCenter.Point);
            Assert.AreEqual(0f, badCenter.Height, Tolerance);

            Surface badExtent = Query(new Vector3(0f, 2f, 0f), 0f, 1f, minima, maxima, 1,
                new Vector3[] { new Vector3(0f, 0.5f, 0f) },
                new Vector3[] { RampNormal(30f) }, new Vector3[] { new Vector3(1f, 0f, 0f) },
                new float[] { 0f }, new float[] { 1f }, 1);
            Assert.IsFalse(badExtent.Ok);
        }

        // --------------------------------------------------------------- 14 ----
        [Test]
        public void TheRampTopIsFlushWithTheTableEdge()
        {
            // A 0.70 m table whose near face is z = 0, and a 30 degree board rising to
            // that edge. At the seam both surfaces report 0.70 m, so the selected id
            // flips without a height jump (the "flush top" rule of design §5.3).
            float run = 0.70f / Mathf.Tan(30f * Mathf.Deg2Rad);
            Vector3[] boxMinima = { new Vector3(-1f, 0f, run) };
            Vector3[] boxMaxima = { new Vector3(1f, 0.70f, run + 1f) };
            Vector3[] rampCenters = { new Vector3(0f, 0.35f, run * 0.5f) };
            Vector3[] rampNormals = { RampNormal(30f) };
            Vector3[] rampRights = { new Vector3(1f, 0f, 0f) };
            float[] rampWidths = { 0.175f };
            float[] rampRuns = { run * 0.5f };

            Surface atSeam = Query(new Vector3(0f, 2f, run), 0f, 1f, boxMinima, boxMaxima,
                1, rampCenters, rampNormals, rampRights, rampWidths, rampRuns, 1);
            Assert.AreEqual(1, atSeam.Id, "the box wins the equal-height tie");
            Assert.AreEqual(0.70f, atSeam.Height, Tolerance);

            Surface justOnRamp = Query(new Vector3(0f, 2f, run - 0.001f), 0f, 1f,
                boxMinima, boxMaxima, 1, rampCenters, rampNormals, rampRights, rampWidths,
                rampRuns, 1);
            Assert.AreEqual(SupportSurfaceMath.FirstRampSurfaceId, justOnRamp.Id);
            Assert.AreEqual(0.70f, justOnRamp.Height, 0.001f,
                "the ramp is flush with the edge, not a step");
            Assert.AreEqual(atSeam.Height, justOnRamp.Height, 0.001f);

            Surface justOnTable = Query(new Vector3(0f, 2f, run + 0.001f), 0f, 1f,
                boxMinima, boxMaxima, 1, rampCenters, rampNormals, rampRights, rampWidths,
                rampRuns, 1);
            Assert.AreEqual(1, justOnTable.Id);
            Assert.AreEqual(0.70f, justOnTable.Height, Tolerance);
        }

        // --------------------------------------------------------------- 15 ----
        [Test]
        public void TheTangentialGravityIsOffByDefaultAndPushesDownhill()
        {
            Vector3 velocity = new Vector3(0.3f, 0f, 0.1f);
            Vector3 next;

            // The motor's default scale is 0: a ramp must leave the velocity untouched.
            Assert.IsTrue(SupportSurfaceMath.TryApplyRampTangentialGravity(velocity,
                RampNormal(30f), 9.81f, 0f, 0.02f, 4f, out next));
            Assert.AreEqual(velocity.x, next.x, 0f);
            Assert.AreEqual(velocity.y, next.y, 0f);
            Assert.AreEqual(velocity.z, next.z, 0f);

            // This normal tilts toward -Z, so downhill is -Z:
            // g * sin(30) * 1 * 0.02 = 0.0981 m/s along -Z.
            Assert.IsTrue(SupportSurfaceMath.TryApplyRampTangentialGravity(Vector3.zero,
                RampNormal(30f), 9.81f, 1f, 0.02f, 4f, out next));
            Assert.AreEqual(0f, next.x, Tolerance);
            Assert.AreEqual(0f, next.y, Tolerance);
            Assert.AreEqual(-9.81f * 0.5f * 0.02f, next.z, Tolerance);

            // Flat support: no tangential component.
            Assert.IsTrue(SupportSurfaceMath.TryApplyRampTangentialGravity(velocity, Up,
                9.81f, 1f, 0.02f, 4f, out next));
            Assert.AreEqual(velocity.x, next.x, 0f);
            Assert.AreEqual(velocity.z, next.z, 0f);

            // The speed cap still applies.
            Assert.IsTrue(SupportSurfaceMath.TryApplyRampTangentialGravity(
                new Vector3(0f, 0f, -3.99f), RampNormal(30f), 9.81f, 1f, 0.02f, 4f,
                out next));
            Assert.AreEqual(-4f, next.z, Tolerance);
            Assert.AreEqual(0f, next.x, Tolerance);

            // Bad input is refused and writes zero, never a half velocity.
            Assert.IsFalse(SupportSurfaceMath.TryApplyRampTangentialGravity(velocity,
                RampNormal(30f), 9.81f, 1.5f, 0.02f, 4f, out next));
            Assert.AreEqual(Vector3.zero, next);
            Assert.IsFalse(SupportSurfaceMath.TryApplyRampTangentialGravity(velocity,
                RampNormal(30f), 9.81f, 1f, 0f, 4f, out next));

            // Slope angle helper: flat is 0, the 30 degree plane is 30.
            float slope;
            Assert.IsTrue(SupportSurfaceMath.TryRampSlopeDegrees(Up, out slope));
            Assert.AreEqual(0f, slope, Tolerance);
            Assert.IsTrue(SupportSurfaceMath.TryRampSlopeDegrees(RampNormal(30f), out slope));
            Assert.AreEqual(30f, slope, 0.01f);
        }
    }
}
