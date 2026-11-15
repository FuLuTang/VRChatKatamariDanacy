using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the order of the pickup gate: geometry first, then the ledger gain.
    /// </summary>
    /// <remarks>
    /// A value below one unit used to pass the geometry gate, take a claim, resolve to
    /// no commit row and disappear without a trace. <see cref="AbsorbOutcome.RejectNoGain"/>
    /// makes that violation visible, and these assertions keep the two concerns from
    /// trading places: a gain problem must never mask a geometry refusal, and a
    /// geometry refusal must never be reported as a gain problem.
    /// </remarks>
    public class RejectNoGainTests
    {
        private static readonly string[] Allowed = new string[] { "small", "medium", "large" };
        private static readonly Vector3 Ball = new Vector3(0f, 0.15f, 0f);
        private static readonly Vector3 InReach = new Vector3(0f, 0.05f, 0.10f);
        private static readonly Vector3 FarAway = new Vector3(0f, 0.05f, 5f);
        private static readonly Vector3 Bounds = new Vector3(0.10f, 0.10f, 0.10f);
        private const float CollisionRadiusM = 0.15f;
        private const float ContactRadiusM = 0f;
        // The size gate's limit is an AABB volume in cubic metres; 1 m^3 is far above
        // this 0.001 m^3 item's volume, so the size gate never decides these cases.
        private const float MaxItemVolumeM3 = 1f;
        private const float MaxGrowthRadiusM = 1.1f;

        private static AbsorbOutcome Evaluate(Vector3 position, float value)
        {
            return PickupAbsorbMath.EvaluateAbsorbWithValue(true, "small", Allowed, Allowed.Length,
                Bounds, position, Ball, CollisionRadiusM, ContactRadiusM, MaxItemVolumeM3,
                MaxGrowthRadiusM, value);
        }

        [Test]
        public void InReachButWorthLessThanOneUnitIsRefusedAsNoGain()
        {
            Assert.AreEqual(AbsorbOutcome.RejectNoGain, Evaluate(InReach, 0.3f));
        }

        [Test]
        public void InReachAndWorthExactlyOneUnitIsAbsorbed()
        {
            Assert.AreEqual(AbsorbOutcome.Absorb, Evaluate(InReach, 1f));
        }

        [Test]
        public void GeometryRefusalIsNotMaskedByTheGainCheck()
        {
            // Out of reach AND worth zero units: the geometry reason must win, so a
            // "no gain" verdict can never explain away a distance failure.
            Assert.AreEqual(AbsorbOutcome.RejectOutOfReach, Evaluate(FarAway, 0.3f));
            // Zero gain is also irrelevant when the kind is not allowed.
            AbsorbOutcome kindRefused = PickupAbsorbMath.EvaluateAbsorbWithValue(true,
                "forbidden", Allowed, Allowed.Length, Bounds, InReach, Ball, CollisionRadiusM,
                ContactRadiusM, MaxItemVolumeM3, MaxGrowthRadiusM, 0.3f);
            Assert.AreEqual(AbsorbOutcome.RejectKindNotAllowed, kindRefused);
        }
    }
}
