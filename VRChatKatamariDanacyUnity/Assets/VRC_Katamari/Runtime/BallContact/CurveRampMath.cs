using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallContact
{
    /// <summary>
    /// Curved ramps as a chain of overlapping slab OBBs (section 5.7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A curve is expanded at authoring time into straight segments, so the runtime sees
    /// only OBBs and every capacity, window and solver rule stays unchanged. An arc of
    /// total angle Phi is cut into K = ceil(Phi / delta) segments; each segment sits at the
    /// arc midpoint with the tangent as its local Z, the surface normal as its local Y and
    /// the side axis as its local X, half a chord long, and is lengthened by one sagitta so
    /// the convex side has no crack. The remaining convex bump is the chord error the
    /// design budgets (sagitta at most 5% of the smallest ball radius).
    /// </para>
    /// <para>
    /// Consequence for the ball: while it presses on two neighbouring segments the
    /// push-out sees two normals and averages them, so the normal the ball feels jumps by
    /// at most delta/2 and the centre height by at most the sagitta. That is why the chain
    /// has no seam to deadlock on and why the design's tolerances are stated in terms of
    /// delta and the sagitta rather than in metres of geometry.
    /// </para>
    /// <para>
    /// Inputs: <paramref name="forward"/> is horizontal and the arc bends up from
    /// <paramref name="origin"/> by <paramref name="arcDeg"/> in the forward/up plane. The
    /// buffers are caller-owned parallel arrays (centres, three axes, half extents, surface
    /// ids, roles) because UdonSharp has no struct; a short buffer is refused instead of
    /// truncated.
    /// </para>
    /// </remarks>
    public static class CurveRampMath
    {
        // The model's 1e-9 threshold for "the segment length is not degenerate", at float scale.
        private const float Epsilon = 1e-6f;

        /// <summary>
        /// Expands one vertical arc into <paramref name="segments"/> slab OBBs and reports
        /// the chain's metrics.
        /// </summary>
        /// <remarks>
        /// False means an unusable argument (fewer than one segment, a non-positive or
        /// non-finite radius, a degenerate forward/up/cross direction, a non-finite width or
        /// thickness, or a buffer shorter than the segment count); the metrics are then
        /// zero and no segment is written. Surface ids run from
        /// <paramref name="surfaceId0"/> upwards so a chain stays addressable in the
        /// diagnostics.
        /// </remarks>
        public static bool TryTessellateVerticalCurve(
            Vector3 origin,
            Vector3 forward,
            Vector3 up,
            float radiusM,
            float arcDeg,
            int segments,
            float widthM,
            float thicknessM,
            Vector3[] centers,
            Vector3[] axisX,
            Vector3[] axisY,
            Vector3[] axisZ,
            Vector3[] halfExtents,
            int[] surfaceIds,
            int[] roles,
            out float deltaDeg,
            out float sagittaM,
            out float normalJumpDeg,
            out float segmentLengthM,
            out float overlap,
            int surfaceId0 = 200,
            int role = ContactCodes.RoleSlab)
        {
            deltaDeg = 0f;
            sagittaM = 0f;
            normalJumpDeg = 0f;
            segmentLengthM = 0f;
            overlap = 0f;

            if (segments < 1) return false;
            if (!KatamariMath.IsFiniteFloat(radiusM) || radiusM <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(arcDeg)) return false;
            if (!KatamariMath.IsFiniteFloat(widthM) || widthM < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(thicknessM) || thicknessM < 0f) return false;
            if (!KatamariMath.IsFiniteVector(origin)) return false;
            if (centers == null || axisX == null || axisY == null || axisZ == null ||
                halfExtents == null || surfaceIds == null || roles == null) return false;
            if (segments > centers.Length || segments > axisX.Length ||
                segments > axisY.Length || segments > axisZ.Length ||
                segments > halfExtents.Length || segments > surfaceIds.Length ||
                segments > roles.Length) return false;

            Vector3 unitForward;
            float forwardLength;
            if (!KatamariMath.TryNormalize(forward, out unitForward, out forwardLength))
                return false;
            Vector3 unitUp;
            float upLength;
            if (!KatamariMath.TryNormalize(up, out unitUp, out upLength)) return false;
            Vector3 side = new Vector3(
                unitForward.y * unitUp.z - unitForward.z * unitUp.y,
                unitForward.z * unitUp.x - unitForward.x * unitUp.z,
                unitForward.x * unitUp.y - unitForward.y * unitUp.x);
            Vector3 unitSide;
            float sideLength;
            if (!KatamariMath.TryNormalize(side, out unitSide, out sideLength)) return false;

            float arc = arcDeg * Mathf.Deg2Rad;
            float delta = arc / segments;
            float halfDelta = delta * 0.5f;
            float segmentLength = 2f * radiusM * Mathf.Sin(halfDelta);
            float sagitta = radiusM * (1f - Mathf.Cos(halfDelta));
            float overlapFactor = 1f;
            if (segmentLength > Epsilon) overlapFactor = 1f + 2f * sagitta / segmentLength;
            if (!KatamariMath.IsFiniteFloat(overlapFactor)) return false;

            for (int i = 0; i < segments; i++)
            {
                float phi = (i + 0.5f) * delta;
                float sinPhi = Mathf.Sin(phi);
                float cosPhi = Mathf.Cos(phi);
                Vector3 point = new Vector3(
                    origin.x + unitForward.x * radiusM * sinPhi +
                        unitUp.x * radiusM * (1f - cosPhi),
                    origin.y + unitForward.y * radiusM * sinPhi +
                        unitUp.y * radiusM * (1f - cosPhi),
                    origin.z + unitForward.z * radiusM * sinPhi +
                        unitUp.z * radiusM * (1f - cosPhi));
                Vector3 tangent = new Vector3(
                    unitForward.x * cosPhi + unitUp.x * sinPhi,
                    unitForward.y * cosPhi + unitUp.y * sinPhi,
                    unitForward.z * cosPhi + unitUp.z * sinPhi);
                Vector3 normal = new Vector3(
                    -unitForward.x * sinPhi + unitUp.x * cosPhi,
                    -unitForward.y * sinPhi + unitUp.y * cosPhi,
                    -unitForward.z * sinPhi + unitUp.z * cosPhi);
                Vector3 half = new Vector3(widthM * 0.5f, thicknessM * 0.5f,
                    segmentLength * overlapFactor * 0.5f);
                if (!KatamariMath.IsFiniteVector(point) || !KatamariMath.IsFiniteVector(tangent)
                    || !KatamariMath.IsFiniteVector(normal) || !KatamariMath.IsFiniteVector(half))
                    return false;
                centers[i] = point;
                axisX[i] = unitSide;
                axisY[i] = normal;
                axisZ[i] = tangent;
                halfExtents[i] = half;
                surfaceIds[i] = surfaceId0 + i;
                roles[i] = role;
            }

            deltaDeg = delta * Mathf.Rad2Deg;
            sagittaM = sagitta;
            normalJumpDeg = deltaDeg * 0.5f;
            segmentLengthM = segmentLength;
            overlap = overlapFactor;
            return true;
        }

        /// <summary>
        /// True when the chord error of a chain is within
        /// <paramref name="maxErrorPerRadius"/> of the ball radius (the design's 5% budget).
        /// </summary>
        public static bool IsCurveWithinChordBudget(float radiusM, int segments,
            float arcDeg, float ballRadiusM,
            float maxErrorPerRadius = ContactTuning.CurveMaxChordErrorPerRadius)
        {
            int count = segments < 1 ? 1 : segments;
            float delta = arcDeg * Mathf.Deg2Rad / count;
            float sagitta = radiusM * (1f - Mathf.Cos(delta * 0.5f));
            return sagitta <= maxErrorPerRadius * ballRadiusM;
        }
    }
}
