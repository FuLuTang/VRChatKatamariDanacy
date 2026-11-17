using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Pickup
{
    /// <summary>
    /// The pure half of <see cref="MotionHost"/>: class codes, per-class dispatch into
    /// <see cref="MotionMath"/>, pose composition, update scheduling, distance culling with
    /// hysteresis, and the "somebody else is moving this prop" test. No component, no
    /// transform, no allocation, no static field.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why it exists. <see cref="MotionMath"/> answers "what pose does this program give at
    /// this clock"; the host has to answer "which program, in which space, composed onto what,
    /// how often, and is it allowed to write this frame". Those are decisions, and decisions
    /// belong in a function that NUnit can call without a scene. The host only owns the arrays
    /// and the transform writes (Docs/Design/PickupAbsorb/MOTION_HOST.md).
    /// </para>
    /// <para>
    /// Quaternions here use only the managed operators (<c>*</c> between quaternions and
    /// with a vector) and plain component maths, never <c>Quaternion.Euler</c> or
    /// <c>Quaternion.Inverse</c>, because those are engine natives: the tests that pin the
    /// composition run on a plain .NET runtime, and the inverse of a unit quaternion is its
    /// conjugate anyway.
    /// </para>
    /// <para>
    /// Spaces follow MotionMath's class comment: SPAWN-LOCAL classes give an offset and euler in
    /// the spawn frame (world = spawn * T(offset) * R(euler)); WORLD-AXIS classes (walkers,
    /// clouds) give a world-axis offset and a world yaw; ABSOLUTE classes (the three path
    /// classes) give a world position and angles. This is the same composition
    /// <c>Tools/motion_sim</c> uses for the editor preview, which is the point: the editor and
    /// the game must place a prop identically.
    /// </para>
    /// </remarks>
    public static class MotionHostMath
    {
        // --- Class codes (the serialized motionClass column) --------------------

        /// <summary>No own motion: a static prop, or a rider that only follows its parent.</summary>
        public const int ClassNone = 0;
        public const int ClassSpin = 1;
        public const int ClassFlip = 2;
        public const int ClassBob = 3;
        public const int ClassSway = 4;
        public const int ClassWhackAMole = 5;
        public const int ClassHop = 6;
        public const int ClassSporadicWalk = 7;
        public const int ClassClouds = 8;
        public const int ClassWaitForPlayer = 9;
        public const int ClassPathCollision = 10;
        public const int ClassSlopingPath = 11;
        public const int ClassBackAndForth = 12;
        public const int ClassDraggedAlong = 13;
        /// <summary>One past the last code; arrays indexed by class are this long.</summary>
        public const int ClassCount = 14;

        public const int SpaceSpawnLocal = 0;
        public const int SpaceWorldAxis = 1;
        public const int SpaceAbsolute = 2;

        /// <summary>props[].inherit: the child ignores its parent's motion.</summary>
        public const int InheritNone = 0;
        /// <summary>props[].inherit = "position": the child rides the translation only.</summary>
        public const int InheritPosition = 1;
        /// <summary>props[].inherit = "full": the child rides the parent's whole delta pose.</summary>
        public const int InheritFull = 2;

        /// <summary>Distance culling radius (PROP_BEHAVIOURS 7.2 wake_radius_m default).</summary>
        public const float DefaultWakeRadiusM = 12f;
        /// <summary>Hysteresis added to the radius before a row freezes (7.3).</summary>
        public const float DefaultCullHysteresisM = 3f;
        /// <summary>
        /// A prop that is more than this far from the pose the host last wrote has been moved
        /// by somebody else (a fall, a reset, a carry); the host stops driving it.
        /// </summary>
        public const float DefaultDriftToleranceM = 0.02f;
        /// <summary>
        /// Below this update rate the pose is interpolated between steps; at or above it the
        /// step is written as it is (hop and whack are 30 Hz by the tier table, and a 7-frame
        /// hop does not survive being smeared).
        /// </summary>
        public const float SmoothBelowUpdatesPerSecond = 29f;

        /// <summary>
        /// The motion class code of a <c>motions[].class</c> name; <see cref="ClassNone"/> for an
        /// unknown name (the caller reports it).
        /// </summary>
        public static int ClassFromName(string name)
        {
            if (name == "spin") return ClassSpin;
            if (name == "flip") return ClassFlip;
            if (name == "bob") return ClassBob;
            if (name == "sway") return ClassSway;
            if (name == "whack_a_mole") return ClassWhackAMole;
            if (name == "hop") return ClassHop;
            if (name == "sporadic_walk") return ClassSporadicWalk;
            if (name == "clouds") return ClassClouds;
            if (name == "wait_for_player") return ClassWaitForPlayer;
            if (name == "path_collision") return ClassPathCollision;
            if (name == "sloping_path") return ClassSlopingPath;
            if (name == "back_and_forth") return ClassBackAndForth;
            if (name == "dragged_along") return ClassDraggedAlong;
            return ClassNone;
        }

        /// <summary>The floats of per-row state the class needs (MotionMath's *StateFloats).</summary>
        public static int StateFloats(int motionClass)
        {
            if (motionClass == ClassSpin) return MotionMath.SpinStateFloats;
            if (motionClass == ClassFlip) return MotionMath.FlipStateFloats;
            if (motionClass == ClassBob) return MotionMath.BobStateFloats;
            if (motionClass == ClassSway) return MotionMath.SwayStateFloats;
            if (motionClass == ClassWhackAMole) return MotionMath.WhackAMoleStateFloats;
            if (motionClass == ClassHop) return MotionMath.HopStateFloats;
            if (motionClass == ClassSporadicWalk || motionClass == ClassClouds)
                return MotionMath.WanderStateFloats;
            if (motionClass == ClassWaitForPlayer) return MotionMath.WaitForPlayerStateFloats;
            if (motionClass == ClassPathCollision) return MotionMath.PathCollisionStateFloats;
            if (motionClass == ClassSlopingPath) return MotionMath.SlopingPathStateFloats;
            if (motionClass == ClassBackAndForth) return MotionMath.BackAndForthNoYawStateFloats;
            return 0;
        }

        /// <summary>
        /// The update tier of a class (LEVEL_FORMAT_V2 6.7.3), used when the document gives no
        /// <c>updatesPerSecond</c>: hop and whack 30, sway and the walkers and the paths 15,
        /// bob and clouds 10, spin/flip/trigger check 5, dragged-along 0.
        /// </summary>
        public static float DefaultUpdatesPerSecond(int motionClass)
        {
            if (motionClass == ClassHop || motionClass == ClassWhackAMole) return 30f;
            if (motionClass == ClassSway || motionClass == ClassSporadicWalk ||
                motionClass == ClassPathCollision || motionClass == ClassSlopingPath ||
                motionClass == ClassBackAndForth) return 15f;
            if (motionClass == ClassBob || motionClass == ClassClouds) return 10f;
            if (motionClass == ClassSpin || motionClass == ClassFlip ||
                motionClass == ClassWaitForPlayer) return 5f;
            return 0f;
        }

        /// <summary>Which of the three output spaces the class answers in.</summary>
        public static int SpaceOf(int motionClass)
        {
            if (motionClass == ClassSporadicWalk || motionClass == ClassClouds)
                return SpaceWorldAxis;
            if (motionClass == ClassPathCollision || motionClass == ClassSlopingPath ||
                motionClass == ClassBackAndForth) return SpaceAbsolute;
            return SpaceSpawnLocal;
        }

        /// <summary>True for the path classes (they need a path row).</summary>
        public static bool IsPathClass(int motionClass)
        {
            return SpaceOf(motionClass) == SpaceAbsolute;
        }

        /// <summary>Clouds ignore yaw: they keep the spawn orientation.</summary>
        public static bool KeepsSpawnRotation(int motionClass)
        {
            return motionClass == ClassClouds;
        }

        /// <summary>
        /// True when the class needs the ball: only WaitForPlayer reads the observer distance.
        /// </summary>
        public static bool NeedsObserver(int motionClass)
        {
            return motionClass == ClassWaitForPlayer;
        }

        // --- Reset and step -----------------------------------------------------

        /// <summary>
        /// Initialises one row's state from its phase seed (D-12). Classes without state or
        /// without a seed (the paths, WaitForPlayer) get their own reset.
        /// </summary>
        public static void ResetState(int motionClass, float[] state, int phaseSeed)
        {
            if (motionClass == ClassSpin) MotionMath.ResetSpin(state, phaseSeed);
            else if (motionClass == ClassFlip) MotionMath.ResetFlip(state, phaseSeed);
            else if (motionClass == ClassBob) MotionMath.ResetBob(state, phaseSeed);
            else if (motionClass == ClassSway) MotionMath.ResetSway(state, phaseSeed);
            else if (motionClass == ClassWhackAMole) MotionMath.ResetWhackAMole(state, phaseSeed);
            else if (motionClass == ClassHop) MotionMath.ResetHop(state, phaseSeed);
            else if (motionClass == ClassSporadicWalk) MotionMath.ResetSporadicWalk(state, phaseSeed);
            else if (motionClass == ClassClouds) MotionMath.ResetClouds(state, phaseSeed);
            else if (motionClass == ClassWaitForPlayer) MotionMath.ResetWaitForPlayer(state);
            else if (motionClass == ClassPathCollision) MotionMath.ResetPathCollision(state);
            else if (motionClass == ClassSlopingPath) MotionMath.ResetSlopingPath(state);
            else if (motionClass == ClassBackAndForth) MotionMath.ResetBackAndForthNoYaw(state);
        }

        /// <summary>
        /// Puts a path class's position on the path in our y-up frame. The program's
        /// <c>point - (0, b, 0)</c> is the original's own y convention; here the path points lie on
        /// the ground (Cancer p23: within 3 mm of the contact mesh) and the props' spawn centres sit
        /// one half height above them (nine of ten path props, 0.5-1.1 cm), so the centre belongs at
        /// <c>point + (0, b, 0)</c>: the result of the program is lifted by <c>2 b</c>. Left alone, a
        /// walking crab's body would be entirely below the ground.
        /// </summary>
        private static bool PathStep(bool stepped, float halfHeightM, ref Vector3 positionM)
        {
            if (stepped) positionM.y += 2f * halfHeightM;
            return stepped;
        }

        /// <summary>
        /// One step of one row's program. <paramref name="offsetOrPositionM"/> is an offset in
        /// the class's space, or the absolute world position for a path class (see
        /// <see cref="SpaceOf"/>); <paramref name="eulerRad"/> is the matching euler.
        /// </summary>
        /// <remarks>
        /// Everything the class needs and the document may not carry arrives as an argument
        /// (half height, speed, hop speed, path, spawn position, observer distance), so this is
        /// a switch and nothing else: no constant of a motion lives here. A refused step (bad
        /// state, non-finite input) returns false and leaves the outputs at zero; the host
        /// keeps the previous pose.
        /// </remarks>
        public static bool TryStep(int motionClass, float[] state, float dtFrames, int phaseSeed,
            float halfHeightM, bool swayAppliesTranslation, bool loop, float speedMps,
            float hopSpeedCmPerFrame, float[] pathPointsM, int pointCount, bool pathClosed,
            Vector3 spawnPositionM, out Vector3 offsetOrPositionM, out Vector3 eulerRad)
        {
            offsetOrPositionM = Vector3.zero;
            eulerRad = Vector3.zero;
            if (motionClass == ClassSpin)
                return MotionMath.TryStepSpin(state, dtFrames, out offsetOrPositionM, out eulerRad);
            if (motionClass == ClassFlip)
                return MotionMath.TryStepFlip(state, dtFrames, out offsetOrPositionM, out eulerRad);
            if (motionClass == ClassBob)
                return MotionMath.TryStepBob(state, dtFrames, halfHeightM, out offsetOrPositionM,
                    out eulerRad);
            if (motionClass == ClassSway)
                return MotionMath.TryStepSway(state, dtFrames, halfHeightM, swayAppliesTranslation,
                    out offsetOrPositionM, out eulerRad);
            if (motionClass == ClassWhackAMole)
                return MotionMath.TryStepWhackAMole(state, dtFrames, phaseSeed, halfHeightM, loop,
                    out offsetOrPositionM, out eulerRad);
            if (motionClass == ClassHop)
                return MotionMath.TryStepHop(state, dtFrames, phaseSeed, hopSpeedCmPerFrame,
                    out offsetOrPositionM, out eulerRad);
            // The host's zone and obstacle queries are not modelled (MOTION_HOST.md 6): every
            // step is allowed and every turn candidate is open, which is exactly what the
            // editor preview assumes too.
            if (motionClass == ClassSporadicWalk)
                return MotionMath.TryStepSporadicWalk(state, dtFrames, phaseSeed, speedMps, 0x7F,
                    false, out offsetOrPositionM, out eulerRad);
            if (motionClass == ClassClouds)
                return MotionMath.TryStepClouds(state, dtFrames, phaseSeed, speedMps, 0x7F,
                    out offsetOrPositionM, out eulerRad);
            if (motionClass == ClassPathCollision)
                return PathStep(MotionMath.TryStepPathCollision(state, dtFrames, spawnPositionM,
                    pathPointsM, pointCount, speedMps, halfHeightM, pathClosed,
                    out offsetOrPositionM, out eulerRad), halfHeightM, ref offsetOrPositionM);
            if (motionClass == ClassSlopingPath)
                return PathStep(MotionMath.TryStepSlopingPath(state, dtFrames, spawnPositionM,
                    pathPointsM, pointCount, speedMps, halfHeightM, pathClosed,
                    out offsetOrPositionM, out eulerRad), halfHeightM, ref offsetOrPositionM);
            if (motionClass == ClassBackAndForth)
                return PathStep(MotionMath.TryStepBackAndForthNoYaw(state, dtFrames, spawnPositionM,
                    pathPointsM, pointCount, speedMps, halfHeightM, out offsetOrPositionM,
                    out eulerRad), halfHeightM, ref offsetOrPositionM);
            // WaitForPlayer, DraggedAlong and ClassNone have no pose of their own.
            return false;
        }

        /// <summary>
        /// WaitForPlayer's gate with the row's own trigger radius (LEVEL_FORMAT_V2 6.7.3
        /// <c>triggerRadiusM</c>), which is NOT the host's wake radius: 2.0 m for the size
        /// groups Cancer has, against 12 m of culling. motionId 0x00 triggers on distance and
        /// size group; 0x04 needs the original's <c>inZone &amp;&amp; parentRolled</c>, unknown
        /// (M2), so it never triggers until that is measured.
        /// </summary>
        public static bool IsTriggered(int motionIdCode, float distanceToObserverM,
            float triggerRadiusM, int sizeGroup)
        {
            if (!KatamariMath.IsFiniteFloat(distanceToObserverM) || distanceToObserverM < 0f)
                return false;
            if (!KatamariMath.IsFiniteFloat(triggerRadiusM) || !(triggerRadiusM > 0f)) return false;
            if (motionIdCode != 0x00) return false;
            if (sizeGroup > 2) return false;
            return distanceToObserverM < triggerRadiusM;
        }

        // --- Time ---------------------------------------------------------------

        /// <summary>
        /// The step in game frames for elapsed seconds: <c>clamp(seconds * 30, 0, 2)</c>
        /// (MotionMath section 12.0). A long gap (a culled row coming back, a hitch) therefore
        /// advances at most two frames and never jumps.
        /// </summary>
        public static float StepDtFrames(float elapsedSeconds)
        {
            if (!KatamariMath.IsFiniteFloat(elapsedSeconds) || elapsedSeconds <= 0f) return 0f;
            float frames = elapsedSeconds * MotionMath.GameFramesPerSecond;
            if (frames > 2f) frames = 2f;
            return frames;
        }

        /// <summary>
        /// Seconds between steps for an update rate; 0 for "every frame" (rate 0 or invalid).
        /// </summary>
        public static float IntervalSeconds(float updatesPerSecond)
        {
            if (!KatamariMath.IsFiniteFloat(updatesPerSecond) || !(updatesPerSecond > 0f)) return 0f;
            return 1f / updatesPerSecond;
        }

        /// <summary>True when a row at this rate is interpolated between its steps.</summary>
        public static bool ShouldSmooth(float updatesPerSecond)
        {
            return updatesPerSecond > 0f && updatesPerSecond < SmoothBelowUpdatesPerSecond;
        }

        /// <summary>
        /// Progress 0..1 from the previous step's pose toward the latest one. An interval of 0
        /// (every frame) is always at the latest pose.
        /// </summary>
        public static float SmoothFraction(float nowSeconds, float lastStepSeconds,
            float intervalSeconds)
        {
            if (!(intervalSeconds > 0f)) return 1f;
            float fraction = (nowSeconds - lastStepSeconds) / intervalSeconds;
            if (!KatamariMath.IsFiniteFloat(fraction)) return 1f;
            if (fraction < 0f) return 0f;
            if (fraction > 1f) return 1f;
            return fraction;
        }

        /// <summary>
        /// When a row is next due, anchored on the schedule rather than on "now" so a 15 Hz row
        /// stays at 15 Hz instead of at 15 Hz minus its frame jitter. If the schedule has
        /// fallen more than one interval behind (the row was culled, the frame hitched) it
        /// restarts from now.
        /// </summary>
        public static float NextDue(float previousDueSeconds, float nowSeconds, float intervalSeconds)
        {
            if (!(intervalSeconds > 0f)) return nowSeconds;
            float next = previousDueSeconds + intervalSeconds;
            if (next < nowSeconds) next = nowSeconds + intervalSeconds;
            return next;
        }

        // --- Culling and ownership ---------------------------------------------

        /// <summary>
        /// The hysteresis state machine of PROP_BEHAVIOURS 7.3: a row wakes at
        /// <c>distance &lt;= radius</c> and freezes at <c>distance &gt; radius + hysteresis</c>;
        /// in between it keeps whatever it was. Distances are squared so the caller never takes
        /// a square root. A non-finite distance keeps the current state.
        /// </summary>
        public static bool UpdateCulled(bool wasCulled, float distanceSquaredM, float wakeRadiusM,
            float hysteresisM)
        {
            if (!KatamariMath.IsFiniteFloat(distanceSquaredM) || distanceSquaredM < 0f)
                return wasCulled;
            float wake = wakeRadiusM;
            if (!KatamariMath.IsFiniteFloat(wake) || wake < 0f) wake = DefaultWakeRadiusM;
            float hysteresis = hysteresisM;
            if (!KatamariMath.IsFiniteFloat(hysteresis) || hysteresis < 0f) hysteresis = 0f;
            float freeze = wake + hysteresis;
            if (distanceSquaredM <= wake * wake) return false;
            if (distanceSquaredM > freeze * freeze) return true;
            return wasCulled;
        }

        /// <summary>
        /// True when the prop is not where the host last put it: somebody else (the absorb
        /// attach, a stack fall, a round reset) has taken it, and the host must stop writing.
        /// </summary>
        public static bool HasDrifted(Vector3 lastWrittenM, Vector3 currentM, float toleranceM)
        {
            if (!KatamariMath.IsFiniteVector(lastWrittenM) || !KatamariMath.IsFiniteVector(currentM))
                return true;
            float tolerance = toleranceM;
            if (!KatamariMath.IsFiniteFloat(tolerance) || tolerance < 0f)
                tolerance = DefaultDriftToleranceM;
            float dx = currentM.x - lastWrittenM.x;
            float dy = currentM.y - lastWrittenM.y;
            float dz = currentM.z - lastWrittenM.z;
            return dx * dx + dy * dy + dz * dz > tolerance * tolerance;
        }

        // --- Pose composition ---------------------------------------------------

        /// <summary>
        /// A unit quaternion's inverse (its conjugate), without the engine native.
        /// </summary>
        public static Quaternion Conjugate(Quaternion rotation)
        {
            return new Quaternion(-rotation.x, -rotation.y, -rotation.z, rotation.w);
        }

        /// <summary>
        /// Euler radians to a quaternion in Unity's <c>Quaternion.Euler(x, y, z)</c> order
        /// (Z first, then X, then Y). The one-axis cases, which are all but one class, are a
        /// single sine and cosine.
        /// </summary>
        public static Quaternion EulerToQuaternion(Vector3 eulerRad)
        {
            float x = eulerRad.x;
            float y = eulerRad.y;
            float z = eulerRad.z;
            if (x == 0f && z == 0f)
            {
                float half = 0.5f * y;
                return new Quaternion(0f, Mathf.Sin(half), 0f, Mathf.Cos(half));
            }
            if (y == 0f && z == 0f)
            {
                float half = 0.5f * x;
                return new Quaternion(Mathf.Sin(half), 0f, 0f, Mathf.Cos(half));
            }
            if (x == 0f && y == 0f)
            {
                float half = 0.5f * z;
                return new Quaternion(0f, 0f, Mathf.Sin(half), Mathf.Cos(half));
            }
            float sx = Mathf.Sin(0.5f * x);
            float cx = Mathf.Cos(0.5f * x);
            float sy = Mathf.Sin(0.5f * y);
            float cy = Mathf.Cos(0.5f * y);
            float sz = Mathf.Sin(0.5f * z);
            float cz = Mathf.Cos(0.5f * z);
            // q = (qy * qx) * qz, with qy = (0, sy, 0, cy), qx = (sx, 0, 0, cx),
            // qz = (0, 0, sz, cz); a is the product qy * qx.
            float ax = cy * sx;
            float ay = sy * cx;
            float az = -sy * sx;
            float aw = cy * cx;
            return new Quaternion(
                ax * cz + ay * sz,
                -ax * sz + ay * cz,
                aw * sz + az * cz,
                aw * cz - az * sz);
        }

        /// <summary>
        /// The pose a class's step puts the prop at on its own, with no parent: spawn-local
        /// classes compose onto the spawn pose, world-axis classes add their offset in world
        /// axes, absolute classes are the pose.
        /// </summary>
        public static void StandalonePose(int motionClass, Vector3 spawnPositionM,
            Quaternion spawnRotation, Vector3 offsetOrPositionM, Vector3 eulerRad,
            out Vector3 positionM, out Quaternion rotation)
        {
            int space = SpaceOf(motionClass);
            if (space == SpaceAbsolute)
            {
                positionM = offsetOrPositionM;
                rotation = EulerToQuaternion(eulerRad);
                return;
            }
            if (space == SpaceWorldAxis)
            {
                positionM = spawnPositionM + offsetOrPositionM;
                rotation = KeepsSpawnRotation(motionClass) ? spawnRotation
                    : EulerToQuaternion(eulerRad);
                return;
            }
            positionM = spawnPositionM + spawnRotation * offsetOrPositionM;
            rotation = spawnRotation * EulerToQuaternion(eulerRad);
        }

        /// <summary>
        /// Carries a child's standalone pose with its parent's motion:
        /// <c>world(child) = world(parent) * inverse(spawn(parent)) * own(child)</c> for
        /// <see cref="InheritFull"/>; for <see cref="InheritPosition"/> only the parent's
        /// translation is added. <see cref="InheritNone"/> returns the pose unchanged.
        /// </summary>
        /// <param name="parentSpawnRotationInverse">Conjugate of the parent's spawn rotation,
        /// computed once by the host.</param>
        public static void CarryByParent(int inherit, Vector3 parentWorldPositionM,
            Quaternion parentWorldRotation, Vector3 parentSpawnPositionM,
            Quaternion parentSpawnRotationInverse, Vector3 ownPositionM, Quaternion ownRotation,
            out Vector3 positionM, out Quaternion rotation)
        {
            positionM = ownPositionM;
            rotation = ownRotation;
            if (inherit == InheritPosition)
            {
                positionM = ownPositionM + (parentWorldPositionM - parentSpawnPositionM);
                return;
            }
            if (inherit != InheritFull) return;
            Quaternion carry = parentWorldRotation * parentSpawnRotationInverse;
            positionM = carry * ownPositionM + (parentWorldPositionM - carry * parentSpawnPositionM);
            rotation = carry * ownRotation;
        }

        /// <summary>
        /// The speed in metres per second a row runs at, resolved once at import time:
        /// a path record's own speed first (6.7.4 "优先于逐类型档"), then the explicit
        /// override, then the type table (<c>defs[].speedIndex</c>, cm per 30 Hz frame).
        /// Returns 0 when nothing resolves; the importer reports that.
        /// </summary>
        public static float ResolveSpeedMps(int speedSourceCode, float pathSpeedCmPerSec,
            bool hasPathSpeed, float explicitCmPerSec, int speedIndex)
        {
            if (speedSourceCode == SpeedSourcePath && hasPathSpeed)
                return pathSpeedCmPerSec * MotionMath.MetresPerCentimetre;
            if (speedSourceCode == SpeedSourceExplicit)
                return explicitCmPerSec * MotionMath.MetresPerCentimetre;
            if (speedSourceCode == SpeedSourceNone) return 0f;
            if (speedIndex < 0) return 0f;
            return MotionMath.SpeedCmPerFrame(speedIndex) * MotionMath.MetresPerCentimetre *
                MotionMath.GameFramesPerSecond;
        }

        public const int SpeedSourceNone = 0;
        public const int SpeedSourceType = 1;
        public const int SpeedSourcePath = 2;
        public const int SpeedSourceExplicit = 3;

        /// <summary>The speed source code of <c>motions[].speed.source</c>.</summary>
        public static int SpeedSourceFromName(string name)
        {
            if (name == "type") return SpeedSourceType;
            if (name == "path") return SpeedSourcePath;
            if (name == "explicit") return SpeedSourceExplicit;
            return SpeedSourceNone;
        }

        /// <summary>props[].inherit as the serialized code ("none"/null is none).</summary>
        public static int InheritFromName(string name)
        {
            if (name == "full") return InheritFull;
            if (name == "position") return InheritPosition;
            return InheritNone;
        }
    }
}
