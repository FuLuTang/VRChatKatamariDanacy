"""挡路道具的离线参考模型：挡路判定、AABB 距离与挡路集合的选择。

PROVISIONAL：与 `Runtime/Pickup/BlockingPropMath.cs` 是同一套规则、同一个
tie-break。本文件是可离线跑的参考模型，C# 是唯一的运行期实现；两边改一处就
要同步另一处，`Docs/tests/pickup_absorb/test_blocking_props.py` 与
`Tests/EditMode/BlockingPropMathTests.cs` 是同一组用例。

单位与坐标：长度是米，世界坐标 +Y 向上。盒子用最小角/最大角表示；输入的
`extents` 是**全尺寸**（与本项目 pickup `bounds` 同义），不是 Unity 的 half
extents，所以盒子是 `centre ± extents / 2`。

拒绝输入用返回值表达，不抛异常：运行期的 Udon 侧没有 try/catch，两边必须能
表达同一件事。非有限值、倒置盒和越界参数（count / capacity / 数组长度）都不会
写出一部分结果——`select_nearest` 整次返回 None，`is_beyond_absorb_limit` 把
坏数据当"超出上限"。上一批里残留的越界下标是例外：它只被忽略，不整帧拒绝。
"""
from __future__ import annotations

import math
from typing import Optional, Tuple

Vec3 = Tuple[float, float, float]
Bounds = Tuple[Vec3, Vec3]


def _finite_float(value) -> Optional[float]:
    if isinstance(value, bool):
        return None
    try:
        result = float(value)
    except (TypeError, ValueError, OverflowError):
        return None
    if not math.isfinite(result):
        return None
    return result


def _finite_vector(value) -> Optional[Vec3]:
    try:
        if len(value) != 3:
            return None
        x = _finite_float(value[0])
        y = _finite_float(value[1])
        z = _finite_float(value[2])
    except (TypeError, IndexError, KeyError):
        return None
    if x is None or y is None or z is None:
        return None
    return x, y, z


def _length(value) -> Optional[int]:
    if value is None:
        return 0
    try:
        return len(value)
    except TypeError:
        return None


def _non_negative_int(value) -> Optional[int]:
    if isinstance(value, bool) or not isinstance(value, int):
        return None
    if value < 0:
        return None
    return value


def _inverted(minimum: Vec3, maximum: Vec3) -> bool:
    return minimum[0] > maximum[0] or minimum[1] > maximum[1] or minimum[2] > maximum[2]


def try_box_from_centre_and_extents(centre, extents) -> Optional[Bounds]:
    """由中心与全尺寸算最小/最大角；负尺寸、非有限值和不可表示的结果都拒绝。

    宿主没有 collider 时用它把 `propBounds` 变成挡路盒，公式就是
    `position ± bounds / 2`（BLOCKING_PROPS_PLAN §5.3）。零尺寸是合法的点盒。
    """
    centre_v = _finite_vector(centre)
    extents_v = _finite_vector(extents)
    if centre_v is None or extents_v is None:
        return None
    if extents_v[0] < 0 or extents_v[1] < 0 or extents_v[2] < 0:
        return None
    half = (extents_v[0] * 0.5, extents_v[1] * 0.5, extents_v[2] * 0.5)
    minimum = (centre_v[0] - half[0], centre_v[1] - half[1], centre_v[2] - half[2])
    maximum = (centre_v[0] + half[0], centre_v[1] + half[1], centre_v[2] + half[2])
    if not all(math.isfinite(value) for value in minimum + maximum):
        return None
    return minimum, maximum


def try_distance_to_box(point, minimum, maximum) -> Optional[float]:
    """点到轴对齐盒的距离；盒内（含表面）= 0。

    这是挡路筛选的度量：3 m 长凳的中心可能在 1.6 m 外，近面却在 0.1 m 外，
    所以用"球心到盒"而不是中心距（BLOCKING_PROPS_PLAN §2 A）。倒置盒
    （minimum > maximum）是数据错，拒绝；零尺寸的点盒合法。
    """
    point_v = _finite_vector(point)
    minimum_v = _finite_vector(minimum)
    maximum_v = _finite_vector(maximum)
    if point_v is None or minimum_v is None or maximum_v is None:
        return None
    if _inverted(minimum_v, maximum_v):
        return None
    distance_squared = 0.0
    for axis in range(3):
        if point_v[axis] < minimum_v[axis]:
            delta = minimum_v[axis] - point_v[axis]
        elif point_v[axis] > maximum_v[axis]:
            delta = point_v[axis] - maximum_v[axis]
        else:
            delta = 0.0
        distance_squared += delta * delta
    if not math.isfinite(distance_squared):
        return None
    return math.sqrt(distance_squared)


