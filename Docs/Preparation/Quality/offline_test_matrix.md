# P03 离线质量门禁矩阵（准备稿）

状态：PREPARATION / NOT_RUN。此矩阵是测试设计与覆盖索引，不是单项测试的当前运行报告。依据 v0.3 契约及 [prototype_defaults.json](../../config/prototype_defaults.json) 的临时参数；不冻结 T06，不代表 Unity/Udon 编译、网络或真人 PASS。数值只作为可参数化的测试输入，不能当原作实测数据。

## Host-only 测试映射与后续证据

下表列出当前仓库中实际存在的纯 Python 参考模型/测试，供矩阵追溯。路径存在不等于本轮已运行，也不等于 Unity/Udon 实现或验收。交叉映射到的球层/协议用例仍是 host-side 模拟。

| 领域 | 已有 host-only 模型/测试（仅列可核实覆盖） | 性质与边界 | 后续必须补充的证据 |
|---|---|---|---|
| 输入松手、Jump边沿、菜单清输入 | [test_intent.py](../../tests/control_intent/test_intent.py#L8-L60) 覆盖来源序列一致、Jump上升沿/去抖、坐姿输入语义及菜单清输入；不模拟真实设备 | 纯帧序列与 intent 模型；没有真实追踪丢失、手柄、Unity移动速度残留或VRChat平台输入证据 | Unity平台实验、PCVR A/B/Desktop、真人手柄/菜单；G1仍需平台门禁 |
| 退出/Respawn/KO状态 | [test_ledger.py](../../tests/ledger/test_ledger.py#L50-L83) 覆盖迟到加入快照、KO/Respawn生命周期与状态迁移；[protocol model tests](../../tests/prep_protocol/test_model.py#L175-L242) 覆盖KO、截止及快照恢复/取消 | host-only 参考状态机；不覆盖实际玩家入场设置保存/恢复、Station、InputJump 或 Udon 回调 | Unity/ClientSim以及用户实测退出、Station、Respawn 与设置恢复 |
| 物品世代、重复请求、争抢 | [protocol model tests](../../tests/prep_protocol/test_model.py#L52-L108) 覆盖幂等请求、旧世代/epoch与争抢；[test_pickup_absorb.py](../../tests/pickup_absorb/test_pickup_absorb.py#L18-L63) 覆盖吸附门槛、重复计数、权威胜者/预测回滚和半径界限 | host-only reducer/参考算法；不等同Udon或多人网络时序 | Udon实现后的双/多客户端重复、乱序、并发争抢及状态日志 |
| 预测确认/回滚 | [test_ledger.py](../../tests/ledger/test_ledger.py#L22-L48) 与 [test_pickup_absorb.py](../../tests/pickup_absorb/test_pickup_absorb.py#L30-L63) 覆盖重复提交、失败方回滚及预测值不可交分 | 纯内存模型；不是实际视觉/权威状态联调 | Unity视觉与权威状态联调、延迟/拥塞下回滚表现 |
| 交分、KO、截止竞争 | [protocol model tests](../../tests/prep_protocol/test_model.py#L130-L232) 覆盖交分幂等、KO价值边界、截止与同tick顺序；[test_ledger.py](../../tests/ledger/test_ledger.py#L22-L75) 覆盖提交、并列、预测回滚及生命周期 | 顺序由参考规则驱动；公平阈值/具体接口未冻结，不证明网络裁决成立 | Unity裁决实现、双/四客户端竞争与恢复日志；规则参数仍待相应决定/接口评审 |
| 迁移快照、世代与乱序 | [protocol model tests](../../tests/prep_protocol/test_model.py#L233-L331) 覆盖完整/缺失/不完整/混版本快照、epoch、数值与请求缓存边界；[test_remote_proxy.py](../../tests/ball_remote_proxy/test_remote_proxy.py#L31-L185) 覆盖host-only摘要重建/修订 | 仅参考数据模型。RemoteProxy使用合成测试身份；生产 `ballSeed` 语义、Udon接线与实际Owner迁移未由这些测试证明 | 实际Owner离开、迟到加入、快照中断及VRChat联网多客户端证据 |
| 尺寸数学、坐标一致性、径向代理与远端装饰 | [test_math_model.py](../../tests/prep_math/test_math_model.py#L12-L35) 覆盖体积/半径换算、半径与直径区分及非法输入；[test_radial_proxy.py](../../tests/ball_radial_proxy/test_radial_proxy.py#L17-L147)、[test_attachment.py](../../tests/ball_attachment/test_attachment.py#L23-L123)、[test_ball_motion.py](../../tests/ball_motion/test_ball_motion.py#L10-L75) 覆盖坐标变换、贡献重建、扫掠/FollowRig与host-only边界 | 数值/状态模型测试；不代表Unity场景、实际PhysX、网络同步或任何性能结果 | Unity多客户端坐标回归、地图实测、性能采样；全客户端共享坐标系 |
| 资源许可与压缩包库存 | [test_archive_inventory.py](../../tests/offline_quality/test_archive_inventory.py#L33-L149) 与只读 [archive_inventory.py](../../tools/offline_quality/archive_inventory.py#L1-L22) 覆盖ZIP元数据、危险路径、大小/压缩限制及CLI边界 | 库存扫描不读/解压成员负载，不判许可、不构成恶意软件或完整内容安全证明 | 人工核许可、完整包检查和正式导入审查；未知许可不得进入正式工程 |

## 压缩包库存工具边界

只支持 Python 标准库与 ZIP；默认仅读目录元数据，不读取成员负载、不解压、不执行脚本。默认限制：归档150 MiB（小包试点）、20,000条、总展开尺寸2 GiB、单文件512 MiB、压缩比200；拒绝绝对/盘符/ADS冒号/反斜杠/遍历路径、Windows保留设备名、组件尾随点或空格、大小写等价重复路径、文件/目录前缀冲突、符号链接、加密成员及非Stored/Deflate压缩。SHA-256（若启用）只针对压缩包外部文件整体计算。库存输出只表示元数据检查通过，不表示安全解压或内容安全。

复现命令：

```sh
python3 -m unittest discover -s Docs/tests/offline_quality -v
python3 Docs/tools/offline_quality/archive_inventory.py --json --sha256 /path/to/archive.zip
python3 Docs/tools/offline_quality/archive_inventory.py --max-archive-bytes 52428800 --max-entries 10000 --max-total-bytes 536870912 /path/to/archive.zip
```

退出码：0完成元数据清点；2拒绝/无法检查。这个结果绝不等于允许恢复/解压，更不意味着内容安全。ZIP中央目录仍是不可信输入；`max_entries` 只有在标准库解析中央目录并构造条目列表后才检查，不是硬内存沙箱。`max_archive_bytes` 先以stat做粗略预检，但并发替换/增长可产生TOCTOU；后续hash、stat与ZIP扫描也不保证读取同一文件版本。CRC仅是目录中的声明值，工具不读取成员负载，因而不验证CRC/负载，不校验内容哈希/签名，不做文件类型嗅探，不发现内嵌归档，也不是安全解压器。路径策略仅为Windows常见恢复路径风险防护，不能覆盖所有目标文件系统/Unicode别名规则。标准库解析器本身也可能有漏洞；限制不替代隔离环境、恶意软件扫描、内容与许可人工审查。

## 覆盖与范围边界

当前 P03 ZIP 单测仅验证该库存工具的元数据策略与CLI输出；之后已有若干独立的纯Python参考模型和测试，如上表映射。它们均为host-only模型，不代表T20完成、Unity游戏功能已实现或真实网络成立。矩阵内尚未映射到实际存在的测试项仍是待后续实现/验证的设计，不应按表中预期性质宣称PASS。

Unity/Udon编译、Unity场景/ClientSim、真实VRChat双/四客户端、网络延迟/丢包/拥塞、Owner迁移、性能采样、真人舒适性与许可批准均不由这些host-only测试覆盖。离线工作不冻结公共接口，不改变T06状态。
