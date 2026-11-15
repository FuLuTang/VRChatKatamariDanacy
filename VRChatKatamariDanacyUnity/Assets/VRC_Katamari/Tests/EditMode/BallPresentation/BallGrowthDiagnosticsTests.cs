using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the growth probe: it ships silent, and while it is silent it does no work at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The probe exists because no line in the project separated "the ledger grew" from "the
    /// mesh grew" (Docs/Design/BallPresentation/VISUAL_GROWTH_AUDIT.md section 4). Its cost
    /// argument is the opposite of a normal feature's: an instance left in the scene must be
    /// free. That is what these tests check, and it is checkable without a running ball:
    /// <list type="bullet">
    /// <item>The serialized switch defaults to off and the interval defaults to 0.5 s, so a
    /// scene author who drops the component in gets no log until they ask for one.</item>
    /// <item>The switch is the FIRST statement of <c>Update</c>, before the clock: a disabled
    /// instance does not even collect <c>Time.deltaTime</c>. If the guard ever moves below the
    /// clock, or a report is reached some other way, this test fails - the observable proof
    /// that no string, no motor getter and no transform read is reachable while it is off.
    /// </item>
    /// <item>The wiring is exactly three references (motor, core mesh, attachment root), so
    /// the probe cannot quietly grow a second source of truth for a size.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Reflection, for the same reason as StoryRoundControllerTests: this test assembly does
    /// not reference UdonSharp.Runtime, so an UdonSharpBehaviour type cannot appear in a field
    /// or a signature here. The behaviour is added by <see cref="Type"/> and driven through its
    /// public <c>Update</c>.
    /// </para>
    /// </remarks>
    public class BallGrowthDiagnosticsTests
    {
        private const string DiagnosticsTypeName =
            "VRCKatamari.BallPresentation.BallGrowthDiagnostics, VRCKatamari.Runtime";

        private const BindingFlags Any =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private GameObject host;
        private Component probe;

        [SetUp]
        public void SetUp()
        {
            Type type = DiagnosticsType();
            Assert.IsNotNull(type, "BallGrowthDiagnostics type not loaded");
            host = new GameObject("BallGrowthDiagnosticsTests");
            probe = host.AddComponent(type);
            Assert.IsNotNull(probe, "AddComponent(BallGrowthDiagnostics) returned null");
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
        }

        /// <summary>
        /// Off by default, reporting every 0.5 s. Both defaults matter: the switch is the bool,
        /// and a zero interval must not turn a later Play into a per-frame log.
        /// </summary>
        [Test]
        public void DefaultsToOff()
        {
            Type type = probe.GetType();

            FieldInfo enabledField = type.GetField("loggingEnabled", Any);
            Assert.IsNotNull(enabledField, "loggingEnabled is missing");
            Assert.AreEqual(false, enabledField.GetValue(probe), "the probe must ship off");

            FieldInfo interval = type.GetField("logIntervalSeconds", Any);
            Assert.IsNotNull(interval, "logIntervalSeconds is missing");
            Assert.AreEqual(0.5f, (float)interval.GetValue(probe), 1e-6f);
        }

        /// <summary>
        /// The probe is wired by three references and nothing else: the motor it reads, the
        /// core mesh it measures and the attachment root it counts.
        /// </summary>
        [Test]
        public void IsWiredByThreeReferences()
        {
            Type type = probe.GetType();
            FieldInfo motor = type.GetField("motor", Any);
            FieldInfo coreMesh = type.GetField("coreMesh", Any);
            FieldInfo attachmentRoot = type.GetField("attachmentRoot", Any);
            Assert.IsNotNull(motor, "motor is missing");
            Assert.IsNotNull(coreMesh, "coreMesh is missing");
            Assert.IsNotNull(attachmentRoot, "attachmentRoot is missing");

            UnityEngine.Object[] links =
            {
                (UnityEngine.Object)motor.GetValue(probe),
                (UnityEngine.Object)coreMesh.GetValue(probe),
                (UnityEngine.Object)attachmentRoot.GetValue(probe),
            };
            for (int i = 0; i < links.Length; i++)
            {
                Assert.IsNull(links[i], "a bare probe must not resolve a scene link by itself");
            }
        }

        /// <summary>
        /// A disabled probe does not advance its own clock and never runs its readiness check,
        /// so nothing it would report can be reached - which is the whole reason it is safe to
        /// leave in a scene the owner is playing.
        /// </summary>
        [Test]
        public void DisabledProbeDoesNotStartItsClock()
        {
            Type type = probe.GetType();
            MethodInfo update = type.GetMethod("Update", Any);
            Assert.IsNotNull(update, "Update is missing");
            Action tick = (Action)Delegate.CreateDelegate(typeof(Action), probe, update);

            FieldInfo elapsed = type.GetField("elapsed", Any);
            FieldInfo ready = type.GetField("ready", Any);
            Assert.IsNotNull(elapsed, "elapsed is missing");
            Assert.IsNotNull(ready, "ready is missing");

            for (int i = 0; i < 100; i++) tick();

            Assert.AreEqual(0f, (float)elapsed.GetValue(probe),
                "a disabled probe advanced its clock, so a report could be reachable");
            Assert.AreEqual(false, ready.GetValue(probe),
                "a disabled probe ran its readiness probe");
        }

        /// <summary>
        /// Turning the switch on with nothing wired must still be harmless: the readiness check
        /// refuses and no line is built.
        /// </summary>
        [Test]
        public void EnabledButUnwiredProbeStaysSilent()
        {
            Type type = probe.GetType();
            FieldInfo enabledField = type.GetField("loggingEnabled", Any);
            Assert.IsNotNull(enabledField, "loggingEnabled is missing");
            enabledField.SetValue(probe, true);

            MethodInfo update = type.GetMethod("Update", Any);
            Assert.IsNotNull(update, "Update is missing");
            Action tick = (Action)Delegate.CreateDelegate(typeof(Action), probe, update);
            for (int i = 0; i < 50; i++) tick();

            FieldInfo ready = type.GetField("ready", Any);
            Assert.IsNotNull(ready, "ready is missing");
            Assert.AreEqual(false, ready.GetValue(probe), "an unwired probe claimed to be ready");

            FieldInfo elapsed = type.GetField("elapsed", Any);
            Assert.AreEqual(0f, (float)elapsed.GetValue(probe),
                "an unwired probe started reporting");
        }

        private static Type DiagnosticsType()
        {
            Type type = Type.GetType(DiagnosticsTypeName);
            if (type != null) return type;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidate =
                    assembly.GetType("VRCKatamari.BallPresentation.BallGrowthDiagnostics");
                if (candidate != null) return candidate;
            }
            return null;
        }
    }
}
