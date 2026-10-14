# C 套来源证据：表现与跟随

研究/访问日期：2026-10-14 UTC。范围为本地仓库资料和本机 HTTP 客户端读取的公开网页；不表示运行或逆向原作。原版游戏源码未取得。

## 源码可用性与证据分级

- **未取得《塊魂》或《Once Upon A KATAMARI》的真实游戏源码。** 本地检查到的是本项目自己的设计档案、离线 Python 草稿和单测；没有原版可构建源码或能证明内部实现的证据。这个结论仅限本仓库检查，不声称公开世界不存在源码。
- 本机标准库 HTTP 客户端成功读取本文件列出网页；只读取网页文字/元数据，未下载或执行代码。专利文本是历史技术披露，不是游戏源代码，不证明任何新版实际沿用。
- 标签：**本地项目事实**指仓库现有文档/代码；**公开来源事实**只限来源明确写出的信息；**项目工程选择**是本项目自己的设计，不应倒写为原作事实；**未知**不以推测填补。

## 本地资料（先核对）

| 来源 | 版本/日期 | 可核对内容 | 适用性与限制 |
|---|---|---|---|
| [`Docs/02_TECHNICAL_CONTRACTS.md`](../../../../02_TECHNICAL_CONTRACTS.md) §2、§3 | 项目 v0.3 契约，非正式冻结接口 | BallPhysicsRoot 拥有位置/平面朝向；BallVisualRollRoot 只拥有可视自转且 scale 恒 1；FollowRig 与镜头/HUD 分离；远端装饰近似，不影响玩法判定。 | 是本项目当前边界，不是原作实现证据；T06 尚未冻结接口。 |
| [`Docs/Planning/BALL_WORKFLOW_REPLAN.md`](../../../../Planning/BALL_WORKFLOW_REPLAN.md) §C | 2026-10-14 | 明确 T53/T55/T56 的责任、VisualRoll 需要独立接触速度 oracle、FollowRig 从 T46 抽离、T56 接线等待 T51。 | 工作流要求，不证明代码或验收完成。 |
| [`Docs/Design/BallLayers/VisualRoll/visual_roll.py`](../visual_roll.py) 与 [`Docs/tests/ball_visual_roll/test_visual_roll.py`](../../../../tests/ball_visual_roll/test_visual_roll.py) | 本地旧草稿；当前源码 | 单位法线、正半径、位移距离生成轴角四元数并左乘累计；测试直接检查 `displacement × normal` 轴方向和整周角度等式。 | 轴测试只是复述实现公式，没独立验证接触点不滑动；含法向位移时角度取全位移而非切向分量；没有初始非法四元数/瞬移/长期误差覆盖。 |
| [`Docs/Design/BallMotion/ball_motion.py`](../../../BallMotion/ball_motion.py) `FollowRig` 与 [`Docs/tests/ball_motion/test_ball_motion.py`](../../../../tests/ball_motion/test_ball_motion.py) | 旧 T46 合并式宿主草稿 | 高度与 yaw 指数滤波；roll/pitch 输出置零；测试检查过滤后跟随与忽略 roll/pitch。 | 它在 BallMotion 模块中，不是独立 T55；只可提取作基线，不得改动或把旧测试当作充分覆盖。未见瞬移重置/眼高范围约束测试。 |
| [`Docs/Design/BallLayers/Attachment/attachment.py`](../../Attachment/attachment.py) | 本地 T51 草稿 | 附着记录里存在实例、世代及由本地 `_next_sequence` 递增的序号；`attachment_hash` 可确定性摘要。 | T56 可先消费合成“已确认摘要”离线模拟；T51 当前本地序号不是可跨端确认身份，不能当权威 attachSequence。 |
| `Docs/Design/BallLayers/RemoteProxy/` 与 `FollowRig/` | 本次路径检查 | 两个独立模块目录不存在。 | 说明目前无 T55/T56 独立实现可审查；不代表没有其他未检出形式的材料。 |
| [`Docs/08_SOURCES_AND_OPEN_QUESTIONS.md`](../../../../08_SOURCES_AND_OPEN_QUESTIONS.md) S01–S05 | 仓库来源索引，researchDate 2026-10-10 | 收录官方作品页、官方 Q&A、公告、制作人访谈与专利 URL，旧索引已标注专利限制。 | 索引记录不等于这次已验证每个页面；下列才是本次实际访问结果。 |

## 公开来源核查

### US7402104B2：历史专利，不是源码

