# 统一操作方式总览（草案）

**状态：DRAFT** · 本页汇总已写入设计文件的操作意图与验证门禁，不是冻结接口或实现清单。**T06通过前不冻结公共输入/状态接口**；未通过T04/T05的内容仍标为 GATED。未声称任何真机兼容性或舒适性 PASS。Unity在Windows，由用户拥有；本页仅文档写作，未操作Unity、SSH或MCP。

## 1. 三种模式总表

表内状态含义：**DECIDED**＝设计文档已有决定；**GATED**＝目标行为有设计，但须T04/T05验证；**OPEN**＝设计未定。一个格可同时标注决定与验证门禁，例如 `DECIDED + GATED`。引用中的行号均指具体来源文件。

| 模式 | 移动 / 转向 / 观察 | 抓取（Grip） | Jump（使用当前道具） | 菜单 | 退出比赛 | VRChat Respawn | 原生 Comfort Turning |
|---|---|---|---|---|---|---|---|
| **PCVR A：双手归中件** | **DECIDED + GATED**：抓取活动件；双手前后输入的组合/差异形成前进与转向，共同横向输入用于侧滚。操作架使用水平参考朝向，不随HMD俯仰/转头改变动力方向；实际映射、自然观察和舒适性待验。([01_GAME_DESIGN.md:L34-L42](../../01_GAME_DESIGN.md#L34-L42), [01_GAME_DESIGN.md:L46](../../01_GAME_DESIGN.md#L46), [G1:L17](../../05_ACCEPTANCE_TESTS.md#L17)) | **OPEN + GATED（设计措辞冲突）**：`L36`写靠近后用`InputGrab`抓取，`L54`却写“A不占Grip”；onboarding和P08测试又按Grab/Grip抓取活动件。是否为不同动作未说明。优先建议统一成用户当前配置的Grab/InputGrab接合活动件，且只在合适近距有效；最终等待用户/设计澄清及T04/T05。([01_GAME_DESIGN.md:L35-L37,L54](../../01_GAME_DESIGN.md#L35-L54), [onboarding.md:L24](../Terminology/onboarding.md#L24), [cases.csv:L5-L6](cases.csv#L5-L6)) | **DECIDED + GATED**：比赛中用用户配置的 Jump 上升沿请求单槽道具；Station内不得实际跳起/退座，长按不重复；空槽只给一次轻提示。平台能否拦截默认跳跃/退座尚待验证。([01_GAME_DESIGN.md:L51-L56](../../01_GAME_DESIGN.md#L51-L56), [cases.csv:L16-L18](cases.csv#L16-L18)) | **DECIDED + GATED**：打开平台菜单；菜单打开时输入归零并阻止重复Jump。平台焦点/菜单检测与返回后的状态待验。([01_GAME_DESIGN.md:L56](../../01_GAME_DESIGN.md#L56), [cases.csv:L8](cases.csv#L8)) | **DECIDED + GATED**：使用明显的世界内“退出比赛”UI，不把KO或平台Respawn当成自愿退赛；离赛后清输入并恢复玩家设置。按钮分支及恢复待验。([01_GAME_DESIGN.md:L59-L65](../../01_GAME_DESIGN.md#L59-L65), [states.md:L11,L46](../Terminology/states.md#L11)) | **DECIDED + GATED**：平台Respawn事件触发座位/比赛状态清理；预期恢复正常行走与原身高。Jump及名单后续状态仍需明确/测试。([01_GAME_DESIGN.md:L62](../../01_GAME_DESIGN.md#L62), [G1:L18-L19](../../05_ACCEPTANCE_TESTS.md#L18-L19), [cases.csv:L21](cases.csv#L21)) | **GATED**：原生转向与Station/InputLookHorizontal交互属于平台门禁；A模式下作用对象与优先级未定。([01_GAME_DESIGN.md:L47-L48](../../01_GAME_DESIGN.md#L47-L48), [G1:L16](../../05_ACCEPTANCE_TESTS.md#L16)) |
| **PCVR B：摇杆追尾** | **DECIDED + GATED**：左摇杆提供球的二维路径意图；右摇杆用于观察/控制架水平朝向。头部观察不应意外改球路；控制架朝向如何参与移动参考仍需教学与验证。([01_GAME_DESIGN.md:L44-L48](../../01_GAME_DESIGN.md#L44-L48), [onboarding.md:L30-L37](../Terminology/onboarding.md#L30-L37), [cases.csv:L9-L11](cases.csv#L9-L11)) | **OPEN**：设计未为B模式指定Grip用途；建议首版保持未绑定，避免与移动/道具冲突，待用户确认。([01_GAME_DESIGN.md:L44-L55](../../01_GAME_DESIGN.md#L44-L55), [cases.csv:L9-L11](cases.csv#L9-L11)) | **DECIDED + GATED**：同PCVR A；Jump是通用道具请求动作，不指定控制器型号/按钮。Station组合与事件可用性需实测。([01_GAME_DESIGN.md:L51-L56](../../01_GAME_DESIGN.md#L51-L56), [states.md:L58](../Terminology/states.md#L58)) | **DECIDED + GATED**：平台菜单；打开时清输入/道具动作状态，需验证重新返回时没有旧输入。([01_GAME_DESIGN.md:L56](../../01_GAME_DESIGN.md#L56), [cases.csv:L15](cases.csv#L15)) | **DECIDED + GATED**：使用世界UI主动退出；清理输入并恢复玩家设置，不能把KO/Respawn混作退赛。([01_GAME_DESIGN.md:L59-L65](../../01_GAME_DESIGN.md#L59-L65), [states.md:L11,L46](../Terminology/states.md#L11)) | **DECIDED + GATED**：平台Respawn触发清理；G1要求恢复正常行走/原身高。Jump恢复和回合名单语义仍待核实。([01_GAME_DESIGN.md:L62-L63](../../01_GAME_DESIGN.md#L62-L63), [G1:L18](../../05_ACCEPTANCE_TESTS.md#L18), [cases.csv:L21](cases.csv#L21)) | **GATED**：右摇杆与原生Comfort Turning须分别在开启/关闭状态测试；不假定两者事件相同，也未定冲突优先级。([01_GAME_DESIGN.md:L47-L48](../../01_GAME_DESIGN.md#L47-L48), [cases.csv:L10-L11](cases.csv#L10-L11)) |
| **Desktop：键盘与鼠标** | **DECIDED（Interact已实测）+ GATED（移动/观察）**：VRChat Desktop 的 **Interact=鼠标左键**（用户2026-10-14 T04手测；[PC结果:L22](../../Evidence/T04/pc_results.md#L22)）；此为交互动作，不是移动或Jump绑定。移动仍为配置的键盘二维意图（设计举例为WASD），鼠标用于观察/控制架水平朝向；准确游戏绑定与视线耦合待测，不采用WASD+IJKL组合。([01_GAME_DESIGN.md:L44-L50](../../01_GAME_DESIGN.md#L44-L50), [onboarding.md:L39-L45](../Terminology/onboarding.md#L39-L45), [00_SCOPE_DECISIONS.md:L31](../../00_SCOPE_DECISIONS.md#L31)) | **OPEN**：设计没有给Desktop分配Grip动作；建议首版不额外绑定抓取，待用户确认。([01_GAME_DESIGN.md:L44-L55](../../01_GAME_DESIGN.md#L44-L55), [cases.csv:L12](cases.csv#L12)) | **DECIDED; PC已测 / VR仍GATED**：桌面Jump当前设计默认候选为Space；比赛中作为道具动作，不是刹车或额外冲刺。T04桌面手测确认坐姿按Jump不退座、长按只计一次、离座后跳跃恢复正常；不外推到VR。([01_GAME_DESIGN.md:L51-L56](../../01_GAME_DESIGN.md#L51-L56), [00_SCOPE_DECISIONS.md:L31](../../00_SCOPE_DECISIONS.md#L31), [onboarding.md:L44](../Terminology/onboarding.md#L44), [T04 PC结果:L15-L16](../../Evidence/T04/pc_results.md#L15-L16), [L21](../../Evidence/T04/pc_results.md#L21)) | **DECIDED + GATED**：使用平台菜单；打开时清移动/动作输入。焦点丢失和恢复需验证。([01_GAME_DESIGN.md:L56](../../01_GAME_DESIGN.md#L56), [cases.csv:L12,L15](cases.csv#L12)) | **DECIDED; PC坐姿按钮路径已测 / 其他仍GATED**：世界UI主动退出比赛，清输入并按设置恢复；T04 PC日志确认坐姿退出按钮路径的JumpImpulse恢复回读，非坐姿分支只记录no-op且可见反馈仍缺。([01_GAME_DESIGN.md:L59-L65](../../01_GAME_DESIGN.md#L59-L65), [T04 PC结果:L17,L20](../../Evidence/T04/pc_results.md#L17-L20), [states.md:L11,L46](../Terminology/states.md#L11)) | **DECIDED; PC路径已测，其他仍GATED**：平台Respawn清理比赛/座位状态；PC T04路径经`OnStationExited`回调恢复并经用户确认正常，未产生独立`reason=respawn`日志；G1要求正常行走与原身高恢复，VR及回合名单规则仍待核验。([01_GAME_DESIGN.md:L62-L63](../../01_GAME_DESIGN.md#L62-L63), [G1:L18](../../05_ACCEPTANCE_TESTS.md#L18), [T04 PC结果:L18-L19,L34-L36](../../Evidence/T04/pc_results.md#L18-L19)) | **DECIDED**：VR原生Comfort Turning不适用于Desktop；桌面观察/参考方向走鼠标路径。([01_GAME_DESIGN.md:L45-L49](../../01_GAME_DESIGN.md#L45-L49)) |

> **Space澄清：** 当前设计把桌面Space列为Jump/道具动作；旧稿中的Space刹车已废止，且不添加独立冲刺键。([01_GAME_DESIGN.md:L49-L56](../../01_GAME_DESIGN.md#L49-L56), [00_SCOPE_DECISIONS.md:L31](../../00_SCOPE_DECISIONS.md#L31))

## 2. 坐在Station按Jump：预期行为与T04现状

设计预期是：

1. **不起跳**：比赛中实际跳跃被禁用；
2. **不退座**：Station的 `Disable Station Exit` 是主防护；将JumpImpulse置零仅是辅助措施；
3. **只触发一次**：按下的上升沿触发一次，保持按住不重复；释放再按才可能再次触发；
4. **有道具时**：请求使用当前单槽道具，只有权威确认后才改变持有状态；
5. **无道具时**：只显示/触发一次轻提示，不执行隐藏效果。

依据：[`01_GAME_DESIGN.md:L51-L61`](../../01_GAME_DESIGN.md#L51-L61)、[`P08 README:L26`](README.md#L26)、[`P08 cases.csv:L16-L18`](cases.csv#L16-L18)、[`states.md:L54-L58`](../Terminology/states.md#L54-L58)。

**T04原型现状与PC实测：** 当前T04原型尚无道具系统可供Jump调用，因此坐在Station按桌面Jump没有道具使用效果是预期，不代表道具故障。2026-10-14 T04桌面手测已确认：坐姿Jump未退座，长按只计一次；离座后跳跃恢复正常。T04还实测了坐姿无道具时的一次短闪提示（圆变色）；它是轻提示，不是道具效果。以上仅为PC桌面结果，不外推VR或舒适性PASS。([01_GAME_DESIGN.md:L51-L61](../../01_GAME_DESIGN.md#L51-L61), [T04 PC结果:L3,L15-L16](../../Evidence/T04/pc_results.md#L3-L16), [L21-L22](../../Evidence/T04/pc_results.md#L21-L22))

## 3. 退出、KO、回合结束与设置恢复

以下区分“清输入/座位/比赛状态”与“恢复玩家入场前设置”。设计预期不等于完整通过；已测PC桌面路径按PC结果逐项注明，其余路径仍需T04复测，VR需T05。设计列出的设置范围是身高、移动、跳跃等，不覆盖未授权覆盖玩家其他偏好。([`01_GAME_DESIGN.md:L60-L65`](../../01_GAME_DESIGN.md#L60-L65), [`P08 README:L19,L28`](README.md#L19))

| 路径 | 文档明确的清理/恢复预期 | 状态与依据 |
|---|---|---|
| 主动“退出比赛”按钮 | 世界UI可主动退出；清除输入/当前动作状态；恢复身高、移动、跳跃等入场前保存设置。 | **PC坐姿按钮路径已测；其余仍GATED**：第二轮记录Interact从坐姿disabled到按钮离座后enabled，并有`restored=3 reread=3`；3是VRCWorld基线，不是玩家原值证明。非坐姿分支虽无物可退，用户可见反馈仍缺；其他玩家设置/模式未据此宣告通过。([01_GAME_DESIGN.md:L56,L62-L65](../../01_GAME_DESIGN.md#L56-L65), [T04 PC结果:L30-L32](../../Evidence/T04/pc_results.md#L30-L32), [T04 PC结果:L20](../../Evidence/T04/pc_results.md#L20), [T04 PC结果:L14,L26-L28](../../Evidence/T04/pc_results.md#L14-L28)) |
| 回合结束/正常重置 | 清输入并恢复身高、移动、跳跃等玩家设置。 | **GATED**：明确列为恢复路径；当前PC结果未报告回合结束/正常重置恢复实测。([01_GAME_DESIGN.md:L63](../../01_GAME_DESIGN.md#L63), [cases.csv:L22](cases.csv#L22), [T04 README:L9,L24](../../Evidence/T04/README.md#L9-L24)) |
| KO/被吞 | 清掉输入/按键状态；KO不是主动退赛，玩家仍是本局参与者并等待重生。设计未说KO时是否恢复/保留玩家原有身高、移动、跳跃设置。 | **OPEN**（行为范围未定）+ **GATED**（清理尚未验证）。([01_GAME_DESIGN.md:L25-L30,L56](../../01_GAME_DESIGN.md#L25-L30), [states.md:L11-L12,L42-L45](../Terminology/states.md#L11-L12), [cases.csv:L19](cases.csv#L19)) |
| VRChat平台Respawn | `OnPlayerRespawn`清理座位及比赛状态；G1明确要求恢复正常行走与原身高。Jump设置和Respawn后仍留在本局、重入或退回大厅的规则，文档没有完全对齐。 | **PC JumpImpulse路径曾测；D1 Interact恢复仍GATED + OPEN**：第一轮（修复前）Respawn请求Station退出，经`OnStationExited`回调恢复至3且用户确认重生正常；没有独立`reason=respawn`恢复日志。第二轮D1修复后未触发Respawn，故不能声称Interact已恢复；名单语义仍未统一。([01_GAME_DESIGN.md:L62-L63](../../01_GAME_DESIGN.md#L62-L63), [G1:L18](../../05_ACCEPTANCE_TESTS.md#L18), [T04 PC结果:L15](../../Evidence/T04/pc_results.md#L15), [T04 PC结果:L36](../../Evidence/T04/pc_results.md#L36), [states.md:L11](../Terminology/states.md#L11)) |
| 异常复位/Recovery reset | 清输入；恢复身高、移动、跳跃等设置；如不能可靠恢复，安全取消而不锁死玩家。 | **GATED**：期望写明，恢复责任/实际值仍需测试。([01_GAME_DESIGN.md:L62-L64](../../01_GAME_DESIGN.md#L62-L64), [cases.csv:L23](cases.csv#L23), [P08 README:L28](README.md#L28)) |

### JumpImpulse 读值边界

T04的多次PC记录显示：世界加载/首次入座时 `GetJumpImpulse()` 读到 `0`；随后显式设置为 `3` 后能读回 `3`。所以**初始读回的0不能作为玩家原跳跃设置的保存/恢复基准**，但也不能声称API始终只返回0。当前T04恢复策略使用文档所述的 `VRCWorld` prefab默认值3并要求恢复后重读核对；这表示恢复到该prefab基线，不等于已证明恢复了某位玩家进入前的自定义值。([T04 PC结果:L11-L14,L26-L28](../../Evidence/T04/pc_results.md#L11-L14), [01_GAME_DESIGN.md:L60-L64](../../01_GAME_DESIGN.md#L60-L64))

## 4. 尚未统一 / 需要用户决定（OPEN清单）

以下只列设计文本尚未说清的决定；每项均提供选项和建议，**建议不代表用户决定**。

1. **无道具轻提示是否足够、是否叠加其他形式**（对应表内 Jump）。T04 PC已实测一次短闪、圆变色。选项：A. 保持目前单次视觉短闪；B. 另加一次短文字提示；C. 增加可选轻触觉或短音效。**建议：**先保留单次视觉短闪、不叠加声音/振动；若测试反馈看不清，再由用户决定是否加文字或可选提示。([01_GAME_DESIGN.md:L55-L57](../../01_GAME_DESIGN.md#L55-L57), [states.md:L54-L56](../Terminology/states.md#L54-L56), [T04 PC结果:L15](../../Evidence/T04/pc_results.md#L15))
2. **Station内能否自由看向四周**（PCVR A观察）。选项：A. HMD自由转头，完全独立于动力参考；B. 只允许有限转头，超限提示回中；C. 另设重新校准/对齐动作。**建议：**优先A，保持A动力参考不随HMD转动，并由T05真人确认舒适和视野。([01_GAME_DESIGN.md:L38,L46](../../01_GAME_DESIGN.md#L38), [G1:L17](../../05_ACCEPTANCE_TESTS.md#L17), [station_decision.md:L29-L35](../../Evidence/T04/station_decision.md#L29-L35))
3. **B/Desktop观察朝向如何影响移动参考**。选项：A. 右摇杆/鼠标转动控制架，HMD/鼠标观察不单独改球路；B. 视线方向直接决定球路；C. 显式切换“观察/转向参考”模式。**建议：**采用A作为继续验证的默认草案，与B模式“自由观察不自动改路径”的方向一致，但先用教学明确并经T04/T05验证。([01_GAME_DESIGN.md:L44-L49](../../01_GAME_DESIGN.md#L44-L49), [onboarding.md:L30-L34,L41-L43](../Terminology/onboarding.md#L30-L43))
4. **原生Comfort Turning与InputLookHorizontal/right-stick的冲突优先级**。选项：A. 保留原生转向、游戏轴输入不再叠加；B. 游戏轴输入优先并关闭原生转向；C. 允许合并但明确去重/限幅。**建议：**未经验证不合并两个转向源；先明确单一有效yaw来源，开/关原生设置分别测后再选。([01_GAME_DESIGN.md:L47-L48](../../01_GAME_DESIGN.md#L47-L48), [cases.csv:L10-L11](cases.csv#L10-L11), [G1:L16](../../05_ACCEPTANCE_TESTS.md#L16))
5. **各模式的Grip分配，尤其PCVR A的冲突措辞**。选项：A. 采纳A模式具体操作段的`InputGrab`抓活动件，B/Desktop不绑定Grip；B. 采纳“A不占Grip”字面含义，A模式抓取另行确定；C. 所有模式的Grip都先留空，待统一交互设计。**建议：**优先A（它同时符合onboarding与P08抓取用例），但先明确`InputGrab`是否就是这里所说的Grip；不得将两句现状合并成已冻结绑定。([01_GAME_DESIGN.md:L35-L37,L54](../../01_GAME_DESIGN.md#L35-L54), [onboarding.md:L24](../Terminology/onboarding.md#L24), [cases.csv:L5-L6](cases.csv#L5-L6))
6. **KO期间设置保留策略**。选项：A. KO时立即恢复所有入场设置；B. 保留本局控制设置、仅清输入，待重生后继续；C. KO时只恢复Jump/移动的一部分。**建议：**KO是本局暂时状态而非退出，先清输入并保留入场时设置快照，待真正离赛/回合终止再恢复；如KO触发离座则另行按T05验证。([01_GAME_DESIGN.md:L25-L30,L56,L63](../../01_GAME_DESIGN.md#L25-L30), [states.md:L11-L12](../Terminology/states.md#L11-L12))
7. **Respawn后的恢复范围与比赛名单**。选项：A. 还在本局，恢复正常行走/原身高后继续；B. 恢复玩家设置并退回大厅；C. 先安全重置，再让玩家明确选择重入/观战。**建议：**Respawn与主动退出分开处理，至少恢复G1明确要求的行走/原身高；名单/重入行为在T04/T05和用户决定前不推断。([01_GAME_DESIGN.md:L62-L65](../../01_GAME_DESIGN.md#L62-L65), [G1:L18-L19](../../05_ACCEPTANCE_TESTS.md#L18-L19), [states.md:L11-L12](../Terminology/states.md#L11-L12))
8. **入场前JumpImpulse读值为0时的保存基线**。选项：A. 找到受支持的入场前设置快照来源；B. 明确以VRCWorld prefab默认3为回退基线；C. 无法可靠保存时阻止入场/退出并提示。**建议：**不把读值0冒充用户原值；优先确认可用快照，若只能恢复3则明确其为world基线、记录reread并让用户认可该回退。([01_GAME_DESIGN.md:L60-L64](../../01_GAME_DESIGN.md#L60-L64), [T04 PC结果:L14,L26-L28](../../Evidence/T04/pc_results.md#L14-L28))
9. **Station入座后本地自写的Interact提示仍显示**（用户2026-10-14 T04观察：入座后“坐下”提示还在，VRChat自带椅子不显示）。选项：A. 入座时切换`DisableInteractive`，离座后恢复；B. 改用VRCStation自带交互提示，不另画本地提示；C. 保留本地提示，但根据 seated 状态隐藏/显示。**建议：**优先A并确认它不妨碍主动退出；若该开关影响入座/离座交互，则选B或C并经桌面复测。已记录的缺陷结论见[T04 PC结果:L23](../../Evidence/T04/pc_results.md#L23)。

**OPEN项数量：9。** 其中部分需用户选择，部分还要先有T04/T05证据；本页不代替决定或验收。

## 5. 已发现的设计书内部不一致 / 实证缺口

- **Respawn恢复范围未对齐：** `01_GAME_DESIGN.md:L62`要求`OnPlayerRespawn`清理座位/比赛状态，`L63`列出的设置恢复路径只有退出、回合结束、异常复位；但`05_ACCEPTANCE_TESTS.md:G1:L18`明确把VRChat Respawn后的正常行走和原身高恢复列为要求。属于恢复路径清单遗漏/不一致，需统一语义；本页不自行选定名单去留规则。
- **JumpImpulse的“保存原值”与初始读值仍有缺口：** 设计书要求保存/恢复原JumpImpulse（`01_GAME_DESIGN.md:L60-L64`）；T04启动/首次入座读值为0，不能当作用户原值。最新PC手测在显式设置3后可读回3，但`3`仍只是VRCWorld基线，不是该玩家原设置的实测证明；需解决快照来源或明确回退语义。([T04 PC结果:L11-L14,L26-L28](../../Evidence/T04/pc_results.md#L11-L14), [01_GAME_DESIGN.md:L60-L64](../../01_GAME_DESIGN.md#L60-L64))
- **Grip映射存在直接措辞冲突：** `01_GAME_DESIGN.md:L36`要求靠近活动件后通过`InputGrab`进入抓取，而`L54`写“A不占Grip”；`onboarding.md:L24`与P08 A-04/A-05又按抓取动作设计。若两处术语本来不同，文件没有定义其差别。需统一后才能冻结A模式抓取绑定。([01_GAME_DESIGN.md:L35-L37,L54](../../01_GAME_DESIGN.md#L35-L54), [onboarding.md:L24](../Terminology/onboarding.md#L24), [cases.csv:L5-L6](cases.csv#L5-L6))
- **其他输入映射属于未定/门禁，不作自相矛盾判定：** B/PC视线与移动参考需要统一教学；Comfort Turning/InputLookHorizontal需验证优先级；均已列入OPEN清单。([01_GAME_DESIGN.md:L45-L48](../../01_GAME_DESIGN.md#L45-L48))

## 6. 验收与安全边界

本页是操作草案，不是全平台运行结果。T04桌面手测所确认的Jump、Interact及座位/恢复观察仅限该PC测试；VR A/B与真人舒适性仍未测试，不能据此宣称VR兼容、舒适、稳定地平线或任何舒适性PASS。相关实测边界及已知D1提示缺陷见[2026-10-14 T04 PC结果:L15-L24](../../Evidence/T04/pc_results.md#L15-L24)；真人安全要求见[human_safety.md:L1-L3,L19-L32](human_safety.md#L1-L3)。
