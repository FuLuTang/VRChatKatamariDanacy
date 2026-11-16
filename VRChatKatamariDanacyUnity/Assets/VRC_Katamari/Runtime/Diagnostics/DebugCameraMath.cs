using VRCKatamari.Core;
using UnityEngine;

namespace VRCKatamari.Diagnostics
{
    /// <summary>
    /// Pure math for the DEV-ONLY third-person ball camera: where the camera sits, what it
    /// looks at, and the clip planes that keep a ball that size in view.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The camera exists so an acceptance run can SEE the ball with the props it absorbed. The
    /// player's own view cannot show it: in the seat the eye point is about 1.05 m above the
    /// ball and the ball is below the frame edge. This module is the offset and framing
    /// arithmetic only - no scene object, no Camera component and no state
    /// (Docs/Design/BallPresentation/DEBUG_CAMERA.md).
    /// </para>
    /// <para>
    /// Every distance is a multiple of the ball radius, which is what lets one set of constants
    /// serve a 15 cm starting ball and a 30 m map-sized one: 2.6 r behind, 1.4 r above, aimed
    /// 0.4 r above the centre. Because the offset and the ball both scale with r, the ball's
    /// angular size - and therefore its place in the frame - is identical at every size.
    /// </para>
    /// <para>
    /// "Behind" is the opposite of the ball's travel direction, which the caller reads from the
    /// motor's heading. Only the horizontal part of that direction is used: the camera sits
    /// behind the heading, never behind the ball's roll or pitch, or the horizon would swim in
    /// every screenshot.
    /// </para>
    /// </remarks>
    public static class DebugCameraMath
    {
        /// <summary>Smallest near plane used, metres; a 0.15 m ball needs a small one.</summary>
        public const float MinimumNearClipM = 0.03f;

        /// <summary>Smallest far plane used, metres: the map, not the ball, sets this floor.</summary>
        public const float MinimumFarClipM = 60f;

        /// <summary>
        /// Places the camera behind and above the ball, aiming slightly above its centre.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <paramref name="distanceM"/> is the camera-to-centre distance that was actually used,
        /// for the clip-plane call that follows it; it is not a second input.
        /// </para>
        /// <para>
        /// Aiming 0.4 r above the centre pushes the ball down the frame: the centre lands about
        /// 39% up the viewport at a 60 degree field of view, inside the lower-middle third, with
        /// the ball's top around 69% and its bottom around 16%. That is the framing the operator
        /// gets without touching a number.
        /// </para>
        /// <para>
        /// False means an input could not be used: a non-finite centre, a zero or negative
        /// radius, a negative or non-finite factor, both offsets zero, or a heading with no
        /// horizontal direction. Position, rotation and distance are then left at their zero
        /// values, so a caller cannot half-apply a refused frame - it must keep the pose it has.
        /// </para>
        /// </remarks>
        public static bool TryComputePose(
            Vector3 ballCenterWorld,
            Vector3 horizontalForward,
            float radiusM,
            float backRadiusFactor,
            float upRadiusFactor,
            float aimLiftRadiusFactor,
            out Vector3 position,
            out Quaternion rotation,
            out float distanceM)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            distanceM = 0f;

            if (!KatamariMath.IsFiniteVector(ballCenterWorld)) return false;
            if (!KatamariMath.IsFiniteFloat(radiusM) || radiusM <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(backRadiusFactor) || backRadiusFactor < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(upRadiusFactor) || upRadiusFactor < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(aimLiftRadiusFactor) || aimLiftRadiusFactor < 0f) return false;
            // A camera at the ball centre has nothing to look at from behind or above.
            if (backRadiusFactor == 0f && upRadiusFactor == 0f) return false;

