using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// The static box table at its new capacity: 64 (was 8). The 64th registration is
    /// accepted, the 65th is an EXPLICIT refusal that latches the overflow signal, and a
    /// single box near the ball still wins among 64 - the far ones must not change the
    /// answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why 64: the v2 format's own budget is <c>budget.boxes = 64</c> / <c>caps.staticSolids
    /// = 256</c>, and the reference (nostalgia) level imports 35 scenery solids that must
    /// block the ball. The pre-v2 8 was an authoring contract, not a measurement; the
    /// importer reports <c>[structure_capacity]</c> for a document over the table, and the
    /// motor refuses (never silently drops) an entry past the capacity.
    /// </para>
    /// <para>
    /// Per-step cost of 64 boxes, from the code: SweepMath validates each box once
    /// (<c>TryExpandBox</c>, ~10 ops) and then tests every obstacle inside the contact loop
    /// (<c>maxContacts</c> = 4 passes) plus the 4 projection passes, i.e. at most
    /// 8 x 64 = 512 box narrow tests per step. A box test is a slab test that rejects a far
    /// box on the first axis (~10-20 ops), so 64 boxes are on the order of 10 k ops/step
    /// (~0.5 Mops/s at 50 Hz) - the same order as ONE terrain chunk's support query
    /// (17 probes x 512 triangles) and far below the 3,456-triangle worst case. Nothing
    /// allocates per step: the sweep arrays are rebuilt only when the obstacle count
    /// changes, which is a registration/scan event (RebuildObstacles' own remark).
    /// </para>
    /// <para>
    /// The host object is <see cref="HideFlags.DontSave"/> and destroyed in
    /// <see cref="TearDown"/>.
    /// </para>
    /// </remarks>
    public class StaticBoxCapacityTests
    {
        private const string MotorTypeName =
            "VRCKatamari.BallMotor.KatamariBallMotor, VRCKatamari.Runtime";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic |
            BindingFlags.Public;
        private const int Capacity = 64;

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
            host = new GameObject("StaticBoxCapacityTests");
            host.hideFlags = HideFlags.DontSave;
            motor = host.AddComponent(motorType);
            Invoke("Start");
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
            host = null;
            motor = null;
        }

        private object Invoke(string name, params object[] args)
        {
            MethodInfo method = motorType.GetMethod(name, Any);
            Assert.IsNotNull(method, name + " not found");
            return method.Invoke(motor, args);
        }

        private int IntField(string name)
        {
            FieldInfo field = motorType.GetField(name, Any);
            Assert.IsNotNull(field, name + " field not found");
            return (int)field.GetValue(motor);
        }

        private bool AddBox(Vector3 minimum, Vector3 maximum)
        {
            return (bool)Invoke("AddBox", minimum, maximum);
        }

        private void Step(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Invoke("StepBall", 0.02f);
            }
        }

        [Test]
        public void SixtyFourBoxesRegisterAndTheSixtyFifthIsRefused()
        {
            for (int i = 0; i < Capacity; i++)
            {
                // Far apart and far from the ball, so only the table is under test.
                float x = 100f + 10f * i;
                Assert.IsTrue(AddBox(new Vector3(x, -1f, 0f), new Vector3(x + 4f, 0f, 4f)),
                    "registration " + (i + 1) + " of " + Capacity);
            }
            Assert.AreEqual(Capacity, IntField("boxCount"),
                "the table holds all 64 registrations");

            // The 65th is refused, and the refusal is an EXPLICIT, latched signal - not a
            // silent drop.
            Assert.IsFalse(AddBox(new Vector3(0f, -1f, 0f), new Vector3(4f, 0f, 4f)),
                "the 65th box must be refused");
            Assert.AreEqual(Capacity, IntField("boxCount"), "a refused box is not stored");
            Assert.AreEqual(1, (int)Invoke("GetOverflowCount"),
                "the refusal latches the overflow counter");
            string reason = (string)Invoke("GetFirstOverflowReason");
            Assert.IsNotNull(reason, "the refusal carries a reason");
            StringAssert.Contains("64", reason, "the reason names the capacity");
            Assert.IsTrue((bool)Invoke("HasOverflowSignal"));
        }

        [Test]
        public void OneBoxNearTheBallStillSupportsItAmongSixtyFour()
        {
            // Box 1 (surface id 1) is the only one near the ball: a 5 cm step under its
            // centre, inside the 0.075 m step band of the 0.30 m start ball.
            Assert.IsTrue(AddBox(new Vector3(-2f, -1f, -2f), new Vector3(2f, 0.05f, 2f)),
                "the near box registers");
            for (int i = 1; i < Capacity; i++)
            {
                float x = 100f + 10f * i;
                Assert.IsTrue(AddBox(new Vector3(x, -1f, 0f), new Vector3(x + 4f, 0f, 4f)),
                    "far box " + (i + 1));
            }
            Assert.AreEqual(Capacity, IntField("boxCount"));
            Assert.AreEqual(0, (int)Invoke("GetOverflowCount"), "nothing overflowed");

            Step(10);

            float radius = (float)Invoke("GetCollisionRadiusWorldM");
            Assert.AreEqual(1, (int)Invoke("GetSupportSurfaceId"),
                "the near box is the support even with 63 far ones registered");
            Assert.That((float)Invoke("GetSupportSurfaceHeightWorldM"),
                Is.EqualTo(0.05f).Within(1e-3f), "the box top is the support plane");
            Vector3 center = (Vector3)Invoke("GetCenterWorld");
            Assert.That(center.y, Is.EqualTo(0.05f + radius).Within(2e-3f),
                "the ball lands on the near box top, not on the ground plane");
        }
    }
}
