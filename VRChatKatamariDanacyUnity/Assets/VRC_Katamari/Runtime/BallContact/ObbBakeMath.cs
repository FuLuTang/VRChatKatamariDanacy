using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallContact
{
    /// <summary>
    /// Baking a collider's eight world corners into the oriented box the contact solver
    /// queries (section 1.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why corners and not rotation/lossyScale: <c>TransformPoint</c> already folds the
    /// parent's scale, rotation and translation into each corner, so the axes and half
    /// extents are read straight from the corner differences without trusting any single
    /// property. A non-uniformly scaled rotated parent produces a sheared parallelogram
    /// whose "axes" are not orthogonal; that case is refused here and the caller falls back
    /// to the bounds AABB with a warning, because a wrong OBB would be worse than a coarse
    /// AABB.
    /// </para>
    /// <para>
    /// Corner order is part of the contract (the same bit order the design's pseudocode
    /// implies): index bit 0 is the local +X side, bit 1 the local +Y side, bit 2 the local
    /// +Z side, so corners[0] is (-x,-y,-z), corners[1] is (+x,-y,-z), corners[2] is
    /// (-x,+y,-z) and corners[4] is (-x,-y,+z). The X edge is corners[1]-corners[0], the Y
    /// edge corners[2]-corners[0] and the Z edge corners[4]-corners[0]. Axis Y is the
    /// walking face normal for a ramp slab, so that assignment matters to the author, not to
    /// the solver.
    /// </para>
    /// <para>
    /// For an axis-aligned box this reproduces the old bounds AABB bit for bit (the corner
    /// differences are the size), which is the S4 migration checkpoint; the EditMode test
    /// pins it.
    /// </para>
    /// </remarks>
    public static class ObbBakeMath
    {
        /// <summary>The number of corners a box contributes.</summary>
        public const int CornerCount = 8;

        /// <summary>
        /// Fits an OBB to eight world corners in the documented bit order.
        /// </summary>
        /// <remarks>
        /// False for a null or short array, a non-finite corner, a degenerate edge, or a
        /// shear above <see cref="ContactTuning.BakeShearTolerance"/>; every out value is
        /// then zero. The centre is the mean of the eight corners, and the half extents are
        /// half the edge lengths (so a size vector is doubled here, not lost).
        /// </remarks>
        public static bool TryBakeFromCorners(Vector3[] corners, out Vector3 center,
            out Vector3 axisX, out Vector3 axisY, out Vector3 axisZ, out Vector3 halfExtents)
        {
            center = Vector3.zero;
            axisX = Vector3.zero;
            axisY = Vector3.zero;
            axisZ = Vector3.zero;
            halfExtents = Vector3.zero;
            if (corners == null || corners.Length < CornerCount) return false;
            for (int i = 0; i < CornerCount; i++)
            {
                if (!KatamariMath.IsFiniteVector(corners[i])) return false;
            }

            Vector3 edgeX = new Vector3(corners[1].x - corners[0].x, corners[1].y - corners[0].y,
                corners[1].z - corners[0].z);
            Vector3 edgeY = new Vector3(corners[2].x - corners[0].x, corners[2].y - corners[0].y,
                corners[2].z - corners[0].z);
            Vector3 edgeZ = new Vector3(corners[4].x - corners[0].x, corners[4].y - corners[0].y,
                corners[4].z - corners[0].z);
            Vector3 unitX;
            float lengthX;
            Vector3 unitY;
            float lengthY;
            Vector3 unitZ;
            float lengthZ;
            if (!KatamariMath.TryNormalize(edgeX, out unitX, out lengthX)) return false;
            if (!KatamariMath.TryNormalize(edgeY, out unitY, out lengthY)) return false;
            if (!KatamariMath.TryNormalize(edgeZ, out unitZ, out lengthZ)) return false;

            if (Mathf.Abs(Dot(unitX, unitY)) > ContactTuning.BakeShearTolerance) return false;
            if (Mathf.Abs(Dot(unitX, unitZ)) > ContactTuning.BakeShearTolerance) return false;
            if (Mathf.Abs(Dot(unitY, unitZ)) > ContactTuning.BakeShearTolerance) return false;

            float centerX = 0f;
            float centerY = 0f;
            float centerZ = 0f;
            for (int i = 0; i < CornerCount; i++)
            {
                centerX += corners[i].x;
                centerY += corners[i].y;
                centerZ += corners[i].z;
            }
            float scale = 1f / CornerCount;
            center = new Vector3(centerX * scale, centerY * scale, centerZ * scale);
            axisX = unitX;
            axisY = unitY;
            axisZ = unitZ;
            halfExtents = new Vector3(lengthX * 0.5f, lengthY * 0.5f, lengthZ * 0.5f);
            if (!KatamariMath.IsFiniteVector(center) || !KatamariMath.IsFiniteVector(halfExtents))
            {
                center = Vector3.zero;
                axisX = Vector3.zero;
                axisY = Vector3.zero;
                axisZ = Vector3.zero;
                halfExtents = Vector3.zero;
                return false;
            }
            return true;
        }

        /// <summary>
        /// The shear fallback: a world-axis OBB from a bounds centre and bounds size.
        /// </summary>
        /// <remarks>
        /// False for a non-finite centre/size or a negative size component. Sizes are
        /// halved into the half extents, so this is the same AABB the previous bake used.
        /// </remarks>
        public static bool TryBakeFromBounds(Vector3 boundsCenter, Vector3 boundsSize,
            out Vector3 axisX, out Vector3 axisY, out Vector3 axisZ, out Vector3 halfExtents)
        {
            axisX = Vector3.zero;
            axisY = Vector3.zero;
            axisZ = Vector3.zero;
            halfExtents = Vector3.zero;
            if (!KatamariMath.IsFiniteVector(boundsCenter)) return false;
            if (!KatamariMath.IsFiniteVector(boundsSize)) return false;
            if (boundsSize.x < 0f || boundsSize.y < 0f || boundsSize.z < 0f) return false;
            axisX = new Vector3(1f, 0f, 0f);
            axisY = new Vector3(0f, 1f, 0f);
            axisZ = new Vector3(0f, 0f, 1f);
            halfExtents = new Vector3(boundsSize.x * 0.5f, boundsSize.y * 0.5f,
                boundsSize.z * 0.5f);
            return true;
        }

        private static float Dot(Vector3 a, Vector3 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }
    }
}
