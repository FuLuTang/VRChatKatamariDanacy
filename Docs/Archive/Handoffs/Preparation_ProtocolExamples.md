# P12 事务恢复宿主参考模型交接

## 交付范围

- 事务场景与规则边界：[transaction_cases.json](transaction_cases.json)、[README.md](README.md)
- 标准库纯宿主参考模型：`Docs/tools/prep_protocol/model.py`
- unittest包：`Docs/tests/prep_protocol/__init__.py`、`test_model.py`
- 写入范围仅 `Docs/Preparation/ProtocolExamples/`、`Docs/tools/prep_protocol/`、`Docs/tests/prep_protocol/`；没有改任务根文件、公共契约、已有P03/P05文档或Unity工程。

## 验证证据

执行 `python3 -m unittest discover -s Docs/tests/prep_protocol -v`：34 tests，OK。只支持并恢复单分片完整快照；multipart一律明确拒绝，另有正向单分片恢复与负向multipart回归测试，证明非header状态不会被静默忽略。迁移时清理旧authority epoch的request-response cache，保留快照内业务幂等ID与物件账本，另有同session/request序号跨epoch更换操作的回归测试。全仓 `python3 -m unittest discover -s Docs/tests -v`：132项全部通过。P22校验器曾错误期待`V01-radius-diameter`；实际P13夹具ID是`V03-radius-diameter-distinct`，offline-quality修正了检查器预期，没有改P13夹具。

可复现命令、模型假设、未冻结政策及不能推导的结论见 [README.md](README.md)。

## 关键边界与风险

- 这是本机标准库fixture，不代表Udon、VRChat网络/Host Authority实现、反作弊、时钟或多人PASS。
- `observed_sender_id`、epoch、接收tick由测试harness假定可信，仅做元数据一致性测试；不伪造SDK sender认证能力。
- queued状态不等于成功或播放成功庆祝；账本只在模拟commit更改。
- pickup/磁铁按spawn世代防重复；bankId/KOId是独立幂等键；KO保留banked积分，清未交存资源。完整快照包含这些业务幂等记录，部分/冲突/倒退快照取消回合。
- cutoff严格小于、同tick事务排序、magnet逐对象部分成功与攻击者reward边界都是可替换的离线实验假设。排序有备选方案用于展示反例，不是批准政策或冻结接口。
- Python只验证本模型自己的断言；Unity/Udon/网络/真人测试未运行。

## 环境/权限

Unity在Windows，项目路径 `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`，用户拥有并启动Editor；MCP未知。本工作未SSH、未调用MCP/Blender/Unity、未下载、安装、访问账号、付费、提交、推送或发布。

## 只读交叉审查请求

`offline-quality` 已完成只读审查并指出两项本轮修正问题。作者修正后已再次请求针对最终版本只读复审：重点验证单分片快照约束、跨epoch request cache、业务幂等账本，以及是否仍有Fixture边界误读。若复审发现问题，报告主管并由作者在本任务写入范围修正；审查者不要改本包文件。
