// RuntimeDraft only: not compiled in Unity/UdonSharp; see the module gate notes.
// The scalar axis expression intentionally avoids Vector3.Cross. It implements
// the declared host kinematics; Unity Quaternion/Transform behavior still needs
// the independent arrow + contact-velocity probe before integration.
using UnityEngine;

namespace VrcKatamari.RuntimeDraft
{
    public static class VisualRollRuntimeDraft
    {
        private const float MinNorm = 1e-12f;
        private const float Epsilon = 1e-7f;

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool Finite(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }

        private static bool Length(Vector3 value, out float length)
        {
            length = 0f;
            float scale = Mathf.Max(Mathf.Abs(value.x), Mathf.Max(Mathf.Abs(value.y), Mathf.Abs(value.z)));
            if (!Finite(scale)) return false;
            if (scale == 0f) return true;
            float x = value.x / scale;
            float y = value.y / scale;
            float z = value.z / scale;
            float scaled = Mathf.Sqrt(x * x + y * y + z * z);
            length = scale * scaled;
            return Finite(length);
        }

        private static bool UnitVector(Vector3 value, out Vector3 unit)
        {
            unit = Vector3.zero;
            if (!Finite(value)) return false;
            float scale = Mathf.Max(Mathf.Abs(value.x), Mathf.Max(Mathf.Abs(value.y), Mathf.Abs(value.z)));
            if (!Finite(scale) || scale == 0f) return false;
            Vector3 scaled = value / scale;
            float norm = Mathf.Sqrt(scaled.x * scaled.x + scaled.y * scaled.y + scaled.z * scaled.z);
            if (!Finite(norm) || norm <= 0f) return false;
            unit = scaled / norm;
            return Finite(unit);
        }

        private static bool UnitQuaternion(Quaternion value, out Quaternion unit)
        {
            unit = Quaternion.identity;
            if (!Finite(value.x) || !Finite(value.y) || !Finite(value.z) || !Finite(value.w)) return false;
            float scale = Mathf.Max(Mathf.Abs(value.x), Mathf.Max(Mathf.Abs(value.y),
                         Mathf.Max(Mathf.Abs(value.z), Mathf.Abs(value.w))));
            if (!Finite(scale) || scale <= 0f) return false;
            float x = value.x / scale;
            float y = value.y / scale;
            float z = value.z / scale;
            float w = value.w / scale;
            float norm = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (!Finite(norm) || norm <= 0f || scale <= MinNorm / norm) return false;
            unit = new Quaternion(x / norm, y / norm, z / norm, w / norm);
            return Finite(unit.x) && Finite(unit.y) && Finite(unit.z) && Finite(unit.w);
        }

        private static Quaternion Multiply(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
                a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
        }

        // Caller-frame host reference algorithm. False means reject with no commit.
        public static bool TryAdvance(Quaternion currentRotation, Vector3 displacement,
            Vector3 groundNormal, float radiusM, bool discontinuous,
            bool useMaximumDisplacement, float maximumDisplacementM,
            out Quaternion nextRotation)
        {
            nextRotation = Quaternion.identity;
            Quaternion current;
            Vector3 normal;
            if (!UnitQuaternion(currentRotation, out current)) return false;
            if (!Finite(displacement) || !UnitVector(groundNormal, out normal)) return false;
            if (!Finite(radiusM) || radiusM <= 0f) return false;
            if (useMaximumDisplacement &&
                (!Finite(maximumDisplacementM) || maximumDisplacementM <= 0f)) return false;

            float totalDistance;
            if (!Length(displacement, out totalDistance)) return false;
            if (discontinuous || (useMaximumDisplacement && totalDistance > maximumDisplacementM))
            {
                nextRotation = current;
                return true;
            }

            float normalDistance = Vector3.Dot(displacement, normal);
            if (!Finite(normalDistance)) return false;
            Vector3 tangent = displacement - normal * normalDistance;
            if (!Finite(tangent)) return false;
            float distance;
            if (!Length(tangent, out distance)) return false;
            if (distance <= Epsilon)
            {
                nextRotation = current;
                return true;
            }

            // omega is parallel to n x tangent because v + omega x (-r*n) = 0
            // in the declared conventional host vector algebra. This component
            // expression is not Unity Vector3.Cross; the Unity adapter gate is open.
            Vector3 axis = new Vector3(
                normal.y * tangent.z - normal.z * tangent.y,
                normal.z * tangent.x - normal.x * tangent.z,
                normal.x * tangent.y - normal.y * tangent.x);
            Vector3 unitAxis;
            if (!UnitVector(axis, out unitAxis)) return false;
            float angle = distance / radiusM;
            if (!Finite(angle)) return false;
            float half = angle * 0.5f;
            float sine = Mathf.Sin(half);
            float cosine = Mathf.Cos(half);
            if (!Finite(sine) || !Finite(cosine)) return false;
            Quaternion delta = new Quaternion(unitAxis.x * sine, unitAxis.y * sine, unitAxis.z * sine, cosine);
            Quaternion candidate;
            if (!UnitQuaternion(Multiply(delta, current), out candidate)) return false;
            nextRotation = candidate;
            return true;
        }

        // Narrow scene adapter: owns only the named visual child rotation/scale.
        // The transform call itself remains unverified until a Windows test object exists.
        public static bool TryApply(Transform visualRollRoot, Vector3 displacement,
            Vector3 groundNormal, float radiusM, bool discontinuous,
            bool useMaximumDisplacement, float maximumDisplacementM)
        {
            if (visualRollRoot == null) return false;
            Quaternion candidate;
            if (!TryAdvance(visualRollRoot.localRotation, displacement, groundNormal, radiusM,
                discontinuous, useMaximumDisplacement, maximumDisplacementM, out candidate)) return false;
            visualRollRoot.localRotation = candidate;
            visualRollRoot.localScale = Vector3.one;
            return true;
        }
    }
}
