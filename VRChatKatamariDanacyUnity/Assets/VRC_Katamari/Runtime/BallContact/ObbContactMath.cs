using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallContact
{
    /// <summary>
    /// The one solid primitive of ball contact v2: an oriented box, and the exact
    /// sphere-vs-box closest point, distance, outward normal and contact record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Geometry. An OBB is a centre, three orthonormal axes and three nonnegative
    /// half extents in metres, in the project's single world space (+Y up). Axes are
    /// passed as separate scalars/vectors rather than as a struct because UdonSharp
    /// has no user structs; a static box, a ramp slab and a prop box are the same
    /// primitive, and only the label (role) differs.
    /// </para>
    /// <para>
    /// The closest point is the clamped local point, so faces, edges and corners all
    /// carry their true normal - the edge/corner normal is the rounded-box one, not a
    /// face normal. That is what removes the 45 deg seam deadlock and the "a deck
    /// is a wall for a big ball" failure that an inflated AABB (every corner square) has. A query point inside the box is
    /// pushed out through the nearest face with normal = that face axis and
    /// distance = -(gap to that face) (negative, so the contact depth is r + gap).
    /// </para>
    /// <para>
    /// Contact convention. The normal points from the solid towards the ball centre;
    /// theta = angle between the normal and +Y, so 0 deg is a floor and 90 deg a wall.
    /// A contact is registered while the centre is within radius + skin of the solid,
    /// so depth = radius - distance may be negative inside the skin band; a negative
    /// depth is "near", not "touching", and the solver never pushes it.
    /// </para>
    /// <para>
    /// Number discipline. The reference model (contact_v2.py) is Python double. Here
    /// Epsilon (1e-6) and AxisTolerance (1e-5) are float-scale placeholders for its
    /// 1e-9 and 1e-6, not measured tolerances. Validation runs on every call exactly
    /// where the model validates (closest_point_on_obb calls _validate_obb); if a
    /// profile later shows it matters, the static-table bake can hoist it, but that is
    /// a cost decision, not a change of contract. Nothing here allocates, reads scene
    /// state or has static state.
    /// </para>
    /// </remarks>
    public static class ObbContactMath
    {
        // Degenerate distance: the model's 1e-9 double epsilon at float scale.
        private const float Epsilon = 1e-6f;

        // "This axis is unit length and these axes are orthogonal", float scale for
        // the model's 1e-6. A quaternion-rotated axis is unit to about 1e-7.
        private const float AxisTolerance = 1e-5f;

        /// <summary>
        /// The point in the box's local frame: <c>dot(point - centre, axis_i)</c>.
        /// </summary>
        public static Vector3 ToLocal(Vector3 point, Vector3 center, Vector3 axisX,
            Vector3 axisY, Vector3 axisZ)
        {
            Vector3 delta = new Vector3(point.x - center.x, point.y - center.y,
                point.z - center.z);
            return new Vector3(Dot(delta, axisX), Dot(delta, axisY), Dot(delta, axisZ));
        }

        /// <summary>Back to world space: <c>centre + sum(local_i * axis_i)</c>.</summary>
        public static Vector3 ToWorld(Vector3 local, Vector3 center, Vector3 axisX,
            Vector3 axisY, Vector3 axisZ)
        {
            return new Vector3(
                center.x + axisX.x * local.x + axisY.x * local.y + axisZ.x * local.z,
                center.y + axisX.y * local.x + axisY.y * local.y + axisZ.y * local.z,
                center.z + axisX.z * local.x + axisY.z * local.y + axisZ.z * local.z);
        }

        /// <summary>
        /// The exact closest point on a solid oriented box, with its distance, the
        /// unit normal from the box towards the query point and the feature code.
        /// </summary>
        /// <remarks>
        /// False means a non-finite input, an axis that is not unit length, axes that
        /// are not orthogonal or a negative half extent; every out value is then zero
        /// and feature is <see cref="ContactCodes.FeatureInside"/>'s value only because
        /// zero is the neutral one. distance is negative when the point is inside the
        /// box: it is minus the gap to the nearest face, and the contact depth built
        /// from it is radius + gap. The normal is <c>e / |e|</c> (rounded edge/corner),
        /// never a face normal, except for the inside case where it is the nearest
        /// face's axis with the sign of the point's local coordinate.
        /// </remarks>
        public static bool TryClosestPointOnObb(Vector3 center, Vector3 axisX, Vector3 axisY,
            Vector3 axisZ, Vector3 halfExtents, Vector3 point, out Vector3 closestPoint,
            out float distance, out Vector3 normal, out int feature)
        {
            closestPoint = Vector3.zero;
            distance = 0f;
            normal = Vector3.zero;
            feature = ContactCodes.FeatureInside;
            if (!KatamariMath.IsFiniteVector(point)) return false;
            if (!IsValidObb(center, axisX, axisY, axisZ, halfExtents)) return false;

            Vector3 local = ToLocal(point, center, axisX, axisY, axisZ);
            float halfX = halfExtents.x;
            float halfY = halfExtents.y;
            float halfZ = halfExtents.z;
            float clampedX = Mathf.Clamp(local.x, -halfX, halfX);
            float clampedY = Mathf.Clamp(local.y, -halfY, halfY);
            float clampedZ = Mathf.Clamp(local.z, -halfZ, halfZ);

            int outside = 0;
            if (Mathf.Abs(local.x - clampedX) > Epsilon) outside++;
            if (Mathf.Abs(local.y - clampedY) > Epsilon) outside++;
            if (Mathf.Abs(local.z - clampedZ) > Epsilon) outside++;

            float ex = local.x - clampedX;
            float ey = local.y - clampedY;
            float ez = local.z - clampedZ;
            float offset = Mathf.Sqrt(ex * ex + ey * ey + ez * ez);
            if (offset > Epsilon)
            {
                float inverse = 1f / offset;
                float nx = ex * inverse;
                float ny = ey * inverse;
                float nz = ez * inverse;
                normal = new Vector3(
                    axisX.x * nx + axisY.x * ny + axisZ.x * nz,
                    axisX.y * nx + axisY.y * ny + axisZ.y * nz,
                    axisX.z * nx + axisY.z * ny + axisZ.z * nz);
                // The outside count is 1..3 whenever the offset exceeds Epsilon; the
                // clamp keeps a float corner case (two mismatches just under Epsilon)
                // from indexing below the first feature.
                if (outside <= 1) feature = ContactCodes.FeatureFace;
                else if (outside == 2) feature = ContactCodes.FeatureEdge;
                else feature = ContactCodes.FeatureCorner;
                closestPoint = ToWorld(new Vector3(clampedX, clampedY, clampedZ), center,
                    axisX, axisY, axisZ);
                distance = offset;
                return true;
            }

            // The centre is inside (or exactly on a face): push out through the nearest
            // face. The penetration is measured from that face, hence the negative
            // distance.
            int bestAxis = 0;
            float bestGap = halfX - Mathf.Abs(local.x);
            float gapY = halfY - Mathf.Abs(local.y);
            if (gapY < bestGap)
            {
                bestGap = gapY;
                bestAxis = 1;
            }
            float gapZ = halfZ - Mathf.Abs(local.z);
            if (gapZ < bestGap)
            {
                bestGap = gapZ;
                bestAxis = 2;
            }

            if (bestAxis == 0)
            {
                float sign = local.x < 0f ? -1f : 1f;
                normal = new Vector3(axisX.x * sign, axisX.y * sign, axisX.z * sign);
                closestPoint = ToWorld(new Vector3(halfX * sign, clampedY, clampedZ), center,
                    axisX, axisY, axisZ);
            }
            else if (bestAxis == 1)
            {
                float sign = local.y < 0f ? -1f : 1f;
                normal = new Vector3(axisY.x * sign, axisY.y * sign, axisY.z * sign);
                closestPoint = ToWorld(new Vector3(clampedX, halfY * sign, clampedZ), center,
                    axisX, axisY, axisZ);
            }
            else
            {
                float sign = local.z < 0f ? -1f : 1f;
                normal = new Vector3(axisZ.x * sign, axisZ.y * sign, axisZ.z * sign);
                closestPoint = ToWorld(new Vector3(clampedX, clampedY, halfZ * sign), center,
                    axisX, axisY, axisZ);
            }
            distance = -bestGap;
            feature = ContactCodes.FeatureInside;
            return true;
        }

        /// <summary>
        /// The contact angle theta in degrees: <c>acos(clamp(n.y, -1, 1))</c>.
        /// </summary>
        /// <remarks>
        /// 0 deg is a floor, 90 deg a wall, 180 deg a ceiling. The clamp keeps float
        /// noise on a unit normal from making acos undefined; a non-finite normal
        /// propagates a non-finite angle, so callers validate the normal upstream
        /// (the contact constructors do).
        /// </remarks>
        public static float ContactAngleDeg(Vector3 normal)
        {
            return Mathf.Acos(Mathf.Clamp(normal.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Registers a sphere-vs-OBB contact when the centre is within radius + skin.
        /// </summary>
        /// <remarks>
        /// False means "no contact" or invalid input (distance beyond the skin band, a
        /// non-finite or negative radius/skin, or an invalid box); every out value is
        /// then zero. The surface id and role travel outside this function - the caller
        /// owns the table and copies them, so the math stays a pure function.
        /// </remarks>
        public static bool TrySphereObbContact(Vector3 center, Vector3 axisX, Vector3 axisY,
            Vector3 axisZ, Vector3 halfExtents, Vector3 sphereCenter, float radius,
            out Vector3 normal, out Vector3 point, out float depth, out int feature,
            out float angleDeg, out int contactClass, float skin = ContactTuning.ContactSkinM)
        {
            normal = Vector3.zero;
            point = Vector3.zero;
            depth = 0f;
            feature = ContactCodes.FeatureInside;
            angleDeg = 0f;
            contactClass = ContactCodes.ClassNone;
            if (!KatamariMath.IsFiniteFloat(radius) || radius < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(skin) || skin < 0f) return false;

            Vector3 closestPoint;
            float distance;
            int closestFeature;
            if (!TryClosestPointOnObb(center, axisX, axisY, axisZ, halfExtents, sphereCenter,
                out closestPoint, out distance, out normal, out closestFeature)) return false;
            if (distance > radius + skin) return false;

            point = closestPoint;
            feature = closestFeature;
            depth = radius - distance;
            angleDeg = ContactAngleDeg(normal);
            contactClass = ContactClassificationMath.ClassifyContact(normal);
            return true;
        }

        /// <summary>
        /// Registers a sphere-vs-one-sided-plane contact (the free side is
        /// <c>dot(normal, x) &gt;= offset</c>).
        /// </summary>
        /// <remarks>
        /// False means "no contact" or invalid input (a degenerate or non-finite plane
        /// normal, a non-finite offset/radius/skin). The model normalises the direction
        /// but leaves the offset alone, and this port does the same: the offset is only a
        /// distance when the caller's normal is unit length. A plane contact is always a
        /// face contact (<see cref="ContactCodes.FeatureFace"/>), and the point is the
        /// foot of the perpendicular from the centre.
        /// </remarks>
        public static bool TryPlaneContact(Vector3 planeNormal, float planeOffset,
            Vector3 sphereCenter, float radius, out Vector3 normal, out Vector3 point,
            out float depth, out float angleDeg, out int contactClass,
            float skin = ContactTuning.ContactSkinM)
        {
            normal = Vector3.zero;
            point = Vector3.zero;
            depth = 0f;
            angleDeg = 0f;
            contactClass = ContactCodes.ClassNone;
            if (!KatamariMath.IsFiniteFloat(planeOffset)) return false;
            if (!KatamariMath.IsFiniteFloat(radius) || radius < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(skin) || skin < 0f) return false;
            if (!KatamariMath.IsFiniteVector(sphereCenter)) return false;

            Vector3 unitNormal;
            float length;
            if (!KatamariMath.TryNormalize(planeNormal, out unitNormal, out length)) return false;

            float signedDistance = unitNormal.x * sphereCenter.x + unitNormal.y * sphereCenter.y
                + unitNormal.z * sphereCenter.z - planeOffset;
            if (signedDistance > radius + skin) return false;

            normal = unitNormal;
            point = new Vector3(sphereCenter.x - unitNormal.x * signedDistance,
                sphereCenter.y - unitNormal.y * signedDistance,
                sphereCenter.z - unitNormal.z * signedDistance);
            depth = radius - signedDistance;
            angleDeg = ContactAngleDeg(unitNormal);
            contactClass = ContactClassificationMath.ClassifyContact(unitNormal);
            return true;
        }

        /// <summary>True when an OBB description is usable (finite, unit, orthogonal).</summary>
        /// <remarks>
        /// The same three checks the reference model makes in every closest-point call:
        /// the axes are unit length, the three pairwise dots are zero, the half extents
        /// are finite and nonnegative, and the centre is finite. Shear (a non-uniformly
        /// scaled parent) violates it on purpose: the arena bake must refuse that box
        /// rather than query a box the author did not draw.
        /// </remarks>
        public static bool IsValidObb(Vector3 center, Vector3 axisX, Vector3 axisY,
            Vector3 axisZ, Vector3 halfExtents)
        {
            if (!KatamariMath.IsFiniteVector(center)) return false;
            if (!IsUnitAxis(axisX) || !IsUnitAxis(axisY) || !IsUnitAxis(axisZ)) return false;
            if (Mathf.Abs(Dot(axisX, axisY)) > AxisTolerance) return false;
            if (Mathf.Abs(Dot(axisX, axisZ)) > AxisTolerance) return false;
            if (Mathf.Abs(Dot(axisY, axisZ)) > AxisTolerance) return false;
            if (!KatamariMath.IsFiniteVector(halfExtents)) return false;
            if (halfExtents.x < 0f || halfExtents.y < 0f || halfExtents.z < 0f) return false;
            return true;
        }

        /// <summary>Bounding-sphere radius of a box: |halfExtents|.</summary>
        public static float BoundingRadius(Vector3 halfExtents)
        {
            return Mathf.Sqrt(halfExtents.x * halfExtents.x + halfExtents.y * halfExtents.y
                + halfExtents.z * halfExtents.z);
        }

        private static bool IsUnitAxis(Vector3 axis)
        {
            if (!KatamariMath.IsFiniteVector(axis)) return false;
            float length = Mathf.Sqrt(Dot(axis, axis));
            return Mathf.Abs(length - 1f) <= AxisTolerance;
        }

        private static float Dot(Vector3 a, Vector3 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }
    }
}
