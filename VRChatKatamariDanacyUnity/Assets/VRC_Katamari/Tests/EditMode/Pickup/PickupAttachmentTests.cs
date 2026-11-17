using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRCKatamari.BallLayers;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>Pure value regressions: no GameObject or engine scene is required.</summary>
    public class PickupAttachmentPoseTests
    {
        private static Quaternion RotationAboutZ(float degrees)
        {
            double half = degrees * System.Math.PI / 360.0;
            return new Quaternion(0f, 0f, (float)System.Math.Sin(half), (float)System.Math.Cos(half));
        }

        private static void AssertSameRotation(Quaternion expected, Quaternion actual)
        {
            float dot = expected.x * actual.x + expected.y * actual.y
                + expected.z * actual.z + expected.w * actual.w;
            Assert.AreEqual(1f, System.Math.Abs(dot), 1e-5f);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(3)]
        [TestCase(8)]
        public void DelayedConsumptionKeepsTheContactLocalPose(int laterSteps)
        {
            // A quarter-turn roll about -Z: travel +X by pi*r/2 on a +Y floor.
            // Object at contact centre + (r+halfWidth,0,0) becomes local +Y.
            const float radius = 0.1f;
            Vector3 previousCenter = new Vector3(2f, 1f, -3f);
            Vector3 contactCenter = previousCenter + new Vector3(Mathf.PI * radius / 2f, 0f, 0f);
            Quaternion contactRoll = RotationAboutZ(-90f);
            Vector3 objectPosition = contactCenter + new Vector3(0.12f, 0f, 0f);
            Vector3 size = new Vector3(0.04f, 0.06f, 0.08f);
            Vector3 localPosition;
            Quaternion localRotation;
            Assert.IsTrue(AttachmentMath.TryCaptureContactPose(previousCenter, Quaternion.identity,
                contactCenter, Vector3.up, radius, false, 1f, objectPosition, Quaternion.identity,
                Vector3.one, out localPosition, out localRotation));
            Assert.Less(Vector3.Distance(new Vector3(0f, 0.12f, 0f), localPosition), 1e-5f);
            AssertSameRotation(RotationAboutZ(90f), localRotation);

            // Motor can travel another N * (4 m/s * 0.02 s) before the host reads the ring.
            Vector3 laterCenter = contactCenter;
            Quaternion laterRoll = contactRoll;
            for (int i = 0; i < laterSteps; i++)
            {
                laterCenter += new Vector3(0.08f, 0f, 0f);
                laterRoll = RotationAboutZ(-0.08f / radius * Mathf.Rad2Deg) * laterRoll;
            }
            Vector3 attachedWorld;
            Quaternion attachedRotation;
            Assert.IsTrue(AttachmentMath.TryLocalPoseToWorld(laterCenter, laterRoll, localPosition,
                localRotation, Vector3.one, out attachedWorld, out attachedRotation));
            Vector3 consumedLocal;
            Quaternion consumedRotation;
            Assert.IsTrue(AttachmentMath.TryWorldPoseToLocal(laterCenter, laterRoll, attachedWorld,
                attachedRotation, Vector3.one, out consumedLocal, out consumedRotation));
            Assert.Less(Vector3.Distance(localPosition, consumedLocal), 1e-5f);
            AssertSameRotation(localRotation, consumedRotation);

            float gateGap;
            float attachedGap;
            Assert.IsTrue(AttachmentMath.TryComputeBoxGap(size, Quaternion.identity, objectPosition,
                contactCenter, radius, 0f, out gateGap));
            Assert.IsTrue(AttachmentMath.TryComputeBoxGap(size, consumedRotation, consumedLocal,
                Vector3.zero, radius, 0f, out attachedGap));
            Assert.AreEqual(0f, gateGap, 1e-6f, "face at 0.12 - 0.02 = 0.10 m");
            Assert.AreEqual(gateGap, attachedGap, 1e-6f);
        }

        [Test]
        public void GapUsesTheGateMarginNotTheFixedVisualCoreOrLaterGrowth()
        {
            // Face at .155 m, collision r=.15 m plus margin=.005 m: exactly touching.
            float gap;
            Assert.IsTrue(AttachmentMath.TryComputeBoxGap(new Vector3(.02f, .02f, .02f),
                Quaternion.identity, new Vector3(.165f, 0f, 0f), Vector3.zero, .15f, .005f, out gap));
            Assert.AreEqual(0f, gap, 1e-6f);
            Assert.IsFalse(AttachmentMath.TryComputeBoxGap(Vector3.one, Quaternion.identity,
                Vector3.zero, Vector3.zero, float.NaN, 0f, out gap));
        }

        [TestCase(1f, 0f, 0f)]
        [TestCase(1f, 1f, 0f)]
        [TestCase(1f, 1f, 1f)]
        public void PickupContactIsASphereInEveryDirection(float x, float y, float z)
        {
            Vector3 direction = new Vector3(x, y, z).normalized;
            string[] allowed = { "test" };
            // Point geometry isolates the ball shape: .14 > r=.10 even in a cube corner.
            Assert.AreEqual(AbsorbOutcome.RejectOutOfReach, PickupAbsorbMath.EvaluateAbsorb(
                true, "test", allowed, 1, Vector3.zero, Quaternion.identity, .14f * direction,
                Vector3.zero, .10f, 0f, .7f, .20f, 1f));
        }
    }

    /// <summary>
    /// Needs Unity: exercises the actual event ring and PickupFieldHost consumer. All
    /// objects live in a disposable preview scene, not the operator's open scene. No Undo.
    /// </summary>
    public class PickupAttachmentIntegrationTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private Scene preview;
        private object motor;
        private object host;
        private Transform prop;
        private Transform root;

        [SetUp]
        public void SetUp()
        {
            preview = EditorSceneManager.NewPreviewScene();
            motor = Create("attachment_motor").AddComponent(typeof(AttachmentMath).Assembly.GetType("VRCKatamari.BallMotor.KatamariBallMotor"));
            Call(motor, "Start");
            host = Create("attachment_host").AddComponent(typeof(AttachmentMath).Assembly.GetType("VRCKatamari.Pickup.PickupFieldHost"));
            prop = Create("attachment_prop").transform;
            root = Create("attachment_root").transform;
            Set(host, "motor", motor);
            Set(host, "attachmentRoot", root);
            Set(host, "propCount", 1);
            Set(host, "itemIds", new[] { "test-item" });
            Set(host, "kinds", new[] { "test" });
            Set(host, "propTransforms", new[] { prop });
            Set(host, "propStates", new[] { 0 });
            Set(host, "propOwnSizes", new[] { new Vector3(.04f, .06f, .08f) });
            Set(host, "kindTallyNames", new string[1]);
            Set(host, "kindTallyCounts", new int[1]);
        }

        [TearDown]
        public void TearDown()
        {
            if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
        }

        private GameObject Create(string name)
        {
            GameObject go = new GameObject(name);
            go.hideFlags = HideFlags.DontSave;
            SceneManager.MoveGameObjectToScene(go, preview);
            return go;
        }

        private static void Set(object target, string field, object value)
        {
            FieldInfo info = target.GetType().GetField(field, Any);
            Assert.IsNotNull(info, field);
            info.SetValue(target, value);
        }

        private static object Call(object target, string method, params object[] args)
        {
            MethodInfo info = target.GetType().GetMethod(method, Any);
            Assert.IsNotNull(info, method);
            return info.Invoke(target, args);
        }

        private void ReadPayload(out Vector3 local, out Quaternion rotation, out Vector3 center,
            out float radius, out float margin, out int delay)
        {
            object[] args = { 0, Vector3.zero, Quaternion.identity, Vector3.zero, 0f, 0f, 0 };
            Assert.IsTrue((bool)Call(motor, "TryGetAbsorbedEventAttachment", args));
            local = (Vector3)args[1];
            rotation = (Quaternion)args[2];
            center = (Vector3)args[3];
            radius = (float)args[4];
            margin = (float)args[5];
            delay = (int)args[6];
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(4)]
        public void HostConsumesFrozenEventPoseAfterMotorAndFreePropMoved(int steps)
        {
            Vector3 contactCenter = new Vector3(1f, .1f, 2f);
            Quaternion contactRotation = Quaternion.AngleAxis(35f, Vector3.up);
            Set(motor, "ballCenterWorld", contactCenter);
            Set(motor, "visualRollRotation", contactRotation);
            Set(motor, "collisionRadiusWorldM", .1f);
            Set(motor, "coreRadiusWorldM", .1f);
            Set(motor, "authorityFrame", 10);
            Assert.IsTrue((bool)Call(motor, "SubmitCandidateWithVolumeKMm3", true, "test", new Vector3(.04f,.06f,.08f),
                Quaternion.identity, contactCenter + new Vector3(.12f, 0f, 0f), 1L, "test-item", 0, .7f));
            Call(motor, "RecordAbsorbedEvent", 0, contactCenter, Vector3.up);

            Vector3 local;
            Quaternion rotation;
            Vector3 center;
            float radius;
            float margin;
            int delay;
            ReadPayload(out local, out rotation, out center, out radius, out margin, out delay);
            Vector3 laterCenter = contactCenter + new Vector3(.08f * steps, .03f, 0f);
            Quaternion laterRotation = Quaternion.AngleAxis(25f * steps, Vector3.forward) * contactRotation;
            Set(motor, "ballCenterWorld", laterCenter);
            Set(motor, "visualRollRotation", laterRotation);
            Set(motor, "collisionRadiusWorldM", .15f); // later growth must not change the payload
            Set(motor, "authorityFrame", 10 + steps);
            root.SetPositionAndRotation(laterCenter, laterRotation);
            prop.SetPositionAndRotation(new Vector3(90f, 80f, 70f), Quaternion.Euler(20f, 30f, 40f));
            Call(host, "ConsumeAbsorbedEvents");
            Assert.AreEqual(root, prop.parent);
            Assert.Less(Vector3.Distance(local, prop.localPosition), 1e-6f);
            Assert.Less(Quaternion.Angle(rotation, prop.localRotation), .05f);
            Assert.AreEqual(1, (int)Call(host, "GetAttachedCount"));
            ReadPayload(out local, out rotation, out center, out radius, out margin, out delay);
            Assert.AreEqual(steps, delay);
            Assert.AreEqual(.1f, radius);
            float gap;
            Assert.IsTrue(AttachmentMath.TryComputeBoxGap(new Vector3(.04f,.06f,.08f),
                prop.localRotation, prop.localPosition, Vector3.zero, radius, margin, out gap));
            Assert.AreEqual(0f, gap, 1e-6f);
            Call(host, "ConsumeAbsorbedEvents");
            Assert.AreEqual(1, (int)Call(host, "GetAttachedCount"), "cursor replay never attaches twice");
        }

        [Test]
        public void MotorRejectsCubeCornerReach()
        {
            Call(motor, "SetAllowedKinds", new[] { "test" }, 1);
            Set(motor, "collisionRadiusWorldM", .1f);
            Set(motor, "semanticDiameterWorldM", .2f);
            Vector3 corner = new Vector3(1f, 1f, 1f).normalized * .14f;
            Assert.IsTrue((bool)Call(motor, "SubmitCandidateWithVolumeKMm3", true, "test", Vector3.zero,
                Quaternion.identity, corner, 1L, "test-item", 0, .7f));
            Call(motor, "EvaluatePendingCandidates", Vector3.zero);
            Assert.AreEqual((int)AbsorbOutcome.RejectOutOfReach, (int)Call(motor, "GetRejectEventReason", 0));
            Assert.AreEqual(.1f, (float)Call(motor, "GetPickupReachWorldM"));
        }
    }
}
