"""支撑图参考模型：几何推导「谁托着谁」，再传播「失去支撑就坠落」。

> **⚠️ 本文件是被取代的设计的参考实现**（活支撑计数 + CSR 反向边 + 工作队列，
> 2026-11-16 第二轮标为过度设计）。**现行 v1 的参考实现是同目录的
> ``floating_requeue.py``**：单父指针 + 每 tick 复检 + 子件坠落，没有计数、没有上限。
> 留在这里是给 `PROP_BEHAVIOURS.md` §5 的旧设计做对照，不要照它实现。

这不是运行时本身，而是运行时的**参考实现**——它故意按 Udon 的数据形状来写：

- 推导（``derive_supports``）只在烘焙期跑一次，产出 CSR 邻接表（``dependent_start``
  / ``dependent_count`` / ``dependent_list``），运行时不存字典、不建 ``List``；
- 运行期只有三样东西：每件道具的「活支撑计数」、一个定长工作队列、以及一次坠落积分；
- 全部浮点比较带容差，容差只在本文件定义（与 ``Tools/levelbuild/placement_core.py``
  的 ``SINK_TOLERANCE_M`` / ``parent_gap_window`` 同量级；旧的 `STACK_TOLERANCE_M`
  已在 2026-11-16 取消）。

术语（全文一致）：

支撑者 supporter
    把另一件道具托在它上面/旁边的道具或结构。
承重者 dependent
    被托着的那一件。
永久支撑 permanent
    地面、结构（墙/桌/架）。它们不会被吸收，所以它们的"支撑"永远不会失效；
    一件道具只要有至少一个永久支撑，它的活计数永远不归零。
活支撑计数 live
    只数**道具**支撑者的个数；结构/地面不进这个数，只置 permanent 标记。

状态机只有四个状态：``RESTING``（静止）/ ``FALLING``（坠落中）/ ``SETTLED``
（落定）/ ``ABSORBED``（已被吸收，终态）。
"""

import math
from typing import Dict, Iterable, List, Sequence, Tuple

Vec3 = Tuple[float, float, float]

# --- 容差（与 placement_core 同量级，改动必须两边一起改） -------------------
SINK_TOLERANCE_M = 0.01      # 承重者可以陷进支撑者顶面这么多仍算支撑
CONTACT_SLOP_M = 0.02        # 底面比支撑顶面高出这么多仍算接触（作者浮点误差）
MIN_OVERLAP_FRACTION = 0.20  # 接触面在两个轴上都不小于「较小者宽度」的 20%%，
                             # 且接触面积不小于「较小者投影面积」的 20%%
LEAN_SLOP_M = 0.03           # 侧面「靠着」的水平间隙上限
LEAN_MIN_HEIGHT_M = 0.05     # 靠着的东西至少要有这么高，才算撑得住
MAX_SUPPORTERS = 4           # 每件最多记四个支撑者（Udon 定长数组的容量）
TOP_EPS_M = 1e-6             # 同一高度的判定容差（"并列支撑"）

# --- 坠落 -------------------------------------------------------------------
GRAVITY_MPS2 = 9.81
FALL_DT_S = 1.0 / 30.0
FALL_STEP_LIMIT = 600        # 单次 step 最多积分的步数，防跑飞
SETTLE_EPS_M = 0.002         # 落定判定的高度容差

# --- 状态 -------------------------------------------------------------------
RESTING = 0
FALLING = 1
SETTLED = 2
ABSORBED = 3

# --- 工作队列事件种类 -------------------------------------------------------
EVENT_REMOVE = 0
EVENT_FALL = 1


