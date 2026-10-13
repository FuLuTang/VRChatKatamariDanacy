# Original basic models — placeholder set

本目录是供离线预览与后续资产讨论使用的原创、中性、可替换低多边形占位模型。不是最终美术、游戏截图或 Unity 导入验证；不包含第三方资产、商标、原作角色/音乐/品牌元素，也未指定授权许可。请勿从这些模型推断玩法实体数、碰撞体、价值或正式美术方向。

## 目录内容

每个模型各有 `.blend` 源文件、`.fbx` 导出、正/侧/俯三视图拼图 PNG 与同名 `.manifest.json`；`generate.py` 是纯 `bpy` 可复现脚本。`manifest.json` 为总清单；`SHA256SUMS` 为各交付文件的 SHA-256 校验清单。

## 临时约定（T06 前不冻结）

- Blender 单位按 1 BU = 1 m 暂定；建模 Z 向上。
- FBX 导出 Forward=-Z、Up=Y、应用变换；预期 Unity 实例 Transform 为 Rotation=(0,0,0)、Scale=(1,1,1)，直立、正面 +Z。此处仅记录导出意图，未作 Unity 导入验证。
- 模型根对象命名为 `<名称>_Root`；球芯枢轴在球心，其余模型枢轴在底面中心。`carrier_platform` 的 `SeatAnchor`、`FollowRigAnchor` 是独立空物体；跟随锚点不是平台几何。
- 采集物世界尺度 **OPEN**；清单中尺寸仅作现实尺寸参考，不代表玩法比例、Collider、收益或游戏内尺度。
- 预览为单模型三视图，不是游戏画面。材质为命名的本地纯色材质，无外部贴图。

## 限制与后续

网格自检在生成脚本内检查非流形边与零面积面，并重算法线；清单记录检查结果。该检查不等同于完整资产 QA、平台性能测试、碰撞体验证、FBX 往返一致性测试或 Unity 导入测试。缓坡/外形和枢轴应在 T06 尺度与接口冻结后再审核。未经审核不得视为可直接集成的最终资产。

## 复现

在安装 Blender 的环境中运行独立后台进程，参数 `MODEL_NAME` 选一个清单模型，`OUT_DIR` 指向隔离输出目录，然后执行 `blender --background --factory-startup --disable-autoexec --python generate.py`。Windows 原始构建目录使用 `%USERPROFILE%` 等环境占位的约定；本目录不记录设备用户名、主机名、私网 IP 或账号信息。
