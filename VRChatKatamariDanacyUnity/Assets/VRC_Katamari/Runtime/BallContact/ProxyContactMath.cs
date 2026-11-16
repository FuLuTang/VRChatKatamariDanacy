using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallContact
{
    /// <summary>
    /// The collider-proxy primitive: sphere, capsule, box and a &lt;=24-point convex hull,
    /// with the exact closest point and the sphere-vs-proxy contact record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the runtime side of <c>Docs/Design/PickupAbsorb/COLLIDER_PROXY.md</c> §6. The
    /// placement block is authored in the prop's local space; <see cref="TryBakeProxy"/>
    /// turns it into world-space arrays once per selected prop, and the per-step solver only
    /// reads those arrays. Nothing here allocates, reads scene state or keeps static state.
    /// </para>
    /// <para>
    /// Boxes are NOT re-implemented: every box branch delegates to
    /// <see cref="ObbContactMath"/> so the v2 box normal/depth/feature convention stays
    /// bit-for-bit the one the old prop block produced. A sphere/capsule/convex branch only
    /// has to supply the closest point; the contact record then uses the same normal, depth,
    /// angle and class rules.
    /// </para>
    /// <para>
    /// UdonSharp limits: static methods only, scalars/Vector3/Quaternion and caller-owned
    /// arrays, no structs, no overloads, no properties, no allocation. The face arrays are
    /// triangulated at bake time by the authoring sync, not here: a 24-point convex hull has
    /// at most 2*24-4 = 44 triangular faces.
    /// </para>
    /// </remarks>
    public static class ProxyContactMath
    {
        public const int KindBox = 0;
        public const int KindSphere = 1;
        public const int KindCapsule = 2;
        public const int KindConvex = 3;

        public const int AxisX = 0;
        public const int AxisY = 1;
        public const int AxisZ = 2;

        public const int MaxHullPoints = 24;
        public const int MaxHullFaces = 44;
        public const int MaxProxyCount = 12;

        private const float Epsilon = 1e-6f;
        private const float AxisTolerance = 1e-5f;

        // ------------------------------------------------------------------ validation ---

        /// <summary>True for a kind code this class understands.</summary>
        public static bool IsKnownKind(int kind)
        {
            return kind >= KindBox && kind <= KindConvex;
        }

        /// <summary>
        /// A cheap shape validation: finite centre/axes, the kind's own required fields,
        /// and a hull/face count within the fixed capacities. The per-face contents are
        /// validated by the authoring sync and again by <see cref="TryClosestPointOnProxy"/>
        /// before a face is read.
        /// </summary>
        public static bool IsValidProxy(int kind, Vector3 center, Vector3 axisX,
            Vector3 axisY, Vector3 axisZ, Vector3 halfExtents, float radius, float halfHeight,
            int axis, int hullCount, int faceCount)
        {
            if (!IsKnownKind(kind)) return false;
            if (!KatamariMath.IsFiniteVector(center)) return false;
            if (kind == KindBox)
            {
                return ObbContactMath.IsValidObb(center, axisX, axisY, axisZ, halfExtents);
            }

            if (kind == KindSphere)
            {
                // Only a sphere/capsule uses the radius; a convex block legitimately carries
                // 0 there, and requiring > 0 for every non-box kind made ConvexTetra fail.
                return KatamariMath.IsFiniteFloat(radius) && radius > 0f;
            }
            if (kind == KindCapsule)
            {
                if (!KatamariMath.IsFiniteFloat(radius) || radius <= 0f) return false;
                if (!KatamariMath.IsFiniteFloat(halfHeight) || halfHeight < 0f) return false;
                if (axis < AxisX || axis > AxisZ) return false;
                return IsUnitAxis(AxisVector(axis, axisX, axisY, axisZ));
            }

            // Convex: the point/face budget is fixed and a degenerate hull cannot collide.
            // Radius is unused, so it only has to be finite (a 0 is normal).
            if (!KatamariMath.IsFiniteFloat(radius)) return false;
            if (hullCount < 4 || hullCount > MaxHullPoints) return false;
            if (faceCount < 1 || faceCount > MaxHullFaces) return false;
            return true;
        }

        /// <summary>Bounding-sphere radius of one baked proxy.</summary>
        public static float BoundingRadius(int kind, Vector3 halfExtents, float radius,
            float halfHeight, Vector3[] hullPoints, int hullStart, int hullCount,
            Vector3 center)
        {
            if (kind == KindBox)
            {
                return Mathf.Sqrt(halfExtents.x * halfExtents.x +
                    halfExtents.y * halfExtents.y + halfExtents.z * halfExtents.z);
            }

            if (kind == KindSphere) return radius;
            if (kind == KindCapsule) return halfHeight + radius;

            float best = 0f;
            if (hullPoints == null || hullStart < 0 || hullCount <= 0) return best;
            for (int i = 0; i < hullCount && hullStart + i < hullPoints.Length; i++)
            {
                Vector3 delta = hullPoints[hullStart + i] - center;
                float distance = Mathf.Sqrt(delta.x * delta.x + delta.y * delta.y +
                    delta.z * delta.z);
                if (distance > best) best = distance;
            }

            return best;
        }

        // ----------------------------------------------------------------------- bake ---

        /// <summary>
        /// Bakes one local collider block into world-space arrays. Called when a prop is
        /// selected into the 12-slot prop block, never per contact.
        /// </summary>
        /// <remarks>
        /// The local hull points are relative to <paramref name="localCenter"/> (the placement
        /// schema's convention); they are transformed by the prop's world rotation and then
        /// offset by the world centre once. The face indices are local to this entry
        /// (<c>0..hullCount-1</c>) and the caller stores them with the entry.
        /// </remarks>
        public static bool TryBakeProxy(
            int kind,
            Vector3 propPosition,
            Quaternion propRotation,
            Vector3 localCenter,
            Vector3 localSize,
            float localRadius,
            float localHalfHeight,
            int localAxis,
            Vector3[] localHull,
            int localHullStart,
            int localHullCount,
            int[] localFaces,
            int localFaceStart,
            int localFaceCount,
            Vector3[] hullWorld,
            int hullWorldStart,
            int hullCapacity,
            Vector3[] faceNormals,
            float[] faceOffsets,
            int[] faceA,
            int[] faceB,
            int[] faceC,
            int faceOutputStart,
            int faceCapacity,
            out Vector3 center,
            out Vector3 axisX,
            out Vector3 axisY,
            out Vector3 axisZ,
            out Vector3 halfExtents,
            out float radius,
            out float halfHeight,
            out int axis,
            out int hullCount,
            out int faceCount,
            out float boundingRadius)
        {
            center = Vector3.zero;
            axisX = Vector3.zero;
            axisY = Vector3.zero;
            axisZ = Vector3.zero;
            halfExtents = Vector3.zero;
            radius = 0f;
            halfHeight = 0f;
            axis = AxisX;
            hullCount = 0;
            faceCount = 0;
            boundingRadius = 0f;

            if (!IsKnownKind(kind)) return false;
            if (!KatamariMath.IsFiniteVector(propPosition)) return false;
            if (!KatamariMath.IsFiniteQuaternion(propRotation)) return false;
            if (!KatamariMath.IsFiniteVector(localCenter)) return false;

            center = propPosition + propRotation * localCenter;
            if (!KatamariMath.IsFiniteVector(center)) return false;
            axisX = propRotation * new Vector3(1f, 0f, 0f);
            axisY = propRotation * new Vector3(0f, 1f, 0f);
            axisZ = propRotation * new Vector3(0f, 0f, 1f);
            if (!IsUnitAxis(axisX) || !IsUnitAxis(axisY) || !IsUnitAxis(axisZ)) return false;

            if (kind == KindBox)
            {
                if (!KatamariMath.IsFiniteVector(localSize)) return false;
                if (localSize.x < 0f || localSize.y < 0f || localSize.z < 0f) return false;
                halfExtents = new Vector3(localSize.x * 0.5f, localSize.y * 0.5f,
                    localSize.z * 0.5f);
                boundingRadius = BoundingRadius(kind, halfExtents, 0f, 0f, null, 0, 0, center);
                return IsValidProxy(kind, center, axisX, axisY, axisZ, halfExtents, 0f, 0f,
                    0, 0, 0) && KatamariMath.IsFiniteFloat(boundingRadius);
            }

            if (!KatamariMath.IsFiniteFloat(localRadius) || localRadius <= 0f) return false;
            radius = localRadius;
            if (kind == KindSphere)
            {
                boundingRadius = radius;
                return IsValidProxy(kind, center, axisX, axisY, axisZ, halfExtents, radius,
                    0f, 0, 0, 0);
            }

            if (kind == KindCapsule)
            {
                if (!KatamariMath.IsFiniteFloat(localHalfHeight) ||
                    localHalfHeight < 0f) return false;
                if (localAxis < AxisX || localAxis > AxisZ) return false;
                halfHeight = localHalfHeight;
                axis = localAxis;
                boundingRadius = radius + halfHeight;
                return IsValidProxy(kind, center, axisX, axisY, axisZ, halfExtents, radius,
                    halfHeight, axis, 0, 0) && KatamariMath.IsFiniteFloat(boundingRadius);
            }

            // Convex.
            if (localHull == null || localFaces == null || hullWorld == null ||
                faceNormals == null || faceOffsets == null || faceA == null ||
                faceB == null || faceC == null) return false;
            if (localHullCount < 4 || localHullCount > MaxHullPoints) return false;
            if (hullWorldStart < 0 || localHullCount > hullCapacity ||
                hullWorldStart + localHullCount > hullWorld.Length) return false;
            if (localHullStart < 0 ||
                localHullStart + localHullCount > localHull.Length) return false;
            if (localFaceCount < 1 || localFaceCount > MaxHullFaces) return false;
            if (faceOutputStart < 0 || localFaceCount > faceCapacity ||
                faceOutputStart + localFaceCount > faceNormals.Length ||
                faceOutputStart + localFaceCount > faceOffsets.Length ||
                faceOutputStart + localFaceCount > faceA.Length ||
                faceOutputStart + localFaceCount > faceB.Length ||
                faceOutputStart + localFaceCount > faceC.Length) return false;
            if (localFaceStart < 0 ||
                localFaceStart + localFaceCount * 3 > localFaces.Length) return false;

            hullCount = localHullCount;
            for (int i = 0; i < hullCount; i++)
            {
                Vector3 local = localHull[localHullStart + i];
                if (!KatamariMath.IsFiniteVector(local)) return false;
                Vector3 world = center + propRotation * local;
                if (!KatamariMath.IsFiniteVector(world)) return false;
                hullWorld[hullWorldStart + i] = world;
            }

            faceCount = localFaceCount;
            for (int i = 0; i < faceCount; i++)
            {
                int baseIndex = localFaceStart + i * 3;
                int a = localFaces[baseIndex];
                int b = localFaces[baseIndex + 1];
                int c = localFaces[baseIndex + 2];
                if (a < 0 || b < 0 || c < 0 || a >= hullCount || b >= hullCount ||
                    c >= hullCount) return false;
                Vector3 va = hullWorld[hullWorldStart + a];
                Vector3 vb = hullWorld[hullWorldStart + b];
                Vector3 vc = hullWorld[hullWorldStart + c];
                Vector3 normal;
                if (!TryNormalize(Cross(vb - va, vc - va), out normal)) return false;
                faceNormals[faceOutputStart + i] = normal;
                faceOffsets[faceOutputStart + i] = Dot(normal, va);
                faceA[faceOutputStart + i] = a;
                faceB[faceOutputStart + i] = b;
                faceC[faceOutputStart + i] = c;
            }

            boundingRadius = BoundingRadius(kind, halfExtents, radius, halfHeight, hullWorld,
                hullWorldStart, hullCount, center);
            return IsValidProxy(kind, center, axisX, axisY, axisZ, halfExtents, radius,
                halfHeight, axis, hullCount, faceCount) &&
                KatamariMath.IsFiniteFloat(boundingRadius);
        }

        // ---------------------------------------------------------------- closest point ---

        /// <summary>
        /// Exact closest point on one baked proxy, following the box convention: a point
        /// inside returns a negative distance and the outward face normal; a point outside
        /// returns the Euclidean distance and the direction from the closest point to it.
        /// </summary>
        public static bool TryClosestPointOnProxy(
            int kind,
            Vector3 center,
            Vector3 axisX,
            Vector3 axisY,
            Vector3 axisZ,
            Vector3 halfExtents,
            float radius,
            float halfHeight,
            int axis,
            Vector3[] hullPoints,
            int hullStart,
            int hullCount,
            Vector3[] faceNormals,
            float[] faceOffsets,
            int[] faceA,
            int[] faceB,
            int[] faceC,
            int faceStart,
            int faceCount,
            Vector3 point,
            out Vector3 closestPoint,
            out float distance,
            out Vector3 normal,
            out int feature)
        {
            closestPoint = Vector3.zero;
            distance = 0f;
            normal = Vector3.zero;
            feature = ContactCodes.FeatureInside;

            if (!KatamariMath.IsFiniteVector(point)) return false;

            if (kind == KindBox)
            {
                return ObbContactMath.TryClosestPointOnObb(center, axisX, axisY, axisZ,
                    halfExtents, point, out closestPoint, out distance, out normal,
                    out feature);
            }

            if (!IsValidProxy(kind, center, axisX, axisY, axisZ, halfExtents, radius,
                halfHeight, axis, hullCount, faceCount)) return false;

            if (kind == KindSphere)
            {
                Vector3 delta = point - center;
                float length = Length(delta);
                if (length <= Epsilon)
                {
                    normal = new Vector3(0f, 1f, 0f);
                    closestPoint = center + normal * radius;
                    distance = -radius;
                    feature = ContactCodes.FeatureInside;
                    return true;
                }

                normal = delta * (1f / length);
                closestPoint = center + normal * radius;
                distance = length - radius;
                feature = ContactCodes.FeatureFace;
                return true;
            }

            if (kind == KindCapsule)
            {
                Vector3 direction = AxisVector(axis, axisX, axisY, axisZ);
                if (!IsUnitAxis(direction)) return false;
                float along = Dot(point - center, direction);
                if (along < -halfHeight) along = -halfHeight;
                if (along > halfHeight) along = halfHeight;
                Vector3 segmentPoint = center + direction * along;
                Vector3 delta = point - segmentPoint;
                float length = Length(delta);
                if (length <= Epsilon)
                {
                    normal = direction;
                    closestPoint = segmentPoint + normal * radius;
                    distance = -radius;
                    feature = ContactCodes.FeatureInside;
                    return true;
                }

                normal = delta * (1f / length);
                closestPoint = segmentPoint + normal * radius;
                distance = length - radius;
                feature = Mathf.Abs(along) < halfHeight - Epsilon
                    ? ContactCodes.FeatureFace : ContactCodes.FeatureCorner;
                return true;
            }

            // Convex: face planes first. If the point is inside, the face with the largest
            // signed plane distance is the nearest face; otherwise the closest point is the
            // minimum over the triangles.
            float largestSigned = -float.MaxValue;
            int largestFace = -1;
            for (int i = 0; i < faceCount; i++)
            {
                Vector3 faceNormal = faceNormals[faceStart + i];
                if (!KatamariMath.IsFiniteVector(faceNormal)) return false;
                float signed = Dot(faceNormal, point) - faceOffsets[faceStart + i];
                if (!KatamariMath.IsFiniteFloat(signed)) return false;
                if (signed > largestSigned)
                {
                    largestSigned = signed;
                    largestFace = i;
                }
            }

            if (largestFace < 0) return false;
            if (largestSigned <= Epsilon)
            {
                int a = faceA[faceStart + largestFace];
                int b = faceB[faceStart + largestFace];
                int c = faceC[faceStart + largestFace];
                Vector3 va;
                Vector3 vb;
                Vector3 vc;
                if (!TryHullPoint(hullPoints, hullStart, hullCount, a, out va)) return false;
                if (!TryHullPoint(hullPoints, hullStart, hullCount, b, out vb)) return false;
                if (!TryHullPoint(hullPoints, hullStart, hullCount, c, out vc)) return false;
                ClosestPointOnTriangle(point, va, vb, vc, out closestPoint);
                normal = faceNormals[faceStart + largestFace];
                distance = largestSigned;
                feature = ContactCodes.FeatureInside;
                return true;
            }

            float bestSquared = float.MaxValue;
            Vector3 bestPoint = Vector3.zero;
            Vector3 bestNormal = Vector3.zero;
            for (int i = 0; i < faceCount; i++)
            {
                int a = faceA[faceStart + i];
                int b = faceB[faceStart + i];
                int c = faceC[faceStart + i];
                Vector3 va;
                Vector3 vb;
                Vector3 vc;
                if (!TryHullPoint(hullPoints, hullStart, hullCount, a, out va)) return false;
                if (!TryHullPoint(hullPoints, hullStart, hullCount, b, out vb)) return false;
                if (!TryHullPoint(hullPoints, hullStart, hullCount, c, out vc)) return false;
                Vector3 candidate;
                ClosestPointOnTriangle(point, va, vb, vc, out candidate);
                Vector3 delta = point - candidate;
                float squared = Dot(delta, delta);
                if (squared < bestSquared)
                {
                    bestSquared = squared;
                    bestPoint = candidate;
                    bestNormal = faceNormals[faceStart + i];
                }
            }

            if (bestSquared == float.MaxValue) return false;
            closestPoint = bestPoint;
            distance = Mathf.Sqrt(bestSquared);
            if (distance > Epsilon)
            {
                normal = (point - closestPoint) * (1f / distance);
            }
            else
            {
                normal = bestNormal;
            }

            feature = ContactCodes.FeatureFace;
            return true;
        }

        // --------------------------------------------------------------------- contact ---

        /// <summary>Sphere-vs-one-proxy contact record; a box delegates to ObbContactMath.</summary>
        public static bool TrySphereProxyContact(
            int kind,
            Vector3 center,
            Vector3 axisX,
            Vector3 axisY,
            Vector3 axisZ,
            Vector3 halfExtents,
            float radius,
            float halfHeight,
            int axis,
            Vector3[] hullPoints,
            int hullStart,
            int hullCount,
            Vector3[] faceNormals,
            float[] faceOffsets,
            int[] faceA,
            int[] faceB,
            int[] faceC,
            int faceStart,
            int faceCount,
            Vector3 sphereCenter,
            float sphereRadius,
            out Vector3 normal,
            out Vector3 point,
            out float depth,
            out int feature,
            out float angleDeg,
            out int contactClass,
            float skin = ContactTuning.ContactSkinM)
        {
            normal = Vector3.zero;
            point = Vector3.zero;
            depth = 0f;
            feature = ContactCodes.FeatureInside;
            angleDeg = 0f;
            contactClass = ContactCodes.ClassNone;

            if (kind == KindBox)
            {
                return ObbContactMath.TrySphereObbContact(center, axisX, axisY, axisZ,
                    halfExtents, sphereCenter, sphereRadius, out normal, out point, out depth,
                    out feature, out angleDeg, out contactClass, skin);
            }

            if (!KatamariMath.IsFiniteFloat(sphereRadius) || sphereRadius < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(skin) || skin < 0f) return false;

            Vector3 closest;
            float distance;
            int closestFeature;
            if (!TryClosestPointOnProxy(kind, center, axisX, axisY, axisZ, halfExtents,
                radius, halfHeight, axis, hullPoints, hullStart, hullCount, faceNormals,
                faceOffsets, faceA, faceB, faceC, faceStart, faceCount, sphereCenter,
                out closest, out distance, out normal, out closestFeature))
                return false;
            if (distance > sphereRadius + skin) return false;

            point = closest;
            feature = closestFeature;
            depth = sphereRadius - distance;
            angleDeg = ObbContactMath.ContactAngleDeg(normal);
            contactClass = ContactClassificationMath.ClassifyContact(normal);
            return true;
        }

        // ---------------------------------------------------------------------- helpers ---

        private static Vector3 AxisVector(int axis, Vector3 axisX, Vector3 axisY, Vector3 axisZ)
        {
            if (axis == AxisX) return axisX;
            if (axis == AxisY) return axisY;
            return axisZ;
        }

        private static bool IsUnitAxis(Vector3 axis)
        {
            if (!KatamariMath.IsFiniteVector(axis)) return false;
            float lengthSquared = Dot(axis, axis);
            return Mathf.Abs(lengthSquared - 1f) <= AxisTolerance;
        }

        private static float Dot(Vector3 a, Vector3 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }

        private static Vector3 Cross(Vector3 a, Vector3 b)
        {
            return new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z,
                a.x * b.y - a.y * b.x);
        }

        private static float Length(Vector3 value)
        {
            return Mathf.Sqrt(Dot(value, value));
        }

        private static bool TryNormalize(Vector3 value, out Vector3 normalized)
        {
            normalized = Vector3.zero;
            float length = Length(value);
            if (!KatamariMath.IsFiniteFloat(length) || length <= Epsilon) return false;
            normalized = value * (1f / length);
            return KatamariMath.IsFiniteVector(normalized);
        }

        private static bool TryHullPoint(Vector3[] hullPoints, int hullStart, int hullCount,
            int index, out Vector3 value)
        {
            value = Vector3.zero;
            if (hullPoints == null || index < 0 || index >= hullCount) return false;
            int at = hullStart + index;
            if (at < 0 || at >= hullPoints.Length) return false;
            value = hullPoints[at];
            return KatamariMath.IsFiniteVector(value);
        }

        /// <summary>Ericson's closest point on a triangle; returns via <paramref name="closest"/>.</summary>
        private static void ClosestPointOnTriangle(Vector3 point, Vector3 a, Vector3 b,
            Vector3 c, out Vector3 closest)
        {
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 ap = point - a;
            float d1 = Dot(ab, ap);
            float d2 = Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                closest = a;
                return;
            }

            Vector3 bp = point - b;
            float d3 = Dot(ab, bp);
            float d4 = Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                closest = b;
                return;
            }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                closest = a + ab * v;
                return;
            }

            Vector3 cp = point - c;
            float d5 = Dot(ab, cp);
            float d6 = Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                closest = c;
                return;
            }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                closest = a + ac * w;
                return;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                closest = b + (c - b) * w;
                return;
            }

            float denominator = va + vb + vc;
            if (Mathf.Abs(denominator) <= Epsilon)
            {
                // Degenerate triangle: the nearest vertex/edge is the best answer.
                Vector3 best = a;
                float bestSquared = Dot(point - a, point - a);
                float squared = Dot(point - b, point - b);
                if (squared < bestSquared) { best = b; bestSquared = squared; }
                squared = Dot(point - c, point - c);
                if (squared < bestSquared) { best = c; bestSquared = squared; }
                closest = best;
                return;
            }

            float vWeight = vb / denominator;
            float wWeight = vc / denominator;
            closest = a + ab * vWeight + ac * wWeight;
        }
    }
}
