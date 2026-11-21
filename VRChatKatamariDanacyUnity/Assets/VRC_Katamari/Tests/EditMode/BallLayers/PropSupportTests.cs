using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/ball_terrain_support/test_prop_support.py (P2; see
    /// Docs/Design/BallMotor/README.md「关卡几何宿主 LevelArenaHost」节). Lengths are metres in world space
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

        private static float[] NoFloats()
        {
            return new float[0];
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

    }
}
