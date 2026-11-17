using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Riding;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the waist rule: whatever an avatar's proportions (any kappa), the seat that
    /// <see cref="RideWaistMath"/> and <see cref="SeatEyeMath"/> compute puts its hips on the top
    /// of the ball and, at the eye height <see cref="RideScaleMath"/> asks for, its head on the
    /// eye point; and when the hips cannot be read the seat falls back to aligning the eye only.
    /// </summary>
    /// <remarks>
    /// The scene is simulated with a yaw-only rig, the way the motor writes it: the rig root is
    /// the eye point (<c>up</c> above the ball centre), the avatar is a rigid body hanging off the
    /// seat whose head and hips offsets are proportional to its eye height (uniform scaling).
    /// </remarks>
    public class RideWaistTests
    {
        private const float NoLimit = RideWaistMath.UnlimitedMeters;

        // One simulated avatar: hips and head offsets from the seat, per metre of eye height, in
        // the rig frame. The head is kappa above the hips; the rest is whatever the avatar does.
        private sealed class Avatar
        {
            public Vector3 HipsPerEye;
            public Vector3 HeadPerEye;

            public static Avatar WithKappa(float kappa, float hipsHeight, float hipsForward)
            {
                var avatar = new Avatar();
                avatar.HipsPerEye = new Vector3(0.02f, hipsHeight, hipsForward);
                avatar.HeadPerEye = avatar.HipsPerEye + new Vector3(0.05f, kappa, 0.04f);
                return avatar;
            }
        }

        private sealed class Scene
        {
            public float BallY = 1.5f;
            public float Radius;
            public float RigY;

            public Scene(float diameter)
            {
                Radius = diameter * 0.5f;
                float up;
                float back;
                Assert.IsTrue(RideViewMath.TryEyeOffset(Radius, out up, out back));
                RigY = BallY + up;
            }

            public float BallTopY { get { return BallY + Radius; } }

            public float WaistTarget()
            {
                return RideWaistMath.WaistTargetInRig(BallY, Radius, RigY);
            }
        }

        // Works in the rig frame (the rig carries yaw only, so heights are world heights): the
        // avatar is measured at eye height E0 (its offsets from the seat), then lets it grow to
        // E and returns, relative to the rig root, where the hips and the head end up.
        private static void Ride(Scene scene, Avatar avatar, float measuredEyeHeight,
            float eyeHeight, bool useWaist, out Vector3 hipsFromRig, out Vector3 headFromRig,
            out float kappa)
        {
            Vector3 headInRig = avatar.HeadPerEye * measuredEyeHeight;
            Vector3 hipsInRig = avatar.HipsPerEye * measuredEyeHeight;

            kappa = 0f;
            bool hasWaist = false;
            int reason;
            if (useWaist)
            {
                hasWaist = RideWaistMath.TryMeasureKappa(headInRig, hipsInRig, measuredEyeHeight,
                    out kappa, out reason);
                Assert.IsTrue(hasWaist, "reason " + reason);
            }

            // The size changes; the offsets scale by the ratio.
            float factor = eyeHeight / measuredEyeHeight;
            Vector3 solveFor = RideWaistMath.EffectiveHeadInRig(headInRig * factor,
                hipsInRig * factor, hasWaist, scene.WaistTarget());
            Vector3 seatOffset;
            float uncompensated;
            float residual;
            Assert.IsTrue(SeatEyeMath.TryComputeSeatOffset(solveFor, Vector3.zero, NoLimit, NoLimit,
                false, 0f, 0f, true, out seatOffset, out uncompensated, out residual, out reason),
                "reason " + reason);

            headFromRig = seatOffset + headInRig * factor;
            hipsFromRig = seatOffset + hipsInRig * factor;
        }

        [Test]
        public void WaistTarget_IsTheBallTopRelativeToTheRig()
        {
            // Ball centre 1.5, radius 0.5, rig 1.2 above... rig at y = 4.0: the top (2.0) is 2.0 below.
            Assert.AreEqual(-2f, RideWaistMath.WaistTargetInRig(1.5f, 0.5f, 4f), 1e-6f);
        }

        // Different avatars (kappa), sizes and yaws: the hips are on the ball's top and the head
        // is on the eye point.
        [TestCase(0.2f, 0.222f)]
        [TestCase(0.35f, 0.222f)]
        [TestCase(0.5f, 0.222f)]
        [TestCase(0.2f, 0.8f)]
        [TestCase(0.35f, 0.8f)]
        [TestCase(0.5f, 0.8f)]
        [TestCase(0.2f, 2f)]
        [TestCase(0.35f, 2f)]
        [TestCase(0.5f, 2f)]
        [TestCase(0.35f, 20f)]
        public void Hips_LandOnTheBallTop_ForAnyKappa(float kappaOfAvatar, float diameter)
        {
            Avatar avatar = Avatar.WithKappa(kappaOfAvatar, 0.12f, 0.1f);
            var scene = new Scene(diameter);
            const float MeasuredEye = 1.6f;

            // The measurement, then the eye height RideScaleMath asks for with that kappa.
            Vector3 hipsFromRig;
            Vector3 headFromRig;
            float kappa;
            Ride(scene, avatar, MeasuredEye, MeasuredEye, true, out hipsFromRig, out headFromRig,
                out kappa);
            Assert.AreEqual(kappaOfAvatar, kappa, 1e-5f);

            float eye;
            bool clamped;
            Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(diameter, kappa, out eye, out clamped));
            Assert.IsFalse(clamped);

            Ride(scene, avatar, MeasuredEye, eye, true, out hipsFromRig, out headFromRig, out kappa);
            Assert.AreEqual(scene.BallTopY, scene.RigY + hipsFromRig.y, diameter * 1e-3f);
            // The view rule still holds: the head is on the eye point.
            Assert.AreEqual(0f, headFromRig.magnitude, diameter * 2e-3f);
            // The head is above the hips, the eye above the ball's top.
            Assert.Greater(headFromRig.y, hipsFromRig.y);
        }

        [Test]
        public void Hips_StayOnTheBallTop_WhenTheEyeHeightLagsTheBall()
        {
            // The rider is still small (eye height 0.5) while the ball is 2 m: the waist stays on
            // the ball, the head ends up below the eye point.
            Avatar avatar = Avatar.WithKappa(0.35f, 0.12f, 0.1f);
            var scene = new Scene(2f);
            Vector3 hipsFromRig;
            Vector3 headFromRig;
            float kappa;
            Ride(scene, avatar, 1.6f, 0.5f, true, out hipsFromRig, out headFromRig, out kappa);
            Assert.AreEqual(scene.BallTopY, scene.RigY + hipsFromRig.y, 2e-3f);
            Assert.AreEqual(0.35f * 0.5f - 0.779558f, headFromRig.y, 2e-3f);
        }

        [Test]
        public void Hips_StayOnTheBallTop_WhenTheEyeHeightIsClamped()
        {
            // A 0.05 m ball asks for 0.0557 m with kappa 0.35, but the platform floor is 0.1 m:
            // the rider is bigger than the rule, the waist still sits on the ball's top and the
            // eye is a little above the eye point.
            Avatar avatar = Avatar.WithKappa(0.35f, 0.12f, 0.1f);
            var scene = new Scene(0.05f);
            float eye;
            bool clamped;
            Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(0.05f, 0.35f, out eye, out clamped));
            Assert.IsTrue(clamped);
            Assert.AreEqual(0.1f, eye, 1e-6f);

            Vector3 hipsFromRig;
            Vector3 headFromRig;
            float kappa;
            Ride(scene, avatar, 1.6f, eye, true, out hipsFromRig, out headFromRig, out kappa);
            Assert.AreEqual(scene.BallTopY, scene.RigY + hipsFromRig.y, 1e-4f);
            // kappa * 0.1 = 0.035 m above the hips, against 0.0195 m eye-above-top the rule wants.
            Assert.AreEqual(0.035f - 0.0195f, headFromRig.y, 5e-4f);
        }

        [Test]
        public void NoUsableHips_FallBackToAligningTheEyeOnly()
        {
            // useWaist false: the hips are never consulted; the seat is the eye-only solution and
            // the head lands on the eye point at the size the avatar has, with no proportion
            // assumed.
            Avatar avatar = Avatar.WithKappa(0.35f, 0.12f, 0.1f);
            var scene = new Scene(0.5f);
            Vector3 hipsFromRig;
            Vector3 headFromRig;
            float kappa;
            Ride(scene, avatar, 1.6f, 1.6f, false, out hipsFromRig, out headFromRig, out kappa);
            Assert.AreEqual(0f, kappa);
            Assert.AreEqual(0f, headFromRig.magnitude, 1e-4f);
        }

        [Test]
        public void EffectiveHead_IsTheHeadWithoutAWaist_AndIgnoresTheHips()
        {
            var head = new Vector3(0.1f, 0.6f, -0.2f);
            Vector3 solved = RideWaistMath.EffectiveHeadInRig(head, new Vector3(9f, 9f, 9f), false,
                -3f);
            Assert.AreEqual(head, solved);
        }

        [Test]
        public void EffectiveHead_WithAWaistTakesTheHeightFromTheHips()
        {
            var head = new Vector3(0.1f, 0.6f, -0.2f);
            var hips = new Vector3(0.3f, 0.2f, 0.4f);
            // The solution is the negation: offset.y = target - hips.y = -3 - 0.2.
            Vector3 solved = RideWaistMath.EffectiveHeadInRig(head, hips, true, -3f);
            Assert.AreEqual(0.1f, solved.x, 1e-6f);
            Assert.AreEqual(0.2f + 3f, solved.y, 1e-6f);
            Assert.AreEqual(-0.2f, solved.z, 1e-6f);
        }

        [Test]
        public void BonePosition_ZeroOrBadIsUnusable()
        {
            Assert.IsFalse(RideWaistMath.IsUsableBonePosition(Vector3.zero));
            Assert.IsFalse(RideWaistMath.IsUsableBonePosition(new Vector3(float.NaN, 1f, 1f)));
            Assert.IsFalse(RideWaistMath.IsUsableBonePosition(
                new Vector3(1f, float.PositiveInfinity, 1f)));
            Assert.IsTrue(RideWaistMath.IsUsableBonePosition(new Vector3(0f, 0.9f, 0f)));
            Assert.IsTrue(RideWaistMath.IsUsableBonePosition(new Vector3(2f, 0f, 0f)));
        }

        [Test]
        public void Kappa_IsTheHeadAboveTheHipsPerMetreOfEyeHeight()
        {
            float kappa;
            int reason;
            Assert.IsTrue(RideWaistMath.TryMeasureKappa(new Vector3(0.1f, 0.7f, 0f),
                new Vector3(-0.3f, 0.14f, 0.5f), 1.6f, out kappa, out reason));
            // (0.7 - 0.14) / 1.6 = 0.35
            Assert.AreEqual(0.35f, kappa, 1e-6f);
            Assert.AreEqual(RideWaistMath.ReasonOk, reason);
        }

        [Test]
        public void Kappa_RefusesImplausibleOrMissingData()
        {
            float kappa;
            int reason;
            // Hips above the head, or the head's own height (not a waist), or further than a whole
            // eye height below it.
            Assert.IsFalse(RideWaistMath.TryMeasureKappa(new Vector3(0f, 0.4f, 0f),
                new Vector3(0f, 0.6f, 0f), 1.6f, out kappa, out reason));
            Assert.AreEqual(RideWaistMath.ReasonKappaOutOfBand, reason);
            Assert.AreEqual(0f, kappa);
            Assert.IsFalse(RideWaistMath.TryMeasureKappa(new Vector3(0f, 0.7f, 0f),
                new Vector3(0f, 0.69f, 0f), 1.6f, out kappa, out reason));
            Assert.AreEqual(RideWaistMath.ReasonKappaOutOfBand, reason);
            Assert.IsFalse(RideWaistMath.TryMeasureKappa(new Vector3(0f, 0.7f, 0f),
                new Vector3(0f, -1.2f, 0f), 1.6f, out kappa, out reason));
            Assert.AreEqual(RideWaistMath.ReasonKappaOutOfBand, reason);
            // No eye height to divide by (the world does not scale the avatar).
            Assert.IsFalse(RideWaistMath.TryMeasureKappa(new Vector3(0f, 0.7f, 0f),
                new Vector3(0f, 0.14f, 0f), 0f, out kappa, out reason));
            Assert.AreEqual(RideWaistMath.ReasonNoEyeHeight, reason);
            // Not numbers.
            Assert.IsFalse(RideWaistMath.TryMeasureKappa(new Vector3(0f, float.NaN, 0f),
                new Vector3(0f, 0.14f, 0f), 1.6f, out kappa, out reason));
            Assert.AreEqual(RideWaistMath.ReasonNotFinite, reason);
        }

        [Test]
        public void BoundsOfTheGuardBand_AreInclusive()
        {
            float kappa;
            int reason;
            Assert.IsTrue(RideWaistMath.TryMeasureKappa(new Vector3(0f, 1.6f, 0f), Vector3.zero,
                1.6f, out kappa, out reason));
            Assert.AreEqual(RideWaistMath.MaxKappa, kappa, 1e-5f);
            Assert.IsTrue(RideWaistMath.TryMeasureKappa(new Vector3(0f, 0.081f, 0f), Vector3.zero,
                1.6f, out kappa, out reason));
            Assert.AreEqual(RideWaistMath.MinKappa, kappa, 1e-3f);
        }

        [Test]
        public void FullPipeline_WithAYawedRig_PutsTheHipsOnTheBallTop()
        {
            // World positions through SeatEyeMath's sampling: a rig yawed 37 degrees, the seat
            // authored away from the eye point, the avatar measured there and then re-seated.
            Avatar avatar = Avatar.WithKappa(0.4f, 0.12f, 0.1f);
            var scene = new Scene(2f);
            Quaternion rotation = Quaternion.Euler(0f, 37f, 0f);
            var rigPosition = new Vector3(3f, scene.RigY, -2f);
            const float MeasuredEye = 1.6f;
            Vector3 authoredSeat = rigPosition + rotation * new Vector3(0.1f, -0.3f, 0.2f);

            Vector3 headWorld = authoredSeat + rotation * (avatar.HeadPerEye * MeasuredEye);
            Vector3 hipsWorld = authoredSeat + rotation * (avatar.HipsPerEye * MeasuredEye);
            Vector3 headInRig;
            Vector3 hipsInRig;
            Vector3 unused;
            int reason;
            Assert.IsTrue(SeatEyeMath.TrySampleHeadOffset(headWorld, authoredSeat, rotation,
                rotation, out headInRig, out unused, out reason));
            Assert.IsTrue(SeatEyeMath.TrySampleHeadOffset(hipsWorld, authoredSeat, rotation,
                rotation, out hipsInRig, out unused, out reason));
            float kappa;
            Assert.IsTrue(RideWaistMath.TryMeasureKappa(headInRig, hipsInRig, MeasuredEye, out kappa,
                out reason));
            Assert.AreEqual(0.4f, kappa, 1e-5f);

            float eye;
            bool clamped;
            Assert.IsTrue(RideScaleMath.TryEyeHeightForDiameter(2f, kappa, out eye, out clamped));
            float factor = eye / MeasuredEye;
            Vector3 solveFor = RideWaistMath.EffectiveHeadInRig(headInRig * factor,
                hipsInRig * factor, true, scene.WaistTarget());
            Vector3 offset;
            float uncompensated;
            float residual;
            Assert.IsTrue(SeatEyeMath.TryComputeSeatOffset(solveFor, Vector3.zero, NoLimit, NoLimit,
                false, 0f, 0f, true, out offset, out uncompensated, out residual, out reason));

            Vector3 seat = rigPosition + rotation * offset;
            Vector3 hipsAfter = seat + rotation * (hipsInRig * factor);
            Vector3 headAfter = seat + rotation * (headInRig * factor);
            Assert.AreEqual(scene.BallTopY, hipsAfter.y, 2e-3f);
            Assert.AreEqual(0f, Vector3.Distance(headAfter, rigPosition), 4e-3f);
        }
    }
}
