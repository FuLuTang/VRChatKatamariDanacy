using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Pickup
{
    /// <summary>
    /// 静止道具的 XZ 均匀网格：把"整场 N 件逐件量距离"换成"只看球附近的格子"。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 纯数学、无状态、不分配：调用方一次性分配好 <c>info</c> / <c>cellStart</c> /
    /// <c>cellRows</c> / <c>cellMin</c> / <c>cellMax</c>，这里只填写与查询。结构是
    /// CSR（compressed sparse row）：格子 c 的成员是 <c>cellRows[cellStart[c] .. cellStart[c+1])</c>，
    /// 成员按行下标升序。每行只进它中心所在的那一格（不按包围盒占多格），所以大件要由调用方
    /// 另外处理（宿主把半对角线超过阈值的静止件放进线性表）。
    /// </para>
    /// <para>
    /// 覆盖保证（有测试逐点对拍暴力解）：<see cref="Query"/> 返回的行是一个超集，包含所有中心到
    /// 查询点的三维距离平方 ≤ radius² 的成员；多出来的行由调用方用自己的精确判定淘汰。
    /// 两层剔除都是保守的：格子范围向外各多取一格（吸收 floor 的舍入），
    /// 格内成员的紧包围盒到点的距离平方大于 radius² 才整格跳过（浮点逐轴相减对输入单调，
    /// 所以盒距平方不会大于任何盒内点的距离平方）。
    /// </para>
    /// <para>
    /// 长度是米，世界坐标 +Y 向上；格子只在 XZ 上划分，Y 由紧包围盒的剔除处理。
    /// </para>
    /// </remarks>
    public static class PickupSpatialGrid
    {
        /// <summary>info[]：布局原点 X。</summary>
        public const int InfoOriginX = 0;
        /// <summary>info[]：布局原点 Z。</summary>
        public const int InfoOriginZ = 1;
        /// <summary>info[]：最终格边长（可能比请求的大，见 <see cref="TryComputeLayout"/>）。</summary>
        public const int InfoCellSize = 2;
        /// <summary>info[]：1 / 格边长。</summary>
        public const int InfoInverseCell = 3;
        /// <summary>info[]：X 方向格数。</summary>
        public const int InfoColumns = 4;
        /// <summary>info[]：Z 方向格数。</summary>
        public const int InfoRows = 5;
        /// <summary>info[]：成员总数。</summary>
        public const int InfoMemberCount = 6;
        /// <summary>info[] 所需长度。</summary>
        public const int InfoLength = 7;

        private const int MaxDoublings = 40;

        /// <summary>
        /// 由成员的位置算布局：外包矩形、格边长和格数。
        /// </summary>
        /// <remarks>
        /// 格数超过 <paramref name="maxCells"/> 时格边长翻倍直到不超（所以格边长只会大于等于请求值）。
        /// 成员是 <c>member[i]</c> 为真且 i &lt; count 的行；位置必须有限，非有限位置是调用方的错
        /// （返回 false）。没有成员、参数非法、info 太短都返回 false，且不写 info。
        /// </remarks>
        public static bool TryComputeLayout(Vector3[] positions, bool[] member, int count,
            float cellSizeM, int maxCells, float[] info)
        {
            if (positions == null || member == null || info == null) return false;
            if (count < 0 || count > positions.Length || count > member.Length) return false;
            if (info.Length < InfoLength) return false;
            if (!KatamariMath.IsFiniteFloat(cellSizeM) || cellSizeM <= 0f) return false;
            if (maxCells < 1) return false;

            int members = 0;
            float minX = 0f;
            float maxX = 0f;
            float minZ = 0f;
            float maxZ = 0f;
            for (int i = 0; i < count; i++)
            {
                if (!member[i]) continue;
                Vector3 p = positions[i];
                if (!KatamariMath.IsFiniteVector(p)) return false;
                if (members == 0)
                {
                    minX = p.x;
                    maxX = p.x;
                    minZ = p.z;
                    maxZ = p.z;
                }
                else
                {
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                }
                members = members + 1;
            }
            if (members == 0) return false;

            float spanX = maxX - minX;
            float spanZ = maxZ - minZ;
            if (!KatamariMath.IsFiniteFloat(spanX) || !KatamariMath.IsFiniteFloat(spanZ)) return false;

            float cell = cellSizeM;
            float columnsF = 1f;
            float rowsF = 1f;
            bool fits = false;
            for (int attempt = 0; attempt < MaxDoublings; attempt++)
            {
                float inverse = 1f / cell;
                columnsF = Mathf.Floor(spanX * inverse) + 1f;
                rowsF = Mathf.Floor(spanZ * inverse) + 1f;
                if (KatamariMath.IsFiniteFloat(columnsF) && KatamariMath.IsFiniteFloat(rowsF)
                    && columnsF * rowsF <= (float)maxCells)
                {
                    fits = true;
                    break;
                }
                cell = cell * 2f;
                if (!KatamariMath.IsFiniteFloat(cell)) return false;
            }
            if (!fits) return false;

            info[InfoOriginX] = minX;
            info[InfoOriginZ] = minZ;
            info[InfoCellSize] = cell;
            info[InfoInverseCell] = 1f / cell;
            info[InfoColumns] = columnsF;
            info[InfoRows] = rowsF;
            info[InfoMemberCount] = (float)members;
            return true;
        }

        /// <summary>布局里的格数（列 × 行）。</summary>
        public static int CellCount(float[] info)
        {
            return (int)info[InfoColumns] * (int)info[InfoRows];
        }

        /// <summary>一维坐标落在第几格，夹到 [0, cellCount-1]；对坐标单调。</summary>
        public static int CellCoordinate(float coordinate, float origin, float inverseCell,
            int cellCount)
        {
            float f = Mathf.Floor((coordinate - origin) * inverseCell);
            if (!(f > 0f)) return 0;
            if (f >= (float)cellCount) return cellCount - 1;
            return (int)f;
        }

        /// <summary>某个位置所在的格子下标（夹到布局内）。</summary>
        public static int CellOf(float[] info, float x, float z)
        {
            int columns = (int)info[InfoColumns];
            int rows = (int)info[InfoRows];
            int cx = CellCoordinate(x, info[InfoOriginX], info[InfoInverseCell], columns);
            int cz = CellCoordinate(z, info[InfoOriginZ], info[InfoInverseCell], rows);
            return cz * columns + cx;
        }

        /// <summary>
        /// 把成员装进 CSR：<paramref name="cellStart"/> 长度至少 cells+1，
        /// <paramref name="cellRows"/> 长度至少成员数。失败返回 false 且不写。
        /// </summary>
        public static bool BuildCells(float[] info, Vector3[] positions, bool[] member, int count,
            int[] cellStart, int[] cellRows)
        {
            if (info == null || positions == null || member == null) return false;
            if (cellStart == null || cellRows == null) return false;
            if (info.Length < InfoLength) return false;
            if (count < 0 || count > positions.Length || count > member.Length) return false;
            int cells = CellCount(info);
            int members = (int)info[InfoMemberCount];
            if (cells < 1 || cellStart.Length < cells + 1 || cellRows.Length < members) return false;

            for (int c = 0; c <= cells; c++) cellStart[c] = 0;
            int placed = 0;
            for (int i = 0; i < count; i++)
            {
                if (!member[i]) continue;
                int cell = CellOf(info, positions[i].x, positions[i].z);
                cellStart[cell + 1] = cellStart[cell + 1] + 1;
                placed = placed + 1;
            }
            if (placed != members) return false;
            for (int c = 0; c < cells; c++) cellStart[c + 1] = cellStart[c + 1] + cellStart[c];

            // Fill by advancing each cell's start, then shift the starts back one slot: no
            // scratch array. Rows land in ascending order inside a cell.
            for (int i = 0; i < count; i++)
            {
                if (!member[i]) continue;
                int cell = CellOf(info, positions[i].x, positions[i].z);
                cellRows[cellStart[cell]] = i;
                cellStart[cell] = cellStart[cell] + 1;
            }
            for (int c = cells; c > 0; c--) cellStart[c] = cellStart[c - 1];
            cellStart[0] = 0;
            return true;
        }

        /// <summary>每格成员中心的紧包围盒；空格写零（查询前先看空格）。</summary>
        public static bool BuildCellBounds(float[] info, Vector3[] positions, int[] cellStart,
            int[] cellRows, Vector3[] cellMin, Vector3[] cellMax)
        {
            if (info == null || positions == null || cellStart == null || cellRows == null) return false;
            if (cellMin == null || cellMax == null || info.Length < InfoLength) return false;
            int cells = CellCount(info);
            if (cells < 1 || cellMin.Length < cells || cellMax.Length < cells) return false;
            if (cellStart.Length < cells + 1) return false;

            for (int c = 0; c < cells; c++)
            {
                int start = cellStart[c];
                int end = cellStart[c + 1];
                if (start >= end)
                {
                    cellMin[c] = Vector3.zero;
                    cellMax[c] = Vector3.zero;
                    continue;
                }
                Vector3 low = positions[cellRows[start]];
                Vector3 high = low;
                for (int k = start + 1; k < end; k++)
                {
                    Vector3 p = positions[cellRows[k]];
                    if (p.x < low.x) low.x = p.x;
                    if (p.y < low.y) low.y = p.y;
                    if (p.z < low.z) low.z = p.z;
                    if (p.x > high.x) high.x = p.x;
                    if (p.y > high.y) high.y = p.y;
                    if (p.z > high.z) high.z = p.z;
                }
                cellMin[c] = low;
                cellMax[c] = high;
            }
            return true;
        }

        /// <summary>
        /// 取出可能在 <paramref name="radiusM"/> 内的成员行（超集，见类说明）；返回行数，
        /// 参数非法或输出缓冲小于成员数返回 -1（此时不写）。
        /// </summary>
        public static int Query(Vector3 point, float radiusM, float[] info, int[] cellStart,
            int[] cellRows, Vector3[] cellMin, Vector3[] cellMax, int[] outRows)
        {
            if (!KatamariMath.IsFiniteVector(point)) return -1;
            if (!KatamariMath.IsFiniteFloat(radiusM) || radiusM < 0f) return -1;
            if (info == null || cellStart == null || cellRows == null) return -1;
            if (cellMin == null || cellMax == null || outRows == null) return -1;
            if (info.Length < InfoLength) return -1;
            int columns = (int)info[InfoColumns];
            int rows = (int)info[InfoRows];
            int cells = columns * rows;
            int members = (int)info[InfoMemberCount];
            if (cells < 1 || cellStart.Length < cells + 1 || cellMin.Length < cells
                || cellMax.Length < cells || cellRows.Length < members
                || outRows.Length < members) return -1;

            float inverse = info[InfoInverseCell];
            float originX = info[InfoOriginX];
            float originZ = info[InfoOriginZ];
            int firstColumn = CellCoordinate(point.x - radiusM, originX, inverse, columns) - 1;
            int lastColumn = CellCoordinate(point.x + radiusM, originX, inverse, columns) + 1;
            int firstRow = CellCoordinate(point.z - radiusM, originZ, inverse, rows) - 1;
            int lastRow = CellCoordinate(point.z + radiusM, originZ, inverse, rows) + 1;
            if (firstColumn < 0) firstColumn = 0;
            if (firstRow < 0) firstRow = 0;
            if (lastColumn > columns - 1) lastColumn = columns - 1;
            if (lastRow > rows - 1) lastRow = rows - 1;

            float radiusSq = radiusM * radiusM;
            int written = 0;
            for (int cz = firstRow; cz <= lastRow; cz++)
            {
                int rowBase = cz * columns;
                for (int cx = firstColumn; cx <= lastColumn; cx++)
                {
                    int cell = rowBase + cx;
                    int start = cellStart[cell];
                    int end = cellStart[cell + 1];
                    if (start >= end) continue;

                    Vector3 low = cellMin[cell];
                    Vector3 high = cellMax[cell];
                    float dx = point.x - Mathf.Clamp(point.x, low.x, high.x);
                    float dy = point.y - Mathf.Clamp(point.y, low.y, high.y);
                    float dz = point.z - Mathf.Clamp(point.z, low.z, high.z);
                    if (dx * dx + dy * dy + dz * dz > radiusSq) continue;

                    for (int k = start; k < end; k++)
                    {
                        outRows[written] = cellRows[k];
                        written = written + 1;
                    }
                }
            }
            return written;
        }
    }
}
