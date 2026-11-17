using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Riding
{
    /// <summary>
    /// What the rider sees by default: where the eye point sits relative to the ball, how
    /// big and how low the ball appears at pitch 0, where the head-locked HUD panels hang,
    /// and how big the "click the ball to sit" target is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The eye rule. VRChat gives a seated rider no pitch of their own: the station carries
    /// the avatar's root, the world cannot tilt the head (desktop pitch is the mouse, VR
    /// pitch is the headset), so the only lever is WHERE the eye is. The rule keeps the ball
    /// centre a fixed <see cref="DepressionDegrees"/> below the horizon and the ball's angular
    /// radius at exactly <see cref="MaxBallHalfAngleDegrees"/>, so the ball fills the lower
    /// third of a 60 degree vertical view at every size. Both follow from
    /// <c>up = EyeUpPerRadius * r</c> and <c>back = up / tan(depression)</c> with NO additive
    /// base: every length is proportional to the radius, so the view is the same picture at
    /// any ball size (the motor's scale-invariant motion relies on the same property). The
    /// four numbers the follow frame needs (<c>FollowRigMath</c>'s base/per-radius pairs) are
    /// derived here, so the scene has one source for them: the editor assembly copies them
    /// onto the motor (the bases are zero).
    /// </para>
    /// <para>
    /// What the proportionality implies for the rider: the eye is <c>up</c> above the ball
    /// centre and a seated body hangs below it, so the rider's size is not free - it is
    /// <see cref="RideScaleMath"/>'s job to size the avatar so that its measured hips are level
    /// with the top of the ball (<see cref="RideWaistMath"/>). This class only gives the eye
    /// point; the seat and the avatar size follow from it.
    /// </para>
    /// <para>
    /// The HUD and the cards are world-space panels parented to the follow frame, not screen
    /// overlays: a screen-space overlay canvas is not drawn by the VRChat client. Their
    /// placement is expressed as (distance, azimuth, elevation) from the eye point.
    /// </para>
    /// </remarks>
    public static class RideViewMath
    {
        /// <summary>Ball centre below the horizon at pitch 0, degrees.</summary>
        public const float DepressionDegrees = 18f;

        /// <summary>The ball's angular radius, degrees (a third of a 60 degree view, diameter-wise).</summary>
        public const float MaxBallHalfAngleDegrees = 10f;

        /// <summary>Eye height above the ball centre at zero radius: none, the rule is proportional.</summary>
        public const float EyeBaseUpMeters = 0f;

        /// <summary>Back-off from the ball centre at zero radius: none, the rule is proportional.</summary>
        public const float EyeBaseBackMeters = 0f;

        /// <summary>Half of the 60 degree vertical field the project designs for.</summary>
        public const float HalfFovDegrees = 30f;

        /// <summary>The ball's lower edge must stay this far inside the view's lower edge.</summary>
        public const float EdgeMarginDegrees = 2f;

        /// <summary>The interact sphere is this much bigger than the ball.</summary>
        public const float SeatTriggerScale = 1.25f;

        /// <summary>The interact sphere never shrinks below this radius, metres.</summary>
        public const float SeatTriggerMinimumRadiusMeters = 0.25f;

        /// <summary>Metres of eye height per metre of ball radius.</summary>
        public static float EyeUpPerRadius()
        {
            return Mathf.Sin(DepressionDegrees * Mathf.Deg2Rad) /
                Mathf.Sin(MaxBallHalfAngleDegrees * Mathf.Deg2Rad);
        }

        /// <summary>Metres of back-off per metre of ball radius.</summary>
        public static float EyeBackPerRadius()
        {
            return EyeUpPerRadius() / Mathf.Tan(DepressionDegrees * Mathf.Deg2Rad);
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
        /// True when the whole ball, plus the margin, lies inside the view's half field.
        /// </summary>
        public static bool IsBallInView(float depressionDegrees, float halfAngleDegrees,
            float halfFovDegrees, float marginDegrees)
        {
            return depressionDegrees + halfAngleDegrees + marginDegrees <= halfFovDegrees;
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
