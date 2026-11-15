"""道具行为离线参考模型（支撑图 / 坠落 / 更新预算）。

只做三件事：从几何推导「谁托着谁」、丢了支撑就按简单坠落传播、以及算清 Udon
每帧能负担多少个活动道具。不是运行时：运行时是 UdonSharp，布局见
``Docs/Design/PickupAbsorb/PROP_BEHAVIOURS.md``。

模块划分：
- ``support_graph``：支撑推导 + CSR 邻接 + 移除传播 + 坠落积分；
- ``budget``：更新预算与分帧节流的算术。
"""
