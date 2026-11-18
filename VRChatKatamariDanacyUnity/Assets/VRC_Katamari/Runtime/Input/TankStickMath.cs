using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Input
{
    /// <summary>
    /// Turns the two controller thumbsticks into the motor's three axes
    /// (forward, turn, lateral), in either the original two-stick "tank" scheme or the
    /// one-stick "simple" scheme.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure math: no device reads, no clock, no static state. The caller owns the
    /// stick values and the deadzone. Design, mapping table and sources:
    /// Docs/Design/ControlIntent/README.md.
    /// </para>
    /// <para>
    /// Axis conventions are the motor's (<c>SetMoveAxes</c>): forward +1 = forward,
    /// turn +1 = turn right (clockwise seen from above), lateral +1 = strafe right.
    /// Stick values are device values in [-1, 1], x right and y forward (up). Values
    /// outside the range are clamped; a NaN or infinity refuses the whole call and
    /// leaves every output 0, because the motor keeps the axes of a rejected frame and
    /// a repaired NaN would steer.
    /// </para>
    /// <para>
    /// Tank scheme: each stick's y is one track. forward = (L + R) / 2,
    /// turn = (L - R) / 2, lateral = (Lx + Rx) / 2. So both forward rolls, left forward
    /// with right back spins right on the spot, one stick alone curves around the other
    /// side at half speed, and both sideways strafes. This is a continuous differential
    /// approximation of the original, not a measured copy of its blending.
    /// </para>
    /// <para>
    /// Simple scheme: the left stick rolls in any direction (y forward, x lateral) and
    /// the right stick's x turns. The right stick's y is ignored: the headset does the
    /// looking in VR.
    /// </para>
    /// <para>
    /// Deadzone: radial per stick, rescaled so the output starts at 0 at the deadzone
    /// edge and still reaches 1 at full tilt; the simple scheme's turn axis uses the
    /// same rule on one axis.
    /// </para>
    /// </remarks>
    public static class TankStickMath
    {
        /// <summary>Original two-stick scheme (the default).</summary>
        public const int ModeTank = 0;

        /// <summary>Once Upon a Katamari "Simple": left stick rolls, right stick turns.</summary>
        public const int ModeSimple = 1;

        /// <summary>Radial deadzone per stick, in stick units.</summary>
        public const float DefaultDeadzone = 0.2f;

        /// <summary>
        /// Maps one frame of both sticks to the motor's three axes in the given scheme.
        /// </summary>
        /// <remarks>
        /// An unknown mode, a negative or non-finite deadzone, a deadzone of 1 or more, or a
        /// non-finite stick value refuses the call with all outputs 0.
        /// </remarks>
        public static bool TryMap(int mode, float leftX, float leftY, float rightX, float rightY,
            float deadzone, out float forward, out float turn, out float lateral)
        {
            if (mode == ModeTank)
            {
                return TryMapTank(leftX, leftY, rightX, rightY, deadzone,
                    out forward, out turn, out lateral);
            }
            if (mode == ModeSimple)
            {
                return TryMapSimple(leftX, leftY, rightX, rightY, deadzone,
                    out forward, out turn, out lateral);
            }
            forward = 0f;
            turn = 0f;
            lateral = 0f;
            return false;
        }

        public const float DefaultTurnSensitivity = 1f;

        /// <summary>
        /// Maps physical stick readings, optionally exchanging both axes of the two sticks
        /// before either scheme sees them. Scales and clamps only the mapped turn axis.
        /// A negative or non-finite turn sensitivity refuses the frame with zero outputs.
        /// </summary>
        public static bool TryMapWithSettings(int mode, float leftX, float leftY, float rightX,
            float rightY, bool swapHands, float deadzone, float turnSensitivity,
            out float forward, out float turn, out float lateral)
        {
            if (!KatamariMath.IsFiniteFloat(turnSensitivity) || turnSensitivity < 0f)
            {
                forward = 0f;
                turn = 0f;
                lateral = 0f;
                return false;
            }

            if (!TryMap(mode, swapHands ? rightX : leftX, swapHands ? rightY : leftY,
                swapHands ? leftX : rightX, swapHands ? leftY : rightY, deadzone,
                out forward, out turn, out lateral)) return false;

            turn = Clamp1(turn * turnSensitivity);
            return true;
        }

        /// <summary>The original two-stick scheme; see the class remarks for the formulas.</summary>
        public static bool TryMapTank(float leftX, float leftY, float rightX, float rightY,
            float deadzone, out float forward, out float turn, out float lateral)
        {
            forward = 0f;
            turn = 0f;
            lateral = 0f;
            float lx;
            float ly;
            float rx;
            float ry;
            if (!TryApplyRadialDeadzone(leftX, leftY, deadzone, out lx, out ly)) return false;
            if (!TryApplyRadialDeadzone(rightX, rightY, deadzone, out rx, out ry)) return false;
            forward = Clamp1((ly + ry) * 0.5f);
            turn = Clamp1((ly - ry) * 0.5f);
            lateral = Clamp1((lx + rx) * 0.5f);
            return true;
        }

        /// <summary>The simple scheme: left stick rolls in any direction, right stick x turns.</summary>
        public static bool TryMapSimple(float leftX, float leftY, float rightX, float rightY,
            float deadzone, out float forward, out float turn, out float lateral)
        {
            forward = 0f;
            turn = 0f;
            lateral = 0f;
            // The right stick's y is not used, but a garbage value still refuses the frame:
            // the device is reporting nonsense and the frame should not be trusted.
            if (!KatamariMath.IsFiniteFloat(rightY)) return false;
            float lx;
            float ly;
            if (!TryApplyRadialDeadzone(leftX, leftY, deadzone, out lx, out ly)) return false;
            float rx;
            if (!TryApplyAxisDeadzone(rightX, deadzone, out rx)) return false;
            forward = ly;
            lateral = lx;
            turn = rx;
            return true;
        }

        /// <summary>
        /// Radial deadzone for one stick: inside <paramref name="deadzone"/> the stick is
        /// at rest; outside it the magnitude is rescaled from [deadzone, 1] to [0, 1] with
        /// the direction kept. Inputs are clamped to [-1, 1] per axis first, and the output
        /// magnitude never exceeds 1.
        /// </summary>
        public static bool TryApplyRadialDeadzone(float x, float y, float deadzone,
            out float outX, out float outY)
        {
            outX = 0f;
            outY = 0f;
            if (!IsValidDeadzone(deadzone)) return false;
            if (!KatamariMath.IsFiniteFloat(x) || !KatamariMath.IsFiniteFloat(y)) return false;
            x = Clamp1(x);
            y = Clamp1(y);
            float magnitude = Mathf.Sqrt(x * x + y * y);
            if (magnitude <= deadzone) return true;
            float scaled = (Mathf.Min(magnitude, 1f) - deadzone) / (1f - deadzone);
            float factor = scaled / magnitude;
            outX = x * factor;
            outY = y * factor;
            return true;
        }

        /// <summary>One-axis form of <see cref="TryApplyRadialDeadzone"/>.</summary>
        public static bool TryApplyAxisDeadzone(float value, float deadzone, out float result)
        {
            result = 0f;
            if (!IsValidDeadzone(deadzone)) return false;
            if (!KatamariMath.IsFiniteFloat(value)) return false;
            value = Clamp1(value);
            float magnitude = Mathf.Abs(value);
            if (magnitude <= deadzone) return true;
            float scaled = (magnitude - deadzone) / (1f - deadzone);
            result = value < 0f ? -scaled : scaled;
            return true;
        }

        private static bool IsValidDeadzone(float deadzone)
        {
            return KatamariMath.IsFiniteFloat(deadzone) && deadzone >= 0f && deadzone < 1f;
        }

        private static float Clamp1(float value)
        {
            if (value > 1f) return 1f;
            if (value < -1f) return -1f;
            return value;
        }
    }
}
