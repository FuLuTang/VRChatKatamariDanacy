using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Pickup
{
    /// <summary>
    /// 挡路道具的纯数学：挡路盒构造、点到盒距离和挡路集合的选择。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 Docs/Design/PickupAbsorb/blocking_props.py 是同一套规则、同一个 tie-break；
    /// 那个文件是可离线跑的参考模型，这里是唯一运行期实现。长度是米，世界坐标 +Y 向上；
    /// 盒子用最小角/最大角表示。
    /// </para>
    /// <para>
    /// 输入的 extents 是**全尺寸**（与 pickup bounds 同义），不是 Unity 的 half extents，
    /// 所以盒子是 `centre ± extents / 2`。
    /// </para>
    /// <para>
    /// 这个类无状态、不分配：结果写进调用方预分配的数组，条数用调用方给的 count /
    /// capacity。失败用 bool 或负的返回条数表示，不抛异常；拒绝时不会写出部分结果。
    /// </para>
    /// </remarks>
    public static class BlockingPropMath
    {
        /// <summary>
        /// 由中心与全尺寸算挡路盒的最小/最大角。
        /// </summary>
        /// <remarks>
        /// 宿主没有 collider 时用它把 propBounds 变成挡路盒（BLOCKING_PROPS_PLAN §5.3）。
        /// 负尺寸、非有限输入和不可表示的结果都拒绝，两个 out 值此时为零；零尺寸是合法
        /// 的点盒（min == max）。
        /// </remarks>
        public static bool TryBoxFromCentreAndExtents(Vector3 centre, Vector3 extents,
            out Vector3 minimum, out Vector3 maximum)
        {
            minimum = Vector3.zero;
            maximum = Vector3.zero;
            if (!KatamariMath.IsFiniteVector(centre)) return false;
            if (!KatamariMath.IsFiniteVector(extents)) return false;
            if (extents.x < 0f || extents.y < 0f || extents.z < 0f) return false;

            float halfX = extents.x * 0.5f;
            float halfY = extents.y * 0.5f;
            float halfZ = extents.z * 0.5f;
            Vector3 low = new Vector3(centre.x - halfX, centre.y - halfY, centre.z - halfZ);
            Vector3 high = new Vector3(centre.x + halfX, centre.y + halfY, centre.z + halfZ);
            if (!KatamariMath.IsFiniteVector(low)) return false;
            if (!KatamariMath.IsFiniteVector(high)) return false;

            minimum = low;
            maximum = high;
            return true;
        }

        /// <summary>
        /// 点到轴对齐盒的距离；盒内（含表面）为 0。
        /// </summary>
        /// <remarks>
        /// 挡路筛选用球心到盒的距离而不是中心距：3 m 长凳的中心可能在 1.6 m 外，近面却在
        /// 0.1 m 外（BLOCKING_PROPS_PLAN §2 A）。倒置盒（min &gt; max）是数据错，返回
        /// false；零尺寸的点盒合法。false 时 distanceM 为 0。
        /// </remarks>
        public static bool TryDistanceToBox(Vector3 point, Vector3 minimum, Vector3 maximum,
            out float distanceM)
        {
            distanceM = 0f;
            if (!KatamariMath.IsFiniteVector(point)) return false;
            if (!KatamariMath.IsFiniteVector(minimum)) return false;
            if (!KatamariMath.IsFiniteVector(maximum)) return false;
            if (minimum.x > maximum.x || minimum.y > maximum.y || minimum.z > maximum.z)
                return false;

            float distance = DistanceToBox(point, minimum, maximum);
            if (!KatamariMath.IsFiniteFloat(distance)) return false;
            distanceM = distance;
            return true;
        }

        /// <summary>
        /// 选出本帧要注册为挡路盒的物件下标与 AABB 距离，返回写入条数。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 规则（BLOCKING_PROPS_PLAN §5.1、§6）：
        /// </para>
        /// <list type="number">
        /// <item><description>
        /// 只有 candidate[i] 为真且 i &lt; count 的条目参与。
        /// </description></item>
        /// <item><description>
        /// 上一批 previousIndices[0, previousCount) 里仍在 radiusM + stickinessM 内的条目
        /// 优先保留，按上一批给出的顺序输出（旧件不因为新件只近一点点就被换掉，防抖）。
        /// 重复下标只保留一次；越界或已不是候选的下标忽略而不整帧拒绝——道具表变化后
        /// 上一批可能残留旧下标。
        /// </description></item>
        /// <item><description>
        /// 其余候选按 AABB 距离升序，等距按下标升序。按下标升序遍历、只在严格更小时替换，
        /// 所以 tie-break 是稳定的。距离等于 radiusM 算在半径内（含边界，与吸收门
        /// "距离恰好等于 reach 仍是接触"一致）。
        /// </description></item>
        /// <item><description>
        /// 先保留、后补足，合起来夹到 capacity。
        /// </description></item>
        /// </list>
        /// <para>
        /// 返回 -1 表示拒绝：非有限或越界参数、倒置盒、不可表示的距离、缓冲区不够。
        /// 此时两个输出数组一个元素都不写。0 到 capacity 是合法条数；capacity == 0 和
        /// count == 0 返回 0。候选盒在写入前全部校验，写入阶段不会半途失败。
        /// </para>
        /// <para>
        /// 歧义备注：上一批条目多于 capacity 时，保留的是上一批顺序里的前 capacity 个，
        /// 而不是其中最近的几个——这是"优先保留 + 夹到 capacity"的最简一致解释。粘性
        /// 条目即使已在 radiusM 外（仍在半径内粘性带）也先占槽位，同样按字面规则处理。
        /// </para>
        /// </remarks>
        public static int SelectNearest(
            Vector3 ballCentre, float radiusM,
            Vector3[] minima, Vector3[] maxima, bool[] candidate, int count,
            int[] previousIndices, int previousCount, float stickinessM,
            int[] outIndices, float[] outDistances, int capacity)
        {
            if (!KatamariMath.IsFiniteVector(ballCentre)) return -1;
            if (!KatamariMath.IsFiniteFloat(radiusM) || radiusM < 0f) return -1;
            if (!KatamariMath.IsFiniteFloat(stickinessM) || stickinessM < 0f) return -1;
            if (capacity < 0) return -1;
            if (count < 0) return -1;
            if (previousCount < 0) return -1;
            if (count > 0)
            {
                if (minima == null || maxima == null || candidate == null) return -1;
                if (count > minima.Length || count > maxima.Length || count > candidate.Length)
                    return -1;
            }
            if (previousCount > 0 && previousIndices == null) return -1;
            if (previousIndices != null && previousCount > previousIndices.Length) return -1;
            if (capacity > 0)
            {
                if (outIndices == null || outDistances == null) return -1;
                if (capacity > outIndices.Length || capacity > outDistances.Length) return -1;
            }

            // 写入前把每个候选盒算一遍并丢弃结果：既校验了盒与距离，也保证后面的写入
            // 不会半途失败留下部分输出。
            for (int i = 0; i < count; i++)
            {
                if (!candidate[i]) continue;
                float ignored;
                if (!TryDistanceToBox(ballCentre, minima[i], maxima[i], out ignored)) return -1;
            }

            float stickyRadius = radiusM + stickinessM;
            if (!KatamariMath.IsFiniteFloat(stickyRadius)) return -1;
            if (capacity == 0) return 0;

            int written = 0;
            for (int slot = 0; slot < previousCount; slot++)
            {
                if (written >= capacity) break;
                int index = previousIndices[slot];
                if (index < 0 || index >= count) continue;
                if (!candidate[index]) continue;
                if (ContainsIndex(outIndices, written, index)) continue;
                float distance = DistanceToBox(ballCentre, minima[index], maxima[index]);
                if (distance > stickyRadius) continue;
                outIndices[written] = index;
                outDistances[written] = distance;
                written = written + 1;
            }

            while (written < capacity)
            {
                int bestIndex = -1;
                float bestDistance = 0f;
                for (int i = 0; i < count; i++)
                {
                    if (!candidate[i]) continue;
                    if (ContainsIndex(outIndices, written, i)) continue;
                    float distance = DistanceToBox(ballCentre, minima[i], maxima[i]);
                    if (distance > radiusM) continue;
                    if (bestIndex < 0 || distance < bestDistance)
                    {
                        bestIndex = i;
                        bestDistance = distance;
                    }
                }
                if (bestIndex < 0) break;
                outIndices[written] = bestIndex;
                outDistances[written] = bestDistance;
                written = written + 1;
            }
            return written;
        }

        // 调用方已经校验过参数；平方和溢出成 infinity 时由 TryDistanceToBox 拒绝。
        private static float DistanceToBox(Vector3 point, Vector3 minimum, Vector3 maximum)
        {
            float dx = point.x - Mathf.Clamp(point.x, minimum.x, maximum.x);
            float dy = point.y - Mathf.Clamp(point.y, minimum.y, maximum.y);
            float dz = point.z - Mathf.Clamp(point.z, minimum.z, maximum.z);
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // 输出缓冲很小（挡路容量是几个），线性查重比引入第二份索引表便宜也少状态。
        private static bool ContainsIndex(int[] indices, int count, int value)
        {
            for (int i = 0; i < count; i++)
            {
                if (indices[i] == value) return true;
            }
            return false;
        }
    }
}
