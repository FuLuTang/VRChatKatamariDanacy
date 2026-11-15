# EditMode 全量（pass3，commit 7f5cd7b，Unity 2022.3.22f1）

## 全量

| 指标 | 值 |
|---|---|
| total | 603 |
| passed | 602 |
| failed | **1** |
| skipped | 0 |
| duration | 13.46 s |
| resultState | Failed(Child) |

唯一失败（与预期一致，SDK 链接检查，非本项目代码）：

```
Tests.GraphNodeTests.CheckHelpURLsForSystemNodes
  URL does not resolve for Const_SystemBooleanArray:
  https://docs.microsoft.com/en-us/dotnet/api/system.array.?view=netframework-4.8
  Expected: True / But was: False
```

## 重点套件（按 group_names 单跑）

| 套件 | total | passed | failed |
|---|---|---|---|
| `RadialProxyTests` | 10 | 10 | 0 |
| `RadialProxyShellTests` | 9 | 9 | 0 |
| `RadialProxyLocalBoundsTests` | 7 | 7 | 0 |
| `StoryRoundControllerTests` | 1 | 1 | 0 |
| `LevelArenaHostRegistrationTests`（同文件里的 arena 挂起用例，独立类名） | 2 | 2 | 0 |
| `PickupFieldValueUnitsTests` | 8 | 8 | 0 |

注：`RadialProxyLocalBoundsTests` 本轮实测 **7** 条（brief 里写 5）；`StoryRoundControllerTests` 这个 group 名只匹配 1 条，三条 arena 用例在 `StoryRoundControllerTests.cs` 内的 `LevelArenaHostRegistrationTests` 类里，另有 1 条在 `StoryRoundControllerTests` 本身；两者合计 3 条全部通过。全量 603 条里只剩 SDK 那 1 条失败。
