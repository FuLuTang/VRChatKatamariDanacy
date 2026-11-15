using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Motor-level checks for the ball contact v2 flag (CONTACT_MODEL_V2.md §7.1 S3):
    /// the geometry registration, the OBB contact authority, the stuck-recovery state
    /// machine and the null-space the v2 getters must report while the flag is off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The motor is stepped headless through reflection: <c>StepBall(dt)</c> takes its
    /// delta as an argument and reads no <c>Time.deltaTime</c>, so the same private
    /// method the Update loop calls can be driven from an EditMode test with a fixed
    /// 0.02 s step. That is the existing pattern in this folder (BankReasonTests,
    /// CandidateQueueIdempotencyTests) and it is why these tests need no scene: the
    /// geometry is registered through the public AddBox/AddObb API and the only other
    /// inputs are the debug move axes.
    /// </para>
    /// <para>
    /// What these tests do NOT claim: they are not the design's W/R/S acceptance cells
    /// (those need the test field's timelines and the Play Mode driver, S5), and they do
    /// not measure frame time. They pin the invariants a code review cannot see:
    /// no penetration into a registered solid, a landing height of exactly surface + r,
    /// the recovery machine reaching its hold instead of looping, the window's explicit
    /// overflow verdict, and the shape ordering of §4A.
    /// </para>
    /// </remarks>
    public class ContactModelV2Tests
    {
        private const string MotorTypeName =
            "VRCKatamari.BallMotor.KatamariBallMotor, VRCKatamari.Runtime";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic |
            BindingFlags.Public;

        private GameObject host;
        private Component motor;
        private Type motorType;

        [SetUp]
        public void SetUp()
        {
            motorType = Type.GetType(MotorTypeName);
            if (motorType == null)
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type candidate = assembly.GetType("VRCKatamari.BallMotor.KatamariBallMotor");
                    if (candidate != null)
                    {
                        motorType = candidate;
                        break;
                    }
                }
            }
            Assert.IsNotNull(motorType, "KatamariBallMotor type not loaded");
            host = new GameObject("ContactModelV2Tests");
            motor = host.AddComponent(motorType);
            Invoke("Start");
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
        }

        private object Invoke(string name, params object[] args)
        {
            MethodInfo method = motorType.GetMethod(name, Any);
            Assert.IsNotNull(method, name + " not found");
            return method.Invoke(motor, args);
        }

        private void SetField(string name, object value)
        {
            FieldInfo field = motorType.GetField(name, Any);
            Assert.IsNotNull(field, name + " field not found");
            field.SetValue(motor, value);
        }

        private float GetFloat(string name)
        {
            FieldInfo field = motorType.GetField(name, Any);
            Assert.IsNotNull(field, name + " field not found");
            return (float)field.GetValue(motor);
        }

        private Vector3 GetCenter()
        {
            return (Vector3)Invoke("GetCenterWorld");
        }

        private float Radius()
        {
            return (float)Invoke("GetCollisionRadiusWorldM");
        }

        /// <summary>
        /// Replaces the motor under test with a fresh one. A test that needs several
        /// independent spheres (one per step height) uses this instead of re-entering
        /// SetUp/TearDown, so every case starts from a clean table and nothing leaks.
        /// </summary>
        private void RecreateMotor()
        {
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
            host = new GameObject("ContactModelV2Tests");
            motor = host.AddComponent(motorType);
            Invoke("Start");
        }

        private void EnableV2()
        {
            Invoke("SetContactModelV2Enabled", true);
            Assert.AreEqual(true, (bool)Invoke("IsContactModelV2"),
                "the contact model flag must read back");
        }

        private void Step(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Invoke("StepBall", 0.02f);
            }
        }

        private bool AddBox(Vector3 minimum, Vector3 maximum)
        {
            return (bool)Invoke("AddBox", minimum, maximum);
        }

        private bool AddObb(Vector3 center, Vector3 halfExtents, int surfaceId)
        {
            return (bool)Invoke("AddObb", center, new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f), halfExtents, surfaceId, 0);
        }

        [Test]
        public void TheFlagDefaultsToOffAndTheV2ReadingsAreNeutral()
        {
            Assert.AreEqual(false, (bool)Invoke("IsContactModelV2"),
                "the serialized default must keep the v1 path");
            Step(10);
            Assert.AreEqual(0, (int)Invoke("GetContactCount"));
            Assert.AreEqual(0, (int)Invoke("GetNearObbCount"));
            Assert.AreEqual(0, (int)Invoke("GetSubstepCount"));
            Assert.AreEqual(0, (int)Invoke("GetClimbState"));
            Assert.AreEqual(0, (int)Invoke("GetRecoveryState"));
            Assert.AreEqual(false, (bool)Invoke("WasContactIncomplete"));
        }

        [Test]
        public void AddBoxAlsoRegistersAnAxisAlignedObb()
        {
            Assert.IsTrue(AddBox(new Vector3(-2f, -1f, -2f), new Vector3(2f, 0f, 2f)),
                "the v1 box registration must still accept");
            Assert.AreEqual(1, (int)Invoke("GetStaticObbCount"),
                "the same box must exist in the v2 table, so an old scene runs with the flag on");
            Assert.AreEqual(256, (int)Invoke("GetStaticObbCapacity"));
        }

        [Test]
        public void AnObbThatIsNotOrthonormalIsRefused()
        {
            // The helper above passes the three world axes, so a half-extent triple is
            // always a VALID box: the shear has to be put in the axes themselves, which
            // is what a non-uniformly scaled parent times a rotation produces.
            Assert.IsFalse((bool)Invoke("AddObb", new Vector3(0f, 0f, 0f),
                new Vector3(1f, 1f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                new Vector3(1f, 1f, 1f), 1, 0),
                "a non-unit axis must be refused rather than queried");
            Assert.IsFalse((bool)Invoke("AddObb", new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f),
                new Vector3(1f, 1f, 1f), 1, 0),
                "two parallel axes must be refused rather than queried");
            Assert.AreEqual(0, (int)Invoke("GetStaticObbCount"));
            Assert.AreEqual(2, (int)Invoke("GetStaticObbRefusalCount"));
            Assert.AreNotEqual("", (string)Invoke("GetLastStaticObbRefusalReason"));
        }

        [Test]
        public void TheStaticObbTableTakesFarMoreThanTheV1BoxCapacity()
        {
            for (int i = 0; i < 40; i++)
            {
                AddObb(new Vector3(100f + i * 3f, 0f, 0f), new Vector3(1f, 1f, 1f), 1 + i);
            }
            Assert.AreEqual(40, (int)Invoke("GetStaticObbCount"),
                "the v2 table is 256, not the v1 eight");
            Invoke("ClearObbs");
            Assert.AreEqual(0, (int)Invoke("GetStaticObbCount"));
        }

        [Test]
        public void TheBallLandsOnAnObbTopAtOneRadiusAboveIt()
        {
            EnableV2();
            float radius = Radius();
            AddObb(new Vector3(0f, 0.25f, 0f), new Vector3(1f, 0.25f, 1f), 1);
            Invoke("TeleportTo", new Vector3(0f, 1.2f, 0f));
            Step(40);
            Vector3 center = GetCenter();
            Assert.AreEqual(0.5f + radius, center.y, 0.01f,
                "a resting ball's centre sits exactly one radius above the OBB top");
            Assert.AreEqual(1, (int)Invoke("GetSupportKind"),
                "the support is a static box (kind 1)");
            Assert.AreEqual(0.5f, (float)Invoke("GetSupportSurfaceHeightWorldM"), 0.01f);
            Assert.Greater((int)Invoke("GetContactCount"), 0);
            Assert.AreEqual(ContactCodes.ClassRide, (int)Invoke("GetDominantContactClass"),
                "the dominant contact must be a rideable (RIDE) one");
        }

        [Test]
        public void BothContactModelsRestTheBallAtTheSameHeightOnAFlatFloor()
        {
            // The v1 authority solves the ground analytically, the v2 authority treats it
            // as a plane contact. On a flat floor the two must agree, which is the
            // cheapest proof that the flag does not silently change the resting state.
            float radius = Radius();
            Step(30);
            float v1Height = GetCenter().y;
            EnableV2();
            Invoke("TeleportTo", new Vector3(0f, radius + 0.2f, 0f));
            Step(30);
            float v2Height = GetCenter().y;
            Assert.AreEqual(radius, v1Height, 0.005f);
            Assert.AreEqual(v1Height, v2Height, 0.01f,
                "the plane contact and the analytic ground solve agree within a centimetre");
        }

        [Test]
        public void ABallPushedIntoAWallNeverPenetratesIt()
        {
            EnableV2();
            float radius = Radius();
            // A 3 m tall, 4 m wide face 1 m in front of the ball.
            AddObb(new Vector3(0f, 1.5f, 2.5f), new Vector3(2f, 1.5f, 0.5f), 1);
            Invoke("TeleportTo", new Vector3(0f, radius, 0f));
            Invoke("SetMoveAxes", 1f, 0f, 0f);
            Step(150);
            Vector3 center = GetCenter();
            // The wall's near face is z = 2.0, so the centre may not come closer than r.
            Assert.GreaterOrEqual(center.z, 2f - radius - 0.002f,
                "the contact push-out must keep the sphere out of the wall");
            Assert.Greater((int)Invoke("GetContactCount"), 0,
                "pushing into a wall must produce contacts");

            Assert.AreEqual(0, (int)Invoke("GetContactRejectedStepCount"),
                "a plain wall must not make the solver reject its own position");
        }

        [Test]
        public void PushingIntoATallWallEndsInTheRecoveryHoldInsteadOfLooping()
        {
            EnableV2();
            float radius = Radius();
            AddObb(new Vector3(0f, 1.5f, 3.4f), new Vector3(2f, 1.5f, 0.4f), 1);
            // The run-up is deliberately short: the ball must reach the wall SLOWER than
            // the climb gate. v_gate = max(|v_planar|, k_intent * v_drive) = max(v, 1.0)
            // at half push, so a 2 m run-up arrives at ~2 m/s, passes v_req(90 deg) = 1.5,
            // opens the climb channel and spends seconds climbing the 0.3 m cap before it
            // can ever be "stuck". From rest 0.10 m out, contact speed is
            // sqrt(2 * 5 m/s^2 * 0.10 m) = 1.0 m/s, the gate stays closed, and the ball is
            // pressed against the wall with driveIntent 0.5 from the first contact on.
            Invoke("TeleportTo", new Vector3(0f, radius, 3f - radius - 0.1f));
            Invoke("SetMoveAxes", 0.5f, 0f, 0f);
            Step(300);
            int recoveries = (int)Invoke("GetRecoveryCount");
            Assert.GreaterOrEqual(recoveries, 1,
                "a ball that cannot move and is still being pushed must be recovered");
            Assert.AreEqual(3, (int)Invoke("GetRecoveryState"),
                "three recoveries inside 10 s must stop and report recoveryState 3");
            // The hold must not keep teleporting: the ball stays where the last recovery
            // left it while the push continues.
            Vector3 held = GetCenter();
            Step(50);
            Assert.AreEqual(held.x, GetCenter().x, 0.02f, "the hold must not teleport the ball");
            Assert.AreEqual(held.z, GetCenter().z, 0.02f, "the hold must not teleport the ball");
            Assert.GreaterOrEqual((int)Invoke("GetLastRecoveryReason"), 1);
        }

        [Test]
        public void AWindowOverflowIsExplicitInsteadOfSilentlyDroppingSolids()
        {
            EnableV2();
            float radius = Radius();
            // Far more solids around the ball than the 24-entry near set can hold, all of
            // them inside the window radius.
            int registered = 0;
            for (int i = 0; i < 36; i++)
            {
                float angle = (float)i * 0.1745f;
                Vector3 center = new Vector3(Mathf.Cos(angle) * 0.3f, 0.2f,
                    Mathf.Sin(angle) * 0.3f);
                if (AddObb(center, new Vector3(0.15f, 0.15f, 0.15f), 1 + i)) registered++;
            }
            Assert.AreEqual(36, registered);
            Invoke("TeleportTo", new Vector3(0f, radius, 0f));
            Step(1);
            Assert.Greater((int)Invoke("GetNearObbCount"), 0, "the near set must not be empty");
            Assert.LessOrEqual((int)Invoke("GetNearObbCount"), 24);
            Assert.Greater((int)Invoke("GetNearObbDropped"), 0,
                "more candidates than capacity must be counted");
            Assert.IsTrue((bool)Invoke("WasContactIncomplete"),
                "dropped solids within reach must fail the step closed, not pass silently");
            Assert.Greater((int)Invoke("GetWindowOverflowCount"), 0);
        }

        [Test]
        public void TheShapeLayersKeepTheirOrderingAndThePickupReachNeverBlocks()
        {
            EnableV2();
            float physical = (float)Invoke("GetPhysicalRadiusWorldM");
            float visual = (float)Invoke("GetVisualCoreRadiusWorldM");
            float rolling = (float)Invoke("GetRollReachWorldM");
            float pickup = (float)Invoke("GetPickupReachWorldM");
            Assert.AreEqual(physical, Radius(), 1e-6f, "r_phys is the v1 collision radius");
            Assert.AreEqual(0.95f * physical, visual, 1e-4f, "r_visual = 0.95 r_phys");
            Assert.Less(visual, physical);
            Assert.GreaterOrEqual(rolling, physical - 1e-4f);
            Assert.GreaterOrEqual(pickup, rolling - 1e-4f,
                "the pickup trigger reaches at least as far as the rolling shape");
            float expectedCap = Mathf.Min(0.35f * physical, 0.3f);
            Assert.AreEqual(expectedCap, (float)Invoke("GetNeedleProtrusionCapWorldM"),
                1e-5f, "the pin cap is min(0.35 r, 0.30)");
        }

        private bool HasContactWithSurfaceIdAtLeast(int minimumSurfaceId)
        {
            int count = (int)Invoke("GetContactCount");
            for (int i = 0; i < count; i++)
            {
                if ((int)Invoke("GetContactSurfaceId", i) >= minimumSurfaceId) return true;
            }
            return false;
        }

        /// <summary>
        /// Registers one prop OBB (surface id 100, blocking role) and returns the result.
        /// The arrays are single-entry on purpose: SetPropObbs takes caller-owned parallel
        /// arrays, and the test keeps that contract visible.
        /// </summary>
        private bool SetOnePropObb(Vector3 center, Vector3 halfExtents, int surfaceId,
            int role)
        {
            Vector3[] centers = { center };
            Vector3[] axisX = { new Vector3(1f, 0f, 0f) };
            Vector3[] axisY = { new Vector3(0f, 1f, 0f) };
            Vector3[] axisZ = { new Vector3(0f, 0f, 1f) };
            Vector3[] half = { halfExtents };
            int[] ids = { surfaceId };
            int[] roles = { role };
            return (bool)Invoke("SetPropObbs", centers, axisX, axisY, axisZ, half, ids,
                roles, 1);
        }

        [Test]
        public void APropObbBlocksTheBallLikeAStaticOne()
        {
            EnableV2();
            float radius = Radius();
            // Teleport FIRST: a teleport clears the prop block (it was chosen for the old
            // position), exactly like the v1 blocking-box set.
            Invoke("TeleportTo", new Vector3(0f, radius, 0f));
            Assert.IsTrue(SetOnePropObb(new Vector3(0f, 1.5f, 2.5f),
                new Vector3(2f, 1.5f, 0.5f), 100, ContactCodes.RoleBlocking),
                "one prop OBB must fit in the twelve-entry block");
            Assert.AreEqual(1, (int)Invoke("GetPropObbsCount"));
            Invoke("SetMoveAxes", 1f, 0f, 0f);
            Step(150);
            Vector3 center = GetCenter();
            // The prop's near face is z = 2.0, so the centre may not come closer than r:
            // the prop goes through exactly the same contact solver as a static box.
            Assert.GreaterOrEqual(center.z, 2f - radius - 0.002f,
                "a prop OBB must not be penetrable either");
            Assert.IsTrue(HasContactWithSurfaceIdAtLeast(100),
                "the contact log must carry the prop surface id");
            bool sawBlockingRole = false;
            bool sawClimbClass = false;
            int count = (int)Invoke("GetContactCount");
            for (int i = 0; i < count; i++)
            {
                if ((int)Invoke("GetContactSurfaceId", i) < 100) continue;
                if ((int)Invoke("GetContactRole", i) == ContactCodes.RoleBlocking)
                {
                    sawBlockingRole = true;
                }
                // A vertical prop wall classifies exactly like a vertical static one: the
                // class comes from theta, so the prop gets no branch and no exception.
                if ((int)Invoke("GetContactClass", i) == ContactCodes.ClassClimb)
                {
                    sawClimbClass = true;
                }
            }
            Assert.IsTrue(sawBlockingRole,
                "the prop contact must keep the role the host registered");
            Assert.IsTrue(sawClimbClass,
                "a vertical prop wall is a climb-class contact, like a static one");
        }

        [Test]
        public void PropObbsShareTheNearWindowBudgetAndWinByDistance()
        {
            EnableV2();
            float radius = Radius();
            Invoke("TeleportTo", new Vector3(0f, radius, 0f));
            // Twenty-four statics just inside R_in but out of reach, so they are window
            // candidates without touching the ball.
            for (int i = 0; i < 24; i++)
            {
                float angle = (float)i * 0.2618f;
                AddObb(new Vector3(Mathf.Cos(angle) * 0.34f, 0.2f, Mathf.Sin(angle) * 0.34f),
                    new Vector3(0.1f, 0.1f, 0.1f), 1 + i);
            }
            // One prop, nearer than every static: the union is 25 candidates for 24 slots,
            // so the budget is shared and the nearest entry (the prop) must survive.
            Assert.IsTrue(SetOnePropObb(new Vector3(0f, 0.2f, 0.2f),
                new Vector3(0.1f, 0.1f, 0.1f), 100, ContactCodes.RoleProp),
                "the prop must register next to the static block");
            Assert.AreEqual(24, (int)Invoke("GetStaticObbCount"));
            Assert.AreEqual(1, (int)Invoke("GetPropObbsCount"));
            Step(1);
            Assert.AreEqual(24, (int)Invoke("GetNearObbCount"),
                "the near set must still hold 24 entries for the union, not 24 per table");
            Assert.Greater((int)Invoke("GetNearObbDropped"), 0,
                "25 candidates for 24 slots must be reported as a drop");
            Assert.IsTrue(HasContactWithSurfaceIdAtLeast(100),
                "the nearest candidate is the prop, so the drop must have taken a static");
        }

        [Test]
        public void APropBlockAboveTheCapacityIsRefusedAndCounted()
        {
            EnableV2();
            Vector3[] centers = new Vector3[13];
            Vector3[] axisX = new Vector3[13];
            Vector3[] axisY = new Vector3[13];
            Vector3[] axisZ = new Vector3[13];
            Vector3[] half = new Vector3[13];
            int[] ids = new int[13];
            int[] roles = new int[13];
            for (int i = 0; i < 13; i++)
            {
                centers[i] = new Vector3(i * 3f, 0f, 0f);
                axisX[i] = new Vector3(1f, 0f, 0f);
                axisY[i] = new Vector3(0f, 1f, 0f);
                axisZ[i] = new Vector3(0f, 0f, 1f);
                half[i] = new Vector3(0.5f, 0.5f, 0.5f);
                ids[i] = 100 + i;
                roles[i] = ContactCodes.RoleProp;
            }
            Assert.IsFalse((bool)Invoke("SetPropObbs", centers, axisX, axisY, axisZ, half,
                ids, roles, 13), "thirteen props must be refused, not truncated");
            Assert.AreEqual(0, (int)Invoke("GetPropObbsCount"), "a refused call changes nothing");
            Assert.AreEqual(1, (int)Invoke("GetPropObbsOverflowCount"));
            Assert.AreEqual(1, (int)Invoke("GetPropObbsRefusalCount"));
            Assert.AreNotEqual("", (string)Invoke("GetPropObbsRefusalReason"));
            Assert.AreEqual(12, (int)Invoke("GetPropObbsCapacity"));
            Assert.IsTrue((bool)Invoke("SetPropObbs", centers, axisX, axisY, axisZ, half,
                ids, roles, 12), "twelve props must fit");
            Assert.AreEqual(12, (int)Invoke("GetPropObbsCount"));
        }

        [Test]
        public void ClearObstaclesAndTeleportClearThePropBlock()
        {
            EnableV2();
            float radius = Radius();
            Assert.IsTrue(SetOnePropObb(new Vector3(0f, 0.5f, 3f),
                new Vector3(0.5f, 0.5f, 0.5f), 100, ContactCodes.RoleProp));
            Assert.IsTrue(AddBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 0f, 1f)));
            Invoke("ClearObstacles");
            Assert.AreEqual(0, (int)Invoke("GetPropObbsCount"),
                "ClearObstacles is 'all obstacles', so props go too");
            Assert.AreEqual(0, (int)Invoke("GetStaticObbCount"));
            Assert.IsTrue(SetOnePropObb(new Vector3(0f, 0.5f, 3f),
                new Vector3(0.5f, 0.5f, 0.5f), 100, ContactCodes.RoleProp));
            Invoke("TeleportTo", new Vector3(0f, radius, 0f));
            Assert.AreEqual(0, (int)Invoke("GetPropObbsCount"),
                "a teleport drops the prop set that was chosen for the old position");
        }

        [Test]
        public void PropObbsAreIgnoredWhileTheFlagIsOff()
        {
            float radius = Radius();
            Invoke("TeleportTo", new Vector3(0f, radius, 0f));
            Assert.IsTrue(SetOnePropObb(new Vector3(0f, 1.5f, 2.5f),
                new Vector3(2f, 1.5f, 0.5f), 100, ContactCodes.RoleBlocking),
                "registration is flag-independent, so the v1 frame can ignore it");
            Invoke("SetMoveAxes", 1f, 0f, 0f);
            Step(150);
            // v1 has no v2 table at all: the ball rolls straight through where the prop is.
            Assert.Greater(GetCenter().z, 3f,
                "the v1 path must keep using SetBlockingBoxes/SetSupportProps only");
            Assert.AreEqual(0, (int)Invoke("GetNearObbCount"));
            Assert.AreEqual(0, (int)Invoke("GetContactCount"));
        }

        [Test]
        public void ABallRestingOnABoxTopReportsTheTopAsTheSupportHeight()
        {
            // The pass6 field run failed the test field's rest_gap check on exactly the
            // cells where the ball had climbed onto a step: support_gap_m read -radius,
            // i.e. the support height was the CENTRE height. A resting ball must report
            // the surface it rests on, at every shipped diameter and step height.
            float[] tops = { 0.05f, 0.075f, 0.1f };
            for (int i = 0; i < tops.Length; i++)
            {
                float top = tops[i];
                RecreateMotor();
                EnableV2();
                float radius = Radius();
                AddObb(new Vector3(0f, top * 0.5f, 0f),
                    new Vector3(2f, top * 0.5f, 2f), 1);
                Invoke("TeleportTo", new Vector3(0f, top + radius + 0.3f, 0f));
                Step(60);
                Vector3 center = GetCenter();
                float height = (float)Invoke("GetSupportSurfaceHeightWorldM");
                Assert.AreEqual(top + radius, center.y, 0.01f,
                    "the ball must rest one radius above the top, top=" + top);
                Assert.AreEqual(top, height, 0.005f,
                    "the support height must be the top, not the centre, top=" + top);
                float gap = (center.y - radius) - height;
                Assert.AreEqual(0f, gap, 0.005f,
                    "support_gap must read 0 for a resting ball, top=" + top);
                Assert.AreEqual(1, (int)Invoke("GetSupportKind"),
                    "a box top is kind 1, top=" + top);
            }
        }

        [Test]
        public void ABallRestingOnTheGroundReportsAZeroGap()
        {
            EnableV2();
            float radius = Radius();
            Step(40);
            Vector3 center = GetCenter();
            Assert.AreEqual(radius, center.y, 0.005f);
            float height = (float)Invoke("GetSupportSurfaceHeightWorldM");
            Assert.AreEqual(0f, height, 0.002f, "the ground plane is at y = 0");
            Assert.AreEqual(0f, (center.y - radius) - height, 0.002f);
            Assert.AreEqual(0, (int)Invoke("GetSupportKind"));
        }

        [Test]
        public void ReleasingThePushEndsTheClimbInsteadOfHoveringOnTheWall()
        {
            // pass6 C09/C14: the ball reached a boundary plane at full speed, started to
            // climb it, the drive timeline released the input, and the ball then HUNG on
            // the wall 2-25 cm above the ground. Design §3.2/§3.3: the gate is a standing
            // condition, so releasing the push must leave the climb and let the ball slide
            // back down (v_slide_max), not freeze it mid-air.
            EnableV2();
            float radius = Radius();
            AddObb(new Vector3(0f, 1.5f, 2.5f), new Vector3(2f, 1.5f, 0.5f), 1);
            Invoke("TeleportTo", new Vector3(0f, radius, 0f));
            Invoke("SetMoveAxes", 1f, 0f, 0f);
            Step(80);
            Assert.AreEqual(1, (int)Invoke("GetClimbState"),
                "a full push into a wall must be climbing by now");
            Assert.Greater(GetCenter().y, radius + 0.01f,
                "the climb must have lifted the ball");
            // Release the push; the facing direction stays into the wall.
            Invoke("SetMoveAxes", 0f, 0f, 0f);
            Step(100);
            Vector3 center = GetCenter();
            Assert.IsFalse((bool)Invoke("IsClimbing"),
                "a closed gate must end the climb");
            Assert.AreNotEqual(1, (int)Invoke("GetClimbState"));
            Assert.AreEqual(radius, center.y, 0.02f,
                "the ball must slide back down to the ground instead of hovering");
            Assert.AreEqual(0, (int)Invoke("GetSupportKind"),
                "on the ground the support is the ground (kind 0)");
            float height = (float)Invoke("GetSupportSurfaceHeightWorldM");
            Assert.AreEqual(0f, height, 0.01f,
                "and the support height is the ground, not the wall point");
        }

        [Test]
        public void AnObbRegisteredBeforeStartSurvivesStart()
        {
            // Unity's Start order across GameObjects is arbitrary: a host that registers
            // its scene before this component's Start ran must not lose the geometry, and
            // Start must not re-allocate the table over it (pass6 read
            // GetStaticObbCount == 0 while [arena] reported obbs=1/1).
            GameObject earlyHost = new GameObject("ContactModelV2TestsPreStart");
            Component early = earlyHost.AddComponent(motorType);
            MethodInfo addObb = motorType.GetMethod("AddObb", Any);
            Assert.IsNotNull(addObb, "AddObb not found");
            bool accepted = (bool)addObb.Invoke(early, new object[]
            {
                new Vector3(0f, 0.25f, 0f), new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                new Vector3(1f, 0.25f, 1f), 1, ContactCodes.RoleBox,
            });
            int countBeforeStart = (int)motorType.GetField("staticObbCount", Any)
                .GetValue(early);
            MethodInfo start = motorType.GetMethod("Start", Any);
            Assert.IsNotNull(start, "Start not found");
            start.Invoke(early, null);
            int countAfterStart = (int)motorType.GetField("staticObbCount", Any)
                .GetValue(early);
            UnityEngine.Object.DestroyImmediate(earlyHost);
            Assert.IsTrue(accepted, "registration before Start must be accepted");
            Assert.AreEqual(1, countBeforeStart, "the table exists as soon as it is needed");
            Assert.AreEqual(1, countAfterStart,
                "Start must not wipe a table that already holds the scene's geometry");
        }

        [Test]
        public void TheClimbStateGetterSeparatesClimbingSlidingAndLocked()
        {
            EnableV2();
            Assert.AreEqual(0, (int)Invoke("GetClimbState"));
            SetField("climbActive", true);
            Assert.AreEqual(1, (int)Invoke("GetClimbState"));
            SetField("climbActive", false);
            SetField("climbCooldown", 0.2f);
            Assert.AreEqual(2, (int)Invoke("GetClimbState"));
            SetField("climbLocked", true);
            Assert.AreEqual(3, (int)Invoke("GetClimbState"));
            Assert.IsTrue((bool)Invoke("IsClimbLocked"));
            Assert.AreEqual(0f, GetFloat("climbHeightWorldM"), 1e-6f);
        }
    }
}