- 来源：[Google Patents — US7402104B2](https://patents.google.com/patent/US7402104B2/en)
- 本机 HTTP 访问：2026-10-14 UTC，HTTP 200，最终 URL 为上列地址。页面书目将其题为 *Game performing method, game apparatus, storage medium, data signal and program*；列出申请日 2004-09-20、授权日 2008-07-22，发明人 Masatoshi Ogita、Kazumi Yamabe、Keita Takahashi，申请人 Namco Bandai Games Inc.。这些是专利页面书目资料，不等于产品版本号。
- 有限摘录（摘要/权利要求）：“setting a plurality of first displacement points capable of displacing only in predetermined radial directions”；并称“controlling the rolling … based on rolling surface reference points”。说明书关于滚动的段落解释这些参考点代表滚动时与地面接触的点，并谈及第二位移点带来的起伏感。
- 对 C 套可支持的有限结论：历史文件把滚动控制与一组滚动面参考点联系起来，提供“几何接触点/滚动参考”作为研究背景。它没有提供本项目 T53 的球体无滑动公式/轴符号，也不证明新版采用该方案。
- 适用时间/版本：专利文件对应其历史申请/授权文本；无法从该文件确认其与任何具体零售版的实现对应、实际部署或后续变更。不可标为原版源码，也不可推定 2025 新版沿用。本文不作专利有效性、许可或侵权法律结论。

### 官方产品页与玩法 Q&A

- 来源：[Bandai Namco — Once Upon A KATAMARI](https://www.bandainamcoent.com/games/once-upon-a-katamari)
- 访问：2026-10-14 UTC，HTTP 200。官方页面确认作品身份并展示作品宣传信息；未找到滚动数学、FollowRig、渲染代理或网络表现实现说明。网页未给出可用于此处的内部算法版本号。
- 来源：[Bandai Namco Europe — What do you need to know about Once Upon A KATAMARI?](https://en.bandainamcoent.eu/katamari/news/what-do-you-need-know-about-once-upon-katamari)
- 访问：2026-10-14 UTC，HTTP 200。页面描述游戏于 2025-10-24 发售，并在 Q&A 中列出 KatamariBall 和道具（Magnet、Timer、Rocket）。这些是公开产品/玩法描述，不含滚动角轴、镜头跟随、远端装饰重建算法。适用作品为 2025 年零售作品；没有在页面核实补丁/构建号。

### 制作人访谈

- 来源：[Pocket Tactics — Once Upon A Katamari dev tells us why its “unique charm” was worth waiting for](https://www.pockettactics.com/once-upon-a-katamari/interview)
- 访问：2026-10-14 UTC，HTTP 200；文章元数据 `datePublished` 为 2025-10-25，标题/正文介绍受访者为制作人 Ryo Ishida。
- 有限摘录（受访者谈 KatamariBall）：“we designed a shared space that we call the 'point conversion area'”，继而称其增加何时转换尺寸为分数、是否干扰其他玩家的策略层。此引文只谈多人模式设计；全文无可用于验证本套旋转/镜头/远端渲染内部技术的内容。
- 适用作品：访谈讨论 2025 年《Once Upon A KATAMARI》；不提供源代码或算法说明，不支持专利算法被新版复用的结论。

## 对照结论

1. **可确定的本项目约束**：呈现自转隔离于玩法朝向；镜头/FollowRig 不继承球滚转；远端装饰只读摘要且不参与玩法碰撞/账本（项目契约）。
2. **公开原作证据**：历史专利提及滚动面参考点；官方与采访资料说明作品及部分玩法，但没有核实 T53/T55/T56 的内部方法。
3. **仍未知**：新版到底如何算滚动、是否跟随/滤镜参数、远端装饰如何去重与限额；没有源码、访谈证据或本次可复现行为观察来回答。
4. **项目自定草案**：下文接口方向、接触点 oracle、FollowRig 瞬移处理、renderer 预算及摘要容错是工程策略，不是对原作的还原主张。

## 2026-10-14离线复核补充

- 原版源码仍**未取得**。本机HTTP客户端在`2026-10-14T00:37:59Z`访问GitHub REST repository-search元数据，查询`Katamari Damacy original source code`与`Once Upon A KATAMARI source code`均HTTP 200、`total_count=0`：[GitHub repo search](https://api.github.com/search/repositories)。这是有限元数据检索，不证明所有网站/私有库均无源码；没有下载仓库或运行代码。
- 同次HTTP客户端读取Unity 2022.3官方页面，均HTTP 200：[Vector3.Cross](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Vector3.Cross.html)页面说明可用“left hand rule”确定结果方向；[Rotation and orientation in Unity](https://docs.unity3d.com/2022.3/Documentation/Manual/QuaternionAndEulerRotationsInUnity.html)写明“Unity uses a left-handed coordinate system”。因此本模块Python采用的传统右手主机代数不直接视作Unity已对拍，RuntimeDraft不调用`Vector3.Cross`，但Unity Quaternion行为仍待独立方向箭头及接触点速度验证。
- 同次读取[UdonSharp文档首页](https://udonsharp.docs.vrchat.com/) HTTP 200；页面自述其不完全符合任何C#语言规范且特性支持有限。这里只作为一般兼容风险，不代表核实了Windows项目安装的UdonSharp版本。
- 没有引入“专利就是源码”或“新版沿用专利”的说法；该研究也没有下载、执行第三方代码。