            // Flatten the heading first; a rolling ball's pitch/roll must not tilt this view.
            float forwardX = horizontalForward.x;
            float forwardZ = horizontalForward.z;
            float forwardSquared = forwardX * forwardX + forwardZ * forwardZ;
            if (!KatamariMath.IsFiniteFloat(forwardSquared)) return false;
            if (forwardSquared <= 1e-6f) return false;
            float inverseForwardLength = 1f / Mathf.Sqrt(forwardSquared);
            Vector3 forward = new Vector3(forwardX * inverseForwardLength, 0f,
                forwardZ * inverseForwardLength);

            Vector3 up = Vector3.up;
            Vector3 candidate = ballCenterWorld
                - forward * (backRadiusFactor * radiusM)
                + up * (upRadiusFactor * radiusM);
            Vector3 aim = ballCenterWorld + up * (aimLiftRadiusFactor * radiusM);
            if (!KatamariMath.IsFiniteVector(candidate)) return false;
            if (!KatamariMath.IsFiniteVector(aim)) return false;

            // KatamariMath.TryNormalize rejects a degenerate or non-finite eye-to-aim vector,
            // which is the only way LookRotation could produce a broken pose.
            Vector3 direction;
            float aimDistance;
            if (!KatamariMath.TryNormalize(aim - candidate, out direction, out aimDistance)) return false;

            Quaternion candidateRotation = Quaternion.LookRotation(direction, up);
            if (!KatamariMath.IsFiniteQuaternion(candidateRotation)) return false;

            // The clip-plane call needs the length of the pose just built. Distance is taken
            // from the position rather than from the factors, so the two can never disagree.
            float offsetX = candidate.x - ballCenterWorld.x;
            float offsetY = candidate.y - ballCenterWorld.y;
            float offsetZ = candidate.z - ballCenterWorld.z;
            distanceM = Mathf.Sqrt(offsetX * offsetX + offsetY * offsetY + offsetZ * offsetZ);
            if (!KatamariMath.IsFiniteFloat(distanceM) || distanceM <= 0f) return false;

            position = candidate;
            rotation = candidateRotation;
            return true;
        }

        /// <summary>
        /// Near and far planes for a camera at <paramref name="distanceM"/> from a ball of
        /// <paramref name="radiusM"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Near is 2% of the camera-to-centre distance, floored at
        /// <see cref="MinimumNearClipM"/>. The ball's near surface is about 1.95 r away, an order
        /// of magnitude inside that, so nothing of the ball is clipped; the floor keeps depth
        /// precision usable at the far plane.
        /// </para>
        /// <para>
        /// Far is twice the distance plus 6 r, floored at <see cref="MinimumFarClipM"/>. For a
        /// 15 m ball the pose puts the camera about 44 m out, so far is about 179 m: past the
        /// ball's far surface (about 59 m) and past the props and ground behind it. Far stays
        /// well under Unity's far &lt;= 100000 * near limit because both planes scale with the
        /// radius.
        /// </para>
        /// <para>
        /// False means a distance or radius that cannot describe a camera: the outputs are then
        /// 0 and the caller must keep the clip planes it has.
        /// </para>
        /// </remarks>
        public static bool TryClipPlanes(float distanceM, float radiusM, out float nearM,
            out float farM)
        {
            nearM = 0f;
            farM = 0f;
            if (!KatamariMath.IsFiniteFloat(distanceM) || distanceM <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(radiusM) || radiusM <= 0f) return false;

            float candidateNear = distanceM * 0.02f;
            if (candidateNear < MinimumNearClipM) candidateNear = MinimumNearClipM;
            float candidateFar = distanceM * 2f + radiusM * 6f;
            if (candidateFar < MinimumFarClipM) candidateFar = MinimumFarClipM;
            if (!KatamariMath.IsFiniteFloat(candidateNear)) return false;
            if (!KatamariMath.IsFiniteFloat(candidateFar)) return false;
            // Unity refuses far <= near; refusing here keeps the camera's authored planes intact
            // instead of writing a pair the engine would silently repair.
            if (!(candidateFar > candidateNear)) return false;

            nearM = candidateNear;
            farM = candidateFar;
            return true;
        }
    }
}
