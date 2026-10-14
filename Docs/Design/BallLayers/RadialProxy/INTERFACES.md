# T50 RadialProxy 边界（DRAFT）

接口未冻结；32方向/4候选都是待性能验证原型值，不代表正式预算。

## 输入 / 输出

- `AttachmentProxy.item_id` 是上游已确认且稳定的实例身份字符串，调用方须编码稳定spawnId及世代，不能给数组序号/Definition ID；预烘焙世界代理端点不在此实时扫描网格。
- `RadialConfig.direction_count` 当前硬为32个固定Fibonacci方向；`max_extreme_candidates`范围0..4，默认4。候选满额时拒绝新候选；不会隐式替换或丢贡献。
- 世界端点依球的位置与四元数转换到局部。host Python使用显式Hamilton代数。`Quaternion.AngleAxis(+90°,Vector3.up)*Vector3.forward`、逆变换、局部到世界方向须由Unity 2022.3对象实测比对；此门未过，不能直连RuntimeDraft到组件。
- `direction_length_to_point(d,l)`要求单位方向、非负有限米值并返回 `p=(dx*l,dy*l,dz*l)`；点按同一局部/世界坐标帧使用。
- 输出方向数组、对应支持长度、突出量 `max(0,l_i-r)`、候选稳定键列表。新增只局部重算；detach对受影响方向按当前所有剩余端点重新取max；完整rebuild按整个已确认ledger重建。

## 失败与原子性

- 无效ID/重复ID、空端点、非法四元数/世界坐标、中间溢出、错误方向/长度、候选超额、非有限半径都明确拒绝。
- `attach`先预计算受影响的新支持；`detach`先预计算移除后的支持；`rebuild`先建临时对象。失败前快照无改动。
- `detach`不处理物件落到地图哪里，也不决策拾取/记分；仅删除当前壳贡献。

## 坐标/移植边界

Unity 2022.3 官方文档声明LH轴系与Cross左手规则；A套Python四元数是host参照，不是Unity API对拍。方向探针值记录前禁止仅改注释或翻符号。Unity行为/Udon可移植性/性能均未验证。
