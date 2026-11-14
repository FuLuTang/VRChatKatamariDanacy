using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Rolling rotation for the ball's visual roll node: one confirmed center
    /// displacement becomes one incremental spin about the contact tangent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lengths are metres, angles are radians, and every input is expressed in
    /// one frame (+Y up in world space). The intended frame is the ball physics
    /// parent's local frame, which is also where the caller keeps the gameplay
    /// heading; this function never reads, writes or derives that heading. The
    /// returned rotation belongs on the separate visual roll child whose scale
    /// stays (1,1,1) - scale and position are not part of this call.
    /// </para>
    /// <para>
    /// No-slip condition. A sphere of radius r touching the plane with unit
    /// normal n has its contact point at rho = -r*n from the center. The contact
    /// point must have zero instantaneous velocity, v + omega x rho = 0, hence
    /// omega is parallel to n x t for the tangential displacement t and
    /// |omega| = |t|/r. For a flat floor (n = +Y) moving the center toward +Z
    /// this rolls about +X, and the rolled angle is the travelled distance over
    /// the radius: a quarter of the circumference 2*pi*r turns the ball 90
    /// degrees, and a full circumference returns it to the same pose. Only the
    /// tangential distance sets the angle; pure normal motion never rolls.
    /// </para>
    /// <para>
    /// The increment is built in the same frame as the displacement and is
    /// therefore composed on the left (delta * current), not on the right:
    /// right-composition would express the axis in the ball's own frame. A
    /// rotation and its negation are the same pose; the result is renormalized
    /// every step so long runs of small steps stay unit length.
    /// </para>
    /// <para>
    /// A step at or below the 1e-7 m tangent cutoff holds the pose, and so does
    /// a step the caller marks discontinuous (teleport/respawn) or one above a
    /// caller-supplied jump threshold: a teleport chord must not spin the ball.
    /// A skipped step preserves the pose and the caller must give the next
    /// segment's displacement from the new position, never the teleport chord.
    /// The tangent cutoff is the only tolerance here and is not a world-scale
    /// decision.
    /// </para>
    /// <para>
    /// Both entry points are pure: no static state, no allocation, and a failed
    /// call leaves no trace. On false nextRotation is identity and must not be
    /// applied; the caller's own rotation is a value argument and stays as it
    /// was.
    /// </para>
    /// </remarks>
    public static class VisualRollMath
    {
        // A raw quaternion whose length is at or below this cannot be
        // renormalized without amplifying whatever produced it.
        private const float MinimumRotationNorm = 1e-12f;

        // Below this tangential distance (metres) the rolling axis is not
        // meaningful and float noise would otherwise turn the pose, so the
        // pose holds instead.
        private const float TangentCutoffM = 1e-7f;

        /// <summary>
        /// Validates a quaternion and returns the same rotation with unit length.
        /// </summary>
        /// <remarks>
        /// A non-unit rotation is normalized rather than rejected, but a zero,
        /// non-finite, or effectively zero-length quaternion is rejected: there
        /// is no rotation to recover and silently substituting identity would
        /// hide a corrupt pose. This is the pose entry point for a scene start
        /// or a reset. False means the rotation is unusable and unitRotation is
        /// identity.
        /// </remarks>
        public static bool TryNormalizeRotation(Quaternion rotation, out Quaternion unitRotation)
        {
            unitRotation = Quaternion.identity;
            if (!KatamariMath.IsFiniteQuaternion(rotation)) return false;
            float scale = Mathf.Max(Mathf.Abs(rotation.x), Mathf.Max(Mathf.Abs(rotation.y),
                Mathf.Max(Mathf.Abs(rotation.z), Mathf.Abs(rotation.w))));
            if (scale <= 0f) return false;
            float x = rotation.x / scale;
            float y = rotation.y / scale;
            float z = rotation.z / scale;
            float w = rotation.w / scale;
            float norm = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (!KatamariMath.IsFiniteFloat(norm) || norm <= 0f) return false;
            if (scale <= MinimumRotationNorm / norm) return false;
            Quaternion candidate = new Quaternion(x / norm, y / norm, z / norm, w / norm);
            if (!KatamariMath.IsFiniteQuaternion(candidate)) return false;
            unitRotation = candidate;
            return true;
        }

        /// <summary>
        /// Advances the visual rolling rotation by one confirmed displacement.
        /// </summary>
        /// <remarks>
        /// currentRotation is the visual roll node's own rotation (it is
        /// normalized on entry, so the caller never has to keep it exact);
        /// displacement is the ball-center move in metres in the same frame as
        /// groundNormal; groundNormal points away from the support surface and
        /// need not be unit length, but zero or non-finite is rejected; radius
        /// is the rolling radius in metres and must be positive - choosing it
        /// (a core radius, a visual radius) is the caller's decision.
        /// useMaxDisplacement true requires a positive finite maxDisplacement
        /// and makes the call skip the roll of a step longer than that
        /// threshold; with useMaxDisplacement false the value is ignored.
        ///
        /// Returns true for a valid step, having either applied the roll or
        /// deliberately held the pose (discontinuous, above the threshold, or
        /// tangential motion at or below the cutoff). Returns false only for
        /// invalid input or a non-finite intermediate, with nextRotation set to
        /// identity. The call never touches the gameplay heading, the position,
        /// the scale or any ledger.
        /// </remarks>
        public static bool TryAdvanceRotation(
            Quaternion currentRotation,
            Vector3 displacement,
            Vector3 groundNormal,
            float radius,
            bool discontinuous,
            bool useMaxDisplacement,
            float maxDisplacement,
            out Quaternion nextRotation)
        {
            nextRotation = Quaternion.identity;

            Quaternion current;
            if (!TryNormalizeRotation(currentRotation, out current)) return false;
            if (!KatamariMath.IsFiniteVector(displacement)) return false;
            Vector3 normal;
            if (!TryUnitVector(groundNormal, out normal)) return false;
            if (!KatamariMath.IsFiniteFloat(radius) || radius <= 0f) return false;
            if (useMaxDisplacement &&
                (!KatamariMath.IsFiniteFloat(maxDisplacement) || maxDisplacement <= 0f)) return false;

            // The jump guard uses the full displacement length, not the
            // tangential one: a teleport chord is mostly normal displacement.
            float totalDistance;
            if (!TryLength(displacement, out totalDistance))
            {
                // TryLength reports either a length below the shared 1e-12 m
                // degeneracy floor or a length that overflows a float. With
                // every component under the tangent cutoff the length is
                // certainly representable, so the floor is the reason and the
                // step is a no-motion step (the zero displacement is the same
                // case); a larger component means the length overflowed, which
                // is rejected rather than treated as no motion.
                if (Mathf.Abs(displacement.x) >= TangentCutoffM ||
                    Mathf.Abs(displacement.y) >= TangentCutoffM ||
                    Mathf.Abs(displacement.z) >= TangentCutoffM) return false;
                totalDistance = 0f;
            }
            if (discontinuous || (useMaxDisplacement && totalDistance > maxDisplacement))
            {
                nextRotation = current;
                return true;
            }

            float normalDistance;
            if (!TryDot(displacement, normal, out normalDistance)) return false;
            Vector3 tangent = displacement - normal * normalDistance;
            if (!KatamariMath.IsFiniteVector(tangent)) return false;

            float tangentDistance;
            if (!TryLength(tangent, out tangentDistance) || tangentDistance <= TangentCutoffM)
            {
                // Pure normal motion, or a step too short to define an axis:
                // the pose holds but the input was still validated.
                nextRotation = current;
                return true;
            }

            // n x t is the rolling axis from the no-slip condition; the tangent
            // is perpendicular to the unit normal, so this cross is not
            // degenerate for a step above the cutoff.
            Vector3 axis;
            if (!TryUnitVector(Vector3.Cross(normal, tangent), out axis)) return false;

            float angle = tangentDistance / radius;
            if (!KatamariMath.IsFiniteFloat(angle)) return false;
            float halfAngle = angle * 0.5f;
            float sine = Mathf.Sin(halfAngle);
            float cosine = Mathf.Cos(halfAngle);
            if (!KatamariMath.IsFiniteFloat(sine) || !KatamariMath.IsFiniteFloat(cosine)) return false;
            Quaternion increment = new Quaternion(axis.x * sine, axis.y * sine, axis.z * sine, cosine);

            // Same frame as the displacement, so the increment goes on the
            // left; the renormalization keeps a long run of small steps unit.
            Quaternion candidate;
            if (!TryNormalizeRotation(increment * current, out candidate)) return false;
            nextRotation = candidate;
            return true;
        }

        // Length in metres of a finite vector. The shared normalizer also
        // returns a unit direction, which this module does not need here: the
        // rolling axis is taken from the unnormalized tangent.
        private static bool TryLength(Vector3 value, out float length)
        {
            Vector3 unit;
            return KatamariMath.TryNormalize(value, out unit, out length);
        }

        // Unit direction of a finite non-zero vector; the shared normalizer
        // also returns the length, which neither call site needs.
        private static bool TryUnitVector(Vector3 value, out Vector3 unit)
        {
            float length;
            return KatamariMath.TryNormalize(value, out unit, out length);
        }

        private static bool TryDot(Vector3 a, Vector3 b, out float value)
        {
            value = 0f;
            float dot = a.x * b.x + a.y * b.y + a.z * b.z;
            if (!KatamariMath.IsFiniteFloat(dot)) return false;
            value = dot;
            return true;
        }
    }
}
