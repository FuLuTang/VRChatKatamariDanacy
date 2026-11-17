using UdonSharp;
using UnityEngine;
using VRCKatamari.BallLayers;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;
using VRCKatamari.Pickup;
using VRCKatamari.Story;

namespace VRCKatamari.Hub
{
    /// <summary>
    /// Makes one level "the active one" on the single ball motor of a multi-level scene:
    /// it swaps what the motor knows about the world (obstacles, terrain, ground, kind
    /// whitelist, start size) and puts the ball on that level's start. The hub launcher
    /// calls <see cref="TryActivate"/> before it moves the player.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists. One scene holds several levels far apart on islands, but there is
    /// one motor, and the motor's tables are global: <c>ClearObstacles()</c> does not clear
    /// the terrain mesh, the prop proxies, the ground plane, the kind whitelist or the
    /// start size. So "switching level" is ten small steps in a fixed order (see
    /// <see cref="TryActivate"/>), and this behaviour is the one place that knows them.
    /// Only the active level is ever registered, which is what keeps the fixed-capacity
    /// tables (8 planes, 256 static OBBs, one terrain) sufficient.
    /// </para>
    /// <para>
    /// Data layout. UdonSharp cannot serialize nested arrays, so per-level lists are
    /// stored flat with a start/count pair per level (the same pattern as the motor's
    /// prop-proxy hulls). Every per-level array is indexed by level index, the same index
    /// the launcher's entry list and the round controller use. All arrays may be shorter
    /// or null: a missing entry means "this level has none of that".
    /// </para>
    /// <para>
    /// <see cref="Start"/> suspends every arena host's self-registration (a
    /// <c>LevelArenaHost</c> otherwise registers itself on its own <c>Update</c>, and a
    /// dozen of them would fill the plane table in the first frame). Nothing is
    /// registered until the first switch; until then the ball sits where the scene put it.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class LevelSwitchHost : UdonSharpBehaviour
    {
        // ---- Switch failure codes (out value of TryActivate; 0 = success) ----
        public const int FailNone = 0;
        public const int FailNoMotor = 1;
        public const int FailMotorNotReady = 2;
        public const int FailLevelIndex = 3;
        public const int FailBallReset = 4;

        [Header("Links")]
        [SerializeField] private KatamariBallMotor motor;

        // One slot per level, each may be null.
        [SerializeField] private PickupFieldHost[] pickupHosts;
        [SerializeField] private TerrainFieldHost[] terrainHosts;

        [Header("Per level (index = level index)")]
        [SerializeField] private string[] levelIds;
        [SerializeField] private float[] groundOffsets;

        // Arena hosts, flat. Level i owns arenaHosts[arenaStarts[i] .. +arenaCounts[i]],
        // registered in that order: the first one clears the table (put the frame/outer
        // walls first), the rest append with their surface ids offset.
        [Header("Arena hosts (flat)")]
        [SerializeField] private LevelArenaHost[] arenaHosts;
        [SerializeField] private int[] arenaStarts;
        [SerializeField] private int[] arenaCounts;

        // Kind whitelist per level, flat. A level with count 0 keeps the whitelist the
        // arena hosts set (a single arena host sends its own).
        [Header("Allowed kinds (flat; optional)")]
        [SerializeField] private string[] kindsFlat;
        [SerializeField] private int[] kindsStarts;
        [SerializeField] private int[] kindsCounts;

        // The ball's start per level, world metres (the island offset already added).
        // startVolumeMm3 <= 0 keeps the motor's current start size.
        [Header("Ball start")]
        [SerializeField] private Vector3[] startCenters;
        [SerializeField] private float[] startHeadingDegrees;
        [SerializeField] private long[] startVolumesMm3;

        // Metres above the ground plane the ball is lifted to when an authored start
        // centre would be below it.
        [SerializeField] private float groundLiftMeters = 0.02f;

        // -1 = no level has been activated yet.
        private int activeLevel = -1;
        private int switchCounter;
        private int lastFailure;

        private void Start()
        {
            // Every arena host of every level: none may register on its own.
            if (arenaHosts == null) return;
            int suspended = 0;
            for (int i = 0; i < arenaHosts.Length; i++)
            {
                if (arenaHosts[i] == null) continue;
                arenaHosts[i].SuspendRegistration();
                suspended = suspended + 1;
            }
            Debug.Log("[switch] ready levels=" + LevelCount() + " arenaHosts=" + suspended);
        }

        /// <summary>Number of levels this host knows (length of the id list).</summary>
        public int LevelCount()
        {
            return levelIds == null ? 0 : levelIds.Length;
        }

        /// <summary>The level last activated successfully; -1 before the first switch.</summary>
        public int GetActiveLevel()
        {
            return activeLevel;
        }

        /// <summary>The <c>Fail*</c> code of the last <see cref="TryActivate"/>.</summary>
        public int GetLastFailure()
        {
            return lastFailure;
        }

        /// <summary>
        /// Activates <paramref name="levelIndex"/>: the motor ends up knowing exactly that
        /// level's world and the ball stands on its start. Returns false with a
        /// <c>Fail*</c> code and changes nothing when it cannot start.
        /// </summary>
        /// <remarks>
        /// The order is the contract. The leaving level's pickups are reset first, so
        /// coming back finds them clean; the three clears come next, because
        /// <c>ClearObstacles</c> alone leaves the terrain and prop proxies behind; the
        /// ground plane is set before the arenas (each arena sets it again with its own
        /// value); arenas register frame-first with growing surface-id offsets (the
        /// physics test field's sandbox order); the terrain, whitelist and start size
        /// follow; and the ball is reset and placed last, after the world it stands in
        /// exists. A level that is already active is switched again (re-registered and
        /// reset), so a caller that does not want that checks <see cref="GetActiveLevel"/>.
        /// </remarks>
        public bool TryActivate(int levelIndex, out int failure)
        {
            failure = FailNone;
            lastFailure = FailNone;

            // 1. The motor is up (the same readiness proof the arena hosts use).
            if (motor == null)
            {
                failure = FailNoMotor;
                lastFailure = failure;
                return false;
            }
            if (motor.GetSemanticDiameterWorldM() <= 0f)
            {
                failure = FailMotorNotReady;
                lastFailure = failure;
                return false;
            }
            if (levelIndex < 0 || levelIndex >= LevelCount())
            {
                failure = FailLevelIndex;
                lastFailure = failure;
                return false;
            }

            // 2. Leave the previous level's pickups clean.
            if (activeLevel >= 0)
            {
                PickupFieldHost leaving = PickupAt(activeLevel);
                if (leaving != null)
                {
                    leaving.SetCollecting(false);
                    leaving.ResetField();
                }
            }

            // 3. Everything the motor holds about the world.
            motor.ClearObstacles();
            motor.ClearTerrainMesh();
            motor.ClearPropProxies();

            // 4. Ground plane first; an arena host sets it again with its own offset.
            float groundOffset = 0f;
            if (groundOffsets != null && levelIndex < groundOffsets.Length)
            {
                groundOffset = groundOffsets[levelIndex];
            }
            motor.SetGroundPlane(new Vector3(0f, 1f, 0f), groundOffset);

            // 5. Arena hosts: the first clears (already cleared above, so it is a no-op
            // clear), the rest append with ids offset by what came before.
            int boxes = 0;
            int ramps = 0;
            int hostsRegistered = 0;
            int arenaStart = StartOf(arenaStarts, levelIndex);
            int arenaCount = CountOf(arenaCounts, levelIndex);
            for (int i = 0; i < arenaCount; i++)
            {
                int slot = arenaStart + i;
                if (arenaHosts == null || slot < 0 || slot >= arenaHosts.Length) break;
                LevelArenaHost host = arenaHosts[slot];
                if (host == null) continue;
                host.Register(hostsRegistered == 0, boxes, ramps);
                boxes = boxes + host.GetRegisteredBoxCount();
                ramps = ramps + host.GetRegisteredRampCount();
                hostsRegistered = hostsRegistered + 1;
            }

            // 6. Terrain: the one terrain this level owns.
            bool terrainOk = true;
            TerrainFieldHost terrain = TerrainAt(levelIndex);
            if (terrain != null) terrainOk = terrain.RegisterTerrain();

            // 7. Kind whitelist (a union when several hosts contribute; each host already
            // sent its own, so this only overrides when the level declares one).
            int kindStart = StartOf(kindsStarts, levelIndex);
            int kindCount = CountOf(kindsCounts, levelIndex);
            if (kindCount > 0 && kindsFlat != null && kindStart >= 0
                && kindStart + kindCount <= kindsFlat.Length)
            {
                string[] kinds = new string[kindCount];
                for (int k = 0; k < kindCount; k++) kinds[k] = kindsFlat[kindStart + k];
                motor.SetAllowedKinds(kinds, kindCount);
            }

            // 8. The ball: start size, then a fresh round id, then the position.
            if (startVolumesMm3 != null && levelIndex < startVolumesMm3.Length
                && startVolumesMm3[levelIndex] > 0L)
            {
                if (!motor.SetStartVolumeMm3(startVolumesMm3[levelIndex]))
                {
                    Debug.LogWarning("[switch] start volume " + startVolumesMm3[levelIndex]
                        + " refused for level " + levelIndex + "; keeping the current start size");
                }
            }

            Vector3 center = Vector3.zero;
            if (startCenters != null && levelIndex < startCenters.Length)
            {
                center = startCenters[levelIndex];
            }
            float heading = 0f;
            if (startHeadingDegrees != null && levelIndex < startHeadingDegrees.Length)
            {
                heading = startHeadingDegrees[levelIndex];
            }

            switchCounter = switchCounter + 1;
            string levelId = levelIds[levelIndex];
            if (levelId == null) levelId = "level" + levelIndex;
            int resetReason = -1;
            bool reset = motor.TryResetForNewRound(levelId + ":hub:" + switchCounter,
                center, heading, out resetReason);
            if (!reset)
            {
                // The world is already swapped; leave activeLevel alone so the caller can
                // retry the same switch.
                failure = FailBallReset;
                lastFailure = failure;
                Debug.LogWarning("[switch] level=" + levelId + " ball reset refused reason="
                    + resetReason);
                return false;
            }

            // Read the radius AFTER the reset (it re-derived the size), then lift the
            // centre if the authored one is below the ground plane.
            float radius = motor.GetCollisionRadiusWorldM();
            if (radius <= 0f) radius = 0.15f;
            float minCenterY = groundOffset + radius + groundLiftMeters;
            if (center.y < minCenterY) center.y = minCenterY;
            motor.TeleportTo(center);
            motor.SetMoveAxes(0f, 0f, 0f);

            // 9. The new level's pickups start clean.
            PickupFieldHost entering = PickupAt(levelIndex);
            if (entering != null) entering.ResetField();

            // 10. Done.
            activeLevel = levelIndex;
            Debug.Log("[switch] level=" + levelId
                + " index=" + levelIndex
                + " arenas=" + hostsRegistered
                + " boxes=" + boxes + " ramps=" + ramps
                + " obbs=" + motor.GetStaticObbCount() + "/" + motor.GetStaticObbCapacity()
                + " obbOverflow=" + motor.GetStaticObbOverflowCount()
                + " terrainTris=" + motor.GetTerrainMeshTriangleCount()
                + " terrainOk=" + terrainOk
                + " ground=" + groundOffset
                + " startDiameter=" + motor.GetStartDiameterWorldM()
                + " start=" + center
                + " heading=" + heading);
            return true;
        }

        private PickupFieldHost PickupAt(int levelIndex)
        {
            if (pickupHosts == null || levelIndex < 0 || levelIndex >= pickupHosts.Length)
            {
                return null;
            }
            return pickupHosts[levelIndex];
        }

        private TerrainFieldHost TerrainAt(int levelIndex)
        {
            if (terrainHosts == null || levelIndex < 0 || levelIndex >= terrainHosts.Length)
            {
                return null;
            }
            return terrainHosts[levelIndex];
        }

        private int StartOf(int[] starts, int levelIndex)
        {
            if (starts == null || levelIndex < 0 || levelIndex >= starts.Length) return 0;
            return starts[levelIndex];
        }

        private int CountOf(int[] counts, int levelIndex)
        {
            if (counts == null || levelIndex < 0 || levelIndex >= counts.Length) return 0;
            return counts[levelIndex];
        }
    }
}
