using NUnit.Framework;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for <see cref="PropFallMath"/>: the stack-fall rules as pure math
    /// (PROP_BEHAVIOURS section 10.9). No GameObjects are created; the cascade tests drive
    /// the same static functions the host calls, in the same order, on plain arrays.
    /// </summary>
    /// <remarks>
    /// Expected values are hand calculations: with g = 9.81 and dt = 1/30 a prop that
    /// starts at rest drops g*dt^2*n(n+1)/2 = 0.0109 * n(n+1)/2 metres in n steps, so
    /// 1 m needs n(n+1)/2 >= 91.5, i.e. 14 steps, and 0.2 m needs 6.
    /// What needs a scene (Transform writes, the terrain query, the pickup host call
    /// points) is NOT covered here: it is on the Unity operator's list.
    /// </remarks>
    public class PropFallMathTests
    {
        private const float Tolerance = 1e-4f;

        // --------------------------------------------------------------- parents ---

        [Test]
        public void SanitizeParents_CleanColumnIsUntouched()
        {
            int[] raw = { -1, 0, 1, -1 };
            var parents = new int[4];
            int repaired = PropFallMath.SanitizeParents(raw, 4, parents);
            Assert.AreEqual(0, repaired);
            CollectionAssert.AreEqual(raw, parents);
        }

        [Test]
        public void SanitizeParents_SelfEdgeAndOutOfRangeBecomeNone()
        {
            int[] raw = { 0, 9, -5, 1 };
            var parents = new int[4];
            int repaired = PropFallMath.SanitizeParents(raw, 4, parents);
            // 0 -> itself, 9 -> past the end, -5 -> below -1: three repairs; the last row
            // points at row 1 which is valid.
            Assert.AreEqual(3, repaired);
            CollectionAssert.AreEqual(new[] { -1, -1, -1, 1 }, parents);
        }

        [Test]
        public void SanitizeParents_CycleIsCutAtItsLowestRow()
        {
            // 0 -> 1 -> 2 -> 0 is a cycle; row 3 hangs off it.
            int[] raw = { 1, 2, 0, 2 };
            var parents = new int[4];
            int repaired = PropFallMath.SanitizeParents(raw, 4, parents);
            Assert.AreEqual(1, repaired);
            CollectionAssert.AreEqual(new[] { -1, 2, 0, 2 }, parents);
        }

        [Test]
        public void SanitizeParents_NullOrShortColumnReadsAsNoParents()
        {
            var parents = new int[3];
            Assert.AreEqual(0, PropFallMath.SanitizeParents(null, 3, parents));
            CollectionAssert.AreEqual(new[] { -1, -1, -1 }, parents);
            Assert.AreEqual(0, PropFallMath.SanitizeParents(new[] { 1 }, 3, parents));
            CollectionAssert.AreEqual(new[] { 1, -1, -1 }, parents);
        }

        [Test]
        public void BuildChildTable_IsCsrInRowOrder()
        {
            // 0 has children 1 and 2; 1 has child 3; 2 and 3 have none.
            int[] parents = { -1, 0, 0, 1 };
            var start = new int[5];
            var list = new int[4];
            int total = PropFallMath.BuildChildTable(parents, 4, start, list);
            Assert.AreEqual(3, total);
            CollectionAssert.AreEqual(new[] { 0, 2, 3, 3, 3 }, start);
            Assert.AreEqual(1, list[0]);
            Assert.AreEqual(2, list[1]);
            Assert.AreEqual(3, list[2]);
        }

        // ------------------------------------------------------------------ fall ---

        [Test]
        public void StepFall_FirstStepMatchesHandCalculation()
        {
            float bottom;
            float velocity;
            bool landed = PropFallMath.StepFall(1f, 0f, 0f, out bottom, out velocity);
            // v = -9.81/30 = -0.327; y = 1 - 0.327/30 = 0.9891
            Assert.IsFalse(landed);
            Assert.AreEqual(-0.327f, velocity, Tolerance);
            Assert.AreEqual(0.9891f, bottom, Tolerance);
        }

        [Test]
        public void StepFall_OneMetreLandsInFourteenSteps()
        {
            float bottom = 1f;
            float velocity = 0f;
            int steps = 0;
            while (steps < 100)
            {
                steps++;
                if (PropFallMath.StepFall(bottom, velocity, 0f, out bottom, out velocity)) break;
            }
            Assert.AreEqual(14, steps);
            Assert.AreEqual(0f, bottom, Tolerance);
            Assert.AreEqual(0f, velocity, Tolerance);
            Assert.That(steps * PropFallMath.StepSeconds, Is.InRange(0.4f, 0.5f));
        }

        [Test]
        public void StepFall_LandsWithinTwoMillimetres()
        {
            float bottom;
            float velocity;
            // A foot 1 mm above the surface after the step lands; 3 mm does not.
            Assert.IsTrue(PropFallMath.StepFall(0.0109f + 0.001f, 0f, 0f, out bottom, out velocity));
            Assert.AreEqual(0f, bottom, Tolerance);
            Assert.IsFalse(PropFallMath.StepFall(0.0109f + 0.003f, 0f, 0f, out bottom, out velocity));
        }

        [Test]
        public void StepFall_FastPropDoesNotTunnelThinBoard()
        {
            float bottom;
            float velocity;
            // 20 m/s down from 1.0 m; a board top at 0.5 m is below the previous foot, so it
            // is a landing surface and the step ends ON it, not under it.
            Assert.IsTrue(PropFallMath.IsLandingSurface(0.5f, 1f));
            Assert.IsTrue(PropFallMath.StepFall(1f, -20f, 0.5f, out bottom, out velocity));
            Assert.AreEqual(0.5f, bottom, Tolerance);
        }

        [Test]
        public void IsLandingSurface_SurfaceAboveThePreviousFootIsNotOne()
        {
            Assert.IsFalse(PropFallMath.IsLandingSurface(1.2f, 1f));
            Assert.IsTrue(PropFallMath.IsLandingSurface(1.001f, 1f)); // within 2 mm
        }

        // ------------------------------------------------------------ seating ---

        [Test]
        public void IsSeatedOn_WindowIsScaledByTheSmallerHeight()
        {
            // child height 0.2, supporter 0.5: the window is [-0.06, +0.10] m.
            Assert.IsTrue(PropFallMath.IsSeatedOn(1.0f, 0.2f, 1.0f, 0.5f));    // gap 0
            Assert.IsTrue(PropFallMath.IsSeatedOn(1.09f, 0.2f, 1.0f, 0.5f));   // hover 9 cm
            Assert.IsFalse(PropFallMath.IsSeatedOn(1.11f, 0.2f, 1.0f, 0.5f));  // hover 11 cm
            Assert.IsTrue(PropFallMath.IsSeatedOn(0.95f, 0.2f, 1.0f, 0.5f));   // embedded 5 cm
            Assert.IsFalse(PropFallMath.IsSeatedOn(0.93f, 0.2f, 1.0f, 0.5f));  // embedded 7 cm
        }

        [Test]
        public void IsSeatedOn_SupporterTopMustBeBelowTheChildTop()
        {
            // A tall supporter whose top is above the child's top does not hold it up.
            Assert.IsFalse(PropFallMath.IsSeatedOn(1.0f, 0.2f, 1.3f, 2.0f));
        }

        [Test]
        public void IsSeatedOnSurface_NoSurfaceNeverSeats()
        {
            Assert.IsFalse(PropFallMath.IsSeatedOnSurface(0f, 0.2f, PropFallMath.NoSurfaceY));
            Assert.IsTrue(PropFallMath.IsSeatedOnSurface(0.02f, 0.2f, 0f));
            Assert.IsFalse(PropFallMath.IsSeatedOnSurface(0.5f, 0.2f, 0f));
        }

        [Test]
        public void FootprintsTouch_TouchingCountsAGapDoesNot()
        {
            Assert.IsTrue(PropFallMath.FootprintsTouch(0, 1, 0, 1, 1, 2, 0, 1));
            Assert.IsFalse(PropFallMath.FootprintsTouch(0, 1, 0, 1, 1.01f, 2, 0, 1));
            Assert.IsTrue(PropFallMath.FootprintsTouch(0, 2, 0, 2, 0.5f, 1, 0.5f, 1));
        }

        // ------------------------------------------------------------- queue ---

        [Test]
        public void Enqueue_SkipsEatenFallingAndAlreadyQueuedRows()
        {
            int[] state = { PropFallMath.StateResting, PropFallMath.StateAbsorbed,
                PropFallMath.StateFalling, PropFallMath.StateSettled };
            var flag = new bool[4];
            var queue = new int[4];
            int count = 0;
            for (int i = 0; i < 4; i++) count = PropFallMath.Enqueue(i, state, flag, queue, count);
            count = PropFallMath.Enqueue(0, state, flag, queue, count); // again
            Assert.AreEqual(2, count);
            Assert.AreEqual(0, queue[0]);
            Assert.AreEqual(3, queue[1]);
        }

        [Test]
        public void ApplyRecheck_SupportedPropIsRetiredAfterMaxRechecks()
        {
            int[] state = { PropFallMath.StateResting };
            var velocity = new float[1];
            var flag = new[] { true };
            var rechecks = new int[1];
            int[] childStart = { 0, 0 };
            var childList = new int[1];
            var next = new int[1];
            int n = PropFallMath.ApplyRecheck(0, true, 3, state, velocity, flag, rechecks,
                childStart, childList, next, 0);
            Assert.AreEqual(1, n);
            n = PropFallMath.ApplyRecheck(0, true, 3, state, velocity, flag, rechecks,
                childStart, childList, next, 0);
            Assert.AreEqual(1, n);
            n = PropFallMath.ApplyRecheck(0, true, 3, state, velocity, flag, rechecks,
                childStart, childList, next, 0);
            Assert.AreEqual(0, n, "the third supported re-check retires the row");
            Assert.IsFalse(flag[0]);
            Assert.AreEqual(PropFallMath.StateResting, state[0]);
        }

        // ------------------------------------------------------------- grid ---

        [Test]
        public void Grid_FindsNeighboursOnceAndSkipsFarProps()
        {
            // Three 1 m boxes: 0 at x 0..1, 1 at x 0.5..1.5 (overlaps 0), 2 far away at x 20..21.
            float[] minX = { 0f, 0.5f, 20f };
            float[] maxX = { 1f, 1.5f, 21f };
            float[] minZ = { 0f, 0f, 0f };
            float[] maxZ = { 1f, 1f, 1f };
            var info = new float[PropFallMath.GridInfoLength];
            Assert.IsTrue(PropFallMath.ComputeGrid(minX, maxX, minZ, maxZ, 3, 1.5f, info));
            var start = new int[(int)(info[PropFallMath.GridColumns] * info[PropFallMath.GridRows]) + 1];
            var items = new int[PropFallMath.CountGridEntries(info, minX, maxX, minZ, maxZ, 3)];
            PropFallMath.FillGrid(info, minX, maxX, minZ, maxZ, 3, start, items);

            var stamp = new int[3];
            var output = new int[8];
            int found = PropFallMath.CollectCandidates(info, start, items, stamp, 1, 0f, 1f, 0f, 1f,
                output);
            Assert.AreEqual(2, found, "rows 0 and 1, each once even if they share several cells");
            Assert.AreEqual(0, output[0]);
            Assert.AreEqual(1, output[1]);
        }

        [Test]
        public void Grid_HugeFootprintIsNotIndexed()
        {
            // A 100 m x 100 m "building" at 1.5 m cells would be 4489 cells: not indexed.
            float[] minX = { 0f, 10f };
            float[] maxX = { 100f, 10.5f };
            float[] minZ = { 0f, 10f };
            float[] maxZ = { 100f, 10.5f };
            var info = new float[PropFallMath.GridInfoLength];
            Assert.IsTrue(PropFallMath.ComputeGrid(minX, maxX, minZ, maxZ, 2, 1.5f, info));
            int entries = PropFallMath.CountGridEntries(info, minX, maxX, minZ, maxZ, 2);
            // The small prop (10..10.5 m, cells 6..7 on both axes) fills 2 x 2 = 4 entries; the
            // 100 m prop would add 4489 but is skipped.
            Assert.AreEqual(4, entries, "only the small prop is indexed");
        }

        // ---------------------------------------------------------- landing ---

        [Test]
        public void HighestLandingTop_IgnoresFallingEatenAndAboveFoot()
        {
            // candidates: 0 resting top 0.2, 1 settled top 0.5, 2 falling top 0.8,
            // 3 eaten top 0.9, 4 resting top 1.5 (above the foot at 1.0).
            int[] state = { PropFallMath.StateResting, PropFallMath.StateSettled,
                PropFallMath.StateFalling, PropFallMath.StateAbsorbed, PropFallMath.StateResting,
                PropFallMath.StateFalling };
            float[] minX = { 0, 0, 0, 0, 0, 0 };
            float[] maxX = { 1, 1, 1, 1, 1, 1 };
            float[] minZ = { 0, 0, 0, 0, 0, 0 };
            float[] maxZ = { 1, 1, 1, 1, 1, 1 };
            float[] bottom = { 0f, 0.3f, 0.7f, 0.8f, 1.0f, 1.0f };
            float[] height = { 0.2f, 0.2f, 0.1f, 0.1f, 0.5f, 0.1f };
            int[] candidates = { 0, 1, 2, 3, 4 };
            float top = PropFallMath.HighestLandingTop(5, candidates, 5, state, minX, maxX, minZ,
                maxZ, bottom, height, 0, 1, 0, 1, 1.0f);
            Assert.AreEqual(0.5f, top, Tolerance);
            float none = PropFallMath.HighestLandingTop(5, new[] { 2, 3 }, 2, state, minX, maxX,
                minZ, maxZ, bottom, height, 0, 1, 0, 1, 1.0f);
            Assert.AreEqual(PropFallMath.NoSurfaceY, none);
        }

        // ---------------------------------------------------------- cascade ---

        /// <summary>
        /// Plain-array stand-in for PropFallHost: the same calls in the same order, with
        /// "everything is a candidate" instead of the grid and a flat ground at y = 0.
        /// </summary>
        private sealed class Sim
        {
            public int N;
            public int[] State;
            public float[] V;
            public bool[] Flag;
            public int[] Rechecks;
            public int[] ChildStart;
            public int[] ChildList;
            public int[] QueueA;
            public int[] QueueB;
            public int QueueCount;
            public int[] All;
            public float[] MinX, MaxX, MinZ, MaxZ, Bottom, Height;
            public int[] StartedAtTick;
            public int[] SettledAtTick;
            public int Tick;
            public int MaxRechecks = 90;

            public Sim(int[] parents, float[] bottom, float[] height, float[] x)
            {
                N = parents.Length;
                State = new int[N];
                V = new float[N];
                Flag = new bool[N];
                Rechecks = new int[N];
                ChildStart = new int[N + 1];
                ChildList = new int[N];
                PropFallMath.BuildChildTable(parents, N, ChildStart, ChildList);
                QueueA = new int[N];
                QueueB = new int[N];
                All = new int[N];
                MinX = new float[N]; MaxX = new float[N]; MinZ = new float[N]; MaxZ = new float[N];
                Bottom = (float[])bottom.Clone();
                Height = (float[])height.Clone();
                StartedAtTick = new int[N];
                SettledAtTick = new int[N];
                for (int i = 0; i < N; i++)
                {
                    All[i] = i;
                    MinX[i] = x[i] - 0.5f; MaxX[i] = x[i] + 0.5f;
                    MinZ[i] = -0.5f; MaxZ[i] = 0.5f;
                }
            }

            public void Absorb(int index)
            {
                State[index] = PropFallMath.StateAbsorbed;
                QueueCount = PropFallMath.EnqueueChildren(index, ChildStart, ChildList, State, Flag,
                    QueueA, QueueCount);
            }

            private bool Supported(int i)
            {
                if (PropFallMath.IsSeatedOnAny(i, All, N, State, MinX, MaxX, MinZ, MaxZ, Bottom,
                    Height, MinX[i], MaxX[i], MinZ[i], MaxZ[i], Bottom[i], Height[i])) return true;
                return PropFallMath.IsSeatedOnSurface(Bottom[i], Height[i], 0f);
            }

            public void StepOnce()
            {
                Tick++;
                int next = 0;
                int checkedCount = QueueCount;
                for (int k = 0; k < checkedCount; k++)
                {
                    int i = QueueA[k];
                    int before = State[i];
                    bool supported = State[i] == PropFallMath.StateAbsorbed || Supported(i);
                    next = PropFallMath.ApplyRecheck(i, supported, MaxRechecks, State, V, Flag,
                        Rechecks, ChildStart, ChildList, QueueB, next);
                    if (before != PropFallMath.StateFalling && State[i] == PropFallMath.StateFalling)
                        StartedAtTick[i] = Tick;
                }
                int[] swap = QueueA; QueueA = QueueB; QueueB = swap;
                QueueCount = next;

                for (int i = 0; i < N; i++)
                {
                    if (State[i] != PropFallMath.StateFalling) continue;
                    float prev = Bottom[i];
                    float landing = PropFallMath.HighestLandingTop(i, All, N, State, MinX, MaxX,
                        MinZ, MaxZ, Bottom, Height, MinX[i], MaxX[i], MinZ[i], MaxZ[i], prev);
                    if (landing < 0f) landing = 0f;
                    float b;
                    float v;
                    bool landed = PropFallMath.StepFall(prev, V[i], landing, out b, out v);
                    Bottom[i] = b;
                    V[i] = v;
                    if (landed)
                    {
                        State[i] = PropFallMath.StateSettled;
                        SettledAtTick[i] = Tick;
                    }
                }
            }

            public void RunUntilIdle(int maxTicks)
            {
                for (int t = 0; t < maxTicks; t++)
                {
                    StepOnce();
                    bool falling = false;
                    for (int i = 0; i < N; i++) if (State[i] == PropFallMath.StateFalling) falling = true;
                    if (!falling && QueueCount == 0) return;
                }
            }
        }

        [Test]
        public void Cascade_ThreeHighStackFallsLayerByLayerAndStacksUpAgain()
        {
            // A(0) B(1) C(2) in one column, each 0.2 m tall, A on the ground. parents: B->A, C->B.
            var sim = new Sim(new[] { -1, 0, 1 }, new[] { 0f, 0.2f, 0.4f }, new[] { 0.2f, 0.2f, 0.2f },
                new[] { 0f, 0f, 0f });
            sim.Absorb(0);
            Assert.AreEqual(1, sim.QueueCount, "only the direct child is queued by the absorb");

            sim.StepOnce();
            Assert.AreEqual(PropFallMath.StateFalling, sim.State[1], "B is judged on tick 1");
            Assert.AreEqual(PropFallMath.StateResting, sim.State[2], "C is NOT judged yet");
            Assert.AreEqual(1, sim.QueueCount, "C waits in the next tick's queue");

            sim.StepOnce();
            Assert.AreEqual(PropFallMath.StateFalling, sim.State[2], "C sees B no longer holds it");
            Assert.AreEqual(1, sim.StartedAtTick[1]);
            Assert.AreEqual(2, sim.StartedAtTick[2]);

            sim.RunUntilIdle(200);
            Assert.AreEqual(PropFallMath.StateSettled, sim.State[1]);
            Assert.AreEqual(PropFallMath.StateSettled, sim.State[2]);
            Assert.AreEqual(0f, sim.Bottom[1], Tolerance, "B lands on the ground where A was");
            Assert.AreEqual(0.2f, sim.Bottom[2], Tolerance, "C lands on B's new top");
            // B drops 0.2 m: 6 steps (0.0109 * 21 = 0.229 >= 0.198), so it lands on tick 6.
            Assert.AreEqual(6, sim.SettledAtTick[1]);
        }

        [Test]
        public void Cascade_ChildStandingOnSomethingElseStaysPut()
        {
            // B stands on A (its parent) and on D (a second support); eating A must not drop it.
            var sim = new Sim(new[] { -1, 0, -1 }, new[] { 0f, 0.2f, 0f }, new[] { 0.2f, 0.2f, 0.2f },
                new[] { 0f, 0.3f, 0.6f });
            // widen D so it overlaps B's footprint (B spans -0.2..0.8)
            sim.MinX[2] = 0.5f; sim.MaxX[2] = 1.0f;
            sim.Absorb(0);
            sim.StepOnce();
            Assert.AreEqual(PropFallMath.StateResting, sim.State[1]);
            Assert.AreEqual(0.2f, sim.Bottom[1], Tolerance);
        }

        [Test]
        public void Cascade_FallingChildNotOnTheGroundLandsOnTheNextPropBelow()
        {
            // A (0..0.2) is eaten; B sits on A at 0.2..0.4; a low table E (0..0.1) stands
            // beside A at the same x so B falls past A's slot onto E's top at 0.1.
            var sim = new Sim(new[] { -1, 0, -1 }, new[] { 0f, 0.2f, 0f }, new[] { 0.2f, 0.2f, 0.1f },
                new[] { 0f, 0f, 0f });
            sim.Absorb(0);
            sim.RunUntilIdle(200);
            Assert.AreEqual(PropFallMath.StateSettled, sim.State[1]);
            Assert.AreEqual(0.1f, sim.Bottom[1], Tolerance);
        }

        [Test]
        public void Cascade_AbsorbingAFallingPropStopsItAndItsChildrenStillFall()
        {
            var sim = new Sim(new[] { -1, 0, 1 }, new[] { 0f, 0.2f, 0.4f }, new[] { 0.2f, 0.2f, 0.2f },
                new[] { 0f, 0f, 0f });
            sim.Absorb(0);
            sim.StepOnce();
            sim.StepOnce(); // B and C are both falling
            sim.Absorb(1);  // the ball eats B in mid-air
            Assert.AreEqual(PropFallMath.StateAbsorbed, sim.State[1]);
            sim.RunUntilIdle(200);
            Assert.AreEqual(PropFallMath.StateSettled, sim.State[2]);
            Assert.AreEqual(0f, sim.Bottom[2], Tolerance, "with B gone C reaches the ground");
        }

        [Test]
        public void Cascade_SameDataGivesTheSameTable()
        {
            int[] parents = { -1, 0, 0, 1, 1, 2 };
            var s1 = new int[7];
            var l1 = new int[6];
            var s2 = new int[7];
            var l2 = new int[6];
            PropFallMath.BuildChildTable(parents, 6, s1, l1);
            PropFallMath.BuildChildTable(parents, 6, s2, l2);
            CollectionAssert.AreEqual(s1, s2);
            CollectionAssert.AreEqual(l1, l2);
        }
    }
}
