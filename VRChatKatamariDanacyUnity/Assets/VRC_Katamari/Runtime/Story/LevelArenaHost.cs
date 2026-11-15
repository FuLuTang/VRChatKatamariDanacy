using UdonSharp;
using UnityEngine;
using VRCKatamari.BallLayers;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Story
{
    /// <summary>
    /// One level's arena geometry, registered with the ball motor at level start:
    /// the ground plane, boundary wall planes, the static boxes whose tops the
    /// ball can stand on, and the static ramps whose walking planes the ball can
    /// roll up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists. The only registration a level had was the lab script
    /// (<c>Labs/StoryLevel/StoryLevelLabBootstrap</c>), which mixed scene wiring with
    /// a self-test drive, a camera trail and a log line. A shipped scene must not carry
    /// a component named "lab", and the level-authoring tool emits one
    /// <c>LevelContent</c> subtree whose structures need exactly this wiring
    /// (Docs/Design/Tools/LEVEL_AUTHORING.md §4.3). This component owns the wiring
    /// only: no rules, no per-frame work after setup, no prop awareness.
    /// </para>
    /// <para>
    /// Authoring contract. Structures are real scene objects with real
    /// <see cref="BoxCollider"/>s (the importer generates them from
    /// <c>placement.json</c>); this component references them and bakes
    /// <c>Collider.bounds</c> into the motor's min/max ONCE in <see cref="Setup"/>. After
    /// that it never reads a Collider again, so the runtime does not depend on the
    /// physics scene and no per-step cost is added. It does not accept hand-typed
    /// min/max pairs: a second copy of the geometry in the inspector is exactly the
    /// drift this replaces.
    /// </para>
    /// <para>
    /// Walls are planes, not boxes (design §7.7 rule 3): a wall box would eat the box
    /// budget and its top face would become a standable narrow ledge. Each entry in
    /// <see cref="boundaryWalls"/> is baked as <c>normal = transform.forward</c>,
    /// <c>offset = dot(normal, transform.position)</c>, so the wall's local +Z (blue
    /// arrow) must point INTO the play area and its origin must lie on the face the
    /// ball should stop at.
    /// </para>
    /// <para>
    /// Ramps (P3) are a separate array on purpose. A ramp is a support-only oriented
    /// plane baked from its collider's centre/size/rotation, and it must NEVER also
    /// appear in <see cref="staticBoxes"/>: the sweep is a radius-expanded AABB, so a
    /// box registration of the tilted board would stop the ball at the ramp foot (the
    /// invisible wall of design §5.5). Setup watches for the same collider in both
    /// arrays and drops the box registration, keeping the ramp. The bake goes through
    /// <see cref="SupportSurfaceMath.TryBakeRamp"/>, so the walking face is the box's
    /// +Y face and the slope guard is the motor's own serialized limit.
    /// </para>
    /// <para>
    /// Division of labour with the pickup host (design §7.4, a hard rule): a prop's
    /// support/blocking identity belongs to <c>PickupFieldHost</c>, which already owns
    /// the prop table and the absorb gate. This component never references a prop and
    /// never calls <c>SetBlockingBoxes</c>; it only knows static scene geometry.
    /// </para>
    /// <para>
    /// Setup is deferred to the first <see cref="Update"/> with a positive semantic
    /// diameter: Unity does not promise that this component's Start runs after the
    /// motor's, and the motor allocates its stores in Start. A positive diameter is the
    /// public proof that the motor is ready.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class LevelArenaHost : UdonSharpBehaviour
    {
        [Header("Links")]
        [SerializeField] private KatamariBallMotor motor;

        [Header("Ground (the analytic plane; always the support fallback)")]
        [SerializeField] private Vector3 groundNormal = new Vector3(0f, 1f, 0f);
        [SerializeField] private float groundOffset = 0f;

        [Header("Boundary walls (planes baked from each Transform; never supports)")]
        // Local +Z must point into the play area; the origin must be on the collision face.
        [SerializeField] private Transform[] boundaryWalls;

        [Header("Static boxes (sweep obstacle AND a support top; bounds baked at Setup)")]
        [SerializeField] private BoxCollider[] staticBoxes;

        [Header("Static ramps (support only, NEVER a sweep box; baked at Setup)")]
        // Each entry's local +Y is the walking plane and its local +Z is the run; the
        // board's thickness extends downward from that face. An entry also listed in
        // staticBoxes is reported and its box registration is skipped.
        [SerializeField] private BoxCollider[] rampBoxes;

        [Header("Optional")]
        // null/empty = the motor refuses every absorb candidate (its contract).
        [SerializeField] private string[] allowedKinds;
        [SerializeField] private bool autoBeginPlaying = true;
        [SerializeField] private bool logSetupLine = true;

        private bool ready;
        private bool beganPlaying;
        private bool groundRegistered;
        private int registeredPlaneCount;
        private int registeredBoxCount;
        private int registeredRampCount;
        private int refusedCount;
        private string lastRampRefusal = "";
        private string setupReport = "";

        public void Update()
        {
            if (ready) return;
            if (motor == null) return;
            if (motor.GetSemanticDiameterWorldM() <= 0f) return;
            Setup();
        }

        /// <summary>
        /// Registers the arena once. Idempotent: a second call is a no-op.
        /// </summary>
        /// <remarks>
        /// Registration order is the design's (§7.3) and is not interchangeable: the
        /// ground first (so the fallback exists), then boundary planes, then ramps
        /// (support-only, so they must be known before any box that might overlap them),
        /// then static boxes (sweep + support tops), then the kind whitelist, then the
        /// round open. A refused registration is counted, never retried and never logged
        /// per frame; the single <c>[arena]</c> line carries the counts and the last ramp
        /// refusal reason.
        /// </remarks>
        public void Setup()
        {
            if (ready) return;
            if (motor == null) return;
            if (motor.GetSemanticDiameterWorldM() <= 0f) return;

            registeredPlaneCount = 0;
            registeredBoxCount = 0;
            registeredRampCount = 0;
            refusedCount = 0;
            lastRampRefusal = "";

            groundRegistered = motor.SetGroundPlane(groundNormal, groundOffset);
            if (!groundRegistered) refusedCount = refusedCount + 1;

            if (boundaryWalls != null)
            {
                for (int i = 0; i < boundaryWalls.Length; i++)
                {
                    Transform wall = boundaryWalls[i];
                    if (wall == null) continue;
                    // Local +Z in world space. Written as a rotation product rather than
                    // Transform.forward so the call surface is the one already proven in
                    // this project (FollowRig/Riding rotate vectors this way).
                    Vector3 normal = wall.rotation * new Vector3(0f, 0f, 1f);
                    Vector3 position = wall.position;
                    float offset = normal.x * position.x + normal.y * position.y
                        + normal.z * position.z;
                    if (motor.AddPlane(normal, offset)) registeredPlaneCount = registeredPlaneCount + 1;
                    else refusedCount = refusedCount + 1;
                }
            }

            if (rampBoxes != null)
            {
                for (int i = 0; i < rampBoxes.Length; i++)
                {
                    BoxCollider box = rampBoxes[i];
                    if (box == null) continue;
                    if (RegisterRamp(box)) registeredRampCount = registeredRampCount + 1;
                    else refusedCount = refusedCount + 1;
                }
            }

            if (staticBoxes != null)
            {
                for (int i = 0; i < staticBoxes.Length; i++)
                {
                    BoxCollider box = staticBoxes[i];
                    if (box == null) continue;
                    // A collider in both arrays is the authoring trap of design §5.5. The
                    // ramp wins (it is support-only, so it cannot create the foot wall),
                    // and the box registration is skipped so the ball is not stopped under
                    // the board. This is O(n*m) over two tiny serialized arrays, once.
                    if (IsRampCollider(box))
                    {
                        Debug.LogError("[arena] '" + box.gameObject.name + "' is in both staticBoxes and "
                            + "rampBoxes; the box registration is skipped so the ramp foot has "
                            + "no invisible wall (keep it out of staticBoxes)");
                        refusedCount = refusedCount + 1;
                        continue;
                    }
                    // The only Collider read in this component's lifetime. A rotated box
                    // bakes to its enclosing world AABB (a documented P1 limitation); the
                    // motor only ever sees min/max.
                    Bounds bounds = box.bounds;
                    if (motor.AddBox(bounds.min, bounds.max)) registeredBoxCount = registeredBoxCount + 1;
                    else refusedCount = refusedCount + 1;
                }
            }

            int kindCount = allowedKinds == null ? 0 : allowedKinds.Length;
            motor.SetAllowedKinds(allowedKinds, kindCount);
            // Begin once per component lifetime: Reapply re-registers geometry on a cell
            // switch and must not try to re-open an already running round.
            if (autoBeginPlaying && !beganPlaying)
            {
                if (motor.TryBeginPlaying(Time.time)) beganPlaying = true;
            }

            ready = true;
            setupReport = "[arena] planes=" + registeredPlaneCount + "/"
                + (boundaryWalls == null ? 0 : boundaryWalls.Length)
                + " boxes=" + registeredBoxCount + "/"
                + (staticBoxes == null ? 0 : staticBoxes.Length)
                + " ramps=" + registeredRampCount + "/"
                + (rampBoxes == null ? 0 : rampBoxes.Length)
                + " supports=" + GetRegisteredSupportCount()
                + " refused=" + refusedCount
                + " kinds=" + kindCount;
            if (lastRampRefusal.Length > 0) setupReport = setupReport + " rampRefusal=" + lastRampRefusal;

            if (!logSetupLine) return;
            if (refusedCount > 0) Debug.LogError(setupReport);
            else Debug.Log(setupReport);
        }

        /// <summary>
        /// Re-registers the arena geometry: clears the motor's obstacle tables and runs
        /// <see cref="Setup"/> again (the physics test field's cell-switch seam,
        /// Docs/Design/BallMotor/PHYSICS_TEST_FIELD.md §3.3).
        /// </summary>
        /// <remarks>
        /// Clear-then-register is the only correct order: AddPlane/AddBox are additive, so a
        /// second pass without the clear would overflow the fixed capacities. ClearObstacles
        /// also drops the dynamic blocking set by its own documented semantics; the pickup
        /// host re-registers that on its next scan, and the field resets its props right
        /// after. This component still never calls SetBlockingBoxes. The round is not
        /// re-opened, and the kind whitelist is re-sent (it is idempotent).
        /// </remarks>
        public void Reapply()
        {
            if (motor == null) return;
            if (motor.GetSemanticDiameterWorldM() <= 0f) return;
            motor.ClearObstacles();
            ready = false;
            Setup();
        }

        /// <summary>True once the arena has been registered with the motor.</summary>
        public bool IsReady()
        {
            return ready;
        }

        /// <summary>Boundary planes the motor accepted this setup.</summary>
        public int GetRegisteredPlaneCount()
        {
            return registeredPlaneCount;
        }

        /// <summary>Static boxes the motor accepted this setup.</summary>
        public int GetRegisteredBoxCount()
        {
            return registeredBoxCount;
        }

        /// <summary>Static ramps the motor accepted this setup.</summary>
        public int GetRegisteredRampCount()
        {
            return registeredRampCount;
        }

        /// <summary>
        /// Support surfaces the arena registered: the ground plus one top per static box
        /// plus one walking plane per static ramp.
        /// </summary>
        /// <remarks>
        /// A count of REGISTRATIONS, not of active candidates: which of them is inside the
        /// step band is decided every step by the motor and reported by
        /// <c>GetSupportSurfaceCount()</c> there.
        /// </remarks>
        public int GetRegisteredSupportCount()
        {
            int supports = registeredBoxCount + registeredRampCount;
            if (groundRegistered) supports = supports + 1;
            return supports;
        }

        /// <summary>The one-line setup report, or "" before setup.</summary>
        public string GetSetupReport()
        {
            if (setupReport == null) return "";
            return setupReport;
        }

        /// <summary>
        /// Bakes one ramp collider through <see cref="SupportSurfaceMath.TryBakeRamp"/>
        /// and registers it with the motor.
        /// </summary>
        /// <remarks>
        /// This is the ONLY place the component reads a collider's centre, size and
        /// rotation, and it does so once. The world size uses the transform's lossy scale
        /// because the importer scales a unit cube: the collider's local size is one and
        /// the authored size is the scale. The world centre is
        /// <c>position + rotation * scale * collider.center</c> (the importer sets the
        /// collider centre to zero, and this form avoids a TransformPoint extern).
        /// The slope guard passed in is the motor's serialized value, so the host and the
        /// motor cannot disagree about what is too steep.
        /// </remarks>
        private bool RegisterRamp(BoxCollider box)
        {
            Transform owner = box.transform;
            Vector3 normal = owner.rotation * new Vector3(0f, 1f, 0f);
            Vector3 right = owner.rotation * new Vector3(1f, 0f, 0f);
            Vector3 scale = owner.lossyScale;
            Vector3 localCenter = box.center;
            Vector3 localSize = box.size;
            Vector3 boxCenter = owner.position + owner.rotation * new Vector3(
                localCenter.x * scale.x, localCenter.y * scale.y, localCenter.z * scale.z);
            Vector3 worldSize = new Vector3(Mathf.Abs(scale.x) * localSize.x,
                Mathf.Abs(scale.y) * localSize.y, Mathf.Abs(scale.z) * localSize.z);

            Vector3 walkingCenter;
            Vector3 rampNormal;
            Vector3 rampRight;
            float halfWidth;
            float halfRun;
            float slopeDegrees;
            int refusalCode;
            if (!SupportSurfaceMath.TryBakeRamp(boxCenter, normal, right, worldSize,
                motor.GetMaxSupportSlopeDegrees(), out walkingCenter, out rampNormal,
                out rampRight, out halfWidth, out halfRun, out slopeDegrees, out refusalCode))
            {
                lastRampRefusal = box.gameObject.name + ": "
                    + SupportSurfaceMath.RampRefusalReason(refusalCode)
                    + " (slope " + slopeDegrees + " deg)";
                return false;
            }
            if (!motor.AddRamp(walkingCenter, rampNormal, rampRight, halfWidth, halfRun))
            {
                lastRampRefusal = box.gameObject.name + ": " + motor.GetLastRampRefusalReason();
                return false;
            }
            return true;
        }

        /// <summary>True when the same collider is listed in <see cref="rampBoxes"/>.</summary>
        private bool IsRampCollider(BoxCollider candidate)
        {
            if (rampBoxes == null) return false;
            for (int i = 0; i < rampBoxes.Length; i++)
            {
                if (rampBoxes[i] == candidate) return true;
            }
            return false;
        }
    }
}
