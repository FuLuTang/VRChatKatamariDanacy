using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Pickup
{
    /// <summary>
    /// The original's 13 prop motion programs as pure, allocation-free math: given the
    /// caller-owned state, one step in game frames and the class's constants, produce the
    /// pose the prop has this frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source of truth: Docs/Research/KatamariBallRules/ORIGINAL_MOTION_PROGRAM.md
    /// sections 11-14 (state, init, update, per-class constants, measurement list) and
    /// Docs/Design/Levels/LEVEL_FORMAT_V2.md section 6.7 (motions[]/paths[], the
    /// updatesPerSecond tiers, phase). The numbers here are that document's constants;
    /// nothing is a new measurement. Every constant the research could not pin down is
    /// marked PROVISIONAL in its own remarks - none of them was replaced by a plausible
    /// looking guess.
    /// </para>
    /// <para>
    /// Time. dt is a count of GAME FRAMES at the original's 30 Hz, not seconds; the host
    /// derives it as dt = clamp(elapsedSeconds * 30, 0, 2) (section 12.0). Speeds the
    /// original stores per frame stay per frame (the hop table and the speed table);
    /// speeds the level data stores in cm/s (paths; v2 speed.cmPerSec) arrive as metres
    /// per second and are converted inside with GameFramesPerSecond. One second is 30
    /// frames, so a step of dt frames covers dt / 30 seconds.
    /// </para>
    /// <para>
    /// Units: metres, radians, +Y up, single world space. Every entry point is pure: it
    /// reads and writes only the caller's float[] state and its arguments, allocates
    /// nothing (no new, no List, no LINQ, no closures, no struct or class state), and
    /// never touches a Transform, a scene, a collider or a physics query. Ground height,
    /// zone membership and support-object lookups belong to the host; where the original
    /// consults them the host passes the outcome in (forwardBlocked, allowedTurnMask,
    /// distanceToObserver). There are no static fields, only constants.
    /// </para>
    /// <para>
    /// Output pose, per class, because the original mixes two spaces:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// SPAWN-LOCAL: offset in metres from spawnPos, in the spawn frame, plus euler in
    /// radians in the spawn frame. Spin, Flip, Bob, Sway, WhackAMole, Hop, WaitForPlayer,
    /// DraggedAlong. Section 12.0 composes world = R(spawnQuat) * R(euler) *
    /// translate(spawnPos), so these functions compute exactly the part the motion owns;
    /// the host multiplies by the spawn transform.
    /// </description></item>
    /// <item><description>
    /// WORLD-AXIS offset from spawnPos with world yaw: SporadicWalk, and Clouds (which
    /// passes ignoreYaw and so keeps its spawn orientation). The original walks along world
    /// -X and its facing is yaw = pi + atan2(vx, vz), so its offset lives in world axes, not
    /// in the spawn frame. That formula binds model yaw 0 to facing -Z (section 15.1 item 1;
    /// item S1 is the prefab check that a +Z-forward prefab would fail).
    /// </description></item>
    /// <item><description>
    /// YAW RANGE: every emitted euler.y is normalized into [0, 2*pi) - Spin, Flip,
    /// SporadicWalk and the three path classes (pitch and roll are not wrapped; their ranges
    /// are already the useful ones). That is what makes the section 15.1 item 1 binding a
    /// fixed range as well as a convention: walking -Z is 0, walking +Z is pi, and nothing
    /// is emitted at the interval's 2*pi end. A direction a hair off -Z lands a hair below
    /// 2*pi, so a consumer comparing facings should compare angles (Mathf.DeltaAngle), not
    /// the wrapped numbers.
    /// </description></item>
    /// <item><description>
    /// ABSOLUTE world position with world yaw/pitch: PathCollision, SlopingPath,
    /// BackAndForthNoYaw. Path points are world points (section 6.7.4) and the original
    /// teleports the prop onto the nearest point at init (section 12.9), so these three
    /// return a world position rather than an offset.
    /// </description></item>
    /// </list>
    /// <para>
    /// State, in floats. The caller allocates one float[] of the class's documented size per
    /// prop and keeps it across frames (a MotionHost stores a parallel float[] row per prop);
    /// nothing here allocates or keeps a reference. Every Reset* writes the whole row, and
    /// the first row of each entry below is the slot list:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Spin 1: clock. Flip 1: clock.</description></item>
    /// <item><description>Bob 2: clock, idleFrames.</description></item>
    /// <item><description>Sway 1: clock.</description></item>
    /// <item><description>WhackAMole 6: clock, sweepStart, holdFrames, rising, done, segmentIndex.</description></item>
    /// <item><description>Hop 7: clock, cycleStart, idleFrames, cycleIndex, offsetY, velocityY,
    /// airborne.</description></item>
    /// <item><description>SporadicWalk / Clouds 15: clock, phase, timer, extraTimer, axisX, axisZ,
    /// angle, angleStep, angleTarget, offsetX, offsetZ, lastStepX, lastStepZ, drawCount, yaw.</description></item>
    /// <item><description>WaitForPlayer 1: useAlt. DraggedAlong 0: no state at all.</description></item>
    /// <item><description>PathCollision / SlopingPath / BackAndForthNoYaw 2: clock, distance (-1 = not
    /// initialised, so the first step resolves the start from spawnPos).</description></item>
    /// </list>
    /// <para>
    /// Drift. Position is recomputed from state (and, for paths, from spawnPos once at
    /// init) on every call; nothing accumulates into the world transform. That is section
    /// 12.0's rule, and it is what makes one large step equal many small steps and what
    /// keeps freeze/resume from moving anything: the tests check both. Three classes
    /// integrate in state because the original does: SporadicWalk and Clouds keep their
    /// walked offset, and Hop keeps its height and vertical velocity. Hop is therefore the
    /// one class whose batched step is NOT equal to the same number of one-frame steps -
    /// that is the original's own semi-implicit arithmetic (section 15.1 item 2: at speed 4
    /// one dt = 2 step drops 4 cm where two dt = 1 steps drop 5 cm, one g apart), not an
    /// implementation shortcut. Step it at its 30 Hz tier, dt = 1.
    /// </para>
    /// <para>
    /// Phase (D-12). Seeded classes derive every random draw from (phaseSeed, drawIndex)
    /// through one deterministic 32-bit hash; there is no Random and no dependence on
    /// time, iteration order or instance identity. The seed is the installer's
    /// hash32(level.id + "/" + itemId) reduced to an int (LEVEL_FORMAT_V2 section 6.7.5).
    /// Periodic classes also start at phase * period: their initial clock is
    /// phase01 * periodFrames, as section 6.7.5 specifies.
    /// </para>
    /// <para>
    /// PROVISIONAL constants (kept as the research states them, not measured):
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Spin / Flip angular rate 0.05 rad/frame (4.19 s per turn): reference constant,
    /// never measured - section 13 item M4. PROVISIONAL, pending M4.
    /// </description></item>
    /// <item><description>
    /// Hop idle 0-255 frames (0-8.5 s uniform): the dip is settled (6 cm, item M3 closed),
    /// but the interval was only measured as "3.4-5 s or more" and the uniform draw is a
    /// reference constant. PROVISIONAL, the spacing half of item M3 / H1.
    /// </description></item>
    /// <item><description>
    /// WhackAMole hold 60-210 frames (2-7 s): reference constant; the settle/descent half
    /// was never measured (section 6.7.3 "fall not measured"). PROVISIONAL.
    /// </description></item>
    /// <item><description>
    /// SporadicWalk turn / burst / pause timers and the 7-candidate turn table: reference
    /// constants, behaviour only observed as a net speed - section 13 item M5 is the
    /// animation test, the timers themselves are unmeasured. PROVISIONAL.
    /// </description></item>
    /// <item><description>
    /// Clouds turn timer 90-600 frames: reference constant, measured only as a speed.
    /// PROVISIONAL.
    /// </description></item>
    /// <item><description>
    /// Path waypoint yaw easing (over the new segment's length) and SlopingPath pitch
    /// easing (over its first quarter): reference implementation reading, no measurement -
    /// section 13 item M7. PROVISIONAL, pending M7.
    /// </description></item>
    /// <item><description>
    /// WaitForPlayer trigger gate: 200/300 cm from the reference constant table for the
    /// two size groups that occur; the real gate is unmeasured - section 13 items M1/M2.
    /// PROVISIONAL, pending M1/M2.
    /// </description></item>
    /// </list>
    /// <para>
    /// Deliberate deviations from the section 12 pseudocode, each local to one class
    /// (section 15.1 is the research's ruling on all of them):
    /// (a) the hop keeps the original's semi-implicit recurrence and order (vy += g * dt,
    /// then y += vy * dt) instead of its closed form, because the closed form only equals the
    /// recurrence at dt = 1; (b) WhackAMole's first segment rises, which section 15.1 item 3
    /// confirms is what the original does; (c) SporadicWalk turns are a Turn phase rather
    /// than an angle advanced inside Walk, same timing; (d) a refused forward step
    /// is reverted and the next direction is chosen from the per-frame mask instead of the
    /// in-line zone rays. Where a class ignores its phase the remarks say so.
    /// </para>
    /// </remarks>
    public static class MotionMath
    {
        // One second of the original's clock is 30 game frames (section 12.0).
        public const float GameFramesPerSecond = 30f;

        public const float MetresPerCentimetre = 0.01f;

        public const float TwoPi = Mathf.PI * 2f;

        // --- Constants the research measured (not PROVISIONAL) -----------------

        // Spin: 0.05 rad per game frame, 2*pi/0.05 = 125.66 frames = 4.19 s per turn.
        // Flip is the same rate about X with the opposite sign.
        public const float SpinRadiansPerFrame = 0.05f;

        // Bob, Sway and WhackAMole all advance one sine step of pi/45 per game frame, so a
        // full 2*pi cycle is 90 frames = 3.0 s (sections 12.3-12.5).
        public const float SineStepRadiansPerFrame = Mathf.PI / 45f;

        // Sway's tilt is +-10 degrees: sin(angle) * (2*pi/36).
        public const float SwayAngleRadians = TwoPi / 36f;

        // Bob's amplitude is 0.15 of the part's half height (section 12.3, measured on two
        // props: +-0.68 / +-0.81 cm).
        public const float BobAmplitudePerHalfHeight = 0.15f;

        // WhackAMole travel is h + (h - (-h)) = 3h = 1.5 model heights (measured on three
        // props: 12.37 / 16.09 / 165.0 cm).
        public const float WhackTravelPerHalfHeight = 3f;

        // The visible rise is a quarter sine: pi/2 at pi/45 per frame = 22.5 frames =
        // 0.75 s (measured 0.7 s).
        public const float WhackRiseFrames = (Mathf.PI / 2f) / SineStepRadiansPerFrame;

        // Bob: 0-60 frames of idle before the swing starts.
        public const float BobIdleMaxFrames = 60f;

        // WhackAMole: 60 + 150 * draw frames of hold at the reached end (2-7 s).
        // PROVISIONAL: reference constant, settle/descent never measured.
        public const float WhackHoldMinFrames = 60f;
        public const float WhackHoldRangeFrames = 150f;

        // Hop: gravity is 1 cm per frame per frame, and the speed table is per size group.
        // The dip is settled: the dt = 1 recurrence is -3, -5, -6, -6, -5, -3, 0 cm, i.e.
        // 6.0 cm deep over 7 frames, and the measured 6.05 / 6.60 cm match it (section 15.1
        // item 2 closed item M3; the old 8 cm "theory" was the continuous solution). Only the
        // 0-8.5 s spacing stays PROVISIONAL.
        public const float HopGravityMetresPerFrame2 = MetresPerCentimetre;
        public const float HopIdleMaxFrames = 255f;

        // SporadicWalk / Clouds timers (sections 12.7-12.8).
        // PROVISIONAL: reference constants; only the net speed was observed.
        public const float WalkTurnMinFrames = 120f;
        public const float WalkTurnRangeFrames = 570f;
        public const float WalkBurstMinFrames = 120f;
        public const float WalkBurstRangeFrames = 60f;
        public const float WalkPauseMinFrames = 30f;
        public const float WalkPauseRangeFrames = 30f;
        public const float WalkRandomTurnStepRadians = 1f / 20f;
        public const float WalkCandidateTurnFrames = 12f;
        public const float CloudsTurnMinFrames = 90f;
        public const float CloudsTurnRangeFrames = 600f;

        // Yaw output is normalized into [0, 2*pi); a value this close to 2*pi is reported as
        // 0 instead, so walking straight along -Z reads as model yaw 0 (section 15.1 item 1)
        // rather than as the 2*pi end of the interval. 1e-4 rad = 0.006 degrees.
        public const float YawSeamRadians = 1e-4f;

        // A path's adjacent points must be at least 5 cm apart (LEVEL_FORMAT_V2 6.7.4);
        // the loader is allowed to be a little looser, but a zero-length segment would make
        // the direction undefined, so anything under a centimetre is refused as bad data.
        public const float MinimumPathSegmentM = 0.01f;

        // --- State sizes, in floats (section 12's per-class state) --------------
        // Every Step writes only these slots of the caller's array; no class allocates.

        // [0] clockFrames
        public const int SpinStateFloats = 1;

        // [0] clockFrames
        public const int FlipStateFloats = 1;

        // [0] clockFrames, [1] idleFrames
        public const int BobStateFloats = 2;

        // [0] clockFrames
        public const int SwayStateFloats = 1;

        // [0] clockFrames, [1] sweepStartFrames, [2] holdFrames, [3] rising (1/0),
        // [4] done (1/0), [5] segmentIndex
        public const int WhackAMoleStateFloats = 6;

        // [0] clockFrames, [1] cycleStartFrames, [2] idleFrames, [3] cycleIndex,
        // [4] offsetY (metres, negative while airborne), [5] velocityY (metres per frame),
        // [6] airborne (1/0). The flag is explicit because velocityY passes through exactly
        // zero at the top of the arc (float32: -0.01 + 0.01), so it cannot double as the flag.
        public const int HopStateFloats = 7;

        // [0] clockFrames, [1] phase (0 walk / 1 pause / 2 turn), [2] timerFrames,
        // [3] extraTimerFrames, [4] axisX, [5] axisZ, [6] angleRad, [7] angleStepRad,
        // [8] angleTargetRad, [9] offsetX, [10] offsetZ, [11] lastStepX, [12] lastStepZ,
        // [13] drawCount, [14] yawRad
        public const int WanderStateFloats = 15;

        // [0] useAlt (1/0)
        public const int WaitForPlayerStateFloats = 1;

        // DraggedAlong has no state of its own (section 12.12).
        public const int DraggedAlongStateFloats = 0;

        // [0] clockFrames, [1] distanceM (-1 = not initialised)
        public const int PathCollisionStateFloats = 2;

        // [0] clockFrames, [1] distanceM (-1 = not initialised)
        public const int SlopingPathStateFloats = 2;

        // [0] clockFrames, [1] distanceM (-1 = not initialised)
        public const int BackAndForthNoYawStateFloats = 2;

        // --- Turn candidates for the walking classes --------------------------
        private const int TurnCandidateCount = 7;

        private const int PhaseWalk = 0;
        private const int PhasePause = 1;
        private const int PhaseTurn = 2;

        // --- Phase and deterministic draws (D-12) ------------------------------

        /// <summary>
        /// The D-12 phase of one prop: a deterministic value in [0, 1) derived from the
        /// installer's seed. 1 and 0 are equivalent (the phase is a fraction of a period).
        /// </summary>
        /// <remarks>
        /// Pure function of the seed: same seed, same value, on every client and in every
        /// replay. Two different seeds are not guaranteed to differ for any particular bit
        /// pattern, but the mix below spreads a sequential range of seeds across the
        /// interval, which is what the phase bucket and the per-prop offset need.
        /// </remarks>
        public static float Phase01(int phaseSeed)
        {
            return Draw01(phaseSeed, 0);
        }

        /// <summary>
        /// The seed whose <see cref="Phase01"/> is the given author phase: the inverse of the
        /// mix below, so an author's <c>props[].phase</c> override (D-12) travels through the
        /// same one-integer API as the derived hash seed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every Reset* takes a seed, not a phase. An override therefore cannot be a separate
        /// path: it is turned into the seed that yields exactly that draw-0 value, and every
        /// later draw of the class (idle lengths, turn timers) stays a deterministic function
        /// of that seed, as it is for a hashed one. The mix is a bijection on 32 bits (each
        /// step is an xor-shift or a multiply by an odd constant), so the inverse is exact
        /// up to the 24-bit float output: <c>Phase01(SeedForPhase01(p))</c> is p within
        /// 2^-23. A phase of 1 is the same as 0 (a fraction of a period); NaN and negatives
        /// read as 0.
        /// </para>
        /// <para>
        /// Pure, no allocation; the editor importer calls it, the runtime never needs to.
        /// </para>
        /// </remarks>
        public static int SeedForPhase01(float phase01)
        {
            float clamped = phase01;
            if (!(clamped >= 0f)) clamped = 0f;
            if (clamped > 1f) clamped = 1f;
            long target = ((long)((double)clamped * 4294967296.0)) & 0xFFFFFFFFL;

            long mixed = target ^ (target >> 16);
            mixed = ((mixed - 12345L) * DrawMultiplierInverse) & 0xFFFFFFFFL;
            mixed = mixed ^ (mixed >> 13) ^ (mixed >> 26);
            mixed = (mixed * DrawMultiplierInverse) & 0xFFFFFFFFL;
            mixed = mixed ^ (mixed >> 16);
            long seed = (mixed - 0x9E3779B9L) & 0xFFFFFFFFL;
            return unchecked((int)seed);
        }

        // 1099511627 * 2612402787 = 1 (mod 2^32): the multiplier of the mix and its inverse.
        private const long DrawMultiplierInverse = 2612402787L;

        // 32-bit mix built from long arithmetic only: no float in the mixing step, so the
        // value is bit-identical everywhere. Every product stays below 2^32 * 2^31, i.e.
        // inside long range, and each step is masked back to 32 bits. Draw index 0 is the
        // phase; classes use further indices for their own random draws in draw order.
        private static float Draw01(int phaseSeed, int drawIndex)
        {
            long mixed = ((long)phaseSeed) + 0x9E3779B9L + ((long)drawIndex) * 7919L;
            mixed = mixed & 0xFFFFFFFFL;
            mixed = mixed ^ (mixed >> 16);
            mixed = (mixed * 1099511627L) & 0xFFFFFFFFL;
            mixed = mixed ^ (mixed >> 13);
            mixed = (mixed * 1099511627L + 12345L) & 0xFFFFFFFFL;
            mixed = mixed ^ (mixed >> 16);
            mixed = mixed & 0xFFFFFFFFL;
            return ((float)mixed) * (1f / 4294967296f);
        }

        // --- Tables the original keeps as code constants (sections 3.4-3.5) -----

        /// <summary>
        /// The size group of an original collection size id (section 3.4:
        /// sizeGrouping = 0,0,0,1,2,3,3,4,4,4,4,4). Sizes outside 0-11 are clamped.
        /// </summary>
        public static int SizeGroup(int size)
        {
            if (size <= 2) return 0;
            if (size == 3) return 1;
            if (size == 4) return 2;
            if (size == 5 || size == 6) return 3;
            return 4;
        }

        /// <summary>
        /// Hop launch speed in cm per game frame for a size group (section 3.4:
        /// 4 / 6 / 7 / 12 / 15). This is the one speed that stays per frame because the
        /// hop's flight length is (2 * speed - 1) frames.
        /// </summary>
        public static float HopSpeedCmPerFrame(int sizeGroup)
        {
            if (sizeGroup <= 0) return 4f;
            if (sizeGroup == 1) return 6f;
            if (sizeGroup == 2) return 7f;
            if (sizeGroup == 3) return 12f;
            return 15f;
        }

        /// <summary>
        /// The original's speed table for a speed index 0-11, in cm per game frame
        /// (section 3.5). Index 11 is the table's zero, and out-of-range indices are
        /// clamped. 1 cm/frame is the 30.3 cm/s unit the data quotes.
        /// </summary>
        public static float SpeedCmPerFrame(int speedIndex)
        {
            if (speedIndex <= 0) return 0.3f;
            if (speedIndex == 1) return 1f;
            if (speedIndex == 2) return 2f;
            if (speedIndex == 3) return 4f;
            if (speedIndex == 4) return 6f;
            if (speedIndex == 5) return 8f;
            if (speedIndex == 6) return 10f;
            if (speedIndex == 7) return 15f;
            if (speedIndex == 8) return 20f;
            if (speedIndex == 9) return 40f;
            if (speedIndex == 10) return 200f;
            return 0f;
        }

        /// <summary>
        /// WaitForPlayer's trigger radius in metres for a size group (section 3.4:
        /// 200 / 300 / 500 / 700 / 900 cm). This is the behaviour gate, not the host's
        /// wake radius; section 14 says the two must stay separate.
        /// </summary>
        /// <remarks>
        /// PROVISIONAL: reference constant table, gate unmeasured. The measured check is
        /// section 13 items M1/M2.
        /// </remarks>
        public static float PanicRadiusM(int sizeGroup)
        {
            if (sizeGroup <= 0) return 2f;
            if (sizeGroup == 1) return 3f;
            if (sizeGroup == 2) return 5f;
            if (sizeGroup == 3) return 7f;
            return 9f;
        }

        // --- Spin ---------------------------------------------------------------

        /// <summary>
        /// Initialises Spin's state: one clock, started at phase * period so a row of
        /// spinners does not turn in lockstep (D-12).
        /// </summary>
        public static void ResetSpin(float[] state, int phaseSeed)
        {
            if (state == null || state.Length < SpinStateFloats) return;
            state[0] = (TwoPi / SpinRadiansPerFrame) * Phase01(phaseSeed);
        }

        /// <summary>
        /// Spin (motionID 0x15, 7 props): a pure yaw about +Y, no translation, no queries.
        /// </summary>
        /// <remarks>
        /// Section 12.1: euler.y += 0.05 * dt per game frame, i.e. 4.19 s per turn.
        /// PROVISIONAL: 0.05 rad/frame is a reference constant, never measured (M4).
        /// The clock is state rather than the accumulated euler, so the pose is a function
        /// of total elapsed frames and cannot drift.
        /// </remarks>
        public static bool TryStepSpin(float[] state, float dtFrames,
            out Vector3 offsetM, out Vector3 eulerRad)
        {
            offsetM = Vector3.zero;
            eulerRad = Vector3.zero;
            if (!IsStateOk(state, SpinStateFloats)) return false;
            if (!IsValidStep(dtFrames)) return false;

            state[0] = state[0] + dtFrames;
            float yaw = NormalizeYaw(SpinRadiansPerFrame * state[0]);
            if (!KatamariMath.IsFiniteFloat(yaw)) return false;
            eulerRad = new Vector3(0f, yaw, 0f);
            return true;
        }

        // --- Flip ---------------------------------------------------------------

        /// <summary>
        /// Initialises Flip's state: one clock at phase * period (D-12).
        /// </summary>
        public static void ResetFlip(float[] state, int phaseSeed)
        {
            if (state == null || state.Length < FlipStateFloats) return;
            state[0] = (TwoPi / SpinRadiansPerFrame) * Phase01(phaseSeed);
        }

        /// <summary>
        /// Flip (motionID 0x1E, 2 props): the same rate as Spin, about X and with the
        /// opposite sign; no translation, no queries.
        /// </summary>
        /// <remarks>
        /// Section 12.2: euler.x -= 0.05 * dt. PROVISIONAL for the same reason as Spin
        /// (M4): position was measured constant, the rate was not.
        /// </remarks>
        public static bool TryStepFlip(float[] state, float dtFrames,
            out Vector3 offsetM, out Vector3 eulerRad)
        {
            offsetM = Vector3.zero;
            eulerRad = Vector3.zero;
            if (!IsStateOk(state, FlipStateFloats)) return false;
            if (!IsValidStep(dtFrames)) return false;

            state[0] = state[0] + dtFrames;
            float pitch = NormalizeYaw(-SpinRadiansPerFrame * state[0]);
            if (!KatamariMath.IsFiniteFloat(pitch)) return false;
            eulerRad = new Vector3(pitch, 0f, 0f);
            return true;
        }

        // --- Bob ----------------------------------------------------------------

        /// <summary>
        /// Initialises Bob's state: [0] clock = 0, [1] idle = 0-60 frames drawn from the
        /// seed.
        /// </summary>
        public static void ResetBob(float[] state, int phaseSeed)
        {
            if (state == null || state.Length < BobStateFloats) return;
            state[0] = 0f;
            state[1] = BobIdleMaxFrames * Draw01(phaseSeed, 0);
        }

        /// <summary>
        /// Bob (motionID 0x16, 2 props): vertical sine of +-0.15 half heights, after a
        /// random 0-2 s idle. No translation on X/Z, no rotation, no queries.
        /// </summary>
        /// <remarks>
        /// Section 12.3: angle += dt * pi/45 (90 frames = 3.0 s per cycle);
        /// pos.y = spawnY + halfHeight * 0.15 * sin(angle). Measured: 1.61 / 1.35 cm
        /// peak-to-peak with 2.83 / 3.07 s, i.e. +-0.15 h with h = half the model height
        /// (section 5.7), so Bob is NOT PROVISIONAL. Deviation from the pseudocode: while
        /// the idle runs the offset is written as 0 instead of being left untouched, which
        /// makes a batched step and many small steps agree exactly.
        /// </remarks>
        public static bool TryStepBob(float[] state, float dtFrames, float halfHeightM,
            out Vector3 offsetM, out Vector3 eulerRad)
        {
            offsetM = Vector3.zero;
            eulerRad = Vector3.zero;
            if (!IsStateOk(state, BobStateFloats)) return false;
            if (!IsValidStep(dtFrames)) return false;
            if (!IsNonNegativeFinite(halfHeightM)) return false;

            state[0] = state[0] + dtFrames;
            float idleFrames = state[1];
            if (state[0] <= idleFrames) return true;

            float angle = (state[0] - idleFrames) * SineStepRadiansPerFrame;
            float y = halfHeightM * BobAmplitudePerHalfHeight * Mathf.Sin(angle);
            if (!KatamariMath.IsFiniteFloat(y)) return false;
            offsetM = new Vector3(0f, y, 0f);
            return true;
        }

        // --- Sway ---------------------------------------------------------------

        /// <summary>
        /// Initialises Sway's state: one clock started at phase * 90 frames (D-12).
        /// </summary>
        public static void ResetSway(float[] state, int phaseSeed)
        {
            if (state == null || state.Length < SwayStateFloats) return;
            state[0] = (TwoPi / SineStepRadiansPerFrame) * Phase01(phaseSeed);
        }

        /// <summary>
        /// Sway (motionID 0x20, 15 props): a +-10 degree roll about the prop's top pivot,
        /// which moves the prop on an ellipse of half height h = modelBboxM[1] / 2.
        /// No queries.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Section 12.4: angle += dt * pi/45 (3.0 s per full cycle); euler.z = sin(angle) *
        /// 10 degrees. The original rotates the local down vector by the model matrix and
        /// puts it back about the bottom pivot, which reduces to R_z(e) * (sin e, -cos e, 0)
        /// = (sin 2e, -cos 2e, 0). With h = half height the spawn-local offset is therefore
        /// </para>
        /// <code>
        /// x = h * sin(2e), y = h * (1 - cos(2e)), z = 0
        /// </code>
        /// <para>
        /// and NOT the naive 2h sin(e): the measured sunflower (h = 120.25 cm) gives
        /// 82.0 / 7.19 cm, which is 0.342 and 0.030 of the full height, i.e. 2h sin 20 and
        /// h (1 - cos 20). Using sin(e) instead of sin(2e) halves the swing. Measured, so
        /// not PROVISIONAL.
        /// </para>
        /// <para>
        /// applyTranslation is false for the one prop the original skips translation on
        /// (BALANCEDOLL01_C, section 12.4): the tilt stays, the offset is zero.
        /// </para>
        /// </remarks>
        public static bool TryStepSway(float[] state, float dtFrames, float halfHeightM,
            bool applyTranslation, out Vector3 offsetM, out Vector3 eulerRad)
        {
            offsetM = Vector3.zero;
            eulerRad = Vector3.zero;
            if (!IsStateOk(state, SwayStateFloats)) return false;
            if (!IsValidStep(dtFrames)) return false;
            if (!IsNonNegativeFinite(halfHeightM)) return false;

            state[0] = state[0] + dtFrames;
            float angle = state[0] * SineStepRadiansPerFrame;
            float tilt = Mathf.Sin(angle) * SwayAngleRadians;
            if (!KatamariMath.IsFiniteFloat(tilt)) return false;

            float x = 0f;
            float y = 0f;
            if (applyTranslation)
            {
                float twice = 2f * tilt;
                x = halfHeightM * Mathf.Sin(twice);
                y = halfHeightM * (1f - Mathf.Cos(twice));
                if (!KatamariMath.IsFiniteFloat(x)) return false;
                if (!KatamariMath.IsFiniteFloat(y)) return false;
            }
            offsetM = new Vector3(x, y, 0f);
            eulerRad = new Vector3(0f, 0f, tilt);
            return true;
        }

        // --- WhackAMole ---------------------------------------------------------

        /// <summary>
        /// Initialises WhackAMole's state: [0] clock, [1] sweep start, [2] hold frames
        /// (2-7 s drawn from the seed), [3] rising = 1, [4] done = 0, [5] segment index.
        /// </summary>
        public static void ResetWhackAMole(float[] state, int phaseSeed)
        {
            if (state == null || state.Length < WhackAMoleStateFloats) return;
            state[0] = 0f;
            state[1] = 0f;
            state[2] = WhackHoldMinFrames + WhackHoldRangeFrames * Draw01(phaseSeed, 0);
            state[3] = 1f;
            state[4] = 0f;
            state[5] = 0f;
        }

        /// <summary>
        /// WhackAMole (motionID 0x22, 8 props): one vertical quarter-sine sweep of
        /// 3 * halfHeight in 0.75 s, then a 2-7 s hold at the end it reached, then back.
        /// No X/Z motion, no rotation, no queries.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Section 12.5: travel = h + (h - (-h)) = 3h = 1.5 model heights (measured
        /// 12.37 / 16.09 / 165.0 cm), sweep pi/2 at pi/45 per frame = 22.5 frames = 0.75 s
        /// (measured 0.7 s), hold 60 + 150 * draw frames (2-7 s). Rising uses
        /// depth * (1 - sin(angle)), sinking depth * sin(angle), with angle from pi/2 to pi;
        /// at both ends sin is 1 or 0, so the pose is continuous across a segment change.
        /// </para>
        /// <para>
        /// The first segment rises: the pseudocode's initial state flip makes the first
        /// segment sink, which contradicts the measured "0.7 s rise, then sits at the top"
        /// and v2's loop = false. Same constants, opposite first direction; recorded here so
        /// the choice is visible.
        /// </para>
        /// <para>
        /// loop = false ends the machine after the first sweep and holds the reached end
        /// (v2 uses this for "stop at the top"). loop = true alternates forever, as the
        /// pseudocode does. PROVISIONAL: the hold distribution is a reference constant and
        /// the descent was never measured.
        /// </para>
        /// </remarks>
        public static bool TryStepWhackAMole(float[] state, float dtFrames, int phaseSeed,
            float halfHeightM, bool loop, out Vector3 offsetM, out Vector3 eulerRad)
        {
            offsetM = Vector3.zero;
            eulerRad = Vector3.zero;
            if (!IsStateOk(state, WhackAMoleStateFloats)) return false;
            if (!IsValidStep(dtFrames)) return false;
            if (!IsNonNegativeFinite(halfHeightM)) return false;

            state[0] = state[0] + dtFrames;

            bool finished = state[4] >= 0.5f;
            if (!finished)
            {
                // A segment is one sweep plus the hold that follows it. Advance while the
                // clock has passed whole segments.
                float segmentStart = state[1];
                float hold = state[2];
                while (state[0] >= segmentStart + WhackRiseFrames + hold)
                {
                    segmentStart = segmentStart + WhackRiseFrames + hold;
                    if (!loop)
                    {
                        state[4] = 1f;
                        finished = true;
                        break;
                    }
                    state[5] = state[5] + 1f;
                    state[3] = 1f - state[3];
                    hold = WhackHoldMinFrames + WhackHoldRangeFrames * Draw01(phaseSeed, (int)state[5]);
                    state[1] = segmentStart;
                    state[2] = hold;
                }
                if (!finished)
                {
                    state[1] = segmentStart;
                    state[2] = hold;
                }
            }

            float elapsed = state[0] - state[1];
            if (elapsed < 0f) elapsed = 0f;
            if (elapsed > WhackRiseFrames) elapsed = WhackRiseFrames;
            float angle = (Mathf.PI / 2f) + elapsed * SineStepRadiansPerFrame;
            float sine = Mathf.Sin(angle);
            float depth = WhackTravelPerHalfHeight * halfHeightM;
            float y = state[3] >= 0.5f ? depth * (1f - sine) : depth * sine;
            if (!KatamariMath.IsFiniteFloat(y)) return false;
            offsetM = new Vector3(0f, y, 0f);
            return true;
        }

        // --- Hop ----------------------------------------------------------------

        /// <summary>
        /// Initialises Hop's state: [0] clock, [1] cycle start, [2] idle frames 0-8.5 s
        /// drawn from the seed, [3] cycle index, [4] height offset 0, [5] vertical velocity 0,
        /// [6] airborne 0.
        /// </summary>
        public static void ResetHop(float[] state, int phaseSeed)
        {
            if (state == null || state.Length < HopStateFloats) return;
            state[0] = 0f;
            state[1] = 0f;
            state[2] = HopIdleMaxFrames * Draw01(phaseSeed, 0);
            state[3] = 0f;
            state[4] = 0f;
            state[5] = 0f;
            state[6] = 0f;
        }

        /// <summary>
        /// Hop (motionID 0x19, 18 props): wait 0-8.5 s, drop and fall back to the spawn
        /// height under the original's gravity. Vertical only, no rotation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Section 12.6 and section 15.1 item 2: the original adds gravity first, then moves
        /// (vy += g * dt, then y += vy * dt), with vy starting at -speed (the size group's
        /// 4/6/7/12/15 cm per frame) and g = 1 cm/frame^2. At speed 4 and dt = 1 the height
        /// is -3, -5, -6, -6, -5, -3, 0 cm: 6.0 cm deep, two frames at the bottom, back at the
        /// spawn height after seven frames. That is what item M3 settled (measured 6.05 /
        /// 6.60 cm); the older 8 cm "theory" was the continuous v^2/2g and the wrong baseline.
        /// </para>
        /// <para>
        /// The recurrence is kept instead of its closed form - v * t + g * t * (t + 1) / 2,
        /// because the two are only equal at dt = 1. One dt = 2 step is vy += 2g then
        /// y += 2vy, which sits 4 cm below the spawn after two game frames where two dt = 1
        /// steps sit 5 cm: exactly one g apart, as section 15.1 records. Hop's update tier is
        /// 30 Hz (LEVEL_FORMAT_V2 6.7.3), so its host step is dt = 1; a batched frame then
        /// reproduces the original's own hitched arithmetic instead of silently quantising it.
        /// </para>
        /// <para>
        /// The height and the vertical velocity live in state, so freeze/resume continues the
        /// arc without a jump; this is the one class whose batched step is not equal to the
        /// same number of one-frame steps (see the class remarks). PROVISIONAL: only the
        /// 0-8.5 s spacing (255 * draw frames) is a reference constant - the measured spacing
        /// is "3.4-5 s or more" (section 15.2, the spacing half of M3 / H1).
        /// </para>
        /// </remarks>
        public static bool TryStepHop(float[] state, float dtFrames, int phaseSeed,
            float hopSpeedCmPerFrame, out Vector3 offsetM, out Vector3 eulerRad)
        {
            offsetM = Vector3.zero;
            eulerRad = Vector3.zero;
            if (!IsStateOk(state, HopStateFloats)) return false;
            if (!IsValidStep(dtFrames)) return false;
            if (!IsPositiveFinite(hopSpeedCmPerFrame)) return false;

            // Everything is computed in locals and committed once, so a refusal (a non-finite
            // clock or trajectory) leaves the caller's row untouched.
            float clock = state[0] + dtFrames;
            if (!KatamariMath.IsFiniteFloat(clock)) return false;
            float cycleStart = state[1];
            float idle = state[2];
            float cycleIndex = state[3];
            float y = state[4];
            float vy = state[5];
            bool airborne = state[6] >= 0.5f;

            if (!airborne)
            {
                // Resting: no time passed, or the idle has not run out yet. The clock still
                // advances, because the idle is counted from it.
                if (dtFrames <= 0f || clock - cycleStart < idle)
                {
                    state[0] = clock;
                    return true;
                }
                airborne = true;
                vy = -(hopSpeedCmPerFrame * MetresPerCentimetre);
            }

            // The original's order and its own arithmetic for any dt: velocity first, then
            // displacement. A dt = 2 step therefore behaves like the original's hitched frame
            // (section 15.1 item 2), not like two quantised dt = 1 steps.
            vy = vy + HopGravityMetresPerFrame2 * dtFrames;
            y = y + vy * dtFrames;
            if (!KatamariMath.IsFiniteFloat(y) || !KatamariMath.IsFiniteFloat(vy)) return false;

            if (y >= 0f)
            {
                // Back at the spawn height (or a batched step overshot it): land and draw the
                // next idle from the seed. The landing frame itself reports height 0.
                y = 0f;
                vy = 0f;
                airborne = false;
                cycleStart = clock;
                cycleIndex = cycleIndex + 1f;
                idle = HopIdleMaxFrames * Draw01(phaseSeed, (int)cycleIndex);
            }

            state[0] = clock;
            state[1] = cycleStart;
            state[2] = idle;
            state[3] = cycleIndex;
            state[4] = y;
            state[5] = vy;
            state[6] = airborne ? 1f : 0f;
            offsetM = new Vector3(0f, y, 0f);
            return true;
        }

        // --- SporadicWalk and Clouds (one state machine, sections 12.7-12.8) ----

        /// <summary>
        /// Initialises the shared walking state (SporadicWalk and Clouds):
        /// [0] clock, [1] phase, [2] timer, [3] extra timer, [4..5] base direction,
        /// [6] angle, [7] angle step, [8] angle target, [9..10] offset, [11..12] last step,
        /// [13] draw count, [14] yaw.
        /// </summary>
        /// <remarks>
        /// Section 12.7's init: the initial direction is world -X (not "forward"), the
        /// first random turn is 120 + 570 * draw frames away, and the walk/pause burst
        /// counter starts at 30 frames.
        /// </remarks>
        public static void ResetSporadicWalk(float[] state, int phaseSeed)
        {
            ResetWander(state, phaseSeed, WalkTurnMinFrames, WalkTurnRangeFrames);
        }

        /// <summary>
        /// Initialises the Clouds state: the same layout, with the clouds' 90 + 600 * draw
        /// turn interval (section 12.8).
        /// </summary>
        public static void ResetClouds(float[] state, int phaseSeed)
        {
            ResetWander(state, phaseSeed, CloudsTurnMinFrames, CloudsTurnRangeFrames);
        }

        private static void ResetWander(float[] state, int phaseSeed,
            float turnMinFrames, float turnRangeFrames)
        {
            if (state == null || state.Length < WanderStateFloats) return;
            state[0] = 0f;
            state[1] = PhaseWalk;
            state[2] = turnMinFrames + turnRangeFrames * Draw01(phaseSeed, 0);
            state[3] = WalkPauseMinFrames;
            state[4] = -1f;
            state[5] = 0f;
            state[6] = 0f;
            state[7] = 0f;
            state[8] = 0f;
            state[9] = 0f;
            state[10] = 0f;
            state[11] = 0f;
            state[12] = 0f;
            state[13] = 1f;
            state[14] = 0f;
        }

        /// <summary>
        /// SporadicWalk (motionID 0x0B/0x0C, 31 props): walks at a constant speed, turns a
        /// random +-90 degrees every 4-23 s, and now and then stops for 1-2 s.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Section 12.7. speedMps is the resolved type speed (speedTable 1 cm/frame is the
        /// measured 30.3 cm/s). The offset returned is in WORLD axes from spawnPos, with
        /// world yaw, because the original walks along world -X and yaws with a world
        /// atan2. Ground following, the zone test and the "does the next step stay in my
        /// zone" rays belong to the host: it reports a refused step through
        /// forwardBlocked, and offers the candidates it accepts as bits in
        /// allowedTurnMask (bit 0..6 = +45, -45, +90, -90, +135, -135, 180 degrees, tried
        /// in that order; 0x7F means "all allowed"). A refused step is undone, that frame
        /// moves nothing, and the new direction is then turned over 12 frames - the original
        /// reverts to the previous position and turns, which this reproduces without needing
        /// the zone rays in the math layer.
        /// </para>
        /// <para>
        /// PROVISIONAL: the timers and the turn table are reference constants; what was
        /// observed is the net speed of 20-25 cm/s and the instantaneous 30.3 (section 4.1).
        /// </para>
        /// <para>
        /// Facing is the formula section 15.1 item 1 settled: yaw = pi + atan2(vx, vz) with v
        /// the velocity direction, for walking and turning alike (the reference implementation
        /// writes TAU/2 + atan2 and uses it in both places). The binding convention that comes
        /// with it: v = (0, 0, -1) gives atan2(0, -1) + pi = 2*pi, so MODEL ZERO YAW MUST FACE
        /// -Z. A prefab authored +Z forward walks backwards; that binding is item S1 in
        /// section 15.2, and the tests pin it.
        /// </para>
        /// </remarks>
        public static bool TryStepSporadicWalk(float[] state, float dtFrames, int phaseSeed,
            float speedMps, int allowedTurnMask, bool forwardBlocked,
            out Vector3 offsetM, out Vector3 eulerRad)
        {
            return TryStepWander(state, dtFrames, phaseSeed, speedMps, allowedTurnMask,
                forwardBlocked, true, false, out offsetM, out eulerRad);
        }

        /// <summary>
        /// Clouds (motionID 0x23, 5 props): the walking machine with no pause, no yaw and
        /// no ground test, at the type speed (10 cm/frame = 3.03 m/s measured).
        /// </summary>
        /// <remarks>
        /// Section 12.8: ignoreYaw, timer 90 + 600 * draw (3-23 s), no Pause phase and no
        /// extra timer. offsetM is a world-axis offset from spawnPos, which for a cloud at
        /// a fixed altitude means y stays 0. PROVISIONAL: the turn interval is a reference
        /// constant; only the speed was measured. Clouds shares the walking machine but
        /// passes ignoreYaw, so it never evaluates the walk facing formula of section 15.1
        /// item 1 - a cloud keeps its spawn orientation, which is why the 180-degree
        /// prefab-axis risk (item S1) does not apply to it.
        /// </remarks>
        public static bool TryStepClouds(float[] state, float dtFrames, int phaseSeed,
            float speedMps, int allowedTurnMask,
            out Vector3 offsetM, out Vector3 eulerRad)
        {
            return TryStepWander(state, dtFrames, phaseSeed, speedMps, allowedTurnMask,
                false, false, true, out offsetM, out eulerRad);
        }

        private static bool TryStepWander(float[] state, float dtFrames, int phaseSeed,
            float speedMps, int allowedTurnMask, bool forwardBlocked, bool allowPause,
            bool ignoreYaw, out Vector3 offsetM, out Vector3 eulerRad)
        {
            offsetM = Vector3.zero;
            eulerRad = Vector3.zero;
            if (!IsStateOk(state, WanderStateFloats)) return false;
            if (!IsValidStep(dtFrames)) return false;
            if (!IsNonNegativeFinite(speedMps)) return false;

            if (forwardBlocked)
            {
                // The host refused the last step: undo it, then pick a new heading. This
                // frame moves nothing and does not advance the new turn yet (the original
                // picks the direction inside a failed walk and turns from the next frame),
                // so a refused spot is never asked for twice and the facing does not jump.
                state[9] = state[9] - state[11];
                state[10] = state[10] - state[12];
                state[11] = 0f;
                state[12] = 0f;
                ChooseTurn(state, phaseSeed, allowedTurnMask);
                offsetM = new Vector3(state[9], 0f, state[10]);
                if (!ignoreYaw) eulerRad = new Vector3(0f, state[14], 0f);
                if (!KatamariMath.IsFiniteVector(offsetM) ||
                    !KatamariMath.IsFiniteVector(eulerRad))
                {
                    offsetM = Vector3.zero;
                    eulerRad = Vector3.zero;
                    return false;
                }
                return true;
            }

            int phase = (int)state[1];
            if (phase == PhasePause)
            {
                state[3] = state[3] - dtFrames;
                if (state[3] < 0f)
                {
                    state[1] = PhaseWalk;
                    state[3] = WalkBurstMinFrames + WalkBurstRangeFrames * NextDraw01(state, phaseSeed);
                }
            }
            else if (phase == PhaseTurn)
            {
                state[6] = state[6] + state[7] * dtFrames;
                float remaining = (state[8] - state[6]) * state[7];
                if (remaining <= 0f)
                {
                    state[6] = state[8];
                    state[7] = 0f;
                    state[1] = PhaseWalk;
                    state[2] = WalkTurnMinFrames + WalkTurnRangeFrames * NextDraw01(state, phaseSeed);
                }
            }
            else
            {
                state[2] = state[2] - dtFrames;
                if (state[2] < 0f)
                {
                    StartRandomTurn(state, phaseSeed);
                }
                else if (allowPause)
                {
                    state[3] = state[3] - dtFrames;
                    if (state[3] < 0f)
                    {
                        state[1] = PhasePause;
                        state[3] = WalkPauseMinFrames + WalkPauseRangeFrames * NextDraw01(state, phaseSeed);
                    }
                }
            }

            // Velocity is the base direction rotated by the current turn angle, in world
            // axes; the axis is unit length by construction, and the rotation preserves it.
            float angle = state[6];
            float axisX = state[4];
            float axisZ = state[5];
            float cos = Mathf.Cos(angle);
            float sin = Mathf.Sin(angle);
            float velocityX = axisX * cos + axisZ * sin;
            float velocityZ = -axisX * sin + axisZ * cos;
            if (!KatamariMath.IsFiniteFloat(velocityX)) return false;
            if (!KatamariMath.IsFiniteFloat(velocityZ)) return false;

            if ((int)state[1] != PhasePause)
            {
                float step = speedMps * (dtFrames / GameFramesPerSecond);
                if (!KatamariMath.IsFiniteFloat(step)) return false;
                state[11] = velocityX * step;
                state[12] = velocityZ * step;
                state[9] = state[9] + state[11];
                state[10] = state[10] + state[12];
            }

            if (!ignoreYaw)
            {
                // Section 15.1 item 1: pi + atan2(vx, vz), one formula for walking and
                // turning alike, then normalized into [0, 2*pi) so that v = (0, 0, -1) is 0
                // (model zero yaw faces -Z) instead of the interval's 2*pi end.
                float yaw = NormalizeYaw(Mathf.PI + Mathf.Atan2(velocityX, velocityZ));
                if (!KatamariMath.IsFiniteFloat(yaw)) return false;
                state[14] = yaw;
                eulerRad = new Vector3(0f, yaw, 0f);
            }

            offsetM = new Vector3(state[9], 0f, state[10]);
            if (!KatamariMath.IsFiniteVector(offsetM)) return false;
            return true;
        }

        private static void StartRandomTurn(float[] state, int phaseSeed)
        {
            // axis = the direction being walked, angle 0; the sign is a seeded draw.
            float axisX = state[4];
            float axisZ = state[5];
            float angle = state[6];
            float cos = Mathf.Cos(angle);
            float sin = Mathf.Sin(angle);
            state[4] = axisX * cos + axisZ * sin;
            state[5] = -axisX * sin + axisZ * cos;
            if (!TryNormalizeDirection(state)) return;

            float sign = NextDraw01(state, phaseSeed) >= 0.5f ? 1f : -1f;
            state[6] = 0f;
            state[7] = sign * WalkRandomTurnStepRadians;
            state[8] = sign * (Mathf.PI / 2f);
            state[1] = PhaseTurn;
        }

        private static void ChooseTurn(float[] state, int phaseSeed, int allowedTurnMask)
        {
            float axisX = state[4];
            float axisZ = state[5];
            float angle = state[6];
            float cos = Mathf.Cos(angle);
            float sin = Mathf.Sin(angle);
            state[4] = axisX * cos + axisZ * sin;
            state[5] = -axisX * sin + axisZ * cos;
            if (!TryNormalizeDirection(state)) return;

            float chosen = Mathf.PI;
            for (int candidate = 0; candidate < TurnCandidateCount; candidate++)
            {
                if ((allowedTurnMask & (1 << candidate)) == 0) continue;
                chosen = TurnCandidateRadians(candidate);
                break;
            }
            state[6] = 0f;
            state[7] = chosen / WalkCandidateTurnFrames;
            state[8] = chosen;
            state[1] = PhaseTurn;
        }

        private static float TurnCandidateRadians(int candidate)
        {
            if (candidate == 0) return Mathf.PI / 4f;
            if (candidate == 1) return -Mathf.PI / 4f;
            if (candidate == 2) return Mathf.PI / 2f;
            if (candidate == 3) return -Mathf.PI / 2f;
            if (candidate == 4) return 3f * Mathf.PI / 4f;
            if (candidate == 5) return -3f * Mathf.PI / 4f;
            return Mathf.PI;
        }

        // Renormalises the base direction in place and reports whether it is usable.
        private static bool TryNormalizeDirection(float[] state)
        {
            float x = state[4];
            float z = state[5];
            float lengthSquared = x * x + z * z;
            if (!KatamariMath.IsFiniteFloat(lengthSquared)) return false;
            if (lengthSquared <= 0f)
            {
                state[4] = -1f;
                state[5] = 0f;
                return true;
            }
            float length = Mathf.Sqrt(lengthSquared);
            if (!KatamariMath.IsFiniteFloat(length) || length <= 0f) return false;
            state[4] = x / length;
            state[5] = z / length;
            return true;
        }

        // Draw index 13 walks forward one draw per call; the index is part of the state so
        // the sequence is deterministic and survives a freeze.
        private static float NextDraw01(float[] state, int phaseSeed)
        {
            int index = (int)state[13];
            state[13] = state[13] + 1f;
            return Draw01(phaseSeed, index);
        }

        // --- WaitForPlayer ------------------------------------------------------

        /// <summary>
        /// Initialises WaitForPlayer's state: [0] useAlt = 0.
        /// </summary>
        public static void ResetWaitForPlayer(float[] state)
        {
            if (state == null || state.Length < WaitForPlayerStateFloats) return;
            state[0] = 0f;
        }

        /// <summary>
        /// WaitForPlayer (motionID 0x00 / 0x04, 15 props): no motion of its own; reports
        /// whether the alt action's condition is met this frame.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Section 12.11. motionId 0x00 (7 props) triggers when the observer is closer than
        /// the size group's panic radius AND the group is at most 2 (so 200 cm for the
        /// Cancer props). motionId 0x04 (8 props) triggers on same zone AND parentRolled;
        /// the reference implementation hard-codes parentRolled = false, so those 8 never
        /// fire there and the original's real condition is unknown (item M2). The caller
        /// passes both facts because the math layer has no zone data.
        /// </para>
        /// <para>
        /// PROVISIONAL: the gate and the post-trigger action are unmeasured (M1/M2). The
        /// pose is always identity - the original hands the prop to the alt action, which
        /// this library does not implement, so "triggered" is the whole output. dt is not
        /// an argument because nothing advances.
        /// </para>
        /// </remarks>
        public static bool TryStepWaitForPlayer(float[] state, int motionId,
            float distanceToObserverM, int sizeGroup, bool observerInSameZone,
            bool parentRolled, out bool triggered, out Vector3 offsetM, out Vector3 eulerRad)
        {
            triggered = false;
            offsetM = Vector3.zero;
            eulerRad = Vector3.zero;
            if (!IsStateOk(state, WaitForPlayerStateFloats)) return false;
            if (!IsNonNegativeFinite(distanceToObserverM)) return false;

            bool met;
            if (motionId == 0x00)
            {
                bool closeEnough = distanceToObserverM < PanicRadiusM(sizeGroup);
                bool smallEnough = sizeGroup <= 2;
                met = closeEnough && smallEnough;
            }
            else if (motionId == 0x04)
            {
                met = observerInSameZone && parentRolled;
            }
            else
            {
                return false;
            }

            state[0] = met ? 1f : 0f;
            triggered = met;
            return true;
        }

        // --- DraggedAlong -------------------------------------------------------

        /// <summary>
        /// DraggedAlong (motionID 0x25, 15 props): the prop does nothing; its pose comes
        /// from its parent's transform.
        /// </summary>
        /// <remarks>
        /// Section 12.12: the original's SetZone branch returns immediately, and the position
        /// is entirely the parent's. There is no state, no phase and no randomness, so this
        /// entry point exists mainly to complete the 13-class dispatch table; it validates
        /// its step and always reports the identity pose. The host must still apply the
        /// parent transform itself - that is the class's definition.
        /// </remarks>
        public static bool TryStepDraggedAlong(float dtFrames,
            out Vector3 offsetM, out Vector3 eulerRad)
        {
            offsetM = Vector3.zero;
            eulerRad = Vector3.zero;
            return IsValidStep(dtFrames);
        }

        // --- Path classes (sections 12.9-12.10) ---------------------------------

        /// <summary>
        /// Initialises PathCollision's state: [0] clock = 0, [1] distance = -1 (the first
        /// step resolves the start from spawnPos).
        /// </summary>
        public static void ResetPathCollision(float[] state)
        {
            ResetPathState(state, PathCollisionStateFloats);
        }

        /// <summary>
        /// Initialises SlopingPath's state: the same two slots.
        /// </summary>
        public static void ResetSlopingPath(float[] state)
        {
            ResetPathState(state, SlopingPathStateFloats);
        }

        /// <summary>
        /// Initialises BackAndForthNoYaw's state: the same two slots.
        /// </summary>
        public static void ResetBackAndForthNoYaw(float[] state)
        {
            ResetPathState(state, BackAndForthNoYawStateFloats);
        }

        private static void ResetPathState(float[] state, int requiredFloats)
        {
            if (state == null || state.Length < requiredFloats) return;
            state[0] = 0f;
            state[1] = -1f;
        }

        /// <summary>
        /// PathCollision (motionID via the backup action, 7-8 props): a constant-speed walk
        /// along a world polyline, with the yaw easing into each segment's heading.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Sections 12.9 and 13 M7. The prop starts at the polyline point nearest spawnPos
        /// (not index 0), then walks at speedMps, sinking by halfHeightM so its bounding
        /// box's bottom sits on the path (pos = point - (0, b, 0)). Yaw is
        /// PI + atan2(dx, dz) of the current segment, eased from the previous segment's
        /// heading across the current segment's length, which is the pseudocode's
        /// "turn over d / speed frames" written as a function of distance instead of time.
        /// closed = true wraps at the end (the original's pathIndex % pointCount);
        /// closed = false stops at the last point.
        /// </para>
        /// <para>
        /// The returned position is absolute world. PROVISIONAL: the easing is a reading of
        /// the pseudocode, never measured (M7); pitch from a ground collision normal
        /// (adjustPitch) is not computed here at all, because that normal is a host query -
        /// PathCollision returns yaw only, so a host that wants terrain pitch writes it
        /// itself.
        /// </para>
        /// </remarks>
        public static bool TryStepPathCollision(float[] state, float dtFrames,
            Vector3 spawnPositionM, float[] pathPointsM, int pointCount, float speedMps,
            float halfHeightM, bool closed, out Vector3 positionM, out Vector3 eulerRad)
        {
            positionM = Vector3.zero;
            eulerRad = Vector3.zero;
            if (!IsStateOk(state, PathCollisionStateFloats)) return false;
            if (!IsValidStep(dtFrames)) return false;
            if (!IsNonNegativeFinite(speedMps)) return false;
            if (!IsNonNegativeFinite(halfHeightM)) return false;
            if (!KatamariMath.IsFiniteVector(spawnPositionM)) return false;
            if (!IsPathOk(pathPointsM, pointCount)) return false;

            if (!TryInitPathDistance(state, spawnPositionM, pathPointsM, pointCount, closed))
                return false;
            state[0] = state[0] + dtFrames;
            state[1] = state[1] + speedMps * (dtFrames / GameFramesPerSecond);

            Vector3 sample;
            int segmentIndex;
            float fraction;
            if (!TrySamplePath(pathPointsM, pointCount, closed, state[1],
                out sample, out segmentIndex, out fraction))
                return false;

            int previous = PreviousSegment(pointCount, closed, segmentIndex);
            float targetYaw = SegmentYaw(pathPointsM, pointCount, closed, segmentIndex);
            float previousYaw = SegmentYaw(pathPointsM, pointCount, closed, previous);
            float yaw = NormalizeYaw(EaseAngle(previousYaw, targetYaw, fraction));
            if (!KatamariMath.IsFiniteFloat(yaw)) return false;

            positionM = new Vector3(sample.x, sample.y - halfHeightM, sample.z);
            if (!KatamariMath.IsFiniteVector(positionM)) return false;
            eulerRad = new Vector3(0f, yaw, 0f);
            return true;
        }

        /// <summary>
        /// SlopingPath (motionID 0x27, 2 props): the same walk, with the body pitching to
        /// the path's slope over the first quarter of each segment.
        /// </summary>
        /// <remarks>
        /// Section 12.9: same movement as PathCollision, plus adjustPitch with
        /// angleTarget = fromPathSlope and the pitch eased over 0.25 * d / speed frames,
        /// which here is the first quarter of the segment's length. Pitch is about X, with
        /// the sign Unity uses for "nose down on a descent": -atan2(dy, horizontal).
        /// PROVISIONAL: easing and sign are unmeasured (M7).
        /// </remarks>
        public static bool TryStepSlopingPath(float[] state, float dtFrames,
            Vector3 spawnPositionM, float[] pathPointsM, int pointCount, float speedMps,
            float halfHeightM, bool closed, out Vector3 positionM, out Vector3 eulerRad)
        {
            positionM = Vector3.zero;
            eulerRad = Vector3.zero;
            if (!IsStateOk(state, SlopingPathStateFloats)) return false;
            if (!IsValidStep(dtFrames)) return false;
            if (!IsNonNegativeFinite(speedMps)) return false;
            if (!IsNonNegativeFinite(halfHeightM)) return false;
            if (!KatamariMath.IsFiniteVector(spawnPositionM)) return false;
            if (!IsPathOk(pathPointsM, pointCount)) return false;

            if (!TryInitPathDistance(state, spawnPositionM, pathPointsM, pointCount, closed))
                return false;
            state[0] = state[0] + dtFrames;
            state[1] = state[1] + speedMps * (dtFrames / GameFramesPerSecond);

            Vector3 sample;
            int segmentIndex;
            float fraction;
            if (!TrySamplePath(pathPointsM, pointCount, closed, state[1],
                out sample, out segmentIndex, out fraction))
                return false;

            int previous = PreviousSegment(pointCount, closed, segmentIndex);
            float targetYaw = SegmentYaw(pathPointsM, pointCount, closed, segmentIndex);
            float previousYaw = SegmentYaw(pathPointsM, pointCount, closed, previous);
            float yaw = NormalizeYaw(EaseAngle(previousYaw, targetYaw, fraction));

            float targetPitch = SegmentPitch(pathPointsM, pointCount, closed, segmentIndex);
            float previousPitch = SegmentPitch(pathPointsM, pointCount, closed, previous);
            float pitchFraction = fraction * 4f;
            if (pitchFraction > 1f) pitchFraction = 1f;
            float pitch = previousPitch + (targetPitch - previousPitch) * pitchFraction;
            if (!KatamariMath.IsFiniteFloat(yaw)) return false;
            if (!KatamariMath.IsFiniteFloat(pitch)) return false;

            positionM = new Vector3(sample.x, sample.y - halfHeightM, sample.z);
            if (!KatamariMath.IsFiniteVector(positionM)) return false;
            eulerRad = new Vector3(pitch, yaw, 0f);
            return true;
        }

        /// <summary>
        /// BackAndForthNoYaw (motionID 0x1D, 1 prop): constant-speed ping-pong along a world
        /// polyline, no yaw at all.
        /// </summary>
        /// <remarks>
        /// Section 12.10: the prop walks to the end, reverses at the end point and walks
        /// back; advancePoint returns false when the next index would leave the polyline,
        /// so both ends turn around. That is the same as folding the travelled distance
        /// into [0, L] and back, which is what this function does, so the motion is a
        /// function of distance and cannot drift. euler is identity: "no yaw" means the
        /// prop keeps its spawn orientation, and the host must not overwrite it with a path
        /// heading.
        /// </remarks>
        public static bool TryStepBackAndForthNoYaw(float[] state, float dtFrames,
            Vector3 spawnPositionM, float[] pathPointsM, int pointCount, float speedMps,
            float halfHeightM, out Vector3 positionM, out Vector3 eulerRad)
        {
            positionM = Vector3.zero;
            eulerRad = Vector3.zero;
            if (!IsStateOk(state, BackAndForthNoYawStateFloats)) return false;
            if (!IsValidStep(dtFrames)) return false;
            if (!IsNonNegativeFinite(speedMps)) return false;
            if (!IsNonNegativeFinite(halfHeightM)) return false;
            if (!KatamariMath.IsFiniteVector(spawnPositionM)) return false;
            if (!IsPathOk(pathPointsM, pointCount)) return false;

            if (!TryInitPathDistance(state, spawnPositionM, pathPointsM, pointCount, false))
                return false;
            state[0] = state[0] + dtFrames;
            state[1] = state[1] + speedMps * (dtFrames / GameFramesPerSecond);

            float length = 0f;
            if (!TryTotalPathLength(pathPointsM, pointCount, false, out length)) return false;
            if (!(length > 0f)) return false;

            float folded = state[1] % (2f * length);
            if (folded < 0f) folded = folded + 2f * length;
            if (folded > length) folded = 2f * length - folded;

            Vector3 sample;
            int segmentIndex;
            float fraction;
            if (!TrySamplePath(pathPointsM, pointCount, false, folded,
                out sample, out segmentIndex, out fraction))
                return false;

            positionM = new Vector3(sample.x, sample.y - halfHeightM, sample.z);
            if (!KatamariMath.IsFiniteVector(positionM)) return false;
            return true;
        }

        // The first path step places the prop at the nearest polyline point (section 12.9),
        // not at index 0. Section 12.9 also mentions a "second nearest, farther" tie-break;
        // its stated purpose is not restarting at point 0, which nearest-point init already
        // achieves, so it is not reproduced here (PROVISIONAL reading, tied to M7).
        private static bool TryInitPathDistance(float[] state, Vector3 spawnPositionM,
            float[] pathPointsM, int pointCount, bool closed)
        {
            if (state[1] >= 0f) return true;
            if (!KatamariMath.IsFiniteVector(spawnPositionM)) return false;

            float bestDistanceSquared = 0f;
            float bestAlong = 0f;
            float along = 0f;
            bool found = false;
            for (int point = 0; point < pointCount; point++)
            {
                int offset = point * 3;
                float dx = pathPointsM[offset] - spawnPositionM.x;
                float dy = pathPointsM[offset + 1] - spawnPositionM.y;
                float dz = pathPointsM[offset + 2] - spawnPositionM.z;
                float distanceSquared = dx * dx + dy * dy + dz * dz;
                if (!found || distanceSquared < bestDistanceSquared)
                {
                    found = true;
                    bestDistanceSquared = distanceSquared;
                    bestAlong = along;
                }
                float segmentLength;
                if (point + 1 < pointCount)
                {
                    segmentLength = SegmentLength(pathPointsM, point, point + 1);
                }
                else if (closed)
                {
                    segmentLength = SegmentLength(pathPointsM, point, 0);
                }
                else
                {
                    segmentLength = 0f;
                }
                along = along + segmentLength;
            }
            if (!found) return false;

            float totalLength;
            if (!TryTotalPathLength(pathPointsM, pointCount, closed, out totalLength)) return false;
            if (closed && totalLength > 0f)
            {
                bestAlong = bestAlong % totalLength;
                if (bestAlong < 0f) bestAlong = bestAlong + totalLength;
            }
            state[1] = bestAlong;
            return true;
        }

        private static bool IsPathOk(float[] pathPointsM, int pointCount)
        {
            if (pathPointsM == null) return false;
            if (pointCount < 2) return false;
            if (pointCount > pathPointsM.Length / 3) return false;
            for (int i = 0; i < pointCount * 3; i++)
            {
                if (!KatamariMath.IsFiniteFloat(pathPointsM[i])) return false;
            }
            int segments;
            if (!TrySegmentCount(pointCount, false, out segments)) return false;
            for (int segment = 0; segment < segments; segment++)
            {
                float length = SegmentLength(pathPointsM, segment, segment + 1);
                if (!KatamariMath.IsFiniteFloat(length)) return false;
                if (length < MinimumPathSegmentM) return false;
            }
            return true;
        }

        private static bool TrySegmentCount(int pointCount, bool closed, out int segmentCount)
        {
            segmentCount = 0;
            if (pointCount < 2) return false;
            segmentCount = closed ? pointCount : pointCount - 1;
            return true;
        }

        private static int NextPointIndex(int point, int pointCount, bool closed)
        {
            int next = point + 1;
            if (next >= pointCount) next = closed ? 0 : pointCount - 1;
            return next;
        }

        private static float SegmentLength(float[] points, int fromPoint, int toPoint)
        {
            int from = fromPoint * 3;
            int to = toPoint * 3;
            float dx = points[to] - points[from];
            float dy = points[to + 1] - points[from + 1];
            float dz = points[to + 2] - points[from + 2];
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static float PathSegmentLength(float[] points, int pointCount, bool closed,
            int segmentIndex)
        {
            int from = segmentIndex;
            int to = NextPointIndex(segmentIndex, pointCount, closed);
            return SegmentLength(points, from, to);
        }

        private static int PreviousSegment(int pointCount, bool closed, int segmentIndex)
        {
            int segmentCount;
            if (!TrySegmentCount(pointCount, closed, out segmentCount)) return segmentIndex;
            int previous = segmentIndex - 1;
            if (previous < 0) previous = closed ? segmentCount - 1 : 0;
            return previous;
        }

        private static float SegmentYaw(float[] points, int pointCount, bool closed,
            int segmentIndex)
        {
            int from = segmentIndex * 3;
            int to = NextPointIndex(segmentIndex, pointCount, closed) * 3;
            float dx = points[to] - points[from];
            float dz = points[to + 2] - points[from + 2];
            // Section 12.9: euler.y = PI + atan2(dx, dz).
            return Mathf.PI + Mathf.Atan2(dx, dz);
        }

        private static float SegmentPitch(float[] points, int pointCount, bool closed,
            int segmentIndex)
        {
            int from = segmentIndex * 3;
            int to = NextPointIndex(segmentIndex, pointCount, closed) * 3;
            float dx = points[to] - points[from];
            float dy = points[to + 1] - points[from + 1];
            float dz = points[to + 2] - points[from + 2];
            float horizontal = Mathf.Sqrt(dx * dx + dz * dz);
            if (!(horizontal > 0f)) return 0f;
            return -Mathf.Atan2(dy, horizontal);
        }

        private static float EaseAngle(float fromYaw, float toYaw, float fraction)
        {
            float delta = toYaw - fromYaw;
            while (delta > Mathf.PI) delta = delta - TwoPi;
            while (delta < -Mathf.PI) delta = delta + TwoPi;
            return fromYaw + delta * fraction;
        }

        private static bool TryTotalPathLength(float[] points, int pointCount, bool closed,
            out float totalLength)
        {
            totalLength = 0f;
            int segmentCount;
            if (!TrySegmentCount(pointCount, closed, out segmentCount)) return false;
            float total = 0f;
            for (int segment = 0; segment < segmentCount; segment++)
            {
                float length = PathSegmentLength(points, pointCount, closed, segment);
                if (!KatamariMath.IsFiniteFloat(length)) return false;
                total = total + length;
            }
            if (!KatamariMath.IsFiniteFloat(total)) return false;
            totalLength = total;
            return true;
        }

        // Samples the polyline at distanceM. The result's y is the path's own y: the
        // caller subtracts halfHeightM (the original's pos = point - (0, b, 0)).
        private static bool TrySamplePath(float[] points, int pointCount, bool closed,
            float distanceM, out Vector3 sample, out int segmentIndex, out float fraction)
        {
            sample = Vector3.zero;
            segmentIndex = 0;
            fraction = 0f;
            if (!IsPathOk(points, pointCount)) return false;
            if (!KatamariMath.IsFiniteFloat(distanceM)) return false;

            float total;
            if (!TryTotalPathLength(points, pointCount, closed, out total)) return false;
            if (!(total > 0f)) return false;

            float travelled = distanceM;
            if (closed)
            {
                travelled = travelled % total;
                if (travelled < 0f) travelled = travelled + total;
            }
            else
            {
                if (travelled < 0f) travelled = 0f;
                if (travelled > total) travelled = total;
            }

            int segmentCount;
            if (!TrySegmentCount(pointCount, closed, out segmentCount)) return false;

            float startDistance = 0f;
            for (int segment = 0; segment < segmentCount; segment++)
            {
                float length = PathSegmentLength(points, pointCount, closed, segment);
                if (!KatamariMath.IsFiniteFloat(length) || length <= 0f) return false;
                if (travelled <= startDistance + length || segment == segmentCount - 1)
                {
                    int from = segment * 3;
                    int to = NextPointIndex(segment, pointCount, closed) * 3;
                    float along = (travelled - startDistance) / length;
                    if (along < 0f) along = 0f;
                    if (along > 1f) along = 1f;
                    float x = points[from] + (points[to] - points[from]) * along;
                    float y = points[from + 1] + (points[to + 1] - points[from + 1]) * along;
                    float z = points[from + 2] + (points[to + 2] - points[from + 2]) * along;
                    sample = new Vector3(x, y, z);
                    if (!KatamariMath.IsFiniteVector(sample)) return false;
                    segmentIndex = segment;
                    fraction = along;
                    return true;
                }
                startDistance = startDistance + length;
            }
            return false;
        }

        // --- Shared validation --------------------------------------------------

        private static bool IsStateOk(float[] state, int requiredFloats)
        {
            if (requiredFloats <= 0) return true;
            if (state == null) return false;
            return state.Length >= requiredFloats;
        }

        // dt is game frames; the host clamps it to [0, 2] (section 12.0). Zero is legal and
        // means "recompute the pose without advancing", which is what a frozen or suspended
        // prop does.
        private static bool IsValidStep(float dtFrames)
        {
            return KatamariMath.IsFiniteFloat(dtFrames) && dtFrames >= 0f;
        }

        private static bool IsNonNegativeFinite(float value)
        {
            return KatamariMath.IsFiniteFloat(value) && value >= 0f;
        }

        private static bool IsPositiveFinite(float value)
        {
            return KatamariMath.IsFiniteFloat(value) && value > 0f;
        }

        // Normalizes an emitted yaw into [0, 2*pi). The value is folded to 0 when it lands
        // within YawSeamRadians of 2*pi: the two are the same rotation, and without the fold a
        // prop walking along -Z would sit at ~2*pi instead of the 0 that section 15.1 item 1
        // binds model yaw 0 to (the formula itself puts -Z on the interval's 2*pi end).
        private static float NormalizeYaw(float radians)
        {
            float wrapped = radians % TwoPi;
            if (wrapped < 0f) wrapped = wrapped + TwoPi;
            if (wrapped > TwoPi - YawSeamRadians) wrapped = 0f;
            return wrapped;
        }
    }
}
