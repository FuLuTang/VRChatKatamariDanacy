# 调试用驾驶覆盖（DEBUG_DRIVE）

`DesktopMoveInput` 的 DEV-ONLY 覆盖：Play 里由操作者直接喂两个轴，用来跑完整回合验收（吸附 →
超时 → 结果卡 → 返回 → 重试）。默认关闭；关闭时设备路径的推轴顺序不变（`Update` 一次 bool 测试后
返回，两个 `InputMove*` 只多一个恒假分支），场景不写这些字段就零变化。

| Udon 程序变量 | 类型 | 含义 |
| --- | --- | --- |
| `debugDriveEnabled` | bool | 总开关，默认 false |
| `debugAxisX` | float | 转向**速率**（同 `InputMoveHorizontal`，+1 = 右转），夹到 [-1,1] |
| `debugAxisZ` | float | 前进（同 `InputMoveVertical`，+1 = 前进），夹到 [-1,1] |
| `debugDriveSeconds` | float | 自动到期秒数，0 = 直到手动关闭（默认 0），从打开那一刻起算 |

它走真实输入**同一条**路：先 `InputOpen()`（骑乘门 ∧ 回合门），再丢非有限值，最后 `PushAxes()`。没坐下
（`BallRideStation` 关着骑乘门）球不动、到点后 `SetRoundGate(false)` 也不动——那是门在生效，不是覆盖失效。

## 操作（`Tools/mcp/mcp_cli.py`，CLI 用法见 [Tools/mcp/README.md](../../../Tools/mcp/README.md)）
0. 源码新增序列化字段后：等编译完 → 存盘 → **重开一次场景**（实例变量表是建实例时的快照，不重开 `SetProgramVariable` 写不进已有实例；`Assets/VRC_Katamari/Labs/README.md`「两张表」）。
1. Play，坐下（不坐下骑乘门是关的）。
2. 对**backing `UdonBehaviour`** 写变量（proxy 只是编辑态影子：调它的 C# 方法、在 Play 里改它的 Inspector 字段都不进 Udon，别用）。
   `execute_code` 入参 `{"action":"execute","code":…}`（`python3 Tools/mcp/mcp_cli.py schema execute_code`）：
```bash
python3 Tools/mcp/mcp_cli.py call execute_code '{"action":"execute","code":"var go=UnityEngine.GameObject.Find(\"DesktopMoveInput\");var ub=(VRC.Udon.UdonBehaviour)go.GetComponent(typeof(VRC.Udon.UdonBehaviour));ub.SetProgramVariable(\"debugDriveEnabled\",true);ub.SetProgramVariable(\"debugAxisX\",0f);ub.SetProgramVariable(\"debugAxisZ\",1f);ub.SetProgramVariable(\"debugDriveSeconds\",30f);UnityEngine.Debug.Log(\"[dbg] on=\"+ub.GetProgramVariable(\"debugDriveEnabled\"));"}'
```
   期望 `[input] debug drive on axes=(0,1) seconds=30`，随后 `[story]` 行的 `axes=` 变非零（就是之前
   `axes=0,0` 的那一项；它读的是 motor 的**平面速度**不是输入轴，不动时仍会是 0）。`GetProgramVariable` 读回是写入成功的证据，物体名取 `Worlds/StoryMode/StoryMode.unity` 里的 `DesktopMoveInput`。
3. 验收：`debugAxisZ` 保持 1、`debugAxisX` 只用来把朝向对准 prop 群 → 等 N 秒 → `[PickupFieldHost] attached id=… attached=N` 与 `[story] … absorbed=N` 递增；时钟到 0 →
   HUD 剩余读 0、结果卡出现、`[StoryResultPresenter] result level=… stars=…`，此时 `[story] axes=` 回到 0,0
   （回合门已关）。收工写 `debugAxisZ=0` 或 `debugDriveEnabled=false`（`[input] debug drive off`；到期则
   `[input] debug drive expired after=30s`）。
4. 返回：点世界里的 `BallRideExitButton`（桌面 = 左键）→ `[ride] exit requested`；重试：点结果卡上的 `StoryRetryButton`，或对它 `SendCustomEvent("OnRetryPressed")` → `[StoryRoundController] retry started round=…`。

## 出厂安全
默认关闭 + 场景从不写这四个变量 = 发布行为不变。**不能**用 `#if UNITY_EDITOR`：UdonSharp 的条件编译只对
非 Udon 代码生效，Udon 程序里的字段裁不掉，所以护栏是「默认值 + 文档 + 字段带 `DEV ONLY` Header/Tooltip」；
Release 前 grep 场景 YAML 的 `debugDriveEnabled`，为空/false 即通过。

## 未验证
未编译、未在 Unity 跑过。以下均未实测：`SetProgramVariable` 写 `[SerializeField] private` 变量生效、夹到 [-1,1] 后 motor 的行为、
`SendCustomEvent("Interact")`（返回建议直接点）、`Time.time` 到期在 ClientSim 的表现，以及这套验收在正式场景里真的跑通。
