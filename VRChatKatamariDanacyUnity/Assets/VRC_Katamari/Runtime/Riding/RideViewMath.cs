using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Riding
{
    /// <summary>
    /// Where the rider's eye is relative to the ball: the push stance. The rider stands
    /// behind the ball with the feet on the ground, and a hand reaching forward and down
    /// from the shoulder meets the ball's back surface with the arm along the ball's radius,
    /// so the push goes through the centre. Also where the rider's panels hang, and how big
    /// the "click the ball to sit" target is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why the stance comes from the HANDS. In VR the rider's hands are the player's own
    /// controllers; Udon cannot place them. VRChat maps the player's real body onto the
    /// avatar's eye height, so relative to the eye the real hands reach about as far as a
    /// human arm does, scaled by the eye height E. The geometry therefore puts the ball's
    /// back surface where a human hand rests when pushing: the contact point is reached from
    /// the shoulder (<see cref="ShoulderBelowEyePerEyeHeight"/> below and
    /// <see cref="ShoulderBehindEyePerEyeHeight"/> behind the eye) by a bent arm
    /// (<see cref="PushReachFraction"/> of <see cref="ArmReachPerEyeHeight"/>), pointing
    /// <see cref="PushAngleDegrees"/> below the horizontal, and the arm continues through
    /// the ball's centre. These four body numbers describe the PLAYER's real body (human
    /// anthropometry), never the avatar's: an avatar's arms only show where IK draws them.
    /// </para>
    /// <para>
    /// The feet. The station carries a standing avatar by its feet, and VRChat's eye height
    /// is the viewpoint's height above the feet, so "feet on the ground beside the ball"
    /// is <c>E = up + r</c> (the eye is <c>up</c> above the centre, the centre <c>r</c>
    /// above the ground). With <c>E = e * r</c> the stance closes:
    /// </para>
    /// <code>
    /// e    = (1 + sin psi) / (1 - shoulderBelow - reach * sin psi)   (reach = fraction * arm)
    /// up   = (e - 1) * r
    /// back = (-shoulderBehind * e + (1 + reach * e) * cos psi) * r
    /// </code>
    /// <para>
    /// Every length is a constant times the radius (no additive base), so contact point,
    /// ball top, eye, feet and hands form one triangle-shaped figure that only scales with
    /// the ball: the same picture and the same reach at every size. The editor assembly
    /// copies <see cref="EyeUpPerRadius"/> and <see cref="EyeBackPerRadius"/> onto the
    /// motor's follow frame (bases zero); <see cref="RideScaleMath"/> asks VRChat for the
    /// eye height <see cref="EyeHeightPerRadius"/> times r (divided by the avatar's measured
    /// stance ratio, <see cref="RideStanceMath"/>).
    /// </para>
    /// <para>
    /// The panels are world-space canvases under a panel root on the follow frame (a
    /// screen-space overlay canvas is drawn for screen users only, never in VR). Their
    /// placement is (distance, azimuth, elevation) from the eye, authored for a rider of
    /// <see cref="PanelReferenceEyeHeightMeters"/>; the panel root is scaled by
    /// <see cref="PanelScaleForEyeHeight"/> so the panels keep their angular size and stay
    /// in front of a rider of any size.
    /// </para>
    /// </remarks>
    public static class RideViewMath
    {
        /// <summary>
        /// The arm's angle below the horizontal at the push, degrees; the hand meets the
        /// ball where the radius points back up along the arm. The one tuning knob of the
        /// stance (RIDE_GEOMETRY.md section 1.1 tabulates 35 to 55 degrees).
        /// </summary>
        public const float PushAngleDegrees = 45f;

        /// <summary>Shoulder joint below the eyes, per metre of eye height (human body).</summary>
        public const float ShoulderBelowEyePerEyeHeight = 0.125f;

        /// <summary>Shoulder joint behind the eyes, per metre of eye height (human body, estimate).</summary>
        public const float ShoulderBehindEyePerEyeHeight = 0.055f;

        /// <summary>Shoulder joint to the middle of the palm, straight arm, per metre of eye height (human body).</summary>
        public const float ArmReachPerEyeHeight = 0.41f;

        /// <summary>The push uses this fraction of the straight reach: elbows slightly bent.</summary>
        public const float PushReachFraction = 0.8f;

        /// <summary>Eye height above the ball centre at zero radius: none, the rule is proportional.</summary>
        public const float EyeBaseUpMeters = 0f;

        /// <summary>Back-off from the ball centre at zero radius: none, the rule is proportional.</summary>
        public const float EyeBaseBackMeters = 0f;

        /// <summary>The eye height the panel layout is authored for, metres.</summary>
        public const float PanelReferenceEyeHeightMeters = 1.64f;

        /// <summary>The interact sphere is this much bigger than the ball.</summary>
        public const float SeatTriggerScale = 1.25f;

        /// <summary>The interact sphere never shrinks below this radius, metres.</summary>
        public const float SeatTriggerMinimumRadiusMeters = 0.25f;

        /// <summary>The bent-arm push reach, per metre of eye height.</summary>
        public static float PushReachPerEyeHeight()
        {
            return PushReachFraction * ArmReachPerEyeHeight;
        }

        /// <summary>
        /// The standing rider's eye height per metre of ball radius (feet on the ground, hands
        /// on the ball): <c>e</c> in the class remarks.
        /// </summary>
        public static float EyeHeightPerRadius()
        {
            float s = Mathf.Sin(PushAngleDegrees * Mathf.Deg2Rad);
            return (1f + s) / (1f - ShoulderBelowEyePerEyeHeight - PushReachPerEyeHeight() * s);
        }

        /// <summary>Metres of eye height above the ball centre per metre of ball radius.</summary>
        public static float EyeUpPerRadius()
        {
            return EyeHeightPerRadius() - 1f;
        }

        /// <summary>Metres of back-off from the ball centre per metre of ball radius.</summary>
        public static float EyeBackPerRadius()
        {
            float e = EyeHeightPerRadius();
            float c = Mathf.Cos(PushAngleDegrees * Mathf.Deg2Rad);
            return -ShoulderBehindEyePerEyeHeight * e + (1f + PushReachPerEyeHeight() * e) * c;
        }

        /// <summary>
        /// The eye point relative to the ball centre for a ball of this radius: <c>up</c>
        /// above it and <c>back</c> behind it, metres. False for a negative or non-finite
        /// radius (outputs zero).
        /// </summary>
        public static bool TryEyeOffset(float radiusMeters, out float upMeters,
            out float backMeters)
        {
            upMeters = 0f;
            backMeters = 0f;
            if (!KatamariMath.IsFiniteFloat(radiusMeters) || radiusMeters < 0f) return false;
            float up = EyeBaseUpMeters + EyeUpPerRadius() * radiusMeters;
            float back = EyeBaseBackMeters + EyeBackPerRadius() * radiusMeters;
            if (!KatamariMath.IsFiniteFloat(up) || !KatamariMath.IsFiniteFloat(back)) return false;
            upMeters = up;
            backMeters = back;
            return true;
        }

        /// <summary>
        /// Where the hands meet the ball, relative to the eye, for a ball of this radius:
        /// <c>forward</c> along the rig's forward and <c>down</c> below the eye, metres. The
        /// point on the ball's back surface whose radius points back along the push.
        /// </summary>
        public static bool TryHandContactFromEye(float radiusMeters, out float forwardMeters,
            out float downMeters)
        {
            forwardMeters = 0f;
            downMeters = 0f;
            float up;
            float back;
            if (!TryEyeOffset(radiusMeters, out up, out back)) return false;
            float angle = PushAngleDegrees * Mathf.Deg2Rad;
            forwardMeters = back - radiusMeters * Mathf.Cos(angle);
            downMeters = up - radiusMeters * Mathf.Sin(angle);
            return true;
        }

        /// <summary>
        /// How far a hand is from the ball's surface, metres: negative inside the ball,
        /// zero on it. The live check that the stance works ("can I touch the ball").
        /// Infinity for a non-finite input.
        /// </summary>
        public static float HandGapMeters(Vector3 handWorld, Vector3 ballCenterWorld,
            float radiusMeters)
        {
            if (!KatamariMath.IsFiniteVector(handWorld) ||
                !KatamariMath.IsFiniteVector(ballCenterWorld) ||
                !KatamariMath.IsFiniteFloat(radiusMeters))
            {
                return Mathf.Infinity;
            }
            return Vector3.Distance(handWorld, ballCenterWorld) - radiusMeters;
        }

        /// <summary>
        /// Where a ball of <paramref name="radiusMeters"/> appears from an eye that is
        /// <paramref name="upMeters"/> above and <paramref name="backMeters"/> behind its
        /// centre, at pitch 0: the centre's angle below the horizon and the ball's angular
        /// radius, degrees. False when the eye is inside the ball or an input is invalid.
        /// </summary>
        public static bool TryBallView(float radiusMeters, float upMeters, float backMeters,
            out float depressionDegrees, out float halfAngleDegrees)
        {
            depressionDegrees = 0f;
            halfAngleDegrees = 0f;
            if (!KatamariMath.IsFiniteFloat(radiusMeters) || radiusMeters <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(upMeters) || !KatamariMath.IsFiniteFloat(backMeters))
            {
                return false;
            }
            float distance = Mathf.Sqrt(upMeters * upMeters + backMeters * backMeters);
            if (distance <= radiusMeters) return false;
            depressionDegrees = Mathf.Atan2(upMeters, backMeters) * Mathf.Rad2Deg;
            halfAngleDegrees = Mathf.Asin(radiusMeters / distance) * Mathf.Rad2Deg;
            return true;
        }

        /// <summary>
        /// A point <paramref name="distanceMeters"/> from the eye, <paramref name="azimuthDegrees"/>
        /// to the right of the rig's forward and <paramref name="elevationDegrees"/> above
        /// the horizon, in the follow frame's local space (forward +Z, up +Y).
        /// </summary>
        public static Vector3 HudLocalPosition(float distanceMeters, float azimuthDegrees,
            float elevationDegrees)
        {
            float azimuth = azimuthDegrees * Mathf.Deg2Rad;
            float elevation = elevationDegrees * Mathf.Deg2Rad;
            float horizontal = distanceMeters * Mathf.Cos(elevation);
            return new Vector3(horizontal * Mathf.Sin(azimuth),
                distanceMeters * Mathf.Sin(elevation), horizontal * Mathf.Cos(azimuth));
        }

        /// <summary>
        /// The rotation that makes a world-space canvas (readable from its -Z side, looking
        /// along +Z) face the eye for the same azimuth and elevation as
        /// <see cref="HudLocalPosition"/>.
        /// </summary>
        public static Quaternion HudLocalRotation(float azimuthDegrees, float elevationDegrees)
        {
            return Quaternion.Euler(-elevationDegrees, azimuthDegrees, 0f);
        }

        /// <summary>
        /// The uniform scale of the panel root for a rider of this eye height: the panels'
        /// distance and size grow with the rider, so their angular size never changes. One
        /// for an unknown (zero, negative or non-finite) eye height.
        /// </summary>
        public static float PanelScaleForEyeHeight(float eyeHeightMeters)
        {
            if (!KatamariMath.IsFiniteFloat(eyeHeightMeters) || eyeHeightMeters <= 0f) return 1f;
            return eyeHeightMeters / PanelReferenceEyeHeightMeters;
        }

        /// <summary>
        /// Radius of the sphere the player clicks to sit down, from the ball's collision
        /// radius. Bigger than the ball and never smaller than a 0.25 m floor.
        /// </summary>
        public static float SeatTriggerRadius(float collisionRadiusMeters)
        {
            if (!KatamariMath.IsFiniteFloat(collisionRadiusMeters) || collisionRadiusMeters < 0f)
            {
                return SeatTriggerMinimumRadiusMeters;
            }
            float scaled = collisionRadiusMeters * SeatTriggerScale;
            if (scaled < SeatTriggerMinimumRadiusMeters) return SeatTriggerMinimumRadiusMeters;
            return scaled;
        }
    }
}