class Body:
    """一件道具或一个结构的**静止姿态**。

    只支持绕 Y 轴的旋转（yaw）。理由：本项目的道具只有作者摆出来的 yaw，没有
    万向节姿态，而绕 Y 的盒子有**精确**的世界 AABB 闭式解，不需要枚举 8 个角点——
    这与 ``placement_core.world_aabb`` 的意图一致，但少一次三角函数。
    """

    __slots__ = ("item_id", "centre", "half_extents", "yaw_deg", "role",
                 "floating_ok", "half_world", "min_x", "max_x", "min_z",
                 "max_z", "bottom", "top")

    def __init__(self, item_id: str, centre: Vec3, half_extents: Vec3,
                 yaw_deg: float = 0.0, role: str = "prop",
                 floating_ok: bool = False) -> None:
        self.item_id = item_id
        self.centre = centre
        self.half_extents = half_extents
        self.yaw_deg = yaw_deg
        self.role = role
        self.floating_ok = floating_ok
        hx, hy, hz = half_extents
        c = abs(math.cos(math.radians(yaw_deg)))
        s = abs(math.sin(math.radians(yaw_deg)))
        self.half_world = (c * hx + s * hz, hy, s * hx + c * hz)
        self.min_x = centre[0] - self.half_world[0]
        self.max_x = centre[0] + self.half_world[0]
        self.min_z = centre[2] - self.half_world[2]
        self.max_z = centre[2] + self.half_world[2]
        self.bottom = centre[1] - hy
        self.top = centre[1] + hy

    def __repr__(self) -> str:  # pragma: no cover - 调试用
        return "Body(%s, y=%.4f, top=%.4f)" % (self.item_id, self.centre[1],
                                                self.top)


def horizontal_overlap_fraction(upper: Body, lower: Body) -> float:
    """``upper`` 的水平投影被 ``lower`` 盖住的面积比例（0..1）。"""
    width = upper.max_x - upper.min_x
    depth = upper.max_z - upper.min_z
    if width <= 0.0 or depth <= 0.0:
        return 0.0
    ox = min(upper.max_x, lower.max_x) - max(upper.min_x, lower.min_x)
    oz = min(upper.max_z, lower.max_z) - max(upper.min_z, lower.min_z)
    if ox <= 0.0 or oz <= 0.0:
        return 0.0
    return (ox * oz) / (width * depth)


def is_support_contact(upper: Body, lower: Body) -> bool:
    """``upper`` 是否真的坐在 ``lower`` 上。

    两条都要满足，都以**较小者**为基准——这是为了同时容纳两种极端：

    - 桌面搭在两条细腿上：接触面只占桌面的 1%，但那 1% 就是腿的全部顶面；
    - 半个盒子悬在台沿外：接触面占小盒子的 80%，但轴上只有 5 cm，撑不住。
    """
    ox = min(upper.max_x, lower.max_x) - max(upper.min_x, lower.min_x)
    oz = min(upper.max_z, lower.max_z) - max(upper.min_z, lower.min_z)
    if ox <= 0.0 or oz <= 0.0:
        return False
    upper_w = upper.max_x - upper.min_x
    upper_d = upper.max_z - upper.min_z
    lower_w = lower.max_x - lower.min_x
    lower_d = lower.max_z - lower.min_z
    min_w = min(upper_w, lower_w)
    min_d = min(upper_d, lower_d)
    if ox < MIN_OVERLAP_FRACTION * min_w or oz < MIN_OVERLAP_FRACTION * min_d:
        return False
    min_area = min(upper_w * upper_d, lower_w * lower_d)
    return ox * oz >= MIN_OVERLAP_FRACTION * min_area


def horizontal_gap(a: Body, b: Body) -> float:
    """两个盒子在水平面上的最近距离（重合时为 0）。"""
    gx = max(a.min_x - b.max_x, b.min_x - a.max_x, 0.0)
    gz = max(a.min_z - b.max_z, b.min_z - a.max_z, 0.0)
    return (gx * gx + gz * gz) ** 0.5


class Violation:
    """一条作者可读的推导诊断。"""

    __slots__ = ("code", "item_id", "detail")

    def __init__(self, code: str, item_id: str, detail: str) -> None:
        self.code = code
        self.item_id = item_id
        self.detail = detail

    def __repr__(self) -> str:  # pragma: no cover - 调试用
        return "Violation(%s, %s, %s)" % (self.code, self.item_id, self.detail)

    def __eq__(self, other: object) -> bool:
        return (isinstance(other, Violation) and other.code == self.code
                and other.item_id == self.item_id)


class SupportReport:
    """推导结果：每件道具的支撑者、是否永久、以及全部诊断。"""

    __slots__ = ("supporters", "permanent", "violations")

    def __init__(self) -> None:
        self.supporters: Dict[str, Tuple[str, ...]] = {}
        self.permanent: Dict[str, bool] = {}
        self.violations: List[Violation] = []

    def add(self, item_id: str, supporters: Sequence[str],
            permanent: bool, violations: Sequence[Violation] = ()) -> None:
        self.supporters[item_id] = tuple(supporters)
        self.permanent[item_id] = permanent
        self.violations.extend(violations)

    def codes(self) -> List[str]:
        return [v.code for v in self.violations]

    def for_item(self, item_id: str) -> Tuple[str, ...]:
        return self.supporters.get(item_id, ())


