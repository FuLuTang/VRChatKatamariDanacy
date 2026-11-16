using VRCKatamari.Core;

namespace VRCKatamari.Input
{
    /// <summary>
    /// Pure math for <see cref="DesktopMoveInput"/>'s DEV-ONLY operator drive override:
    /// clamping the two override axes and deciding when the override has expired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The override itself is policy - which fields exist, what is logged, which gates apply -
    /// and it lives in the behaviour, documented in
    /// <c>Docs/Design/ControlIntent/DEBUG_DRIVE.md</c>. Everything decidable from the numbers
    /// alone is here, so EditMode tests can pin it without a scene, a motor or Play mode. No
    /// state, no allocation, no static fields.
    /// </para>
    /// <para>
    /// Both contracts follow the device path this override sits in front of. Clamping refuses
    /// non-finite input instead of repairing it, because the motor keeps the axes of a
    /// rejected frame (<see cref="DesktopMoveInput"/> remarks): a NaN silently clamped to a
    /// limit would steer, while a dropped frame does not. Expiry treats a non-positive
    /// duration as "no expiry", which is the 0 the behaviour ships with.
    /// </para>
    /// </remarks>
    public static class DebugDriveMath
    {
        /// <summary>
        /// Clamps both override axes into [-1, 1], the range the motor itself accepts.
        /// </summary>
        /// <remarks>
        /// False means at least one raw axis was NaN or infinite. Both outputs are then 0, so
        /// a caller cannot half-apply such a frame, and it must keep the axes it already had
        /// rather than push this zero.
        /// </remarks>
        public static bool TryClampAxes(float rawX, float rawZ, out float axisX, out float axisZ)
        {
            axisX = 0f;
            axisZ = 0f;
            if (!KatamariMath.IsFiniteFloat(rawX)) return false;
            if (!KatamariMath.IsFiniteFloat(rawZ)) return false;
            axisX = ClampAxis(rawX);
            axisZ = ClampAxis(rawZ);
            return true;
        }

        /// <summary>
        /// True once <paramref name="durationSeconds"/> have passed since the override was
        /// switched on; false for the shipped default of 0 ("until cleared").
        /// </summary>
        /// <remarks>
        /// The boundary is inclusive: at exactly the duration the override is already over, so
        /// a 30 s run cannot owe one extra frame. A non-finite duration or timestamp also
        /// returns false - a garbage timer must not switch drive off in the middle of a run -
        /// and a start reading after now cannot have expired either.
        /// </remarks>
        public static bool HasExpired(float startTimeSeconds, float nowSeconds,
            float durationSeconds)
        {
            if (!KatamariMath.IsFiniteFloat(startTimeSeconds)) return false;
            if (!KatamariMath.IsFiniteFloat(nowSeconds)) return false;
            if (!KatamariMath.IsFiniteFloat(durationSeconds)) return false;
            if (durationSeconds <= 0f) return false;
            return nowSeconds - startTimeSeconds >= durationSeconds;
        }

        private static float ClampAxis(float value)
        {
            if (value > 1f) return 1f;
            if (value < -1f) return -1f;
            return value;
        }
    }
}
