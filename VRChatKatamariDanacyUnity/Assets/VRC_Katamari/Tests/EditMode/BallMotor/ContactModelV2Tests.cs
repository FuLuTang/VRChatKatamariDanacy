using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Motor-level checks for the ball contact solver (CONTACT_MODEL_V2.md §7.1 S3):
    /// the geometry registration, the OBB contact authority and the stuck-recovery state
    /// machine.
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

        /// <summary>An OBB whose local +Z is <paramref name="normal"/> (a tilted wall).</summary>
        private bool AddWall(Vector3 center, Vector3 normal, float halfWidth, float halfHeight,
            float halfDepth, int surfaceId)
        {
            Vector3 axisY = new Vector3(0f, 1f, 0f);
            Vector3 axisZ = normal.normalized;
            Vector3 axisX = Vector3.Cross(axisY, axisZ).normalized;
            return (bool)Invoke("AddObb", center, axisX, axisY, axisZ,
                new Vector3(halfWidth, halfHeight, halfDepth), surfaceId, 0);
        }

        [Test]
        public void AddBoxAlsoRegistersAnAxisAlignedObb()
        {
            Assert.IsTrue(AddBox(new Vector3(-2f, -1f, -2f), new Vector3(2f, 0f, 2f)),
                "the box registration must accept");
            Assert.AreEqual(1, (int)Invoke("GetStaticObbCount"),
                "the box is registered as an axis-aligned OBB");
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
        public void ARejectedPropGapStepClearsTheMotorsStoredVelocity()
        {
            float radius = Radius();
            float face = radius - 0.035f;
            Assert.IsTrue(AddObb(new Vector3(0f, 1f, -face - 0.04f),
                new Vector3(10f, 1f, 0.04f), 110));
            Assert.IsTrue(AddObb(new Vector3(0f, 1f, face + 0.04f),
                new Vector3(10f, 1f, 0.04f), 111));
            Invoke("TeleportTo", new Vector3(0f, radius, 0f));
            SetField("headingDegrees", 90f);
            Invoke("SetMoveAxes", 1f, 0f, 0f);
            Vector3 start = GetCenter();
            for (int i = 0; i < 50; i++)
            {
                Step(1);
                Assert.Greater((float)Invoke("GetSolverResidualMax"), 0.02f);
                Assert.AreEqual(start, GetCenter());
                Assert.LessOrEqual(((Vector3)Invoke("GetPlanarVelocity")).magnitude, 1e-6f);
                Assert.AreEqual(0f, GetFloat("contactVerticalVelocity"));
            }
            Assert.Greater((int)Invoke("GetContactRejectedStepCount"), 0);
            Invoke("ClearObbs");
            Invoke("SetMoveAxes", 0f, 0f, 0f);
            Step(1);
            Assert.AreEqual(start.x, GetCenter().x, 1e-6f);
            Assert.AreEqual(0f, ((Vector3)Invoke("GetPlanarVelocity")).magnitude, 1e-6f);
        }

        /// <summary>
        /// A slab whose top face is the plane through <see cref="SlopeOrigin"/>, tilted
        /// <paramref name="degrees"/> so its normal leans toward +X (the downhill side).
        /// It sits 5 m up: the arena's flat ground plane (y = 0) is a contact source too, and
        /// a slab that crossed it would let the ground, not the slope, support the ball.
        /// Returns the unit normal.
        /// </summary>
        private Vector3 AddSlope(float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            Vector3 normal = new Vector3(Mathf.Sin(radians), Mathf.Cos(radians), 0f);
            Vector3 axisX = new Vector3(Mathf.Cos(radians), -Mathf.Sin(radians), 0f);
            Vector3 axisZ = new Vector3(0f, 0f, 1f);
            Assert.IsTrue((bool)Invoke("AddObb", SlopeOrigin - normal * 0.2f, axisX, normal,
                axisZ, new Vector3(10f, 0.2f, 10f), 1, 1));
            return normal;
        }

        private static readonly Vector3 SlopeOrigin = new Vector3(0f, 5f, 0f);

        private bool GetBool(string name)
        {
            FieldInfo field = motorType.GetField(name, Any);
            Assert.IsNotNull(field, name + " field not found");
            return (bool)field.GetValue(motor);
        }

        private Vector3 GetVector(string name)
        {
            FieldInfo field = motorType.GetField(name, Any);
            Assert.IsNotNull(field, name + " field not found");
            return (Vector3)field.GetValue(motor);
        }

        /// <summary>Every state the rest-then-slide gates read, for an assertion message.</summary>
        private string SlideDiagnostics(int steps, string history)
        {
            Vector3 normal = GetVector("supportSurfaceNormalWorld");
            float slope = Mathf.Acos(Mathf.Clamp(normal.y, -1f, 1f)) * Mathf.Rad2Deg;
            Vector3 velocity = (Vector3)Invoke("GetPlanarVelocity");
            Vector3 center = GetCenter();
            return "dt=0.02 steps=" + steps + " t=" + (steps * 0.02f)
                + " restTimer=" + GetFloat("rampRestTimerSeconds")
                + " sliding=" + GetBool("rampSliding")
                + " prevRejected=" + GetBool("previousStepRejected")
                + " supportKind=" + Invoke("GetSupportKind")
                + " supportId=" + Invoke("GetSupportSurfaceId")
                + " falling=" + Invoke("IsFalling")
                + " fromNeedle=" + GetBool("supportFromNeedle")
                + " fromClimb=" + GetBool("supportFromClimb")
                + " normal=" + normal.ToString("F4") + " slopeDeg=" + slope
                + " planarSpeed=" + Mathf.Sqrt(velocity.x * velocity.x + velocity.z * velocity.z)
                + " center=" + center.ToString("F4")
                + " scale=" + GetFloat("rampTangentialGravityScale")
                + " minSlopeDeg=" + GetFloat("rampSlideMinSlopeDegrees")
                + " maxSlopeDeg=" + GetFloat("maxSupportSlopeDegrees")
                + " restSpeed=" + GetFloat("rampRestSpeedMps")
                + " restSeconds=" + GetFloat("rampRestSeconds")
                + " history(per step: S=sliding F=falling .=neither)=" + history;
        }

        [Test]
        public void ABallAtRestOnATenDegreeSlopeStartsSlidingAfterTheHold()
        {
            Vector3 normal = AddSlope(10f);
            Invoke("TeleportTo", SlopeOrigin + normal * Radius());
            Step(5);
            Vector3 settled = GetCenter();
            Assert.IsFalse(GetBool("rampSliding"), "not before the hold: "
                + SlideDiagnostics(5, ""));

            Step(10);
            Assert.Less(Mathf.Abs(GetCenter().x - settled.x), 0.005f,
                "0.2 s of rest: the ball has not moved downhill yet: " + SlideDiagnostics(15, ""));

            string history = "";
            for (int i = 0; i < 40; i++)
            {
                Step(1);
                bool sliding = GetBool("rampSliding");
                history += sliding ? "S" : (bool)Invoke("IsFalling") ? "F" : ".";
            }
            int firstSlide = history.IndexOf('S');
            Assert.GreaterOrEqual(firstSlide, 0, "0.3 s of rest on a slope opens the latch: "
                + SlideDiagnostics(55, history));
            Assert.AreEqual(-1, history.Substring(firstSlide).IndexOfAny(new[] { '.', 'F' }),
                "once open, the latch stays open and the ball never loses its support: "
                + SlideDiagnostics(55, history));
            Vector3 velocity = (Vector3)Invoke("GetPlanarVelocity");
            Assert.Greater(Mathf.Sqrt(velocity.x * velocity.x + velocity.z * velocity.z), 0.2f,
                "and the ball keeps accelerating downhill: " + SlideDiagnostics(55, history));
            Assert.Greater(GetCenter().x - settled.x, 0.1f, "toward +X: "
                + SlideDiagnostics(55, history));
        }

        [Test]
        public void ABallAtRestOnFlatGroundNeverSlides()
        {
            Step(5);
            Vector3 settled = GetCenter();
            Step(60);
            Assert.IsFalse(GetBool("rampSliding"));
            Assert.Less(Mathf.Abs(GetCenter().x - settled.x), 0.002f);
            Assert.Less(Mathf.Abs(GetCenter().z - settled.z), 0.002f);
        }

        [Test]
        public void APushOnASlopeAddsNoDownhillComponent()
        {
            Vector3 normal = AddSlope(10f);
            Invoke("TeleportTo", SlopeOrigin + normal * Radius());
            SetField("debugForward", 1f);
            Step(60);
            Assert.IsFalse(GetBool("rampSliding"), "a push keeps the latch shut");
            Assert.AreEqual(0f, GetFloat("rampRestTimerSeconds"), 1e-6f, "and the rest timer at 0");
        }

        [Test]
        public void TheStaticObbTableTakesFarMoreThanSixtyFourSolids()
        {
            for (int i = 0; i < 40; i++)
            {
                AddObb(new Vector3(100f + i * 3f, 0f, 0f), new Vector3(1f, 1f, 1f), 1 + i);
            }
            Assert.AreEqual(40, (int)Invoke("GetStaticObbCount"),
                "the table is 256, not 64");
            Invoke("ClearObbs");
            Assert.AreEqual(0, (int)Invoke("GetStaticObbCount"));
        }

        [Test]
        public void TheBallLandsOnAnObbTopAtOneRadiusAboveIt()
        {
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
        public void ABallPushedIntoAWallNeverPenetratesIt()
        {
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
        public void APushIntoATallWallIsBlockedNotStuckAndStaysControllable()
        {
            // A clean stop is blocked, not stuck: pressing a wall is not a stuck episode, so
            // the recovery must NOT fire, clear the climb state or freeze the ball (the 2026-11-16 RefCancer pit).
            float radius = Radius();
            AddObb(new Vector3(0f, 1.5f, 3.4f), new Vector3(2f, 1.5f, 0.4f), 1);
            // Short run-up and half push: the contact speed (sqrt(2*5*0.1) = 1.0 m/s) and
            // v_gate = max(v, k_intent * 0.5 * maxSpeed = 1.0) stay under v_req(90) = 1.5, so
            // the climb never opens and the ball is simply pressed against the wall.
            Invoke("TeleportTo", new Vector3(0f, radius, 3f - radius - 0.1f));
            Invoke("SetMoveAxes", 0.5f, 0f, 0f);
            Step(300);
            Assert.AreEqual(0, (int)Invoke("GetRecoveryCount"),
                "pushing a wall is blocked, not stuck: no recovery episode may fire");
            Assert.AreNotEqual(3, (int)Invoke("GetRecoveryState"),
                "the recovery hold must never engage for a wall press");
            Vector3 pressed = GetCenter();
            Assert.GreaterOrEqual(pressed.z, 3f - radius - 0.002f,
                "the wall push-out must keep the sphere out of the wall");
            Assert.IsFalse(float.IsNaN(pressed.x) || float.IsNaN(pressed.y) ||
                float.IsNaN(pressed.z), "the pressed state must stay finite");

            // The real point of the recovery net is that the ball is still CONTROLLED:
            // reversing the push walks it away from the wall instead of leaving it frozen.
            Invoke("SetMoveAxes", -1f, 0f, 0f);
            Step(60);
            Vector3 after = GetCenter();
            Assert.LessOrEqual(after.z, pressed.z - 0.2f,
                "reversing the push must move the ball at least 0.2 m off the wall");
            Assert.LessOrEqual((int)Invoke("GetRecoveryCount"), 0,
                "leaving a wall is not a recovery episode either");
        }

        [Test]
        public void ANarrowFiftyDegreeWedgeIsStuckNotBlockedAndEndsInTheRecoveryHold()
        {
            // The recovery net's real job: no progress while no contact opposes the PUSH.
            // Two faces whose normals are 65 deg either side of -Z leave a 50 deg V (inner
            // angle beta = 180 - 130). Pushing along +Z, the bisector, gives each face a
            // dot of -sin(beta / 2) = -sin(25 deg) = -0.42: not below the -0.5 gate, so the
            // press is stuck, not blocked, and the recovery must fire and end in the hold
            // (design 2.8). The gate reads the player's intent, not the velocity: the
            // solver strips each face's normal component in turn, so the velocity swings
            // to +-0.35 of +Z every few steps and would read as pushing into one face.
            float radius = Radius();
            float angle = 65f * Mathf.Deg2Rad;
            Vector3 normalA = new Vector3(Mathf.Sin(angle), 0f, -Mathf.Cos(angle));
            Vector3 normalB = new Vector3(-Mathf.Sin(angle), 0f, -Mathf.Cos(angle));
            Vector3 apex = new Vector3(0f, 1.5f, 3f);
            Assert.IsTrue(AddWall(apex - normalA * 0.5f, normalA, 3f, 1.5f, 0.5f, 2));
            Assert.IsTrue(AddWall(apex - normalB * 0.5f, normalB, 3f, 1.5f, 0.5f, 3));
            Invoke("TeleportTo", new Vector3(0f, radius, 2.6f));
            Invoke("SetMoveAxes", 0.5f, 0f, 0f);

            // Steps to the hold, from the recovery constants (dt = 0.02 s):
            //   ~0.2 s  the ball rolls the 0.045 m into the apex (half push, 2.5 m/s^2);
            //   +3.0 s  stuck > ContactStuckHardSeconds. On the way the soft stage fires
            //           once at 1.5 s (count 1); the hard stage then teleports to the stable
            //           point (count 2) and zeroes the timer;
            //   +3.0 s  the second episode starts at once (the stable point is the apex),
            //           the soft stage is not re-armed (only a step that leaves the
            //           ContactStuckConfineM disc or releases the push re-arms it), so it
            //           runs to the next hard stage (count 3 = ContactRecoveryHoldCount).
            //   = 0.2 + 3.0 + 3.0 = 6.2 s = 310 steps, plus one step per strict '>' check.
            // 330 steps (6.6 s) keeps 0.4 s of slack and stays inside the 10 s hold window.
            Step(330);
            Assert.GreaterOrEqual((int)Invoke("GetRecoveryCount"), 1,
                "a wedge that no contact opposes must be recovered");
            Assert.AreEqual(3, (int)Invoke("GetRecoveryState"),
                "three episodes inside 10 s must end in the hold");
        }

        [Test]
        public void APushIntoARightAngleCornerIsBlockedNotStuck()
        {
            // The same -sin(beta / 2) rule on the other side of the gate: a 90 deg inner
            // corner pushed along its bisector gives each face -sin(45 deg) = -0.71, so it
            // is blocked and the ball stays in control (TERRAIN_CLIMB 6.3: beta > 60 deg).
            float radius = Radius();
            // Faces z = 3 (normal -Z) and x = 1 (normal -X); the corner is (1, 3).
            AddObb(new Vector3(0f, 1.5f, 3.4f), new Vector3(4f, 1.5f, 0.4f), 2);
            AddObb(new Vector3(1.4f, 1.5f, 0f), new Vector3(0.4f, 1.5f, 4f), 3);
            Invoke("TeleportTo", new Vector3(1f - radius - 0.05f, radius, 3f - radius - 0.05f));
            SetField("headingDegrees", 45f);
            Invoke("SetMoveAxes", 0.5f, 0f, 0f);
            Step(400);
            Assert.AreEqual(0, (int)Invoke("GetRecoveryCount"),
                "a corner the push runs into is blocked, not stuck");
            Assert.AreNotEqual(3, (int)Invoke("GetRecoveryState"));
            Vector3 pressed = GetCenter();
            Assert.Greater((int)Invoke("GetContactCount"), 0, "the ball must be touching");
            Assert.LessOrEqual(pressed.x, 1f - radius + 0.002f, "no penetration of the x face");
            Assert.LessOrEqual(pressed.z, 3f - radius + 0.002f, "no penetration of the z face");

            // Still controlled: turning round and pushing walks the ball out of the corner.
            SetField("headingDegrees", 225f);
            Step(60);
            Vector3 after = GetCenter();
            float away = Mathf.Sqrt((after.x - pressed.x) * (after.x - pressed.x)
                + (after.z - pressed.z) * (after.z - pressed.z));
            Assert.GreaterOrEqual(away, 0.2f, "the ball must leave the corner when pushed out");
            Assert.AreEqual(0, (int)Invoke("GetRecoveryCount"));
        }

        [Test]
        public void SlidingAlongAWallIsNeitherBlockedStuckNorFrozenAtAnyIncidence()
        {
            // The ball is driven along a long wall at 45 deg (intent dot with the normal
            // -0.71, blocked) and at 75 deg (-0.26, not blocked). Neither may count a
            // stuck episode, and both keep moving along the wall: the stuck test is "stayed
            // inside a 1 cm disc", which a ball sliding at 1-2 m/s never does.
            float[] headings = { 45f, 75f };
            for (int i = 0; i < headings.Length; i++)
            {
                RecreateMotor();
                float radius = Radius();
                AddObb(new Vector3(0f, 1.5f, 3.4f), new Vector3(8f, 1.5f, 0.4f), 1);
                Invoke("TeleportTo", new Vector3(-4f, radius, 3f - radius - 0.05f));
                SetField("headingDegrees", headings[i]);
                Invoke("SetMoveAxes", 0.5f, 0f, 0f);
                Step(150);
                Vector3 center = GetCenter();
                Assert.GreaterOrEqual(center.x, -2f,
                    "the ball must have slid at least 2 m along the wall, heading=" + headings[i]);
                Assert.LessOrEqual(center.z, 3f - radius + 0.002f,
                    "no penetration, heading=" + headings[i]);
                Assert.AreEqual(0, (int)Invoke("GetRecoveryCount"),
                    "a slide is not a stuck episode, heading=" + headings[i]);
            }
        }

        [Test]
        public void AHeldFullPushIntoAFiftyDegreeWedgeNeverClimbsPastTheCap()
        {
            // TERRAIN_CLIMB 6.6. The same 50 deg V as the recovery test above, pushed at full
            // stick for 40 s from a few facings. The climb opens, reaches H_cap, peels, falls
            // and retries; the face the velocity points at flips on the way, and each flip
            // used to re-anchor the cap at the current height (field peak 0.69 m against the
            // 0.60 m budget, 1.0 m from some facings). The budget belongs to the whole
            // wall-hugging stretch: rest + H_cap + the 5 mm contact skin, at every facing.
            float[] headings = { 0f, 3f, 10f };
            for (int i = 0; i < headings.Length; i++)
            {
                RecreateMotor();
                float radius = Radius();
                float cap = ClimbMath.ClimbHeightCap(radius, 0f);
                float angle = 65f * Mathf.Deg2Rad;
                Vector3 normalA = new Vector3(Mathf.Sin(angle), 0f, -Mathf.Cos(angle));
                Vector3 normalB = new Vector3(-Mathf.Sin(angle), 0f, -Mathf.Cos(angle));
                Vector3 apex = new Vector3(0f, 1.5f, 3f);
                Assert.IsTrue(AddWall(apex - normalA * 0.5f, normalA, 3f, 1.5f, 0.5f, 2));
                Assert.IsTrue(AddWall(apex - normalB * 0.5f, normalB, 3f, 1.5f, 0.5f, 3));
                Invoke("TeleportTo", new Vector3(0f, radius, 2.6f));
                SetField("headingDegrees", headings[i]);
                Invoke("SetMoveAxes", 1f, 0f, 0f);
                float highest = 0f;
                bool climbed = false;
                for (int step = 0; step < 2000; step++)
                {
                    Step(1);
                    float y = GetCenter().y;
                    if (y > highest) highest = y;
                    if ((int)Invoke("GetClimbState") == 1) climbed = true;
                }
                Assert.IsTrue(climbed, "the full push must open a climb, heading=" + headings[i]);
                Assert.LessOrEqual(highest, radius + cap + 0.005f,
                    "the centre peaked at " + highest + " against rest + H_cap = "
                    + (radius + cap) + ", heading=" + headings[i]);
                Assert.IsFalse(float.IsNaN(GetCenter().y), "the state stays finite");
            }
        }

        private Vector3[] ContactNormals()
        {
            return (Vector3[])motorType.GetField("contactNormalsV2", Any).GetValue(motor);
        }

        /// <summary>
        /// Writes a contact log of one core contact (index 0, which the needle response
        /// must never touch) and one needle contact (index 1), with the given velocity.
        /// </summary>
        private void SetNeedleLog(Vector3 needleNormal, float needleDepth, int needleClass,
            Vector3 planar, float vertical)
        {
            Vector3[] normals = ContactNormals();
            float[] depths = (float[])motorType.GetField("contactDepthsV2", Any).GetValue(motor);
            int[] classes = (int[])motorType.GetField("contactClassesV2", Any).GetValue(motor);
            normals[0] = new Vector3(0f, 0f, -1f);
            depths[0] = 0.01f;
            classes[0] = ContactCodes.ClassClimb;
            normals[1] = needleNormal;
            depths[1] = needleDepth;
            classes[1] = needleClass;
            SetField("v2ContactCount", 2);
            SetField("planarVelocity", planar);
            SetField("contactVerticalVelocity", vertical);
        }

        [Test]
        public void TheNeedleVelocityResponseFollowsTheSolversThreeClassRules()
        {
            // CONTACT_MODEL_V2 4A.3 / 7.5: a pin contact removes velocity by class, exactly
            // as the core solver does, and never adds any (no restitution, no rebound).
            Vector3 moving = new Vector3(0.3f, 0f, 2f);

            // CLIMB: the horizontal inward component goes, the tangential one and the
            // vertical velocity stay.
            SetNeedleLog(new Vector3(0f, 0f, -1f), 0.001f, ContactCodes.ClassClimb, moving, -1f);
            Invoke("ApplyNeedleVelocityResponse", 1);
            Assert.AreEqual(0.3f, ((Vector3)Invoke("GetPlanarVelocity")).x, 1e-5f);
            Assert.AreEqual(0f, ((Vector3)Invoke("GetPlanarVelocity")).z, 1e-5f,
                "the inward component of a wall contact is removed");
            Assert.AreEqual(-1f, GetFloat("contactVerticalVelocity"), 1e-5f);

            // CLIMB, velocity already leaving the surface: untouched (no push away).
            SetNeedleLog(new Vector3(0f, 0f, -1f), 0.001f, ContactCodes.ClassClimb,
                new Vector3(0.3f, 0f, -1f), 0f);
            Invoke("ApplyNeedleVelocityResponse", 1);
            Assert.AreEqual(-1f, ((Vector3)Invoke("GetPlanarVelocity")).z, 1e-5f,
                "an outward velocity is not touched: the response never rebounds");

            // RIDE (a pin standing on a floor): removes nothing.
            SetNeedleLog(new Vector3(0f, 1f, 0f), 0.001f, ContactCodes.ClassRide, moving, -1f);
            Invoke("ApplyNeedleVelocityResponse", 1);
            Assert.AreEqual(2f, ((Vector3)Invoke("GetPlanarVelocity")).z, 1e-5f);
            Assert.AreEqual(-1f, GetFloat("contactVerticalVelocity"), 1e-5f);

            // NONE (an underside / ceiling): removes the inward part in all three axes.
            SetNeedleLog(new Vector3(0f, -1f, 0f), 0.001f, ContactCodes.ClassNone, moving, 2f);
            Invoke("ApplyNeedleVelocityResponse", 1);
            Assert.AreEqual(0f, GetFloat("contactVerticalVelocity"), 1e-5f,
                "an upward velocity into a ceiling is removed");
            Assert.AreEqual(2f, ((Vector3)Invoke("GetPlanarVelocity")).z, 1e-5f,
                "the horizontal velocity is not the ceiling's business");

            // A contact with no depth (a first touch of the sweep) removes nothing, and the
            // core contact at index 0 is never read by this pass.
            SetNeedleLog(new Vector3(0f, 0f, -1f), 0f, ContactCodes.ClassClimb, moving, 0f);
            Invoke("ApplyNeedleVelocityResponse", 1);
            Assert.AreEqual(2f, ((Vector3)Invoke("GetPlanarVelocity")).z, 1e-5f,
                "depth <= 0 is not a response, and the core contact (index 0) is skipped");
        }

        [Test]
        public void APinThatTouchesTheWallBeforeTheCoreStopsTheBallThereWithoutRebound()
        {
            // A long item stuck to the ball is a pin (a "needle") that reaches r + cap, up
            // to 35% of a radius beyond the core. Pushed into a wall, the pin touches
            // first: the ball must stop with the pin tip at the wall (not with the core
            // there, and not with the tip inside the wall), keep no speed into the wall,
            // not bounce, not climb, and leave at once when the push reverses.
            float radius = Radius();
            float tipReach = radius + Mathf.Min(0.35f * radius, 0.3f);
            // The face z = 3 (normal -Z).
            AddObb(new Vector3(0f, 1.5f, 3.4f), new Vector3(2f, 1.5f, 0.4f), 1);
            Invoke("TeleportTo", new Vector3(0f, radius,
                3f - tipReach - ContactTuning.NeedleTipRadiusM - 0.02f));

            // The shell has 16 fixed pin directions; the rod is laid along the one nearest
            // +Z and the ball is rolled so that pin points at the wall (otherwise whether a
            // pin or the core touches first would depend on the roll phase).
            Vector3[] directions = (Vector3[])motorType.GetField("proxyDirections", Any)
                .GetValue(motor);
            Vector3 pin = directions[0];
            for (int i = 1; i < directions.Length; i++)
            {
                if (directions[i].z > pin.z) pin = directions[i];
            }
            Assert.IsTrue((bool)Invoke("AttachRadialProxy", "rod", new Vector3(0.02f, 0.02f, 0.8f),
                Vector3.zero, Quaternion.FromToRotation(Vector3.forward, pin)),
                "a 0.8 m rod clears the core and must register");
            SetField("visualRollRotation", Quaternion.FromToRotation(pin, Vector3.forward));
            Invoke("SetMoveAxes", 1f, 0f, 0f);

            bool sawNeedleContact = false;
            float slowestZ = 0f;
            for (int i = 0; i < 100; i++)
            {
                Invoke("StepBall", 0.02f);
                if ((int)Invoke("GetNeedleContactCount") > 0) sawNeedleContact = true;
                slowestZ = Mathf.Min(slowestZ, ((Vector3)Invoke("GetPlanarVelocity")).z);
            }
            Vector3 pressed = GetCenter();
            Quaternion roll = (Quaternion)motorType.GetField("visualRollRotation", Any)
                .GetValue(motor);
            Vector3 tip = pressed + roll * pin * tipReach;
            Assert.IsTrue(sawNeedleContact, "the pin, not only the core, must have touched");
            Assert.LessOrEqual(pressed.z, 3f - radius - 0.02f,
                "the ball must stop at least 2 cm before where the bare core would (cap is 5 cm)");
            Assert.LessOrEqual(tip.z, 3f, "the pin tip must not be inside the wall");
            Assert.LessOrEqual(pressed.y, radius + 0.01f, "the ball must not climb the wall");
            Assert.LessOrEqual(((Vector3)Invoke("GetPlanarVelocity")).z, 0.2f,
                "after 2 s of pushing no speed into the wall may be left");
            Assert.GreaterOrEqual(slowestZ, -0.1f, "the response removes velocity, never reverses it");
            Assert.AreEqual(0, (int)Invoke("GetRecoveryCount"), "a pinned press is blocked, not stuck");

            // Releasing holds the ball where it is, reversing walks it off at once.
            Invoke("SetMoveAxes", 0f, 0f, 0f);
            Step(30);
            Assert.AreEqual(pressed.z, GetCenter().z, 0.01f, "released: no drift into the wall");
            Invoke("SetMoveAxes", -1f, 0f, 0f);
            Step(40);
            Assert.LessOrEqual(GetCenter().z, pressed.z - 0.2f,
                "reversed: the ball must leave the wall without having to cancel a stored speed");
        }

        [Test]
        public void AWindowOverflowIsExplicitInsteadOfSilentlyDroppingSolids()
        {
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
            float physical = (float)Invoke("GetPhysicalRadiusWorldM");
            float visual = (float)Invoke("GetVisualCoreRadiusWorldM");
            float rolling = (float)Invoke("GetRollReachWorldM");
            float pickup = (float)Invoke("GetPickupReachWorldM");
            Assert.AreEqual(physical, Radius(), 1e-6f, "r_phys is the collision radius");
            Assert.AreEqual(physical, visual, 1e-6f,
                "the visual core fills the current collision sphere");
            Assert.GreaterOrEqual(rolling, physical - 1e-4f);
            Assert.AreEqual(physical, pickup, 1e-6f,
                "pickup uses the collision sphere, never a cube support radius or pins");
            float expectedCap = Mathf.Min(0.35f * physical, 0.3f);
            Assert.AreEqual(expectedCap, (float)Invoke("GetNeedleProtrusionCapWorldM"),
                1e-5f, "the pin cap is min(0.35 r, 0.30)");
        }

        [Test]
        public void EveryAttachedPropRegistersIntoThePinsNotOnlyLongOnes()
        {
            // A 10 cm cube sitting on the +Z surface of the start ball (r = 0.15): not a long
            // object, and it used to be skipped. Its far corners clear the core, so it must
            // register and lengthen the pins that face it.
            float radius = Radius();
            Assert.AreEqual(0, (int)Invoke("GetProxyItemCount"));
            Assert.IsTrue((bool)Invoke("AttachRadialProxy", "cube#1", new Vector3(0.1f, 0.1f, 0.1f),
                new Vector3(0f, 0f, radius), Quaternion.identity),
                "a small prop whose box clears the core registers");
            Assert.AreEqual(1, (int)Invoke("GetProxyItemCount"));
            Assert.AreEqual(8, (int)Invoke("GetProxyPointCount"), "one prop = its 8 corners");
            Assert.Greater((int)Invoke("GetProxyProtrudingPinCount"), 0);
            // Idempotent by id.
            Assert.IsTrue((bool)Invoke("AttachRadialProxy", "cube#1", new Vector3(0.1f, 0.1f, 0.1f),
                new Vector3(0f, 0f, radius), Quaternion.identity));
            Assert.AreEqual(1, (int)Invoke("GetProxyItemCount"));

            // A prop buried in the core cannot lengthen any pin: skipped, not counted as a refusal.
            Assert.IsFalse((bool)Invoke("AttachRadialProxy", "pebble#1",
                new Vector3(0.02f, 0.02f, 0.02f), new Vector3(0f, 0f, 0.05f), Quaternion.identity));
            Assert.AreEqual(1, (int)Invoke("GetProxyItemCount"));
            Assert.AreEqual(0, (int)Invoke("GetProxyRefusalCount"));

            // The ledger takes far more than the old four long objects.
            for (int i = 0; i < 100; i++)
            {
                Assert.IsTrue((bool)Invoke("AttachRadialProxy", "cube#" + (i + 2),
                    new Vector3(0.1f, 0.1f, 0.1f), new Vector3(0f, radius, 0f),
                    Quaternion.identity));
            }
            Assert.AreEqual(101, (int)Invoke("GetProxyItemCount"));

            // A field reset empties the shell.
            Invoke("ClearRadialProxies");
            Assert.AreEqual(0, (int)Invoke("GetProxyItemCount"));
            Assert.AreEqual(0, (int)Invoke("GetProxyProtrudingPinCount"));
        }

        [TestCase(0.222f)]
        [TestCase(0.3f)]
        public void TheVisualCoreFollowsCollisionWhileTheStartDiameterStaysFixed(float startDiameterM)
        {
            // A level that sets its start diameter (Cancer: 0.222 m).
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
            host = new GameObject("ContactModelV2Tests");
            motor = host.AddComponent(motorType);
            SetField("startDiameterWorldM", startDiameterM);
            Invoke("Start");

            float start = (float)Invoke("GetStartDiameterWorldM");
            Assert.AreEqual((float)Invoke("GetSemanticDiameterWorldM"), start, 1e-6f,
                "the start diameter is the size model's diameter at zero confirmed volume");
            Assert.AreEqual(startDiameterM, start, 1e-6f);
            Assert.AreEqual(Radius(), (float)Invoke("GetVisualCoreRadiusWorldM"), 1e-6f);

            // Successful absorption publishes confirmed volume, then re-derives size.
            SetField("confirmedVolumeMm3", 1000000L);
            Invoke("DeriveSize");
            Assert.AreEqual(start, (float)Invoke("GetStartDiameterWorldM"), 0f);
            Assert.Greater(Radius(), 0.5f * start);
            Assert.AreEqual(Radius(), (float)Invoke("GetVisualCoreRadiusWorldM"), 0f);
            Assert.AreEqual(Radius(), (float)Invoke("GetPhysicalRadiusWorldM"), 0f);
        }

        [Test]
        public void TheStartDiameterIsZeroBeforeStartSoTheViewDrawsNothingYet()
        {
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
            host = new GameObject("ContactModelV2Tests");
            motor = host.AddComponent(motorType);
            Assert.AreEqual(0f, (float)Invoke("GetStartDiameterWorldM"));
            Assert.AreEqual(0f, (float)Invoke("GetVisualCoreRadiusWorldM"));
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
            float radius = Radius();
            // Teleport FIRST: a teleport clears the prop block (it was chosen for the old
            // position).
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
        public void ARollingBallIsNotLiftedAboveAThinPlate()
        {
            // C14's remaining red (pass8): lift_over_nominal read 0.019..0.213, i.e.
            // 0.06-0.12 x D. The trajectory shows the lift happens at the FAR end of the
            // cell, where the ball reaches the arena's boundary plane at full push and
            // climbs it for the last 0.2-0.3 s of the drive; the climb rate scales with
            // H_cap, i.e. with D. Over the plate itself the bottom never rises above the
            // plate top: rolling over a step of height h can lift the bottom to h and no
            // further, at any diameter.
            //
            // This test measures the lift the way a gated criterion should: only while
            // the ball is over the plate (its footprint plus one radius).
            float radius = Radius();
            const float top = 0.028f;
            AddObb(new Vector3(0f, top * 0.5f, 0f), new Vector3(2f, top * 0.5f, 1f), 1);
            Invoke("TeleportTo", new Vector3(0f, radius, -3f));
            Invoke("SetMoveAxes", 1f, 0f, 0f);
            float maxLift = -1f;
            bool crossed = false;
            for (int i = 0; i < 150; i++)
            {
                Invoke("StepBall", 0.02f);
                Vector3 center = GetCenter();
                if (center.z > 1f + radius) crossed = true;
                if (center.z < -1f - radius || center.z > 1f + radius) continue;
                float lift = (center.y - radius) - top;
                if (lift > maxLift) maxLift = lift;
            }
            Assert.IsTrue(crossed, "the ball must actually roll over the plate");
            Assert.LessOrEqual(maxLift, 0.01f,
                "over the plate the bottom may not rise above the plate top by more than a centimetre");
            // And it is back on the ground on the far side (the plate is not a ramp).
            Assert.AreEqual(radius, GetCenter().y, 0.01f,
                "the ball rolls off the plate and back onto the ground");
            Assert.AreEqual(0, (int)Invoke("GetSupportKind"));
        }

        [Test]
        public void ABallRestingOnTheGroundReportsAZeroGap()
        {
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
            bool accepted;
            int countBeforeStart;
            int countAfterStart;
            try
            {
                Component early = earlyHost.AddComponent(motorType);
                MethodInfo addObb = motorType.GetMethod("AddObb", Any);
                Assert.IsNotNull(addObb, "AddObb not found");
                accepted = (bool)addObb.Invoke(early, new object[]
                {
                    new Vector3(0f, 0.25f, 0f), new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                    new Vector3(1f, 0.25f, 1f), 1, ContactCodes.RoleBox,
                });
                countBeforeStart = (int)motorType.GetField("staticObbCount", Any)
                    .GetValue(early);
                MethodInfo start = motorType.GetMethod("Start", Any);
                Assert.IsNotNull(start, "Start not found");
                start.Invoke(early, null);
                countAfterStart = (int)motorType.GetField("staticObbCount", Any)
                    .GetValue(early);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(earlyHost);
            }

            Assert.IsTrue(accepted, "registration before Start must be accepted");
            Assert.AreEqual(1, countBeforeStart, "the table exists as soon as it is needed");
            Assert.AreEqual(1, countAfterStart,
                "Start must not wipe a table that already holds the scene's geometry");
        }

        [Test]
        public void TheClimbStateGetterSeparatesClimbingSlidingAndLocked()
        {
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
