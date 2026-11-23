using UdonSharp;
using UnityEngine;
using VRCKatamari.BallContact;
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
    /// Why this exists. Scene wiring, a self-test drive, a camera trail and a log line
    /// must not live in one lab script. A shipped scene must not carry
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
    /// Ramps (P3) are a separate array on purpose. A ramp is a solid slab whose collider's
    /// local +Y is the walking face, and it must NEVER also appear in
    /// <see cref="staticBoxes"/>: the same board registered twice would be two solids at
    /// one place (design §5.5). Setup watches for the same collider in both
    /// arrays and drops the box registration, keeping the ramp.
    /// </para>
    /// <para>
    /// Division of labour with the pickup host (design §7.4, a hard rule): a prop's
    /// support/blocking identity belongs to <c>PickupFieldHost</c>, which already owns
    /// the prop table and the absorb gate. This component never references a prop and
    /// never writes the motor's prop solids; it only knows static scene geometry.
    /// </para>
    /// <para>
    /// Static OBB bake (CONTACT_MODEL_V2 §1.4/§5.7/§7.4). Every static box, straight ramp
    /// and curved-ramp slab segment is baked the same way: its eight world corners are
    /// fitted into a true OBB (<see cref="ObbBakeMath.TryBakeFromCorners"/>) and
    /// registered through <c>AddObb</c>. Surface ids stay in the original id space (box
    /// <c>1+i</c>, ramp <c>SupportSurfaceMath.FirstRampSurfaceId + i</c>), so a diagnostic
    /// reading an id still knows what it points at. <c>AddBox</c>/<c>AddRamp</c> are not
    /// called: they mirror into the same OBB table, and a scene must never register one
    /// solid twice.
    /// </para>
    /// <para>
    /// The bake is reached through the collider's transform (position, rotation, lossy
    /// scale) rather than <c>Transform.TransformPoint</c>, which no runtime file in this
    /// project uses and whose Udon binding is unverified: the corner is
    /// <c>position + rotation * (localCorner * lossyScale)</c>, the same expression
    /// <see cref="RegisterRamp"/> already uses for the box centre. The corner SET is the
    /// design's; only the last multiply is spelled out. A parent that is rotated and
    /// non-uniformly scaled makes that reconstruction differ from the true matrix, and the
    /// difference is caught by comparing the OBB's world AABB with <c>Collider.bounds</c>
    /// (a clean rotated box matches it exactly), which is the shear test: a sheared box is
    /// refused with a reason code and the bounds AABB is registered instead (§1.4).
    /// </para>
    /// <para>
    /// Setup is deferred to the first <see cref="Update"/> with a positive semantic
    /// diameter: Unity does not promise that this component's Start runs after the
    /// motor's, and the motor allocates its stores in Start. A positive diameter is the
    /// public proof that the motor is ready.
    /// </para>
    /// <para>
    /// One registration, two questions. <see cref="Register"/> is the only registration entry;
    /// "register the whole thing" (<see cref="Reapply"/>) is one answer to its parameters, not a
    /// second code path. A host can be asked to clear the motor first or to append to what is
    /// already there, and to offset its surface ids so several hosts registered into one motor
    /// never share an id (the contact solver de-duplicates by surface id, so two solids with one
    /// id would be one solid). The physics test field's manual sandbox is the only caller that
    /// asks for anything but the defaults: one frame host (floor + outer walls), then every
    /// cell's boards appended.
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

        [Header("Static boxes (solid and a support top; baked at Setup)")]
        [SerializeField] private BoxCollider[] staticBoxes;
        // Parallel to staticBoxes: the item id of the PICKUP PROP this box is the collision of
        // ("" = a plain solid that stays for good). Written by the importer from the level's
        // solids that share an id with a prop, so a rolled-up prop can take its own solid with
        // it (RemoveSolidsOf) and a field reset can bring it back (RestoreRemovedSolids). A
        // GameObject name is NOT used: the importer renames objects, the id it writes here is
        // the data's. Null or shorter than staticBoxes = no removable solids (older scenes).
        [SerializeField] private string[] staticBoxItemIds;

        [Header("Static ramps (slab solids; baked at Setup)")]
        // Each entry's local +Y is the walking plane and its local +Z is the run; the
        // board's thickness extends downward from that face. An entry also listed in
        // staticBoxes is reported and its box registration is skipped.
        [SerializeField] private BoxCollider[] rampBoxes;

        [Header("Optional")]
        // null/empty = the motor refuses every absorb candidate (its contract).
        [SerializeField] private string[] allowedKinds;
        [SerializeField] private bool autoBeginPlaying = true;
        [SerializeField] private bool logSetupLine = true;

        // ---- Arena refusal codes (reported as "refusal=<code>:<text>", one per setup) ----
        // 0 is "accepted", so a caller that never sees a refusal reads 0.
        private const int RefusalNone = 0;
        // The eight corners exist but do not form an orthogonal box: a rotated,
        // non-uniformly scaled parent sheared it. The bounds AABB is registered instead (§1.4).
        private const int RefusalShear = 1;
        // The corner fit or the bounds fallback was unusable (non-finite, degenerate).
        private const int RefusalDegenerate = 2;
        // The motor's v2 static table (256) is full: refused, counted, never silently dropped.
        private const int RefusalObbCapacity = 3;
        // The motor refused the box as not finite/unit/orthogonal.
        private const int RefusalObbInvalid = 4;
        // The collider is listed in BOTH staticBoxes and rampBoxes: the box registration is
        // skipped so the ramp foot has no invisible wall (§5.5).
        private const int RefusalDoubleListed = 7;
        // The eight-corner OBB and the bounds AABB disagree for axis-aligned geometry
        // (§7.4): the AABB is used and this is logged once as an error.
        private const int RefusalDoubleBake = 8;

        // §7.4's agreement tolerance for axis-aligned geometry, plus a scale term so a box
        // 30 m from the origin (float ulp ~2e-6) is not called a mismatch.
        private const float DoubleBakeToleranceM = 1e-6f;

        private bool ready;
        private bool registrationSuspended;

        // ---- Per-box registration record (parallel to staticBoxes; rebuilt by every Setup) ----
        // What the motor was given for each static box, so a solid taken out with its prop can
        // be put back byte-for-byte. A box that was not registered (null, refused, listed as a
        // ramp) has boxRegistered false and is never touched.
        private bool[] boxRegistered;
        private bool[] boxRemoved;
        private Vector3[] boxBakeCenter;
        private Vector3[] boxBakeAxisX;
        private Vector3[] boxBakeAxisY;
        private Vector3[] boxBakeAxisZ;
        private Vector3[] boxBakeHalfExtents;
        private int[] boxBakeSurfaceId;
        // The motor's static-table epoch seen right after this Setup. Another host clearing the
        // table (a level switch) changes it, and then these records describe a table that no
        // longer exists: remove and restore both become no-ops instead of writing into the
        // new level's table.
        private int registeredEpoch;
        private int removedSolidCount;
        // Registration parameters (see Register). Zero by default so a scene that never calls
        // Register behaves exactly as before.
        private int boxSurfaceIdBase;
        private int rampSurfaceIdBase;
        private bool beganPlaying;
        private bool groundRegistered;
        private int registeredPlaneCount;
        private int registeredBoxCount;
        private int registeredRampCount;
        private int refusedCount;
        private string lastRampRefusal = "";
        private string setupReport = "";

        // ---- Contact v2 bake state (reset on every Setup, reported once) ----
        // Eight world corners of the collider being baked; allocated once, reused.
        private Vector3[] bakeCorners;
        private int obbAttemptCount;
        private int registeredObbCount;
        private int obbRefusedCount;
        private int obbOverflowCount;
        private int lastRefusalCode;
        private string lastRefusalReason = "";
        private int doubleBakeCheckedCount;
        private int doubleBakeMismatchCount;
        private bool loggedDoubleBakeError;
        private bool loggedShear;
        private bool loggedObbCapacity;

        public void Update()
        {
            // A suspended arena is VISIBLE but must not write the motor. The compact physics
            // test field keeps every cell's geometry on screen while registering only the
            // active one; without this gate the other hosts would self-register on their own
            // Update and latch the motor's capacity. An explicit Reapply() ignores the flag:
            // it is the owner's "register this now" call.
            if (registrationSuspended) return;
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
            obbAttemptCount = 0;
            registeredObbCount = 0;
            obbRefusedCount = 0;
            obbOverflowCount = 0;
            lastRefusalCode = RefusalNone;
            lastRefusalReason = "";
            doubleBakeCheckedCount = 0;
            doubleBakeMismatchCount = 0;
            if (bakeCorners == null) bakeCorners = new Vector3[ObbBakeMath.CornerCount];
            CheckObbCapacity();

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
                    if (RegisterRampObb(box)) registeredRampCount = registeredRampCount + 1;
                    else refusedCount = refusedCount + 1;
                }
            }

            ResetBoxRecords();
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
                        NoteRefusal(RefusalDoubleListed, box.gameObject.name + " in both arrays");
                        refusedCount = refusedCount + 1;
                        continue;
                    }
                    if (RegisterBoxObb(box, i)) registeredBoxCount = registeredBoxCount + 1;
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

            registeredEpoch = motor.GetStaticObbEpoch();
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
            // Appended AFTER the fields the tools and docs already parse, so every existing
            // "[arena] planes=… boxes=… ramps=…" prefix stays a valid substring.
            setupReport = setupReport
                + " obbs=" + registeredObbCount + "/" + obbAttemptCount
                + " obbRefused=" + obbRefusedCount
                + " obbOverflow=" + obbOverflowCount
                + " doubleBake=" + doubleBakeCheckedCount + "/" + doubleBakeMismatchCount;
            if (lastRefusalCode != RefusalNone)
            {
                setupReport = setupReport + " refusal=" + lastRefusalCode + ":"
                    + lastRefusalReason;
            }

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
        /// Clear-then-register is the only correct order: AddPlane/AddObb are additive, so a
        /// second pass without the clear would overflow the fixed capacities. ClearObstacles
        /// also drops the motor's prop solids; the pickup host re-registers them on its next
        /// scan, and the field resets its props right after. The round is not re-opened, and
        /// the kind whitelist is re-sent (it is idempotent).
        /// </remarks>
        public void Reapply()
        {
            Register(true, 0, 0);
        }

        /// <summary>
        /// The registration entry: optionally clears the motor's obstacle tables, then runs
        /// <see cref="Setup"/> again with the given parameters. <see cref="Reapply"/> is
        /// <c>Register(true, 0, 0)</c>.
        /// </summary>
        /// <param name="clearFirst">
        /// True = <c>ClearObstacles</c> first (the cell-switch behaviour). False = append to
        /// whatever the motor already holds, which is how several hosts share one motor.
        /// </param>
        /// <param name="boxSurfaceIdOffset">
        /// Added to every box surface id (<c>1 + i</c>). The caller passes the number of boxes
        /// the motor already holds from earlier hosts, so ids stay unique.
        /// </param>
        /// <param name="rampSurfaceIdOffset">
        /// Added to every ramp surface id (<c>FirstRampSurfaceId + i</c>), for the same reason.
        /// </param>
        /// <remarks>
        /// The parameters persist until the next call, which is deliberate: the host's
        /// own <see cref="Update"/> only ever registers a host that has never been ready, and
        /// that path runs with the defaults. Clear-then-register stays the only correct order
        /// when the table is not meant to be shared (AddPlane/AddObb are additive).
        /// </remarks>
        public void Register(bool clearFirst, int boxSurfaceIdOffset, int rampSurfaceIdOffset)
        {
            if (motor == null) return;
            if (motor.GetSemanticDiameterWorldM() <= 0f) return;
            if (clearFirst) motor.ClearObstacles();
            boxSurfaceIdBase = boxSurfaceIdOffset;
            rampSurfaceIdBase = rampSurfaceIdOffset;
            ready = false;
            Setup();
        }

        /// <summary>
        /// Stops this arena from registering itself in <see cref="Update"/> while leaving its
        /// GameObject and visible children on screen. The flag starts false, so every existing
        /// scene keeps its exact behaviour.
        /// </summary>
        public void SuspendRegistration()
        {
            registrationSuspended = true;
        }

        /// <summary>Lets <see cref="Update"/> register this arena again (no-op if ready).</summary>
        public void ResumeRegistration()
        {
            registrationSuspended = false;
        }

        /// <summary>True while the arena is visible but forbidden to self-register.</summary>
        public bool IsRegistrationSuspended()
        {
            return registrationSuspended;
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

        /// <summary>Static OBBs the motor accepted this setup (0 outside the v2 OBB path).</summary>
        public int GetRegisteredObbCount()
        {
            return registeredObbCount;
        }

        /// <summary>Static OBB registrations the motor refused this setup.</summary>
        public int GetObbRefusalCount()
        {
            return obbRefusedCount;
        }

        /// <summary>Refusals caused by the motor's 256-solid capacity this setup.</summary>
        public int GetObbOverflowCount()
        {
            return obbOverflowCount;
        }

        /// <summary>
        /// The last refusal code of this setup (0 = none): 1 shear, 2 degenerate, 3 OBB
        /// capacity, 4 OBB refused, 7 listed twice, 8 double-bake mismatch.
        /// </summary>
        public int GetLastRefusalCode()
        {
            return lastRefusalCode;
        }

        /// <summary>The last refusal as a literal (the object name plus what was wrong).</summary>
        public string GetLastRefusalReason()
        {
            if (lastRefusalReason == null) return "";
            return lastRefusalReason;
        }

        /// <summary>Axis-aligned colliders the §7.4 double-bake check compared this setup.</summary>
        public int GetDoubleBakeCheckCount()
        {
            return doubleBakeCheckedCount;
        }

        /// <summary>Comparisons the §7.4 check failed (0 is the only healthy value).</summary>
        public int GetDoubleBakeMismatchCount()
        {
            return doubleBakeMismatchCount;
        }

        // ------------------------------------------------------------------
        // Static OBB bake (design §1.4/§1.5/§5.7/§7.4).
        // ------------------------------------------------------------------

        /// <summary>
        /// Registers one static box as a true OBB under box surface id <c>1 + i</c> (plus
        /// the registration's box offset).
        /// </summary>
        private bool RegisterBoxObb(BoxCollider box, int boxIndex)
        {
            Vector3 center;
            Vector3 axisX;
            Vector3 axisY;
            Vector3 axisZ;
            Vector3 halfExtents;
            int refusalCode;
            bool usable = TryBakeSolidObb(box, out center, out axisX, out axisY, out axisZ,
                out halfExtents, out refusalCode);
            // A bake refusal can still produce a usable box (the shear/mismatch fallback), so
            // the code is recorded before the early return: the report must name the fallback
            // even when the geometry did register.
            if (refusalCode != RefusalNone)
            {
                NoteRefusal(refusalCode, box.gameObject.name + " " + DescribeBake(refusalCode));
            }
            if (!usable) return false;
            // The surface id is 1 + the box's registration order, so a diagnostic that
            // prints an id still names the same geometry.
            int surfaceId = boxSurfaceIdBase + 1 + registeredBoxCount;
            if (!AddSolidObb(center, axisX, axisY, axisZ, halfExtents, surfaceId,
                ContactCodes.RoleBox, box.gameObject.name)) return false;
            if (boxRegistered != null && boxIndex >= 0 && boxIndex < boxRegistered.Length)
            {
                boxRegistered[boxIndex] = true;
                boxBakeCenter[boxIndex] = center;
                boxBakeAxisX[boxIndex] = axisX;
                boxBakeAxisY[boxIndex] = axisY;
                boxBakeAxisZ[boxIndex] = axisZ;
                boxBakeHalfExtents[boxIndex] = halfExtents;
                boxBakeSurfaceId[boxIndex] = surfaceId;
            }
            return true;
        }

        // Fresh, empty records for this Setup. Allocated per Setup (a setup-time event), sized
        // to the serialized box array; a host with no boxes keeps no arrays.
        private void ResetBoxRecords()
        {
            removedSolidCount = 0;
            int count = staticBoxes == null ? 0 : staticBoxes.Length;
            if (count == 0)
            {
                boxRegistered = null;
                boxRemoved = null;
                boxBakeCenter = null;
                boxBakeAxisX = null;
                boxBakeAxisY = null;
                boxBakeAxisZ = null;
                boxBakeHalfExtents = null;
                boxBakeSurfaceId = null;
                return;
            }
            boxRegistered = new bool[count];
            boxRemoved = new bool[count];
            boxBakeCenter = new Vector3[count];
            boxBakeAxisX = new Vector3[count];
            boxBakeAxisY = new Vector3[count];
            boxBakeAxisZ = new Vector3[count];
            boxBakeHalfExtents = new Vector3[count];
            boxBakeSurfaceId = new int[count];
        }

        /// <summary>
        /// Takes out of the motor the static solid(s) that are the collision of the pickup prop
        /// <paramref name="itemId"/>, and returns how many OBBs were removed (a split solid has
        /// one per part).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The reference level's big scenery (a lantern, a sliding door, a table) is two
        /// things with one id: a prop the ball may roll up once it is big enough, and a static
        /// solid that is that prop's collision. Rolling the prop up used to leave the solid
        /// behind as an invisible wall. The pickup host calls this when it consumes the prop's
        /// absorb event.
        /// </para>
        /// <para>
        /// A no-op (0) when the host has not registered, when no box carries the id, when the
        /// box was never registered with the motor, when it is already out, or when the
        /// motor's table has been dropped since this Setup (another level's geometry is in
        /// it now). The other boxes, their surface ids and the motor's prop block are not
        /// touched, so two hosts on one motor never affect each other.
        /// </para>
        /// </remarks>
        public int RemoveSolidsOf(string itemId)
        {
            if (!ready) return 0;
            if (motor == null) return 0;
            if (itemId == null || itemId.Length == 0) return 0;
            if (staticBoxItemIds == null || boxRegistered == null) return 0;
            if (motor.GetStaticObbEpoch() != registeredEpoch) return 0;
            int removed = 0;
            for (int i = 0; i < boxRegistered.Length; i++)
            {
                if (i >= staticBoxItemIds.Length) break;
                if (!boxRegistered[i] || boxRemoved[i]) continue;
                if (staticBoxItemIds[i] != itemId) continue;
                if (!motor.RemoveStaticObb(boxBakeSurfaceId[i], boxBakeCenter[i])) continue;
                boxRemoved[i] = true;
                removed = removed + 1;
            }
            removedSolidCount = removedSolidCount + removed;
            return removed;
        }

        /// <summary>
        /// Puts back every solid <see cref="RemoveSolidsOf"/> took out, with the surface id and
        /// bake it had, and returns how many were restored. The pickup host calls it from its
        /// field reset (retry, hub switch, test-field reset).
        /// </summary>
        /// <remarks>
        /// Restoring appends to the motor's table (order is not part of the contract; ids are).
        /// If the table was dropped since Setup (a level switch cleared it), nothing is
        /// written - the entries describe a table that is gone and the next <c>Register</c>
        /// re-bakes every box from scratch - and the records are just forgotten, so a solid is
        /// never registered twice.
        /// </remarks>
        public int RestoreRemovedSolids()
        {
            if (boxRemoved == null) return 0;
            if (removedSolidCount <= 0) return 0;
            bool sameTable = ready && motor != null && motor.GetStaticObbEpoch() == registeredEpoch;
            int restored = 0;
            for (int i = 0; i < boxRemoved.Length; i++)
            {
                if (!boxRemoved[i]) continue;
                boxRemoved[i] = false;
                if (!sameTable) continue;
                if (motor.AddObb(boxBakeCenter[i], boxBakeAxisX[i], boxBakeAxisY[i],
                    boxBakeAxisZ[i], boxBakeHalfExtents[i], boxBakeSurfaceId[i],
                    ContactCodes.RoleBox))
                {
                    restored = restored + 1;
                }
                else
                {
                    NoteRefusal(RefusalObbInvalid, "restoring a removed solid: "
                        + motor.GetLastStaticObbRefusalReason());
                }
            }
            removedSolidCount = 0;
            return restored;
        }

        /// <summary>Solids currently taken out because their prop was rolled up.</summary>
        public int GetRemovedSolidCount()
        {
            return removedSolidCount;
        }

        /// <summary>
        /// Registers one ramp (or one curved-ramp slab segment) as a solid slab OBB under
        /// ramp surface id <c>SupportSurfaceMath.FirstRampSurfaceId + i</c>.
        /// </summary>
        /// <remarks>
        /// The collider IS the board: its local +Y is the walking-face normal and its top
        /// face lies on the authored walking line (the importer builds both a straight ramp
        /// and every curve segment that way), so the eight-corner fit yields exactly the slab
        /// the motor's own <c>RegisterRampSlab</c> would, without needing the motor's
        /// serialized board thickness. A curve's segments arrive here as ordinary
        /// <see cref="rampBoxes"/> entries - one logical ramp, N slabs (§5.7) - so nothing in
        /// this component needs to know that arcs exist.
        /// </remarks>
        private bool RegisterRampObb(BoxCollider box)
        {
            Vector3 center;
            Vector3 axisX;
            Vector3 axisY;
            Vector3 axisZ;
            Vector3 halfExtents;
            int refusalCode;
            bool usable = TryBakeSolidObb(box, out center, out axisX, out axisY, out axisZ,
                out halfExtents, out refusalCode);
            if (refusalCode != RefusalNone)
            {
                NoteRefusal(refusalCode, box.gameObject.name + " " + DescribeBake(refusalCode));
                // The ramp report keeps its own slot so an operator looking at a curved ramp
                // sees which segment fell back (the box path has no equivalent line).
                lastRampRefusal = box.gameObject.name + ": " + DescribeBake(refusalCode);
            }
            if (!usable) return false;
            return AddSolidObb(center, axisX, axisY, axisZ, halfExtents,
                SupportSurfaceMath.FirstRampSurfaceId + rampSurfaceIdBase + registeredRampCount,
                ContactCodes.RoleSlab, box.gameObject.name);
        }

        /// <summary>
        /// Bakes a collider's eight world corners into an OBB, with the §7.4 double-bake
        /// check and the §1.4 shear refusal.
        /// </summary>
        /// <returns>
        /// True with an orthogonal box (the fit, or the bounds AABB after a refusal);
        /// false only when neither the corners nor the bounds give a usable box.
        /// </returns>
        /// <remarks>
        /// The corner set is the design's: the collider's local centre plus/minus half its
        /// size, mapped through the collider's full world matrix. That matrix is accumulated
        /// from every level's <c>localRotation * localScale</c> instead of being read from
        /// <c>lossyScale</c> and <c>rotation</c>, because those two properties CANNOT express
        /// a sheared parent (a rotated parent with a non-uniform scale) - they would silently
        /// return an orthogonal box the author never drew. With the product, shear shows up
        /// exactly where §1.4 says it does: the three edge vectors are no longer orthogonal,
        /// and <see cref="ObbBakeMath.TryBakeFromCorners"/> refuses the box.
        /// </remarks>
        private bool TryBakeSolidObb(BoxCollider box, out Vector3 center, out Vector3 axisX,
            out Vector3 axisY, out Vector3 axisZ, out Vector3 halfExtents, out int refusalCode)
        {
            center = Vector3.zero;
            axisX = Vector3.zero;
            axisY = Vector3.zero;
            axisZ = Vector3.zero;
            halfExtents = Vector3.zero;
            refusalCode = RefusalNone;
            if (bakeCorners == null) bakeCorners = new Vector3[ObbBakeMath.CornerCount];

            FillCorners(box);
            if (!ObbBakeMath.TryBakeFromCorners(bakeCorners, out center, out axisX, out axisY,
                out axisZ, out halfExtents))
            {
                refusalCode = ClassifyBakeFailure();
                if (refusalCode == RefusalShear && !loggedShear)
                {
                    loggedShear = true;
                    Debug.LogWarning("[arena] shear on '" + box.gameObject.name
                        + "': its eight corners are not an orthogonal box (a rotated parent "
                        + "with a non-uniform scale?); registering the bounds AABB "
                        + "(design §1.4)");
                }
                Bounds shearBounds = box.bounds;
                return TryBakeBoundsFallback(shearBounds.center, shearBounds.size, out center,
                    out axisX, out axisY, out axisZ, out halfExtents);
            }

            // Only axis-aligned geometry gets the §7.4 comparison: a rotated box's world AABB
            // is legitimately larger than its OBB, so comparing them would call every tilted
            // board a mismatch.
            if (!IsWorldAxisAligned(axisX, axisY, axisZ)) return true;
            doubleBakeCheckedCount = doubleBakeCheckedCount + 1;
            Bounds bounds = box.bounds;
            if (ObbWorldAabbAgrees(center, axisX, axisY, axisZ, halfExtents, bounds.center,
                bounds.extents, DoubleBakeTolerance(center, halfExtents))) return true;

            // §7.4: for axis-aligned geometry the two bakes must agree. A disagreement is a
            // defect in one of them, so it is an error, logged once, and the AABB wins.
            doubleBakeMismatchCount = doubleBakeMismatchCount + 1;
            refusalCode = RefusalDoubleBake;
            if (!loggedDoubleBakeError)
            {
                loggedDoubleBakeError = true;
                Debug.LogError("[arena] double bake mismatch on '" + box.gameObject.name
                    + "': the eight-corner OBB and the bounds AABB disagree; registering the "
                    + "bounds AABB (design §7.4)");
            }
            return TryBakeBoundsFallback(bounds.center, bounds.size, out center, out axisX,
                out axisY, out axisZ, out halfExtents);
        }

        /// <summary>
        /// Writes the collider's eight world corners in the bit order
        /// <see cref="ObbBakeMath.TryBakeFromCorners"/> documents: bit 0 is +local X, bit 1
        /// +local Y, bit 2 +local Z.
        /// </summary>
        /// <remarks>
        /// The basis is accumulated bottom-up (<c>L_parent * L_child * ...</c>) so every
        /// level's rotation and scale is applied, including the shear a rotated parent with a
        /// non-uniform scale introduces. See <see cref="TryBakeSolidObb"/> for why
        /// <c>lossyScale</c> is not used.
        /// </remarks>
        private void FillCorners(BoxCollider box)
        {
            Transform owner = box.transform;
            Vector3 position = owner.position;
            Vector3 localCenter = box.center;
            Vector3 localSize = box.size;
            float halfX = localSize.x * 0.5f;
            float halfY = localSize.y * 0.5f;
            float halfZ = localSize.z * 0.5f;

            Vector3 basisX;
            Vector3 basisY;
            Vector3 basisZ;
            AccumulateWorldBasis(owner, out basisX, out basisY, out basisZ);

            for (int i = 0; i < ObbBakeMath.CornerCount; i++)
            {
                float signX = (i & 1) == 0 ? -1f : 1f;
                float signY = (i & 2) == 0 ? -1f : 1f;
                float signZ = (i & 4) == 0 ? -1f : 1f;
                Vector3 local = new Vector3(localCenter.x + signX * halfX,
                    localCenter.y + signY * halfY, localCenter.z + signZ * halfZ);
                Vector3 offset = ApplyBasis(basisX, basisY, basisZ, local);
                bakeCorners[i] = new Vector3(position.x + offset.x, position.y + offset.y,
                    position.z + offset.z);
            }
        }

        /// <summary>
        /// The collider transform's world basis (three columns), with every ancestor's
        /// rotation and scale applied in order.
        /// </summary>
        private void AccumulateWorldBasis(Transform owner, out Vector3 basisX,
            out Vector3 basisY, out Vector3 basisZ)
        {
            Vector3 scale = owner.localScale;
            Quaternion rotation = owner.localRotation;
            basisX = rotation * new Vector3(scale.x, 0f, 0f);
            basisY = rotation * new Vector3(0f, scale.y, 0f);
            basisZ = rotation * new Vector3(0f, 0f, scale.z);

            Transform node = owner.parent;
            while (node != null)
            {
                Vector3 parentScale = node.localScale;
                Quaternion parentRotation = node.localRotation;
                Vector3 columnX = parentRotation * new Vector3(parentScale.x, 0f, 0f);
                Vector3 columnY = parentRotation * new Vector3(0f, parentScale.y, 0f);
                Vector3 columnZ = parentRotation * new Vector3(0f, 0f, parentScale.z);
                Vector3 nextX = ApplyBasis(columnX, columnY, columnZ, basisX);
                Vector3 nextY = ApplyBasis(columnX, columnY, columnZ, basisY);
                Vector3 nextZ = ApplyBasis(columnX, columnY, columnZ, basisZ);
                basisX = nextX;
                basisY = nextY;
                basisZ = nextZ;
                node = node.parent;
            }
        }

        /// <summary><c>basis * value</c> for a basis given as three columns.</summary>
        private Vector3 ApplyBasis(Vector3 basisX, Vector3 basisY, Vector3 basisZ, Vector3 value)
        {
            return new Vector3(
                basisX.x * value.x + basisY.x * value.y + basisZ.x * value.z,
                basisX.y * value.x + basisY.y * value.y + basisZ.y * value.z,
                basisX.z * value.x + basisY.z * value.y + basisZ.z * value.z);
        }

        /// <summary>
        /// Names why the eight-corner fit failed: a degenerate edge or a sheared corner set.
        /// </summary>
        /// <remarks>
        /// The DECISION not to use the box is <see cref="ObbBakeMath.TryBakeFromCorners"/>'s;
        /// this only labels the refusal so the arena line can say "shear" instead of "invalid"
        /// (the design wants the shear case to be visible, §1.4).
        /// </remarks>
        private int ClassifyBakeFailure()
        {
            Vector3 edgeX = new Vector3(bakeCorners[1].x - bakeCorners[0].x,
                bakeCorners[1].y - bakeCorners[0].y, bakeCorners[1].z - bakeCorners[0].z);
            Vector3 edgeY = new Vector3(bakeCorners[2].x - bakeCorners[0].x,
                bakeCorners[2].y - bakeCorners[0].y, bakeCorners[2].z - bakeCorners[0].z);
            Vector3 edgeZ = new Vector3(bakeCorners[4].x - bakeCorners[0].x,
                bakeCorners[4].y - bakeCorners[0].y, bakeCorners[4].z - bakeCorners[0].z);
            float lengthX = Length3(edgeX);
            float lengthY = Length3(edgeY);
            float lengthZ = Length3(edgeZ);
            if (lengthX <= 1e-6f || lengthY <= 1e-6f || lengthZ <= 1e-6f) return RefusalDegenerate;
            float dotXY = Dot3(edgeX, edgeY) / (lengthX * lengthY);
            float dotXZ = Dot3(edgeX, edgeZ) / (lengthX * lengthZ);
            float dotYZ = Dot3(edgeY, edgeZ) / (lengthY * lengthZ);
            if (Mathf.Abs(dotXY) > ContactTuning.BakeShearTolerance) return RefusalShear;
            if (Mathf.Abs(dotXZ) > ContactTuning.BakeShearTolerance) return RefusalShear;
            if (Mathf.Abs(dotYZ) > ContactTuning.BakeShearTolerance) return RefusalShear;
            return RefusalDegenerate;
        }

        private float Length3(Vector3 value)
        {
            return Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z);
        }

        private float Dot3(Vector3 left, Vector3 right)
        {
            return left.x * right.x + left.y * right.y + left.z * right.z;
        }

        /// <summary>The §1.4 fallback: a world-axis box from the collider's bounds.</summary>
        /// <remarks>
        /// The centre and size travel as two Vector3s rather than a <c>Bounds</c>: the
        /// fallback reads only the plain <c>Collider.bounds</c> pair.
        /// </remarks>
        private bool TryBakeBoundsFallback(Vector3 boundsCenter, Vector3 boundsSize,
            out Vector3 center, out Vector3 axisX, out Vector3 axisY, out Vector3 axisZ,
            out Vector3 halfExtents)
        {
            center = Vector3.zero;
            axisX = Vector3.zero;
            axisY = Vector3.zero;
            axisZ = Vector3.zero;
            halfExtents = Vector3.zero;
            if (ObbBakeMath.TryBakeFromBounds(boundsCenter, boundsSize, out axisX, out axisY,
                out axisZ, out halfExtents))
            {
                center = boundsCenter;
                return true;
            }
            center = Vector3.zero;
            return false;
        }

        /// <summary>
        /// True when every axis is a world basis direction to within a 1e-4 m/m slack.
        /// </summary>
        /// <remarks>
        /// Only axis-aligned geometry gets the §7.4 tolerance: a rotated box's world AABB is
        /// legitimately larger than its OBB, so comparing them would call every tilted board
        /// a mismatch.
        /// </remarks>
        private bool IsWorldAxisAligned(Vector3 axisX, Vector3 axisY, Vector3 axisZ)
        {
            return IsAxisAligned(axisX) && IsAxisAligned(axisY) && IsAxisAligned(axisZ);
        }

        private bool IsAxisAligned(Vector3 axis)
        {
            float ax = Mathf.Abs(axis.x);
            float ay = Mathf.Abs(axis.y);
            float az = Mathf.Abs(axis.z);
            if (ax > 0.9999f && ay < 0.0001f && az < 0.0001f) return true;
            if (ay > 0.9999f && ax < 0.0001f && az < 0.0001f) return true;
            if (az > 0.9999f && ax < 0.0001f && ay < 0.0001f) return true;
            return false;
        }

        /// <summary>§7.4's tolerance: 1e-6 m plus one part per million of the box's reach.</summary>
        private float DoubleBakeTolerance(Vector3 center, Vector3 halfExtents)
        {
            float reach = Mathf.Abs(center.x) + Mathf.Abs(center.y) + Mathf.Abs(center.z)
                + halfExtents.x + halfExtents.y + halfExtents.z;
            float scale = reach * 1e-6f;
            if (scale > DoubleBakeToleranceM) return scale;
            return DoubleBakeToleranceM;
        }

        /// <summary>
        /// Compares the OBB's world AABB with the collider's bounds within
        /// <paramref name="toleranceM"/> per component.
        /// </summary>
        private bool ObbWorldAabbAgrees(Vector3 center, Vector3 axisX, Vector3 axisY,
            Vector3 axisZ, Vector3 halfExtents, Vector3 boundsCenter, Vector3 boundsExtents,
            float toleranceM)
        {
            float extentX = Mathf.Abs(axisX.x) * halfExtents.x + Mathf.Abs(axisY.x) * halfExtents.y
                + Mathf.Abs(axisZ.x) * halfExtents.z;
            float extentY = Mathf.Abs(axisX.y) * halfExtents.x + Mathf.Abs(axisY.y) * halfExtents.y
                + Mathf.Abs(axisZ.y) * halfExtents.z;
            float extentZ = Mathf.Abs(axisX.z) * halfExtents.x + Mathf.Abs(axisY.z) * halfExtents.y
                + Mathf.Abs(axisZ.z) * halfExtents.z;
            if (Mathf.Abs(center.x - boundsCenter.x) > toleranceM) return false;
            if (Mathf.Abs(center.y - boundsCenter.y) > toleranceM) return false;
            if (Mathf.Abs(center.z - boundsCenter.z) > toleranceM) return false;
            if (Mathf.Abs(extentX - boundsExtents.x) > toleranceM) return false;
            if (Mathf.Abs(extentY - boundsExtents.y) > toleranceM) return false;
            if (Mathf.Abs(extentZ - boundsExtents.z) > toleranceM) return false;
            return true;
        }

        /// <summary>
        /// Calls <c>AddObb</c> once and turns a refusal into a counted, reason-coded answer.
        /// </summary>
        /// <remarks>
        /// Overflow and a bad box are told apart by the motor's own overflow counter: a
        /// refusal that did not move it was a geometry refusal. Both are reported, neither is
        /// dropped (§5.4): the scene keeps the other solids and the arena's one line names the
        /// count and the last reason.
        /// </remarks>
        private bool AddSolidObb(Vector3 center, Vector3 axisX, Vector3 axisY, Vector3 axisZ,
            Vector3 halfExtents, int surfaceId, int role, string objectName)
        {
            obbAttemptCount = obbAttemptCount + 1;
            int overflowBefore = motor.GetStaticObbOverflowCount();
            if (motor.AddObb(center, axisX, axisY, axisZ, halfExtents, surfaceId, role))
            {
                registeredObbCount = registeredObbCount + 1;
                return true;
            }
            obbRefusedCount = obbRefusedCount + 1;
            if (motor.GetStaticObbOverflowCount() > overflowBefore)
            {
                obbOverflowCount = obbOverflowCount + 1;
                NoteRefusal(RefusalObbCapacity, objectName + ": obb capacity "
                    + motor.GetStaticObbCapacity() + " exceeded");
            }
            else
            {
                NoteRefusal(RefusalObbInvalid, objectName + ": "
                    + motor.GetLastStaticObbRefusalReason());
            }
            return false;
        }

        /// <summary>
        /// Refuses the whole registration up front when the scene asks for more solids than the
        /// motor's table holds, so the overflow is one error line and not 100 silent drops.
        /// </summary>
        /// <remarks>
        /// The per-entry refusal is still counted by <see cref="AddSolidObb"/> (the motor
        /// refuses each one), so a partial table is visible in both the error and the report
        /// line. The count matches what the loops below will attempt: non-null ramps plus
        /// non-null boxes that are not also listed as ramps.
        /// </remarks>
        private void CheckObbCapacity()
        {
            int requested = 0;
            if (rampBoxes != null)
            {
                for (int i = 0; i < rampBoxes.Length; i++)
                {
                    if (rampBoxes[i] != null) requested = requested + 1;
                }
            }
            if (staticBoxes != null)
            {
                for (int i = 0; i < staticBoxes.Length; i++)
                {
                    if (staticBoxes[i] == null) continue;
                    if (IsRampCollider(staticBoxes[i])) continue;
                    requested = requested + 1;
                }
            }
            // Counted against what the motor already holds: an appended host (Register with
            // clearFirst = false) shares the table with the hosts before it. After a clear, or
            // for a lone host, the held count is 0 and this is the old per-host check.
            int held = motor.GetStaticObbCount();
            int capacity = motor.GetStaticObbCapacity();
            if (requested + held <= capacity) return;
            NoteRefusal(RefusalObbCapacity, "scene asks for " + requested + " static OBBs on top of "
                + held + ", capacity " + capacity);
            if (loggedObbCapacity) return;
            loggedObbCapacity = true;
            Debug.LogError("[arena] static OBB table overflow: " + held + " already held + "
                + requested + " from this host (boxes + ramp slabs + curve segments) > "
                + capacity + " slots; the ones past " + capacity + " are refused and counted, "
                + "never dropped silently (design §5.4)");
        }

        private void NoteRefusal(int code, string reason)
        {
            if (code == RefusalNone) return;
            lastRefusalCode = code;
            lastRefusalReason = reason;
        }

        /// <summary>The one English phrase per bake refusal code, for the report line.</summary>
        private string DescribeBake(int code)
        {
            if (code == RefusalShear) return "shear";
            if (code == RefusalDegenerate) return "degenerate";
            if (code == RefusalDoubleBake) return "double bake mismatch";
            if (code == RefusalObbCapacity) return "obb capacity";
            if (code == RefusalObbInvalid) return "obb refused";
            return "none";
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