def derive_supports(bodies: Iterable[Body], ground_y: float,
                    structures: Iterable[Body] = ()) -> SupportReport:
    """从几何推导支撑关系。

    三遍，互不依赖，顺序固定（按底面高度、再按 ``item_id``），所以结果可复现：

    1. **接触**：支撑者顶面与承重者底面同高（±容差），且水平投影被盖住
       ≥ ``MIN_OVERLAP_FRACTION``；取顶面最高的那一层，同高的全收（并列支撑），
       超过 ``MAX_SUPPORTERS`` 截断并报 ``SUPPORTER_CAP``。
    2. **靠**：没有接触支撑、也没贴地时，找侧面贴着、且顶面比自己底面高出
       ``LEAN_MIN_HEIGHT_M`` 的东西（墙、柜子、箱子）。靠着的支撑者**也是普通
       支撑者**——靠箱子会掉，靠墙不会，因为墙是结构。这一条允许两件高矮相同的
       东西互相「靠」，因此成环是可能的，由 ``CYCLE`` 诊断抓出来。
    3. **地面**：底面贴地则永久支撑。

    既没有接触、也没有靠、也没贴地，就是 ``FLOATING``（浮空）。气球/装饰件用
    ``floating_ok=True`` 显式声明，不报错（契约 §6「明确悬浮类型可以浮空」）。
    """
    props = [b for b in bodies if b.role == "prop"]
    # 支撑者候选包含所有结构 + 所有道具（道具可以托道具）
    everything = list(bodies) + list(structures)
    by_id = {b.item_id: b for b in everything}
    report = SupportReport()

    for upper in sorted(props, key=lambda b: (b.bottom, b.item_id)):
        # --- 1. 接触 ---
        contacts: List[Tuple[float, float, Body]] = []
        for lower in everything:
            if lower.item_id == upper.item_id:
                continue
            if lower.top > upper.bottom + CONTACT_SLOP_M:
                continue
            if lower.top < upper.bottom - SINK_TOLERANCE_M:
                continue
            if not is_support_contact(upper, lower):
                continue
            contacts.append((lower.top, horizontal_overlap_fraction(
                upper, lower), lower))
        supporters: List[str] = []
        violations: List[Violation] = []
        permanent = abs(upper.bottom - ground_y) <= CONTACT_SLOP_M
        if contacts:
            highest = max(c[0] for c in contacts)
            same_level = [c for c in contacts if highest - c[0] <= TOP_EPS_M]
            same_level.sort(key=lambda c: (-c[1], c[2].item_id))
            if len(same_level) > MAX_SUPPORTERS:
                violations.append(Violation(
                    "SUPPORTER_CAP", upper.item_id,
                    "支撑者 %d 个，超过上限 %d，只保留压住面积最大的 %d 个"
                    % (len(same_level), MAX_SUPPORTERS, MAX_SUPPORTERS)))
                same_level = same_level[:MAX_SUPPORTERS]
            supporters = [c[2].item_id for c in same_level]
            permanent = permanent or any(
                by_id[s].role != "prop" for s in supporters)
        # --- 2. 靠 ---
        if not supporters and not permanent:
            leans: List[Tuple[float, Body]] = []
            for lower in everything:
                if lower.item_id == upper.item_id:
                    continue
                if lower.top < upper.bottom + LEAN_MIN_HEIGHT_M:
                    continue          # 太矮，撑不住倾斜
                gap = horizontal_gap(upper, lower)
                if gap > LEAN_SLOP_M:
                    continue
                leans.append((gap, lower))
            if leans:
                leans.sort(key=lambda pair: (pair[0], pair[1].item_id))
                supporters = [leans[0][1].item_id]
                permanent = by_id[supporters[0]].role != "prop"
        # --- 3. 浮空 ---
        if not supporters and not permanent and not upper.floating_ok:
            violations.append(Violation(
                "FLOATING", upper.item_id,
                "底面 %.4f m 既不贴地也不在任何支撑面上，没有任何支撑者"
                % upper.bottom))
        report.add(upper.item_id, supporters, permanent, violations)

    report.violations.extend(_find_cycles(report))
    return report


