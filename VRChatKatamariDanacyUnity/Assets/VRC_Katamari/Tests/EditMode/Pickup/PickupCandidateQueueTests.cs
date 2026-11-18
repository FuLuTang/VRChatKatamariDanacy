using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins what the pickup host puts in the motor's four candidate slots: only props the
    /// ball can absorb at its current size, and always the nearest of those.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defect (0.222 m start ball): the four slots were held by props
    /// the size gate refused on every step (a 1.76 m onigiri, a wallet needing 246 mm, a
    /// 397 mm crab) while smaller props lying inside the same radius could not get in, so
    /// 20 props the ball had touched were never absorbed. The selection must therefore skip a
    /// prop the gate's own size predicate refuses, however near it is.
    /// </para>
    /// <para>
    /// The first group runs the pure selection (<see cref="PickupCandidateSelection"/>) and
    /// needs no engine object. The second group drives the real host and motor with
    /// <c>GameObject</c>s, so it only runs in Unity (the local runner lists it as not
    /// runnable); it pins the part the pure group cannot: the queue is made to match the
    /// selection, and an accepted candidate is never withdrawn.
    /// </para>
    /// <para>
    /// The size oracle is the original's rule (<c>Req = floor(1000*cbrt(60*k*a*b*c/pi))</c>
    /// whole millimetres against the ball's floored diameter), re-derived in the fixture
    /// asserts so a prop cannot quietly turn from "too big" into food.
    /// </para>
    /// </remarks>
    public class PickupCandidateQueueTests
    {
        private const float K = PickupAbsorbMath.DefaultVolumeK;
        private const float ActivationM = 1.6f;
        private const int Slots = 4;

        // Req 47 mm (cbrt(60 * 0.7 * 8e-6 / pi) = 0.0475 m): food for the 0.222 m start ball.
        private static readonly Vector3 SmallProp = new Vector3(0.02f, 0.02f, 0.02f);
        // Req 1186 mm (cbrt(60 * 0.7 * 0.125 / pi) = 1.186 m): a wall for a 0.222 m ball.
        private static readonly Vector3 BigProp = new Vector3(0.5f, 0.5f, 0.5f);

        private int[] indices;
        private float[] distancesSq;
        private int selected;

        [SetUp]
        public void SetUpSelection()
        {
            indices = new int[Slots];
            distancesSq = new float[Slots];
            selected = 0;
        }

        // ---- pure selection -------------------------------------------------------

        // Offers rows 0..n-1 (row i at distance distances[i] along +x) the way the host's
        // loop does; returns how many were inside the radius / within the size gate.
        private void Scan(float[] distances, Vector3[] sizes, float ballDiameterM,
            out int inRange, out int withinLimit)
        {
            float[] ks = new float[distances.Length];
            Vector3[] positions = new Vector3[distances.Length];
            for (int i = 0; i < distances.Length; i++)
            {
                ks[i] = K;
                positions[i] = new Vector3(distances[i], 0f, 0f);
            }
            inRange = 0;
            withinLimit = 0;
            selected = 0;
            for (int i = 0; i < distances.Length; i++)
            {
                int after;
                int offer = PickupCandidateSelection.OfferProp(i, positions[i], Vector3.zero,
                    ActivationM, sizes, ks, ballDiameterM, indices, distancesSq, selected, Slots,
                    out after);
                selected = after;
                if (offer == PickupCandidateSelection.OfferOutOfRange) continue;
                inRange++;
                if (offer == PickupCandidateSelection.OfferWithinLimit) withinLimit++;
            }
        }

        private static void AssertReq(Vector3 size, float expectedMm)
        {
            float reqMm;
            Assert.IsTrue(PickupAbsorbMath.TryComputePickupReqDiameterMm(size, K, out reqMm));
            Assert.AreEqual(expectedMm, reqMm, "fixture prop's Req drifted from the oracle");
        }

        [Test]
        public void TheFixturePropsAreTheSizesTheTestsClaim()
        {
            AssertReq(SmallProp, 47f);
            AssertReq(BigProp, 1186f);
        }

        [Test]
        public void TheNearestFourBeingTooBigDoNotKeepASmallPropOutOfTheSelection()
        {
            // Four near props the ball cannot take, small ones further out.
            float[] distances = { 0.3f, 0.4f, 0.5f, 0.6f, 0.9f, 1.1f, 1.3f };
            Vector3[] sizes = { BigProp, BigProp, BigProp, BigProp, SmallProp, SmallProp, SmallProp };

            int inRange;
            int withinLimit;
            Scan(distances, sizes, 0.222f, out inRange, out withinLimit);

            Assert.AreEqual(7, inRange);
            Assert.AreEqual(3, withinLimit, "only the small props pass the size gate");
            Assert.AreEqual(3, selected);
            Assert.AreEqual(4, indices[0], "nearest absorbable first");
            Assert.AreEqual(5, indices[1]);
            Assert.AreEqual(6, indices[2]);
            for (int row = 0; row < 4; row++)
            {
                Assert.IsFalse(PickupCandidateSelection.IsSelected(row, indices, selected),
                    "a prop the gate refuses on size must not hold a slot");
            }
        }

        [Test]
        public void ANearerAbsorbablePropDisplacesAFartherOneWhateverTheOfferOrder()
        {
            // Five absorbable props, four slots: the farthest (row 0) must be the one left out
            // although it is offered first - this is what makes the host withdraw it.
            float[] distances = { 1.5f, 0.2f, 0.4f, 0.6f, 0.8f };
            Vector3[] sizes = { SmallProp, SmallProp, SmallProp, SmallProp, SmallProp };

            int inRange;
            int withinLimit;
            Scan(distances, sizes, 0.222f, out inRange, out withinLimit);

            Assert.AreEqual(5, withinLimit);
            Assert.AreEqual(4, selected);
            Assert.IsFalse(PickupCandidateSelection.IsSelected(0, indices, selected));
            Assert.AreEqual(1, indices[0]);
            Assert.AreEqual(2, indices[1]);
            Assert.AreEqual(3, indices[2]);
            Assert.AreEqual(4, indices[3]);
        }

        [Test]
        public void APropThatWasTooBigIsSelectedOnceTheBallHasGrown()
        {
            float[] distances = { 0.4f };
            Vector3[] sizes = { BigProp };

            int inRange;
            int withinLimit;
            Scan(distances, sizes, 0.222f, out inRange, out withinLimit);
            Assert.AreEqual(1, inRange);
            Assert.AreEqual(0, withinLimit);
            Assert.AreEqual(0, selected);

            // Req is 1186 mm: one millimetre short still refuses, the exact value is food.
            Scan(distances, sizes, 1.185f, out inRange, out withinLimit);
            Assert.AreEqual(0, selected, "1185 mm is one short of the requirement");
            Scan(distances, sizes, 1.186f, out inRange, out withinLimit);
            Assert.AreEqual(1, withinLimit);
            Assert.AreEqual(1, selected);
            Assert.AreEqual(0, indices[0]);
        }

        [Test]
        public void TheSelectionAgreesWithTheGateAtEveryDiameter()
        {
            // One definition of "too big": the selection's verdict must equal the gate's own
            // resolver, not a copy of it, across a sweep that crosses both props' thresholds.
            Vector3[] sizes = { SmallProp, BigProp, new Vector3(0.12f, 0.2f, 0.08f) };
            float[] distances = { 0.5f, 0.6f, 0.7f };
            for (int mm = 20; mm <= 1400; mm += 7)
            {
                float diameterM = mm / 1000f;
                int inRange;
                int withinLimit;
                Scan(distances, sizes, diameterM, out inRange, out withinLimit);
                int expected = 0;
                for (int i = 0; i < sizes.Length; i++)
                {
                    float reqMm;
                    float ballMm;
                    bool within;
                    Assert.IsTrue(PickupAbsorbMath.TryResolveAbsorbGate(sizes[i], K, diameterM,
                        out reqMm, out ballMm, out within));
                    if (within) expected++;
                }
                Assert.AreEqual(expected, withinLimit, "diameter " + mm + " mm");
            }
        }

        [Test]
        public void APropOutsideTheActivationRadiusIsNotOffered()
        {
            float[] distances = { 1.7f };
            Vector3[] sizes = { SmallProp };
            int inRange;
            int withinLimit;
            Scan(distances, sizes, 0.222f, out inRange, out withinLimit);
            Assert.AreEqual(0, inRange);
            Assert.AreEqual(0, selected);
        }

        [Test]
        public void AnIllegalDiameterSelectsNothing()
        {
            // The gate refuses every item at a non-finite or negative diameter; the
            // selection must agree instead of queueing what the motor can only refuse.
            float[] distances = { 0.3f };
            Vector3[] sizes = { SmallProp };
            int inRange;
            int withinLimit;
            Scan(distances, sizes, float.NaN, out inRange, out withinLimit);
            Assert.AreEqual(0, selected);
            Scan(distances, sizes, -1f, out inRange, out withinLimit);
            Assert.AreEqual(0, selected);
        }

        [Test]
        public void PoseRefreshUsesOneMillimetreAtTheCentreOrBoxCorners()
        {
            Assert.IsFalse(PickupCandidateSelection.HasPoseMoved(Vector3.zero, Quaternion.identity,
                new Vector3(0.0005f, 0f, 0f), Quaternion.identity, SmallProp, 0.001f));
            Assert.IsTrue(PickupCandidateSelection.HasPoseMoved(Vector3.zero, Quaternion.identity,
                new Vector3(0.0011f, 0f, 0f), Quaternion.identity, SmallProp, 0.001f));
            // A long object can move a corner by >1 mm with a tiny rotation and no translation.
            Assert.IsTrue(PickupCandidateSelection.HasPoseMoved(Vector3.zero, Quaternion.identity,
                Vector3.zero, Quaternion.Euler(0f, 0.02f, 0f), new Vector3(10f, 0.01f, 0.01f), 0.001f));
            Assert.IsFalse(PickupCandidateSelection.HasPoseMoved(Vector3.zero, Quaternion.identity,
                Vector3.zero, new Quaternion(0f, 0f, 0f, -1f), SmallProp, 0.001f));
        }

        [Test]
        public void TheContactGateJudgesTheSnapshotPoseButStillNeedsAnAllowedKind()
        {
            // The two facts the Unity mover test rests on, engine-free. (1) An empty kind
            // allowlist refuses every candidate BEFORE the contact test, whatever its pose:
            // the motor's Start initialises the allowlist empty and a level host is the one
            // that calls SetAllowedKinds, so a bare motor fixture looks exactly like a stale
            // pose without being one. (2) The contact verdict is a function of the snapshot's
            // own position - the value RefreshCandidatePoseUnlessAccepted writes - so a
            // refreshed candidate is judged where it is now, not where it was submitted.
            bool[] accepted = new bool[1];
            int[] outcomes = new int[1];
            float[] boxDistances = new float[1];
            float[] contactLimits = new float[1];
            Vector3[] sizes = { SmallProp };
            Quaternion[] rotations = { Quaternion.identity };
            Vector3[] positions = { new Vector3(0.6f, 0f, 0f) }; // the submission pose
            long[] growth = { 1000000L };
            float[] volumeK = { K };
            const float collisionRadius = 0.15f;  // the 0.2997 m start ball's half
            const float semanticDiameter = 0.2997f;
            const float maxCollisionRadius = 15f; // half the motor's default 30 m cap

            PickupAbsorbStepMath.EvaluatePendingCandidates(new[] { true }, new[] { "stone" },
                sizes, rotations, positions, growth, volumeK, 1, new string[0], 0,
                Vector3.zero, collisionRadius, 0f, semanticDiameter, maxCollisionRadius,
                accepted, outcomes, boxDistances, contactLimits);
            Assert.AreEqual((int)AbsorbOutcome.RejectKindNotAllowed, outcomes[0],
                "an empty allowlist refuses before the contact test, whatever the pose");

            PickupAbsorbStepMath.EvaluatePendingCandidates(new[] { true }, new[] { "stone" },
                sizes, rotations, positions, growth, volumeK, 1, new[] { "stone" }, 1,
                Vector3.zero, collisionRadius, 0f, semanticDiameter, maxCollisionRadius,
                accepted, outcomes, boxDistances, contactLimits);
            Assert.AreEqual((int)AbsorbOutcome.RejectOutOfReach, outcomes[0],
                "the stale submission pose is out of reach");
            Assert.Greater(boxDistances[0], contactLimits[0]);

            // What RefreshCandidatePoseUnlessAccepted writes: pendingPositions/pendingRotations.
            positions[0] = Vector3.zero;
            PickupAbsorbStepMath.EvaluatePendingCandidates(new[] { true }, new[] { "stone" },
                sizes, rotations, positions, growth, volumeK, 1, new[] { "stone" }, 1,
                Vector3.zero, collisionRadius, 0f, semanticDiameter, maxCollisionRadius,
                accepted, outcomes, boxDistances, contactLimits);
            Assert.IsTrue(accepted[0], "the refreshed pose is the pose the contact gate judges");
            Assert.AreEqual((int)AbsorbOutcome.Absorb, outcomes[0]);
        }

        // ---- host + motor (Unity only) -----------------------------------------------

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic |
            BindingFlags.Public;

        private Type motorType;
        private Type fieldType;
        private GameObject motorHost;
        private Component motor;
        private GameObject fieldHost;
        private Component field;
        private GameObject rootObject;
        private GameObject[] propObjects;
        private string[] ids;

        [TearDown]
        public void TearDownHost()
        {
            if (fieldHost != null) UnityEngine.Object.DestroyImmediate(fieldHost);
            if (rootObject != null) UnityEngine.Object.DestroyImmediate(rootObject);
            if (propObjects != null)
            {
                foreach (GameObject prop in propObjects)
                {
                    if (prop != null) UnityEngine.Object.DestroyImmediate(prop);
                }
            }
            if (motorHost != null) UnityEngine.Object.DestroyImmediate(motorHost);
            fieldHost = null;
            rootObject = null;
            propObjects = null;
            motorHost = null;
            motor = null;
            field = null;
        }

        // Builds a started motor and a started host over the given props. distances are
        // metres along +x from the ball centre; sizes are each prop's own size.
        private void BuildHost(float[] distances, Vector3[] sizes)
        {
            motorType = FindType("VRCKatamari.BallMotor.KatamariBallMotor");
            fieldType = FindType("VRCKatamari.Pickup.PickupFieldHost");
            Assert.IsNotNull(motorType, "KatamariBallMotor type not loaded");
            Assert.IsNotNull(fieldType, "PickupFieldHost type not loaded");

            motorHost = new GameObject("PickupCandidateQueueTestsMotor");
            motor = motorHost.AddComponent(motorType);
            Call(motorType, motor, "Start");
            Assert.Greater((float)Call(motorType, motor, "GetAbsorbGateDiameterMm"), 0f,
                "the motor must have derived a size");
            // A level host (LevelArenaHost / LevelSwitchHost) wires the gate's kind allowlist.
            // Start initialises it empty and an empty allowlist denies EVERY kind before the
            // contact test, so a bare fixture would reject candidates as kind-not-allowed at
            // any pose - which reads like a stale-pose failure but is not one.
            Call(motorType, motor, "SetAllowedKinds", new[] { "stone" }, 1);
            Assert.AreEqual(1, (int)Call(motorType, motor, "GetAllowedKindCount"),
                "the fixture must allow the kinds it submits");
            Vector3 center = (Vector3)Call(motorType, motor, "GetCenterWorld");

            int count = distances.Length;
            propObjects = new GameObject[count];
            ids = new string[count];
            Transform[] transforms = new Transform[count];
            string[] kinds = new string[count];
            Vector3[] bounds = new Vector3[count];
            long[] growth = new long[count];
            for (int i = 0; i < count; i++)
            {
                ids[i] = "prop_" + i;
                propObjects[i] = new GameObject(ids[i]);
                propObjects[i].transform.position = center + new Vector3(distances[i], 0f, 0f);
                propObjects[i].AddComponent<MeshRenderer>();
                transforms[i] = propObjects[i].transform;
                kinds[i] = "stone";
                bounds[i] = sizes[i];
                growth[i] = 1000000L;
            }

            rootObject = new GameObject("PickupCandidateQueueTestsRoot");
            fieldHost = new GameObject("PickupCandidateQueueTestsField");
            field = fieldHost.AddComponent(fieldType);
            SetField(fieldType, field, "motor", motor);
            SetField(fieldType, field, "attachmentRoot", rootObject.transform);
            SetField(fieldType, field, "propTransforms", transforms);
            SetField(fieldType, field, "itemIds", ids);
            SetField(fieldType, field, "kinds", kinds);
            SetField(fieldType, field, "propBounds", bounds);
            SetField(fieldType, field, "propLocalBounds", bounds);
            SetField(fieldType, field, "propGrowthMm3", growth);
            Call(fieldType, field, "Start");
        }

        private void ScanOnce()
        {
            // EditMode does not advance the host's cadence, so the next scan is made due.
            SetField(fieldType, field, "nextScanTime", 0f);
            Call(fieldType, field, "Update");
        }

        private int Pending()
        {
            return (int)Call(motorType, motor, "GetPendingCandidateCount");
        }

        private bool IsQueued(string id)
        {
            return (bool)Call(motorType, motor, "IsCandidateQueued", id);
        }

        [Test]
        public void TheHostQueuesTheSmallPropsBehindFourWallsAndNeverTheWalls()
        {
            BuildHost(new[] { 0.5f, 0.6f, 0.7f, 0.8f, 1.0f, 1.1f, 1.2f },
                new[] { BigProp, BigProp, BigProp, BigProp, SmallProp, SmallProp, SmallProp });

            ScanOnce();

            Assert.AreEqual(3, Pending(), "only the three small props are queued");
            for (int wall = 0; wall < 4; wall++)
                Assert.IsFalse(IsQueued(ids[wall]), ids[wall] + " is too big and must not hold a slot");
            for (int food = 4; food < 7; food++)
                Assert.IsTrue(IsQueued(ids[food]), ids[food] + " is absorbable and must be queued");
            Assert.AreEqual(7, (int)Call(fieldType, field, "GetScanInRangeCount"));
            Assert.AreEqual(3, (int)Call(fieldType, field, "GetScanWithinLimitCount"));
            Assert.AreEqual(3, (int)Call(fieldType, field, "GetQueuedCount"));
        }

        [Test]
        public void ANearerAbsorbablePropEvictsTheFarthestQueuedOne()
        {
            BuildHost(new[] { 0.6f, 0.8f, 1.0f, 1.2f, 1.5f },
                new[] { SmallProp, SmallProp, SmallProp, SmallProp, SmallProp });

            ScanOnce();
            // Rows 0..3 fill the four slots; row 4 (1.5 m) is the fifth and waits.
            Assert.AreEqual(4, Pending());
            Assert.IsFalse(IsQueued(ids[4]));

            // A prop that is nearer than everything queued: the farthest queued (row 3) goes.
            Vector3 center = (Vector3)Call(motorType, motor, "GetCenterWorld");
            propObjects[4].transform.position = center + new Vector3(0.3f, 0f, 0f);
            ScanOnce();

            Assert.AreEqual(4, Pending());
            Assert.IsTrue(IsQueued(ids[4]), "the nearer prop took a slot");
            Assert.IsFalse(IsQueued(ids[3]), "the farthest queued prop was displaced");
        }

        [Test]
        public void AnAcceptedCandidateIsNotWithdrawnEvenWhenANearerPropDisplacesIt()
        {
            BuildHost(new[] { 0.6f, 0.8f, 1.0f, 1.2f, 1.5f },
                new[] { SmallProp, SmallProp, SmallProp, SmallProp, SmallProp });
            ScanOnce();
            Assert.AreEqual(4, Pending());

            // Row 3's gate verdict is Absorb and its commit has not happened (the state a failed
            // commit leaves). pendingAccepted is the gate stage's own flag; the test sets it
            // directly because a real accept needs a stepped motor.
            bool[] accepted = (bool[])GetField(motorType, motor, "pendingAccepted");
            for (int slot = 0; slot < Pending(); slot++)
            {
                if ((string)Call(motorType, motor, "GetPendingCandidateItemId", slot) == ids[3])
                    accepted[slot] = true;
            }

            Vector3 center = (Vector3)Call(motorType, motor, "GetCenterWorld");
            propObjects[4].transform.position = center + new Vector3(0.3f, 0f, 0f);
            ScanOnce();

            Assert.IsTrue(IsQueued(ids[3]), "an accepted, uncommitted candidate keeps its slot");
            Assert.IsTrue(IsQueued(ids[0]));
        }

        [Test]
        public void APropTooBigAtFirstIsQueuedAfterTheBallGrows()
        {
            BuildHost(new[] { 0.5f }, new[] { BigProp });

            ScanOnce();
            Assert.AreEqual(0, Pending(), "the ball cannot take a 0.5 m cube yet");
            Assert.AreEqual(1, (int)Call(fieldType, field, "GetScanInRangeCount"));
            Assert.AreEqual(0, (int)Call(fieldType, field, "GetScanWithinLimitCount"));

            // The ball's diameter is the gate's ball side; 1.3 m is past the cube's 1186 mm.
            SetField(motorType, motor, "semanticDiameterWorldM", 1.3f);
            ScanOnce();

            Assert.AreEqual(1, Pending());
            Assert.IsTrue(IsQueued(ids[0]));
        }

        [Test]
        public void APropThatBecomesTooBigIsWithdrawnOnTheNextScan()
        {
            BuildHost(new[] { 0.5f }, new[] { BigProp });
            SetField(motorType, motor, "semanticDiameterWorldM", 1.3f);
            ScanOnce();
            Assert.IsTrue(IsQueued(ids[0]));

            // A shrink (a bank-back) moves the ball under the requirement again.
            SetField(motorType, motor, "semanticDiameterWorldM", 0.3f);
            ScanOnce();
            Assert.IsFalse(IsQueued(ids[0]), "a candidate the gate would now refuse is released");
        }

        [Test]
        public void AFullQueueRefreshesAMoverAndAbsorbsAtItsNewPosition()
        {
            BuildHost(new[] { 0.6f, 0.8f, 1.0f, 1.2f },
                new[] { SmallProp, SmallProp, SmallProp, SmallProp });
            ScanOnce();
            Assert.AreEqual(4, Pending());
            float[] times = (float[])GetField(motorType, motor, "pendingSubmitTimes");
            times[0] = -123f;
            Vector3 center = (Vector3)Call(motorType, motor, "GetCenterWorld");
            propObjects[0].transform.position = center;
            propObjects[0].transform.rotation = Quaternion.Euler(0f, 35f, 0f);
            ScanOnce();
            Vector3[] positions = (Vector3[])GetField(motorType, motor, "pendingPositions");
            Quaternion[] rotations = (Quaternion[])GetField(motorType, motor, "pendingRotations");
            Assert.AreEqual(center, positions[0], "a full queue must not prevent pose refresh");
            Assert.AreEqual(propObjects[0].transform.rotation, rotations[0]);
            Assert.AreEqual(-123f, times[0], "pose refresh preserves the submission age");
            Call(motorType, motor, "EvaluatePendingCandidates", center);
            bool[] accepted = (bool[])GetField(motorType, motor, "pendingAccepted");
            Assert.IsTrue(accepted[0], "the ball contacts the moved box, not its stale position");
            Call(motorType, motor, "CommitAcceptedAbsorbs", center, Vector3.up);
            Assert.IsFalse(IsQueued(ids[0]), "the accepted moved prop committed and left the queue");
            Assert.Greater((int)Call(motorType, motor, "GetAbsorbedEventCount"), 0);
        }

        [Test]
        public void AnAcceptedMoverKeepsItsFrozenPoseAndSubmillimetreMovesAccumulate()
        {
            BuildHost(new[] { 0.6f }, new[] { SmallProp });
            ScanOnce();
            Vector3[] positions = (Vector3[])GetField(motorType, motor, "pendingPositions");
            Vector3 initial = positions[0];
            propObjects[0].transform.position = initial + new Vector3(0.0006f, 0f, 0f);
            ScanOnce();
            Assert.AreEqual(initial, positions[0]);
            propObjects[0].transform.position = initial + new Vector3(0.0012f, 0f, 0f);
            ScanOnce();
            Assert.AreEqual(propObjects[0].transform.position, positions[0]);
            Vector3 frozen = positions[0];
            bool[] accepted = (bool[])GetField(motorType, motor, "pendingAccepted");
            accepted[0] = true;
            propObjects[0].transform.position = initial + new Vector3(0.2f, 0f, 0f);
            ScanOnce();
            Assert.AreEqual(frozen, positions[0], "accepted entries are not refreshed or withdrawn");
            Assert.IsTrue(accepted[0]);
            Assert.IsTrue(IsQueued(ids[0]));
            Assert.AreEqual(1, Pending());
        }

        // ---- reflection helpers ----------------------------------------------------

        private static Type FindType(string fullName)
        {
            Type direct = Type.GetType(fullName + ", VRCKatamari.Runtime");
            if (direct != null) return direct;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type found = assembly.GetType(fullName);
                if (found != null) return found;
            }
            return null;
        }

        private static object Call(Type type, object target, string name, params object[] args)
        {
            MethodInfo method = type.GetMethod(name, Any);
            Assert.IsNotNull(method, name + " not found on " + type.Name);
            return method.Invoke(target, args);
        }

        private static object GetField(Type type, object target, string name)
        {
            FieldInfo fieldInfo = type.GetField(name, Any);
            Assert.IsNotNull(fieldInfo, name + " field not found on " + type.Name);
            return fieldInfo.GetValue(target);
        }

        private static void SetField(Type type, object target, string name, object value)
        {
            FieldInfo fieldInfo = type.GetField(name, Any);
            Assert.IsNotNull(fieldInfo, name + " field not found on " + type.Name);
            fieldInfo.SetValue(target, value);
        }
    }
}
