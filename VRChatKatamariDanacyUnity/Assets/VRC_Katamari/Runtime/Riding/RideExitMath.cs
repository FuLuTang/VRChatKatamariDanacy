using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Riding
{
    /// <summary>
    /// Where the rider is put when they leave the seat: on walkable ground next to the
    /// ball, never inside it and never under the terrain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why the exit point cannot stay where it was authored. The seat hangs on the follow
    /// frame, and the frame's eye point is above ground by only about 0.6 m for a small
    /// ball while the avatar's root sits a seated head's height (0.85 to 1.2 m) below the
    /// eye, so the seat, and an exit point parented to it, is under the terrain. VRChat
    /// moves the player to the station's exit location on exit; a point inside a collider
    /// drops them through the world. So the exit location is placed at the moment of the
    /// exit, from the ball and the ground below a few candidate spots.
    /// </para>
    /// <para>
    /// Candidates are numbered 0 behind, 1 right, 2 left, 3 in front of the ball (heading is
    /// degrees about +Y, forward = (sin, 0, cos)), each <see cref="ClearanceMeters"/> past
    /// the ball's surface. The caller casts a ray straight down from the probe origin, asks
    /// <see cref="IsStandable"/> about the hit and uses the first candidate that passes; the
    /// ray itself is an engine call and stays in the behaviour.
    /// </para>
    /// </remarks>
    public static class RideExitMath
    {
        /// <summary>Gap between the ball's surface and the landing spot, metres.</summary>
        public const float ClearanceMeters = 0.9f;

        /// <summary>Number of landing candidates tried in order.</summary>
        public const int CandidateCount = 4;

        /// <summary>Probe origin height above the ball's top, metres.</summary>
        public const float ProbeLiftMeters = 1.0f;

        /// <summary>How far below the expected ground the probe reaches, metres.</summary>
        public const float ProbeReachBelowGroundMeters = 2.5f;

        /// <summary>Walkable surfaces are at most about 45 degrees steep.</summary>
        public const float MinimumGroundNormalY = 0.7f;

        /// <summary>A landing spot may be this much above the ball's resting plane, metres.</summary>
        public const float MaximumStepUpMeters = 0.6f;

        /// <summary>A landing spot may be this far below the ball's resting plane, metres.</summary>
        public const float MaximumDropMeters = 1.5f;

        /// <summary>The player is set this far above the hit so they never start inside it, metres.</summary>
        public const float StandLiftMeters = 0.05f;

        /// <summary>
        /// The ray for candidate <paramref name="index"/>: where to start it, how long it
        /// is, and the height the ground should be at if the ball rests on flat ground.
        /// False for an index outside 0..3 or a non-finite input.
        /// </summary>
        public static bool TryExitProbe(Vector3 ballCenter, float radiusMeters,
            float headingDegrees, int index, out Vector3 probeOrigin, out float probeLength,
            out float expectedGroundY)
        {
            probeOrigin = Vector3.zero;
            probeLength = 0f;
            expectedGroundY = 0f;
            if (index < 0 || index >= CandidateCount) return false;
            if (!KatamariMath.IsFiniteVector(ballCenter)) return false;
            if (!KatamariMath.IsFiniteFloat(radiusMeters) || radiusMeters < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(headingDegrees)) return false;

            float heading = headingDegrees * Mathf.Deg2Rad;
            float forwardX = Mathf.Sin(heading);
            float forwardZ = Mathf.Cos(heading);
            float rightX = forwardZ;
            float rightZ = -forwardX;
            float dirX;
            float dirZ;
            if (index == 0) { dirX = -forwardX; dirZ = -forwardZ; }
            else if (index == 1) { dirX = rightX; dirZ = rightZ; }
            else if (index == 2) { dirX = -rightX; dirZ = -rightZ; }
            else { dirX = forwardX; dirZ = forwardZ; }

            float distance = radiusMeters + ClearanceMeters;
            expectedGroundY = ballCenter.y - radiusMeters;
            float originY = ballCenter.y + radiusMeters + ProbeLiftMeters;
            probeOrigin = new Vector3(ballCenter.x + dirX * distance, originY,
                ballCenter.z + dirZ * distance);
            probeLength = originY - expectedGroundY + ProbeReachBelowGroundMeters;
            return true;
        }

        /// <summary>
        /// True when a downward ray's hit is somewhere a person can stand next to the ball:
        /// a gentle slope, at about the height the ball rests at.
        /// </summary>
        public static bool IsStandable(float hitNormalY, float hitY, float expectedGroundY)
        {
            if (!KatamariMath.IsFiniteFloat(hitNormalY) || !KatamariMath.IsFiniteFloat(hitY))
            {
                return false;
            }
            if (hitNormalY < MinimumGroundNormalY) return false;
            if (hitY > expectedGroundY + MaximumStepUpMeters) return false;
            if (hitY < expectedGroundY - MaximumDropMeters) return false;
            return true;
        }

        /// <summary>
        /// The pose to give the exit location for a standable ground point: the point lifted by
        /// <see cref="StandLiftMeters"/>, facing the ball. False (outputs zero) for a non-finite
        /// input. The one rule shared by the exit at run time (the ray's hit) and the scene
        /// assembly (the expected flat ground of the first candidate).
        /// </summary>
        public static bool TryStandPose(Vector3 groundPoint, Vector3 ballCenter,
            out Vector3 position, out float yawDegrees)
        {
            position = Vector3.zero;
            yawDegrees = 0f;
            if (!KatamariMath.IsFiniteVector(groundPoint) || !KatamariMath.IsFiniteVector(ballCenter))
            {
                return false;
            }
            position = groundPoint + new Vector3(0f, StandLiftMeters, 0f);
            yawDegrees = FacingYawDegrees(position, ballCenter);
            return true;
        }

        /// <summary>
        /// Yaw (degrees about +Y, [0, 360)) that makes someone standing at
        /// <paramref name="from"/> face <paramref name="to"/> on the horizontal plane; 0 when
        /// the two coincide horizontally or an input is not finite.
        /// </summary>
        public static float FacingYawDegrees(Vector3 from, Vector3 to)
        {
            if (!KatamariMath.IsFiniteVector(from) || !KatamariMath.IsFiniteVector(to)) return 0f;
            float dx = to.x - from.x;
            float dz = to.z - from.z;
            if (dx * dx + dz * dz < 1e-8f) return 0f;
            float yaw = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
            if (yaw < 0f) yaw += 360f;
            return yaw;
        }
    }
}
