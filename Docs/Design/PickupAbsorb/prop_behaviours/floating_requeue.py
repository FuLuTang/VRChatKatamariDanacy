"""逐 tick 复检参考模型：「我脚下还有没有东西」，没有就落。

这不是运行时本身，而是 ``PROP_BEHAVIOURS.md`` §10.9 那套 v1 规则的**参考实现**，
和 ``support_graph.py``（§10.4 的支撑图 + 划名 + 活计数，已被所有者判定为过度设计）
并列：那是旧设计，这个是现行设计，两者都在 ``Docs/`` 下、都不进 Unity。

数据形状按 Udon 来写：

- 烘焙期一次算好 ``parent`` 指针和它的 CSR 子表（``child_start`` / ``child_list``）；
- 运行期只有：每件的状态、一个待复检集合（Udon 里是 ``bool[]`` + 一个小队列）、
  每件的 ``vy``，以及一次的竖直积分；
- 「谁托着谁」只在**入队**时用一次指针（省一次全表扫描），**判定**永远只看几何——
  指针错了只是浪费一次复检，不会让不该落的落、该落的留。

三条状态机规则（与 §10.9 一一对应）：

1. 一件道具只在两种时刻被重新检查：它的父件被吃掉，或它的父件开始下落。
2. 复检通过（脚下还有东西）⇒ 留在待复检集合里，下个 tick 再看；不通过 ⇒ 开始落。
3. 一件开始落 ⇒ 它**自己**不再是支撑面（状态不是 RESTING/SETTLED），于是它的子件
   在下一轮复检里必然失败——链就这样一层层落下去，不需要递归，不需要事件传播。

``pinned``（作者说「这件自己站着，别让它掉」：装饰、挂着的招牌、云）：

- **永不入队、永不下落**——它没有「父件没了」这个需求，所以 ``_enqueue`` 直接跳过；
- 因此它是**传播的终点**：A 上钉着 P，P 上放着 C；吃掉 A，P 不动，于是 C 的复检
  仍然看见 P 的顶面，C 也不动。这就是「pinned 挡住坠落传播」；
- 它**仍然是支撑面**（状态 RESTING），别的东西照常可以放在它上面；
- ``pinned`` 和 ``parent`` 可以同时存在，那就是「挂在横梁上的招牌」：指针说明它属于谁
  （整组记账 / 被吃时一起处理），``pinned`` 说明梁没了也别掉。``pinned`` 优先。

容差只有一个来源：``placement_core.contact``（和推导、校验器同一个函数），所以这里
不会出现「导出说有支撑、运行期说没有」的漂移。
"""

from Tools.levelbuild import placement_core as core

# --- 状态（与 PROP_BEHAVIOURS §4 的 propState 同值） ----------------------
RESTING = 0
FALLING = 1
SETTLED = 2
ABSORBED = 3

#: 竖直积分步长与重力；落地判定容差与 ``support_graph.py`` 同量级。
GRAVITY_MPS2 = 9.81
FALL_DT_S = 1.0 / 30.0
GROUND_EPS_M = 1e-3
SYMBOL = {RESTING: "resting", FALLING: "falling", SETTLED: "settled", ABSORBED: "absorbed"}


class Body:
    """一件道具：世界 AABB、父指针、是否钉住（``box`` 是 ``((x,y,z),(x,y,z))``）。"""

    def __init__(self, item_id, box, parent=None, pinned=False):
        self.item_id = item_id
        self.box = box
        self.parent = parent
        self.pinned = bool(pinned)

    def __repr__(self):  # pragma: no cover - 调试用
        return "Body(%s, parent=%r, pinned=%r)" % (self.item_id, self.parent, self.pinned)


