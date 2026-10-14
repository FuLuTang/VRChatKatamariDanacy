// RuntimeDraft only: not compiled in Unity/UdonSharp; see module gate notes.
// This class accepts scalar horizontal yaw only. It contains no HMD, controller,
// ball quaternion, roll, pitch, camera, or gameplay-state input.
using UnityEngine;

namespace VrcKatamari.RuntimeDraft
{
    public sealed class FollowRigRuntimeDraft
    {
        public float HeightFilterHz = 5f; // T46-derived starter value; still provisional.
        public float YawFilterHz = 8f;    // T46-derived starter value; still provisional.

        private bool _initialized;
        private float _x;
        private float _z;
        private float _eyeOffset;
        private float _yaw;

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static float Clamp(float value, float low, float high)
        {
            return Mathf.Max(low, Mathf.Min(high, value));
        }

        private static float Wrap360(float value)
        {
            float wrapped = value % 360f;
            if (wrapped < 0f) wrapped += 360f;
            return wrapped;
        }

        private static float ShortestDelta(float target, float current)
        {
            float difference = (target - current + 180f) % 360f;
            if (difference < 0f) difference += 360f;
            return difference - 180f;
        }

        private static bool TryAlpha(float hz, float dt, out float alpha)
        {
            alpha = 0f;
            if (!Finite(hz) || hz < 0f || !Finite(dt) || dt < 0f) return false;
            if (hz == 0f || dt == 0f) return true;
            float exponent = (2f * Mathf.PI * hz) * dt;
            if (!Finite(exponent)) return false;
            alpha = 1f - Mathf.Exp(-exponent);
            return Finite(alpha) && alpha >= 0f && alpha <= 1f;
        }

        // Returns false on invalid input with no mutation. Bounds are explicit
        // caller-owned tuning inputs, not a VR comfort or platform guarantee.
        public bool TryUpdate(Vector3 ballWorldPosition, float horizontalYawDeg,
            float dtSeconds, float eyeHeightMeters, float eyeHeightMinMeters,
            float eyeHeightMaxMeters, bool respawnOrTeleport,
            out Vector3 rigWorldPosition, out float rigYawDeg)
        {
            rigWorldPosition = Vector3.zero;
            rigYawDeg = 0f;
            if (!Finite(ballWorldPosition.x) || !Finite(ballWorldPosition.y) ||
                !Finite(ballWorldPosition.z) || !Finite(horizontalYawDeg) ||
                !Finite(dtSeconds) || dtSeconds < 0f || !Finite(eyeHeightMeters) ||
                !Finite(eyeHeightMinMeters) || !Finite(eyeHeightMaxMeters) ||
                eyeHeightMinMeters > eyeHeightMaxMeters) return false;

            float alphaHeight;
            float alphaYaw;
            if (!TryAlpha(HeightFilterHz, dtSeconds, out alphaHeight) ||
                !TryAlpha(YawFilterHz, dtSeconds, out alphaYaw)) return false;
            float targetOffset = Clamp(eyeHeightMeters, eyeHeightMinMeters, eyeHeightMaxMeters);
            float targetYaw = Wrap360(horizontalYawDeg);
            float nextX;
            float nextZ;
            float nextOffset;
            float nextYaw;

            if (!_initialized || respawnOrTeleport)
            {
                nextX = ballWorldPosition.x;
                nextZ = ballWorldPosition.z;
                nextOffset = targetOffset;
                nextYaw = targetYaw;
            }
            else
            {
                float priorOffset = Clamp(_eyeOffset, eyeHeightMinMeters, eyeHeightMaxMeters);
                nextX = _x + (ballWorldPosition.x - _x) * alphaHeight;
                nextZ = _z + (ballWorldPosition.z - _z) * alphaHeight;
                nextOffset = priorOffset + (targetOffset - priorOffset) * alphaHeight;
                nextOffset = Clamp(nextOffset, eyeHeightMinMeters, eyeHeightMaxMeters);
                nextYaw = Wrap360(_yaw + ShortestDelta(targetYaw, _yaw) * alphaYaw);
            }

            float nextY = ballWorldPosition.y + nextOffset;
            if (!Finite(nextX) || !Finite(nextY) || !Finite(nextZ) ||
                !Finite(nextOffset) || !Finite(nextYaw)) return false;

            // Commit only after every candidate value has passed validation.
            _x = nextX;
            _z = nextZ;
            _eyeOffset = nextOffset;
            _yaw = nextYaw;
            _initialized = true;
            rigWorldPosition = new Vector3(nextX, nextY, nextZ);
            rigYawDeg = nextYaw;
            return true;
        }
    }
}
