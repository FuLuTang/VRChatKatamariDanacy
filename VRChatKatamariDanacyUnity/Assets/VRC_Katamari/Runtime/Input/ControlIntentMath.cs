using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Input
{
    /// <summary>
    /// The kinds a canonical input frame can produce. The numeric order is the
    /// model's enumeration order; the per-frame emission order is different.
    /// </summary>
    public enum ControlIntentKind
    {
        Move2D = 0,
        Look2D = 1,
        UseItemOrJump = 2,
        InteractOrSeat = 3,
        ExitStation = 4,
        Respawn = 5,
        ComfortTurn = 6
    }

    /// <summary>
    /// Provenance of a canonical frame. Sources are routing tags only; the motion
    /// layer never branches on them.
    /// </summary>
    /// <remarks>
    /// The model's keys are the strings "A", "B" and "Desktop": two virtual
    /// controller handles, native VR sticks, and keyboard/mouse. The exact device
    /// bindings remain explicitly unresolved in the design document and are not
    /// repeated as runtime data here.
    /// </remarks>
    public enum ControlIntentSource
    {
        VirtualHandles = 0,
        NativeVr = 1,
        Desktop = 2
    }

    /// <summary>
    /// Turns one canonical input frame into that frame's ordered intent stream and
    /// exposes the two host-side input policies (source provenance, prompt visibility).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of Docs/Design/ControlIntent/intent.py, the DRAFT host-only reference
    /// model. This is a pure converter: it does not read Unity input, VRCPlayerApi,
    /// the clock or any MonoBehaviour, and it has no static state. The caller owns the
    /// clock, the device adapters and all memory. Every output buffer is caller-owned
    /// and reused, so one frame allocates nothing.
    /// </para>
    /// <para>
    /// Frame and units: Move2D and Look2D are dimensionless normalized device-axis
    /// pairs, clamped to [-1, 1]; they are not world directions. The model defines no
    /// mapping from axis x/y to world X/Z, no forward vector and no yaw, so this layer
    /// asserts none. The project's single world frame (+Y up, yaw about +Y) and any
    /// head/hand/stick choice of yaw authority belong to the adapters, motion and
    /// FollowRig layers, not here. A zero axis pair emits nothing.
    /// </para>
    /// <para>
    /// Deadzone: none. The model has only the [-1, 1] clamp, with no deadzone radius,
    /// no hysteresis and therefore no deadzone unit to document. Axis inputs below any
    /// magnitude still pass through unchanged.
    /// </para>
    /// <para>
    /// Time: timestamps are non-negative seconds from the caller's monotonic clock.
    /// Every intent emitted by one call carries that call's timestamp; the model never
    /// substitutes another time, so the timestamp is not duplicated into the buffers.
    /// A press is one request on its rising edge: holding it emits nothing more, and a
    /// release followed by a press at or after the debounce window requests again. Each
    /// of jump, interact, exit and respawn keeps its own edge and last-fire time.
    /// debounceSeconds defaults to the model's draft 0.2 s, not a tuned value.
    /// </para>
    /// <para>
    /// Items: the model has no item id, slot, magnet or rocket output. UseItemOrJump is
    /// only "the item/use button was pressed"; the game-design single-held-item rule
    /// (no silent overwrite) and item idempotency belong to the item lifecycle and
    /// ledger, not to this layer. The model also has no descend request and no physical
    /// jump output. These semantics are deliberately absent rather than approximated,
    /// so no single-slot state exists here to hold two items.
    /// </para>
    /// <para>
    /// Seating: the model deletes its seated argument and emits no physical-jump or
    /// seat-exit intent, so jump always keeps its item-request meaning. Seating only
    /// affects the local prompt through IsPromptVisible.
    /// </para>
    /// <para>
    /// Menu: while menuOpen is true nothing is emitted, but the physical down state of
    /// the four edge actions and the comfort setting are still latched. Closing the menu
    /// with a control held therefore produces no synthetic press; the control must be
    /// released first. A menu frame with no comfort sample stores "unknown", matching
    /// the model's assignment of the optional value.
    /// </para>
    /// <para>
    /// Validation: false means the frame was not consumed and no intent was written; it
    /// is never an exception. The Python model raises for a negative timestamp, a
    /// negative debounce and an unknown source, and lets NaN through its clamp. This
    /// port rejects every non-finite input instead (project convention), which is the
    /// only intentional behavioural difference. All state arrays must start at the
    /// values TryReset writes.
    /// </para>
    /// <para>
    /// Buffers: kindBuffer, xBuffer, yBuffer and valueBuffer need at least
    /// MaxIntentsPerFrame entries. Emitted entries are [0, intentCount). valueBuffer is
    /// meaningful only for ComfortTurn; every other kind writes false there, the
    /// model's absent optional value.
    /// </para>
    /// </remarks>
    public static class ControlIntentMath
    {
        /// <summary>Most intents one frame can emit: two axes, one comfort change, four actions.</summary>
        public const int MaxIntentsPerFrame = 7;

        /// <summary>Number of edge-triggered action channels.</summary>
        public const int ActionCount = 4;

        public const int JumpAction = 0;
        public const int InteractAction = 1;
        public const int ExitAction = 2;
        public const int RespawnAction = 3;

        /// <summary>comfortState index holding whether a comfort setting was ever sampled.</summary>
        public const int ComfortKnownState = 0;

        /// <summary>comfortState index holding the last sampled comfort setting.</summary>
        public const int ComfortValueState = 1;

        /// <summary>lastActionTime value meaning "this action never fired". Any negative value works.</summary>
        public const float NoActionTime = -1f;

        /// <summary>The model's draft debounce window in seconds.</summary>
        public const float DefaultDebounceSeconds = 0.2f;

        /// <summary>True for the three provenance tags the model binds.</summary>
        /// <remarks>
        /// Ports adapt_source's unknown-source rejection. The binding descriptions in
        /// the design document are documentation, not runtime data, so they are not
        /// duplicated here. This check is expected once per adapter selection, not once
        /// per frame.
        /// </remarks>
        public static bool IsSupportedSource(ControlIntentSource source)
        {
            return source == ControlIntentSource.VirtualHandles ||
                source == ControlIntentSource.NativeVr ||
                source == ControlIntentSource.Desktop;
        }

        /// <summary>Host policy: the local interact prompt is hidden while seated or in a menu.</summary>
        public static bool IsPromptVisible(bool seated, bool menuOpen)
        {
            return !seated && !menuOpen;
        }

        /// <summary>Clears the caller-owned edge, debounce and comfort state (the model's reset()).</summary>
        /// <remarks>
        /// False for a null or short array; the arrays are then left untouched. A
        /// caller may instead initialize the same values itself: all previousDown
        /// entries false, all lastActionTime entries NoActionTime, and both comfortState
        /// entries false.
        /// </remarks>
        public static bool TryReset(bool[] previousDown, float[] lastActionTime, bool[] comfortState)
        {
            if (previousDown == null || previousDown.Length < ActionCount) return false;
            if (lastActionTime == null || lastActionTime.Length < ActionCount) return false;
            if (comfortState == null || comfortState.Length <= ComfortValueState) return false;
            for (int i = 0; i < ActionCount; i++)
            {
                previousDown[i] = false;
                lastActionTime[i] = NoActionTime;
            }
            comfortState[ComfortKnownState] = false;
            comfortState[ComfortValueState] = false;
            return true;
        }

        /// <summary>
        /// Converts one canonical frame into its ordered intent stream.
        /// </summary>
        /// <remarks>
        /// Emission order per frame is Move2D, Look2D, ComfortTurn, UseItemOrJump,
        /// InteractOrSeat, ExitStation, Respawn. Intent timestamps are the timestamp
        /// argument. On false, intentCount is zero and no buffer entry is written.
        /// </remarks>
        public static bool TryProcess(
            float timestamp,
            Vector2 move,
            Vector2 look,
            bool jump,
            bool interact,
            bool exitStation,
            bool respawn,
            bool comfortTurnKnown,
            bool comfortTurn,
            bool menuOpen,
            bool[] previousDown,
            float[] lastActionTime,
            bool[] comfortState,
            ControlIntentKind[] kindBuffer,
            float[] xBuffer,
            float[] yBuffer,
            bool[] valueBuffer,
            out int intentCount,
            float debounceSeconds = DefaultDebounceSeconds)
        {
            intentCount = 0;
            if (!KatamariMath.IsFiniteFloat(timestamp) || timestamp < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(debounceSeconds) || debounceSeconds < 0f) return false;
            if (previousDown == null || previousDown.Length < ActionCount) return false;
            if (lastActionTime == null || lastActionTime.Length < ActionCount) return false;
            if (comfortState == null || comfortState.Length <= ComfortValueState) return false;
            if (kindBuffer == null || kindBuffer.Length < MaxIntentsPerFrame) return false;
            if (xBuffer == null || xBuffer.Length < MaxIntentsPerFrame) return false;
            if (yBuffer == null || yBuffer.Length < MaxIntentsPerFrame) return false;
            if (valueBuffer == null || valueBuffer.Length < MaxIntentsPerFrame) return false;
            for (int i = 0; i < ActionCount; i++)
            {
                // A negative stored time is the "never fired" sentinel; anything else
                // must be a real timestamp.
                if (!KatamariMath.IsFiniteFloat(lastActionTime[i])) return false;
            }

            // Menus suppress every game intent but still latch physical state, so a
            // control held while the menu closes cannot synthesize a stale press.
            if (menuOpen)
            {
                previousDown[JumpAction] = jump;
                previousDown[InteractAction] = interact;
                previousDown[ExitAction] = exitStation;
                previousDown[RespawnAction] = respawn;
                comfortState[ComfortKnownState] = comfortTurnKnown;
                comfortState[ComfortValueState] = comfortTurn;
                return true;
            }

            // Both axis pairs are validated before anything is emitted, so a bad look
            // axis cannot leave a half-written frame.
            float moveX;
            float moveY;
            float lookX;
            float lookY;
            if (!TryClampAxis(move.x, out moveX)) return false;
            if (!TryClampAxis(move.y, out moveY)) return false;
            if (!TryClampAxis(look.x, out lookX)) return false;
            if (!TryClampAxis(look.y, out lookY)) return false;

            if (moveX != 0f || moveY != 0f)
            {
                Emit(ControlIntentKind.Move2D, moveX, moveY, false, kindBuffer,
                    xBuffer, yBuffer, valueBuffer, ref intentCount);
            }
            if (lookX != 0f || lookY != 0f)
            {
                Emit(ControlIntentKind.Look2D, lookX, lookY, false, kindBuffer,
                    xBuffer, yBuffer, valueBuffer, ref intentCount);
            }

            // Comfort is a setting-change edge, not a continuous turn axis. An absent
            // sample leaves the previous setting in place.
            if (comfortTurnKnown &&
                (!comfortState[ComfortKnownState] || comfortTurn != comfortState[ComfortValueState]))
            {
                comfortState[ComfortKnownState] = true;
                comfortState[ComfortValueState] = comfortTurn;
                Emit(ControlIntentKind.ComfortTurn, 0f, 0f, comfortTurn, kindBuffer,
                    xBuffer, yBuffer, valueBuffer, ref intentCount);
            }

            ApplyEdge(ControlIntentKind.UseItemOrJump, JumpAction, jump, timestamp,
                debounceSeconds, previousDown, lastActionTime, kindBuffer, xBuffer,
                yBuffer, valueBuffer, ref intentCount);
            ApplyEdge(ControlIntentKind.InteractOrSeat, InteractAction, interact, timestamp,
                debounceSeconds, previousDown, lastActionTime, kindBuffer, xBuffer,
                yBuffer, valueBuffer, ref intentCount);
            ApplyEdge(ControlIntentKind.ExitStation, ExitAction, exitStation, timestamp,
                debounceSeconds, previousDown, lastActionTime, kindBuffer, xBuffer,
                yBuffer, valueBuffer, ref intentCount);
            ApplyEdge(ControlIntentKind.Respawn, RespawnAction, respawn, timestamp,
                debounceSeconds, previousDown, lastActionTime, kindBuffer, xBuffer,
                yBuffer, valueBuffer, ref intentCount);
            return true;
        }

        private static bool TryClampAxis(float value, out float clamped)
        {
            clamped = 0f;
            if (!KatamariMath.IsFiniteFloat(value)) return false;
            if (value < -1f)
            {
                clamped = -1f;
            }
            else if (value > 1f)
            {
                clamped = 1f;
            }
            else
            {
                clamped = value;
            }
            return true;
        }

        private static void ApplyEdge(
            ControlIntentKind kind,
            int actionIndex,
            bool down,
            float timestamp,
            float debounceSeconds,
            bool[] previousDown,
            float[] lastActionTime,
            ControlIntentKind[] kindBuffer,
            float[] xBuffer,
            float[] yBuffer,
            bool[] valueBuffer,
            ref int intentCount)
        {
            bool wasDown = previousDown[actionIndex];
            previousDown[actionIndex] = down;
            if (!down || wasDown) return;
            float last = lastActionTime[actionIndex];
            // Negative means "never fired". Skipping the subtraction keeps a caller's
            // sentinel from overflowing the comparison.
            if (last >= 0f && timestamp - last < debounceSeconds) return;
            lastActionTime[actionIndex] = timestamp;
            Emit(kind, 0f, 0f, false, kindBuffer, xBuffer, yBuffer, valueBuffer,
                ref intentCount);
        }

        private static void Emit(
            ControlIntentKind kind,
            float x,
            float y,
            bool value,
            ControlIntentKind[] kindBuffer,
            float[] xBuffer,
            float[] yBuffer,
            bool[] valueBuffer,
            ref int intentCount)
        {
            kindBuffer[intentCount] = kind;
            xBuffer[intentCount] = x;
            yBuffer[intentCount] = y;
            valueBuffer[intentCount] = value;
            intentCount++;
        }
    }
}
