# C套 FollowRig 模拟记录

日期：2026-10-14 UTC。FollowRig从T46单独建立宿主模块；没有修改`Docs/Design/BallMotion/ball_motion.py`或其旧测试。

## 本模块实际测试

命令：`PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s Docs/tests/ball_follow_rig -v`

结果：**8 tests，8通过，0失败**。覆盖359°→1°与1°→359°短弧、指数滤波不超调、显式respawn/teleport snap、显式上下限的眼高夹持、错误输入和巨大中间量的原子拒绝、API形状只含标量水平yaw，以及1000步有限/高度边界序列。

实现输入不含姿态/HMD/自转；输出`FollowFrame(position,yaw_deg)`没有roll/pitch字段。眼高range必须由调用方提供，没有选择VR舒适参数。5Hz/8Hz只继承T46的原型起始值，不是定案。

## 全库与计划校验

- `PYTHONDONTWRITEBYTECODE=1 python3 Docs/tools/validate_plan.py`：PASS，57 tasks、两种release DAG无环、source hashes通过；Unity/Udon/VR/多人/性能/commerce均NOT_RUN。
- `PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s Docs/tests -v`：**206 tests，203通过，3失败**，失败为A/Sweep的盒内初始嵌入、角落滑出、多接触顺序。未改Sweep及其测试。较早的重排记录为194项/191通过/3失败；当前仅报告本次实际发现数，不推断计数差异来源。

## 未测与对象准备

原版源码未取得；有限GitHub metadata查询和T46本地来源见`SOURCE_EVIDENCE.md`。普通C#草稿 `RuntimeDraft/FollowRigRuntimeDraft.cs` 未在Unity/UdonSharp编译。T46源码没有被编辑。独立可进入测试Prefab尚未搭建；`TEST_OBJECT_SPEC.md`给出层级、操作、读数、失败指示与重置。没有真实Camera/HMD、VR舒适度、Udon调用/序列化或平台眼高范围验证。