class Field:
    """一关的运行期状态：CSR 子表 + 状态 + 待复检集合 + 竖直速度。"""

    def __init__(self, bodies, ground_y=0.0):
        self.bodies = list(bodies)
        self.ground_y = float(ground_y)
        self.count = len(self.bodies)
        self.ids = [body.item_id for body in self.bodies]
        self.index = {item_id: i for i, item_id in enumerate(self.ids)}
        self.parent = [None if body.parent is None else body.parent for body in self.bodies]
        self.pinned = [body.pinned for body in self.bodies]
        self.state = [RESTING] * self.count
        self.vy = [0.0] * self.count
        self.bottom = [body.box[0][1] for body in self.bodies]
        self.height = [body.box[1][1] - body.box[0][1] for body in self.bodies]
        self.min_x = [body.box[0][0] for body in self.bodies]
        self.max_x = [body.box[1][0] for body in self.bodies]
        self.min_z = [body.box[0][2] for body in self.bodies]
        self.max_z = [body.box[1][2] for body in self.bodies]
        self._build_children()
        #: 待复检（Udon 里是 ``bool[] pendingFlag`` + ``int[] pending`` + 队头队尾）。
        self.pending = []
        self.pending_flag = [False] * self.count
        self.last_enqueued = []          # 上一次谁真的被入队（用例要看这个）

    # -- 烘焙：CSR 子表（两趟，行序稳定） ---------------------------------- #
    def _build_children(self):
        counts = [0] * (self.count + 1)
        for i in range(self.count):
            parent = self.parent[i]
            if parent is not None and parent in self.index:
                counts[self.index[parent] + 1] += 1
        for i in range(self.count):
            counts[i + 1] += counts[i]
        self.child_start = counts
        fill = list(self.child_start[:-1])
        self.child_list = [0] * self.child_start[-1]
        for i in range(self.count):
            parent = self.parent[i]
            if parent is not None and parent in self.index:
                slot = fill[self.index[parent]]
                self.child_list[slot] = i
                fill[self.index[parent]] = slot + 1

    def children(self, i):
        return list(self.child_list[self.child_start[i]:self.child_start[i + 1]])

    # -- 几何 -------------------------------------------------------------- #
    def box_of(self, i):
        return ((self.min_x[i], self.bottom[i], self.min_z[i]),
                (self.max_x[i], self.bottom[i] + self.height[i], self.max_z[i]))

    def top_of(self, i):
        return self.bottom[i] + self.height[i]

    def _seated(self, i):
        """脚下最高的顶面（含地面）；没有就 ``None``。只用 RESTING/SETTLED 当支撑面。"""
        child = self.box_of(i)
        best = None
        for j in range(self.count):
            if j == i or self.state[j] not in (RESTING, SETTLED):
                continue
            if core.contact(child, self.box_of(j))[0]:
                top = self.top_of(j)
                if best is None or top > best:
                    best = top
        if self.bottom[i] <= self.ground_y + GROUND_EPS_M:
            if best is None or self.ground_y > best:
                best = self.ground_y
        return best

    def _landing_top(self, i, from_bottom):
        """落点：只有静止/落定的东西接得住；一件都接不住就是地面。

        ``from_bottom`` 是本步下移**之前**的底面，不是现在的：一步只走 1 cm，但速度
        一大就会一步跨过薄板的顶面，拿现在的底面去比会把板「钻」过去（C# 那边同样要
        用步前的底面，否则快速下坠会穿过薄桌面）。
        """
        child = self.box_of(i)
        best = self.ground_y
        for j in range(self.count):
            if j == i or self.state[j] not in (RESTING, SETTLED):
                continue
            if core.footprint_overlap(child, self.box_of(j)) is None:
                continue
            top = self.top_of(j)
            if top <= from_bottom + GROUND_EPS_M and top > best:
                best = top
        return best

    # -- 队列 -------------------------------------------------------------- #
    def _enqueue(self, i):
        if self.pinned[i] or self.state[i] == ABSORBED:
            return False                    # 钉住的、已经被吃掉的不需要复检
        if self.pending_flag[i]:
            return False
        self.pending_flag[i] = True
        self.pending.append(i)
        self.last_enqueued.append(i)
        return True

    # -- 吸收 -------------------------------------------------------------- #
    def absorb(self, item_ids):
        """吃掉这些道具：它们的孩子入队复检（钉住的孩子跳过）。"""
        self.last_enqueued = []
        absorbed = []
        for item_id in item_ids:
            i = self.index.get(item_id)
            if i is None or self.state[i] == ABSORBED:
                continue
            self.state[i] = ABSORBED
            self.vy[i] = 0.0
            if self.pending_flag[i]:
                self.pending_flag[i] = False      # 尸体不再复检
            absorbed.append(item_id)
        for item_id in absorbed:
            for child in self.children(self.index[item_id]):
                self._enqueue(child)
        return absorbed

    # -- 每 tick 复检 + 积分 ----------------------------------------------- #
    def _requeue(self):
        """复检每一个待复检的道具：还有支撑就留着，没有就开始落。

        子件在这时入队，但**下一 tick** 才复检（``_enqueue`` 追加到新的 ``self.pending``，
        而这一轮遍历的是旧队列）：所以一摞是「一层一层」落的，不会在父件还没动的那一帧
        就把整摞判成坠落。
        """
        queue = self.pending
        self.pending = []
        for i in queue:
            if self.state[i] == ABSORBED:
                self.pending_flag[i] = False
                continue
            if self._seated(i) is not None:
                self.pending.append(i)       # 留着，下个 tick 再看
                continue
            self.pending_flag[i] = False
            if self.state[i] in (RESTING, SETTLED):
                self.state[i] = FALLING
                self.vy[i] = 0.0
                for child in self.children(i):
                    self._enqueue(child)     # 往上爬一层

    def _integrate(self):
        for i in range(self.count):
            if self.state[i] != FALLING:
                continue
            previous = self.bottom[i]
            self.vy[i] -= GRAVITY_MPS2 * FALL_DT_S
            self.bottom[i] += self.vy[i] * FALL_DT_S
            landing = self._landing_top(i, previous)
            if self.bottom[i] <= landing + GROUND_EPS_M:
                self.bottom[i] = landing
                self.vy[i] = 0.0
                self.state[i] = SETTLED

    def step(self, ticks=1):
        for _ in range(ticks):
            self._requeue()
            self._integrate()
        return self

    def settle(self, max_steps=600):
        for _ in range(max_steps):
            if not self.pending and FALLING not in self.state:
                return self
            self.step()
        return self

    # -- 读取 -------------------------------------------------------------- #
    def states(self):
        return {item_id: self.state[i] for item_id, i in self.index.items()}

    def heights(self):
        return {item_id: round(self.bottom[i], 4) for item_id, i in self.index.items()}

    def summary(self):
        return {item_id: SYMBOL[self.state[i]] for item_id, i in self.index.items()}
