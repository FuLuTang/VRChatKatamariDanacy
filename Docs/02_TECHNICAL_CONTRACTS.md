# 02 技术契约与实现边界

此处是设计契约，不是已经通过Udon编译的运行时代码。
代码Agent须在目标Unity/SDK版本编译；编辑器C#、UdonSharp、Blender Python互不混用。

## 1. 坐标与尺寸：先消除架构矛盾
同一局只有一个共享地图坐标系。碰撞、拾取、吞噬、交分均在此坐标系计算。
可以把整张地图在制作时统一缩放；不能每个客户端按自己的成长独立缩地图。
Avatar眼高、追尾偏移、球的语义名称是表现层，不改变公共地图的物品坐标。
首版生长范围有上限，通过交分重置，避免无限体积。
0.2–5米是用户选择的表现工作区间，不当成VRChat绝对技术上限。
选择眼高应主要保证能看见球和前路；不要求脚踩地。
如果将来采用私人微缩视图，必须明确整套世界到视图变换；该复杂模式不在首版。

命名严格区分：
- coreRadiusWorldM：共享坐标中的核心半径。
- collisionRadiusWorldM：PVP与环境简化判定半径。
- semanticDiameter：UI叙事尺寸，可与物理米不同。
- avatarEyeHeightM：玩家眼高。
- earnedVolumeUnits / bankedScore：游戏价值账本，不从渲染Mesh反推。
所有接口禁止一个字段同时表示半径和直径。

## 2. 本地球分层
BallPhysicsRoot：位置、平面朝向、速度、统一碰撞。
BallVisualRollRoot：只有可视滚动旋转，localScale保持1。
CoreMesh：单独缩放球芯。
AttachmentRoot：真实吸附物，记录局部位姿，不因为球芯长大而整体放大所有物件。
FollowRig：独立Station、操作参考架、HUD；绝不成为滚动Mesh的孩子。
RemoteBallProxy：位置/尺寸/状态/少量外观摘要重建。
不要让动态MeshCollider成为所有附着物的真实刚体外壳。

## 3. 成长、吸附与凹凸滚动
### 3.1 三种不同的问题
价值/体积累加决定可拾取门槛与基础体积。
视觉树保留吸到什么、吸在哪。
滚动代理决定可视凹凸和后续受控接触效果。
历史专利存在显示、碰撞和滚动模型分离及径向点思想；本项目下列公式为自己的工程近似。[S05]

### 3.2 建议算法
初期N=32个固定方向采样点，可加入最多4个长物件极值候选。均为待性能验证的原型值。
每个方向d_i和长度l_i表示球的稀疏外壳；长物件提供预烘焙端点，不实时扫描全部顶点。
吸附时：
1. 从物体简化代理取端点/包围点，转到BallVisualRollRoot局部坐标。
2. 更新附近采样方向的支持长度；新增长杆极值候选。
3. 核心半径由确认体积更新：r = cube_root(3V/(4π))，V是调校过的有效体积。
4. 重新压低已被球芯覆盖的突出量；相对突出量max(0,l_i-r)会随核心长大自然减小。
5. 旧视觉物件被球芯遮住后可以回收，但它的体积留在账本。
6. 物件掉落时移除该物体的贡献，并重算受影响采样。不能只持续max，否则杆永远不缩短。
7. 交分/KO导致大幅缩小时，按新账本重建代理/外观，不能把一切仅缩放一下。

接触近似：
对地面向上法线n，支持高度h(-n)=max(r,max_i dot(-n,Q*p_i))。
Q为球的视觉旋转，p_i为代理点。平面接触可据此估计球芯离地高度。
该公式只给出支持距离，不是完整动力学解法：扫掠、坡道、台阶、多接触、滚动角速度还需实现和测试。
最大突出量、单步抬升、速度扰动均设上限，避免长棍把球弹上天。

### 3.3 首版推进顺序
A. 球形扫掠/碰撞 + 可视正确滚动。
B. 径向代理驱动球的凹凸、音效、振动，镜头不跟着颠。
C. 如需要代理实际影响运动，固定上限并让可影响PVP的外形摘要也进入共享状态。
远端随机装饰不得决定物理。直到C通过一致性测试，PVP统一使用球形碰撞半径。

### 3.4 视觉吸附
用确认时物体世界位姿转换为球的局部位姿。
磁铁可额外插入短飞行动画，但最后局部姿态稳定。
大球增长不能把已附着杯子同步放大成楼。
物品ID区分定义与实例；远端Hash使用ballSeed+实例ID+世代+attachSequence，避免重复模型重叠。
保留本地最近显著物件；埋藏物件合入球芯视觉。不能无限保留每个Renderer。
拾取Collider在收走后退出地图判定；本地装饰不保留可争抢Collider。

## 4. 数据定义
### ItemDefinition：同一种东西
definitionId、visualPrefabRef、pickupDiameterWorldM、gainVolumeUnits、
collisionProxyType、surfaceClass、extremePoints、canWakePhysics、supportPolicy、
localVisualLOD、remoteProxyVariant、licenseManifestId。
不是网络实体；编辑器可使用JSON/ScriptableObject，构建时烘焙为Udon可用数组/引用。
不要让Udon运行时依赖编辑器API或随意读取磁盘JSON。

