using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Places the floating push-direction cue ahead of the ball: a visual-only marker
    /// that shows which way the rider's forward push will send it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Frame and units: the project's single world space, +Y up, metres and seconds.
    /// pushYawDegrees is a yaw about +Y in degrees using the same convention as
    /// <see cref="FollowRigMath"/>: forward is (sin yaw, 0, cos yaw), so 0 faces +Z and
    /// the angle grows from +Z towards +X. The returned yaw is wrapped into [0, 360).
    /// </para>
    /// <para>
    /// The cue's centre is
    /// <c>ballCentre + forward * (radius + gap) + up * (radius + hover + bob)</c>, with
    /// <c>gap = gapBaseMeters + diameter * gapPerDiameter</c> and
    /// <c>length = clamp(lengthBaseMeters + diameter * lengthPerDiameter, min, max)</c>.
    /// The horizontal terms are measured from the ball CENTRE; the hover is measured from
    /// the TOP of the ball, so the cue floats just above the ball instead of hugging the
    /// ground. That is a visibility rule, not decoration: the rider's seat is
    /// <c>0.55 + 2.4 * radius</c> above the centre (FollowRig's provisional framing), the
    /// line of sight that grazes the top of the ball only descends, and a ground-hugging
    /// marker ahead of a grown ball therefore disappears BEHIND the ball. A point at or
    /// above the ball's top is always outside that silhouette, because the eye is above
    /// the top and the whole sphere stays below the upper tangent line. For a 0.30 m ball
    /// this still puts the cue only 0.38 m above the ground.
    /// </para>
    /// <para>
    /// Why the yaw is the caller's (follow-rig) yaw and not the ball's velocity: a ball at
    /// rest must still show where a forward push goes, and at rest the planar velocity is
    /// zero (and while braking it points the other way). The follow rig's yaw is the
    /// filtered heading the rider's seat faces - the same value the seat is built from -
    /// so the marker and the seat cannot disagree. This class takes the angle as data and
    /// has no opinion about where the caller read it.
    /// </para>
    /// <para>
    /// Spacing and length both grow linearly with the drawn (semantic) diameter, because
    /// the ball is judged from about 0.10 m to 2.20 m: a constant-size arrow would either
    /// cover a small ball or vanish next to a large one, and a constant surface gap would
    /// let absorbed props standing on a grown ball swallow the cue. With the committed
    /// coefficients the cue lands about 20 degrees below the rider's horizon at every
    /// size, which is the property the linear laws are chosen for.
    /// </para>
    /// <para>
    /// The bob is <c>bobAmplitudeMeters * sin(2*pi*bobHz*phaseSeconds)</c> and moves the
    /// cue vertically only. phaseSeconds is caller-owned (Time.time in the scene), so the
    /// call stays pure and deterministic; amplitude or frequency zero disables the bob, and
    /// the amplitude is deliberately small enough not to change the size law. Nothing
    /// rotates about any other axis: the pose is a position, a yaw and one uniform scale.
    /// </para>
    /// <para>
    /// This is presentation only: no collision, no pickup candidate, no gameplay state, no
    /// allocation, no statics and no config object. Validation is atomic - a non-finite or
    /// negative input, a min/max conflict, or a non-finite candidate returns false and every
    /// out value is zero, so the caller never applies half a pose. A zero diameter is
    /// allowed: it is the same geometry with no ball, matching FollowRigMath's treatment of
    /// a zero radius.
    /// </para>
    /// </remarks>
    public static class PushCueMath
    {
        /// <summary>
        /// Evaluates one cue pose from the ball's centre, its drawn diameter and the push yaw.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ballDiameterM is the semantic (drawn) diameter, not the collision diameter:
        /// this layer decorates the ball the player sees. gapBaseMeters, gapPerDiameter,
        /// hoverAboveBallTopMeters, lengthBaseMeters, lengthPerDiameter,
        /// minLengthMeters, maxLengthMeters, bobAmplitudeMeters and bobHz are the caller's
        /// PROVISIONAL tuning inputs; minLengthMeters must be positive and the length law is
        /// clamped into [minLengthMeters, maxLengthMeters]. A zero gap, hover, length base or
        /// bob amplitude is legal - each removes that term rather than being an error.
        /// </para>
        /// <para>
        /// On success cueWorldPosition is the cue's centre in world metres, cueYawDegrees is
        /// in [0, 360), and cueLengthMeters is the uniform scale for an arrow authored at a
        /// 1.0 m design length along +Z. On false all three are zero.
        /// </para>
        /// </remarks>
        public static bool TryEvaluate(
            Vector3 ballCenterWorld,
            float ballDiameterM,
            float pushYawDegrees,
            float phaseSeconds,
            float gapBaseMeters,
            float gapPerDiameter,
            float hoverAboveBallTopMeters,
            float lengthBaseMeters,
            float lengthPerDiameter,
            float minLengthMeters,
            float maxLengthMeters,
            float bobAmplitudeMeters,
            float bobHz,
            out Vector3 cueWorldPosition,
            out float cueYawDegrees,
            out float cueLengthMeters)
        {
            cueWorldPosition = Vector3.zero;
            cueYawDegrees = 0f;
            cueLengthMeters = 0f;

            if (!KatamariMath.IsFiniteVector(ballCenterWorld)) return false;
            if (!KatamariMath.IsFiniteFloat(ballDiameterM) || ballDiameterM < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(pushYawDegrees)) return false;
            if (!KatamariMath.IsFiniteFloat(phaseSeconds)) return false;
            if (!KatamariMath.IsFiniteFloat(gapBaseMeters) || gapBaseMeters < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(gapPerDiameter) || gapPerDiameter < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(hoverAboveBallTopMeters) ||
                hoverAboveBallTopMeters < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(lengthBaseMeters) || lengthBaseMeters < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(lengthPerDiameter) || lengthPerDiameter < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(minLengthMeters) || minLengthMeters <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(maxLengthMeters)) return false;
            if (maxLengthMeters < minLengthMeters) return false;
            if (!KatamariMath.IsFiniteFloat(bobAmplitudeMeters) || bobAmplitudeMeters < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(bobHz) || bobHz < 0f) return false;

            // Both laws are linear in the diameter; every intermediate is checked so a
            // huge but finite diameter cannot slip through as infinity.
            float radius = ballDiameterM * 0.5f;
            float gap = gapBaseMeters + ballDiameterM * gapPerDiameter;
            if (!KatamariMath.IsFiniteFloat(gap)) return false;
            float horizontalOffset = radius + gap;
            if (!KatamariMath.IsFiniteFloat(horizontalOffset)) return false;

            float length = lengthBaseMeters + ballDiameterM * lengthPerDiameter;
            if (!KatamariMath.IsFiniteFloat(length)) return false;
            if (length < minLengthMeters) length = minLengthMeters;
            if (length > maxLengthMeters) length = maxLengthMeters;

            float yaw = Wrap360(pushYawDegrees);
            float yawRadians = yaw * Mathf.Deg2Rad;
            float sinYaw = Mathf.Sin(yawRadians);
            float cosYaw = Mathf.Cos(yawRadians);
            if (!KatamariMath.IsFiniteFloat(sinYaw) || !KatamariMath.IsFiniteFloat(cosYaw)) return false;

            // One sine on a slow rate; the rate and the phase product are both checked so
            // an overflowing frequency is refused instead of producing a garbage sample.
            float bobRate = 2f * Mathf.PI * bobHz;
            if (!KatamariMath.IsFiniteFloat(bobRate)) return false;
            float bobPhase = bobRate * phaseSeconds;
            if (!KatamariMath.IsFiniteFloat(bobPhase)) return false;
            float bobOffset = bobAmplitudeMeters * Mathf.Sin(bobPhase);
            if (!KatamariMath.IsFiniteFloat(bobOffset)) return false;

            float heightAboveCentre = radius + hoverAboveBallTopMeters + bobOffset;
            if (!KatamariMath.IsFiniteFloat(heightAboveCentre)) return false;

            float x = ballCenterWorld.x + sinYaw * horizontalOffset;
            float y = ballCenterWorld.y + heightAboveCentre;
            float z = ballCenterWorld.z + cosYaw * horizontalOffset;
            if (!KatamariMath.IsFiniteFloat(x) || !KatamariMath.IsFiniteFloat(y)) return false;
            if (!KatamariMath.IsFiniteFloat(z)) return false;

            cueWorldPosition = new Vector3(x, y, z);
            cueYawDegrees = yaw;
            cueLengthMeters = length;
            return true;
        }

        private static float Wrap360(float degrees)
        {
            float wrapped = degrees % 360f;
            if (wrapped < 0f) wrapped += 360f;
            return wrapped;
        }
    }
}
