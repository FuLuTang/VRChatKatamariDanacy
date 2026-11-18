using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRCKatamari.BallLayers;
using VRCKatamari.Riding;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the push stance: where the eye sits relative to the ball so that a standing rider's
    /// hands meet the ball's back surface, where the panels hang, and the click target. Expected
    /// numbers are hand calculations of the stated geometry (push angle 45 degrees, shoulder
    /// 0.125 E below and 0.055 E behind the eye, push reach 0.8 * 0.41 E, feet on the ground),
    /// plus the real <see cref="FollowRigMath"/> fed with the derived coefficients, so the test
    /// closes the loop through the code that actually moves the eye.
    /// </summary>
    public class RideViewTests
    {
        private const float Tolerance = 0.02f;

        // The scene's follow-frame bounds the assembly writes next to the derived numbers.
        private const float EyeUpMin = 0.1f;
        private const float EyeUpMax = 128f;
        private const float EyeBackMax = 128f;

        // Steady-state rig for a ball at the origin heading +Z: no filtering history, so the
        // frame is exactly the target. Returns up above and back behind the centre.
        private static void RigFor(float radius, out float up, out float back)
        {
            Vector3 rig;
            float yaw;
            float height;
            bool ok = FollowRigMath.TryUpdate(5f, 8f, Vector3.zero, radius, 0f, 0.02f,
                RideViewMath.EyeBaseUpMeters, RideViewMath.EyeUpPerRadius(), EyeUpMin, EyeUpMax,
                RideViewMath.EyeBaseBackMeters, RideViewMath.EyeBackPerRadius(), EyeBackMax,
                true, false, 0f, 0f, out rig, out yaw, out height);
            Assert.IsTrue(ok);
            up = rig.y;
            back = -rig.z;
        }

        [Test]
        public void RideAssemblerAlignsAPoseOntoAFrameWithoutAnOffset()
        {
            // The pose half of the same fix, without UdonSharp: the assembly's AlignPose must
            // put the entry exactly on the station and the station exactly on the ride frame,
            // so the seated player's root has no authored lateral or vertical offset.
            Type assembler = Type.GetType(
                "VRCKatamari.Editor.Story.RideUxAssembler, Assembly-CSharp-Editor", false);
            if (assembler == null)
            {
                Assert.Ignore("the editor assembly is not loaded");
            }
            Type counters = assembler.GetNestedType("Counters", BindingFlags.NonPublic);
            MethodInfo align = assembler.GetMethod("AlignPose",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(counters, "RideUxAssembler.Counters is missing");
            Assert.IsNotNull(align, "RideUxAssembler.AlignPose is missing");

            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var rig = new GameObject("FollowRigRoot").transform;
                SceneManager.MoveGameObjectToScene(rig.gameObject, scene);
                rig.SetPositionAndRotation(new Vector3(2f, 3f, 7f), Quaternion.Euler(0f, 47f, 0f));
                var rideRoot = new GameObject("RideRoot").transform;
                rideRoot.SetParent(rig, false);
                var station = new GameObject("RideSeat").transform;
                station.SetParent(rideRoot, false);
                var enter = new GameObject("StandPoint").transform;
                enter.SetParent(station, false);
                enter.localPosition = new Vector3(0.45f, -0.15f, 0f);
                enter.localRotation = Quaternion.Euler(0f, 90f, 0f);

                object counterState = Activator.CreateInstance(counters, true);
                align.Invoke(null, new object[] { station, rig, "Align the ride station", counterState });
                align.Invoke(null, new object[] { enter, station, "Align the station entry", counterState });

                Assert.Less(Vector3.Distance(rig.position, station.position), 1e-5f);
                Assert.Less(Quaternion.Angle(rig.rotation, station.rotation), 1e-4f);
                Assert.Less(Vector3.Distance(station.position, enter.position), 1e-5f);
                Assert.Less(Quaternion.Angle(station.rotation, enter.rotation), 1e-4f);
                Assert.Less(Vector3.Distance(Vector3.zero, enter.localPosition), 1e-5f);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void RideAssemblerAlignsTheStationAndEntryToTheRideFrame()
        {
            // Unity-only (engine + UdonSharp editor): the local Roslyn runner reports it as
            // not runnable. It runs the real assembly step on a throwaway preview scene: an
            // old wiring (station offset, entry +0.45/-0.15, anchor on the station) must come
            // out as the station AND the entry on the rig pose, with the compensation anchor
            // on the player-carrying station transform.
            Type assembler = Type.GetType(
                "VRCKatamari.Editor.Story.RideUxAssembler, Assembly-CSharp-Editor", false);
            // VRCStation ships in the precompiled VRCSDK3.dll (assembly "VRCSDK3"), not in an
            // asmdef named VRC.SDK3, so look it up by full name across the loaded assemblies.
            Type vrcStationType = FindLoadedType("VRC.SDK3.Components.VRCStation");
            Type udonSharpUndo = Type.GetType("UdonSharpEditor.UdonSharpUndo, UdonSharp.Editor", false);
            if (assembler == null || vrcStationType == null || udonSharpUndo == null)
            {
                Assert.Ignore("the editor assembly, the VRC SDK or UdonSharp editor is not loaded");
            }

            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var rig = new GameObject("FollowRigRoot").transform;
                SceneManager.MoveGameObjectToScene(rig.gameObject, scene);
                rig.SetPositionAndRotation(new Vector3(2f, 3f, 7f), Quaternion.Euler(0f, 47f, 0f));
                var rideRoot = new GameObject("RideRoot").transform;
                rideRoot.SetParent(rig, false);
                var station = new GameObject("RideSeat").transform;
                station.SetParent(rideRoot, false);
                var enter = new GameObject("StandPoint").transform;
                enter.SetParent(station, false);
                enter.localPosition = new Vector3(0.45f, -0.15f, 0f);
                var exit = new GameObject("RideExitPoint").transform;
                exit.SetParent(station, false);

                Component vrcStation = station.gameObject.AddComponent(vrcStationType);
                SetRideReference(vrcStation, "stationEnterPlayerLocation", enter);
                SetRideReference(vrcStation, "stationExitPlayerLocation", exit);
                Component ride = AddRideBehaviour(station.gameObject, "VRCKatamari.Riding.BallRideStation");
                Component seatEye = AddRideBehaviour(station.gameObject,
                    "VRCKatamari.Riding.SeatEyeCompensation");
                SetRideReference(ride, "station", vrcStation);
                SetRideReference(seatEye, "seatAnchor", station);

                Type counters = assembler.GetNestedType("Counters", BindingFlags.NonPublic);
                MethodInfo assemble = assembler.GetMethod("AssembleSeatLinks",
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo standing = assembler.GetMethod("AssembleStandingStation",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.IsNotNull(counters, "RideUxAssembler.Counters is missing");
                Assert.IsNotNull(assemble, "RideUxAssembler.AssembleSeatLinks is missing");
                Assert.IsNotNull(standing, "RideUxAssembler.AssembleStandingStation is missing");
                object counterState = Activator.CreateInstance(counters, true);
                assemble.Invoke(null, new object[] { seatEye, null, ride, rig, counterState });
                standing.Invoke(null, new object[] { ride, counterState });

                // The station moves every frame, so it is a moving-station station, standing.
                var stationSettings = new SerializedObject(vrcStation);
                Assert.IsFalse(stationSettings.FindProperty("seated").boolValue);
                Assert.AreEqual(2, stationSettings.FindProperty("PlayerMobility").intValue,
                    "PlayerMobility must be ImmobilizeForVehicle");

                var compensation = new SerializedObject(seatEye);
                Assert.AreSame(station, compensation.FindProperty("seatAnchor").objectReferenceValue,
                    "the compensation must drive the player-carrying station transform");
                Assert.AreSame(rig, compensation.FindProperty("followRigRoot").objectReferenceValue);
                Assert.Less(Vector3.Distance(rig.position, station.position), 1e-4f,
                    "the station must sit on the ride frame");
                Assert.Less(Quaternion.Angle(rig.rotation, station.rotation), 1e-3f);
                Assert.Less(Vector3.Distance(station.position, enter.position), 1e-4f,
                    "the entry must put the player at the station, with no authored offset");
                Assert.Less(Vector3.Distance(Vector3.zero, enter.localPosition), 1e-4f);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static Component AddRideBehaviour(GameObject owner, string name)
        {
            Type behaviour = typeof(RideViewMath).Assembly.GetType(name, true);
            // The test asmdef intentionally does not reference the UdonSharp editor assembly.
            Type undo = Type.GetType("UdonSharpEditor.UdonSharpUndo, UdonSharp.Editor", true);
            foreach (MethodInfo method in undo.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == "AddComponent" && method.IsGenericMethodDefinition
                    && parameters.Length == 1 && parameters[0].ParameterType == typeof(GameObject))
                    return (Component)method.MakeGenericMethod(behaviour).Invoke(null, new object[] { owner });
            }
            Assert.Fail("UdonSharpUndo.AddComponent<T>(GameObject) is unavailable");
            return null;
        }

        private static void SetRideReference(Component owner, string property, UnityEngine.Object value)
        {
            var serialized = new SerializedObject(owner);
            serialized.FindProperty(property).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [TestCase(0f, 90f)]
        [TestCase(359f, 12f)]
        [TestCase(253f, 187f)]
        [TestCase(90f, 90f)]
        public void EyeAndCompensatedHeadStayBehindFilteredYaw(float previousYaw, float targetYaw)
        {
            const float radius = 0.15f;
            Vector3 ball = new Vector3(31f, radius, -8f);
            Assert.IsTrue(FollowRigMath.TryUpdate(5f, 8f, ball, radius, targetYaw, 0.02f,
                0f, RideViewMath.EyeUpPerRadius(), EyeUpMin, EyeUpMax,
                0f, RideViewMath.EyeBackPerRadius(), EyeBackMax,
                false, true, previousYaw, 1.654625f * radius,
                out Vector3 rig, out float yaw, out _));
            if (previousYaw != targetYaw) Assert.AreNotEqual(targetYaw, yaw);
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            Quaternion toRig = Quaternion.Inverse(rotation);
            Vector3 expected = new Vector3(0f, 1.654625f * radius, -1.176792f * radius);
            Assert.Less((toRig * (rig - ball) - expected).magnitude, 1e-5f);

            // The same anchor both carries the head and receives the compensation.
            // Sample a head that is not centred over the entry, then place the entry.
            float eyeHeight = 2.654625f * radius;
            Vector3 headOffset = new Vector3(0.03f, eyeHeight, -0.02f);
            Assert.IsTrue(SeatEyeMath.TrySampleHeadOffset(rig + rotation * headOffset, rig,
                rotation, rotation, out Vector3 measured, out _, out _));
            float feet = RideStanceMath.FeetTargetInRig(ball.y, radius, rig.y);
            Vector3 solveFor = RideStanceMath.EffectiveHeadInRig(measured, true, feet);
            Assert.IsTrue(SeatEyeMath.TryComputeSeatOffset(solveFor, Vector3.zero,
                RideStanceMath.UnlimitedMeters, 0.35f * SeatEyeMath.LimitScaleForEyeHeight(eyeHeight),
                false, 0f, 0f, true, out Vector3 seatOffset, out _, out _, out _));
            Vector3 entry = rig + rotation * seatOffset;
            Vector3 head = entry + rotation * headOffset;
            Assert.AreEqual(0f, entry.y, 1e-5f);
            Assert.Less((toRig * (head - ball) - expected).magnitude, 1e-5f);
        }

        [Test]
        public void DerivedCoefficients_MatchTheirClosedForm()
        {
            // s = sin 45 = 0.707107, reach = 0.8 * 0.41 = 0.328.
            // e = (1 + s) / (1 - 0.125 - 0.328 s) = 1.707107 / 0.643069 = 2.654625.
            Assert.AreEqual(0.328f, RideViewMath.PushReachPerEyeHeight(), 1e-6f);
            Assert.AreEqual(2.654625f, RideViewMath.EyeHeightPerRadius(), 1e-4f);
            Assert.AreEqual(1.654625f, RideViewMath.EyeUpPerRadius(), 1e-4f);
            // back = -0.055 e + (1 + 0.328 e) cos 45 = -0.146004 + 1.870717 * 0.707107 = 1.176792.
            Assert.AreEqual(1.176792f, RideViewMath.EyeBackPerRadius(), 1e-4f);
            // No additive base: the picture is the same at every size.
            Assert.AreEqual(0f, RideViewMath.EyeBaseUpMeters);
            Assert.AreEqual(0f, RideViewMath.EyeBaseBackMeters);
        }

        // The stance's own closure: the eye is one eye height above the ground (feet on the
        // ground beside the ball), and the shoulder-to-centre distance is the push reach plus
        // the radius along a 45 degree line (the push goes through the centre).
        [TestCase(0.15f)]
        [TestCase(1f)]
        [TestCase(10f)]
        public void Stance_FeetOnTheGroundAndArmAlongTheRadius(float radius)
        {
            float up;
            float back;
            Assert.IsTrue(RideViewMath.TryEyeOffset(radius, out up, out back));
            float eye = RideViewMath.EyeHeightPerRadius() * radius;
            Assert.AreEqual(eye, up + radius, eye * 1e-4f);

            float shoulderForward = -RideViewMath.ShoulderBehindEyePerEyeHeight * eye;
            float shoulderDown = RideViewMath.ShoulderBelowEyePerEyeHeight * eye;
            float dx = back - shoulderForward;
            float dy = up - shoulderDown;
            float reach = RideViewMath.PushReachPerEyeHeight() * eye;
            Assert.AreEqual(reach + radius, Mathf.Sqrt(dx * dx + dy * dy), radius * 1e-3f);
            Assert.AreEqual(45f, Mathf.Atan2(dy, dx) * Mathf.Rad2Deg, 1e-2f);
        }

        // Ball centre 54.58 deg below the horizon, half angle asin(1 / 2.0305) = 29.51 deg: the
        // ball spans 25.07 to 84.08 deg below the horizon at every size.
        [TestCase(0.111f)]
        [TestCase(0.25f)]
        [TestCase(0.4f)]
        [TestCase(1f)]
        [TestCase(10f)]
        public void BallView_IsTheSameAtEveryPlayedSize(float radius)
        {
            float up;
            float back;
            RigFor(radius, out up, out back);
            float depression;
            float halfAngle;
            Assert.IsTrue(RideViewMath.TryBallView(radius, up, back, out depression,
                out halfAngle));
            Assert.AreEqual(54.58f, depression, Tolerance);
            Assert.AreEqual(29.51f, halfAngle, Tolerance);
            Assert.AreEqual(25.07f, depression - halfAngle, Tolerance);
            if (radius == 1f)
            {
                Assert.AreEqual(1.6546f, up, 1e-3f);
                Assert.AreEqual(1.1768f, back, 1e-3f);
            }
        }

        [Test]
        public void HandContact_IsInFrontOfTheChestWithinReach()
        {
            // At r = 1: forward = 1.176792 - cos 45 = 0.469685, down = 1.654625 - sin 45 =
            // 0.947518, i.e. 0.177 E ahead of and 0.357 E below the eye (E = 2.654625).
            float forward;
            float down;
            Assert.IsTrue(RideViewMath.TryHandContactFromEye(1f, out forward, out down));
            Assert.AreEqual(0.469685f, forward, 1e-4f);
            Assert.AreEqual(0.947518f, down, 1e-4f);
            float eye = RideViewMath.EyeHeightPerRadius();
            Assert.AreEqual(0.1769f, forward / eye, 1e-3f);
            Assert.AreEqual(0.3569f, down / eye, 1e-3f);
            Assert.IsFalse(RideViewMath.TryHandContactFromEye(-1f, out forward, out down));
        }

        [Test]
        public void HandGap_IsTheDistanceToTheSurface()
        {
            Vector3 centre = new Vector3(1f, 2f, 3f);
            Assert.AreEqual(0.5f, RideViewMath.HandGapMeters(centre + new Vector3(0f, 0f, 1.5f),
                centre, 1f), 1e-5f);
            Assert.AreEqual(-0.25f, RideViewMath.HandGapMeters(centre + new Vector3(0.75f, 0f, 0f),
                centre, 1f), 1e-5f);
            Assert.IsTrue(float.IsPositiveInfinity(RideViewMath.HandGapMeters(
                new Vector3(float.NaN, 0f, 0f), centre, 1f)));
        }

        [Test]
        public void PanelScale_FollowsTheRider()
        {
            Assert.AreEqual(1f, RideViewMath.PanelScaleForEyeHeight(1.64f), 1e-6f);
            // D = 0.3 m: E = 2.654625 * 0.15 = 0.398194, scale 0.398194 / 1.64 = 0.242801.
            Assert.AreEqual(0.242801f, RideViewMath.PanelScaleForEyeHeight(0.398194f), 1e-5f);
            Assert.AreEqual(1f, RideViewMath.PanelScaleForEyeHeight(0f));
            Assert.AreEqual(1f, RideViewMath.PanelScaleForEyeHeight(float.NaN));
        }

        [Test]
        public void EyeOffset_MatchesTheFollowFrame()
        {
            float expectedUp;
            float expectedBack;
            Assert.IsTrue(RideViewMath.TryEyeOffset(0.25f, out expectedUp, out expectedBack));
            float up;
            float back;
            RigFor(0.25f, out up, out back);
            Assert.AreEqual(expectedUp, up, 1e-4f);
            Assert.AreEqual(expectedBack, back, 1e-4f);
            // Hand value: 1.654625 * 0.25 = 0.413656.
            Assert.AreEqual(0.4137f, up, 1e-3f);
        }

        [Test]
        public void EyeOffset_RefusesNegativeAndNonFiniteRadius()
        {
            float up;
            float back;
            Assert.IsFalse(RideViewMath.TryEyeOffset(-0.1f, out up, out back));
            Assert.AreEqual(0f, up);
            Assert.IsFalse(RideViewMath.TryEyeOffset(float.NaN, out up, out back));
            Assert.IsFalse(RideViewMath.TryEyeOffset(float.PositiveInfinity, out up, out back));
        }

        [Test]
        public void BallView_RefusesAnEyeInsideTheBall()
        {
            float depression;
            float halfAngle;
            Assert.IsFalse(RideViewMath.TryBallView(1f, 0.3f, 0.3f, out depression,
                out halfAngle));
            Assert.IsFalse(RideViewMath.TryBallView(0f, 1f, 1f, out depression, out halfAngle));
        }

        [Test]
        public void HudPlacement_RoundTripsDistanceAzimuthAndElevation()
        {
            // 1.6 m straight ahead and level: (0, 0, 1.6).
            Vector3 ahead = RideViewMath.HudLocalPosition(1.6f, 0f, 0f);
            Assert.AreEqual(0f, ahead.x, 1e-5f);
            Assert.AreEqual(0f, ahead.y, 1e-5f);
            Assert.AreEqual(1.6f, ahead.z, 1e-5f);

            // 90 degrees right is +X; 30 degrees up at 2 m is y = 1.0, horizontal 1.732.
            Vector3 right = RideViewMath.HudLocalPosition(2f, 90f, 0f);
            Assert.AreEqual(2f, right.x, 1e-5f);
            Assert.AreEqual(0f, right.z, 1e-5f);
            Vector3 up = RideViewMath.HudLocalPosition(2f, 0f, 30f);
            Assert.AreEqual(1f, up.y, 1e-5f);
            Assert.AreEqual(1.7320508f, up.z, 1e-5f);

            // The panel's forward points away from the eye, along the placement direction.
            Vector3 place = RideViewMath.HudLocalPosition(1.6f, -20f, 15f);
            Quaternion facing = RideViewMath.HudLocalRotation(-20f, 15f);
            Vector3 normal = facing * Vector3.forward;
            Vector3 away = place.normalized;
            Assert.AreEqual(away.x, normal.x, 1e-4f);
            Assert.AreEqual(away.y, normal.y, 1e-4f);
            Assert.AreEqual(away.z, normal.z, 1e-4f);
        }

        // Hand values: 1.25 * 0.111 = 0.139 -> floor 0.25; 1.25 * 0.4 = 0.5; 1.25 * 15 = 18.75.
        [TestCase(0.111f, 0.25f)]
        [TestCase(0.2f, 0.25f)]
        [TestCase(0.4f, 0.5f)]
        [TestCase(15f, 18.75f)]
        public void SeatTrigger_CoversTheBallAndScalesWithIt(float collisionRadius,
            float expected)
        {
            float radius = RideViewMath.SeatTriggerRadius(collisionRadius);
            Assert.AreEqual(expected, radius, 1e-4f);
            Assert.GreaterOrEqual(radius, collisionRadius);
        }

        [Test]
        public void SeatTrigger_FallsBackToTheFloorOnBadInput()
        {
            Assert.AreEqual(0.25f, RideViewMath.SeatTriggerRadius(float.NaN), 1e-6f);
            Assert.AreEqual(0.25f, RideViewMath.SeatTriggerRadius(-1f), 1e-6f);
        }

        private static Type FindLoadedType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type found = assembly.GetType(fullName, false);
                if (found != null) return found;
            }
            return null;
        }
    }
}
