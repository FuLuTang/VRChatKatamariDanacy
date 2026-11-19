using UnityEngine;

namespace VRCKatamari.BallContact
{
    /// <summary>Builds bounded line pairs for the local collision display.</summary>
    public static class CollisionWireMath
    {
        public const int StaticBoxes = 0;
        public const int Terrain = 1;
        public const int Props = 2;

        /// <summary>Line endpoints per layer: 128 boxes, 768 triangles, 44 spheres (26 capsules).</summary>
        public const int StaticVertexCapacity = 128 * 24;
        public const int TerrainVertexCapacity = 768 * 6;
        public const int PropVertexCapacity = 44 * 72;

        /// <summary>Shown radius around the ball centre: 5 m, or three ball diameters once larger.</summary>
        public const float MinRangeM = 5f;
        public const float RangePerDiameter = 3f;

        public static float WindowRange(float ballDiameterM)
        {
            float range = ballDiameterM * RangePerDiameter;
            if (!(range > MinRangeM)) return MinRangeM;
            return range;
        }

        public static bool OverlapsWindow(Vector3 lo, Vector3 hi, Vector3 p, float r)
        {
            return lo.x <= p.x + r && hi.x >= p.x - r
                && lo.y <= p.y + r && hi.y >= p.y - r
                && lo.z <= p.z + r && hi.z >= p.z - r;
        }

        public static bool TriangleInWindow(Vector3 a, Vector3 b, Vector3 c, Vector3 p, float r)
        {
            return OverlapsWindow(Vector3.Min(a, Vector3.Min(b, c)),
                Vector3.Max(a, Vector3.Max(b, c)), p, r);
        }

        private static int AppendLine(Vector3[] dst, int at, Vector3 a, Vector3 b)
        {
            dst[at] = a;
            dst[at + 1] = b;
            return at + 2;
        }

        public static int AppendTriangle(Vector3[] dst, int at, Vector3 a, Vector3 b, Vector3 c)
        {
            if (at + 6 > dst.Length) return at;
            at = AppendLine(dst, at, a, b);
            at = AppendLine(dst, at, b, c);
            return AppendLine(dst, at, c, a);
        }

        public static int AppendBox(Vector3[] dst, int at, Vector3 center, Vector3 axisX,
            Vector3 axisY, Vector3 axisZ, Vector3 half)
        {
            if (at + 24 > dst.Length) return at;
            Vector3 x = axisX * half.x;
            Vector3 y = axisY * half.y;
            Vector3 z = axisZ * half.z;
            for (int sign = -1; sign <= 1; sign += 2)
            {
                Vector3 c = center + y * sign;
                Vector3 a = c - x - z;
                Vector3 b = c + x - z;
                Vector3 d = c - x + z;
                Vector3 e = c + x + z;
                at = AppendLine(dst, at, a, b);
                at = AppendLine(dst, at, b, e);
                at = AppendLine(dst, at, e, d);
                at = AppendLine(dst, at, d, a);
                if (sign == -1)
                {
                    at = AppendLine(dst, at, a, a + y * 2f);
                    at = AppendLine(dst, at, b, b + y * 2f);
                    at = AppendLine(dst, at, d, d + y * 2f);
                    at = AppendLine(dst, at, e, e + y * 2f);
                }
            }
            return at;
        }

        public static int AppendRound(Vector3[] dst, int at, Vector3 center, Vector3 x,
            Vector3 y, Vector3 z, float radius, float halfHeight, int axis)
        {
            bool capsule = halfHeight > 0f;
            if (at + (capsule ? 120 : 72) > dst.Length) return at;
            Vector3 axial = axis == 0 ? x : axis == 1 ? y : z;
            Vector3 u = axis == 0 ? y : z;
            Vector3 v = axis == 0 || axis == 1 ? (axis == 0 ? z : x) : y;
            if (axis == 2) u = x;
            Vector3 span = axial * halfHeight;
            at = AppendRing(dst, at, center + span, u, v, radius, 0, 12);
            if (capsule) at = AppendRing(dst, at, center - span, u, v, radius, 0, 12);
            if (!capsule)
            {
                at = AppendRing(dst, at, center, u, axial, radius, 0, 12);
                return AppendRing(dst, at, center, v, axial, radius, 0, 12);
            }
            at = AppendRing(dst, at, center + span, u, axial, radius, 0, 6);
            at = AppendRing(dst, at, center + span, v, axial, radius, 0, 6);
            at = AppendRing(dst, at, center - span, u, axial, radius, 6, 6);
            at = AppendRing(dst, at, center - span, v, axial, radius, 6, 6);
            at = AppendLine(dst, at, center + span + u * radius, center - span + u * radius);
            at = AppendLine(dst, at, center + span - u * radius, center - span - u * radius);
            at = AppendLine(dst, at, center + span + v * radius, center - span + v * radius);
            return AppendLine(dst, at, center + span - v * radius, center - span - v * radius);
        }

        private static int AppendRing(Vector3[] dst, int at, Vector3 center, Vector3 a,
            Vector3 b, float radius, int first, int count)
        {
            const float angleStep = Mathf.PI / 6f;
            float start = first * angleStep;
            Vector3 previous = center + radius * (a * Mathf.Cos(start) + b * Mathf.Sin(start));
            for (int i = first + 1; i <= first + count; i++)
            {
                float angle = i * angleStep;
                Vector3 point = center + radius * (a * Mathf.Cos(angle) + b * Mathf.Sin(angle));
                at = AppendLine(dst, at, previous, point);
                previous = point;
            }
            return at;
        }
    }
}
