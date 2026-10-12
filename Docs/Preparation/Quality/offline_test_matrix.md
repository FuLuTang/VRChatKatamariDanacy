# P03 离线质量门禁矩阵（准备稿）

状态：PREPARATION / NOT_RUN。依据 v0.3 契约与 `Docs/config/prototype_defaults.json` 的临时参数；不冻结 T06，不代表已实现、Unity/Udon 编译、网络或真人 PASS。数值只作为可参数化的测试输入，不能当原作实测数据。

| 领域 | 离线纯 Python 可测（当前可写断言/模型） | 预期性质/失败条件 | 后续必须补充的证据 |
|---|---|---|---|
| 输入松手、追踪丢失 | 对输入帧序列做纯函数模型：Grip释放、追踪无效、菜单打开、输入模式切换均清空意图/速度残留；Jump仅上升沿触发一次 | 松手/丢追踪/菜单后输出零驱动；持续按住不重复激活 | Unity平台实验、PCVR A/B/Desktop、真人手柄与菜单；G1，当前无 PASS |
| 退出/Respawn | 状态恢复模型覆盖比赛退出、Respawn、KO清理路径 | 恢复进入前玩家设置；Station/输入/道具无残留 | Unity/ClientSim后再由用户真人验证退出与站姿恢复 |
| 物品世代/重复/乱序 | 请求键 `(roundId, spawnId, spawnGeneration, playerSessionGeneration, requestSequence)` 的参考 reducer；乱序旧revision拒绝，重复确认不重复记账 | 单个世代至多一个winner；新世代不接受旧请求；确认序列单调 | Udon实现后的Unity双客户端/多客户端日志、延迟/重复/乱序实测 |
| 预测确认/回滚 | 模拟临时预测收益与确定提交账本分层；拒绝时回滚预测附着和预测收益 | 预测值不能交分/吞噬；拒绝后最终账本不变 | 实际视觉与权威状态联调、拥塞下回滚表现 |
| 交分/KO/截止竞争 | 用显式时间戳和序列化裁决规则穷举排列；bankRequestId幂等，Settling仅接受截止前已收到的有效操作 | 已交存分不被KO夺走；各事务不复制价值；截止后的迟到输入不伪装提前 | Unity裁决实现及双/四客户端竞争日志；项目截止规则仍需接口评审 |
| 迁移快照 | 分块快照模型检查同snapshotRevision/epoch、完整分块集合、commitSequence单调、旧epoch拒绝 | 不完整/混版本不能提交；无完整快照时显式取消而非猜测恢复 | 实际Owner离开、快照中途离开、迟到加入的VRChat联网证据 |
| 坐标一致性/远端装饰 | 检查变换样例和确定性装饰种子；碰撞/收益只读共享世界坐标和确认账本 | 远端随机装饰不改碰撞、半径、收益；同输入可重建摘要 | Unity多客户端坐标回归、地图实测；所有客户端共享坐标系 |
| 资源许可证/库存 | JSON/CSV字段完整性、来源与包哈希记录、只读ZIP结构安全库存 | 未知许可不进入正式工程；异常包拒绝；不执行/解压包内代码 | 人工核许可、完整包检查和正式导入审查；扫描器不能证明许可 |

## 压缩包库存工具

只支持 Python 标准库与 ZIP；默认仅读目录元数据，不读取成员负载、不解压、不执行脚本。默认限制：归档150 MiB（小包试点）、20,000条、总展开尺寸2 GiB、单文件512 MiB、压缩比200；拒绝绝对/盘符/ADS冒号/反斜杠/遍历路径、Windows保留设备名、组件尾随点或空格、大小写等价重复路径、文件/目录前缀冲突、符号链接、加密成员及非Stored/Deflate压缩。SHA-256（若启用）只针对压缩包外部文件整体计算。库存输出只表示元数据检查通过，不表示安全解压或内容安全。

复现：

```sh
python3 -m unittest discover -s Docs/tests/offline_quality -v
python3 Docs/tools/offline_quality/archive_inventory.py --json --sha256 /path/to/archive.zip
python3 Docs/tools/offline_quality/archive_inventory.py --max-archive-bytes 52428800 --max-entries 10000 --max-total-bytes 536870912 /path/to/archive.zip
```

退出码：0完成元数据清点；2拒绝/无法检查。这个结果绝不等于允许恢复/解压，更不意味着内容安全。ZIP中央目录仍是不可信输入；`max_entries` 只有在标准库解析中央目录并构造条目列表后才检查，不是硬内存沙箱。`max_archive_bytes` 先以stat做粗略预检，但并发替换/增长可产生TOCTOU；后续hash、stat与ZIP扫描也不保证读取同一文件版本。CRC仅是目录中的声明值，工具不读取成员负载，因而不验证CRC/负载，不校验内容哈希/签名，不做文件类型嗅探，不发现内嵌归档，也不是安全解压器。路径策略仅为Windows常见恢复路径风险防护，不能覆盖所有目标文件系统/Unicode别名规则。标准库解析器本身也可能有漏洞；限制不替代隔离环境、恶意软件扫描、内容与许可人工审查。

## 覆盖与范围边界

当前脚本单测仅验证ZIP元数据策略与CLI输出；纯Python状态模型尚未实现，以上相关项是待后续模块实现后采用的测试设计。绝不据此宣称T20完成或Unity游戏功能已实现。本离线工作不冻结公共接口，不改变T06状态。
