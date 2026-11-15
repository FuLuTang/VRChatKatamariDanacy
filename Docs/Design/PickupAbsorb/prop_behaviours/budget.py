"""Udon 更新预算：每帧到底能养几个活动道具。

Udon 的每帧预算不属于我们：90 Hz 头显一帧 11.1 ms，里面还有 VRChat 本体和玩家
Avatar（`Docs/02_TECHNICAL_CONTRACTS.md` §9）。所以这里只回答一个算术问题：

    给这批道具 X ms/帧，它们最多能有多少个、以多低的频率更新？

三个量分开算，因为它们的成本差一个数量级以上：

- ``Animator``：自循环动画（人挥手、风车转）。**Udon 侧成本是 0**——只要
  ``Animator`` 在视野里被引擎更新；关掉 ``Animator`` 也就省下了。所以这类行为
  不占本模块的 Udon 预算，只占 Draw Call / 骨骼预算。
- 路径 / 绕行：每个活动件每秒写一次 ``transform``，成本随件数线性。
- 坠落：只在事件发生后的一小段时间里有成本，落定即停。

节流用**固定步长**（每 k 帧更一次）而不是"每帧都更、按 dt 缩放"：Udon 里
``Time.deltaTime`` 抖动会让不同客户端的落点差出几厘米，而本地单人模式里
固定步长也更好复现。
"""

import math

# 一个「写一次 transform + 两三次算术」的 Udon 更新，按 0.02 ms 计。
# 这是**未实测量级**：没有在 Unity 里跑过 profile，取的是社区对 Udon 慢 200–1000×
# 的保守中间值。它只用于给出数量级结论（"几十个，不是几百个"），不是承诺。
DEFAULT_COST_MS_PER_UPDATE = 0.02


def game_budget_ms(frame_ms: float = 1000.0 / 90.0,
                   share: float = 0.25) -> float:
    """一帧里分给本游戏的毫秒数（默认 90 Hz 的 25%）。"""
    if frame_ms <= 0.0:
        raise ValueError("frame_ms must be positive")
    if not 0.0 < share <= 1.0:
        raise ValueError("share must be in (0, 1]")
    return frame_ms * share


def updates_per_frame(budget_ms: float,
                      cost_ms_per_update: float = DEFAULT_COST_MS_PER_UPDATE
                      ) -> int:
    """这一帧最多做多少次更新（向下取整，且至少 1）。"""
    if cost_ms_per_update <= 0.0:
        raise ValueError("cost_ms_per_update must be positive")
    return max(1, int(budget_ms / cost_ms_per_update))


def stride_for(n_movers: int, updates_hz: float, frame_hz: float) -> int:
    """``n_movers`` 个活动件要降到多少 Hz 才不超预算 → 每 k 帧更一次。"""
    if n_movers <= 0:
        raise ValueError("n_movers must be positive")
    if updates_hz <= 0.0 or frame_hz <= 0.0:
        raise ValueError("frequencies must be positive")
    if updates_hz > frame_hz:
        raise ValueError("updates_hz cannot exceed frame_hz")
    return max(1, int(round(frame_hz / updates_hz)))


def movers_per_frame(n_movers: int, updates_hz: float,
                     frame_hz: float) -> float:
    """按固定步长分摊后，平均每帧真正被更新的件数。"""
    stride = stride_for(n_movers, updates_hz, frame_hz)
    # 每个活动件每 stride 帧更一次；摊到每帧是 n/stride，但一个物件不能半次更新，
    # 所以按"每帧最多 ceil(n/stride)"给上界，平均值用 n/stride。
    return n_movers / float(stride)


def steady_state_cost_ms(n_movers: int, updates_hz: float, frame_hz: float,
                         cost_ms_per_update: float =
                         DEFAULT_COST_MS_PER_UPDATE) -> float:
    """这套配置的稳态成本（ms/帧）。"""
    return movers_per_frame(n_movers, updates_hz, frame_hz) \
        * cost_ms_per_update


def max_movers(budget_ms: float, updates_hz: float, frame_hz: float,
               cost_ms_per_update: float = DEFAULT_COST_MS_PER_UPDATE) -> int:
    """在给定预算下能养的最大活动件数（按同样的固定步长分摊）。

    解 ``n / stride(n) * cost <= budget``。因为 ``stride`` 对 n 是阶梯函数，
    直接逐个试比闭式解更诚实：从 1 往上加，直到超预算。
    """
    if budget_ms <= 0.0:
        return 0
    best = 0
    n = 1
    while n <= 100000:
        if movers_per_frame(n, updates_hz, frame_hz) \
                * cost_ms_per_update > budget_ms:
            break
        best = n
        n += 1
    return best


def fall_cost_ms(drop_m: float, dt_s: float = 1.0 / 30.0,
                 cost_ms_per_update: float = DEFAULT_COST_MS_PER_UPDATE
                 ) -> float:
    """一次坠落的总 Udon 成本：步数 × 每步成本（下落 0.5·g·t²）。"""
    if drop_m < 0.0:
        raise ValueError("drop_m must be non-negative")
    steps = int(math.ceil((2.0 * drop_m / 9.81) ** 0.5 / dt_s))
    return steps * cost_ms_per_update
