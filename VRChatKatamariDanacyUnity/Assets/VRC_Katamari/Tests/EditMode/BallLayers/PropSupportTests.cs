using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ball_terrain_support/test_prop_support.py (P2 of
    /// Docs/Design/BallMotor/TERRAIN_SUPPORT.md). Lengths are metres in world space
    /// (+Y up). Expected values are hand calculations or geometric invariants, never
    /// a re-run of the implementation formula.
    ///
    /// The assembly layer (KatamariBallMotor) cannot be instantiated in EditMode, so
    /// every case exercises the pure decision SupportSurfaceMath owns: the prop role
    /// (support vs wall vs food), the capacity predicate, the prop tops entering the
    /// support table with a stable prop surface id, and the fall that starts when a
    /// prop the ball is standing on is absorbed.
    /// </summary>
    public class PropSupportTests
    {
        private const float Tolerance = 1e-5f;
        private const float StepRatio = 0.25f;

        private static readonly Vector3 Up = new Vector3(0f, 1f, 0f);

        private static Vector3[] Empty()
        {
            return new Vector3[0];
        }

        private static float StepLimit(float diameter)
        {
            float limit;
            Assert.IsTrue(SupportSurfaceMath.TryStepHeightLimit(diameter, StepRatio, out limit));
            return limit;
        }

        private static int Role(bool beyondAbsorbLimit, float topY, float supportHeight,
            float stepLimit, bool alreadyAbove)
        {
            int role;
            Assert.IsTrue(SupportSurfaceMath.TryClassifyProp(beyondAbsorbLimit, topY,
                supportHeight, stepLimit, alreadyAbove, out role));
            return role;
        }

        private static bool Resolve(Vector3 probe, float referenceHeight, float stepLimit,
            Vector3[] boxMinima, Vector3[] boxMaxima, int boxCount,
            Vector3[] propMinima, Vector3[] propMaxima, int propCount,
            out int surfaceId, out Vector3 point, out Vector3 normal, out float height,
            out int candidateCount)
        {
            return SupportSurfaceMath.TryResolveSupport(probe, referenceHeight, stepLimit,
                Up, 0f, boxMinima, boxMaxima, boxCount, propMinima, propMaxima, propCount,
                out surfaceId, out point, out normal, out height, out candidateCount);
        }

        // ---------------------------------------------------------------- 1 ----
        [Test]
        public void AnAbsorbProofLowPropIsASupportNotAWall()
        {
            // The C14 flat sheet: 0.028 m top, 0.30 m ball -> stepLimit 0.075. (A real
            // napkin is 0.014 m and D_req ~= 0.18, so at 0.30 it is food, not a step:
            // the absorb gate answers first.)
            Assert.AreEqual(SupportSurfaceMath.PropRoleSupport,
                Role(true, 0.028f, 0f, StepLimit(0.30f), false));
        }

        // ---------------------------------------------------------------- 2 ----
        [Test]
        public void AnAbsorbProofPropExactlyAtTheStepLimitIsASupport()
        {
            // The boundary is inclusive. Exactly representable numbers keep the case a
            // contract test rather than a float-rounding test; the picnic corner's
            // 0.35 m chair -> 0.70 m table chain (D = 1.40, rise == 0.35) is the
            // production shape of the same equality.
            float limit = StepLimit(1f);
            Assert.AreEqual(0.25f, limit, Tolerance);
            Assert.AreEqual(SupportSurfaceMath.PropRoleSupport,
                Role(true, 0.5f, 0.25f, limit, false));
        }

        // ---------------------------------------------------------------- 3 ----
        [Test]
        public void OneMillimetreAboveTheLimitIsAWall()
        {
            float limit = StepLimit(0.30f);
            Assert.AreEqual(SupportSurfaceMath.PropRoleBlocking,
                Role(true, limit + 0.001f, 0f, limit, false));
        }

        // ---------------------------------------------------------------- 4 ----
        [Test]
        public void TheSamePropFlipsToSupportWhenTheBallGrows()
        {
            // A 0.30 m tall ABSORB-PROOF prop: a wall for a 0.30 m ball (limit 0.075),
            // a step for a 1.20 m ball (limit 0.30, equality again). The real cooler
            // becomes food at D ~= 1.03, so its own window is empty (design rule 9);
            // this case pins the geometric branch, not that prop.
            Assert.AreEqual(SupportSurfaceMath.PropRoleBlocking,
                Role(true, 0.30f, 0f, StepLimit(0.30f), false));
            Assert.AreEqual(SupportSurfaceMath.PropRoleSupport,
                Role(true, 0.30f, 0f, StepLimit(1.20f), false));
        }

        // ---------------------------------------------------------------- 5 ----
        [Test]
        public void AnAbsorbablePropIsNeitherAWallNorASupport()
        {
            // The absorb gate opened: the same geometry is now food, not a step.
            Assert.AreEqual(SupportSurfaceMath.PropRoleNone,
                Role(false, 0.028f, 0f, StepLimit(0.30f), false));
            Assert.AreEqual(SupportSurfaceMath.PropRoleNone,
                Role(false, 1.8f, 0f, StepLimit(0.30f), false));
        }

        // ---------------------------------------------------------------- 6 ----
        [Test]
        public void ABallAlreadyAboveTheTopNeverGetsAWall()
        {
            // Descending onto a 1 m crate from a table: the ball bottom is above the
            // top, so the landing surface must not be classified as a wall.
            Assert.AreEqual(SupportSurfaceMath.PropRoleSupport,
                Role(true, 1f, 0f, StepLimit(0.30f), true));
        }

        // ---------------------------------------------------------------- 7 ----
        [Test]
        public void APropTopEntersTheSupportTableWithAPropSurfaceId()
        {
            Vector3[] propMinima = { new Vector3(-0.5f, 0f, -0.5f) };
            Vector3[] propMaxima = { new Vector3(0.5f, 0.35f, 0.5f) };
            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;

            Assert.IsTrue(Resolve(new Vector3(0f, 0.9f, 0f), 0.35f, StepLimit(0.35f),
                Empty(), Empty(), 0, propMinima, propMaxima, 1,
                out id, out point, out normal, out height, out candidates));
            Assert.AreEqual(SupportSurfaceMath.FirstPropSurfaceId, id);
            Assert.IsTrue(SupportSurfaceMath.IsPropSurfaceId(id));
            Assert.AreEqual(0, SupportSurfaceMath.PropSurfaceIndex(id));
            Assert.AreEqual(0.35f, height, Tolerance);
            Assert.AreEqual(new Vector3(0f, 0.35f, 0f), point);
            Assert.AreEqual(Up, normal);
            // Ground and the prop top are both inside the band.
            Assert.AreEqual(2, candidates);
        }

        // ---------------------------------------------------------------- 8 ----
        [Test]
        public void APropFootprintEdgeIsInclusive()
        {
            Vector3[] propMinima = { new Vector3(0f, 0f, 0f) };
            Vector3[] propMaxima = { new Vector3(1f, 0.2f, 1f) };
            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;

            Assert.IsTrue(Resolve(new Vector3(1f, 1f, 0f), 0f, 0.275f, Empty(), Empty(), 0,
                propMinima, propMaxima, 1, out id, out point, out normal, out height,
                out candidates));
            Assert.AreEqual(SupportSurfaceMath.FirstPropSurfaceId, id);
            Assert.IsTrue(Resolve(new Vector3(0f, 1f, 1f), 0f, 0.275f, Empty(), Empty(), 0,
                propMinima, propMaxima, 1, out id, out point, out normal, out height,
                out candidates));
            Assert.AreEqual(SupportSurfaceMath.FirstPropSurfaceId, id);
        }

        // ---------------------------------------------------------------- 9 ----
        [Test]
        public void APropOutsideItsFootprintFallsBackToTheGround()
        {
            Vector3[] propMinima = { new Vector3(0f, 0f, 0f) };
            Vector3[] propMaxima = { new Vector3(1f, 0.2f, 1f) };
            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;

            Assert.IsTrue(Resolve(new Vector3(1.0001f, 1f, 0f), 0f, 0.275f, Empty(), Empty(),
                0, propMinima, propMaxima, 1, out id, out point, out normal, out height,
                out candidates));
            Assert.AreEqual(SupportSurfaceMath.GroundSurfaceId, id);
            Assert.AreEqual(0f, height, Tolerance);
        }

        // --------------------------------------------------------------- 10 ----
        [Test]
        public void APropAboveTheStepBandIsNotACandidate()
        {
            Vector3[] propMinima = { new Vector3(-1f, 0f, -1f) };
            Vector3[] propMaxima = { new Vector3(1f, 1f, 1f) };
            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;

            Assert.IsTrue(Resolve(new Vector3(0f, 0.5f, 0f), 0f, StepLimit(1.10f),
                Empty(), Empty(), 0, propMinima, propMaxima, 1,
                out id, out point, out normal, out height, out candidates));
            Assert.AreEqual(SupportSurfaceMath.GroundSurfaceId, id);
            Assert.AreEqual(0f, height, Tolerance);
            Assert.AreEqual(1, candidates, "only the ground is inside the band");
        }

        // --------------------------------------------------------------- 11 ----
        [Test]
        public void ABoxAndAPropAtTheSameHeightKeepTheBox()
        {
            Vector3[] boxMinima = { new Vector3(-1f, 0f, -1f) };
            Vector3[] boxMaxima = { new Vector3(1f, 0.2f, 1f) };
            Vector3[] propMinima = { new Vector3(-0.5f, 0f, -0.5f) };
            Vector3[] propMaxima = { new Vector3(0.5f, 0.2f, 0.5f) };
            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;

            Assert.IsTrue(Resolve(new Vector3(0f, 1f, 0f), 0f, 0.4f, boxMinima, boxMaxima, 1,
                propMinima, propMaxima, 1, out id, out point, out normal, out height,
                out candidates));
            Assert.AreEqual(1, id, "a tie keeps the earlier reader (the static box)");

            Vector3[] tallerPropMaxima = { new Vector3(0.5f, 0.3f, 0.5f) };
            Assert.IsTrue(Resolve(new Vector3(0f, 1f, 0f), 0f, 0.4f, boxMinima, boxMaxima, 1,
                propMinima, tallerPropMaxima, 1, out id, out point, out normal, out height,
                out candidates));
            Assert.AreEqual(SupportSurfaceMath.FirstPropSurfaceId, id);
            Assert.AreEqual(0.3f, height, Tolerance);
        }

        // --------------------------------------------------------------- 12 ----
        [Test]
        public void SupportSelectionReadsExactlyTheReportedPropCount()
        {
            // Six registered supports and a seventh array entry that would win if it
            // were read. The query reads exactly propCount; capacity lives in one
            // predicate.
            Vector3[] propMinima = new Vector3[7];
            Vector3[] propMaxima = new Vector3[7];
            for (int i = 0; i < 7; i++)
            {
                propMinima[i] = new Vector3(-1f, 0f, -1f);
                propMaxima[i] = new Vector3(1f, 0.01f * (float)(i + 1), 1f);
            }
            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;

            Assert.IsTrue(Resolve(new Vector3(0f, 1f, 0f), 0f, 0.5f, Empty(), Empty(), 0,
                propMinima, propMaxima, SupportSurfaceMath.MaxSupportPropCount,
                out id, out point, out normal, out height, out candidates));
            Assert.AreEqual(SupportSurfaceMath.FirstPropSurfaceId + 5, id,
                "the sixth prop is the highest readable one");
            Assert.AreEqual(0.06f, height, Tolerance);

            Assert.IsTrue(SupportSurfaceMath.IsSupportPropCountAllowed(0));
            Assert.IsTrue(SupportSurfaceMath.IsSupportPropCountAllowed(6));
            Assert.IsFalse(SupportSurfaceMath.IsSupportPropCountAllowed(7));
            Assert.IsFalse(SupportSurfaceMath.IsSupportPropCountAllowed(-1));
            Assert.AreEqual(6, SupportSurfaceMath.MaxSupportPropCount);
        }

        // --------------------------------------------------------------- 13 ----
        [Test]
        public void AnAbsorbedSupportPropStartsTheGravityFall()
        {
            Vector3[] propMinima = { new Vector3(-0.5f, 0f, -0.5f) };
            Vector3[] propMaxima = { new Vector3(0.5f, 0.35f, 0.5f) };
            int id;
            Vector3 point;
            Vector3 normal;
            float height;
            int candidates;

            // 1. The ball stands on the prop top.
            Assert.IsTrue(Resolve(new Vector3(0f, 0.9f, 0f), 0.35f, StepLimit(0.35f),
                Empty(), Empty(), 0, propMinima, propMaxima, 1,
                out id, out point, out normal, out height, out candidates));
            Assert.AreEqual(SupportSurfaceMath.FirstPropSurfaceId, id);
            float previousSupportHeight = height;

            // 2. The prop is absorbed: the host's next scan sends an empty table.
            Assert.IsTrue(Resolve(new Vector3(0f, 0.9f, 0f), previousSupportHeight,
                StepLimit(0.35f), Empty(), Empty(), 0, Empty(), Empty(), 0,
                out id, out point, out normal, out height, out candidates));
            Assert.AreEqual(SupportSurfaceMath.GroundSurfaceId, id);
            Assert.AreEqual(0f, height, Tolerance);

            // 3. That drop starts the P1 fall; there is no second fall rule.
            bool dropped;
            Assert.IsTrue(SupportSurfaceMath.SupportDroppedAway(height, previousSupportHeight,
                0.01f, out dropped));
            Assert.IsTrue(dropped);
            Assert.IsTrue(SupportSurfaceMath.ResolveFallState(false, dropped, false, false));

            float fallSpeed;
            float fallBudget;
            Assert.IsTrue(SupportSurfaceMath.TryIntegrateFall(0f, 9.81f, 0.02f, 40f,
                out fallSpeed, out fallBudget));
            Assert.Greater(fallSpeed, 0f);
            Assert.Greater(fallBudget, 0f);
            Assert.Less(fallBudget, previousSupportHeight,
                "one gravity step does not teleport the ball to the floor");
        }

        // --------------------------------------------------------------- 14 ----
        [Test]
        public void AnEmptyPropTableLeavesThePreP2Surface()
        {
            // The feature-off shape: the motor passes propCount = 0 even when the host
            // still has a set registered. The query must then be the P1 query.
            Vector3[] boxMinima = { new Vector3(-1f, 0f, -1f) };
            Vector3[] boxMaxima = { new Vector3(1f, 0.2f, 1f) };
            Vector3[] propMinima = { new Vector3(-1f, 0f, -1f) };
            Vector3[] propMaxima = { new Vector3(1f, 0.15f, 1f) };
            int idOn;
            Vector3 pointOn;
            Vector3 normalOn;
            float heightOn;
            int candidatesOn;
            int idOff;
            Vector3 pointOff;
            Vector3 normalOff;
            float heightOff;
            int candidatesOff;

            // Exactly what the motor does when terrainSupportEnabled is false.
            Assert.IsTrue(Resolve(new Vector3(0f, 1f, 0f), 0f, 0.4f, boxMinima, boxMaxima, 1,
                propMinima, propMaxima, 0,
                out idOn, out pointOn, out normalOn, out heightOn, out candidatesOn));
            Assert.IsTrue(Resolve(new Vector3(0f, 1f, 0f), 0f, 0.4f, boxMinima, boxMaxima, 1,
                Empty(), Empty(), 0,
                out idOff, out pointOff, out normalOff, out heightOff, out candidatesOff));

            Assert.AreEqual(idOff, idOn);
            Assert.AreEqual(heightOff, heightOn, 0f);
            Assert.AreEqual(pointOff, pointOn);
            Assert.AreEqual(normalOff, normalOn);
            Assert.AreEqual(candidatesOff, candidatesOn);
            Assert.AreEqual(1, idOn, "the static box still wins");
            Assert.AreEqual(0.2f, heightOn, Tolerance);
        }

        // --------------------------------------------------------------- 15 ----
        [Test]
        public void NonFinitePropIsRefusedAndNothingIsWritten()
        {
            Vector3[] badMinima = { new Vector3(float.NaN, 0f, 0f) };
            Vector3[] propMaxima = { new Vector3(1f, 0.2f, 1f) };
            int id = 99;
            Vector3 point = new Vector3(9f, 9f, 9f);
            Vector3 normal = new Vector3(9f, 9f, 9f);
            float height = 99f;
            int candidates = 99;

            Assert.IsFalse(Resolve(new Vector3(0f, 1f, 0f), 0f, 0.3f, Empty(), Empty(), 0,
                badMinima, propMaxima, 1, out id, out point, out normal, out height,
                out candidates));
            Assert.AreEqual(SupportSurfaceMath.NoSurfaceId, id);
            Assert.AreEqual(Vector3.zero, point);
            Assert.AreEqual(Vector3.zero, normal);
            Assert.AreEqual(0f, height, Tolerance);
            Assert.AreEqual(0, candidates);

            // Inverted prop bounds are refused up front, not skipped.
            Vector3[] validMinima = { new Vector3(0f, 0f, 0f) };
            Vector3[] invertedMaxima = { new Vector3(-1f, 0.2f, 1f) };
            id = 99;
            height = 99f;
            Assert.IsFalse(Resolve(new Vector3(0f, 1f, 0f), 0f, 0.3f, Empty(), Empty(), 0,
                validMinima, invertedMaxima, 1, out id, out point, out normal, out height,
                out candidates));
            Assert.AreEqual(SupportSurfaceMath.NoSurfaceId, id);
            Assert.AreEqual(0f, height, Tolerance);
        }
    }
}
