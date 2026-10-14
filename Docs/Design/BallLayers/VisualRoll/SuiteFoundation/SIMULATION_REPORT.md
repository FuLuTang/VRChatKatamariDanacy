# C套 VisualRoll 模拟记录

日期：2026-10-14 UTC。范围只在C套宿主源码/单测与文档；无Unity/Windows/Blender/账号/Git写入。

## 本模块实际测试

命令：`PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s Docs/tests/ball_visual_roll -v`

结果：**12 tests，12通过，0失败**。其中VisualRoll单层10项，跨层事件模拟2项。覆盖独立接触点瞬时无滑动残差与轴符号、正反向/斜面、切向角度、纯法向、整周、显式/阈值瞬移、非法初始四元数及原子拒绝、12,000小步归一化/回程、跨层确认/重复/撤销/快照重建/世代乱序/重生/非法输入。

开发中首轮FollowRig检查发现3个测试夹具问题（冻结滤波参数却期待跟随、重生期望值误用下界、浮点边界无容差）；修正夹具/比较容差后该模块最终复跑全绿，不是隐藏运行时失败。

## 跨层序列摘要

使用Python标准库`unittest`与`hashlib`生成合成摘要fixture；没有创建任何真实T51消息。事件包含正常移动、0/360最短航向、重复确认、合成撤销、完整快照重建、乱序旧代/高代、视觉瞬移跳过、恢复小步、非法输入原子失败和context清场。VisualRoll接触点oracle直接求`v + ω×ρ`残差，不从被测`_cross`取expected。测试仅证明宿主数学/状态模型。

## 全库与计划校验

- `PYTHONDONTWRITEBYTECODE=1 python3 Docs/tools/validate_plan.py`：PASS，报告57 tasks、两种release DAG无环、source hashes通过。NOT_RUN项包括Unity/Udon、VR舒适度、多人、性能、commerce。
- C worker早期 `PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s Docs/tests -v` 报告**206 tests，203通过，3失败**，但当时未递归发现缺`__init__.py`的FollowRig/RemoteProxy两目录，故该结果不是完整全库测试。两目录后补包标记，targeted复跑为8项和14项全通过。随后A/B/C工作流全部结束，Lead重新运行完整全库命令：**247 tests，247通过，0失败**，包含两新目录。发现修正及独立最终复跑见 [`FollowRig/SuiteFoundation/DISCOVERY_ADDENDUM.md`](../../FollowRig/SuiteFoundation/DISCOVERY_ADDENDUM.md)。

## 尚未测/草稿限制

原版游戏源码未取得；来源边界见同目录`SOURCE_EVIDENCE.md`。C# `RuntimeDraft/VisualRollRuntimeDraft.cs`未导入目标工程、未编译C#/UdonSharp、未生成ProgramAsset/Prefab。Unity Cross官方左手规则与本模块host右手代数需在Windows用方向箭头及接触速度独立探针对拍；未通过前不可场景集成。Prefab没有创建；可进入对象仅有`TEST_OBJECT_SPEC.md`规格。没有验证C#四元数乘法顺序、浮点阈值映射、Udon性能、VR舒适性或视觉效果。