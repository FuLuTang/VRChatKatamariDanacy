using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Input;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of Docs/tests/control_intent/test_intent.py.
    /// Expected values are hand calculations from the model's documented emission
    /// order and edge rules; no assertion re-runs the implementation formula. Axes are
    /// dimensionless normalized device axes, not world directions.
    /// </summary>
    /// <remarks>
    /// The Python suite has six tests. The two-dimensional axis shape case cannot be
    /// expressed with <see cref="Vector2"/> and is replaced by the non-finite-axis case.
    /// The model has no deadzone, no item slot, no descend request and no left/right
    /// stick concept, so no test invents one; the mirror invariant tested here is the
    /// sign symmetry of the axis treatment.
    /// </remarks>
    public class ControlIntentTests
    {
        private const float Tolerance = 1e-5f;

        // Test-only record mirroring the Python Intent dataclass (kind, timestamp, x, y,
        // value). Value is null when the model's optional value is absent.
        private sealed class IntentRecord
        {
            public ControlIntentKind Kind;
            public float Timestamp;
            public float X;
            public float Y;
            public bool? Value;
        }

        // Test-only mirror of the Python IntentAdapter: it owns the caller-side state
        // arrays and a fixed record log. It is not runtime code.
        private sealed class Adapter
        {
            private const int Capacity = 128;

            private readonly bool[] previousDown = new bool[ControlIntentMath.ActionCount];
            private readonly float[] lastActionTime = new float[ControlIntentMath.ActionCount];
            private readonly bool[] comfortState = new bool[2];
            private readonly ControlIntentKind[] kinds =
                new ControlIntentKind[ControlIntentMath.MaxIntentsPerFrame];
            private readonly float[] xs = new float[ControlIntentMath.MaxIntentsPerFrame];
            private readonly float[] ys = new float[ControlIntentMath.MaxIntentsPerFrame];
            private readonly bool[] values = new bool[ControlIntentMath.MaxIntentsPerFrame];

            public readonly IntentRecord[] Records = new IntentRecord[Capacity];
            public int Count;
            public float DebounceSeconds;

            public Adapter(float debounceSeconds = ControlIntentMath.DefaultDebounceSeconds)
            {
                DebounceSeconds = debounceSeconds;
                Assert.IsTrue(ControlIntentMath.TryReset(previousDown, lastActionTime, comfortState));
            }

            public bool Reset()
            {
                return ControlIntentMath.TryReset(previousDown, lastActionTime, comfortState);
            }

            public bool Step(
                float timestamp,
                Vector2? move = null,
                Vector2? look = null,
                bool jump = false,
                bool interact = false,
                bool exitStation = false,
                bool respawn = false,
                bool? comfortTurn = null,
                bool menuOpen = false)
            {
                int emitted;
                bool accepted = ControlIntentMath.TryProcess(
                    timestamp,
                    move.HasValue ? move.Value : Vector2.zero,
                    look.HasValue ? look.Value : Vector2.zero,
                    jump,
                    interact,
                    exitStation,
                    respawn,
                    comfortTurn.HasValue,
                    comfortTurn.HasValue && comfortTurn.Value,
                    menuOpen,
                    previousDown,
                    lastActionTime,
                    comfortState,
                    kinds,
                    xs,
                    ys,
                    values,
                    out emitted,
                    DebounceSeconds);
                if (!accepted) return false;
                for (int i = 0; i < emitted; i++)
                {
                    Assert.Less(Count, Capacity, "test record capacity");
                    IntentRecord record = new IntentRecord();
                    record.Kind = kinds[i];
                    record.Timestamp = timestamp;
                    record.X = xs[i];
                    record.Y = ys[i];
                    record.Value = kinds[i] == ControlIntentKind.ComfortTurn
                        ? (bool?)values[i]
                        : null;
                    Records[Count] = record;
                    Count++;
                }
                return true;
            }
        }

        private static Adapter RunScript(ControlIntentSource source)
        {
            Assert.IsTrue(ControlIntentMath.IsSupportedSource(source), "source " + source);
            Adapter adapter = new Adapter();
            adapter.Step(0f);
            adapter.Step(0.1f, new Vector2(0.2f, 1.4f), new Vector2(-0.5f, 0f));
            adapter.Step(0.2f, jump: true);
            adapter.Step(0.3f, jump: true);
            adapter.Step(0.4f, jump: false);
            adapter.Step(0.5f, jump: true);
            adapter.Step(0.7f, jump: false);
            adapter.Step(0.8f, jump: true);
            adapter.Step(1.0f, interact: true, comfortTurn: true);
            return adapter;
        }

        [Test]
        public void ThreeSourcesProduceIdenticalIntentSequence()
        {
            Adapter virtualHandles = RunScript(ControlIntentSource.VirtualHandles);
            Adapter nativeVr = RunScript(ControlIntentSource.NativeVr);
            Adapter desktop = RunScript(ControlIntentSource.Desktop);
            AssertSameSequence(virtualHandles, nativeVr);
            AssertSameSequence(nativeVr, desktop);
            Assert.AreEqual(ControlIntentKind.Move2D, virtualHandles.Records[0].Kind);
            Assert.AreEqual(0.2f, virtualHandles.Records[0].X, Tolerance);
            Assert.AreEqual(1.0f, virtualHandles.Records[0].Y, Tolerance);
        }

        [Test]
        public void ScriptedSequenceMatchesTheModelOrder()
        {
            // Hand run of the model. The press at 0.5 s follows the 0.2 s press by
            // 0.3 s, which is outside the 0.2 s draft window, so it does emit; the
            // Python test's inline comment there says otherwise but its assertions do
            // not depend on it.
            Adapter adapter = RunScript(ControlIntentSource.Desktop);
            AssertKinds(adapter,
                ControlIntentKind.Move2D,
                ControlIntentKind.Look2D,
                ControlIntentKind.UseItemOrJump,
                ControlIntentKind.UseItemOrJump,
                ControlIntentKind.UseItemOrJump,
                ControlIntentKind.ComfortTurn,
                ControlIntentKind.InteractOrSeat);
            Assert.AreEqual(0.1f, adapter.Records[0].Timestamp, Tolerance);
            Assert.AreEqual(0.1f, adapter.Records[1].Timestamp, Tolerance);
            Assert.AreEqual(0.2f, adapter.Records[2].Timestamp, Tolerance);
            Assert.AreEqual(0.5f, adapter.Records[3].Timestamp, Tolerance);
            Assert.AreEqual(0.8f, adapter.Records[4].Timestamp, Tolerance);
            Assert.AreEqual(1.0f, adapter.Records[5].Timestamp, Tolerance);
            Assert.AreEqual(1.0f, adapter.Records[6].Timestamp, Tolerance);
            Assert.AreEqual(-0.5f, adapter.Records[1].X, Tolerance);
            Assert.AreEqual(0f, adapter.Records[1].Y, Tolerance);
            Assert.AreEqual((bool?)true, adapter.Records[5].Value);
            Assert.IsNull(adapter.Records[0].Value);
        }

        [Test]
        public void RisingEdgeEmitsOnceWhileHeld()
        {
            Adapter adapter = new Adapter(0.2f);
            adapter.Step(0f, jump: true);
            adapter.Step(0.05f, jump: true);
            adapter.Step(0.1f, jump: true);
            adapter.Step(0.15f, jump: false);
            AssertKinds(adapter, ControlIntentKind.UseItemOrJump);
            Assert.AreEqual(0f, adapter.Records[0].Timestamp, Tolerance);
        }

        [Test]
        public void DebounceSuppressesQuickRepressAndAllowsLaterPress()
        {
            Adapter adapter = new Adapter(0.2f);
            adapter.Step(0f, jump: true);
            adapter.Step(0.01f, jump: false);
            adapter.Step(0.1f, jump: true);
            adapter.Step(0.11f, jump: false);
            adapter.Step(0.21f, jump: true);
            AssertKinds(adapter, ControlIntentKind.UseItemOrJump, ControlIntentKind.UseItemOrJump);
            Assert.AreEqual(0f, adapter.Records[0].Timestamp, Tolerance);
            Assert.AreEqual(0.21f, adapter.Records[1].Timestamp, Tolerance);
        }

        [Test]
        public void JumpOnlyRequestsItemUseAndSeatedHidesPrompt()
        {
            Adapter adapter = new Adapter();
            Assert.IsTrue(adapter.Step(0f, jump: true));
            // There is no physical-jump or descend kind; the only jump output is the
            // item/use request. Seating is not an input to TryProcess.
            AssertKinds(adapter, ControlIntentKind.UseItemOrJump);
            Assert.IsFalse(ControlIntentMath.IsPromptVisible(true, false));
            Assert.IsTrue(ControlIntentMath.IsPromptVisible(false, false));
        }

        [Test]
        public void MenuSuppressesEveryIntentAndLatchesHeldControls()
        {
            Adapter adapter = new Adapter();
            Assert.IsTrue(adapter.Step(0f,
                move: new Vector2(1f, 0f),
                look: new Vector2(0f, 1f),
                jump: true,
                interact: true,
                exitStation: true,
                respawn: true,
                comfortTurn: true,
                menuOpen: true));
            Assert.AreEqual(0, adapter.Count);

            // A still-held control on menu close is not a new rising edge.
            Assert.IsTrue(adapter.Step(1f, jump: true, interact: true));
            Assert.AreEqual(0, adapter.Count);
            Assert.IsFalse(ControlIntentMath.IsPromptVisible(false, true));
        }

        [Test]
        public void EmptyFrameProducesNoIntent()
        {
            Adapter adapter = new Adapter();
            Assert.IsTrue(adapter.Step(0f));
            Assert.AreEqual(0, adapter.Count);
        }

        [Test]
        public void ZeroAxisEmitsNothingAndLookDoesNotBecomeMove()
        {
            Adapter adapter = new Adapter();
            adapter.Step(0f, move: Vector2.zero, look: new Vector2(0.25f, -0.5f));
            AssertKinds(adapter, ControlIntentKind.Look2D);
            Assert.AreEqual(0.25f, adapter.Records[0].X, Tolerance);
            Assert.AreEqual(-0.5f, adapter.Records[0].Y, Tolerance);
        }

        [Test]
        public void AxesAreClampedToUnitRange()
        {
            Adapter adapter = new Adapter();
            adapter.Step(0f, move: new Vector2(1.4f, -2f));
            AssertKinds(adapter, ControlIntentKind.Move2D);
            Assert.AreEqual(1f, adapter.Records[0].X, Tolerance);
            Assert.AreEqual(-1f, adapter.Records[0].Y, Tolerance);
        }

        [Test]
        public void AxisHandlingIsSignSymmetric()
        {
            // The model has no left/right stick distinction (adapters own that), so
            // the mirror invariant available here is that negating every axis component
            // negates the emitted component and changes nothing else.
            Adapter positive = new Adapter();
            Adapter negative = new Adapter();
            positive.Step(0f,
                move: new Vector2(0.6f, -0.3f),
                look: new Vector2(2f, 0.25f));
            negative.Step(0f,
                move: new Vector2(-0.6f, 0.3f),
                look: new Vector2(-2f, -0.25f));
            Assert.AreEqual(positive.Count, negative.Count, "intent count");
            for (int i = 0; i < positive.Count; i++)
            {
                Assert.AreEqual(positive.Records[i].Kind, negative.Records[i].Kind, "kind " + i);
                Assert.AreEqual(positive.Records[i].X, -negative.Records[i].X, Tolerance, "x " + i);
                Assert.AreEqual(positive.Records[i].Y, -negative.Records[i].Y, Tolerance, "y " + i);
            }
            Assert.AreEqual(1f, positive.Records[1].X, Tolerance);
            Assert.AreEqual(-1f, negative.Records[1].X, Tolerance);
        }

        [Test]
        public void FrameOrderIsAxesThenComfortThenActions()
        {
            Adapter adapter = new Adapter();
            adapter.Step(0.25f,
                move: new Vector2(1f, 0f),
                look: new Vector2(0f, 1f),
                jump: true,
                interact: true,
                exitStation: true,
                respawn: true,
                comfortTurn: true);
            AssertKinds(adapter,
                ControlIntentKind.Move2D,
                ControlIntentKind.Look2D,
                ControlIntentKind.ComfortTurn,
                ControlIntentKind.UseItemOrJump,
                ControlIntentKind.InteractOrSeat,
                ControlIntentKind.ExitStation,
                ControlIntentKind.Respawn);
            Assert.AreEqual((bool?)true, adapter.Records[2].Value);
            Assert.IsNull(adapter.Records[0].Value);
            for (int i = 0; i < adapter.Count; i++)
            {
                Assert.AreEqual(0.25f, adapter.Records[i].Timestamp, Tolerance, "timestamp " + i);
            }
        }

        [Test]
        public void ComfortTurnEmitsOnlyWhenTheSettingChanges()
        {
            Adapter adapter = new Adapter();
            adapter.Step(0f, comfortTurn: true);
            adapter.Step(0.1f, comfortTurn: true);
            adapter.Step(0.2f, comfortTurn: false);
            adapter.Step(0.3f, comfortTurn: null);
            AssertKinds(adapter, ControlIntentKind.ComfortTurn, ControlIntentKind.ComfortTurn);
            Assert.AreEqual((bool?)true, adapter.Records[0].Value);
            Assert.AreEqual((bool?)false, adapter.Records[1].Value);
        }

        [Test]
        public void MenuLatchesComfortState()
        {
            // An unknown sample during the menu replaces the stored setting, so a
            // later true is a fresh change.
            Adapter cleared = new Adapter();
            cleared.Step(0f, comfortTurn: true);
            cleared.Step(0.1f, comfortTurn: null, menuOpen: true);
            cleared.Step(0.2f, comfortTurn: true);
            AssertKinds(cleared, ControlIntentKind.ComfortTurn, ControlIntentKind.ComfortTurn);

            // A held true sample during the menu is latched, so closing does not emit.
            Adapter held = new Adapter();
            held.Step(0f, comfortTurn: true);
            held.Step(0.1f, comfortTurn: true, menuOpen: true);
            held.Step(0.2f, comfortTurn: true);
            AssertKinds(held, ControlIntentKind.ComfortTurn);
        }

        [Test]
        public void DebounceIsTrackedPerAction()
        {
            Adapter adapter = new Adapter(0.2f);
            adapter.Step(0f, jump: true);
            adapter.Step(0.05f, jump: false, interact: true);
            // The jump repress is 0.1 s after the first, so it is suppressed; the first
            // exit press has its own timer and emits.
            adapter.Step(0.1f, jump: true, interact: false, exitStation: true);
            AssertKinds(adapter,
                ControlIntentKind.UseItemOrJump,
                ControlIntentKind.InteractOrSeat,
                ControlIntentKind.ExitStation);
        }

        [Test]
        public void DebounceWindowBoundaryIsInclusive()
        {
            Adapter exact = new Adapter(0.2f);
            exact.Step(0f, jump: true);
            exact.Step(0.1f, jump: false);
            exact.Step(0.2f, jump: true);
            Assert.AreEqual(2, exact.Count, "press exactly at the window");

            Adapter early = new Adapter(0.2f);
            early.Step(0f, jump: true);
            early.Step(0.1f, jump: false);
            early.Step(0.19f, jump: true);
            Assert.AreEqual(1, early.Count, "press inside the window");
        }

        [Test]
        public void MenuDoesNotResetDebounceTimers()
        {
            // The menu latches physical state but leaves lastActionTime alone, so a
            // press just after the menu closes is still inside the 0.2 s window.
            Adapter adapter = new Adapter(0.2f);
            adapter.Step(0f, jump: true);
            adapter.Step(0.05f, jump: true, menuOpen: true);
            adapter.Step(0.1f, jump: false);
            adapter.Step(0.15f, jump: true);
            AssertKinds(adapter, ControlIntentKind.UseItemOrJump);
        }

        [Test]
        public void ResetClearsEdgesTimersAndComfort()
        {
            Adapter adapter = new Adapter();
            adapter.Step(0f, jump: true, comfortTurn: true);
            Assert.AreEqual(2, adapter.Count);
            Assert.IsTrue(adapter.Reset());
            adapter.Step(0.05f, jump: true, comfortTurn: true);
            Assert.AreEqual(4, adapter.Count);
            Assert.AreEqual(ControlIntentKind.ComfortTurn, adapter.Records[2].Kind);
            Assert.AreEqual(ControlIntentKind.UseItemOrJump, adapter.Records[3].Kind);
        }

        [Test]
        public void PromptVisibilityPolicy()
        {
            Assert.IsTrue(ControlIntentMath.IsPromptVisible(false, false));
            Assert.IsFalse(ControlIntentMath.IsPromptVisible(true, false));
            Assert.IsFalse(ControlIntentMath.IsPromptVisible(false, true));
            Assert.IsFalse(ControlIntentMath.IsPromptVisible(true, true));
        }

        [Test]
        public void OnlyTheThreeModelSourcesAreAccepted()
        {
            Assert.IsTrue(ControlIntentMath.IsSupportedSource(ControlIntentSource.VirtualHandles));
            Assert.IsTrue(ControlIntentMath.IsSupportedSource(ControlIntentSource.NativeVr));
            Assert.IsTrue(ControlIntentMath.IsSupportedSource(ControlIntentSource.Desktop));
            Assert.IsFalse(ControlIntentMath.IsSupportedSource((ControlIntentSource)3));
            Assert.IsFalse(ControlIntentMath.IsSupportedSource((ControlIntentSource)(-1)));
        }

        [Test]
        public void InvalidParametersAndBuffersAreRejected()
        {
            bool[] previous = new bool[ControlIntentMath.ActionCount];
            float[] last = new float[ControlIntentMath.ActionCount];
            bool[] comfort = new bool[2];
            ControlIntentKind[] kinds =
                new ControlIntentKind[ControlIntentMath.MaxIntentsPerFrame];
            int count;

            // The Python model raises for a negative timestamp; this port returns
            // false. NaN timestamps and axes are rejected by project convention.
            Assert.IsFalse(TryProcessRaw(-0.1f, Vector2.zero, Vector2.zero, 0.2f,
                previous, last, comfort, kinds, out count));
            Assert.AreEqual(0, count);
            Assert.IsFalse(TryProcessRaw(float.NaN, Vector2.zero, Vector2.zero, 0.2f,
                previous, last, comfort, kinds, out count));
            Assert.IsFalse(TryProcessRaw(0f, new Vector2(float.NaN, 0f), Vector2.zero, 0.2f,
                previous, last, comfort, kinds, out count));
            Assert.IsFalse(TryProcessRaw(0f, Vector2.zero, new Vector2(0f, float.PositiveInfinity),
                0.2f, previous, last, comfort, kinds, out count));

            // Adapter-construction failures, checked per call here.
            Assert.IsFalse(TryProcessRaw(0f, Vector2.zero, Vector2.zero, -0.1f,
                previous, last, comfort, kinds, out count));
            Assert.IsFalse(TryProcessRaw(0f, Vector2.zero, Vector2.zero, float.NaN,
                previous, last, comfort, kinds, out count));

            // Caller-owned state and buffers must be present and long enough.
            Assert.IsFalse(TryProcessRaw(0f, Vector2.zero, Vector2.zero, 0.2f,
                null, last, comfort, kinds, out count));
            Assert.IsFalse(TryProcessRaw(0f, Vector2.zero, Vector2.zero, 0.2f,
                previous, null, comfort, kinds, out count));
            Assert.IsFalse(TryProcessRaw(0f, Vector2.zero, Vector2.zero, 0.2f,
                previous, last, null, kinds, out count));
            Assert.IsFalse(TryProcessRaw(0f, Vector2.zero, Vector2.zero, 0.2f,
                previous, last, comfort,
                new ControlIntentKind[ControlIntentMath.MaxIntentsPerFrame - 1], out count));
            last[ControlIntentMath.JumpAction] = float.NaN;
            Assert.IsFalse(TryProcessRaw(0f, Vector2.zero, Vector2.zero, 0.2f,
                previous, last, comfort, kinds, out count));
        }

        private static bool TryProcessRaw(
            float timestamp,
            Vector2 move,
            Vector2 look,
            float debounceSeconds,
            bool[] previousDown,
            float[] lastActionTime,
            bool[] comfortState,
            ControlIntentKind[] kindBuffer,
            out int intentCount)
        {
            float[] xBuffer = new float[ControlIntentMath.MaxIntentsPerFrame];
            float[] yBuffer = new float[ControlIntentMath.MaxIntentsPerFrame];
            bool[] valueBuffer = new bool[ControlIntentMath.MaxIntentsPerFrame];
            return ControlIntentMath.TryProcess(
                timestamp,
                move,
                look,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                previousDown,
                lastActionTime,
                comfortState,
                kindBuffer,
                xBuffer,
                yBuffer,
                valueBuffer,
                out intentCount,
                debounceSeconds);
        }

        private static void AssertKinds(Adapter adapter, params ControlIntentKind[] expected)
        {
            Assert.AreEqual(expected.Length, adapter.Count, "intent count");
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], adapter.Records[i].Kind, "kind " + i);
            }
        }

        private static void AssertSameSequence(Adapter expected, Adapter actual)
        {
            Assert.AreEqual(expected.Count, actual.Count, "intent count");
            for (int i = 0; i < expected.Count; i++)
            {
                IntentRecord left = expected.Records[i];
                IntentRecord right = actual.Records[i];
                Assert.AreEqual(left.Kind, right.Kind, "kind " + i);
                Assert.AreEqual(left.Timestamp, right.Timestamp, Tolerance, "timestamp " + i);
                Assert.AreEqual(left.X, right.X, Tolerance, "x " + i);
                Assert.AreEqual(left.Y, right.Y, Tolerance, "y " + i);
                Assert.AreEqual(left.Value, right.Value, "value " + i);
            }
        }
    }
}
