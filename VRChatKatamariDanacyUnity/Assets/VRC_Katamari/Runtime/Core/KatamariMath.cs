using UnityEngine;

namespace VRCKatamari.Core
{
    /// <summary>
    /// The small finite-input helpers every ball-layer module shares.
    /// </summary>
    /// <remarks>
    /// Lengths are metres in the project's single world space (+Y up). No
    /// module-specific physics, no static state and no allocation lives here.
    /// </remarks>
    public static class KatamariMath
    {
        // A length at or below this is treated as degenerate. It also keeps a
        // caller from dividing a distance by a reciprocal that would overflow.
        private const float MinimumLength = 1e-12f;

        /// <summary>True for an ordinary float; NaN and both infinities are false.</summary>
        public static bool IsFiniteFloat(float value)
        {
            // Range comparison rather than float.IsNaN/IsInfinity: NaN fails both
            // comparisons, so this stays within Udon's supported externs.
            return value >= float.MinValue && value <= float.MaxValue;
        }

        /// <summary>True when all three world-space components are finite.</summary>
        public static bool IsFiniteVector(Vector3 value)
        {
            return IsFiniteFloat(value.x) && IsFiniteFloat(value.y) && IsFiniteFloat(value.z);
        }

        /// <summary>True when all four rotation components are finite.</summary>
        public static bool IsFiniteQuaternion(Quaternion value)
        {
            return IsFiniteFloat(value.x) && IsFiniteFloat(value.y) &&
                IsFiniteFloat(value.z) && IsFiniteFloat(value.w);
        }

        /// <summary>
        /// Splits a vector into a unit direction and its length in metres.
        /// </summary>
        /// <remarks>
        /// The largest component is divided out before the components are squared,
        /// so neither huge nor tiny vectors underflow or overflow the length. False
        /// means the vector is non-finite, is degenerate (~zero), or has a length
        /// too large for a finite float (beyond roughly 1.7e38 m); unit and length
        /// are then zero. The unit direction is not rounded to a tolerance.
        /// </remarks>
        public static bool TryNormalize(Vector3 value, out Vector3 unit, out float length)
        {
            unit = Vector3.zero;
            length = 0f;
            if (!IsFiniteVector(value)) return false;
            float scale = Mathf.Max(Mathf.Abs(value.x),
                Mathf.Max(Mathf.Abs(value.y), Mathf.Abs(value.z)));
            if (scale <= 0f) return false;
            float x = value.x / scale;
            float y = value.y / scale;
            float z = value.z / scale;
            float magnitude = Mathf.Sqrt(x * x + y * y + z * z);
            if (!IsFiniteFloat(magnitude) || magnitude <= 0f) return false;
            float candidateLength = scale * magnitude;
            if (!IsFiniteFloat(candidateLength) || candidateLength <= MinimumLength) return false;
            Vector3 candidateUnit = new Vector3(x / magnitude, y / magnitude, z / magnitude);
            if (!IsFiniteVector(candidateUnit)) return false;
            unit = candidateUnit;
            length = candidateLength;
            return true;
        }
    }
}