def _find_cycles(report: SupportReport) -> List[Violation]:
    """找互相支撑的环（只可能出现在「靠」上；垂直堆叠不会成环）。

    环在几何上意味着两件东西一起飘着或一起倒，运行期按「钉住」处理：不传播坠落，
    因为无论先处理谁都会得到一个自相矛盾的结果。这是作者错误，必须报出来。
    """
    WHITE, GREY, BLACK = 0, 1, 2
    colour = {key: WHITE for key in report.supporters}
    cycles: List[List[str]] = []

    def visit(node: str, path: List[str]) -> None:
        colour[node] = GREY
        path.append(node)
        for child in report.supporters.get(node, ()):
            if child not in colour:      # 结构/地面这类不在图里的支撑者
                continue
            if colour[child] == GREY:
                cycles.append(path[path.index(child):])
            elif colour[child] == WHITE:
                visit(child, path)
        path.pop()
        colour[node] = BLACK

    for start in sorted(report.supporters):
        if colour[start] == WHITE:
            visit(start, [])

    found: List[Violation] = []
    seen = set()
    for cycle in cycles:
        for member in cycle:
            if member in seen:
                continue
            seen.add(member)
            report.permanent[member] = True   # 钉住，不传播坠落
            found.append(Violation(
                "CYCLE", member,
                "与 %s 互相「靠」着，没有外部支撑：运行期按钉住处理"
                % "、".join(m for m in cycle if m != member)))
    return found


class SupportGraph:
    """CSR 邻接表 + 每件的活计数。**这就是要让 Udon 看到的数据形状。**"""

    __slots__ = ("ids", "index", "supporters", "permanent", "live",
                 "dependent_start", "dependent_count", "dependent_list",
                 "ground_y", "bodies")

    def __init__(self, bodies: Sequence[Body], report: SupportReport,
                 ground_y: float) -> None:
        self.bodies = list(bodies)
        self.ids = [b.item_id for b in self.bodies]
        self.index = {item_id: i for i, item_id in enumerate(self.ids)}
        self.ground_y = ground_y
        self.supporters = [report.supporters.get(item_id, ())
                           for item_id in self.ids]
        self.permanent = [report.permanent.get(item_id, False)
                          for item_id in self.ids]
        # 活计数只数道具支撑者
        self.live = []
        for i, item_id in enumerate(self.ids):
            count = 0
            for supporter in self.supporters[i]:
                if supporter in self.index:
                    count += 1
            self.live.append(count)
        # CSR：以「支撑者」为行，攒它的承重者
        n = len(self.ids)
        start = [0] * (n + 1)
        for i in range(n):
            for supporter in self.supporters[i]:
                j = self.index.get(supporter)
                if j is not None:
                    start[j + 1] += 1
        for i in range(n):
            start[i + 1] += start[i]
        fill = list(start[:-1])
        flat = [0] * start[n]
        for i in range(n):
            for supporter in self.supporters[i]:
                j = self.index.get(supporter)
                if j is None:
                    continue
                flat[fill[j]] = i
                fill[j] += 1
        self.dependent_start = start[:-1]
        self.dependent_count = [start[i + 1] - start[i] for i in range(n)]
        self.dependent_list = flat

    def dependents(self, i: int) -> List[int]:
        start = self.dependent_start[i]
        return self.dependent_list[start:start + self.dependent_count[i]]

    def __len__(self) -> int:
        return len(self.ids)


