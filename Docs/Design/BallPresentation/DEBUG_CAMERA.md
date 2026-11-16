# 调试用第三人称球机位（DEBUG_CAMERA）

## 为什么
坐进座位后眼睛大约在球上方 1.05 m，球就在画面下缘之外：验收截图里有道具、有地面，**没有球**，所以
"道具粘在球上"这件事今天证明不了。这个 DEV-ONLY 机位把球从后方 2.6 r、上方 1.4 r 拍下来（r = 球半径，
所以 0.15 m 和 30 m 的球取景完全一样），渲染进 RenderTexture，再用 RawImage 贴在屏幕上。

## VRChat API 结论：**没有**受支持的"玩家视角切换"
- 官方 `VRCCameraSettings`（文档 2025-08-27 更新，对应本项目 SDK 3.10.5）：
  <https://creators.vrchat.com/worlds/udon/vrc-graphics/vrc-camera-settings/>
  `ScreenCamera.Position/Rotation/Forward/Up` 全部**只读**；可写的只有 NearClipPlane / FarClipPlane /
  AllowHDR / DepthTextureMode / UseOcclusionCulling / AllowMSAA / ClearFlags / BackgroundColor /
  LayerCullDistances，以及**只对 ScreenCamera 有效**的 CullingMask（对别的实例 set 会抛异常）。原始
  `Camera` 组件不暴露。也就是说：世界**不能**移动或替换玩家视角，也没有"把视角切给某个世界相机"的入口。
  本仓 `Docs/Research/ReferenceRules/VRC_WORLD_SIZE_LIMITS.md` S9 行同此结论。
- `VRC_SceneDescriptor` 的 Reference Camera 只是把 near/far/FOV 之类**设置**应用到用户相机，不是视角切换。
- 结论：用**第二个 Camera + RenderTexture + RawImage** 的"画中画"，ClientSim 与真实客户端都成立。

## 怎么开

- ClientSim / 桌面客户端：`F2` 开关主机位，`F3` 开关 inset；或点击 `DebugCameraRig` 的碰撞体
  （`Interact`，桌面 = 左键；VR 没有键盘，只能用这个）。日志：`[camera] debug ball camera on` / `off`、
  `[camera] ball-only inset on` / `off`。
- 自动化（MCP）：`execute_code` 对 backing `UdonBehaviour` 写 `debugCameraEnabled`，写法与
  `Docs/Design/ControlIntent/DEBUG_DRIVE.md` 相同（proxy 是影子，别调它的 C#）。

## 场景对象（操作者接线）
| 对象 | 组件与设置 |
| --- | --- |
| `DebugCameraRig` | 静止不动：`BallFollowCamera`（U#）+ `BoxCollider`（`Interact` 用；放在够得着的地方，例如结果板旁） |
| `DebugCameraRig/Camera` | `Camera`（Depth -100，Target Texture = `RT_BallFollowCamera`，FOV 60，Culling Mask 抄 `ReferenceCamera` 但去掉 UiMenu/InternalUI），接到 `debugCamera` |
| `DebugCameraRig/Camera/Inset` | 子物体，`Camera`（Target Texture = `RT_BallInset`，Culling Mask 只留球所在的层；项目现在没有专用层，不建层就与主相机相同、等于更紧的裁切），姿态随父机位，行为只改 FOV |
| `DebugCameraOverlay` | `Canvas`：Screen Space - Overlay，Sorting Order 100；子 `MainView`（RawImage → `RT_BallFollowCamera`，右上 480×270）、`BallInset`（RawImage → `RT_BallInset`，右下 160×90） |
| `RT_BallFollowCamera` / `RT_BallInset` | `Assets/VRC_Katamari/Art/Diagnostics/`，1280×720 / 320×180，depth 24 |

`BallFollowCamera` 的字段：`motor`、`ballTransform`（无 motor 时的 fallback）、`fallbackRadiusM`、
`debugCamera`、`insetCamera`、`overlayRoot`=`MainView`、`insetRoot`=`BallInset`；开关
`debugCameraEnabled`（默认 **false**）、`insetEnabled`（默认 false）、`toggleKey`=F2、`insetToggleKey`=F3。

## MCP / 编辑器路线

1. 新脚本先等 Unity 编译；`Runtime/Diagnostics/BallFollowCamera.asset`（program asset）和
   `Assets/SerializedUdonPrograms/` 里的产物必须和脚本一起提交，否则场景里只有空壳。
2. 用 `execute_code` 建对象、相机、Canvas、RawImage 并接线。U# 组件**不要**用 `AddComponent<T>()`，按
   `Assets/VRC_Katamari/Labs/README.md` 的必做序列（backing `UdonBehaviour` +
   `_udonSharpBackingUdonBehaviour` + `CopyProxyToUdon`），引用全部走 `CopyProxyToUdon` 回写。
3. 存盘 → 重开场景（实例变量表是快照）→ Play，按 `F2`，Game 视图右上应出现第三人称球，右下是 inset。

## 出厂安全

默认 `debugCameraEnabled=false`；`Start` 与 `OnDisable` 会把相机和 overlay 强制关掉，所以场景即使存过
"开着"的状态也漏不进构建。关闭时每帧只做两次 `Input.GetKeyDown`：不读 motor、不碰相机、不分配。
**不能**用 `#if UNITY_EDITOR`（UdonSharp 的条件编译只对非 Udon 代码生效）。Release 前 grep 场景 YAML 的
`debugCameraEnabled`，为 false/空即通过。

## 未验证

未编译、未跑 Play。`Input.GetKeyDown(KeyCode)` 与 `KeyCode` 序列化字段在 U# 里的可用性（本仓无先例）、
Screen Space - Overlay canvas 在 **VR** 客户端里的可见性（桌面成立）、inset 的 culling mask 真能只留球、
`execute_code` 建相机+RawImage 并接线、RenderTexture 在 ClientSim Game 视图里的实际显示，均未实测。
