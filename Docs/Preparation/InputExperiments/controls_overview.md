# 统一操作方式总览（草案）

**状态：DRAFT** · 本页汇总已写入设计文件的操作意图与验证门禁，不是冻结接口或实现清单。**T06通过前不冻结公共输入/状态接口**；未通过T04/T05的内容仍标为 GATED。未声称任何真机兼容性或舒适性 PASS。Unity在Windows，由用户拥有；本页仅文档写作，未操作Unity、SSH或MCP。

## 1. 三种模式总表

表内状态含义：**DECIDED**＝设计文档已有决定；**GATED**＝目标行为有设计，但须T04/T05验证；**OPEN**＝设计未定。一个格可同时标注决定与验证门禁，例如 `DECIDED + GATED`。引用中的行号均指具体来源文件。

| 模式 | 移动 / 转向 / 观察 | 抓取（Grip） | Jump（使用当前道具） | 菜单 | 退出比赛 | VRChat Respawn | 原生 Comfort Turning |
|---|---|---|---|---|---|---|---|
| **PCVR A：双手归中件** | **DECIDED + GATED**：抓取活动件；双手前后输入的组合/差异形成前进与转向，共同横向输入用于侧滚。操作架使用水平参考朝向，不随HMD俯仰/转头改变动力方向；实际映射、自然观察和舒适性待验。([01_GAME_DESIGN.md:L34-L42](../../01_GAME_DESIGN.md#L34-L42), [01_GAME_DESIGN.md:L46](../../01_GAME_DESIGN.md#L46), [G1:L17](../../05_ACCEPTANCE_TESTS.md#L17)) | **OPEN + GATED（设计措辞冲突）**：`L36`写靠近后用`InputGrab`抓取，`L54`却写“A不占Grip”；onboarding和P08测试又按Grab/Grip抓取活动件。是否为不同动作未说明。优先建议统一成用户当前配置的Grab/InputGrab接合活动件，且只在合适近距有效；最终等待用户/设计澄清及T04/T05。([01_GAME_DESIGN.md:L35-L37,L54](../../01_GAME_DESIGN.md#L35-L54), [onboarding.md:L24](../Terminology/onboarding.md#L24), [cases.csv:L5-L6](cases.csv#L5-L6)) | **DECIDED + GATED**：比赛中用用户配置的 Jump 上升沿请求单槽道具；Station内不得实际跳起/退座，长按不重复；空槽只给一次轻提示。平台能否拦截默认跳跃/退座尚待验证。([01_GAME_DESIGN.md:L51-L56](../../01_GAME_DESIGN.md#L51-L56), [cases.csv:L16-L18](cases.csv#L16-L18)) | **DECIDED + GATED**：打开平台菜单；菜单打开时输入归零并阻止重复Jump。平台焦点/菜单检测与返回后的状态待验。([01_GAME_DESIGN.md:L56](../../01_GAME_DESIGN.md#L56), [cases.csv:L8](cases.csv#L8)) | **DECIDED + GATED**：使用明显的世界内“退出比赛”UI，不把KO或平台Respawn当成自愿退赛；离赛后清输入并恢复玩家设置。按钮分支及恢复待验。([01_GAME_DESIGN.md:L59-L65](../../01_GAME_DESIGN.md#L59-L65), [states.md:L11,L46](../Terminology/states.md#L11)) | **DECIDED + GATED**：平台Respawn事件触发座位/比赛状态清理；预期恢复正常行走与原身高。Jump及名单后续状态仍需明确/测试。([01_GAME_DESIGN.md:L62](../../01_GAME_DESIGN.md#L62), [G1:L18-L19](../../05_ACCEPTANCE_TESTS.md#L18-L19), [cases.csv:L21](cases.csv#L21)) | **GATED**：原生转向与Station/InputLookHorizontal交互属于平台门禁；A模式下作用对象与优先级未定。([01_GAME_DESIGN.md:L47-L48](../../01_GAME_DESIGN.md#L47-L48), [G1:L16](../../05_ACCEPTANCE_TESTS.md#L16)) |
| **PCVR B：摇杆追尾** | **DECIDED + GATED**：左摇杆提供球的二维路径意图；右摇杆用于观察/控制架水平朝向。头部观察不应意外改球路；控制架朝向如何参与移动参考仍需教学与验证。([01_GAME_DESIGN.md:L44-L48](../../01_GAME_DESIGN.md#L44-L48), [onboarding.md:L30-L37](../Terminology/onboarding.md#L30-L37), [cases.csv:L9-L11](cases.csv#L9-L11)) | **OPEN**：设计未为B模式指定Grip用途；建议首版保持未绑定，避免与移动/道具冲突，待用户确认。([01_GAME_DESIGN.md:L44-L55](../../01_GAME_DESIGN.md#L44-L55), [cases.csv:L9-L11](cases.csv#L9-L11)) | **DECIDED + GATED**：同PCVR A；Jump是通用道具请求动作，不指定控制器型号/按钮。Station组合与事件可用性需实测。([01_GAME_DESIGN.md:L51-L56](../../01_GAME_DESIGN.md#L51-L56), [states.md:L58](../Terminology/states.md#L58)) | **DECIDED + GATED**：平台菜单；打开时清输入/道具动作状态，需验证重新返回时没有旧输入。([01_GAME_DESIGN.md:L56](../../01_GAME_DESIGN.md#L56), [cases.csv:L15](cases.csv#L15)) | **DECIDED + GATED**：使用世界UI主动退出；清理输入并恢复玩家设置，不能把KO/Respawn混作退赛。([01_GAME_DESIGN.md:L59-L65](../../01_GAME_DESIGN.md#L59-L65), [states.md:L11,L46](../Terminology/states.md#L11)) | **DECIDED + GATED**：平台Respawn触发清理；G1要求恢复正常行走/原身高。Jump恢复和回合名单语义仍待核实。([01_GAME_DESIGN.md:L62-L63](../../01_GAME_DESIGN.md#L62-L63), [G1:L18](../../05_ACCEPTANCE_TESTS.md#L18), [cases.csv:L21](cases.csv#L21)) | **GATED**：右摇杆与原生Comfort Turning须分别在开启/关闭状态测试；不假定两者事件相同，也未定冲突优先级。([01_GAME_DESIGN.md:L47-L48](../../01_GAME_DESIGN.md#L47-L48), [cases.csv:L10-L11](cases.csv#L10-L11)) |
| **Desktop：键盘与鼠标** | **DECIDED + GATED**：配置的键盘二维移动意图控制球路径（设计举例为WASD）；鼠标用于观察/控制架水平朝向。准确绑定、鼠标视线与移动参考耦合待测试。没有额外必需字母键，不采用WASD+IJKL组合。([01_GAME_DESIGN.md:L44-L50](../../01_GAME_DESIGN.md#L44-L50), [onboarding.md:L39-L45](../Terminology/onboarding.md#L39-L45), [00_SCOPE_DECISIONS.md:L31](../../00_SCOPE_DECISIONS.md#L31)) | **OPEN**：设计没有给Desktop分配Grip动作；建议首版不额外绑定抓取，待用户确认。([01_GAME_DESIGN.md:L44-L55](../../01_GAME_DESIGN.md#L44-L55), [cases.csv:L12](cases.csv#L12)) | **DECIDED + GATED**：桌面Jump当前设计默认候选为Space；比赛中作为道具动作，不是刹车或额外冲刺。按用户实际Jump绑定验证。([01_GAME_DESIGN.md:L51-L56](../../01_GAME_DESIGN.md#L51-L56), [00_SCOPE_DECISIONS.md:L31](../../00_SCOPE_DECISIONS.md#L31), [onboarding.md:L44](../Terminology/onboarding.md#L44)) | **DECIDED + GATED**：使用平台菜单；打开时清移动/动作输入。焦点丢失和恢复需验证。([01_GAME_DESIGN.md:L56](../../01_GAME_DESIGN.md#L56), [cases.csv:L12,L15](cases.csv#L12)) | **DECIDED + GATED**：世界UI主动退出比赛，清输入并恢复玩家设置；实际退出路径待验。([01_GAME_DESIGN.md:L59-L65](../../01_GAME_DESIGN.md#L59-L65), [states.md:L11,L46](../Terminology/states.md#L11)) | **DECIDED + GATED**：平台Respawn清理比赛/座位状态；G1要求正常行走与原身高恢复，具体恢复及回合状态须验证。([01_GAME_DESIGN.md:L62-L63](../../01_GAME_DESIGN.md#L62-L63), [G1:L18](../../05_ACCEPTANCE_TESTS.md#L18)) | **DECIDED**：VR原生Comfort Turning不适用于Desktop；桌面观察/参考方向走鼠标路径。([01_GAME_DESIGN.md:L45-L49](../../01_GAME_DESIGN.md#L45-L49)) |

> **Space澄清：** 当前设计把桌面Space列为Jump/道具动作；旧稿中的Space刹车已废止，且不添加独立冲刺键。([01_GAME_DESIGN.md:L49-L56](../../01_GAME_DESIGN.md#L49-L56), [00_SCOPE_DECISIONS.md:L31](../../00_SCOPE_DECISIONS.md#L31))

## 2. 坐在Station按Jump：预期行为与T04现状

设计预期是：

1. **不起跳**：比赛中实际跳跃被禁用；
2. **不退座**：Station的 `Disable Station Exit` 是主防护；将JumpImpulse置零仅是辅助措施；
3. **只触发一次**：按下的上升沿触发一次，保持按住不重复；释放再按才可能再次触发；
4. **有道具时**：请求使用当前单槽道具，只有权威确认后才改变持有状态；
5. **无道具时**：最多显示/触发一次轻提示，不执行隐藏效果。

依据：[`01_GAME_DESIGN.md:L51-L61`](../../01_GAME_DESIGN.md#L51-L61)、[`P08 README:L26`](README.md#L26)、[`P08 cases.csv:L16-L18`](cases.csv#L16-L18)、[`states.md:L54-L58`](../Terminology/states.md#L54-L58)。

**T04原型说明：** 当前T04原型阶段尚无道具系统可供该动作调用（本任务给定的原型现状）；因此现在坐在Station按桌面Space/JUMP看不到道具效果是预期，不应误报成道具功能故障。但Jump“不跳/不退座”和按住不重复仍是需单独验证的门禁，不可把“没看到效果”当成通过。T04证据显示Jump单击/长按及不退座尚未测试，且P08的J-01/J-02/J-03都处于 `NOT_RUN`。无道具轻提示是设计要求，但当前T04原型尚未提供。([T04 README:L17-L21](../../Evidence/T04/README.md#L17-L21), [PC结果:L18-L20](../../Evidence/T04/pc_results.md#L18-L20), [VR清单:L26-L28](../../Evidence/T04/vr_checklist_for_T05.md#L26-L28))

## 3. 退出、KO、回合结束与设置恢复

以下区分“清输入/座位/比赛状态”与“恢复玩家入场前设置”。恢复预期不是实测结果；每条实际路径仍需T04/T05验证。设计列出的设置范围是身高、移动、跳跃等，不覆盖未授权覆盖玩家其他偏好。([`01_GAME_DESIGN.md:L60-L65`](../../01_GAME_DESIGN.md#L60-L65), [`P08 README:L19,L28`](README.md#L19))

| 路径 | 文档明确的清理/恢复预期 | 状态与依据 |
|---|---|---|
| 主动“退出比赛”按钮 | 世界UI可主动退出；清除输入/当前动作状态；恢复身高、移动、跳跃等入场前保存设置。 | **GATED**：设计已定，坐姿/非坐姿分支、恢复值及Station释放尚未通过复测。([01_GAME_DESIGN.md:L56,L62-L65](../../01_GAME_DESIGN.md#L56-L65), [cases.csv:L20](cases.csv#L20), [T04 VR清单:L29-L30](../../Evidence/T04/vr_checklist_for_T05.md#L29-L30)) |
| 回合结束/正常重置 | 清输入并恢复身高、移动、跳跃等玩家设置。 | **GATED**：明确列为恢复路径；运行恢复结果未确认。([01_GAME_DESIGN.md:L63](../../01_GAME_DESIGN.md#L63), [cases.csv:L22](cases.csv#L22), [T04 README:L17-L18](../../Evidence/T04/README.md#L17-L18)) |
| KO/被吞 | 清掉输入/按键状态；KO不是主动退赛，玩家仍是本局参与者并等待重生。设计未说KO时是否恢复/保留玩家原有身高、移动、跳跃设置。 | **OPEN**（行为范围未定）+ **GATED**（清理尚未验证）。([01_GAME_DESIGN.md:L25-L30,L56](../../01_GAME_DESIGN.md#L25-L30), [states.md:L11-L12,L42-L45](../Terminology/states.md#L11-L12), [cases.csv:L19](cases.csv#L19)) |
| VRChat平台Respawn | `OnPlayerRespawn`清理座位及比赛状态；G1明确要求恢复正常行走与原身高。Jump设置和Respawn后仍留在本局、重入或退回大厅的规则，文档没有完全对齐。 | **GATED + OPEN**：具体路径未测试；G1比设计书恢复清单多写了Respawn后的正常行走/原身高。([01_GAME_DESIGN.md:L62-L63](../../01_GAME_DESIGN.md#L62-L63), [G1:L18](../../05_ACCEPTANCE_TESTS.md#L18), [states.md:L11](../Terminology/states.md#L11), [cases.csv:L21](cases.csv#L21)) |
| 异常复位/Recovery reset | 清输入；恢复身高、移动、跳跃等设置；如不能可靠恢复，安全取消而不锁死玩家。 | **GATED**：期望写明，恢复责任/实际值仍需测试。([01_GAME_DESIGN.md:L62-L64](../../01_GAME_DESIGN.md#L62-L64), [cases.csv:L23](cases.csv#L23), [P08 README:L28](README.md#L28)) |

### JumpImpulse 读值边界

`Docs/Evidence/T04/README.md`记载当前环境世界加载和入座两次 `GetJumpImpulse()` 都读到 `0`，所以**不能把该读回值当作玩家原跳跃设置的保存/恢复基准**；该记录只有两次观察，不是证明它每个时刻都恒为0。当前T04恢复策略使用文档所述的 `VRCWorld` prefab默认值3并要求恢复后重读核对；这表示恢复到该prefab基线，不等于已证明恢复了某位玩家进入前的自定义值。([T04 README:L7-L10,L21](../../Evidence/T04/README.md#L7-L10), [PC结果:L7-L12,L18](../../Evidence/T04/pc_results.md#L7-L12))

## 4. 尚未统一 / 需要用户决定（OPEN清单）

以下只列设计文本尚未说清的决定；每项均提供选项和建议，**建议不代表用户决定**。

1. **无道具轻提示的形式与节流**（对应表内 Jump）。选项：A. 一次短文字提示；B. 轻微触觉反馈；C. 短音效（也可先仅选一种）。**建议：**先用一次短文字提示，避免在VR里叠加声音/振动；长按始终不重复。([01_GAME_DESIGN.md:L55-L57](../../01_GAME_DESIGN.md#L55-L57), [states.md:L54-L56](../Terminology/states.md#L54-L56))
2. **Station内能否自由看向四周**（PCVR A观察）。选项：A. HMD自由转头，完全独立于动力参考；B. 只允许有限转头，超限提示回中；C. 另设重新校准/对齐动作。**建议：**优先A，保持A动力参考不随HMD转动，并由T05真人确认舒适和视野。([01_GAME_DESIGN.md:L38,L46](../../01_GAME_DESIGN.md#L38), [G1:L17](../../05_ACCEPTANCE_TESTS.md#L17), [station_decision.md:L29-L35](../../Evidence/T04/station_decision.md#L29-L35))
3. **B/Desktop观察朝向如何影响移动参考**。选项：A. 右摇杆/鼠标转动控制架，HMD/鼠标观察不单独改球路；B. 视线方向直接决定球路；C. 显式切换“观察/转向参考”模式。**建议：**采用A作为继续验证的默认草案，与B模式“自由观察不自动改路径”的方向一致，但先用教学明确并经T04/T05验证。([01_GAME_DESIGN.md:L44-L49](../../01_GAME_DESIGN.md#L44-L49), [onboarding.md:L30-L34,L41-L43](../Terminology/onboarding.md#L30-L43))
4. **原生Comfort Turning与InputLookHorizontal/right-stick的冲突优先级**。选项：A. 保留原生转向、游戏轴输入不再叠加；B. 游戏轴输入优先并关闭原生转向；C. 允许合并但明确去重/限幅。**建议：**未经验证不合并两个转向源；先明确单一有效yaw来源，开/关原生设置分别测后再选。([01_GAME_DESIGN.md:L47-L48](../../01_GAME_DESIGN.md#L47-L48), [cases.csv:L10-L11](cases.csv#L10-L11), [G1:L16](../../05_ACCEPTANCE_TESTS.md#L16))
5. **各模式的Grip分配，尤其PCVR A的冲突措辞**。选项：A. 采纳A模式具体操作段的`InputGrab`抓活动件，B/Desktop不绑定Grip；B. 采纳“A不占Grip”字面含义，A模式抓取另行确定；C. 所有模式的Grip都先留空，待统一交互设计。**建议：**优先A（它同时符合onboarding与P08抓取用例），但先明确`InputGrab`是否就是这里所说的Grip；不得将两句现状合并成已冻结绑定。([01_GAME_DESIGN.md:L35-L37,L54](../../01_GAME_DESIGN.md#L35-L54), [onboarding.md:L24](../Terminology/onboarding.md#L24), [cases.csv:L5-L6](cases.csv#L5-L6))
6. **KO期间设置保留策略**。选项：A. KO时立即恢复所有入场设置；B. 保留本局控制设置、仅清输入，待重生后继续；C. KO时只恢复Jump/移动的一部分。**建议：**KO是本局暂时状态而非退出，先清输入并保留入场时设置快照，待真正离赛/回合终止再恢复；如KO触发离座则另行按T05验证。([01_GAME_DESIGN.md:L25-L30,L56,L63](../../01_GAME_DESIGN.md#L25-L30), [states.md:L11-L12](../Terminology/states.md#L11-L12))
7. **Respawn后的恢复范围与比赛名单**。选项：A. 还在本局，恢复正常行走/原身高后继续；B. 恢复玩家设置并退回大厅；C. 先安全重置，再让玩家明确选择重入/观战。**建议：**Respawn与主动退出分开处理，至少恢复G1明确要求的行走/原身高；名单/重入行为在T04/T05和用户决定前不推断。([01_GAME_DESIGN.md:L62-L65](../../01_GAME_DESIGN.md#L62-L65), [G1:L18-L19](../../05_ACCEPTANCE_TESTS.md#L18-L19), [states.md:L11-L12](../Terminology/states.md#L11-L12))
8. **原JumpImpulse无法从getter取得时的保存来源**。选项：A. 找到受支持的入场前设置快照来源；B. 明确以VRCWorld prefab默认3为回退基线；C. 无法可靠保存时阻止入场/退出并提示。**建议：**不把读值0冒充用户原值；优先确认可用快照，若只能恢复3则明确其为world基线、记录reread并让用户认可该回退。([01_GAME_DESIGN.md:L60-L64](../../01_GAME_DESIGN.md#L60-L64), [T04 README:L7-L10](../../Evidence/T04/README.md#L7-L10), [pc_results.md:L8-L12](../../Evidence/T04/pc_results.md#L8-L12))

**OPEN项数量：8。** 其中部分需用户选择，部分还要先有T04/T05证据；本页不代替决定或验收。

## 5. 已发现的设计书内部不一致 / 实证缺口

- **Respawn恢复范围未对齐：** `01_GAME_DESIGN.md:L62`要求`OnPlayerRespawn`清理座位/比赛状态，`L63`列出的设置恢复路径只有退出、回合结束、异常复位；但`05_ACCEPTANCE_TESTS.md:G1:L18`明确把VRChat Respawn后的正常行走和原身高恢复列为要求。属于恢复路径清单遗漏/不一致，需统一语义；本页不自行选定名单去留规则。
- **JumpImpulse的“保存原值”与当前读值不兼容：** 设计书要求保存/恢复原JumpImpulse（`01_GAME_DESIGN.md:L60-L64`），但T04证据中启动、入座getter读值都是0，不能当作用户原值；T04当前使用VRCWorld prefab默认3作为恢复基线。`3`不是该玩家原设置的实测证明，需解决快照来源或明确回退语义。([T04 README:L7-L10,L21](../../Evidence/T04/README.md#L7-L10), [pc_results.md:L7-L12](../../Evidence/T04/pc_results.md#L7-L12))
- **Grip映射存在直接措辞冲突：** `01_GAME_DESIGN.md:L36`要求靠近活动件后通过`InputGrab`进入抓取，而`L54`写“A不占Grip”；`onboarding.md:L24`与P08 A-04/A-05又按抓取动作设计。若两处术语本来不同，文件没有定义其差别。需统一后才能冻结A模式抓取绑定。([01_GAME_DESIGN.md:L35-L37,L54](../../01_GAME_DESIGN.md#L35-L54), [onboarding.md:L24](../Terminology/onboarding.md#L24), [cases.csv:L5-L6](cases.csv#L5-L6))
- **其他输入映射属于未定/门禁，不作自相矛盾判定：** B/PC视线与移动参考需要统一教学；Comfort Turning/InputLookHorizontal需验证优先级；均已列入OPEN清单。([01_GAME_DESIGN.md:L45-L48](../../01_GAME_DESIGN.md#L45-L48))

## 6. 验收与安全边界

本页是草案说明，不是运行结果。P08测试案例目前预填 `NOT_RUN`；T04本轮记录确认Jump单击/长按、不退座及退出恢复仍待验证；T05真人VR/舒适性尚未完成。不得据此宣称真机兼容、舒适、稳定地平线或任何舒适性PASS。([P08 README:L3,L32](README.md#L3), [T04 README:L15-L23](../../Evidence/T04/README.md#L15-L23), [human_safety.md:L1-L3,L19-L32](human_safety.md#L1-L3))
