using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ball_terrain_support/test_terrain_support.py.
    /// Lengths are metres in world space (+Y up). Expected values are hand
    /// calculations or geometric invariants, never a re-run of the implementation
    /// formula. The assembly layer (KatamariBallMotor) cannot be instantiated in
    /// EditMode, so every case here exercises SupportSurfaceMath, which is where
    /// the design puts the whole decision.
    ///
    /// The last three cases cover the P1 gravity decision: support loss starts a
    /// fall, the fall follows gravity and stops exactly on the lower surface, and
    /// the fall is NOT limited by the old per-step drop clamp.
    /// </summary>
    public class SupportSurfaceTests
    {
        private const float Tolerance = 1e-5f;

        // The design's default step ratio (layout.json max_step_height_over_ball_diameter).
        private const float StepRatio = 0.25f;

        private static readonly Vector3 Up = new Vector3(0f, 1f, 0f);

        private static Vector3[] NoBoxes()
        {
            return new Vector3[0];
        }

        private static float StepLimit(float diameter)
        {
            float limit;
            Assert.IsTrue(SupportSurfaceMath.TryStepHeightLimit(diameter, StepRatio, out limit));
            return limit;
        }

        private static bool Resolve(Vector3 probe, float referenceHeight, float stepLimit,
            Vector3[] minima, Vector3[] maxima, int boxCount,
            out int surfaceId, out Vector3 point, out Vector3 normal, out float height,
            out int candidateCount)
        {
            return SupportSurfaceMath.TryResolveSupport(probe, referenceHeight, stepLimit, Up, 0f,
                minima, maxima, boxCount, out surfaceId, out point, out normal, out height,
                out candidateCount);
        }

        // ---------------------------------------------------------------- 1 ----
        [Test]
        public void GroundOnlyReturnsTheGroundPlane()
        {
            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;
            Vector3[] minima = NoBoxes();
            Vector3[] maxima = NoBoxes();

            Assert.IsTrue(SupportSurfaceMath.TryResolveSupport(new Vector3(1f, 5f, 2f), 0f, 0.6f,
                Up, 0.5f, minima, maxima, 0, out id, out point, out normal, out height,
                out candidates));

            Assert.AreEqual(SupportSurfaceMath.GroundSurfaceId, id);
            Assert.AreEqual(0.5f, height, Tolerance);
            Assert.AreEqual(1f, point.x, Tolerance);
            Assert.AreEqual(0.5f, point.y, Tolerance);
            Assert.AreEqual(2f, point.z, Tolerance);
            Assert.AreEqual(0f, normal.x, Tolerance);
            Assert.AreEqual(1f, normal.y, Tolerance);
            Assert.AreEqual(1, candidates);
        }

        // ---------------------------------------------------------------- 2 ----
        [Test]
        public void BoxTopWinsInsideItsFootprint()
        {
            Vector3[] minima = { new Vector3(-1f, 0f, -1f) };
            Vector3[] maxima = { new Vector3(1f, 0.26f, 1f) };
            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;

            Assert.IsTrue(Resolve(new Vector3(0f, 1f, 0f), 0f, 0.275f,
                minima, maxima, 1, out id, out point, out normal, out height, out candidates));

            Assert.AreEqual(1, id);
            Assert.AreEqual(0.26f, height, Tolerance);
            Assert.AreEqual(0f, point.x, Tolerance);
            Assert.AreEqual(0.26f, point.y, Tolerance);
            Assert.AreEqual(0f, point.z, Tolerance);
            Assert.AreEqual(0f, normal.x, Tolerance);
            Assert.AreEqual(1f, normal.y, Tolerance);
            Assert.AreEqual(2, candidates);
        }

        // ---------------------------------------------------------------- 3 ----
        [Test]
        public void ProbeOnTheFootprintEdgeCountsAsInside()
        {
            Vector3[] minima = { new Vector3(0f, 0f, 0f) };
            Vector3[] maxima = { new Vector3(1f, 0.2f, 1f) };
            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;

            Assert.IsTrue(Resolve(new Vector3(1f, 1f, 0f), 0f, 0.275f,
                minima, maxima, 1, out id, out point, out normal, out height, out candidates));
            Assert.AreEqual(1, id, "the near rim counts as on the box");
            Assert.AreEqual(0.2f, height, Tolerance);

            Assert.IsTrue(Resolve(new Vector3(0f, 1f, 1f), 0f, 0.275f,
                minima, maxima, 1, out id, out point, out normal, out height, out candidates));
            Assert.AreEqual(1, id, "the far rim counts as on the box");
        }

        // ---------------------------------------------------------------- 4 ----
        [Test]
        public void ProbeOutsideTheFootprintFallsBackToGround()
        {
            Vector3[] minima = { new Vector3(0f, 0f, 0f) };
            Vector3[] maxima = { new Vector3(1f, 0.2f, 1f) };
            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;

            // One float step past the rim is outside: the band still counts the box,
            // but the footprint does not.
            Assert.IsTrue(Resolve(new Vector3(1.0001f, 1f, 0f), 0f, 0.275f,
                minima, maxima, 1, out id, out point, out normal, out height, out candidates));
            Assert.AreEqual(SupportSurfaceMath.GroundSurfaceId, id);
            Assert.AreEqual(0f, height, Tolerance);
            Assert.AreEqual(2, candidates);
        }

        // ---------------------------------------------------------------- 5 ----
        [Test]
        public void HighestSurfaceWinsWhenTwoFootprintsOverlap()
        {
            Vector3[] minima = { new Vector3(-2f, 0f, -2f), new Vector3(-1f, 0f, -1f) };
            Vector3[] maxima = { new Vector3(2f, 0.2f, 2f), new Vector3(1f, 0.3f, 1f) };
            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;

            Assert.IsTrue(Resolve(new Vector3(0f, 1f, 0f), 0f, 0.35f,
                minima, maxima, 2, out id, out point, out normal, out height, out candidates));
            Assert.AreEqual(2, id);
            Assert.AreEqual(0.3f, height, Tolerance);
            Assert.AreEqual(3, candidates);
        }

        // ---------------------------------------------------------------- 6 ----
        [Test]
        public void SurfacesAboveTheStepBandAreNotCandidates()
        {
            // A 1 m wall on the ground: its top is above ground + stepLimit for a
            // 1.10 m ball, so it must not act as a floor.
            Vector3[] minima = { new Vector3(-1f, 0f, -1f) };
            Vector3[] maxima = { new Vector3(1f, 1f, 1f) };
            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;

            Assert.IsTrue(Resolve(new Vector3(0f, 0.5f, 0f), 0f, StepLimit(1.10f),
                minima, maxima, 1, out id, out point, out normal, out height, out candidates));
            Assert.AreEqual(SupportSurfaceMath.GroundSurfaceId, id);
            Assert.AreEqual(0f, height, Tolerance);
            Assert.AreEqual(1, candidates, "only the ground is inside the band");

            // The band edge is inclusive: a top exactly at reference + limit is a
            // candidate (it is the step the ball may enter, not a wall).
            Vector3[] edgeMaxima = { new Vector3(1f, StepLimit(1.10f), 1f) };
            Assert.IsTrue(Resolve(new Vector3(0f, 0.5f, 0f), 0f, StepLimit(1.10f),
                minima, edgeMaxima, 1, out id, out point, out normal, out height, out candidates));
            Assert.AreEqual(1, id);
            Assert.AreEqual(StepLimit(1.10f), height, Tolerance);
        }

        // ---------------------------------------------------------------- 7 ----
        [Test]
        public void StepLimitScalesWithDiameter()
        {
            float limit;
            Assert.IsTrue(SupportSurfaceMath.TryStepHeightLimit(0.30f, 0.25f, out limit));
            Assert.AreEqual(0.075f, limit, Tolerance);
            Assert.IsTrue(SupportSurfaceMath.TryStepHeightLimit(0.83f, 0.25f, out limit));
            Assert.AreEqual(0.2075f, limit, Tolerance);
            Assert.IsTrue(SupportSurfaceMath.TryStepHeightLimit(1.10f, 0.25f, out limit));
            Assert.AreEqual(0.275f, limit, Tolerance);
            Assert.IsTrue(SupportSurfaceMath.TryStepHeightLimit(2.20f, 0.25f, out limit));
            Assert.AreEqual(0.55f, limit, Tolerance);

            // Refusals: a non-finite size, a negative/above-one ratio, and a ratio
            // that is not a ratio at all.
            Assert.IsFalse(SupportSurfaceMath.TryStepHeightLimit(float.NaN, 0.25f, out limit));
            Assert.AreEqual(0f, limit, Tolerance);
            Assert.IsFalse(SupportSurfaceMath.TryStepHeightLimit(1.10f, -0.1f, out limit));
            Assert.IsFalse(SupportSurfaceMath.TryStepHeightLimit(1.10f, 1.5f, out limit));
        }

        // ---------------------------------------------------------------- 8 ----
        [Test]
        public void TheBoxThatIsWithinTheLimitLeavesTheSweep()
        {
            // 0.26 m picnic bed. A 1.10 m ball has a 0.275 m limit -> step; a
            // 0.30 m ball has 0.075 m -> wall.
            Assert.IsTrue(SupportSurfaceMath.IsSteppable(0.26f, StepLimit(1.10f), false, false, 0.02f));
            Assert.IsFalse(SupportSurfaceMath.IsSteppable(0.26f, StepLimit(0.30f), false, false, 0.02f));
            // A top below the support is never a wall either.
            Assert.IsTrue(SupportSurfaceMath.IsSteppable(-0.1f, 0f, false, false, 0f));
            // Invalid numbers are conservative: keep the box as a wall.
            Assert.IsFalse(SupportSurfaceMath.IsSteppable(float.NaN, 0.275f, false, false, 0.02f));
            Assert.IsFalse(SupportSurfaceMath.IsSteppable(0.1f, -1f, false, false, 0.02f));
        }

        // ---------------------------------------------------------------- 9 ----
        [Test]
        public void HysteresisKeepsASteppableBoxOutUntilItExceedsTheLimitPlusTheMargin()
        {
            // 0.28 m is above the 0.275 limit but inside the 0.02 m hysteresis, so a
            // box already classified as a step stays one; a fresh box does not.
            Assert.IsTrue(SupportSurfaceMath.IsSteppable(0.28f, 0.275f, false, true, 0.02f));
            Assert.IsFalse(SupportSurfaceMath.IsSteppable(0.28f, 0.275f, false, false, 0.02f));
            // Past limit + hysteresis it goes back to being a wall.
            Assert.IsFalse(SupportSurfaceMath.IsSteppable(0.30f, 0.275f, false, true, 0.02f));
            // Exactly at limit + hysteresis still counts (inclusive).
            Assert.IsTrue(SupportSurfaceMath.IsSteppable(0.29f, 0.275f, false, true, 0.02f));
        }

        // --------------------------------------------------------------- 10 ----
        [Test]
        public void ABallAlreadyAboveTheTopIsNeverPushedOut()
        {
            // Standing on the box: rise is 0 and the box is a step. Even a bogus
            // rise far above the limit must not turn it into a wall while the ball's
            // bottom is already at or above the top: that is the case where the sweep
            // would otherwise push a ball that is inside the expanded box.
            Assert.IsTrue(SupportSurfaceMath.IsSteppable(0f, 0.275f, true, true, 0.02f));
            Assert.IsTrue(SupportSurfaceMath.IsSteppable(1f, 0.275f, true, false, 0.02f));
            // Non-finite input is still refused first: a corrupt state must not
            // masquerade as a classified box.
            Assert.IsFalse(SupportSurfaceMath.IsSteppable(float.NaN, 0.275f, true, false, 0f));
        }

        // --------------------------------------------------------------- 11 ----
        [Test]
        public void NonFiniteInputIsRefusedAndNothingIsWritten()
        {
            Vector3[] minima = { new Vector3(0f, 0f, 0f) };
            Vector3[] maxima = { new Vector3(1f, 0.2f, 1f) };
            int id = 99;
            Vector3 point = new Vector3(9f, 9f, 9f);
            Vector3 normal = new Vector3(9f, 9f, 9f);
            float height = 99f;
            int candidates = 99;

            Assert.IsFalse(SupportSurfaceMath.TryResolveSupport(new Vector3(float.NaN, 1f, 0f), 0f,
                0.3f, Up, 0f, minima, maxima, 1, out id, out point, out normal, out height,
                out candidates));
            Assert.AreEqual(SupportSurfaceMath.NoSurfaceId, id);
            Assert.AreEqual(Vector3.zero, point);
            Assert.AreEqual(Vector3.zero, normal);
            Assert.AreEqual(0f, height, Tolerance);
            Assert.AreEqual(0, candidates);

            // Inverted box bounds are refused up front, not skipped.
            Vector3[] invertedMaxima = { new Vector3(-1f, 0.2f, 1f) };
            id = 99;
            height = 99f;
            Assert.IsFalse(SupportSurfaceMath.TryResolveSupport(new Vector3(0f, 1f, 0f), 0f,
                0.3f, Up, 0f, minima, invertedMaxima, 1, out id, out point, out normal,
                out height, out candidates));
            Assert.AreEqual(SupportSurfaceMath.NoSurfaceId, id);
            Assert.AreEqual(0f, height, Tolerance);

            // A wall-like (horizontal) ground normal cannot answer a height query.
            id = 99;
            Assert.IsFalse(SupportSurfaceMath.TryResolveSupport(new Vector3(0f, 1f, 0f), 0f,
                0.3f, new Vector3(1f, 0f, 0f), 0f, minima, maxima, 1, out id, out point,
                out normal, out height, out candidates));
            Assert.AreEqual(SupportSurfaceMath.NoSurfaceId, id);

            // A count larger than the buffers is refused instead of read past the end.
            id = 99;
            Assert.IsFalse(SupportSurfaceMath.TryResolveSupport(new Vector3(0f, 1f, 0f), 0f,
                0.3f, Up, 0f, minima, maxima, 2, out id, out point, out normal, out height,
                out candidates));
            Assert.AreEqual(SupportSurfaceMath.NoSurfaceId, id);
        }

        // --------------------------------------------------------------- 12 ----
        [Test]
        public void SurfaceSelectionIsStableAcrossTwoIdenticalCalls()
        {
            Vector3[] minima = { new Vector3(-2f, 0f, -2f), new Vector3(-1f, 0f, -1f) };
            Vector3[] maxima = { new Vector3(2f, 0.2f, 2f), new Vector3(1f, 0.2f, 1f) };
            int idA;
            Vector3 pointA;
            Vector3 normalA;
            float heightA;
            int candidatesA;
            int idB;
            Vector3 pointB;
            Vector3 normalB;
            float heightB;
            int candidatesB;

            Vector3 probe = new Vector3(0.25f, 1f, -0.75f);
            Assert.IsTrue(Resolve(probe, 0f, 0.4f, minima, maxima, 2,
                out idA, out pointA, out normalA, out heightA, out candidatesA));
            Assert.IsTrue(Resolve(probe, 0f, 0.4f, minima, maxima, 2,
                out idB, out pointB, out normalB, out heightB, out candidatesB));
            Assert.AreEqual(idA, idB);
            Assert.AreEqual(heightA, heightB, 0f);
            Assert.AreEqual(pointA, pointB);
            Assert.AreEqual(normalA, normalB);
            Assert.AreEqual(candidatesA, candidatesB);

            // Equal heights (0.2 and 0.2, both containing the probe) keep the first
            // registration, which is what makes the order stable.
            Assert.AreEqual(1, idA);
        }

        // --------------------------------------------------------------- 13 ----
        [Test]
        public void CapacityRefusesTheNinthBoxWithoutTouchingTheFirst()
        {
            // The motor's AddBox keeps the ninth registration out (its own capacity
            // signal); this query's contract is that the box table is exactly
            // boxCount entries, so a leftover ninth slot can never be read. The
            // ninth slot here is deliberately the highest box under the probe: a
            // query that read past boxCount would select it.
            Vector3[] minima = new Vector3[9];
            Vector3[] maxima = new Vector3[9];
            for (int i = 0; i < 8; i++)
            {
                minima[i] = new Vector3(-5f, 0f, -5f);
                maxima[i] = new Vector3(5f, 0.1f + 0.01f * i, 5f);
            }
            minima[8] = new Vector3(-5f, 0f, -5f);
            maxima[8] = new Vector3(5f, 5f, 5f);

            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;
            Assert.IsTrue(Resolve(new Vector3(0f, 1f, 0f), 0f, 10f, minima, maxima, 8,
                out id, out point, out normal, out height, out candidates));
            Assert.AreEqual(8, id, "the eighth box wins; the ninth slot is not read");
            Assert.AreEqual(0.17f, height, Tolerance);
            Assert.AreEqual(9, candidates, "1 ground + 8 registered boxes are in the band");

            // The first eight are untouched by the ignored ninth.
            Assert.IsTrue(Resolve(new Vector3(4f, 1f, 4f), 0f, 10f, minima, maxima, 1,
                out id, out point, out normal, out height, out candidates));
            Assert.AreEqual(1, id);
            Assert.AreEqual(0.1f, height, Tolerance);
        }

        // --------------------------------------------------------------- 14 ----
        [Test]
        public void NoSurfaceIsEverReportedWithoutTheGround()
        {
            Vector3[] minima = NoBoxes();
            Vector3[] maxima = NoBoxes();
            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;

            // The ground is outside the band (reference below it, zero limit) and is
            // still reported: it is the fallback, not a band candidate.
            Assert.IsTrue(Resolve(new Vector3(0f, 1f, 0f), -10f, 0f,
                minima, maxima, 0, out id, out point, out normal, out height, out candidates));
            Assert.AreEqual(SupportSurfaceMath.GroundSurfaceId, id);
            Assert.AreEqual(0f, height, Tolerance);
            Assert.AreEqual(0, candidates);

            // A box whose top is below the ground never wins.
            Vector3[] belowMinima = { new Vector3(-1f, -2f, -1f) };
            Vector3[] belowMaxima = { new Vector3(1f, -0.5f, 1f) };
            Assert.IsTrue(Resolve(new Vector3(0f, 1f, 0f), 0f, 1f,
                belowMinima, belowMaxima, 1, out id, out point, out normal, out height,
                out candidates));
            Assert.AreEqual(SupportSurfaceMath.GroundSurfaceId, id);
            Assert.AreEqual(0f, height, Tolerance);

            // A ground offset is honoured, so the fallback is the real floor.
            Assert.IsTrue(SupportSurfaceMath.TryResolveSupport(new Vector3(0f, 1f, 0f), 0f, 0.3f,
                Up, -0.05f, minima, maxima, 0, out id, out point, out normal, out height,
                out candidates));
            Assert.AreEqual(-0.05f, height, Tolerance);
        }

        // --------------------------------------------------------------- 15 ----
        [Test]
        public void WalkingOffTheLedgeStartsAFall()
        {
            bool dropped;
            Assert.IsTrue(SupportSurfaceMath.SupportDroppedAway(0f, 0.26f, 0.001f, out dropped));
            Assert.IsTrue(dropped, "0.26 m down is a lost surface");
            Assert.IsTrue(SupportSurfaceMath.SupportDroppedAway(0.26f, 0.26f, 0.001f, out dropped));
            Assert.IsFalse(dropped, "same surface height is not a drop");
            Assert.IsTrue(SupportSurfaceMath.SupportDroppedAway(0.2595f, 0.26f, 0.001f, out dropped));
            Assert.IsFalse(dropped, "sub-millimetre noise is not a ledge");

            // A drop starts a fall even before the ground solve has run.
            Assert.IsTrue(SupportSurfaceMath.ResolveFallState(false, true, false, false));
            Assert.IsFalse(SupportSurfaceMath.ResolveFallState(false, false, true, false));
            // Falling continues while the step is drop-clamped (has not arrived).
            Assert.IsTrue(SupportSurfaceMath.ResolveFallState(true, false, true, true));
            // Arriving at the surface ends the fall.
            Assert.IsFalse(SupportSurfaceMath.ResolveFallState(true, false, true, false));
            // A failed solve keeps the state instead of claiming a landing.
            Assert.IsTrue(SupportSurfaceMath.ResolveFallState(true, false, false, false));
        }

        // --------------------------------------------------------------- 16 ----
        [Test]
        public void TheFallFollowsGravityAndLandsOnTheLowerSurface()
        {
            const float fixedStep = 0.02f;
            const float gravity = 9.81f;
            const float maxFallSpeed = 40f;

            // Walking off a 0.26 m ledge: the lower surface is the ground at 0.
            float ballBottom = 0.26f;
            float fallSpeed = 0f;
            bool falling = SupportSurfaceMath.ResolveFallState(false, true, false, false);
            int steps = 0;
            while (falling && steps < 1000)
            {
                float nextSpeed;
                float budget;
                Assert.IsTrue(SupportSurfaceMath.TryIntegrateFall(fallSpeed, gravity,
                    fixedStep, maxFallSpeed, out nextSpeed, out budget));
                fallSpeed = nextSpeed;
                float requested = -ballBottom;
                float correction = Mathf.Clamp(requested, -budget, budget);
                bool dropClamped = requested < -budget;
                ballBottom = ballBottom + correction;
                falling = SupportSurfaceMath.ResolveFallState(falling, false, true, dropClamped);
                steps = steps + 1;
                Assert.Less(steps, 1000, "a 0.26 m fall must terminate");
            }

            Assert.IsFalse(falling);
            Assert.AreEqual(0f, ballBottom, Tolerance, "it lands exactly on the ground");
            Assert.Greater(steps, 1, "gravity takes more than one step for 0.26 m");
            // Semi-implicit Euler, hand-derived: after k steps the drop is
            // g dt^2 k(k+1)/2 and the speed is g dt k. 0.26 m needs k = 12
            // (0.24 s, 0.306 m) or k = 11 (0.22 s, 0.246 m) if it had arrived; the
            // clamp ends it when the remaining gap fits inside one budget.
            Assert.AreEqual(12, steps);
            Assert.AreEqual(0f, ballBottom, Tolerance);

            // One more step with no support loss stays grounded: no bounce, no drift.
            Assert.IsFalse(SupportSurfaceMath.ResolveFallState(false, false, true, false));
        }

        // --------------------------------------------------------------- 17 ----
        [Test]
        public void TheFallIsNotLimitedByTheDropPerStepClamp()
        {
            const float fixedStep = 0.02f;
            const float gravity = 9.81f;
            const float maxFallSpeed = 40f;
            const float maxDropPerStep = 0.1f;

            // A 20 m drop: after 1.5 s free fall the ball has covered ~11 m, which
            // is impossible under the old 0.1 m-per-step clamp (7.5 m in 75 steps).
            float ballBottom = 20f;
            float fallSpeed = 0f;
            float fastestStep = 0f;
            for (int i = 0; i < 75; i++)
            {
                float nextSpeed;
                float budget;
                Assert.IsTrue(SupportSurfaceMath.TryIntegrateFall(fallSpeed, gravity,
                    fixedStep, maxFallSpeed, out nextSpeed, out budget));
                fallSpeed = nextSpeed;
                if (budget > fastestStep) fastestStep = budget;
                ballBottom = ballBottom - budget;
            }

            Assert.Greater(fastestStep, maxDropPerStep,
                "a fast fall must move further per step than the old drop clamp allows");
            Assert.Greater(20f - ballBottom, 75f * maxDropPerStep,
                "the total fall is not capped at maxDropPerStep per step");
            // v = g t = 9.81 * 1.5 = 14.715 m/s; the guard is 40, so it never bites.
            Assert.AreEqual(gravity * 75f * fixedStep, fallSpeed, 1e-3f);
            Assert.Less(fallSpeed, maxFallSpeed);
        }

        // --------------------------------------------------------------- 18 ----
        [Test]
        public void StepCrossingIgnoresNumericalNoise()
        {
            // The minimum-step de-noise is NOT the map's 0.05 m classification gate:
            // a 0.26 m bed crosses, a 4 mm lip is silently climbed with the normal cap.
            Assert.IsTrue(SupportSurfaceMath.IsStepCrossing(0.26f, 0.005f));
            Assert.IsFalse(SupportSurfaceMath.IsStepCrossing(0.004f, 0.005f));
            Assert.IsFalse(SupportSurfaceMath.IsStepCrossing(0.005f, 0.005f),
                "the threshold is exclusive");
            Assert.IsFalse(SupportSurfaceMath.IsStepCrossing(float.NaN, 0.005f));
        }
    }
}
