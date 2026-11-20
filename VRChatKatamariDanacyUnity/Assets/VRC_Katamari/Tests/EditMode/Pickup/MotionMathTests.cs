using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for <see cref="MotionMath"/>: the original's 13 prop motion programs
    /// as pure math (Docs/Research/KatamariBallRules/ORIGINAL_MOTION_PROGRAM.md sections
    /// 11-14).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Expected values are hand calculations from the section 12 formulas and the section
    /// 5 measurements, written out in each test's comments; they are never re-runs of the
    /// implementation. Where a value depends on the D-12 phase, the test reads the public
    /// <see cref="MotionMath.Phase01"/> (which has its own determinism test) and derives the
    /// expected number with the documented formula, e.g. Sway's reset clock is
    /// phase * 90 frames.
    /// </para>
    /// <para>
    /// Units: dt is GAME FRAMES at 30 Hz; lengths are metres; angles are radians. Tolerances
    /// are 1e-4 m (0.1 mm) for positions and 1e-4 rad for angles unless a comment says why a
    /// test needs more room: the only such cases are float cancellation in a clock that was
    /// built from a fractional phase, and the path tests, where a speed in metres per second
    /// is divided by 30 frames and summed.
    /// </para>
    /// <para>
    /// This fixture creates no GameObjects and touches no scene: the library is pure, so the
    /// tests call it directly and nothing is left behind in an open scene.
    /// </para>
    /// </remarks>
    public class MotionMathTests
    {
        private static readonly Vector3 NoOffset = Vector3.zero;
        private static readonly Vector3 NoRotation = Vector3.zero;

        private const float Dt = 1f;
        private const float PositionTolerance = 1e-4f;

        // The only hop speed Cancer uses (all 18 instances are size = 2 -> group 0 -> 4).
        private const float CancerHopSpeedCmPerFrame = 4f;

        private static void AssertOffset(Vector3 expected, Vector3 actual, string what)
        {
            Assert.AreEqual(expected.x, actual.x, PositionTolerance, what + " (x)");
            Assert.AreEqual(expected.y, actual.y, PositionTolerance, what + " (y)");
            Assert.AreEqual(expected.z, actual.z, PositionTolerance, what + " (z)");
        }

        [Test]
        public void OriginalMotionFrameReflectsAllThreeAxesAfterScalingToMetres()
        {
            // 非对称输入区别三轴点反射与绕 X 转 180°，也能抓到重复的厘米换算。
            Vector3 offset = MotionMath.OriginalMotionOffsetToWorld(1f, -2f, 3f);
            Assert.AreEqual(-1f, offset.x, 0f);
            Assert.AreEqual(2f, offset.y, 0f);
            Assert.AreEqual(-3f, offset.z, 0f);
        }

        [TestCase(0f, 1f, 0f)]
        [TestCase(1f, 0f, Mathf.PI / 2f)]
        [TestCase(0f, -1f, Mathf.PI)]
        [TestCase(-1f, 0f, 3f * Mathf.PI / 2f)]
        [TestCase(3f, 4f, 0.6435011f)]
        public void OriginalYawUsesTheWorldDirectionWithoutASecondHalfTurn(float dx, float dz, float expected)
        {
            Assert.AreEqual(expected, MotionMath.OriginalMotionYawFromWorldDirection(dx, dz), 1e-6f);
        }

        [TestCase(0.5f, 0.4636476f)]
        [TestCase(-0.5f, -0.4636476f)]
        public void OriginalPitchFlipsTheVerticalDirectionButNotThePlanarLength(float worldDy, float expected)
        {
            Assert.AreEqual(expected, MotionMath.OriginalMotionPitchFromWorldDirection(0.6f, worldDy, 0.8f),
                1e-6f);
        }

        // --- Phase (D-12) -------------------------------------------------------

        [Test]
        public void PhaseSeedIsDeterministicAndSpreadsSeedsOverTheUnitInterval()
        {
            // D-12: the phase is a deterministic function of the seed, never Random.
            float first = MotionMath.Phase01(12345);
            float again = MotionMath.Phase01(12345);
            Assert.AreEqual(first, again, 0f);
            Assert.GreaterOrEqual(first, 0f);
            Assert.Less(first, 1f);

            // Two seeds an installer would plausibly hand out must not land on one phase.
            Assert.Greater(Mathf.Abs(MotionMath.Phase01(54321) - first), 0f);
            Assert.Greater(Mathf.Abs(MotionMath.Phase01(0) - first), 0f);

            // Seeds are ints and the mix is over the full 32 bits, including negatives.
            Assert.GreaterOrEqual(MotionMath.Phase01(-7), 0f);
            Assert.Less(MotionMath.Phase01(-7), 1f);
        }

        // --- Spin / Flip --------------------------------------------------------

        [Test]
        public void SpinTurnsOnceEvery419SecondsAndKeepsItsPosition()
        {
            // Section 12.1: euler.y += 0.05 * dt, so one turn is 2*pi/0.05 = 125.6637 frames
            // = 4.18879 s. The reset clock is phase * period (D-12), and 0.05 * period is a
            // full turn, so the starting yaw is exactly 2*pi*phase.
            float period = 2f * Mathf.PI / MotionMath.SpinRadiansPerFrame;
            Assert.AreEqual(4.18879f, period / MotionMath.GameFramesPerSecond, 1e-4f);
            Assert.AreEqual(125.6637f, period, 1e-3f);

            float[] state = new float[MotionMath.SpinStateFloats];
            MotionMath.ResetSpin(state, 12345);
            Vector3 offset;
            Vector3 euler;
            Assert.IsTrue(MotionMath.TryStepSpin(state, 0f, out offset, out euler));
            AssertOffset(NoOffset, offset, "spin does not translate");
            Assert.AreEqual(0f, euler.x, 0f);
            Assert.AreEqual(0f, euler.z, 0f);
            Assert.AreEqual(2f * Mathf.PI * MotionMath.Phase01(12345), euler.y, 1e-4f);

            float startYaw = euler.y;
            Assert.IsTrue(MotionMath.TryStepSpin(state, period, out offset, out euler));
            Assert.AreEqual(startYaw, euler.y, 1e-3f);
            AssertOffset(NoOffset, offset, "spin does not translate");

            // A quarter turn is 31.4159 frames: +pi/2 from where the prop was.
            Assert.IsTrue(MotionMath.TryStepSpin(state, 31.4159f, out offset, out euler));
            Assert.AreEqual(Mathf.Repeat(startYaw + Mathf.PI / 2f, 2f * Mathf.PI), euler.y, 1e-3f);
        }

        [Test]
        public void FlipIsTheSameRateAboutXInTheOppositeDirection()
        {
            // Section 12.2: euler.x -= 0.05 * dt about X while Spin turns +Y about Y.
            float[] spin = new float[MotionMath.SpinStateFloats];
            float[] flip = new float[MotionMath.FlipStateFloats];
            MotionMath.ResetSpin(spin, 777);
            MotionMath.ResetFlip(flip, 777);
            Vector3 spinOffset;
            Vector3 spinEuler;
            Vector3 flipOffset;
            Vector3 flipEuler;
            Assert.IsTrue(MotionMath.TryStepSpin(spin, 40f, out spinOffset, out spinEuler));
            Assert.IsTrue(MotionMath.TryStepFlip(flip, 40f, out flipOffset, out flipEuler));

            AssertOffset(NoOffset, flipOffset, "flip does not translate");
            Assert.AreEqual(0f, flipEuler.y, 0f);
            Assert.AreEqual(0f, flipEuler.z, 0f);
            // Both start from the same phase clock, so equal magnitudes and opposite signs
            // make the sines cancel: sin(+x) = -sin(-x).
            Assert.AreEqual(-Mathf.Sin(spinEuler.y), Mathf.Sin(flipEuler.x), 1e-4f);

            // One full 4.19 s turn (125.6637 frames) later the flip is back at the pitch it
            // had 40 frames in: the pitch is periodic with the same period as Spin's yaw.
            float period = 2f * Mathf.PI / MotionMath.SpinRadiansPerFrame;
            float pitchAfterFortyFrames = flipEuler.x;
            Assert.IsTrue(MotionMath.TryStepFlip(flip, period, out flipOffset, out flipEuler));
            Assert.AreEqual(Mathf.Sin(pitchAfterFortyFrames), Mathf.Sin(flipEuler.x), 1e-3f);
        }

        // --- Bob ----------------------------------------------------------------

        [Test]
        public void BobFloatsAtThreeSecondsWithTheMeasuredAmplitude()
        {
            // Sections 12.3 and 5.7: PS2 y = halfHeight * 0.15 * sin(angle), angle +=
            // dt * pi/45 -> 90 frames = 3.0 s per cycle. Measured on the spider (full height
            // 10.7 cm, so half height 5.35 cm): +-0.81 cm = 0.15 * 5.35 cm = 0.008025 m,
            // near the observed +-0.68/+-0.81 cm and the 2.83/3.07 s periods.
            const float halfHeightM = 0.0535f;
            int seed = 0;
            float[] state = new float[MotionMath.BobStateFloats];
            MotionMath.ResetBob(state, seed);
            float idle = MotionMath.BobIdleMaxFrames * MotionMath.Phase01(seed);
            Assert.Greater(idle, 0.5f);

            Vector3 offset;
            Vector3 euler;
            Assert.IsTrue(MotionMath.TryStepBob(state, idle * 0.5f, halfHeightM, out offset, out euler));
            AssertOffset(NoOffset, offset, "bob rests while its idle runs");

            // The swing needs 22.5 frames to reach sin = 1 (pi/2 at pi/45 per frame).
            Assert.IsTrue(MotionMath.TryStepBob(state, idle * 0.5f + 22.5f, halfHeightM,
                out offset, out euler));
            Assert.AreEqual(-0.15f * halfHeightM, offset.y, 1e-6f);
            Assert.AreEqual(-0.008025f, offset.y, 1e-6f);
            Assert.AreEqual(0f, offset.x, 0f);
            Assert.AreEqual(0f, offset.z, 0f);
            AssertOffset(NoRotation, euler, "bob has no rotation");

            // One cycle later (90 frames = 3 s) it is at the same height.
            Assert.IsTrue(MotionMath.TryStepBob(state, 90f, halfHeightM, out offset, out euler));
            Assert.AreEqual(-0.008025f, offset.y, 1e-6f);

            // 半周期之后 PS2 正弦为 −1，世界偏移在顶点。
            Assert.IsTrue(MotionMath.TryStepBob(state, 45f, halfHeightM, out offset, out euler));
            Assert.AreEqual(0.008025f, offset.y, 1e-6f);
        }

        // --- Sway ---------------------------------------------------------------

        [Test]
        public void SwayMovesOnTheEllipseOfHalfTheModelHeight()
        {
            // Section 12.4: euler.z = sin(angle) * 10 degrees with angle += dt * pi/45
            // (90 frames = 3.0 s per cycle). 以下为 PS2 偏移，世界输出取负：
            //   x = h*sin(2e), y = h*(1 - cos(2e)),  h = modelBboxM[1] / 2.
            // At the swing's end e = 10 degrees, so x = h*sin(20) = 0.342 h and
            // y = h*(1-cos20) = 0.0603 h; the peak-to-peak is 0.684 h = 0.342 of the full
            // height. Sunflower: full height 240.5 cm, h = 120.25 cm = 1.2025 m, and the
            // prediction is 82.26 / 7.25 cm against the measured 82.0 / 7.19 cm.
            const float halfHeightM = 1.2025f;
            int seed = 0;
            float[] state = new float[MotionMath.SwayStateFloats];
            MotionMath.ResetSway(state, seed);

            Vector3 offset;
            Vector3 euler;
            // The reset clock is phase * 90 frames; sin(clock*pi/45) = 1 at 22.5 frames.
            float clock = 90f * MotionMath.Phase01(seed);
            float toSwingEnd = 22.5f - clock;
            Assert.Greater(toSwingEnd, 0f);
            Assert.IsTrue(MotionMath.TryStepSway(state, toSwingEnd, halfHeightM, true,
                out offset, out euler));

            Assert.AreEqual(MotionMath.SwayAngleRadians, euler.z, 1e-5f);   // +10 degrees
            Assert.AreEqual(-0.4112792f, offset.x, 1e-4f);   // 1.2025 * sin(20) = 41.13 cm
            Assert.AreEqual(-0.0725196f, offset.y, 1e-4f);  // 世界向下 7.25 cm
            Assert.AreEqual(0f, offset.z, 0f);
            Assert.AreEqual(0f, euler.x, 0f);
            Assert.AreEqual(0f, euler.y, 0f);

            // The doc's warning, as an assertion: a naive "rotate +-10 degrees about the
            // pivot" gives 2h*sin(10) = 41.7 cm of horizontal travel, half the truth.
            Assert.AreEqual(0.8225585f, -2f * offset.x, 1e-4f);
            Assert.Less(offset.x, -0.4f);

            // A full 3 s cycle later it is back at the same point.
            Assert.IsTrue(MotionMath.TryStepSway(state, 90f, halfHeightM, true, out offset, out euler));
            Assert.AreEqual(-0.4112792f, offset.x, 1e-4f);
            Assert.AreEqual(MotionMath.SwayAngleRadians, euler.z, 1e-5f);

            // Half a swing later (22.5 frames) the tilt has crossed zero and the offset is
            // the origin again: sin(2e) = 0 and cos(2e) = 1 at e = 0.
            Assert.IsTrue(MotionMath.TryStepSway(state, 22.5f, halfHeightM, true, out offset, out euler));
            Assert.AreEqual(0f, offset.y, 1e-5f);
            Assert.AreEqual(0f, euler.z, 1e-5f);
        }

        [Test]
        public void SwayKeepsItsTiltButNoOffsetForTheBalancedDollProp()
        {
            // Section 12.4 skips the translation for BALANCEDOLL01_C only; the tilt stays.
            float[] state = new float[MotionMath.SwayStateFloats];
            MotionMath.ResetSway(state, 0);
            float toSwingEnd = 22.5f - 90f * MotionMath.Phase01(0);
            Vector3 offset;
            Vector3 euler;
            Assert.IsTrue(MotionMath.TryStepSway(state, toSwingEnd, 1.2025f, false,
                out offset, out euler));
            AssertOffset(NoOffset, offset, "the balanced doll does not translate");
            Assert.AreEqual(MotionMath.SwayAngleRadians, euler.z, 1e-5f);
        }

        // --- WhackAMole ---------------------------------------------------------

        [Test]
        public void WhackAMoleWaitsAtSpawnThenSinksThreeHalfHeightsIn075Seconds()
        {
            const float halfHeightM = 0.05f;
            const int seed = 7;
            float[] state = new float[MotionMath.WhackAMoleStateFloats];
            MotionMath.ResetWhackAMole(state, seed);
            float hold = 60f + 150f * MotionMath.Phase01(seed);
            Vector3 offset;
            Vector3 euler;
            Assert.IsTrue(MotionMath.TryStepWhackAMole(state, hold - 1f, seed, halfHeightM, true,
                out offset, out euler));
            Assert.AreEqual(0f, offset.y, 0f);
            Assert.IsTrue(MotionMath.TryStepWhackAMole(state, 1f, seed, halfHeightM, true,
                out offset, out euler));
            Assert.AreEqual(0f, offset.y, 1e-6f);

            // θ = 3π/4：PS2 位移 +0.15 * (1 − sin θ)，世界向下。
            Assert.IsTrue(MotionMath.TryStepWhackAMole(state, 11.25f, seed, halfHeightM, true,
                out offset, out euler));
            Assert.AreEqual(-0.0439340f, offset.y, 1e-5f);
            AssertOffset(NoRotation, euler, "whack-a-mole does not rotate");
            Assert.IsTrue(MotionMath.TryStepWhackAMole(state, 11.25f, seed, halfHeightM, true,
                out offset, out euler));
            Assert.AreEqual(-0.15f, offset.y, 1e-5f);
            Assert.AreEqual(0.75f, MotionMath.WhackSweepFrames / MotionMath.GameFramesPerSecond, 1e-5f);
        }

        [Test]
        public void WhackAMoleHoldsRisesAndRepeatsWithSeededEndPointDelays()
        {
            const float halfHeightM = 0.05f;
            const int seed = 7;
            float[] state = new float[MotionMath.WhackAMoleStateFloats];
            MotionMath.ResetWhackAMole(state, seed);
            float initialHold = 60f + 150f * MotionMath.Phase01(seed);
            Vector3 offset;
            Vector3 euler;
            Assert.IsTrue(MotionMath.TryStepWhackAMole(state, initialHold + 22.5f, seed,
                halfHeightM, true, out offset, out euler));
            Assert.AreEqual(-0.15f, offset.y, 1e-5f);

            float undergroundHold = state[2];
            Assert.That(undergroundHold, Is.InRange(60f, 210f));
            Assert.AreNotEqual(initialHold, undergroundHold);
            Assert.IsTrue(MotionMath.TryStepWhackAMole(state, undergroundHold, seed, halfHeightM, true,
                out offset, out euler));
            Assert.AreEqual(-0.15f, offset.y, 1e-5f);
            Assert.IsTrue(MotionMath.TryStepWhackAMole(state, 11.25f, seed, halfHeightM, true,
                out offset, out euler));
            Assert.AreEqual(-0.1060660f, offset.y, 1e-5f); // −0.15 * sin(3π/4)
            Assert.IsTrue(MotionMath.TryStepWhackAMole(state, 11.25f, seed, halfHeightM, true,
                out offset, out euler));
            Assert.AreEqual(0f, offset.y, 1e-5f);

            float raisedHold = state[2];
            Assert.That(raisedHold, Is.InRange(60f, 210f));
            Assert.IsTrue(MotionMath.TryStepWhackAMole(state, raisedHold, seed, halfHeightM, true,
                out offset, out euler));
            Assert.AreEqual(0f, offset.y, 1e-5f);
            Assert.IsTrue(MotionMath.TryStepWhackAMole(state, 11.25f, seed, halfHeightM, true,
                out offset, out euler));
            Assert.AreEqual(-0.0439340f, offset.y, 1e-5f);
        }

        [Test]
        public void WhackAMoleStaysBuriedForAuthoredOneShot()
        {
            const float halfHeightM = 0.05f;
            const int seed = 7;
            float[] state = new float[MotionMath.WhackAMoleStateFloats];
            MotionMath.ResetWhackAMole(state, seed);
            Vector3 offset;
            Vector3 euler;
            Assert.IsTrue(MotionMath.TryStepWhackAMole(state, 500f, seed, halfHeightM, false,
                out offset, out euler));
            Assert.AreEqual(-0.15f, offset.y, 1e-5f);
        }

        // --- Hop ----------------------------------------------------------------

        [Test]
        public void HopRisesSixCentimetresInTheOriginalsSevenFrameArc()
        {
            // Sections 12.6 and 15.1 item 2: the original adds gravity first, then moves
            // (vy += g * dt; y += vy * dt) with vy starting at -4 cm/frame and g = 1
            // PS2 的离散 y 为 −3、−5、−6、−6、−5、−3、0 cm，换算后在 +6 cm 处两帧。
            int seed = 0;
            float[] state = new float[MotionMath.HopStateFloats];
            MotionMath.ResetHop(state, seed);
            Vector3 offset;
            Vector3 euler;

            // Walk the 0-8.5 s idle out one frame at a time.
            int frames = 0;
            while (true)
            {
                Assert.IsTrue(MotionMath.TryStepHop(state, Dt, seed, CancerHopSpeedCmPerFrame,
                    out offset, out euler));
                frames = frames + 1;
                if (offset.y > 0f) break;
                AssertOffset(NoOffset, offset, "hop rests while idle");
                Assert.Less(frames, 300);
            }
            AssertOffset(NoRotation, euler, "hop has no rotation");
            Assert.AreEqual(0.03f, offset.y, 1e-6f);   // vy = -4 + 1 = -3 cm/frame

            Assert.IsTrue(MotionMath.TryStepHop(state, Dt, seed, CancerHopSpeedCmPerFrame,
                out offset, out euler));
            Assert.AreEqual(0.05f, offset.y, 1e-6f);
            Assert.IsTrue(MotionMath.TryStepHop(state, Dt, seed, CancerHopSpeedCmPerFrame,
                out offset, out euler));
            Assert.AreEqual(0.06f, offset.y, 1e-6f);   // the 6 cm jump
            Assert.IsTrue(MotionMath.TryStepHop(state, Dt, seed, CancerHopSpeedCmPerFrame,
                out offset, out euler));
            Assert.AreEqual(0.06f, offset.y, 1e-6f);   // two frames at the apex
            Assert.IsTrue(MotionMath.TryStepHop(state, Dt, seed, CancerHopSpeedCmPerFrame,
                out offset, out euler));
            Assert.AreEqual(0.05f, offset.y, 1e-6f);
            Assert.IsTrue(MotionMath.TryStepHop(state, Dt, seed, CancerHopSpeedCmPerFrame,
                out offset, out euler));
            Assert.AreEqual(0.03f, offset.y, 1e-6f);
            Assert.IsTrue(MotionMath.TryStepHop(state, Dt, seed, CancerHopSpeedCmPerFrame,
                out offset, out euler));
            Assert.AreEqual(0f, offset.y, 1e-6f);       // back at the spawn height
            // Landing moves the cycle start to this frame and draws the next idle.
            Assert.AreEqual(state[0], state[1], 0f);
            Assert.LessOrEqual(state[2], MotionMath.HopIdleMaxFrames);
        }

        [Test]
        public void HopTwoFrameStepUsesTheOriginalsOwnArithmetic()
        {
            // Section 15.1 item 2: the recurrence is not dt-quantised. One dt = 2 step is
            // vy += 2g then y += 2vy, i.e. -4 cm after two game frames, while two dt = 1
            // steps are -3 then -5 cm: exactly one g (1 cm) apart. Hop is therefore the one
            // class whose batched step differs from many small steps - by design, because it
            // reproduces the original's hitched arithmetic instead of quantising it.
            int seed = 0;
            float[] two = new float[MotionMath.HopStateFloats];
            float[] ones = new float[MotionMath.HopStateFloats];
            MotionMath.ResetHop(two, seed);
            MotionMath.ResetHop(ones, seed);
            Vector3 offset;
            Vector3 euler;

            // The first flight frame is the first frame whose clock reaches the idle.
            int idleTicks = Mathf.CeilToInt(two[2]);
            for (int i = 0; i < idleTicks - 1; i++)
            {
                Assert.IsTrue(MotionMath.TryStepHop(two, Dt, seed, CancerHopSpeedCmPerFrame,
                    out offset, out euler));
                AssertOffset(NoOffset, offset, "still resting");
            }
            Assert.IsTrue(MotionMath.TryStepHop(two, 2f, seed, CancerHopSpeedCmPerFrame,
                out offset, out euler));
            Assert.AreEqual(0.04f, offset.y, 1e-6f);

            for (int i = 0; i < idleTicks - 1; i++)
            {
                Assert.IsTrue(MotionMath.TryStepHop(ones, Dt, seed, CancerHopSpeedCmPerFrame,
                    out offset, out euler));
            }
            Assert.IsTrue(MotionMath.TryStepHop(ones, Dt, seed, CancerHopSpeedCmPerFrame,
                out offset, out euler));
            Assert.AreEqual(0.03f, offset.y, 1e-6f);
            Assert.IsTrue(MotionMath.TryStepHop(ones, Dt, seed, CancerHopSpeedCmPerFrame,
                out offset, out euler));
            Assert.AreEqual(0.05f, offset.y, 1e-6f);
        }

        [Test]
        public void HopIdlesAreUniformOverZeroTo85SecondsAndHitTheMeasuredBand()
        {
            // Section 12.6 draws timer = 255 * rand frames, i.e. 0-8.5 s. The measured
            // spacing was only "3.4-5 s or more" (ORIGINAL_ANIMATION_OBSERVATIONS), so this
            // test pins the implemented distribution (the constant) and shows the measured
            // band occurs inside it; the real distribution is section 13 item M3.
            int seed = 0;
            float[] state = new float[MotionMath.HopStateFloats];
            MotionMath.ResetHop(state, seed);
            Vector3 offset;
            Vector3 euler;
            float previousCycleStart = state[1];
            int cycles = 0;
            int cyclesInMeasuredBand = 0;
            for (int frame = 0; frame < 30000; frame++)
            {
                Assert.IsTrue(MotionMath.TryStepHop(state, 1f, seed, CancerHopSpeedCmPerFrame,
                    out offset, out euler));
                if (state[1] > previousCycleStart)
                {
                    // On landing, state[1] becomes the landing frame and state[2] the idle
                    // that just started, so this samples the drawn idle directly.
                    float idleFrames = state[2];
                    Assert.GreaterOrEqual(idleFrames, 0f);
                    Assert.LessOrEqual(idleFrames, MotionMath.HopIdleMaxFrames);
                    Assert.GreaterOrEqual(state[1] - previousCycleStart, 7f);   // the flight
                    cycles = cycles + 1;
                    if (idleFrames >= 3.4f * 30f && idleFrames <= 5f * 30f) cyclesInMeasuredBand++;
                    previousCycleStart = state[1];
                }
            }
            Assert.Greater(cycles, 100);
            Assert.Greater(cyclesInMeasuredBand, 0);
        }

        // --- SporadicWalk -------------------------------------------------------

        [Test]
        public void SporadicWalkStartsAlongWorldPositiveXAtItsTypeSpeed()
        {
            // 原作选择 PS2 −X，偏航 π + atan2(−1,0) = π/2。
            // 一帧 0.303 / 30 m 的位移换算后应朝世界 +X。
            float[] state = new float[MotionMath.WanderStateFloats];
            MotionMath.ResetSporadicWalk(state, 12345);
            Vector3 offset;
            Vector3 euler;
            Assert.IsTrue(MotionMath.TryStepSporadicWalk(state, Dt, 12345, 0.303f, 0x7F, false,
                out offset, out euler));
            Assert.AreEqual(0.303f / 30f, offset.x, 1e-6f);
            Assert.AreEqual(0f, offset.z, 0f);
            Assert.AreEqual(0f, offset.y, 0f);
            Assert.AreEqual(Mathf.PI / 2f, euler.y, 1e-5f);
        }

        [Test]
        public void SporadicWalkPausesButOtherwiseKeepsItsSpeed()
        {
            // Section 12.7: the walk is at a constant speed; the pause is 1-2 s out of every
            // 5-7 s, so a 1200-frame run must contain both stopped and moving frames and
            // every moving frame must cover exactly speed * dt.
            float[] state = new float[MotionMath.WanderStateFloats];
            MotionMath.ResetSporadicWalk(state, 0);
            float expectedStep = 0.303f / 30f;
            Vector3 previous = Vector3.zero;
            int stopped = 0;
            int moving = 0;
            for (int i = 0; i < 1200; i++)
            {
                Vector3 offset;
                Vector3 euler;
                Assert.IsTrue(MotionMath.TryStepSporadicWalk(state, Dt, 0, 0.303f, 0x7F, false,
                    out offset, out euler));
                float dx = offset.x - previous.x;
                float dz = offset.z - previous.z;
                float stepLength = Mathf.Sqrt(dx * dx + dz * dz);
                if (stepLength < 1e-9f)
                {
                    stopped = stopped + 1;
                }
                else
                {
                    Assert.AreEqual(expectedStep, stepLength, 1e-4f);
                    moving = moving + 1;
                }
                previous = offset;
            }
            Assert.Greater(stopped, 0);
            Assert.Greater(moving, 0);
        }

        [Test]
        public void SporadicWalkIsDeterministicPerSeedAndDiffersAcrossSeeds()
        {
            Vector3 first = WalkOffsetAfter(12345, 600);
            Vector3 again = WalkOffsetAfter(12345, 600);
            Vector3 other = WalkOffsetAfter(54321, 600);
            Assert.AreEqual(first.x, again.x, 0f);
            Assert.AreEqual(first.z, again.z, 0f);
            // Different seeds draw different turn timers and turn signs, so their paths part.
            float separation = Mathf.Abs(first.x - other.x) + Mathf.Abs(first.z - other.z);
            Assert.Greater(separation, 1e-3f);
        }

        private static Vector3 WalkOffsetAfter(int seed, int frames)
        {
            float[] state = new float[MotionMath.WanderStateFloats];
            MotionMath.ResetSporadicWalk(state, seed);
            Vector3 offset = Vector3.zero;
            for (int i = 0; i < frames; i++)
            {
                Vector3 euler;
                Assert.IsTrue(MotionMath.TryStepSporadicWalk(state, Dt, seed, 0.303f, 0x7F, false,
                    out offset, out euler));
            }
            return offset;
        }

        [Test]
        public void SporadicWalkRevertsARefusedStepAndTurns()
        {
            // Section 12.7's attemptForwardStep: a refused step restores the previous
            // position and the class then chooses a new direction. The host reports the
            // refusal, so the math layer can undo the step it already returned.
            float[] state = new float[MotionMath.WanderStateFloats];
            MotionMath.ResetSporadicWalk(state, 12345);
            Vector3 before = Vector3.zero;
            Vector3 offset = Vector3.zero;
            Vector3 euler = Vector3.zero;
            // Stop on a frame that actually walked: a pause frame would make the revert
            // assertion vacuous.
            for (int i = 0; i < 600; i++)
            {
                before = offset;
                Assert.IsTrue(MotionMath.TryStepSporadicWalk(state, Dt, 12345, 0.303f, 0x7F, false,
                    out offset, out euler));
                if (Mathf.Abs(offset.x - before.x) + Mathf.Abs(offset.z - before.z) > 1e-9f) break;
            }
            Assert.AreNotEqual(before, offset);

            Vector3 yawBefore = euler;
            Assert.IsTrue(MotionMath.TryStepSporadicWalk(state, Dt, 12345, 0.303f, 0x7F, true,
                out offset, out euler));
            AssertOffset(before, offset, "a refused step is reverted");
            Assert.AreEqual(yawBefore.y, euler.y, 1e-6f);   // the facing is unchanged, only the turn starts

            // The next frame moves again (now while turning).
            Assert.IsTrue(MotionMath.TryStepSporadicWalk(state, Dt, 12345, 0.303f, 0x7F, false,
                out offset, out euler));
            Assert.Greater(Mathf.Abs(offset.x - before.x) + Mathf.Abs(offset.z - before.z), 1e-6f);
        }

        [Test]
        public void SporadicWalkTurnsOnlyToAllowedCandidates()
        {
            // The host's zone rays decide which of +45, -45, +90, -90, +135, -135, 180
            // degrees stays in the zone; a mask with only -90 allowed must produce a -90
            // degree turn over the documented 12 frames (section 12.7 ChooseDirection).
            float[] state = new float[MotionMath.WanderStateFloats];
            MotionMath.ResetSporadicWalk(state, 12345);
            Vector3 offset;
            Vector3 euler;
            Assert.IsTrue(MotionMath.TryStepSporadicWalk(state, Dt, 12345, 0.303f, 0x7F, false,
                out offset, out euler));
            // Bit 3 is -90 degrees.
            Assert.IsTrue(MotionMath.TryStepSporadicWalk(state, Dt, 12345, 0.303f, 1 << 3, true,
                out offset, out euler));
            // The new base direction is the current one; after 12 frames the turn is done and
            // the walk continues along the previous direction rotated by -90 degrees:
            // PS2 (−1,0) 转 −90° 后为 (0,−1)，世界方向为 +Z。
            for (int i = 0; i < 12; i++)
            {
                Assert.IsTrue(MotionMath.TryStepSporadicWalk(state, Dt, 12345, 0.303f, 0x7F, false,
                    out offset, out euler));
            }
            Vector3 previous = offset;
            Assert.IsTrue(MotionMath.TryStepSporadicWalk(state, Dt, 12345, 0.303f, 0x7F, false,
                out offset, out euler));
            Assert.AreEqual(0f, offset.x - previous.x, 1e-4f);
            Assert.AreEqual(0.303f / 30f, offset.z - previous.z, 1e-4f);
            // 世界 +Z 是零偏航，微小浮点误差不会让结果贴在 2π。
            Assert.AreEqual(0f, euler.y, 1e-6f);
            Assert.GreaterOrEqual(euler.y, 0f);
            Assert.Less(euler.y, 2f * Mathf.PI);
        }

        [Test]
        public void WalkFacingTracksTheConvertedVelocityThroughATurn()
        {
            float[] state = new float[MotionMath.WanderStateFloats];
            MotionMath.ResetSporadicWalk(state, 12345);
            Vector3 offset;
            Vector3 euler;

            // 世界 +X 对应 π/2。
            Assert.IsTrue(MotionMath.TryStepSporadicWalk(state, Dt, 12345, 0.303f, 0x7F, false,
                out offset, out euler));
            Assert.AreEqual(Mathf.PI / 2f, euler.y, 1e-5f);

            // 世界 +X 转 +90° 之后沿 −Z，偏航为 π。
            Assert.IsTrue(MotionMath.TryStepSporadicWalk(state, Dt, 12345, 0.303f, 1 << 2, true,
                out offset, out euler));
            for (int i = 0; i < 14; i++)
            {
                Assert.IsTrue(MotionMath.TryStepSporadicWalk(state, Dt, 12345, 0.303f, 0x7F, false,
                    out offset, out euler));
            }
            Vector3 previous = offset;
            Assert.IsTrue(MotionMath.TryStepSporadicWalk(state, Dt, 12345, 0.303f, 0x7F, false,
                out offset, out euler));
            Assert.AreEqual(-0.303f / 30f, offset.z - previous.z, 1e-4f);
            Assert.AreEqual(Mathf.PI, euler.y, 1e-5f);
            Assert.GreaterOrEqual(euler.y, 0f);
            Assert.Less(euler.y, 2f * Mathf.PI);
        }

        // --- Clouds -------------------------------------------------------------

        [Test]
        public void CloudsNeverPausesAndNeverYaws()
        {
            // Section 12.8: the same machine as SporadicWalk with ignoreYaw, no Pause and
            // the 90 + 600 * draw timer. speedTable 6 = 10 cm/frame = 3.03 m/s measured, so
            // one frame covers 3.03 / 30 = 0.101 m and no frame is ever stopped.
            float[] state = new float[MotionMath.WanderStateFloats];
            MotionMath.ResetClouds(state, 12345);
            Vector3 previous = Vector3.zero;
            for (int i = 0; i < 900; i++)
            {
                Vector3 offset;
                Vector3 euler;
                Assert.IsTrue(MotionMath.TryStepClouds(state, Dt, 12345, 3.03f, 0x7F,
                    out offset, out euler));
                AssertOffset(NoRotation, euler, "clouds do not yaw");
                if (i == 0) Assert.AreEqual(3.03f / 30f, offset.x, 1e-6f); // PS2 −X → 世界 +X
                Assert.AreEqual(0f, offset.y, 0f);
                float dx = offset.x - previous.x;
                float dz = offset.z - previous.z;
                Assert.AreEqual(3.03f / 30f, Mathf.Sqrt(dx * dx + dz * dz), 1e-4f);
                previous = offset;
            }
        }

        [Test]
        public void CloudsAreDeterministicPerSeed()
        {
            float[] first = new float[MotionMath.WanderStateFloats];
            float[] again = new float[MotionMath.WanderStateFloats];
            MotionMath.ResetClouds(first, 777);
            MotionMath.ResetClouds(again, 777);
            Vector3 a = Vector3.zero;
            Vector3 b = Vector3.zero;
            for (int i = 0; i < 600; i++)
            {
                Vector3 euler;
                Assert.IsTrue(MotionMath.TryStepClouds(first, Dt, 777, 3.03f, 0x7F, out a, out euler));
                Assert.IsTrue(MotionMath.TryStepClouds(again, Dt, 777, 3.03f, 0x7F, out b, out euler));
                Assert.AreEqual(a.x, b.x, 0f);
                Assert.AreEqual(a.z, b.z, 0f);
            }
            float[] other = new float[MotionMath.WanderStateFloats];
            MotionMath.ResetClouds(other, 54321);
            Vector3 c = Vector3.zero;
            for (int i = 0; i < 600; i++)
            {
                Vector3 euler;
                Assert.IsTrue(MotionMath.TryStepClouds(other, Dt, 54321, 3.03f, 0x7F, out c, out euler));
            }
            float separation = Mathf.Abs(a.x - c.x) + Mathf.Abs(a.z - c.z);
            Assert.Greater(separation, 1e-3f);
        }

        // --- WaitForPlayer / DraggedAlong --------------------------------------

        [Test]
        public void WaitForPlayerUsesTheSizeGroupTriggerRadius()
        {
            // Section 12.11: motionID 0x00 fires when the observer is inside the size
            // group's panic radius and the group is at most 2. The Cancer props are size 2,
            // so group 0 and 200 cm; group 1 is 300 cm. Section 14: this is the behaviour
            // gate, not the host's 12 m wake radius.
            float[] state = new float[MotionMath.WaitForPlayerStateFloats];
            MotionMath.ResetWaitForPlayer(state);
            bool triggered;
            Vector3 offset;
            Vector3 euler;

            Assert.IsTrue(MotionMath.TryStepWaitForPlayer(state, 0x00, 1.99f, 0, false, false,
                out triggered, out offset, out euler));
            Assert.IsTrue(triggered);
            AssertOffset(NoOffset, offset, "wait-for-player does not move");
            AssertOffset(NoRotation, euler, "wait-for-player does not rotate");
            Assert.AreEqual(2f, MotionMath.PanicRadiusM(0), 0f);

            Assert.IsTrue(MotionMath.TryStepWaitForPlayer(state, 0x00, 2.01f, 0, false, false,
                out triggered, out offset, out euler));
            Assert.IsFalse(triggered);

            Assert.IsTrue(MotionMath.TryStepWaitForPlayer(state, 0x00, 2.99f, 1, false, false,
                out triggered, out offset, out euler));
            Assert.IsTrue(triggered);
            Assert.AreEqual(3f, MotionMath.PanicRadiusM(1), 0f);

            // Groups above 2 never satisfy the 0x00 condition, however close the observer is.
            Assert.IsTrue(MotionMath.TryStepWaitForPlayer(state, 0x00, 0.1f, 3, false, false,
                out triggered, out offset, out euler));
            Assert.IsFalse(triggered);

            // 0x04 needs the same zone and a rolled parent; the reference hard-codes the
            // parent as not rolled, so it never fires there (section 12.11, item M2).
            Assert.IsTrue(MotionMath.TryStepWaitForPlayer(state, 0x04, 0.5f, 0, true, false,
                out triggered, out offset, out euler));
            Assert.IsFalse(triggered);
            Assert.IsTrue(MotionMath.TryStepWaitForPlayer(state, 0x04, 0.5f, 0, true, true,
                out triggered, out offset, out euler));
            Assert.IsTrue(triggered);

            // An unknown motion id is refused rather than guessed.
            Assert.IsFalse(MotionMath.TryStepWaitForPlayer(state, 0x01, 0.5f, 0, true, true,
                out triggered, out offset, out euler));
        }

        [Test]
        public void DraggedAlongProducesNoMotionOfItsOwn()
        {
            // Section 12.12: the class has no state and no motion; the parent's transform is
            // the pose. Completing the 13-class table is the whole point of this entry point.
            Assert.AreEqual(0, MotionMath.DraggedAlongStateFloats);
            Vector3 offset;
            Vector3 euler;
            Assert.IsTrue(MotionMath.TryStepDraggedAlong(Dt, out offset, out euler));
            AssertOffset(NoOffset, offset, "dragged-along is the parent's pose");
            AssertOffset(NoRotation, euler, "dragged-along adds no rotation");
            Assert.IsFalse(MotionMath.TryStepDraggedAlong(float.NaN, out offset, out euler));
        }

        // --- PathCollision / SlopingPath / BackAndForthNoYaw -------------------

        [Test]
        public void PathCollisionWalksAtTheRecordedSpeed()
        {
            // Sections 12.9 and 6.7.4: the prop starts at the nearest point of the world
            // polyline, one half height above it, yaw = atan2(worldDx, worldDz), and walks at the
            // path record's speed. Path p23's 24.4 cm/s is 0.244 m/s, so one second (30
            // frames) covers 0.244 m.
            float[] points = { 0f, 0f, 0f, 10f, 0f, 0f };
            float[] state = new float[MotionMath.PathCollisionStateFloats];
            MotionMath.ResetPathCollision(state);
            Vector3 position;
            Vector3 euler;
            Assert.IsTrue(MotionMath.TryStepPathCollision(state, 0f, new Vector3(0.5f, 0f, 0f),
                points, 2, 0.244f, 0.5f, false, out position, out euler));
            Assert.AreEqual(0f, position.x, 1e-5f);
            Assert.AreEqual(0.5f, position.y, 1e-5f);
            Assert.AreEqual(Mathf.PI / 2f, euler.y, 1e-5f);    // 世界 +X
            Assert.AreEqual(0f, euler.x, 0f);                      // no pitch without the host

            Assert.IsTrue(MotionMath.TryStepPathCollision(state, 30f, new Vector3(0.5f, 0f, 0f),
                points, 2, 0.244f, 0.5f, false, out position, out euler));
            Assert.AreEqual(0.244f, position.x, 1e-4f);
            Assert.AreEqual(0.5f, position.y, 1e-4f);
        }

        [Test]
        public void PathSpeedsComeFromThePathRecords()
        {
            // The three Cancer path records (LEVEL_FORMAT_V2 6.7.4 sample data): p15
            // sloping_path 36.6 cm/s, p22 back_and_forth 18.3 cm/s, p23 path_collision
            // 24.4 cm/s. One second of travel is the speed in metres.
            float[] points = { 0f, 0f, 0f, 10f, 0f, 0f };
            float[] speeds = { 0.366f, 0.183f, 0.244f };
            for (int i = 0; i < 3; i++)
            {
                float[] state = new float[MotionMath.PathCollisionStateFloats];
                MotionMath.ResetPathCollision(state);
                Vector3 position;
                Vector3 euler;
                Assert.IsTrue(MotionMath.TryStepPathCollision(state, 30f, Vector3.zero,
                    points, 2, speeds[i], 0f, false, out position, out euler));
                Assert.AreEqual(speeds[i], position.x, 1e-4f);
            }
        }

        [Test]
        public void PathCollisionWrapsAtTheEndOfAClosedPolyline()
        {
            // Section 12.9 wraps with pathIndex % pointCount, so a closed 4-point square of
            // side 1 m travelled at 1 m/s returns to the same pose after 4 s (120 frames).
            float[] points = { 0f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 1f, 0f, 0f, 1f };
            float[] state = new float[MotionMath.PathCollisionStateFloats];
            MotionMath.ResetPathCollision(state);
            Vector3 position;
            Vector3 euler;
            Assert.IsTrue(MotionMath.TryStepPathCollision(state, 30f, Vector3.zero,
                points, 4, 1f, 0f, true, out position, out euler));
            Vector3 afterOneSecond = position;
            Assert.AreEqual(1f, afterOneSecond.x, 1e-4f);
            Assert.AreEqual(0f, afterOneSecond.z, 1e-4f);

            Assert.IsTrue(MotionMath.TryStepPathCollision(state, 120f, Vector3.zero,
                points, 4, 1f, 0f, true, out position, out euler));
            AssertOffset(afterOneSecond, position, "one closed lap returns to the same point");
        }

        [Test]
        public void SlopingPathPitchesToTheSegmentSlope()
        {
            // Section 12.9 with adjustPitch: the body follows the path's slope, eased over
            // the first quarter of the segment. For a segment rising 0.1 m per 1 m the pitch
            // is +atan2(0.1, 1) = +0.09967 rad about X. At a quarter of the way the ease is
            // finished, and the position is a quarter along the segment.
            float[] points = { 0f, 0f, 0f, 1f, 0.1f, 0f };
            float[] state = new float[MotionMath.SlopingPathStateFloats];
            MotionMath.ResetSlopingPath(state);
            Vector3 position;
            Vector3 euler;
            // The segment is sqrt(1^2 + 0.1^2) = 1.00499 m long, so a quarter of it is
            // 0.25125 m of travel = 20.594 frames at 0.366 m/s.
            float quarterSegmentFrames = (0.25f * Mathf.Sqrt(1.01f) / 0.366f) * 30f;
            Assert.IsTrue(MotionMath.TryStepSlopingPath(state, quarterSegmentFrames, Vector3.zero,
                points, 2, 0.366f, 0.05f, false, out position, out euler));
            Assert.AreEqual(0.25f, position.x, 1e-3f);
            Assert.AreEqual(0.025f + 0.05f, position.y, 1e-3f);
            Assert.AreEqual(0.0996687f, euler.x, 1e-4f);
            Assert.AreEqual(Mathf.PI / 2f, euler.y, 1e-5f);
        }

        [Test]
        public void BackAndForthNoYawPingPongsWithoutTurning()
        {
            // Section 12.10: walk to the end, reverse, walk back, and never yaw. A 1 m
            // segment at 0.183 m/s: after 30 frames the prop is 0.183 m along; after enough
            // frames to pass the end (s = 1.2 m) it is 0.8 m along, i.e. coming back.
            float[] points = { 0f, 0f, 0f, 1f, 0f, 0f };
            float[] state = new float[MotionMath.BackAndForthNoYawStateFloats];
            MotionMath.ResetBackAndForthNoYaw(state);
            Vector3 position;
            Vector3 euler;
            Assert.IsTrue(MotionMath.TryStepBackAndForthNoYaw(state, 30f, Vector3.zero,
                points, 2, 0.183f, 0f, out position, out euler));
            Assert.AreEqual(0.183f, position.x, 1e-4f);
            AssertOffset(NoRotation, euler, "back-and-forth keeps its spawn orientation");

            float framesToPassTheEnd = (1.2f / 0.183f) * 30f;
            Assert.IsTrue(MotionMath.TryStepBackAndForthNoYaw(state,
                framesToPassTheEnd - 30f, Vector3.zero, points, 2, 0.183f, 0f,
                out position, out euler));
            Assert.AreEqual(0.8f, position.x, 1e-3f);
            AssertOffset(NoRotation, euler, "back-and-forth keeps its spawn orientation");

            // The same distance keeps no drift: 30 more frames move it back by 0.183 m.
            Assert.IsTrue(MotionMath.TryStepBackAndForthNoYaw(state, 30f, Vector3.zero,
                points, 2, 0.183f, 0f, out position, out euler));
            Assert.AreEqual(0.8f - 0.183f, position.x, 1e-3f);
        }

        // --- Zero input, batching and validation --------------------------------

        [Test]
        public void AZeroStepRecomputesThePoseWithoutAdvancing()
        {
            // The host clamps dt to [0, 2]; a frozen prop asks for dt = 0 and must get the
            // same pose back, not a rephased one. Section 12.0's freeze rule depends on it.
            int seed = 12345;
            float[] spin = new float[MotionMath.SpinStateFloats];
            MotionMath.ResetSpin(spin, seed);
            Vector3 offset;
            Vector3 euler;
            Assert.IsTrue(MotionMath.TryStepSpin(spin, 0f, out offset, out euler));
            float spinYaw = euler.y;
            float spinClock = spin[0];
            Assert.IsTrue(MotionMath.TryStepSpin(spin, 0f, out offset, out euler));
            Assert.AreEqual(spinYaw, euler.y, 0f);
            Assert.AreEqual(spinClock, spin[0], 0f);

            float[] sway = new float[MotionMath.SwayStateFloats];
            MotionMath.ResetSway(sway, seed);
            Assert.IsTrue(MotionMath.TryStepSway(sway, 0f, 1f, true, out offset, out euler));
            Vector3 swayOffset = offset;
            Assert.IsTrue(MotionMath.TryStepSway(sway, 0f, 1f, true, out offset, out euler));
            AssertOffset(swayOffset, offset, "a zero step keeps the sway pose");

            float[] whack = new float[MotionMath.WhackAMoleStateFloats];
            MotionMath.ResetWhackAMole(whack, seed);
            Assert.IsTrue(MotionMath.TryStepWhackAMole(whack, 0f, seed, 0.05f, true,
                out offset, out euler));
            AssertOffset(NoOffset, offset, "whack-a-mole starts buried");

            float[] walk = new float[MotionMath.WanderStateFloats];
            MotionMath.ResetSporadicWalk(walk, seed);
            Assert.IsTrue(MotionMath.TryStepSporadicWalk(walk, 0f, seed, 0.303f, 0x7F, false,
                out offset, out euler));
            AssertOffset(NoOffset, offset, "a zero step leaves the walk at its spawn offset");
            Assert.AreEqual(Mathf.PI / 2f, euler.y, 1e-5f);   // pi + atan2(-1, 0): facing -X
            Assert.IsTrue(MotionMath.TryStepSporadicWalk(walk, 0f, seed, 0.303f, 0x7F, false,
                out offset, out euler));
            AssertOffset(NoOffset, offset, "a zero step keeps the walk pose");

            float[] points = { 0f, 0f, 0f, 1f, 0f, 0f };
            float[] path = new float[MotionMath.PathCollisionStateFloats];
            MotionMath.ResetPathCollision(path);
            Vector3 position;
            Assert.IsTrue(MotionMath.TryStepPathCollision(path, 0f, Vector3.zero, points, 2,
                0.244f, 0f, false, out position, out euler));
            Vector3 pathStart = position;
            Assert.IsTrue(MotionMath.TryStepPathCollision(path, 0f, Vector3.zero, points, 2,
                0.244f, 0f, false, out position, out euler));
            AssertOffset(pathStart, position, "a zero step keeps the path pose");
        }

        [Test]
        public void OneLargeStepMatchesManySmallSteps()
        {
            // Section 12.0: positions are recomputed from state, never accumulated into the
            // world transform, so stepping 200 frames at once must equal 200 one-frame steps.
            // This is the test that catches an incremental implementation.
            const int frames = 200;
            int seed = 12345;
            Vector3 offset;
            Vector3 euler;
            Vector3 smallOffset = Vector3.zero;
            Vector3 smallEuler = Vector3.zero;

            float[] spinSmall = new float[MotionMath.SpinStateFloats];
            float[] spinBig = new float[MotionMath.SpinStateFloats];
            MotionMath.ResetSpin(spinSmall, seed);
            MotionMath.ResetSpin(spinBig, seed);
            for (int i = 0; i < frames; i++) Assert.IsTrue(MotionMath.TryStepSpin(spinSmall, Dt, out smallOffset, out smallEuler));
            Assert.IsTrue(MotionMath.TryStepSpin(spinBig, frames, out offset, out euler));
            Assert.AreEqual(smallEuler.y, euler.y, 1e-4f);

            float[] bobSmall = new float[MotionMath.BobStateFloats];
            float[] bobBig = new float[MotionMath.BobStateFloats];
            MotionMath.ResetBob(bobSmall, seed);
            MotionMath.ResetBob(bobBig, seed);
            for (int i = 0; i < frames; i++) Assert.IsTrue(MotionMath.TryStepBob(bobSmall, Dt, 0.05f, out smallOffset, out smallEuler));
            Assert.IsTrue(MotionMath.TryStepBob(bobBig, frames, 0.05f, out offset, out euler));
            AssertOffset(smallOffset, offset, "bob batches without drift");

            float[] swaySmall = new float[MotionMath.SwayStateFloats];
            float[] swayBig = new float[MotionMath.SwayStateFloats];
            MotionMath.ResetSway(swaySmall, seed);
            MotionMath.ResetSway(swayBig, seed);
            for (int i = 0; i < frames; i++) Assert.IsTrue(MotionMath.TryStepSway(swaySmall, Dt, 1f, true, out smallOffset, out smallEuler));
            Assert.IsTrue(MotionMath.TryStepSway(swayBig, frames, 1f, true, out offset, out euler));
            AssertOffset(smallOffset, offset, "sway batches without drift");
            Assert.AreEqual(smallEuler.z, euler.z, 1e-4f);

            float[] whackSmall = new float[MotionMath.WhackAMoleStateFloats];
            float[] whackBig = new float[MotionMath.WhackAMoleStateFloats];
            MotionMath.ResetWhackAMole(whackSmall, seed);
            MotionMath.ResetWhackAMole(whackBig, seed);
            for (int i = 0; i < frames; i++) Assert.IsTrue(MotionMath.TryStepWhackAMole(whackSmall, Dt, seed, 0.05f, true, out smallOffset, out smallEuler));
            Assert.IsTrue(MotionMath.TryStepWhackAMole(whackBig, frames, seed, 0.05f, true, out offset, out euler));
            AssertOffset(smallOffset, offset, "whack-a-mole batches without drift");

            // Hop is deliberately NOT in this list: it keeps the original's semi-implicit
            // recurrence, so a 2-frame step drops 4 cm where two 1-frame steps drop 5 cm
            // (section 15.1 item 2). HopTwoFrameStepUsesTheOriginalsOwnArithmetic pins that
            // difference; Hop is stepped at its 30 Hz tier with dt = 1.

            // Paths are a function of travelled distance: a batched step lands on the same
            // point as many small ones (float rounding of the distance sum is the only gap).
            float[] points = { 0f, 0f, 0f, 10f, 0f, 0f };
            float[] pathSmall = new float[MotionMath.PathCollisionStateFloats];
            float[] pathBig = new float[MotionMath.PathCollisionStateFloats];
            MotionMath.ResetPathCollision(pathSmall);
            MotionMath.ResetPathCollision(pathBig);
            Vector3 smallPosition = Vector3.zero;
            Vector3 bigPosition = Vector3.zero;
            for (int i = 0; i < frames; i++) Assert.IsTrue(MotionMath.TryStepPathCollision(pathSmall, Dt, Vector3.zero, points, 2, 0.244f, 0f, false, out smallPosition, out smallEuler));
            Assert.IsTrue(MotionMath.TryStepPathCollision(pathBig, frames, Vector3.zero, points, 2, 0.244f, 0f, false, out bigPosition, out euler));
            AssertOffset(smallPosition, bigPosition, "the path walks without drift");
        }

        [Test]
        public void InvalidInputsAreRefusedWithoutTouchingTheState()
        {
            // Udon has no exceptions in this layer, so a refusal is a false return with the
            // caller's state untouched and every out value zero.
            Vector3 offset;
            Vector3 euler;
            float[] sway = new float[MotionMath.SwayStateFloats];
            MotionMath.ResetSway(sway, 1);
            float clock = sway[0];

            Assert.IsFalse(MotionMath.TryStepSway(null, Dt, 1f, true, out offset, out euler));
            Assert.IsFalse(MotionMath.TryStepSway(sway, -1f, 1f, true, out offset, out euler));
            Assert.IsFalse(MotionMath.TryStepSway(sway, float.NaN, 1f, true, out offset, out euler));
            Assert.IsFalse(MotionMath.TryStepSway(sway, Dt, -1f, true, out offset, out euler));
            Assert.IsFalse(MotionMath.TryStepSway(sway, Dt, float.PositiveInfinity, true,
                out offset, out euler));
            Assert.AreEqual(clock, sway[0], 0f);
            AssertOffset(NoOffset, offset, "a refusal writes no offset");
            AssertOffset(NoRotation, euler, "a refusal writes no rotation");

            float[] tooShort = new float[MotionMath.SwayStateFloats - 1];
            Assert.IsFalse(MotionMath.TryStepSway(tooShort, Dt, 1f, true, out offset, out euler));

            // Path data: a zero-length segment, a single point, a null array and a
            // non-finite point are all refused.
            float[] degenerate = { 0f, 0f, 0f, 0f, 0f, 0f };
            float[] pathState = new float[MotionMath.PathCollisionStateFloats];
            MotionMath.ResetPathCollision(pathState);
            Vector3 position;
            Assert.IsFalse(MotionMath.TryStepPathCollision(pathState, Dt, Vector3.zero,
                degenerate, 2, 0.1f, 0f, false, out position, out euler));
            Assert.IsFalse(MotionMath.TryStepPathCollision(pathState, Dt, Vector3.zero,
                degenerate, 1, 0.1f, 0f, false, out position, out euler));
            Assert.IsFalse(MotionMath.TryStepPathCollision(pathState, Dt, Vector3.zero,
                null, 2, 0.1f, 0f, false, out position, out euler));
            float[] notFinite = { 0f, 0f, 0f, float.NaN, 0f, 0f };
            Assert.IsFalse(MotionMath.TryStepPathCollision(pathState, Dt, Vector3.zero,
                notFinite, 2, 0.1f, 0f, false, out position, out euler));

            // A hop speed of zero has no flight, so it is bad input, not a still prop.
            float[] hop = new float[MotionMath.HopStateFloats];
            MotionMath.ResetHop(hop, 1);
            Assert.IsFalse(MotionMath.TryStepHop(hop, Dt, 1, 0f, out offset, out euler));
        }

        [Test]
        public void TheTableHelpersMatchTheOriginalConstantTables()
        {
            // Section 3.4/3.5: the size grouping, the hop speeds and the speed table are the
            // only pure constant tables in the original's data path, so their edges are worth
            // pinning. 1 cm/frame is the 30.3 cm/s unit.
            Assert.AreEqual(0, MotionMath.SizeGroup(0));
            Assert.AreEqual(0, MotionMath.SizeGroup(2));
            Assert.AreEqual(1, MotionMath.SizeGroup(3));
            Assert.AreEqual(2, MotionMath.SizeGroup(4));
            Assert.AreEqual(3, MotionMath.SizeGroup(6));
            Assert.AreEqual(4, MotionMath.SizeGroup(11));

            Assert.AreEqual(4f, MotionMath.HopSpeedCmPerFrame(0), 0f);
            Assert.AreEqual(6f, MotionMath.HopSpeedCmPerFrame(1), 0f);
            Assert.AreEqual(7f, MotionMath.HopSpeedCmPerFrame(2), 0f);
            Assert.AreEqual(12f, MotionMath.HopSpeedCmPerFrame(3), 0f);
            Assert.AreEqual(15f, MotionMath.HopSpeedCmPerFrame(4), 0f);

            Assert.AreEqual(0.3f, MotionMath.SpeedCmPerFrame(0), 0f);
            Assert.AreEqual(1f, MotionMath.SpeedCmPerFrame(1), 0f);
            Assert.AreEqual(2f, MotionMath.SpeedCmPerFrame(2), 0f);
            Assert.AreEqual(10f, MotionMath.SpeedCmPerFrame(6), 0f);
            Assert.AreEqual(40f, MotionMath.SpeedCmPerFrame(9), 0f);
            Assert.AreEqual(200f, MotionMath.SpeedCmPerFrame(10), 0f);
            Assert.AreEqual(0f, MotionMath.SpeedCmPerFrame(11), 0f);
        }

        [Test]
        public void StateSizesCoverEverySlotTheStepsWrite()
        {
            // The documented state sizes are what the host allocates (a parallel float[] per
            // prop), so a step must fit its own array exactly and must not need one more.
            Assert.AreEqual(1, MotionMath.SpinStateFloats);
            Assert.AreEqual(1, MotionMath.FlipStateFloats);
            Assert.AreEqual(2, MotionMath.BobStateFloats);
            Assert.AreEqual(1, MotionMath.SwayStateFloats);
            Assert.AreEqual(4, MotionMath.WhackAMoleStateFloats);
            Assert.AreEqual(7, MotionMath.HopStateFloats);
            Assert.AreEqual(15, MotionMath.WanderStateFloats);
            Assert.AreEqual(1, MotionMath.WaitForPlayerStateFloats);
            Assert.AreEqual(0, MotionMath.DraggedAlongStateFloats);
            Assert.AreEqual(2, MotionMath.PathCollisionStateFloats);
            Assert.AreEqual(2, MotionMath.SlopingPathStateFloats);
            Assert.AreEqual(2, MotionMath.BackAndForthNoYawStateFloats);
        }
    }
}
