using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// Which attached props a ball draws: the original's "bury" rule first, then the
    /// "biggest N + most recent N" display cap over what is left. Pure functions over
    /// caller-owned arrays; nothing here touches a Transform, a Renderer or the ledger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bury (We Love Katamari REROLL+, <c>Game_Mono.sRegCore</c> RVA 0xC572A0 and
    /// <c>Game_Core.sPostProcess</c> RVA 0x98E420): an attached prop is deleted from the
    /// display once it lies wholly inside the bury sphere. Its radius is
    /// <c>base = Rs + 0.38 (R - Rs)</c> (the per-mission display-core coefficient, 0.38 in
    /// most missions), deepened linearly toward R once more than 150 props are attached
    /// (<c>k = clamp((n - 150) / 150, 0, 1)</c>, <c>T = base + k (R - base)</c>). The first
    /// REROLL uses the same shape with 0.38 r, 100 / 90 and 0.75 (native
    /// <c>FUN_180012cf0</c>); it is the comparison case in the tests, not a switch. A buried
    /// prop never comes back in the same round, so the cursor below only moves forward
    /// and a bank shrink buries nothing new and restores nothing.
    /// </para>
    /// <para>
    /// "Wholly inside" uses the prop's farthest box corner from the ball centre in the
    /// ball's local frame (<see cref="RadialProxyShellMath.TryBuildAabbCorners"/>), which is
    /// fixed once the prop is attached. Both sides are compared in whole millimetres, so
    /// two clients that agree on the confirmed numbers agree on the result.
    /// </para>
    /// <para>
    /// Display cap (owner 2026-11-23): B = the <c>bigCap</c> largest live props by volume
    /// key (ties: the later one first); R = the <c>recentCap</c> most recent live props
    /// that are not in B. A prop that is both big and recent is counted once and the
    /// recent group simply reaches one further back. Every change is incremental: one
    /// attach changes at most one shown and one hidden prop, reported through
    /// <c>shownRow</c> / <c>hiddenRow</c> so the host touches only those renderers. A bury
    /// that hits a shown prop is repaired by one <see cref="TryRebuild"/> per frame.
    /// </para>
    /// <para>
    /// Rows are the host's prop indices. Per-row columns: <c>rowSeq</c> (attach order, -1 =
    /// not attached), <c>rowKey</c> (volume key), <c>rowBuried</c>, <c>rowFarMm</c>.
    /// <c>histRows</c> lists attached rows in attach order (index = sequence).
    /// </para>
    /// </remarks>
    public static class AttachedDisplayMath
    {
        public const int DefaultBiggestCount = 10;
        public const int DefaultRecentCount = 10;
        /// <summary>WLK <c>gf32CoreDispScaleCoef</c> default entry (and the first game's 0.38 r).</summary>
        public const float DisplayCoreCoefficient = 0.38f;
        /// <summary>WLK: deepening starts above this many attached props.</summary>
        public const int DeepenStartCount = 150;
        /// <summary>WLK: deepening reaches the full radius this many props later.</summary>
        public const int DeepenSpanCount = 150;

        /// <summary>Clears the per-row columns for a new round.</summary>
        public static void ResetRows(int[] rowSeq, bool[] rowBuried, int count)
        {
            if (rowSeq == null || rowBuried == null) return;
            for (int i = 0; i < count && i < rowSeq.Length && i < rowBuried.Length; i++)
            {
                rowSeq[i] = -1;
                rowBuried[i] = false;
            }
        }

        /// <summary>Whole millimetres, floored; false for a negative or non-finite length.</summary>
        public static bool TryFloorMm(float metres, out int millimetres)
        {
            millimetres = 0;
            if (!KatamariMath.IsFiniteFloat(metres) || metres < 0f) return false;
            float scaled = metres * 1000f;
            if (scaled >= 2000000000f) return false;
            millimetres = Mathf.FloorToInt(scaled);
            return true;
        }

        /// <summary>The prop's own volume in whole cubic millimetres (the "biggest" key).</summary>
        public static long VolumeKey(Vector3 ownSize)
        {
            if (!KatamariMath.IsFiniteVector(ownSize)) return 0L;
            if (ownSize.x <= 0f || ownSize.y <= 0f || ownSize.z <= 0f) return 0L;
            long x = (long)(ownSize.x * 1000f);
            long y = (long)(ownSize.y * 1000f);
            long z = (long)(ownSize.z * 1000f);
            return x * y * z;
        }

        /// <summary>
        /// The bury radius in whole millimetres for the current collision radius
        /// <paramref name="radiusM"/>, the round's start radius and the attached count.
        /// </summary>
        public static bool TryBuryRadiusMm(float radiusM, float startRadiusM, int attachedCount,
            out int buryRadiusMm)
        {
            buryRadiusMm = 0;
            if (!KatamariMath.IsFiniteFloat(radiusM) || radiusM <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(startRadiusM) || startRadiusM <= 0f) return false;
            if (attachedCount < 0) return false;
            float start = startRadiusM < radiusM ? startRadiusM : radiusM;
            float baseRadius = start + DisplayCoreCoefficient * (radiusM - start);
            float k = 0f;
            if (attachedCount > DeepenStartCount)
            {
                k = (float)(attachedCount - DeepenStartCount) / DeepenSpanCount;
                if (k > 1f) k = 1f;
            }
            float bury = baseRadius + k * (radiusM - baseRadius);
            return TryFloorMm(bury, out buryRadiusMm);
        }

        // ------------------------------------------------------------------
        // Display cap
        // ------------------------------------------------------------------

        /// <summary>True when row a ranks before row b in the biggest group.</summary>
        public static bool BiggerFirst(int a, int b, long[] rowKey, int[] rowSeq)
        {
            if (rowKey[a] != rowKey[b]) return rowKey[a] > rowKey[b];
            return rowSeq[a] > rowSeq[b];
        }

        /// <summary>
        /// Appends <paramref name="row"/> as the newest attached prop. A buried prop is
        /// recorded but never shown. On return <paramref name="shownRow"/> is the row that
        /// became visible (or -1) and <paramref name="hiddenRow"/> the row that stopped being
        /// visible (or -1). False (and nothing changed) for a bad row, an already attached
        /// row or a full history.
        /// </summary>
        public static bool TryAttach(int row, bool buried, long[] rowKey, int[] rowSeq,
            bool[] rowBuried, int[] histRows, ref int histCount, int[] bigRows, ref int bigCount,
            int bigCap, int[] recentRows, ref int recentCount, int recentCap,
            out int shownRow, out int hiddenRow)
        {
            shownRow = -1;
            hiddenRow = -1;
            if (!RowsValid(rowKey, rowSeq, rowBuried, histRows, bigRows, recentRows)) return false;
            if (row < 0 || row >= rowSeq.Length || row >= rowKey.Length || row >= rowBuried.Length) return false;
            if (rowSeq[row] >= 0) return false;
            if (histCount < 0 || histCount >= histRows.Length) return false;
            if (bigCap < 0 || bigCap > bigRows.Length || recentCap < 0 || recentCap > recentRows.Length) return false;

            rowSeq[row] = histCount;
            rowBuried[row] = buried;
            histRows[histCount] = row;
            histCount = histCount + 1;
            if (buried) return true;

            if (bigCap > 0 && (bigCount < bigCap || BiggerFirst(row, bigRows[bigCount - 1], rowKey, rowSeq)))
            {
                int dropped = InsertBig(row, rowKey, rowSeq, bigRows, ref bigCount, bigCap);
                shownRow = row;
                if (dropped >= 0)
                {
                    // The prop pushed out of B competes for R; it is older than everything
                    // R holds only if R has no room for it.
                    hiddenRow = InsertRecent(dropped, rowSeq, recentRows, ref recentCount, recentCap);
                }
                return true;
            }

            // Not big enough: it is the newest prop, so it heads R.
            int pushed = InsertRecent(row, rowSeq, recentRows, ref recentCount, recentCap);
            if (pushed == row) return true; // recentCap == 0: never shown, nothing changed.
            shownRow = row;
            hiddenRow = pushed;
            return true;
        }

        /// <summary>
        /// Marks an attached row buried. Returns true when it was shown, i.e. when the
        /// caller must rebuild the groups once (<see cref="TryRebuild"/>) after it has marked
        /// every row this frame buries. Marking never repairs the groups by itself, so a
        /// frame that buries many rows still rebuilds at most once.
        /// </summary>
        public static bool MarkBuried(int row, int[] rowSeq, bool[] rowBuried, int[] bigRows,
            int bigCount, int[] recentRows, int recentCount)
        {
            if (rowSeq == null || rowBuried == null) return false;
            if (row < 0 || row >= rowSeq.Length || row >= rowBuried.Length) return false;
            if (rowSeq[row] < 0 || rowBuried[row]) return false;
            rowBuried[row] = true;
            return IsShown(row, bigRows, bigCount, recentRows, recentCount);
        }

        /// <summary>
        /// Recomputes B and R from the attach history by their definition. O(histCount x
        /// (bigCap + recentCap)): one pass forward with a bounded sorted insert for B, one
        /// pass backward for R. Used after a bury that hit a shown row, and by the tests as
        /// the oracle the incremental attach must match.
        /// </summary>
        public static bool TryRebuild(long[] rowKey, int[] rowSeq, bool[] rowBuried,
            int[] histRows, int histCount, int[] bigRows, ref int bigCount, int bigCap,
            int[] recentRows, ref int recentCount, int recentCap)
        {
            if (!RowsValid(rowKey, rowSeq, rowBuried, histRows, bigRows, recentRows)) return false;
            if (histCount < 0 || histCount > histRows.Length) return false;
            if (bigCap < 0 || bigCap > bigRows.Length || recentCap < 0 || recentCap > recentRows.Length) return false;
            bigCount = 0;
            recentCount = 0;
            if (bigCap > 0)
            {
                for (int i = 0; i < histCount; i++)
                {
                    int row = histRows[i];
                    if (rowBuried[row]) continue;
                    if (bigCount < bigCap || BiggerFirst(row, bigRows[bigCount - 1], rowKey, rowSeq))
                        InsertBig(row, rowKey, rowSeq, bigRows, ref bigCount, bigCap);
                }
            }
            for (int i = histCount - 1; i >= 0 && recentCount < recentCap; i--)
            {
                int row = histRows[i];
                if (rowBuried[row]) continue;
                if (IndexOf(bigRows, bigCount, row) >= 0) continue;
                recentRows[recentCount] = row;
                recentCount = recentCount + 1;
            }
            return true;
        }

        /// <summary>True when the row is in B or R.</summary>
        public static bool IsShown(int row, int[] bigRows, int bigCount, int[] recentRows, int recentCount)
        {
            return IndexOf(bigRows, bigCount, row) >= 0 || IndexOf(recentRows, recentCount, row) >= 0;
        }

        // ------------------------------------------------------------------
        // Bury cursor: live rows sorted by farthest corner, ascending
        // ------------------------------------------------------------------

        /// <summary>
        /// Inserts a live row by its farthest corner. Only the part at or after
        /// <paramref name="cursor"/> is kept sorted: the part before it is already buried.
        /// False when the table is full.
        /// </summary>
        public static bool TryInsertByFar(int row, int[] rowFarMm, int[] sortedRows,
            ref int sortedCount, int cursor)
        {
            if (rowFarMm == null || sortedRows == null) return false;
            if (row < 0 || row >= rowFarMm.Length) return false;
            if (sortedCount >= sortedRows.Length) return false;
            if (cursor < 0 || cursor > sortedCount) return false;
            int at = sortedCount;
            while (at > cursor && rowFarMm[sortedRows[at - 1]] > rowFarMm[row])
            {
                sortedRows[at] = sortedRows[at - 1];
                at = at - 1;
            }
            sortedRows[at] = row;
            sortedCount = sortedCount + 1;
            return true;
        }

        /// <summary>
        /// The next row to bury (farthest corner below the bury radius), advancing the
        /// cursor past it; -1 when none. The caller buries it and calls again.
        /// </summary>
        public static int NextToBury(int[] rowFarMm, int[] sortedRows, int sortedCount,
            ref int cursor, int buryRadiusMm)
        {
            if (rowFarMm == null || sortedRows == null) return -1;
            if (cursor < 0 || cursor >= sortedCount) return -1;
            int row = sortedRows[cursor];
            if (rowFarMm[row] >= buryRadiusMm) return -1;
            cursor = cursor + 1;
            return row;
        }

        // ------------------------------------------------------------------

        private static bool RowsValid(long[] rowKey, int[] rowSeq, bool[] rowBuried,
            int[] histRows, int[] bigRows, int[] recentRows)
        {
            return rowKey != null && rowSeq != null && rowBuried != null && histRows != null
                && bigRows != null && recentRows != null;
        }

        // Sorted insert into B; returns the row that fell off the end, or -1.
        private static int InsertBig(int row, long[] rowKey, int[] rowSeq, int[] bigRows,
            ref int bigCount, int bigCap)
        {
            int dropped = -1;
            int count = bigCount;
            if (count == bigCap)
            {
                dropped = bigRows[count - 1];
                count = count - 1;
            }
            int at = count;
            while (at > 0 && BiggerFirst(row, bigRows[at - 1], rowKey, rowSeq))
            {
                bigRows[at] = bigRows[at - 1];
                at = at - 1;
            }
            bigRows[at] = row;
            bigCount = count + 1;
            return dropped;
        }

        // Sorted (newest first) insert into R. Returns the row that ends up not shown: the
        // one pushed off the end, the row itself when it does not fit, or -1.
        private static int InsertRecent(int row, int[] rowSeq, int[] recentRows,
            ref int recentCount, int recentCap)
        {
            if (recentCap <= 0) return row;
            int count = recentCount;
            int dropped = -1;
            if (count == recentCap)
            {
                if (rowSeq[row] < rowSeq[recentRows[count - 1]]) return row;
                dropped = recentRows[count - 1];
                count = count - 1;
            }
            int at = count;
            while (at > 0 && rowSeq[row] > rowSeq[recentRows[at - 1]])
            {
                recentRows[at] = recentRows[at - 1];
                at = at - 1;
            }
            recentRows[at] = row;
            recentCount = count + 1;
            return dropped;
        }

        private static int IndexOf(int[] rows, int count, int row)
        {
            if (rows == null) return -1;
            for (int i = 0; i < count; i++)
            {
                if (rows[i] == row) return i;
            }
            return -1;
        }

    }
}
