using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Pickup
{
    /// <summary>
    /// Pure, allocation-free rules of the stack-fall behaviour: the single-parent column,
    /// the child table, the per-tick re-check queue step, the vertical fall integral and the
    /// support geometry. <see cref="PropFallHost"/> owns the state and the Transforms; every
    /// decision lives here so it can be tested without a scene.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source of truth: Docs/Design/PickupAbsorb/PROP_BEHAVIOURS.md section 10.9 (single
    /// parent pointer, per-tick floating re-check, children fall; no carry), 5.3 (pure
    /// gravity onto the highest surface below, no tilt) and the executable spec
    /// Docs/Design/PickupAbsorb/prop_behaviours/floating_requeue.py. The pointer is only
    /// used to decide WHO is re-checked; whether a prop falls is decided by geometry
    /// alone, so a wrong pointer costs one re-check and can never make a supported prop
    /// fall.
    /// </para>
    /// <para>
    /// Time order (what the tests pin): a re-check tick first judges the props that were
    /// queued BEFORE the tick, then moves everything that is falling. A prop that starts to
    /// fall queues its own children for the NEXT tick, so a stack falls layer by layer
    /// (1 eaten, 2 judged and falls, 3 is judged next tick and sees 2 no longer holds it).
    /// </para>
    /// <para>
    /// Units: metres, seconds, +Y up, world space. A prop's box is its world AABB: bottom,
    /// height and the XZ extents; the fall only changes <c>bottom</c>.
    /// </para>
    /// </remarks>
    public static class PropFallMath
    {
        /// <summary>Standing where it was authored, or never involved.</summary>
        public const int StateResting = 0;
        /// <summary>In free fall: eatable, but neither a wall, a floor nor a support.</summary>
        public const int StateFalling = 1;
        /// <summary>Landed. A normal prop again (a support, a wall, eatable).</summary>
        public const int StateSettled = 2;
        /// <summary>Eaten by the ball (attached or hidden by the pickup host).</summary>
        public const int StateAbsorbed = 3;

        /// <summary>
        /// Same number as the ball motor's <c>gravityMps2</c> default; props do not get a
        /// second gravity knob (PROP_BEHAVIOURS section 1).
        /// </summary>
        public const float GravityMps2 = 9.81f;

        /// <summary>Fixed fall step, 30 Hz (PROP_BEHAVIOURS section 5.3).</summary>
        public const float StepSeconds = 1f / 30f;

        /// <summary>A bottom within this of a surface lands on it (2 mm, section 10.9).</summary>
        public const float LandingEpsilonM = 0.002f;

        /// <summary>A support may be embedded this fraction of the smaller height.</summary>
        public const float EmbedFraction = 0.30f;

        /// <summary>A support may be this fraction of the smaller height below the prop's foot.</summary>
        public const float HoverFraction = 0.50f;

        /// <summary>Touching footprints count (an overlap of exactly 0).</summary>
        public const float FootprintTouchM = 1e-6f;

        /// <summary>Returned for "no surface here"; far below any playable height.</summary>
        public const float NoSurfaceY = -1.0e9f;

        /// <summary>Never run more catch-up steps than this in one frame.</summary>
        public const int MaxStepsPerFrame = 4;

        /// <summary>A prop whose footprint covers more grid cells than this is not indexed.</summary>
        public const int MaxCellsPerProp = 256;

        /// <summary>Grid index slots in <see cref="ComputeGrid"/>'s output.</summary>
        public const int GridOriginX = 0;
        public const int GridOriginZ = 1;
        public const int GridCellSize = 2;
        public const int GridColumns = 3;
        public const int GridRows = 4;
        public const int GridInfoLength = 5;

        // ------------------------------------------------------------------ parents ---

        /// <summary>
        /// Copies the serialized parent column into a runtime-safe one: a missing column or
        /// short row, an index out of range and a self edge become -1, and every cycle is cut
        /// at its lowest-indexed member. Deterministic, so the same data gives the same table.
        /// </summary>
        /// <returns>How many entries were repaired (0 for clean data).</returns>
        public static int SanitizeParents(int[] raw, int count, int[] parents)
        {
            if (parents == null || count <= 0) return 0;
            int repaired = 0;
            for (int i = 0; i < count; i++)
            {
                int value = -1;
                if (raw != null && i < raw.Length) value = raw[i];
                if (value < -1 || value >= count || value == i)
                {
                    if (value != -1) repaired = repaired + 1;
                    value = -1;
                }
                parents[i] = value;
            }

            for (int i = 0; i < count; i++)
            {
                int current = parents[i];
                int steps = 0;
                while (current >= 0 && current != i && steps <= count)
                {
                    current = parents[current];
                    steps = steps + 1;
                }
                if (current == i)
                {
                    // i's chain returns to i: cut here. Lower indices were processed first,
                    // so a cycle is cut once, at its lowest member.
                    parents[i] = -1;
                    repaired = repaired + 1;
                }
            }
            return repaired;
        }

        /// <summary>
        /// Builds the CSR child table of a sanitized parent column: the children of
        /// <c>p</c> are <c>childList[childStart[p] .. childStart[p + 1])</c>, in row order
        /// (stable, so one data set always gives one table).
        /// </summary>
        /// <param name="childStart">Length at least count + 1.</param>
        /// <param name="childList">Length at least count.</param>
        /// <returns>The number of child entries written.</returns>
        public static int BuildChildTable(int[] parents, int count, int[] childStart, int[] childList)
        {
            if (parents == null || childStart == null || childList == null || count <= 0) return 0;
            for (int i = 0; i <= count; i++) childStart[i] = 0;
            for (int i = 0; i < count; i++)
            {
                int parent = parents[i];
                if (parent >= 0 && parent < count) childStart[parent + 1] = childStart[parent + 1] + 1;
            }
            for (int i = 0; i < count; i++) childStart[i + 1] = childStart[i + 1] + childStart[i];

            // Second pass: place each child in the first free slot of its parent's slice
            // (-1 marks free). A slice is at most one sibling group, so the scan is short.
            int total = childStart[count];
            for (int i = 0; i < total; i++) childList[i] = -1;
            for (int i = 0; i < count; i++)
            {
                int parent = parents[i];
                if (parent < 0 || parent >= count) continue;
                int slot = childStart[parent];
                while (childList[slot] != -1) slot = slot + 1;
                childList[slot] = i;
            }
            return total;
        }

        // ------------------------------------------------------------------- queue ---

        /// <summary>
        /// Queues one prop for re-check (next tick) unless it is eaten, already falling or
        /// already queued.
        /// </summary>
        /// <returns>The new queue length.</returns>
        public static int Enqueue(int index, int[] state, bool[] pendingFlag, int[] queue,
            int queueCount)
        {
            if (state == null || pendingFlag == null || queue == null) return queueCount;
            if (index < 0 || index >= state.Length || queueCount >= queue.Length) return queueCount;
            int current = state[index];
            if (current == StateAbsorbed || current == StateFalling) return queueCount;
            if (pendingFlag[index]) return queueCount;
            pendingFlag[index] = true;
            queue[queueCount] = index;
            return queueCount + 1;
        }

        /// <summary>
        /// Queues every direct child of <paramref name="parent"/> (the eaten or newly falling
        /// prop): one slice of the CSR table, no scan.
        /// </summary>
        /// <returns>The new queue length.</returns>
        public static int EnqueueChildren(int parent, int[] childStart, int[] childList,
            int[] state, bool[] pendingFlag, int[] queue, int queueCount)
        {
            if (childStart == null || childList == null || parent < 0) return queueCount;
            if (parent + 1 >= childStart.Length) return queueCount;
            int end = childStart[parent + 1];
            for (int k = childStart[parent]; k < end; k++)
            {
                queueCount = Enqueue(childList[k], state, pendingFlag, queue, queueCount);
            }
            return queueCount;
        }

        /// <summary>
        /// Applies one prop's re-check verdict. A supported prop stays queued (written to
        /// <paramref name="nextQueue"/>) until it has been re-checked
        /// <paramref name="maxRechecks"/> times; an unsupported one starts to fall and
        /// queues its own children for the next tick.
        /// </summary>
        /// <param name="rechecks">Per-prop count of re-checks already spent.</param>
        /// <returns>The new length of <paramref name="nextQueue"/>.</returns>
        public static int ApplyRecheck(int index, bool supported, int maxRechecks,
            int[] state, float[] velocityY, bool[] pendingFlag, int[] rechecks,
            int[] childStart, int[] childList, int[] nextQueue, int nextCount)
        {
            if (index < 0 || index >= state.Length) return nextCount;
            if (state[index] == StateAbsorbed || state[index] == StateFalling)
            {
                pendingFlag[index] = false;
                return nextCount;
            }
            if (supported)
            {
                rechecks[index] = rechecks[index] + 1;
                if (rechecks[index] >= maxRechecks || nextCount >= nextQueue.Length)
                {
                    pendingFlag[index] = false;
                    return nextCount;
                }
                nextQueue[nextCount] = index;
                return nextCount + 1;
            }

            pendingFlag[index] = false;
            state[index] = StateFalling;
            velocityY[index] = 0f;
            return EnqueueChildren(index, childStart, childList, state, pendingFlag, nextQueue,
                nextCount);
        }

        // -------------------------------------------------------------------- fall ---

        /// <summary>
        /// One fixed step of the pure-gravity fall: <c>v -= g dt; bottom += v dt</c>, then
        /// land if the new bottom is at or below <paramref name="landingTopY"/> + 2 mm.
        /// </summary>
        /// <param name="landingTopY">Highest surface the prop can reach this step, or
        /// <see cref="NoSurfaceY"/> for none.</param>
        /// <returns>True when the prop landed (bottom snapped to the surface, velocity 0).</returns>
        public static bool StepFall(float bottom, float velocityY, float landingTopY,
            out float newBottom, out float newVelocityY)
        {
            float v = velocityY - GravityMps2 * StepSeconds;
            float b = bottom + v * StepSeconds;
            if (b <= landingTopY + LandingEpsilonM)
            {
                newBottom = landingTopY;
                newVelocityY = 0f;
                return true;
            }
            newBottom = b;
            newVelocityY = v;
            return false;
        }

        /// <summary>
        /// A surface can catch a falling prop only if its top was at or below the prop's
        /// foot BEFORE the step (+ 2 mm). Comparing with the post-step foot would let a
        /// fast prop tunnel through a thin board.
        /// </summary>
        public static bool IsLandingSurface(float surfaceTopY, float previousBottom)
        {
            return surfaceTopY <= previousBottom + LandingEpsilonM;
        }

        // ---------------------------------------------------------------- geometry ---

        /// <summary>XZ footprints touch or overlap (touching counts).</summary>
        public static bool FootprintsTouch(float aMinX, float aMaxX, float aMinZ, float aMaxZ,
            float bMinX, float bMaxX, float bMinZ, float bMaxZ)
        {
            float dx = Mathf.Min(aMaxX, bMaxX) - Mathf.Max(aMinX, bMinX);
            float dz = Mathf.Min(aMaxZ, bMaxZ) - Mathf.Max(aMinZ, bMinZ);
            return dx >= -FootprintTouchM && dz >= -FootprintTouchM;
        }

        /// <summary>
        /// The "rests on" window shared with the validator (<c>placement_core.contact</c>):
        /// gap = childBottom - supporterTop within [-0.30, +0.50] times the smaller of the
        /// two heights, and the supporter's top strictly below the child's top.
        /// </summary>
        public static bool IsSeatedOn(float childBottom, float childHeight, float supporterTop,
            float supporterHeight)
        {
            float smallest = Mathf.Min(childHeight, supporterHeight);
            float gap = childBottom - supporterTop;
            if (gap < -EmbedFraction * smallest || gap > HoverFraction * smallest) return false;
            return supporterTop < childBottom + childHeight;
        }

        /// <summary>
        /// Is this prop's foot on terrain (or the fallback floor)? Same window as props,
        /// measured against the highest surface under its footprint.
        /// </summary>
        public static bool IsSeatedOnSurface(float childBottom, float childHeight, float surfaceY)
        {
            if (surfaceY <= NoSurfaceY * 0.5f) return false;
            float gap = childBottom - surfaceY;
            return gap >= -EmbedFraction * childHeight && gap <= HoverFraction * childHeight;
        }

        /// <summary>
        /// Is any candidate a support for this prop right now? Only resting and settled
        /// props hold anything up; a falling or eaten prop never does.
        /// </summary>
        public static bool IsSeatedOnAny(int self, int[] candidates, int candidateCount,
            int[] state, float[] minX, float[] maxX, float[] minZ, float[] maxZ,
            float[] bottom, float[] height,
            float childMinX, float childMaxX, float childMinZ, float childMaxZ,
            float childBottom, float childHeight)
        {
            for (int k = 0; k < candidateCount; k++)
            {
                int j = candidates[k];
                if (j == self) continue;
                int s = state[j];
                if (s != StateResting && s != StateSettled) continue;
                if (!FootprintsTouch(childMinX, childMaxX, childMinZ, childMaxZ,
                    minX[j], maxX[j], minZ[j], maxZ[j])) continue;
                if (IsSeatedOn(childBottom, childHeight, bottom[j] + height[j], height[j]))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The highest resting or settled top below the prop's PREVIOUS foot that its
        /// footprint touches, or <see cref="NoSurfaceY"/> when none.
        /// </summary>
        public static float HighestLandingTop(int self, int[] candidates, int candidateCount,
            int[] state, float[] minX, float[] maxX, float[] minZ, float[] maxZ,
            float[] bottom, float[] height,
            float childMinX, float childMaxX, float childMinZ, float childMaxZ,
            float previousBottom)
        {
            float best = NoSurfaceY;
            for (int k = 0; k < candidateCount; k++)
            {
                int j = candidates[k];
                if (j == self) continue;
                int s = state[j];
                if (s != StateResting && s != StateSettled) continue;
                if (!FootprintsTouch(childMinX, childMaxX, childMinZ, childMaxZ,
                    minX[j], maxX[j], minZ[j], maxZ[j])) continue;
                float top = bottom[j] + height[j];
                if (!IsLandingSurface(top, previousBottom)) continue;
                if (top > best) best = top;
            }
            return best;
        }

        // -------------------------------------------------------------------- grid ---

        /// <summary>
        /// Chooses a uniform XZ grid over every finite footprint. The cell is
        /// <paramref name="requestedCellM"/> but is enlarged until the grid has at most
        /// 65 536 cells. Writes <see cref="GridInfoLength"/> numbers into
        /// <paramref name="info"/> (origin x/z, cell, columns, rows).
        /// </summary>
        public static bool ComputeGrid(float[] minX, float[] maxX, float[] minZ, float[] maxZ,
            int count, float requestedCellM, float[] info)
        {
            if (minX == null || maxX == null || minZ == null || maxZ == null || info == null) return false;
            if (info.Length < GridInfoLength || count <= 0 || !(requestedCellM > 0f)) return false;
            float lowX = float.MaxValue;
            float lowZ = float.MaxValue;
            float highX = -float.MaxValue;
            float highZ = -float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (!KatamariMath.IsFiniteFloat(minX[i]) || !KatamariMath.IsFiniteFloat(maxX[i]) ||
                    !KatamariMath.IsFiniteFloat(minZ[i]) || !KatamariMath.IsFiniteFloat(maxZ[i])) continue;
                if (minX[i] < lowX) lowX = minX[i];
                if (minZ[i] < lowZ) lowZ = minZ[i];
                if (maxX[i] > highX) highX = maxX[i];
                if (maxZ[i] > highZ) highZ = maxZ[i];
            }
            if (lowX > highX || lowZ > highZ) return false;

            float cell = requestedCellM;
            float width = highX - lowX;
            float depth = highZ - lowZ;
            int columns = (int)(width / cell) + 1;
            int rows = (int)(depth / cell) + 1;
            while (columns * rows > 65536)
            {
                cell = cell * 2f;
                columns = (int)(width / cell) + 1;
                rows = (int)(depth / cell) + 1;
            }
            info[GridOriginX] = lowX;
            info[GridOriginZ] = lowZ;
            info[GridCellSize] = cell;
            info[GridColumns] = columns;
            info[GridRows] = rows;
            return true;
        }

        /// <summary>
        /// Cell range of one footprint, clamped to the grid. False when the footprint is
        /// not finite or covers more than <see cref="MaxCellsPerProp"/> cells (such a prop
        /// is not indexed: it is a building, not a prop that holds a stack).
        /// </summary>
        public static bool TryCellRange(float[] info, float minX, float maxX, float minZ, float maxZ,
            out int firstColumn, out int lastColumn, out int firstRow, out int lastRow)
        {
            firstColumn = 0;
            lastColumn = -1;
            firstRow = 0;
            lastRow = -1;
            if (info == null || info.Length < GridInfoLength) return false;
            if (!KatamariMath.IsFiniteFloat(minX) || !KatamariMath.IsFiniteFloat(maxX) ||
                !KatamariMath.IsFiniteFloat(minZ) || !KatamariMath.IsFiniteFloat(maxZ)) return false;
            float cell = info[GridCellSize];
            int columns = (int)info[GridColumns];
            int rows = (int)info[GridRows];
            firstColumn = ClampCell((int)Mathf.Floor((minX - info[GridOriginX]) / cell), columns);
            lastColumn = ClampCell((int)Mathf.Floor((maxX - info[GridOriginX]) / cell), columns);
            firstRow = ClampCell((int)Mathf.Floor((minZ - info[GridOriginZ]) / cell), rows);
            lastRow = ClampCell((int)Mathf.Floor((maxZ - info[GridOriginZ]) / cell), rows);
            return (lastColumn - firstColumn + 1) * (lastRow - firstRow + 1) <= MaxCellsPerProp;
        }

        private static int ClampCell(int value, int size)
        {
            if (value < 0) return 0;
            if (value >= size) return size - 1;
            return value;
        }

        /// <summary>First pass: how many (cell, prop) entries the grid needs.</summary>
        public static int CountGridEntries(float[] info, float[] minX, float[] maxX, float[] minZ,
            float[] maxZ, int count)
        {
            int total = 0;
            for (int i = 0; i < count; i++)
            {
                int c0;
                int c1;
                int r0;
                int r1;
                if (!TryCellRange(info, minX[i], maxX[i], minZ[i], maxZ[i], out c0, out c1, out r0,
                    out r1)) continue;
                total = total + (c1 - c0 + 1) * (r1 - r0 + 1);
            }
            return total;
        }

        /// <summary>
        /// Second pass: fills the CSR cell table. <paramref name="cellStart"/> needs
        /// columns * rows + 1 entries, <paramref name="cellItems"/> the first pass's count.
        /// Items inside a cell are in row order.
        /// </summary>
        public static void FillGrid(float[] info, float[] minX, float[] maxX, float[] minZ,
            float[] maxZ, int count, int[] cellStart, int[] cellItems)
        {
            int cells = (int)info[GridColumns] * (int)info[GridRows];
            int columns = (int)info[GridColumns];
            for (int c = 0; c <= cells; c++) cellStart[c] = 0;
            for (int i = 0; i < count; i++)
            {
                int c0;
                int c1;
                int r0;
                int r1;
                if (!TryCellRange(info, minX[i], maxX[i], minZ[i], maxZ[i], out c0, out c1, out r0,
                    out r1)) continue;
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++)
                        cellStart[r * columns + c + 1] = cellStart[r * columns + c + 1] + 1;
            }
            for (int c = 0; c < cells; c++) cellStart[c + 1] = cellStart[c + 1] + cellStart[c];
            int[] cursor = new int[cells];
            for (int c = 0; c < cells; c++) cursor[c] = cellStart[c];
            for (int i = 0; i < count; i++)
            {
                int c0;
                int c1;
                int r0;
                int r1;
                if (!TryCellRange(info, minX[i], maxX[i], minZ[i], maxZ[i], out c0, out c1, out r0,
                    out r1)) continue;
                for (int r = r0; r <= r1; r++)
                {
                    for (int c = c0; c <= c1; c++)
                    {
                        int cell = r * columns + c;
                        cellItems[cursor[cell]] = i;
                        cursor[cell] = cursor[cell] + 1;
                    }
                }
            }
        }

        /// <summary>
        /// Collects the distinct props indexed in the cells a footprint covers into
        /// <paramref name="output"/> (capacity-bounded). <paramref name="stamp"/> is a
        /// per-prop scratch array and <paramref name="stampValue"/> a value not yet used in
        /// it, so no clearing is needed between calls.
        /// </summary>
        /// <returns>How many candidates were written.</returns>
        public static int CollectCandidates(float[] info, int[] cellStart, int[] cellItems,
            int[] stamp, int stampValue, float minX, float maxX, float minZ, float maxZ,
            int[] output)
        {
            int c0;
            int c1;
            int r0;
            int r1;
            if (!TryCellRange(info, minX, maxX, minZ, maxZ, out c0, out c1, out r0, out r1)) return 0;
            int columns = (int)info[GridColumns];
            int written = 0;
            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    int cell = r * columns + c;
                    for (int k = cellStart[cell]; k < cellStart[cell + 1]; k++)
                    {
                        int item = cellItems[k];
                        if (stamp[item] == stampValue) continue;
                        stamp[item] = stampValue;
                        if (written >= output.Length) return written;
                        output[written] = item;
                        written = written + 1;
                    }
                }
            }
            return written;
        }
    }
}