class SupportRuntime:
    """把吸收和坠落按事件推进；一次 ``absorb`` 收一堆 id 也只跑一遍队列。"""

    __slots__ = ("graph", "bodies", "y", "vy", "state", "queue_kind",
                 "queue_index", "head", "tail", "falls", "landings",
                 "ground_y", "steps")

    def __init__(self, graph: SupportGraph) -> None:
        self.graph = graph
        self.bodies = graph.bodies
        self.ground_y = graph.ground_y
        self.y = [b.centre[1] for b in self.bodies]
        self.vy = [0.0] * len(self.bodies)
        self.state = [RESTING] * len(self.bodies)
        # 定长工作队列：每件最多进队一次 REMOVE + 一次 FALL
        n = len(self.bodies)
        capacity = 2 * n + 4
        self.queue_kind = [0] * capacity
        self.queue_index = [0] * capacity
        self.head = 0
        self.tail = 0
        self.falls: List[str] = []
        self.landings: List[Tuple[str, float]] = []
        self.steps = 0

    # --- 队列 ---------------------------------------------------------------
    def _enqueue(self, kind: int, index: int) -> None:
        if self.tail >= len(self.queue_kind):
            raise RuntimeError("worklist overflow: capacity %d"
                               % len(self.queue_kind))
        self.queue_kind[self.tail] = kind
        self.queue_index[self.tail] = index
        self.tail += 1

    def _drain(self) -> None:
        while self.head < self.tail:
            kind = self.queue_kind[self.head]
            index = self.queue_index[self.head]
            self.head += 1
            if kind == EVENT_REMOVE:
                self._apply_remove(index)
            else:
                self._start_fall(index)

    # --- 事件 ---------------------------------------------------------------
    def _apply_remove(self, i: int) -> None:
        previous = self.state[i]
        if previous == ABSORBED:
            return
        self.state[i] = ABSORBED
        if previous == FALLING:
            return   # 坠落中已经开始「不再托人」，不重复扣
        for dependent in self.graph.dependents(i):
            self._lose_one(dependent)

    def _lose_one(self, d: int) -> None:
        if self.state[d] not in (RESTING, SETTLED):
            return
        if self.graph.permanent[d]:
            return   # 还有贴地/结构撑着，计数怎么减都掉不下来
        self.graph.live[d] -= 1
        if self.graph.live[d] <= 0:
            self._enqueue(EVENT_FALL, d)

    def _start_fall(self, i: int) -> None:
        if self.state[i] not in (RESTING, SETTLED):
            return
        self.state[i] = FALLING
        self.vy[i] = 0.0
        self.falls.append(self.bodies[i].item_id)
        for dependent in self.graph.dependents(i):
            self._lose_one(dependent)

    # --- 对外 ---------------------------------------------------------------
    def absorb(self, item_ids: Sequence[str]) -> List[str]:
        """吸收一批道具。顺序无关：先全部入队，再跑一遍队列。"""
        for item_id in item_ids:
            index = self.graph.index.get(item_id)
            if index is None:
                raise KeyError(item_id)
            self._enqueue(EVENT_REMOVE, index)
        self._drain()
        return [self.bodies[i].item_id for i in range(len(self.bodies))
                if self.state[i] == ABSORBED]

    def top_of(self, i: int) -> float:
        return self.y[i] + self.bodies[i].half_world[1]

    def bottom_of(self, i: int) -> float:
        return self.y[i] - self.bodies[i].half_world[1]

    def _landing_top(self, i: int, previous_bottom: float) -> float:
        best = self.ground_y
        shape = self.bodies[i]
        for j in range(len(self.bodies)):
            if j == i or self.state[j] not in (RESTING, SETTLED):
                continue
            top = self.top_of(j)
            if top > previous_bottom + SETTLE_EPS_M:
                continue
            if not is_support_contact(shape, self.bodies[j]):
                continue
            if top > best:
                best = top
        return best

    def step(self, dt: float = FALL_DT_S) -> None:
        """推进一次坠落积分（只动 ``FALLING`` 的道具）。"""
        self.steps += 1
        for i in range(len(self.bodies)):
            if self.state[i] != FALLING:
                continue
            previous_bottom = self.bottom_of(i)
            self.vy[i] -= GRAVITY_MPS2 * dt
            self.y[i] += self.vy[i] * dt
            landing = self._landing_top(i, previous_bottom)
            if self.bottom_of(i) <= landing:
                self.y[i] = landing + self.bodies[i].half_world[1]
                self.vy[i] = 0.0
                self.state[i] = SETTLED
                self.landings.append((self.bodies[i].item_id, self.y[i]))

    def settle_all(self, max_steps: int = FALL_STEP_LIMIT,
                   dt: float = FALL_DT_S) -> int:
        """反复 step 直到没有 ``FALLING``；返回用掉的步数。"""
        used = 0
        while used < max_steps:
            if not any(s == FALLING for s in self.state):
                return used
            self.step(dt)
            used += 1
        raise RuntimeError("模拟未收敛：%d 步后仍有道具在坠落" % used)

    def states(self) -> Dict[str, int]:
        return {self.bodies[i].item_id: self.state[i]
                for i in range(len(self.bodies))}

    def heights(self) -> Dict[str, float]:
        return {self.bodies[i].item_id: self.y[i]
                for i in range(len(self.bodies))}
