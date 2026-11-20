using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for <see cref="MotionHostMath"/> and <see cref="MotionMath.SeedForPhase01"/>:
    /// the pure half of the motion host (class dispatch, composition, scheduling, culling).
    /// No GameObject is created, so these run on a plain .NET runtime; the host component itself
    /// (<see cref="MotionHost"/>) needs Unity and is covered by the operator checklist in
    /// Docs/Design/PickupAbsorb/MOTION_HOST.md.
    /// </summary>
    public class MotionHostMathTests
    {
        private const float Tol = 1e-4f;

        // --- phase override -------------------------------------------------

        [Test]
        public void SeedForPhaseRoundTripsThroughPhase01()
        {
            float[] phases = { 0f, 0.001f, 0.25f, 0.5f, 0.731f, 0.999f };
            for (int i = 0; i < phases.Length; i++)
            {
                int seed = MotionMath.SeedForPhase01(phases[i]);
                Assert.AreEqual(phases[i], MotionMath.Phase01(seed), 2e-6f, "phase " + phases[i]);
            }
        }

        [Test]
        public void SeedForPhaseTreatsOneAsZeroAndBadInputAsZero()
        {
            Assert.AreEqual(MotionMath.SeedForPhase01(0f), MotionMath.SeedForPhase01(1f));
            Assert.AreEqual(MotionMath.SeedForPhase01(0f), MotionMath.SeedForPhase01(float.NaN));
            Assert.AreEqual(MotionMath.SeedForPhase01(0f), MotionMath.SeedForPhase01(-3f));
        }

        [Test]
        public void OverrideSeedStartsASpinnerAtThatFractionOfItsPeriod()
        {
            // Spin's reset clock is phase * (2*pi / 0.05) frames (D-12).
            float[] state = new float[MotionMath.SpinStateFloats];
            MotionMath.ResetSpin(state, MotionMath.SeedForPhase01(0.5f));
            Assert.AreEqual(0.5f * MotionMath.TwoPi / MotionMath.SpinRadiansPerFrame, state[0], 0.01f);
        }

        // --- class table ------------------------------------------------------

        [Test]
        public void ClassNamesMapToDistinctCodesAndUnknownIsNone()
        {
            string[] names = { "spin", "flip", "bob", "sway", "whack_a_mole", "hop", "sporadic_walk",
                "clouds", "wait_for_player", "path_collision", "sloping_path", "back_and_forth",
                "dragged_along" };
            bool[] seen = new bool[MotionHostMath.ClassCount];
            for (int i = 0; i < names.Length; i++)
            {
                int code = MotionHostMath.ClassFromName(names[i]);
                Assert.Greater(code, 0, names[i]);
                Assert.IsFalse(seen[code], "duplicate code for " + names[i]);
                seen[code] = true;
            }
            Assert.AreEqual(MotionHostMath.ClassNone, MotionHostMath.ClassFromName("teleport"));
            Assert.AreEqual(MotionHostMath.ClassNone, MotionHostMath.ClassFromName(null));
        }

        [Test]
        public void TiersFollowTheDisplacementTable()
        {
            Assert.AreEqual(30f, MotionHostMath.DefaultUpdatesPerSecond(MotionHostMath.ClassHop));
            Assert.AreEqual(30f, MotionHostMath.DefaultUpdatesPerSecond(MotionHostMath.ClassWhackAMole));
            Assert.AreEqual(15f, MotionHostMath.DefaultUpdatesPerSecond(MotionHostMath.ClassSway));
            Assert.AreEqual(15f, MotionHostMath.DefaultUpdatesPerSecond(MotionHostMath.ClassPathCollision));
            Assert.AreEqual(10f, MotionHostMath.DefaultUpdatesPerSecond(MotionHostMath.ClassBob));
            Assert.AreEqual(5f, MotionHostMath.DefaultUpdatesPerSecond(MotionHostMath.ClassSpin));
            Assert.AreEqual(0f, MotionHostMath.DefaultUpdatesPerSecond(MotionHostMath.ClassDraggedAlong));
            Assert.IsFalse(MotionHostMath.ShouldSmooth(30f), "hop/whack are written as stepped");
            Assert.IsTrue(MotionHostMath.ShouldSmooth(15f));
        }

        [Test]
        public void StateSizesMatchMotionMath()
        {
            Assert.AreEqual(MotionMath.HopStateFloats, MotionHostMath.StateFloats(MotionHostMath.ClassHop));
            Assert.AreEqual(MotionMath.WanderStateFloats, MotionHostMath.StateFloats(MotionHostMath.ClassClouds));
            Assert.AreEqual(0, MotionHostMath.StateFloats(MotionHostMath.ClassNone));
        }

        // --- dispatch is MotionMath, not a copy ---------------------------------

        [Test]
        public void SpinDispatchEqualsDirectCall()
        {
            float[] a = new float[MotionMath.SpinStateFloats];
            float[] b = new float[MotionMath.SpinStateFloats];
            MotionHostMath.ResetState(MotionHostMath.ClassSpin, a, 77);
            MotionMath.ResetSpin(b, 77);
            Vector3 o1; Vector3 e1; Vector3 o2; Vector3 e2;
            Assert.IsTrue(MotionHostMath.TryStep(MotionHostMath.ClassSpin, a, 2f, 77, 0.1f, true, true,
                0f, 4f, null, 0, false, Vector3.zero, out o1, out e1));
            Assert.IsTrue(MotionMath.TryStepSpin(b, 2f, out o2, out e2));
            Assert.AreEqual(e2.y, e1.y, 0f);
        }

        [Test]
        public void SwayDispatchAppliesTranslationFlag()
        {
            float[] a = new float[MotionMath.SwayStateFloats];
            MotionHostMath.ResetState(MotionHostMath.ClassSway, a, 5);
            Vector3 offset; Vector3 euler;
            Assert.IsTrue(MotionHostMath.TryStep(MotionHostMath.ClassSway, a, 7f, 5, 1.2f, false, true,
                0f, 4f, null, 0, false, Vector3.zero, out offset, out euler));
            Assert.AreEqual(0f, offset.x, 0f);
            Assert.AreEqual(0f, offset.y, 0f);
            Assert.AreNotEqual(0f, euler.z);
        }

        [Test]
        public void ClassWithoutProgramRefusesToStep()
        {
            Vector3 o; Vector3 e;
            Assert.IsFalse(MotionHostMath.TryStep(MotionHostMath.ClassNone, new float[0], 1f, 0, 1f,
                true, true, 0f, 4f, null, 0, false, Vector3.zero, out o, out e));
            Assert.IsFalse(MotionHostMath.TryStep(MotionHostMath.ClassWaitForPlayer, new float[1], 1f, 0,
                1f, true, true, 0f, 4f, null, 0, false, Vector3.zero, out o, out e));
        }

        [Test]
        public void PathClassStepsAlongItsPolylineAtTheResolvedSpeed()
        {
            // 2-point path along +X; 0.3 m/s for 30 frames = 0.3 m. The prop starts at the
            // nearest point (x = 0) with its centre half a height (0.05) above the path point.
            float[] points = { 0f, 1f, 0f, 2f, 1f, 0f };
            float[] state = new float[MotionMath.BackAndForthNoYawStateFloats];
            MotionHostMath.ResetState(MotionHostMath.ClassBackAndForth, state, 0);
            Vector3 position = Vector3.zero; Vector3 euler;
            for (int i = 0; i < 15; i++)
            {
                Assert.IsTrue(MotionHostMath.TryStep(MotionHostMath.ClassBackAndForth, state, 2f, 0, 0.05f,
                    true, true, 0.3f, 4f, points, 2, false, new Vector3(0f, 1f, 0f), out position, out euler));
            }
            Assert.AreEqual(0.3f, position.x, 1e-3f);
            Assert.AreEqual(1.05f, position.y, Tol);
        }

        [Test]
        public void PathClassesPutTheBodyOnThePathNotUnderIt()
        {
            // Ground-level path points at y = -0.6563 (Cancer p23): a prop with half height 0.11 has its
            // centre 0.11 above them, so its bottom rests on the path. PS2 的 −b 由 MotionMath 转换。
            float[] points = { 0f, -0.6563f, 0f, 2f, -0.6563f, 0f };
            int[] classes = { MotionHostMath.ClassPathCollision, MotionHostMath.ClassSlopingPath,
                              MotionHostMath.ClassBackAndForth };
            for (int c = 0; c < classes.Length; c++)
            {
                float[] state = new float[MotionHostMath.StateFloats(classes[c])];
                MotionHostMath.ResetState(classes[c], state, 0);
                Vector3 position; Vector3 euler;
                Assert.IsTrue(MotionHostMath.TryStep(classes[c], state, 2f, 0, 0.11f, true, true,
                    0.244f, 4f, points, 2, false, new Vector3(0.5f, -0.5f, 0f), out position, out euler),
                    "class " + classes[c]);
                Assert.AreEqual(-0.6563f + 0.11f, position.y, 1e-4f, "class " + classes[c]);
            }
        }

        [Test]
        public void OnlyThePathClassesHoldTheWalkAnimation()
        {
            // The original starts every path crab in MOVING and the path program never pauses
            // it; SporadicWalk alternates walk and pause on its own state machine.
            Assert.IsTrue(MotionHostMath.HoldsMovingAnimation(MotionHostMath.ClassPathCollision));
            Assert.IsTrue(MotionHostMath.HoldsMovingAnimation(MotionHostMath.ClassSlopingPath));
            Assert.IsTrue(MotionHostMath.HoldsMovingAnimation(MotionHostMath.ClassBackAndForth));
            Assert.IsFalse(MotionHostMath.HoldsMovingAnimation(MotionHostMath.ClassSporadicWalk));
            Assert.IsFalse(MotionHostMath.HoldsMovingAnimation(MotionHostMath.ClassHop));
            Assert.IsFalse(MotionHostMath.HoldsMovingAnimation(MotionHostMath.ClassWaitForPlayer));
            Assert.IsFalse(MotionHostMath.HoldsMovingAnimation(MotionHostMath.ClassNone));
        }

        [Test]
        public void ARiderOfAPathParentFollowsTheParentAndNeverCollapsesToTheOrigin()
        {
            // Cancer's coconut_crab_011 (PathCollision, p23) carries color_tv_001: the parent spawns
            // at (1.418, -0.558, -0.260), the rider 0.395 m above and 0.076 m beside it. After the
            // parent's first step both are on the path ring and the rider keeps its offset from the
            // parent (rotated by the parent's yaw change); neither is the zero vector.
            float[] ring = new float[12 * 3];
            for (int k = 0; k < 12; k++)
            {
                float a = k * Mathf.PI * 2f / 12f;
                ring[k * 3] = 1.4f * Mathf.Cos(a);
                ring[k * 3 + 1] = -0.6563f;
                ring[k * 3 + 2] = 1.4f * Mathf.Sin(a);
            }
            Vector3 parentSpawn = new Vector3(1.4184f, -0.558f, -0.2596f);
            Vector3 riderSpawn = new Vector3(1.3427f, -0.1631f, -0.2678f);
            Quaternion spawnRotation = Quaternion.identity;

            float[] state = new float[MotionMath.PathCollisionStateFloats];
            MotionHostMath.ResetState(MotionHostMath.ClassPathCollision, state, 0);
            Vector3 offsetOrPosition = Vector3.zero; Vector3 euler = Vector3.zero;
            Assert.IsTrue(MotionHostMath.TryStep(MotionHostMath.ClassPathCollision, state, 2f, 0, 0.11f,
                true, true, 0.244f, 4f, ring, 12, true, parentSpawn, out offsetOrPosition, out euler));

            Vector3 parentPosition; Quaternion parentRotation;
            MotionHostMath.StandalonePose(MotionHostMath.ClassPathCollision, parentSpawn, spawnRotation,
                offsetOrPosition, euler, out parentPosition, out parentRotation);
            Assert.Greater(new Vector3(parentPosition.x, 0f, parentPosition.z).magnitude, 1f,
                "the parent is on the ring, not at the origin");
            Assert.AreEqual(-0.6563f + 0.11f, parentPosition.y, 1e-3f, "centre one half height above the path");

            Vector3 riderPosition; Quaternion riderRotation;
            MotionHostMath.CarryByParent(MotionHostMath.InheritFull, parentPosition, parentRotation,
                parentSpawn, MotionHostMath.Conjugate(spawnRotation), riderSpawn, Quaternion.identity,
                out riderPosition, out riderRotation);
            Assert.AreEqual((riderSpawn - parentSpawn).magnitude, (riderPosition - parentPosition).magnitude, 1e-3f,
                "a full-inherit rider keeps its distance to the parent");
            Assert.AreEqual(riderSpawn.y - parentSpawn.y, riderPosition.y - parentPosition.y, 1e-3f,
                "a yaw-only parent turn does not tilt the rider");
        }

        // --- trigger gate vs wake radius ----------------------------------------

        [Test]
        public void TriggerGateUsesItsOwnRadiusAndNotTheWakeRadius()
        {
            // Cancer: motionID 0x00, size group 0, gate 2 m; the 12 m wake radius never decides it.
            Assert.IsTrue(MotionHostMath.IsTriggered(0x00, 1.9f, 2f, 0));
            Assert.IsFalse(MotionHostMath.IsTriggered(0x00, 2.1f, 2f, 0));
            Assert.IsFalse(MotionHostMath.IsTriggered(0x00, 5f, 2f, 0), "12 m is not the gate");
        }

        [Test]
        public void TriggerGateMatchesMotionMathAtTheDefaultRadius()
        {
            float[] state = new float[MotionMath.WaitForPlayerStateFloats];
            for (int g = 0; g <= 4; g++)
            {
                float radius = MotionMath.PanicRadiusM(g);
                for (int d = 0; d < 12; d++)
                {
                    float distance = d;
                    bool triggered; Vector3 o; Vector3 e;
                    MotionMath.TryStepWaitForPlayer(state, 0x00, distance, g, true, false, out triggered, out o, out e);
                    Assert.AreEqual(triggered, MotionHostMath.IsTriggered(0x00, distance, radius, g),
                        "group " + g + " distance " + d);
                }
            }
        }

        [Test]
        public void MotionId04NeverTriggersUntilTheOriginalConditionIsMeasured()
        {
            Assert.IsFalse(MotionHostMath.IsTriggered(0x04, 0.1f, 2f, 0));
        }

        // --- time -----------------------------------------------------------------

        [Test]
        public void StepDtIsGameFramesClampedToTwo()
        {
            Assert.AreEqual(1f, MotionHostMath.StepDtFrames(1f / 30f), Tol);
            Assert.AreEqual(2f, MotionHostMath.StepDtFrames(1f), 0f);
            Assert.AreEqual(0f, MotionHostMath.StepDtFrames(-1f), 0f);
            Assert.AreEqual(0f, MotionHostMath.StepDtFrames(float.NaN), 0f);
        }

        [Test]
        public void ScheduleKeepsItsRateAndResyncsAfterAHitch()
        {
            Assert.AreEqual(1f / 15f, MotionHostMath.IntervalSeconds(15f), 1e-6f);
            Assert.AreEqual(0f, MotionHostMath.IntervalSeconds(0f));
            // On schedule: anchored on the previous due time, not on now.
            Assert.AreEqual(1.0667f, MotionHostMath.NextDue(1f, 1.01f, 1f / 15f), 1e-3f);
            // Far behind: restart from now.
            Assert.AreEqual(5f + 1f / 15f, MotionHostMath.NextDue(1f, 5f, 1f / 15f), 1e-3f);
        }

        [Test]
        public void SmoothFractionIsClamped()
        {
            Assert.AreEqual(0.5f, MotionHostMath.SmoothFraction(1.05f, 1f, 0.1f), 1e-4f);
            Assert.AreEqual(1f, MotionHostMath.SmoothFraction(3f, 1f, 0.1f));
            Assert.AreEqual(0f, MotionHostMath.SmoothFraction(0.5f, 1f, 0.1f));
            Assert.AreEqual(1f, MotionHostMath.SmoothFraction(1f, 1f, 0f));
        }

        // --- culling hysteresis ------------------------------------------------------

        [Test]
        public void CullingHasHysteresisAndFreezesOnlyBeyondRadiusPlusMargin()
        {
            // radius 12, margin 3: awake <=12, frozen >15, in between keeps its state.
            Assert.IsFalse(MotionHostMath.UpdateCulled(true, 11.9f * 11.9f, 12f, 3f));
            Assert.IsFalse(MotionHostMath.UpdateCulled(false, 14f * 14f, 12f, 3f), "awake stays awake in the band");
            Assert.IsTrue(MotionHostMath.UpdateCulled(true, 14f * 14f, 12f, 3f), "frozen stays frozen in the band");
            Assert.IsTrue(MotionHostMath.UpdateCulled(false, 15.1f * 15.1f, 12f, 3f));
            Assert.IsTrue(MotionHostMath.UpdateCulled(true, float.NaN, 12f, 3f), "bad distance keeps state");
        }

        // --- ownership ------------------------------------------------------------------

        [Test]
        public void DriftDetectsAPropMovedBySomebodyElse()
        {
            Vector3 written = new Vector3(1f, 2f, 3f);
            Assert.IsFalse(MotionHostMath.HasDrifted(written, written + new Vector3(0.001f, 0f, 0f), 0.02f));
            Assert.IsTrue(MotionHostMath.HasDrifted(written, written + new Vector3(0f, -0.1f, 0f), 0.02f));
            Assert.IsTrue(MotionHostMath.HasDrifted(written, new Vector3(float.NaN, 0f, 0f), 0.02f));
        }

        // --- composition ------------------------------------------------------------------

        private static Quaternion YawDeg(float degrees)
        {
            float half = degrees * Mathf.PI / 360f;
            return new Quaternion(0f, Mathf.Sin(half), 0f, Mathf.Cos(half));
        }

        [Test]
        public void EulerToQuaternionMatchesAxisAngleOnEachAxis()
        {
            Quaternion yaw = MotionHostMath.EulerToQuaternion(new Vector3(0f, Mathf.PI / 2f, 0f));
            Vector3 forward = yaw * new Vector3(0f, 0f, 1f);
            Assert.AreEqual(1f, forward.x, Tol);
            Assert.AreEqual(0f, forward.z, Tol);
            Quaternion roll = MotionHostMath.EulerToQuaternion(new Vector3(0f, 0f, Mathf.PI / 2f));
            Vector3 right = roll * new Vector3(1f, 0f, 0f);
            Assert.AreEqual(1f, right.y, Tol);
        }

        [Test]
        public void EulerToQuaternionAppliesZThenXThenY()
        {
            // Unity: Euler(x, y, z) = qy * qx * qz. Roll 90 about Z then pitch 90 about X maps
            // +X to +Y (roll) then +Y to +Z (pitch): the vector ends on +Z.
            Quaternion q = MotionHostMath.EulerToQuaternion(new Vector3(Mathf.PI / 2f, 0f, Mathf.PI / 2f));
            Vector3 v = q * new Vector3(1f, 0f, 0f);
            Assert.AreEqual(0f, v.x, Tol);
            Assert.AreEqual(0f, v.y, Tol);
            Assert.AreEqual(1f, v.z, Tol);
        }

        [Test]
        public void SpawnLocalStandalonePoseIsSpawnTimesOffsetTimesEuler()
        {
            // Spawn yawed 90 degrees: a +Y offset stays up, a +X offset (spawn-local) goes to -Z.
            Quaternion spawn = YawDeg(90f);
            Vector3 position; Quaternion rotation;
            MotionHostMath.StandalonePose(MotionHostMath.ClassSway, new Vector3(1f, 0f, 0f), spawn,
                new Vector3(0.5f, 0.2f, 0f), Vector3.zero, out position, out rotation);
            Assert.AreEqual(1f, position.x, Tol);
            Assert.AreEqual(0.2f, position.y, Tol);
            Assert.AreEqual(-0.5f, position.z, Tol);
        }

        [Test]
        public void WorldAxisAndAbsoluteClassesDoNotUseTheSpawnFrame()
        {
            Quaternion spawn = YawDeg(90f);
            Vector3 position; Quaternion rotation;
            MotionHostMath.StandalonePose(MotionHostMath.ClassSporadicWalk, new Vector3(1f, 0f, 0f), spawn,
                new Vector3(0.5f, 0f, 0f), new Vector3(0f, Mathf.PI, 0f), out position, out rotation);
            Assert.AreEqual(1.5f, position.x, Tol, "offset is in world axes");
            Vector3 forward = rotation * new Vector3(0f, 0f, 1f);
            Assert.AreEqual(-1f, forward.z, Tol, "yaw pi is the walker's own world yaw, spawn rotation is dropped");

            MotionHostMath.StandalonePose(MotionHostMath.ClassPathCollision, Vector3.zero, spawn,
                new Vector3(7f, 8f, 9f), Vector3.zero, out position, out rotation);
            Assert.AreEqual(7f, position.x, Tol);
            Assert.AreEqual(8f, position.y, Tol);
        }

        [TestCase(MotionHostMath.ClassBob)]
        [TestCase(MotionHostMath.ClassWhackAMole)]
        [TestCase(MotionHostMath.ClassHop)]
        public void VerticalMotionDoesNotRotateTheOffsetWithTheSpawnPose(int motionClass)
        {
            // 倾倒 90° 的出生姿态可区分世界 Y 和局部 Y。
            Quaternion spawn = new Quaternion(0.70710678f, 0f, 0f, 0.70710678f);
            Vector3 position;
            Quaternion rotation;
            MotionHostMath.StandalonePose(motionClass, new Vector3(1f, 2f, 3f), spawn,
                new Vector3(0f, -0.25f, 0f), Vector3.zero, out position, out rotation);
            Assert.AreEqual(1f, position.x, Tol);
            Assert.AreEqual(1.75f, position.y, Tol);
            Assert.AreEqual(3f, position.z, Tol);
            Assert.AreEqual(spawn.x, rotation.x, Tol);
            Assert.AreEqual(spawn.y, rotation.y, Tol);
            Assert.AreEqual(spawn.z, rotation.z, Tol);
            Assert.AreEqual(spawn.w, rotation.w, Tol);
        }

        [Test]
        public void BackAndForthKeepsItsSpawnOrientationAtAnAbsolutePathPoint()
        {
            Quaternion spawn = YawDeg(-90f);
            Vector3 point = new Vector3(7f, 8f, 9f);
            Vector3 position;
            Quaternion rotation;
            MotionHostMath.StandalonePose(MotionHostMath.ClassBackAndForth, new Vector3(1f, 2f, 3f),
                spawn, point, Vector3.zero, out position, out rotation);
            Assert.AreEqual(point.x, position.x, Tol);
            Assert.AreEqual(point.y, position.y, Tol);
            Assert.AreEqual(point.z, position.z, Tol);
            Assert.AreEqual(spawn.y, rotation.y, Tol);
            Assert.AreEqual(spawn.w, rotation.w, Tol);
        }

        [Test]
        public void CloudsKeepTheirSpawnRotation()
        {
            Quaternion spawn = YawDeg(33f);
            Vector3 position; Quaternion rotation;
            MotionHostMath.StandalonePose(MotionHostMath.ClassClouds, Vector3.zero, spawn,
                new Vector3(1f, 0f, 1f), new Vector3(0f, 2f, 0f), out position, out rotation);
            Assert.AreEqual(spawn.y, rotation.y, Tol);
            Assert.AreEqual(spawn.w, rotation.w, Tol);
        }

        [Test]
        public void FullInheritCarriesAChildOnASpinningParent()
        {
            // Parent spawned at the origin, now yawed 90 degrees about +Y at the origin. A child
            // spawned at (1, 0, 0) rides it to (0, 0, -1) and turns with it.
            Quaternion parentSpawn = Quaternion.identity;
            Quaternion parentNow = YawDeg(90f);
            Vector3 position; Quaternion rotation;
            MotionHostMath.CarryByParent(MotionHostMath.InheritFull, Vector3.zero, parentNow, Vector3.zero,
                MotionHostMath.Conjugate(parentSpawn), new Vector3(1f, 0f, 0f), Quaternion.identity,
                out position, out rotation);
            Assert.AreEqual(0f, position.x, Tol);
            Assert.AreEqual(-1f, position.z, Tol);
            Assert.AreEqual(parentNow.y, rotation.y, Tol);
        }

        [Test]
        public void FullInheritAboutAParentThatSpawnedAwayFromTheOrigin()
        {
            // Parent spawned at (10, 0, 0) and yawed 90 degrees in place. A child 1 m in +X of it
            // must swing to 1 m in -Z of the parent: world(child) = world(P) * inverse(spawn(P)) * own.
            Quaternion parentNow = YawDeg(90f);
            Vector3 position; Quaternion rotation;
            MotionHostMath.CarryByParent(MotionHostMath.InheritFull, new Vector3(10f, 0f, 0f), parentNow,
                new Vector3(10f, 0f, 0f), MotionHostMath.Conjugate(Quaternion.identity),
                new Vector3(11f, 0f, 0f), Quaternion.identity, out position, out rotation);
            Assert.AreEqual(10f, position.x, Tol);
            Assert.AreEqual(-1f, position.z, Tol);
        }

        [Test]
        public void PositionInheritRidesTranslationOnly()
        {
            Vector3 position; Quaternion rotation;
            Quaternion own = YawDeg(10f);
            MotionHostMath.CarryByParent(MotionHostMath.InheritPosition, new Vector3(3f, 1f, 0f), YawDeg(90f),
                new Vector3(1f, 0f, 0f), Quaternion.identity, new Vector3(5f, 5f, 5f), own,
                out position, out rotation);
            Assert.AreEqual(7f, position.x, Tol);
            Assert.AreEqual(6f, position.y, Tol);
            Assert.AreEqual(own.y, rotation.y, 0f);
        }

        [Test]
        public void NoInheritLeavesThePoseAlone()
        {
            Vector3 position; Quaternion rotation;
            MotionHostMath.CarryByParent(MotionHostMath.InheritNone, new Vector3(3f, 1f, 0f), YawDeg(90f),
                Vector3.zero, Quaternion.identity, new Vector3(5f, 5f, 5f), Quaternion.identity,
                out position, out rotation);
            Assert.AreEqual(5f, position.x, 0f);
        }

        // --- speed ---------------------------------------------------------------------------

        [Test]
        public void SpeedResolutionPrefersAPathRecordThenExplicitThenTheTypeTable()
        {
            // 1.2 cm/frame path = 36.6 cm/s in the data -> 0.366 m/s.
            Assert.AreEqual(0.366f, MotionHostMath.ResolveSpeedMps(MotionHostMath.SpeedSourcePath, 36.6f, true, 0f, 3), 1e-5f);
            // No path speed: falls to the type table (index 1 = 1 cm/frame = 0.3 m/s).
            Assert.AreEqual(0.3f, MotionHostMath.ResolveSpeedMps(MotionHostMath.SpeedSourcePath, 0f, false, 0f, 1), 1e-5f);
            Assert.AreEqual(0.303f, MotionHostMath.ResolveSpeedMps(MotionHostMath.SpeedSourceExplicit, 0f, false, 30.3f, -1), 1e-5f);
            Assert.AreEqual(0f, MotionHostMath.ResolveSpeedMps(MotionHostMath.SpeedSourceNone, 0f, false, 0f, 1));
            Assert.AreEqual(0f, MotionHostMath.ResolveSpeedMps(MotionHostMath.SpeedSourceType, 0f, false, 0f, -1));
            // Clouds: speed index 6 = 10 cm/frame = 3.0 m/s.
            Assert.AreEqual(3f, MotionHostMath.ResolveSpeedMps(MotionHostMath.SpeedSourceType, 0f, false, 0f, 6), 1e-5f);
        }

        [Test]
        public void NamesToCodes()
        {
            Assert.AreEqual(MotionHostMath.InheritFull, MotionHostMath.InheritFromName("full"));
            Assert.AreEqual(MotionHostMath.InheritNone, MotionHostMath.InheritFromName(null));
            Assert.AreEqual(MotionHostMath.SpeedSourcePath, MotionHostMath.SpeedSourceFromName("path"));
            Assert.AreEqual(MotionHostMath.SpeedSourceNone, MotionHostMath.SpeedSourceFromName("none"));
        }
    }
}
