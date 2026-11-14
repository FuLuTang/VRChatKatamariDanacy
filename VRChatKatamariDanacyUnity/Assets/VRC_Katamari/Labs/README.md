# 实验场景（Labs）

本目录是被手动或脚本搭出来的实验场景，不是产品场景。每个场景有自己的说明；下面这一节是所有**脚本搭场景**共用的前置条件。

## 脚本搭场景的两件必备 SDK 组件

用 VRChat SDK 向导（`VRChat SDK → Show Build Control Panel` 里新建世界）建场景时，SDK 会自动补上下面两件。**用脚本（或手工摆）搭场景时必须自己补**，否则会出现一个很坑的不对称：**Play Mode 一切正常，构建必挂**。

### 1. `VRC.Core.PipelineManager`（挂在场景根对象上）

- **作用**：承载这个世界的身份与打包元数据 —— `blueprintId`、SDK 版本、内容校验。它是「出版元数据」，不是「运行元数据」。
- **缺了会怎样**：构建时报
  `BuildFailedException: The built scene has no pipeline manager component. There must be exactly one.`
- **为什么 Play Mode 不报**：Play Mode 走的是 **ClientSim 模拟器**，它只查询 `VRCSceneDescriptor` 与 spawn 点，然后直接驱动 Udon 虚拟机；`PipelineManager` 从头到尾没有被查询过。

### 2. `VRCSceneDescriptor.ReferenceCamera`（指向场景里的主相机）

- **作用**：模拟器/客户端用来取初始视角的参考相机。
- **缺了会怎样**：`[ClientSimSceneManager] Reference camera is not set in the SceneDescriptor!`（每次进入 Play Mode 一条警告）；对构建无影响。
- **为什么只在 Play 时出现**：它只影响 ClientSim 的视角初始化，构建管线不需要它。

### 不对称对照

| | Play Mode（ClientSim） | 构建（SDK 构建管线） |
| --- | --- | --- |
| 谁在驱动 | ClientSim 模拟器 → Udon 虚拟机 | SDK 打包成 `.vrcw` |
| 需要 `PipelineManager` | **否**，从不查询 | **是**，缺了直接 `BuildFailedException` |
| 需要 `ReferenceCamera` | 是（否则警告，视角可能不对） | 否 |
| 缺组件的表现 | 一切正常，最多一条警告 | 构建失败，Play Mode 里毫无征兆 |

### 给下一个搭场景的人

> 用 SDK 向导建世界会自动补 `PipelineManager` 和 `ReferenceCamera`；**用脚本搭场景必须手动补**，否则 Play Mode 一切正常、构建必挂。

在 `BallPlayground` 里这两件是补过的（`PipelineManager` 挂在根 `BallPlayground` 上，`ReferenceCamera` 指向 `PlaygroundCamera`）。

## BallPlayground

见 [BallPlayground/README.md](BallPlayground/README.md)。
