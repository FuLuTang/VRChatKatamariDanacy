using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins which item quantity the absorb gate and the contact reach read: the item's OWN
    /// three edge lengths (the original's per-type `a, b, c`), never the axis-aligned box
    /// of the item as it is turned in the world.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate is `floor(1000*D) >= floor(1000*cbrt(60*k*a*b*c/pi))`. A turned item's
    /// world box is larger than the item, so feeding the gate the world box prices a
    /// toothbrush at 189 mm instead of 76 mm (reference level, one real row). Every case
    /// here passes only the item's own size, as the host does; the world box of a turned
    /// item appears only as the wrong number the contract removed.
    /// </para>
    /// <para>
    /// The reference numbers are literals copied from the reference level's placement data
    /// (`aabb` = the item's own size, `absorbDiameterReqMm` = the data's requirement, and
    /// the world box of one real orientation computed from its `rotationQuat`); the test
    /// never reads that git-ignored file.
    /// </para>
    /// </remarks>
    public class AbsorbItemSizeTests
    {
        private const float MaxGrowthRadiusM = 1000f;
        private const float K = PickupAbsorbMath.DefaultVolumeK;
        private static readonly string[] Allowed = new string[] { "stone" };

        // The item's world box when it is yawed by `yawDegrees` about +Y (the other two
        // edges keep their meaning): the closed form |cos|*a + |sin|*c along x, and
        // |sin|*a + |cos|*c along z.
        private static Vector3 YawedWorldBox(Vector3 size, float yawDegrees)
        {
            float radians = yawDegrees * Mathf.PI / 180f;
            float c = Mathf.Abs(Mathf.Cos(radians));
            float s = Mathf.Abs(Mathf.Sin(radians));
            return new Vector3(c * size.x + s * size.z, size.y, s * size.x + c * size.z);
        }

        private static float ReqMm(Vector3 size, float volumeK)
        {
            float reqMm;
            Assert.IsTrue(PickupAbsorbMath.TryComputePickupReqDiameterMm(size, volumeK,
                out reqMm));
            return reqMm;
        }

        // The item sits at the ball's centre, so contact never masks the size gate.
        private static AbsorbOutcome EvaluateAtBall(float ballDiameterM, Vector3 size,
            float volumeK)
        {
            return PickupAbsorbMath.EvaluateAbsorb(true, "stone", Allowed, Allowed.Length,
                size, Vector3.zero, Vector3.zero, ballDiameterM * 0.5f, 0f, volumeK,
                ballDiameterM, MaxGrowthRadiusM);
        }

        [Test]
        public void TurningAnItemDoesNotChangeItsRequirement()
        {
            Vector3 size = new Vector3(0.20f, 0.04f, 0.12f);
            float reqUnturned = ReqMm(size, K);
            float[] yaws = new float[] { 0f, 45f, 90f, 137f };
            for (int i = 0; i < yaws.Length; i++)
            {
                // The requirement is a function of the size alone.
                float reqMm;
                float ballMm;
                bool within;
                Assert.IsTrue(PickupAbsorbMath.TryResolveAbsorbGate(size, K,
                    (reqUnturned + 0.5f) / 1000f, out reqMm, out ballMm, out within));
                Assert.AreEqual(reqUnturned, reqMm, "yaw " + yaws[i]);
                Assert.IsTrue(within);

                // And the gate's verdict flips at exactly that millimetre: one mm above is
                // food, one mm below is too large. The turn never enters the call.
                Assert.AreEqual(AbsorbOutcome.Absorb,
                    EvaluateAtBall((reqUnturned + 0.5f) / 1000f, size, K),
                    "yaw " + yaws[i] + " at the requirement");
                Assert.AreEqual(AbsorbOutcome.RejectItemTooLarge,
                    EvaluateAtBall((reqUnturned - 0.5f) / 1000f, size, K),
                    "yaw " + yaws[i] + " one millimetre short");
            }
        }

        [Test]
        public void ThePriceOfTheWorldBoxIsTheBugThisContractRemoved()
        {
            // At 45 degrees the 0.20 x 0.12 footprint becomes 0.2263 x 0.2263 m: pricing
            // that box instead of the item's own size asks for a larger ball.
            Vector3 size = new Vector3(0.20f, 0.04f, 0.12f);
            Vector3 worldBox = YawedWorldBox(size, 45f);
            Assert.Greater(worldBox.x * worldBox.z, size.x * size.z);
            Assert.Greater(ReqMm(worldBox, K), ReqMm(size, K));

            // A ball that is exactly big enough for the item takes it even though the
            // world box alone would have refused it.
            float ballM = (ReqMm(size, K) + 0.5f) / 1000f;
            Assert.IsTrue(PickupAbsorbMath.IsBeyondAbsorbLimit(worldBox, K, ballM));
            Assert.AreEqual(AbsorbOutcome.Absorb, EvaluateAtBall(ballM, size, K));
        }

        [Test]
        public void TheReferenceLevelsRowsPriceExactlyAsTheirData()
        {
            // name, own size (placement `aabb`), k, data requirement (placement
            // `absorbDiameterReqMm`), the item's world box in one real orientation.
            Vector3[] sizes = new Vector3[]
            {
                new Vector3(0.0120f, 0.0155f, 0.1800f),
                new Vector3(0.2392f, 0.0097f, 0.1724f),
                new Vector3(0.1260f, 0.0169f, 0.2243f)
            };
            Vector3[] worldBoxes = new Vector3[]
            {
                new Vector3(0.1039f, 0.1636f, 0.0298f), // toothbrush_001
                new Vector3(0.1069f, 0.2392f, 0.1468f), // dish_rag_001
                new Vector3(0.2458f, 0.0675f, 0.2480f)  // scissors_001
            };
            float[] dataReqMm = new float[] { 76f, 174f, 185f };
            string[] names = new string[] { "toothbrush", "dish_rag", "scissors" };
            const float referenceK = 0.6998f;
            const float startBallM = 0.222f;

            for (int i = 0; i < sizes.Length; i++)
            {
                Assert.AreEqual(dataReqMm[i], ReqMm(sizes[i], referenceK), names[i]);

                // The world box of the very same item is a different (wrong) number: the
                // reason the 222 mm start ball could take only half of what the data says.
                Assert.Greater(ReqMm(worldBoxes[i], referenceK), dataReqMm[i],
                    names[i] + " world box");

                Assert.IsFalse(PickupAbsorbMath.IsBeyondAbsorbLimit(sizes[i], referenceK,
                    startBallM), names[i] + " is food at the start ball");
                Assert.AreEqual(AbsorbOutcome.Absorb,
                    EvaluateAtBall(startBallM, sizes[i], referenceK), names[i]);
            }
        }

        [Test]
        public void ReachIsTheCircumscribedSphereOfTheItemsOwnBoxWhateverItsTurn()
        {
            // The reach's item radius is the half-diagonal of the item's own box: the sphere
            // that contains the whole item in every orientation, so a turn cannot change it.
            // The axis-aligned box of a turned item has a larger half-diagonal; that is only
            // a loose bound caused by the rotation, and it is not an input.
            Vector3 size = new Vector3(0.20f, 0.04f, 0.12f);
            float itemRadius;
            float reach;
            Assert.IsTrue(PickupAbsorbMath.TryComputeReach(size, 0.1f, 0f, out itemRadius,
                out reach));
            Assert.AreEqual(Mathf.Sqrt(0.1f * 0.1f + 0.02f * 0.02f + 0.06f * 0.06f), itemRadius,
                1e-6f);
            Assert.AreEqual(0.1f + itemRadius, reach, 1e-6f);

            float[] yaws = new float[] { 0f, 30f, 45f, 90f };
            for (int i = 0; i < yaws.Length; i++)
            {
                Vector3 worldBox = YawedWorldBox(size, yaws[i]);
                float boxRadius;
                float boxReach;
                Assert.IsTrue(PickupAbsorbMath.TryComputeReach(worldBox, 0.1f, 0f,
                    out boxRadius, out boxReach));
                Assert.GreaterOrEqual(boxRadius, itemRadius - 1e-6f,
                    "the world box of yaw " + yaws[i] + " is never tighter than the item");
            }

            // The gate compares against exactly that reach: just inside absorbs, just
            // outside is out of reach, at every turn because no turn is an input.
            float ballDiameterM = 0.5f;
            Assert.AreEqual(AbsorbOutcome.Absorb, PickupAbsorbMath.EvaluateAbsorb(true, "stone",
                Allowed, Allowed.Length, size, new Vector3(reach - 0.001f, 0f, 0f),
                Vector3.zero, 0.1f, 0f, K, ballDiameterM, MaxGrowthRadiusM));
            Assert.AreEqual(AbsorbOutcome.RejectOutOfReach, PickupAbsorbMath.EvaluateAbsorb(
                true, "stone", Allowed, Allowed.Length, size,
                new Vector3(reach + 0.001f, 0f, 0f), Vector3.zero, 0.1f, 0f, K,
                ballDiameterM, MaxGrowthRadiusM));
        }

        [Test]
        public void AnIllegalSizeIsInvalidInputNotAWall()
        {
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(0.5f, new Vector3(-0.1f, 0.1f, 0.1f), K));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(0.5f, new Vector3(float.NaN, 0.1f, 0.1f), K));
            Assert.AreEqual(AbsorbOutcome.RejectInvalidInput,
                EvaluateAtBall(0.5f, new Vector3(1e20f, 1e20f, 1e20f), K));
        }
    }
}
