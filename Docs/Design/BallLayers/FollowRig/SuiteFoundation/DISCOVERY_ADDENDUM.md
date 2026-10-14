# 测试发现补充：FollowRig 与 RemoteProxy

## 原因

`Docs/tests/ball_follow_rig/` 和 `Docs/tests/ball_remote_proxy/` 之前缺少 `__init__.py`。Python `unittest discover` 从 `Docs/tests` 递归发现时，不会进入这两个非包子目录，因此旧仓库级报告中的 **206 项不包含这两套共 22 项测试**。直接把任一目录指定为 discovery 起点仍会发现其测试，但这不等于它们曾被旧的仓库级运行覆盖。

本次仅在两个测试目录新增空 `__init__.py`，作为测试包发现标记。未修改测试或实现代码。

## 本次 targeted 结果

从仓库根目录分别运行：

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s Docs/tests/ball_follow_rig -v
```

结果：**8 项通过，0 失败**（`Ran 8 tests ... OK`）。

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s Docs/tests/ball_remote_proxy -v
```

结果：**14 项通过，0 失败**（`Ran 14 tests ... OK`）。

以上两次 targeted runs 合计 **22 项通过**。本次没有运行仓库级全套测试：并行 workflow-6 正在修改 A/B 范围，应由 Lead 在该修改完成后统一运行回归。不要把旧的 206 项报告描述为包含上述 22 项，也不要据本补充声称仓库全套通过或给出全套总数。