### ItemInstance：地图里具体某一个
spawnId、definitionId、initialPose、zoneId、sizeTier、supportParentId、
spawnGeneration、state、stateRevision、respawnAt。
稳定spawnId独立于数组顺序；改图后build/version与校验值一起改变。
状态可为Available、ClaimPending(仅本地)、Attached、Removed、
Falling、Settled、RespawnPending、Disabled。

### BallPublicState
slotId、roundId、sessionGeneration、position、velocity、moveHeading、
collisionRadiusWorldM、confirmedVolumeUnits、bankedScore、
heldPowerup、activePowerup、powerupEndsAt、lifeState、protectionEndsAt、
cosmeticId、proxySeed、recentAttachmentSummary、stateRevision。
不把本地预测收益写成已确认收益。

### MatchState / AuthorityState
protocolVersion、worldBuildId、roundId、authorityEpoch、commitSequence、
phase、phaseEndsAt、roster、mapId、mapSeed、rulesetId、
bankAnchorIndex、nextBankMoveAt、committedSnapshotRevision。
MatchAuthority状态与玩家球表现Owner分开。
玩法裁决由裁决对象Owner执行，但不视作可信服务器或完整反作弊。

## 5. 一件物品两人抢的协议
唯一对象键：(roundId,spawnId,spawnGeneration)。
每请求具有(playerSessionGeneration,requestSequence)幂等键。
客户端本地先展示暂定吸附；不能让暂定收益参加交分或吞噬。
按可调间隔批量提交itemId与必要的接触证据，不能每个粒子发事件。
裁决端验证名单、生命状态、有效尺寸、物品世代、合理距离/速度、道具效果与可用状态。
先到有效请求作为首版裁决；不信任客户端自报时间来推翻已提交归属。
更公平的延迟窗口/回溯是后续方案，要定义等待窗口和不可篡改证据才启用。
确认产生单调commitSequence及唯一winner；失败方撤销视觉预测和预测质量。

持久结果写同步状态/快照；网络事件只是请求、响应加速和临时特效，不作为唯一账本。[S11][S13]
若请求发送者可由SDK上下文获得，使用实际调用方；不只相信请求里传来的playerId。
否则通过绑定Owner的请求通道确认来源。准确API由编译验证任务决定，禁止编造API名称。

## 6. 物件重力、父子关系与再生
- 普通静态物件：不运行Rigidbody和逐帧Update。
- 可碰倒物：只在近处受有效事件后借用限额动态对象池；静止后睡眠/烘回静态。
- 悬挂/堆叠物：用supportParentId。支撑物被卷走时按策略一起收取、坠落或固定展示。
- 一起收取只允许全部子件也满足门槛，并明确是否计入父物价值；严禁重复收益。
- 坠落若改变可争抢的位置，必须由裁决方确定落点/运动摘要，或同步少量重要动态物；
  不能各客户端不同PhysX落点，却按不同坐标抢同一ID。
- 首版推荐支撑物移除后给子物体预计算合法落点+统一开始时间，客户端播放坠落。
- 气球/装饰等明确悬浮类型可以浮空；不是把缺失支撑逻辑当特效。
- 重生资源按zone/tier预算补充，近球/视野内可延后并给生成预告。
- 使用新spawnGeneration；旧重试不得领取新一代物品。
- 被撞掉的资源可用少数通用碎团表示；价值守恒，不逐个联网恢复历史一千个物品。
- 每局重置恢复初始世代/地图seed并清空所有临时状态；开始下局前完整性校验。

## 7. 成绩、交分与PVP事务
交分是一笔事务：减少未交存资源、增加安全积分、重置成长、更新状态。
同一bankRequestId只结算一次，超时重试不能双倍计分。
被吞是一笔事务：被吞者清理/KO，攻击者受限获得收益，双方生命/道具/保护状态更新。
相撞双方不能各自宣布对方被吞；裁决只产出一份结果。
回合截止以裁决时间规则为准；在截止后收到的过期请求如何处理必须固定。
建议截止进入Settling，仅处理已收到且有效的请求，不接受事后伪造“早已接触”。

## 8. 掉线接管与迟到加入
快照包含commitSequence、物件状态分块及版本、球确认账本、回合终点。
分块快照只有在完整同版本收到后才能切换为committed；不能把不同版本混拼。
Owner离开后进入Recovery，新Owner基于最近完整快照增加authorityEpoch。
玩家重发未确认请求；幂等键过滤重复；旧epoch包不得修改新状态。
无法可靠恢复就取消本局并返回大厅，不猜奖品或购买记录。
迟到玩家先读完整快照与名单，作为观众/排队者；不靠历史事件重放恢复世界。
持久外观解锁不受本局恢复影响。

## 9. 性能与网络
PC-only仍要限制CPU、Udon、Draw Call、音源、透明特效和网络开销。
网格/区域索引只扫描附近候选；静态ID/初始位姿离线烘焙。
使用固定数组、复用缓冲、事件驱动、分帧回收，避免每小物件独立Udon Update。[S16]
同步频率是实验参数，不能把RequestSerialization每帧调用等同真实网络60Hz。[S12]
记录OnPostSerialization byteCount、IsClogged、状态往返延迟及重试。
90Hz头显整帧预算约11.1ms；其中还包括VRChat与玩家Avatar，不能全给这个游戏。
性能报告必须列硬件、头显刷新率、分辨率、Avatar条件、4球同时高速吸附的P95帧时间。
本包不声称达到任何FPS或既定联网延迟。
