# EditMode 全量（第 J 轮，commit 44e0cfe，Unity 2022.3.22f1）

## 全量

| 指标 | 值 |
|---|---|
| total | **681**（上轮 658 + 新增 23） |
| passed | **678** |
| failed | **3** |
| skipped | 0 |
| duration | 15.01 s |
| resultState | Failed(Child) |

## 3 条失败

### 1. SDK 链接检查（预期内）

```
Tests.GraphNodeTests.CheckHelpURLsForSystemNodes
  URL does not resolve for Const_SystemBooleanArray
  Expected: True / But was: False
```

### 2. ContactModelV2Tests.AnObbThatIsNotOrthonormalIsRefused

```
a sheared box must be refused rather than queried
Expected: False
But was:  True
```

### 3. ContactModelV2Tests.PushingIntoATallWallEndsInTheRecoveryHoldInsteadOfLooping

```
a ball that cannot move and is still being pushed must be recovered
Expected: greater than or equal to 1
But was:  0
```

## 上轮两条已修好

| 用例 | 上轮 | 本轮 |
|---|---|---|
| `ContactClassificationTests.TheWallIsTheContactTheDrivePushesIntoMost` | 失败（期望 1 实得 0） | **通过** |
| `ContactGoldenValueTests.TheReferenceScenariosMatchTheModel` | 失败（差 1.5 mm） | **通过** |

## 新增类逐类结果（合计 23 条）

| 类 | total | passed | failed |
|---|---|---|---|
| `NeedleSegmentTests` | 12 | 12 | 0 |
| `ContactModelV2Tests` | **11** | 9 | **2** |

（brief 里写 `ContactModelV2Tests` 10 条，本轮实测 11 条。）
