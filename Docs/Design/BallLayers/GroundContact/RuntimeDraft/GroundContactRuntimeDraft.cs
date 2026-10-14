// RuntimeDraft only: plain static C# candidate, not a MonoBehaviour/component.
// NOT compiled by Unity or UdonSharp. See TEST_OBJECT_SPEC.md and the coordinate gate.
using UnityEngine;

namespace VrcKatamari.RuntimeDraft
{
    public struct GroundMatrix3Draft
    {
        public float m00, m01, m02;
        public float m10, m11, m12;
        public float m20, m21, m22;
    }

    public struct GroundContactDraftResult
    {
        public float supportHeight;
        public Vector3 centerPosition;
        public float centerNormalCoordinate;
        public Vector3 planeAnchor;
        public float appliedNormalCorrection;
        public float normalSpeedPerturbation;
        public bool liftClamped;
        public bool dropClamped;
    }

    public static class GroundContactRuntimeDraft
    {
        private const float MatrixTolerance = 1e-5f;
        private const float MinimumNorm = 1e-12f;

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool Finite(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }

        private static bool Dot(Vector3 a, Vector3 b, out float value)
        {
            value = a.x * b.x + a.y * b.y + a.z * b.z;
            return Finite(value);
        }

        private static bool Unit(Vector3 input, out Vector3 unit, out float length)
        {
            unit = Vector3.zero;
            length = 0f;
            if (!Finite(input)) return false;
            float scale = Mathf.Max(Mathf.Abs(input.x), Mathf.Max(Mathf.Abs(input.y), Mathf.Abs(input.z)));
            if (!Finite(scale) || scale <= 0f) return false;
            Vector3 scaled = input / scale;
            float norm = Mathf.Sqrt(scaled.x * scaled.x + scaled.y * scaled.y + scaled.z * scaled.z);
            if (!Finite(norm) || norm <= 0f) return false;
            length = scale * norm;
            if (!Finite(length) || length <= MinimumNorm) return false;
            unit = scaled / norm;
            return Finite(unit);
        }

        private static Vector3 Row(GroundMatrix3Draft m, int row)
        {
            if (row == 0) return new Vector3(m.m00, m.m01, m.m02);
            if (row == 1) return new Vector3(m.m10, m.m11, m.m12);
            return new Vector3(m.m20, m.m21, m.m22);
        }

        private static bool ProperRotation(GroundMatrix3Draft m)
        {
            Vector3 r0 = Row(m, 0);
            Vector3 r1 = Row(m, 1);
            Vector3 r2 = Row(m, 2);
            if (!Finite(r0) || !Finite(r1) || !Finite(r2)) return false;
            float a, b, c, d, e, f;
            if (!Dot(r0, r0, out a) || !Dot(r1, r1, out b) || !Dot(r2, r2, out c) ||
                !Dot(r0, r1, out d) || !Dot(r0, r2, out e) || !Dot(r1, r2, out f)) return false;
            if (Mathf.Abs(a - 1f) > MatrixTolerance || Mathf.Abs(b - 1f) > MatrixTolerance ||
                Mathf.Abs(c - 1f) > MatrixTolerance || Mathf.Abs(d) > MatrixTolerance ||
                Mathf.Abs(e) > MatrixTolerance || Mathf.Abs(f) > MatrixTolerance) return false;
            float determinant =
                m.m00 * (m.m11 * m.m22 - m.m12 * m.m21) -
                m.m01 * (m.m10 * m.m22 - m.m12 * m.m20) +
                m.m02 * (m.m10 * m.m21 - m.m11 * m.m20);
            return Finite(determinant) && Mathf.Abs(determinant - 1f) <= MatrixTolerance;
        }

        private static bool Multiply(GroundMatrix3Draft m, Vector3 v, out Vector3 result)
        {
            result = Vector3.zero;
            result.x = m.m00 * v.x + m.m01 * v.y + m.m02 * v.z;
            result.y = m.m10 * v.x + m.m11 * v.y + m.m12 * v.z;
            result.z = m.m20 * v.x + m.m21 * v.y + m.m22 * v.z;
            return Finite(result);
        }

        // Returns false for invalid input; no mutable component or physics object is owned here.
        public static bool TrySolve(float coreRadius, GroundMatrix3Draft localToWorld,
            Vector3[] localProxyPoints, Vector3 rawPlaneNormal, float rawPlaneOffset,
            Vector3 currentCenter, float maxLiftPerStep, float maxDropPerStep,
            float requestedNormalSpeedPerturbation, float maxNormalSpeedPerturbation,
            out GroundContactDraftResult result)
        {
            result = new GroundContactDraftResult();
            if (!Finite(coreRadius) || coreRadius < 0f || !ProperRotation(localToWorld) ||
                !Finite(rawPlaneOffset) || !Finite(currentCenter) ||
                !Finite(maxLiftPerStep) || maxLiftPerStep < 0f ||
                !Finite(maxDropPerStep) || maxDropPerStep < 0f ||
                !Finite(requestedNormalSpeedPerturbation) ||
                !Finite(maxNormalSpeedPerturbation) || maxNormalSpeedPerturbation < 0f ||
                localProxyPoints == null) return false;

            Vector3 normal;
            float normalLength;
            if (!Unit(rawPlaneNormal, out normal, out normalLength)) return false;
            float offset = rawPlaneOffset / normalLength;
            if (!Finite(offset)) return false;

            float support = coreRadius;
            for (int i = 0; i < localProxyPoints.Length; i++)
            {
                Vector3 point = localProxyPoints[i];
                if (!Finite(point)) return false;
                Vector3 worldDirection;
                if (!Multiply(localToWorld, point, out worldDirection)) return false;
                float projection;
                if (!Dot(normal, worldDirection, out projection)) return false;
                support = Mathf.Max(support, -projection);
                if (!Finite(support)) return false;
            }

            float currentCoordinate;
            if (!Dot(normal, currentCenter, out currentCoordinate)) return false;
            float targetCoordinate = offset + support;
            float requestedCorrection = targetCoordinate - currentCoordinate;
            if (!Finite(targetCoordinate) || !Finite(requestedCorrection)) return false;
            float correction = Mathf.Clamp(requestedCorrection, -maxDropPerStep, maxLiftPerStep);
            Vector3 center = currentCenter + normal * correction;
            float centerCoordinate;
            if (!Finite(center) || !Dot(normal, center, out centerCoordinate)) return false;
            Vector3 anchor = normal * offset;
            if (!Finite(anchor)) return false;

            result.supportHeight = support;
            result.centerPosition = center;
            result.centerNormalCoordinate = centerCoordinate;
            result.planeAnchor = anchor;
            result.appliedNormalCorrection = correction;
            result.normalSpeedPerturbation = Mathf.Clamp(requestedNormalSpeedPerturbation,
                -maxNormalSpeedPerturbation, maxNormalSpeedPerturbation);
            result.liftClamped = requestedCorrection > maxLiftPerStep;
            result.dropClamped = requestedCorrection < -maxDropPerStep;
            return Finite(result.normalSpeedPerturbation);
        }
    }
}