def is_beyond_absorb_limit(bounds, limit_world_m) -> bool:
    """物件是否"太大"到吸不进（= 应当挡路），与吸收门的尺寸比较完全一致。

    比较的是 bounds 的最大全长，只有**严格更大**才算超出：`extent == limit`
    仍然可吸（`PickupAbsorbMath` 的 `>`，与 round model 的 `<=` 同一边界）。
    非有限 bounds、负尺寸和非有限 limit 一律返回 True：门上这些输入的结论是
    RejectInvalidInput 而不是 Absorb，而 NaN 与任何数比较都是假——若在这里放行，
    坏数据的道具会既不挡也吸不动，正是要修的那个症状。
    """
    bounds_v = _finite_vector(bounds)
    limit = _finite_float(limit_world_m)
    if bounds_v is None or limit is None:
        return True
    if bounds_v[0] < 0 or bounds_v[1] < 0 or bounds_v[2] < 0:
        return True
    return max(bounds_v) > limit


def select_nearest(ball_centre, radius_m, minima, maxima, candidate, count,
                   previous_indices, previous_count, stickiness_m, capacity
                   ) -> Optional[Tuple[Tuple[int, ...], Tuple[float, ...]]]:
    """按 AABB 距离选出本帧要注册为挡路盒的道具，返回 (下标, 距离)。

    规则（BLOCKING_PROPS_PLAN §5.1、§6）：

    1. 只有 `candidate[i]` 为真且 `i < count` 的条目参与。
    2. 上一批 `previous_indices[0:previous_count]` 里仍在
       `radius_m + stickiness_m` 内的条目优先保留，按上一批给出的顺序输出
       （粘性：旧件不因为新件只近一点点就被换掉，防抖）。重复下标只保留一次，
       越界或已不是候选的下标忽略，不整帧拒绝。
    3. 其余候选按 AABB 距离升序；等距按下标升序（按下标升序遍历、只在严格
       更小时替换，因此是稳定的）。距离等于 `radius_m` 算在半径内（含边界，
       与吸收门"距离恰好等于 reach 仍是接触"一致）。
    4. 先保留、后补足，合起来夹到 `capacity`。

    返回 None 表示拒绝：非有限/越界参数、倒置盒或不可表示的距离。整次调用
    不产生部分结果。`capacity == 0` 和 `count == 0` 是合法输入，返回空结果。

    歧义备注：上一批条目多于 `capacity` 时，保留的是上一批顺序里的前
    `capacity` 个，而不是其中最近的几个——"优先保留"+ "夹到 capacity" 的
    最简一致解释。粘性条目即使已在 `radius_m` 外（但在半径内粘性带）也先占
    槽位，同样按字面规则处理。
    """
    ball = _finite_vector(ball_centre)
    radius = _finite_float(radius_m)
    stickiness = _finite_float(stickiness_m)
    if ball is None or radius is None or stickiness is None:
        return None
    if radius < 0 or stickiness < 0:
        return None
    capacity_count = _non_negative_int(capacity)
    if capacity_count is None:
        return None
    item_count = _non_negative_int(count)
    if item_count is None:
        return None
    previous_total = _non_negative_int(previous_count)
    if previous_total is None:
        return None

    minima_length = _length(minima)
    maxima_length = _length(maxima)
    candidate_length = _length(candidate)
    previous_length = _length(previous_indices)
    if None in (minima_length, maxima_length, candidate_length, previous_length):
        return None
    if item_count > min(minima_length, maxima_length, candidate_length):
        return None
    if previous_total > previous_length:
        return None

    for i in range(item_count):
        if not candidate[i]:
            continue
        minimum = _finite_vector(minima[i])
        maximum = _finite_vector(maxima[i])
        if minimum is None or maximum is None:
            return None
        if _inverted(minimum, maximum):
            return None
        # 同一函数先算一遍并丢弃：第二步不会再半途失败，拒绝也就不会留下部分结果。
        if try_distance_to_box(ball, minimum, maximum) is None:
            return None

    sticky_radius = radius + stickiness
    if not math.isfinite(sticky_radius):
        return None
    if capacity_count == 0:
        return (), ()

    selected = []
    distances = []
    for slot in range(previous_total):
        if len(selected) >= capacity_count:
            break
        index = previous_indices[slot]
        if isinstance(index, bool) or not isinstance(index, int):
            continue
        if index < 0 or index >= item_count:
            continue
        if not candidate[index]:
            continue
        if index in selected:
            continue
        distance = try_distance_to_box(ball, minima[index], maxima[index])
        if distance is None:
            return None
        if distance > sticky_radius:
            continue
        selected.append(index)
        distances.append(distance)

    while len(selected) < capacity_count:
        best_index = -1
        best_distance = 0.0
        for i in range(item_count):
            if not candidate[i] or i in selected:
                continue
            distance = try_distance_to_box(ball, minima[i], maxima[i])
            if distance is None:
                return None
            if distance > radius:
                continue
            if best_index < 0 or distance < best_distance:
                best_index = i
                best_distance = distance
        if best_index < 0:
            break
        selected.append(best_index)
        distances.append(best_distance)
    return tuple(selected), tuple(distances)
