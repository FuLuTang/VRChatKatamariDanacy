using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the rules that make the motion host cheap without changing what it computes: the
    /// prepared path table gives the original path steps' numbers, a far row that writes only on
    /// its own steps (and a stepped row that skips an unchanged pose) always writes the pose the
    /// every-frame version has at that frame, and the small decisions around them (near radius,
    /// write gating, re-classification stride, resume gap). Pure maths, no GameObject.
    /// </summary>
    public class MotionHostSchedulingTests
    {
        private const float Exact = 1e-6f;

        // --- prepared paths equal the original steps ---------------------------------------

        private static float[] ZigZag(int count, float step, float wobble)
        {
            float[] points = new float[count * 3];
            for (int i = 0; i < count; i++)
            {
                points[i * 3] = i * step;
                points[i * 3 + 1] = 0.2f * Mathf.Sin(i * 0.7f);
                points[i * 3 + 2] = (i % 2 == 0 ? 1f : -1f) * wobble + 0.1f * i;
            }
            return points;
        }

        private static float[] Ring(int count, float radius)
        {
            float[] points = new float[count * 3];
            for (int k = 0; k < count; k++)
            {
                float a = k * Mathf.PI * 2f / count;
                points[k * 3] = radius * Mathf.Cos(a);
                points[k * 3 + 1] = 0.05f * k;
                points[k * 3 + 2] = radius * Mathf.Sin(a);
            }
            return points;
        }

        private static void AssertPreparedMatchesOriginal(int motionClass, float[] points, int count,
            bool closed, float speedMps, float halfHeightM, float dtFrames, int steps, int resetEvery)
        {
            Vector3 spawn = new Vector3(points[3] + 0.01f, points[4] + halfHeightM, points[5]);
            float[] table = new float[MotionMath.PathTableFloats(count, closed)];
            Assert.IsTrue(MotionMath.BuildPathTable(points, count, closed, table));

            float[] original = new float[MotionHostMath.StateFloats(motionClass)];
            float[] prepared = new float[MotionHostMath.StateFloats(motionClass)];
            MotionHostMath.ResetState(motionClass, original, 0);
            MotionHostMath.ResetState(motionClass, prepared, 0);
            for (int s = 0; s < steps; s++)
            {
                if (resetEvery > 0 && s % resetEvery == resetEvery - 1)
                {
                    // A revive: state back to the start, the hint with it.
                    MotionHostMath.ResetState(motionClass, original, 0);
                    MotionHostMath.ResetState(motionClass, prepared, 0);
                }
                Vector3 expectedPosition; Vector3 expectedEuler;
                bool expectedOk = MotionHostMath.TryStep(motionClass, original, dtFrames, 0, halfHeightM,
                    false, true, speedMps, 4f, points, count, closed, spawn,
                    out expectedPosition, out expectedEuler);
                Vector3 actualPosition; Vector3 actualEuler;
                bool actualOk = MotionHostMath.TryStepPreparedPath(motionClass, prepared, dtFrames, spawn,
                    points, table, speedMps, halfHeightM, out actualPosition, out actualEuler);
                Assert.AreEqual(expectedOk, actualOk, "step " + s);
                if (!expectedOk) return;
                string at = "class " + motionClass + " step " + s;
                Assert.AreEqual(expectedPosition.x, actualPosition.x, Exact, at + " x");
                Assert.AreEqual(expectedPosition.y, actualPosition.y, Exact, at + " y");
                Assert.AreEqual(expectedPosition.z, actualPosition.z, Exact, at + " z");
                Assert.AreEqual(expectedEuler.x, actualEuler.x, Exact, at + " pitch");
                Assert.AreEqual(expectedEuler.y, actualEuler.y, Exact, at + " yaw");
                Assert.AreEqual(expectedEuler.z, actualEuler.z, Exact, at + " roll");
                Assert.AreEqual(original[0], prepared[0], Exact, at + " clock");
                Assert.AreEqual(original[1], prepared[1], Exact, at + " distance");
            }
        }

        [Test]
        public void PreparedOpenPathMatchesOriginalForEveryPathClass()
        {
            // 50 points like Cancer's p23: the whole walk, several end-to-end sweeps.
            float[] points = ZigZag(50, 0.35f, 0.3f);
            int[] classes = { MotionHostMath.ClassPathCollision, MotionHostMath.ClassSlopingPath,
                MotionHostMath.ClassBackAndForth };
            for (int c = 0; c < classes.Length; c++)
            {
                AssertPreparedMatchesOriginal(classes[c], points, 50, false, 0.366f, 0.11f, 2f, 4000, 0);
                AssertPreparedMatchesOriginal(classes[c], points, 50, false, 1.7f, 0.11f, 1f, 4000, 0);
            }
        }

        [Test]
        public void PreparedClosedPathMatchesOriginalIncludingTheWrap()
        {
            float[] ring = Ring(12, 1.4f);
            AssertPreparedMatchesOriginal(MotionHostMath.ClassPathCollision, ring, 12, true, 0.9f, 0.11f, 2f, 3000, 0);
            AssertPreparedMatchesOriginal(MotionHostMath.ClassSlopingPath, ring, 12, true, 0.9f, 0.11f, 2f, 3000, 0);
            AssertPreparedMatchesOriginal(MotionHostMath.ClassPathCollision, Ring(59, 3f), 59, true, 4f, 0.2f, 2f, 3000, 0);
        }

        [Test]
        public void PreparedPathMatchesOriginalAfterRevivesAndBigSteps()
        {
            float[] points = ZigZag(59, 0.3f, 0.25f);
            AssertPreparedMatchesOriginal(MotionHostMath.ClassSlopingPath, points, 59, false, 0.5f, 0.1f, 2f, 2000, 137);
            // A step far bigger than the host's (a hitch): the hint must not be trusted blindly.
            AssertPreparedMatchesOriginal(MotionHostMath.ClassPathCollision, points, 59, false, 40f, 0.1f, 2f, 500, 0);
        }

        [Test]
        public void BackAndForthOnAClosedPathUsesTheOpenTable()
        {
            float[] ring = Ring(12, 1.4f);
            // The original ignores the closed flag for BackAndForth; the host gives it an open table.
            AssertPreparedMatchesOriginal(MotionHostMath.ClassBackAndForth, ring, 12, false, 0.6f, 0.1f, 2f, 2000, 0);
        }

        [Test]
        public void PathTableRefusesWhatTheOriginalRefusesOnEveryCall()
        {
            float[] shortSegment = { 0f, 0f, 0f, 0.001f, 0f, 0f, 1f, 0f, 0f };
            float[] table = new float[MotionMath.PathTableFloats(3, false)];
            Assert.IsFalse(MotionMath.BuildPathTable(shortSegment, 3, false, table));
            Assert.AreEqual(0f, table[0], "valid flag stays 0");
            float[] nan = { 0f, 0f, 0f, float.NaN, 0f, 0f };
            Assert.IsFalse(MotionMath.BuildPathTable(nan, 2, false, new float[MotionMath.PathTableFloats(2, false)]));
            Assert.IsFalse(MotionMath.BuildPathTable(null, 2, false, new float[8]));
            Assert.IsFalse(MotionMath.PathRowInputsOk(-1f, 0.1f, Vector3.zero));
            Assert.IsFalse(MotionMath.PathRowInputsOk(1f, float.NaN, Vector3.zero));
            Assert.IsFalse(MotionMath.PathRowInputsOk(1f, 0.1f, new Vector3(float.NaN, 0f, 0f)));
            Assert.IsTrue(MotionMath.PathRowInputsOk(0f, 0f, Vector3.zero));
        }

        [Test]
        public void PathClassesCarryTheSegmentHintSlot()
        {
            Assert.AreEqual(MotionMath.PreparedPathStateFloats, MotionHostMath.StateFloats(MotionHostMath.ClassPathCollision));
            Assert.AreEqual(MotionMath.PreparedPathStateFloats, MotionHostMath.StateFloats(MotionHostMath.ClassSlopingPath));
            Assert.AreEqual(MotionMath.PreparedPathStateFloats, MotionHostMath.StateFloats(MotionHostMath.ClassBackAndForth));
            float[] state = { 5f, 6f, 7f };
            MotionHostMath.ResetState(MotionHostMath.ClassPathCollision, state, 0);
            Assert.AreEqual(0f, state[0]);
            Assert.AreEqual(-1f, state[1]);
            Assert.AreEqual(0f, state[2], "the hint is reset with the state");
        }

        // --- far-row write decimation never changes the pose that is written --------------

        // A row the way MotionHost.DriveRow runs it, minus the Transform: steps on the
        // 1/rate schedule, an interpolated or stepped pose, written when ShouldWritePose says so.
        private sealed class RowSim
        {
            public int MotionClass;
            public float[] State;
            public float Interval;
            public float TierFrames;
            public bool Smooth;
            public bool Near;
            public bool Decimated;
            public Vector3 Spawn = new Vector3(2f, 0.5f, -3f);
            public Vector3 PrevPos, NextPos, WrittenPos;
            public float PrevYaw, NextYaw, WrittenYaw;
            public float NextDue, StepTime;
            public bool Wrote, HasLast;
            public Vector3 LastOffset, LastEuler;
            public int Steps, Writes;
            public float[] Points;
            public float[] Table;
            public int Seed = 12345;

            public RowSim(int motionClass, float rate, bool decimated, float[] points)
            {
                MotionClass = motionClass;
                Decimated = decimated;
                Points = points;
                State = new float[MotionHostMath.StateFloats(motionClass)];
                MotionHostMath.ResetState(motionClass, State, Seed);
                Interval = MotionHostMath.IntervalSeconds(rate);
                TierFrames = MotionMath.GameFramesPerSecond / rate;
                Smooth = MotionHostMath.ShouldSmooth(rate);
                Near = !decimated;
                PrevPos = NextPos = WrittenPos = Spawn;
                if (points != null)
                {
                    Table = new float[MotionMath.PathTableFloats(points.Length / 3, false)];
                    Assert.IsTrue(MotionMath.BuildPathTable(points, points.Length / 3, false, Table));
                }
            }

            // Returns whether the row wrote this frame.
            public bool Frame(float now, int frame)
            {
                bool stepRan = false;
                bool changed = false;
                int guard = 0;
                while (now >= NextDue && guard < 3)
                {
                    guard++;
                    stepRan = true;
                    StepTime = NextDue;
                    NextDue = StepTime + Interval;
                    if (Step()) changed = true;
                }
                if (now >= NextDue) NextDue = MotionHostMath.NextDue(NextDue, now, Interval);

                if (!MotionHostMath.ShouldWritePose(Wrote, Near, Smooth, Smooth ? stepRan : changed,
                    false, false)) return false;
                Vector3 position = NextPos;
                float yaw = NextYaw;
                if (Smooth)
                {
                    float f = MotionHostMath.SmoothFraction(now, StepTime, Interval);
                    position = Vector3.Lerp(PrevPos, NextPos, f);
                    yaw = PrevYaw + (NextYaw - PrevYaw) * f;
                }
                WrittenPos = position;
                WrittenYaw = yaw;
                Wrote = true;
                Writes++;
                return true;
            }

            private bool Step()
            {
                Steps++;
                float remaining = TierFrames;
                Vector3 offset = Vector3.zero;
                Vector3 euler = Vector3.zero;
                bool ok = false;
                while (remaining > 0.0001f)
                {
                    float chunk = remaining > 2f ? 2f : remaining;
                    remaining -= chunk;
                    if (Table != null)
                        ok = MotionHostMath.TryStepPreparedPath(MotionClass, State, chunk, Spawn, Points,
                            Table, 0.4f, 0.11f, out offset, out euler);
                    else
                        ok = MotionHostMath.TryStep(MotionClass, State, chunk, Seed, 0.11f, false, true,
                            0.3f, 4f, null, 0, false, Spawn, out offset, out euler);
                    if (!ok) break;
                }
                if (!ok) return false;
                if (!Smooth && HasLast && MotionHostMath.SamePoseRequest(offset, euler, LastOffset, LastEuler))
                    return false;
                Vector3 position; Quaternion rotation;
                MotionHostMath.StandalonePose(MotionClass, Spawn, Quaternion.identity, offset, euler,
                    out position, out rotation);
                if (!Smooth) { LastOffset = offset; LastEuler = euler; HasLast = true; }
                PrevPos = NextPos; PrevYaw = NextYaw;
                NextPos = position; NextYaw = euler.y;
                return true;
            }
        }

        private static void RunBothVersions(int motionClass, float rate, float[] points, int frames,
            float fps, bool expectFewerWrites)
        {
            RowSim full = new RowSim(motionClass, rate, false, points);
            RowSim far = new RowSim(motionClass, rate, true, points);
            // After the first frame the far row is classified far.
            for (int f = 0; f < frames; f++)
            {
                float now = 10f + f / fps;
                bool fullWrote = full.Frame(now, f);
                if (f == 0) far.Near = true; else far.Near = false;
                bool farWrote = far.Frame(now, f);
                Assert.AreEqual(full.Steps, far.Steps, "same schedule, same steps at frame " + f);
                Assert.AreEqual(full.State[0], far.State[0], 0f, "program clock at frame " + f);
                if (farWrote)
                {
                    // The far row writes the pose the every-frame row has at this very frame.
                    Assert.AreEqual(full.WrittenPos.x, far.WrittenPos.x, Exact, "x at frame " + f);
                    Assert.AreEqual(full.WrittenPos.y, far.WrittenPos.y, Exact, "y at frame " + f);
                    Assert.AreEqual(full.WrittenPos.z, far.WrittenPos.z, Exact, "z at frame " + f);
                    Assert.AreEqual(full.WrittenYaw, far.WrittenYaw, Exact, "yaw at frame " + f);
                }
                Assert.IsTrue(fullWrote || !farWrote || !full.Smooth, "a far row never writes more often");
                if (!far.Smooth)
                {
                    // A stepped row holds its pose: skipping an unchanged pose loses nothing.
                    Assert.AreEqual(full.WrittenPos.x, far.WrittenPos.x, Exact, "held x at frame " + f);
                    Assert.AreEqual(full.WrittenPos.y, far.WrittenPos.y, Exact, "held y at frame " + f);
                    Assert.AreEqual(full.WrittenPos.z, far.WrittenPos.z, Exact, "held z at frame " + f);
                }
            }
            if (expectFewerWrites) Assert.Less(far.Writes, full.Writes, "class " + motionClass);
        }

        [Test]
        public void FarSmoothRowsWriteTheCurrentTimePoseOnlyOnStepFrames()
        {
            // 15 Hz walker and path crab, 10 Hz bob, 5 Hz spin, rendered at 60 and 30 fps.
            float[] points = ZigZag(50, 0.35f, 0.3f);
            float[] fpsList = { 60f, 30f, 144f };
            for (int i = 0; i < fpsList.Length; i++)
            {
                RunBothVersions(MotionHostMath.ClassSporadicWalk, 15f, null, 900, fpsList[i], true);
                RunBothVersions(MotionHostMath.ClassPathCollision, 15f, points, 900, fpsList[i], true);
                RunBothVersions(MotionHostMath.ClassBob, 10f, null, 900, fpsList[i], true);
                RunBothVersions(MotionHostMath.ClassSpin, 5f, null, 900, fpsList[i], i != 1);
            }
        }

        [Test]
        public void SteppedRowsSkipUnchangedPosesWithoutLosingAny()
        {
            // Hop (30 Hz): idle most of the time; whack (30 Hz) holds at the ends.
            RunBothVersions(MotionHostMath.ClassHop, 30f, null, 6000, 60f, false);
            RunBothVersions(MotionHostMath.ClassWhackAMole, 30f, null, 6000, 60f, false);
            RowSim hop = new RowSim(MotionHostMath.ClassHop, 30f, false, null);
            for (int f = 0; f < 6000; f++) hop.Frame(10f + f / 60f, f);
            Assert.Less(hop.Writes, hop.Steps / 4, "a hop row writes only while it moves");
        }

        // --- the decisions ---------------------------------------------------------------

        [Test]
        public void WriteGatingTruthTable()
        {
            // First write is unconditional.
            Assert.IsTrue(MotionHostMath.ShouldWritePose(false, false, false, false, false, false));
            // A step that changed the pose writes, near or far.
            Assert.IsTrue(MotionHostMath.ShouldWritePose(true, false, true, true, false, false));
            Assert.IsTrue(MotionHostMath.ShouldWritePose(true, true, false, true, false, false));
            // Smooth rows interpolate every frame only while near.
            Assert.IsTrue(MotionHostMath.ShouldWritePose(true, true, true, false, false, false));
            Assert.IsFalse(MotionHostMath.ShouldWritePose(true, false, true, false, false, false));
            // A stepped row with nothing new never writes.
            Assert.IsFalse(MotionHostMath.ShouldWritePose(true, true, false, false, false, false));
            Assert.IsFalse(MotionHostMath.ShouldWritePose(true, false, false, false, false, false));
            // A rider follows its parent's write, near or far; without it, only its own rules.
            Assert.IsTrue(MotionHostMath.ShouldWritePose(true, false, false, false, true, true));
            Assert.IsFalse(MotionHostMath.ShouldWritePose(true, true, false, false, true, false));
            Assert.IsFalse(MotionHostMath.ShouldWritePose(true, false, true, false, true, false));
            Assert.IsTrue(MotionHostMath.ShouldWritePose(true, true, true, false, true, false));
        }

        [Test]
        public void NearRadiusFollowsTheBallAndRepairsBadInput()
        {
            Assert.AreEqual(6.3f, MotionHostMath.NearRadiusM(0.3f, 6f), 1e-5f);
            Assert.AreEqual(14f, MotionHostMath.NearRadiusM(8f, 6f), 1e-5f, "a big ball widens it");
            Assert.AreEqual(6f, MotionHostMath.NearRadiusM(float.NaN, 6f), 1e-5f);
            Assert.AreEqual(6f, MotionHostMath.NearRadiusM(-2f, 6f), 1e-5f);
            Assert.AreEqual(MotionHostMath.DefaultNearMarginM, MotionHostMath.NearRadiusM(0f, -1f), 1e-5f);
            Assert.AreEqual(MotionHostMath.DefaultNearMarginM, MotionHostMath.NearRadiusM(0f, float.NaN), 1e-5f);
        }

        [Test]
        public void NearMarginCoversWhatReadsMoverTransforms()
        {
            // Absorb activation 1.6 m, blocking margin 0.8 m, stickiness 0.25 m, a 2 m half diagonal,
            // and the ball's travel between two re-tests (6 frames at 30 fps, 12 m/s).
            float travel = 12f * MotionHostMath.DefaultClassifyStrideFrames / 30f;
            Assert.GreaterOrEqual(MotionHostMath.DefaultNearMarginM, 1.6f + 0.8f + 0.25f + 2f + travel - 3f,
                "default margin leaves the reach of the absorb/blocking scans inside the near zone");
            Assert.Greater(MotionHostMath.DefaultNearMarginM, 1.6f + 0.8f + 0.25f + 2f);
        }

        [Test]
        public void FarClassificationUsesSquaredDistanceAndKeepsStateOnBadInput()
        {
            float r2 = 6.3f * 6.3f;
            Assert.IsFalse(MotionHostMath.UpdateFar(true, 6.2f * 6.2f, r2));
            Assert.IsTrue(MotionHostMath.UpdateFar(false, 6.4f * 6.4f, r2));
            Assert.IsTrue(MotionHostMath.UpdateFar(true, float.NaN, r2));
            Assert.IsFalse(MotionHostMath.UpdateFar(false, float.NaN, r2));
            Assert.IsFalse(MotionHostMath.UpdateFar(true, 1e9f, float.MaxValue), "no observer = nothing far");
        }

        [Test]
        public void SquaredDriftTestAgreesWithTheOriginal()
        {
            Vector3 written = new Vector3(1f, 2f, 3f);
            float[] offsets = { 0f, 0.001f, 0.0199f, 0.0201f, 0.1f, 5f };
            float[] tolerances = { 0.02f, 0.5f, 0f, -1f, float.NaN };
            for (int t = 0; t < tolerances.Length; t++)
            {
                float sq = MotionHostMath.DriftToleranceSquared(tolerances[t]);
                for (int o = 0; o < offsets.Length; o++)
                {
                    Vector3 current = written + new Vector3(0f, -offsets[o], 0f);
                    Assert.AreEqual(MotionHostMath.HasDrifted(written, current, tolerances[t]),
                        MotionHostMath.HasDriftedSquared(written, current, sq),
                        "tolerance " + tolerances[t] + " offset " + offsets[o]);
                }
                Assert.IsTrue(MotionHostMath.HasDriftedSquared(written, new Vector3(float.NaN, 0f, 0f), sq));
                Assert.IsTrue(MotionHostMath.HasDriftedSquared(written, new Vector3(float.PositiveInfinity, 0f, 0f), sq));
            }
        }

        [Test]
        public void EveryRowIsClassifiedOncePerStride()
        {
            int[] rowCounts = { 1, 7, 155, 156 };
            int[] strides = { 0, 1, 6, 12, 200 };
            for (int r = 0; r < rowCounts.Length; r++)
            {
                for (int s = 0; s < strides.Length; s++)
                {
                    int rows = rowCounts[r];
                    int stride = strides[s];
                    int batch = MotionHostMath.ClassifyBatchSize(rows, stride);
                    int[] seen = new int[rows];
                    int cursor = 0;
                    int frames = stride < 1 ? 1 : stride;
                    for (int f = 0; f < frames; f++)
                    {
                        for (int k = 0; k < batch; k++)
                        {
                            seen[cursor]++;
                            cursor++;
                            if (cursor >= rows) cursor = 0;
                        }
                    }
                    for (int i = 0; i < rows; i++)
                        Assert.GreaterOrEqual(seen[i], 1, "row " + i + " rows " + rows + " stride " + stride);
                }
            }
            Assert.AreEqual(0, MotionHostMath.ClassifyBatchSize(0, 6));
            Assert.AreEqual(26, MotionHostMath.ClassifyBatchSize(155, 6));
        }

        [Test]
        public void ResumeGapIsALongPauseNotAHitch()
        {
            Assert.IsFalse(MotionHostMath.IsResumeGap(10f, 10.033f, 0.5f));
            Assert.IsFalse(MotionHostMath.IsResumeGap(10f, 10.2f, 0.5f), "a 200 ms hitch is not a resume");
            Assert.IsTrue(MotionHostMath.IsResumeGap(10f, 70f, 0.5f), "a parked island coming back");
            Assert.IsFalse(MotionHostMath.IsResumeGap(10f, float.NaN, 0.5f));
        }

        [Test]
        public void SamePoseRequestIsExactNotApproximate()
        {
            Vector3 a = new Vector3(0f, 0.01f, 0f);
            Assert.IsTrue(MotionHostMath.SamePoseRequest(a, Vector3.zero, a, Vector3.zero));
            Assert.IsFalse(MotionHostMath.SamePoseRequest(a, Vector3.zero, a + new Vector3(0f, 1e-7f, 0f), Vector3.zero),
                "Vector3's == would call this equal; one more ulp is a new pose");
            Assert.IsFalse(MotionHostMath.SamePoseRequest(a, Vector3.zero, a, new Vector3(0f, 0.1f, 0f)));
        }

        [Test]
        public void StandalonePoseInEqualsStandalonePose()
        {
            int[] classes = { MotionHostMath.ClassBob, MotionHostMath.ClassSporadicWalk,
                MotionHostMath.ClassClouds, MotionHostMath.ClassPathCollision };
            Quaternion spawnRotation = MotionHostMath.EulerToQuaternion(new Vector3(0.1f, 0.7f, 0f));
            Vector3 spawn = new Vector3(1f, 2f, 3f);
            Vector3 offset = new Vector3(0.3f, 0.2f, -0.4f);
            Vector3 euler = new Vector3(0f, 1.1f, 0f);
            for (int c = 0; c < classes.Length; c++)
            {
                Vector3 pa; Quaternion ra; Vector3 pb; Quaternion rb;
                MotionHostMath.StandalonePose(classes[c], spawn, spawnRotation, offset, euler, out pa, out ra);
                MotionHostMath.StandalonePoseIn(MotionHostMath.SpaceOf(classes[c]),
                    MotionHostMath.KeepsSpawnRotation(classes[c]), spawn, spawnRotation, offset, euler,
                    out pb, out rb);
                Assert.AreEqual(pa.x, pb.x, 0f); Assert.AreEqual(pa.y, pb.y, 0f); Assert.AreEqual(pa.z, pb.z, 0f);
                Assert.AreEqual(ra.x, rb.x, 0f); Assert.AreEqual(ra.y, rb.y, 0f);
                Assert.AreEqual(ra.z, rb.z, 0f); Assert.AreEqual(ra.w, rb.w, 0f);
            }
        }
    }
}
