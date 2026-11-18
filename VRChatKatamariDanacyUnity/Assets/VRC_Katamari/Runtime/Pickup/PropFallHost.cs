using UdonSharp;
using UnityEngine;
using VRCKatamari.BallLayers;

namespace VRCKatamari.Pickup
{
    /// <summary>
    /// Stack fall: when a prop is eaten, the props that were standing on it (its children in
    /// the single-parent column) fall with pure gravity onto the highest surface below and
    /// land untilted, layer by layer, and are ordinary free props again afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Design: Docs/Design/PickupAbsorb/PROP_BEHAVIOURS.md section 10.9 (decision
    /// Docs/OPEN_DECISIONS.md (s)); every rule lives in <see cref="PropFallMath"/>. This
    /// behaviour only owns the state arrays, reads Transforms and moves them vertically.
    /// It never touches the ball, the ledger or the pickup host's three states: the pickup
    /// host calls <see cref="OnPropAbsorbed"/> once per eaten row, and asks
    /// <see cref="IsPropFalling"/> / <see cref="ResetFalls"/>.
    /// </para>
    /// <para>
    /// Cost: <c>Update</c> returns at once while nothing is queued or falling. While work
    /// exists it runs fixed 30 Hz steps (at most <see cref="PropFallMath.MaxStepsPerFrame"/>
    /// per frame). A supported child stays queued for <c>maxRecheckSeconds</c> and is then
    /// dropped, so the host goes idle by itself a few seconds after the last event.
    /// Supporters are found through a static XZ grid built once, never by scanning every
    /// prop; the ground under a prop is sampled once when it is queued and cached.
    /// </para>
    /// <para>
    /// Known limits (documented, not hidden): props that move sideways on their own (the
    /// motion host) are indexed at their START cell, so they are found as supporters only
    /// near it; a prop is judged by its world AABB, so a tilted or narrow support counts as
    /// its box. Local only: <c>BehaviourSyncMode.None</c>.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PropFallHost : UdonSharpBehaviour
    {
        private const int CandidateCapacity = 64;
        private const int GroundProbeCount = 5;

        [SerializeField] private PickupFieldHost pickupField;
        // Optional. Without terrain data a prop that has no prop below it falls to floorY.
        [SerializeField] private TerrainFieldHost terrain;
        [SerializeField] private float floorY = -100f;
        [SerializeField] private float cellSizeM = 1.5f;
        // How long a prop that is still supported after its parent went is re-checked
        // before the host forgets it (the owner's "every tick" question, section 10.9).
        [SerializeField] private float maxRecheckSeconds = 3f;

        private bool ready;
        private int count;
        private Transform[] transforms;
        private int[] parents;
        private int[] childStart;
        private int[] childList;
        private int[] state;
        private float[] velocityY;
        private bool[] pendingFlag;
        private int[] rechecks;
        private int[] queueA;
        private int[] queueB;
        private int queueCount;
        private int[] fallingList;
        private int fallingCount;
        private int maxRechecks;

        // The box of each row. XZ extents come from the start pose; bottom is kept in step
        // with the Transform for every prop this host moves and refreshed for candidates.
        private float[] rowMinX;
        private float[] rowMaxX;
        private float[] rowMinZ;
        private float[] rowMaxZ;
        private float[] rowBottom;
        private float[] rowHeight;
        private Vector3[] startPositions;
        private Vector3[] rowSizes;
        private bool[] moved;
        private float[] groundHeight;
        private bool[] groundValid;

        private float[] gridInfo;
        private int[] cellStart;
        private int[] cellItems;
        private bool gridReady;
        private int[] stamp;
        private int stampCounter;
        private int[] candidates;

        private float accumulator;
        private int repairedParents;
        private int landedCount;
        private bool loggedReady;

        /// <summary>Read-only: the prop is in free fall right now (motion hosts must not drive it).</summary>
        public bool IsPropFalling(int index)
        {
            if (!ready || index < 0 || index >= count) return false;
            return state[index] == PropFallMath.StateFalling;
        }

        /// <summary>One of the <c>PropFallMath.State*</c> values; resting before the host is ready.</summary>
        public int GetPropFallState(int index)
        {
            if (!ready || index < 0 || index >= count) return PropFallMath.StateResting;
            return state[index];
        }

        /// <summary>Props falling now.</summary>
        public int GetFallingCount()
        {
            return fallingCount;
        }

        /// <summary>Props waiting for their next support re-check.</summary>
        public int GetPendingCount()
        {
            return queueCount;
        }

        /// <summary>Props that have landed since the field was last reset.</summary>
        public int GetLandedCount()
        {
            return landedCount;
        }

        /// <summary>Parent-column entries the sanitizer had to repair (0 for clean data).</summary>
        public int GetRepairedParentCount()
        {
            return repairedParents;
        }

        /// <summary>
        /// Called by the pickup host once per eaten row: the row stops being a support and
        /// its children are queued for a support re-check on the next tick.
        /// </summary>
        public void OnPropAbsorbed(int index)
        {
            if (!EnsureReady()) return;
            if (index < 0 || index >= count) return;
            if (state[index] == PropFallMath.StateAbsorbed) return;
            if (state[index] == PropFallMath.StateFalling) RemoveFalling(index);
            state[index] = PropFallMath.StateAbsorbed;
            velocityY[index] = 0f;
            pendingFlag[index] = false;
            int before = queueCount;
            queueCount = PropFallMath.EnqueueChildren(index, childStart, childList, state,
                pendingFlag, queueA, queueCount);
            for (int k = before; k < queueCount; k++)
            {
                int child = queueA[k];
                rechecks[child] = 0;
                groundValid[child] = false;
            }
        }

        /// <summary>
        /// The pickup host's field reset: every prop this host moved returns to its start
        /// position and all fall state is cleared.
        /// </summary>
        public void ResetFalls()
        {
            if (!ready) return;
            for (int i = 0; i < count; i++)
            {
                if (moved[i] && transforms[i] != null)
                {
                    transforms[i].position = startPositions[i];
                }
                moved[i] = false;
                state[i] = PropFallMath.StateResting;
                velocityY[i] = 0f;
                pendingFlag[i] = false;
                rechecks[i] = 0;
                groundValid[i] = false;
                rowBottom[i] = startPositions[i].y - rowHeight[i] * 0.5f;
            }
            queueCount = 0;
            fallingCount = 0;
            landedCount = 0;
            accumulator = 0f;
        }

        public void Update()
        {
            if (!ready)
            {
                EnsureReady();
                return;
            }
            if (queueCount == 0 && fallingCount == 0)
            {
                accumulator = 0f;
                return;
            }

            accumulator = accumulator + Time.deltaTime;
            float cap = PropFallMath.StepSeconds * PropFallMath.MaxStepsPerFrame;
            if (accumulator > cap) accumulator = cap;
            while (accumulator >= PropFallMath.StepSeconds)
            {
                accumulator = accumulator - PropFallMath.StepSeconds;
                StepOnce();
                if (queueCount == 0 && fallingCount == 0) break;
            }
        }

        // ------------------------------------------------------------------ set-up ---

        private bool EnsureReady()
        {
            if (ready) return true;
            if (pickupField == null) return false;
            int n = pickupField.GetPropCount();
            if (n <= 0) return false;

            count = n;
            transforms = new Transform[n];
            parents = new int[n];
            childStart = new int[n + 1];
            childList = new int[n];
            state = new int[n];
            velocityY = new float[n];
            pendingFlag = new bool[n];
            rechecks = new int[n];
            queueA = new int[n];
            queueB = new int[n];
            fallingList = new int[n];
            rowMinX = new float[n];
            rowMaxX = new float[n];
            rowMinZ = new float[n];
            rowMaxZ = new float[n];
            rowBottom = new float[n];
            rowHeight = new float[n];
            startPositions = new Vector3[n];
            rowSizes = new Vector3[n];
            moved = new bool[n];
            groundHeight = new float[n];
            groundValid = new bool[n];
            stamp = new int[n];
            candidates = new int[CandidateCapacity];
            gridInfo = new float[PropFallMath.GridInfoLength];
            queueCount = 0;
            fallingCount = 0;
            stampCounter = 0;

            int[] raw = new int[n];
            for (int i = 0; i < n; i++) raw[i] = pickupField.GetPropParentIndex(i);
            repairedParents = PropFallMath.SanitizeParents(raw, n, parents);
            PropFallMath.BuildChildTable(parents, n, childStart, childList);

            int edges = 0;
            for (int i = 0; i < n; i++)
            {
                transforms[i] = pickupField.GetPropTransform(i);
                Vector3 size = pickupField.GetPropBounds(i);
                Vector3 position = Vector3.zero;
                if (transforms[i] != null) position = transforms[i].position;
                startPositions[i] = position;
                rowSizes[i] = size;
                rowHeight[i] = size.y;
                rowBottom[i] = position.y - size.y * 0.5f;
                rowMinX[i] = position.x - size.x * 0.5f;
                rowMaxX[i] = position.x + size.x * 0.5f;
                rowMinZ[i] = position.z - size.z * 0.5f;
                rowMaxZ[i] = position.z + size.z * 0.5f;
                if (parents[i] >= 0) edges = edges + 1;
            }

            gridReady = PropFallMath.ComputeGrid(rowMinX, rowMaxX, rowMinZ, rowMaxZ, n,
                cellSizeM, gridInfo);
            if (gridReady)
            {
                int cells = (int)gridInfo[PropFallMath.GridColumns] *
                            (int)gridInfo[PropFallMath.GridRows];
                cellStart = new int[cells + 1];
                int entries = PropFallMath.CountGridEntries(gridInfo, rowMinX, rowMaxX, rowMinZ,
                    rowMaxZ, n);
                cellItems = new int[entries];
                PropFallMath.FillGrid(gridInfo, rowMinX, rowMaxX, rowMinZ, rowMaxZ, n, cellStart,
                    cellItems);
            }

            maxRechecks = (int)(maxRecheckSeconds / PropFallMath.StepSeconds);
            if (maxRechecks < 1) maxRechecks = 1;
            ready = true;
            if (!loggedReady)
            {
                loggedReady = true;
                Debug.Log("[PropFallHost] ready props=" + n + " parentEdges=" + edges
                    + " repaired=" + repairedParents + " grid=" + (gridReady ? "on" : "off")
                    + " terrain=" + (terrain != null && terrain.HasTerrainData() ? "on" : "floorY"));
            }
            return true;
        }

        // -------------------------------------------------------------- tick step ---

        private void StepOnce()
        {
            // 1. Re-check the props queued before this tick. Children of a prop that
            // starts to fall go to the NEXT tick's queue (layer by layer).
            int nextCount = 0;
            int checkedCount = queueCount;
            for (int k = 0; k < checkedCount; k++)
            {
                int i = queueA[k];
                bool supported = true;
                if (state[i] != PropFallMath.StateAbsorbed && state[i] != PropFallMath.StateFalling)
                {
                    if (!pickupField.IsPropFree(i))
                    {
                        // Eaten without a notification (lost ring event): treat as eaten.
                        OnLateAbsorbed(i);
                    }
                    else
                    {
                        supported = IsSupported(i);
                    }
                }
                int previousFalling = state[i];
                nextCount = PropFallMath.ApplyRecheck(i, supported, maxRechecks, state, velocityY,
                    pendingFlag, rechecks, childStart, childList, queueB, nextCount);
                if (previousFalling != PropFallMath.StateFalling &&
                    state[i] == PropFallMath.StateFalling)
                {
                    fallingList[fallingCount] = i;
                    fallingCount = fallingCount + 1;
                    for (int c = childStart[i]; c < childStart[i + 1]; c++)
                    {
                        rechecks[childList[c]] = 0;
                        groundValid[childList[c]] = false;
                    }
                }
            }
            int[] swap = queueA;
            queueA = queueB;
            queueB = swap;
            queueCount = nextCount;

            // 2. Move everything that is falling (including props that just started).
            int index = 0;
            while (index < fallingCount)
            {
                int i = fallingList[index];
                if (FallOne(i))
                {
                    // Landed: swap-remove, so the next element now sits at `index`.
                    fallingCount = fallingCount - 1;
                    fallingList[index] = fallingList[fallingCount];
                }
                else
                {
                    index = index + 1;
                }
            }
        }

        private void OnLateAbsorbed(int i)
        {
            if (state[i] == PropFallMath.StateFalling) RemoveFalling(i);
            state[i] = PropFallMath.StateAbsorbed;
            velocityY[i] = 0f;
        }

        private void RemoveFalling(int i)
        {
            for (int k = 0; k < fallingCount; k++)
            {
                if (fallingList[k] != i) continue;
                fallingCount = fallingCount - 1;
                fallingList[k] = fallingList[fallingCount];
                return;
            }
        }

        /// <summary>One fall step for one prop; true when it landed.</summary>
        private bool FallOne(int i)
        {
            Transform t = transforms[i];
            if (t == null || !pickupField.IsPropFree(i))
            {
                if (state[i] == PropFallMath.StateFalling) state[i] = PropFallMath.StateAbsorbed;
                return true;
            }

            float previousBottom = rowBottom[i];
            int found = GatherCandidates(i);
            float landing = PropFallMath.HighestLandingTop(i, candidates, found, state, rowMinX,
                rowMaxX, rowMinZ, rowMaxZ, rowBottom, rowHeight, rowMinX[i], rowMaxX[i],
                rowMinZ[i], rowMaxZ[i], previousBottom);
            float ground = GroundHeightOf(i, previousBottom);
            if (ground > landing && PropFallMath.IsLandingSurface(ground, previousBottom))
                landing = ground;
            if (landing <= PropFallMath.NoSurfaceY * 0.5f) landing = floorY;

            float newBottom;
            float newVelocity;
            bool landed = PropFallMath.StepFall(previousBottom, velocityY[i], landing,
                out newBottom, out newVelocity);
            velocityY[i] = newVelocity;

            // The pickup host finds static props by cached position; a prop this host moves
            // must be read live from its first write on (idempotent, one cross call per fall).
            if (!moved[i]) pickupField.MarkPropMobile(i);
            Vector3 position = t.position;
            position.y = position.y + (newBottom - previousBottom);
            t.position = position;
            rowBottom[i] = newBottom;
            moved[i] = true;

            if (landed)
            {
                state[i] = PropFallMath.StateSettled;
                velocityY[i] = 0f;
                landedCount = landedCount + 1;
                // A settled prop is a normal prop again; its own children (if still
                // standing on it) were queued when it began to fall and are re-judged against
                // its new, lower top as supporters.
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ support ---

        private bool IsSupported(int i)
        {
            RefreshRow(i);
            int found = GatherCandidates(i);
            if (PropFallMath.IsSeatedOnAny(i, candidates, found, state, rowMinX, rowMaxX, rowMinZ,
                rowMaxZ, rowBottom, rowHeight, rowMinX[i], rowMaxX[i], rowMinZ[i], rowMaxZ[i],
                rowBottom[i], rowHeight[i])) return true;
            float ground = GroundHeightOf(i, rowBottom[i] + rowHeight[i] * PropFallMath.HoverFraction);
            return PropFallMath.IsSeatedOnSurface(rowBottom[i], rowHeight[i], ground);
        }

        /// <summary>Re-reads a row's position from its Transform (a motion host may have moved it).</summary>
        private void RefreshRow(int i)
        {
            Transform t = transforms[i];
            if (t == null) return;
            Vector3 position = t.position;
            Vector3 size = rowSizes[i];
            rowBottom[i] = position.y - size.y * 0.5f;
            rowMinX[i] = position.x - size.x * 0.5f;
            rowMaxX[i] = position.x + size.x * 0.5f;
            rowMinZ[i] = position.z - size.z * 0.5f;
            rowMaxZ[i] = position.z + size.z * 0.5f;
        }

        private int GatherCandidates(int i)
        {
            if (!gridReady) return 0;
            stampCounter = stampCounter + 1;
            int found = PropFallMath.CollectCandidates(gridInfo, cellStart, cellItems, stamp,
                stampCounter, rowMinX[i], rowMaxX[i], rowMinZ[i], rowMaxZ[i], candidates);
            // Candidates are read live: a neighbour may have been moved by a fall or a motion.
            for (int k = 0; k < found; k++)
            {
                int j = candidates[k];
                if (j != i && state[j] != PropFallMath.StateFalling) RefreshRow(j);
            }
            return found;
        }

        /// <summary>
        /// Highest terrain under the prop's footprint (centre + four corners) at or below
        /// <paramref name="ceilingY"/>, cached per queueing; the fixed floor when the
        /// level has no terrain data; NoSurfaceY when the terrain has a hole there.
        /// </summary>
        private float GroundHeightOf(int i, float ceilingY)
        {
            if (terrain == null || !terrain.HasTerrainData()) return floorY;
            if (groundValid[i]) return groundHeight[i];
            float minX = rowMinX[i];
            float maxX = rowMaxX[i];
            float minZ = rowMinZ[i];
            float maxZ = rowMaxZ[i];
            float best = PropFallMath.NoSurfaceY;
            for (int p = 0; p < GroundProbeCount; p++)
            {
                float x = (minX + maxX) * 0.5f;
                float z = (minZ + maxZ) * 0.5f;
                if (p == 1 || p == 3) x = minX;
                if (p == 2 || p == 4) x = maxX;
                if (p == 1 || p == 2) z = minZ;
                if (p == 3 || p == 4) z = maxZ;
                float h = terrain.SampleSupportHeight(x, z, ceilingY);
                if (h > best) best = h;
            }
            groundHeight[i] = best;
            groundValid[i] = true;
            return best;
        }
    }
}
