# Cancer 参考关卡（`_ref_cancer`）导入 + Play

2026-11-15，commit `b528b39`，Windows Editor 2022.3.22f1 / SDK 3.10.5 / ClientSim。

## 素材

- 模型包 `/tmp/ref_cancer_models.tar`，sha256 `f42640462c0fc923e4b667ffe3e5ee39388a29ed314ff11871cc3532138ed06c`（与 `.sha256` 一致）。
- 145 个模型目录 + `exports/` + `README.txt`，解包到 `Assets/_RefUnlicensed/Models/`：
  - 122 个新目录（原本不存在）→ 拷入；
  - 23 个同名目录（第一星已在用）→ **内容与本次包不同**（24 个目录哈希不同，含 `exports`），为不破坏已验收的第一星参考关卡，**保留第一星版本、未覆盖**；差异清单只在报告里，属已知冲突。
- 关卡数据：Linux `Docs/Design/Levels/_ref_cancer/placement.model.json` → Windows `Docs/Design/Levels/_ref_cancer/placement.json`（273,579 B，`level.scene=StoryMode`）。
- 场景：`StoryMode.unity` 磁盘拷贝 → `Assets/_RefUnlicensed/Scenes/RefCancer.unity`（795,185 B，新 GUID 由 Unity 生成；`_RefUnlicensed` 不入库）。

## 导入（`PlacementImporter.Import("_ref_cancer")`）

补齐两件 U# 宿主后（`LevelContent/_ref_cancer` 上 `LevelArenaHost`、`LevelContent/_ref_cancer/PickupField` 上 `PickupFieldHost`，都用 `UdonSharpUndo.AddComponent` 建 proxy+backing，再 `CopyProxyToUdon`）：

```
[level] cell=_ref_cancer +511 ~0 -0 props=511 other=5 errors=0
  pickup sync: ran, rows=511, rejected=0; udon copy: issued
  valueUnits: declared=737377, preserved=0, undeclared=0, read-back: verified
  arena: wired (boxes=0, walls=4, ramps=0, kinds=1)
```

再导一次（F2/F3 验收）：

```
[level] cell=_ref_cancer +0 ~0 -0 props=511 other=0 errors=0
```

## Play（v1，`startDiameterWorldM=0.222`，30 s+）

```
[PickupFieldHost] ready props=511 submittable=511 propRows=511 propObstacles=on ...
[BallMotor] Start ready diameter=0.222 collisionRadius=0.111 baselineUnits=1.397249 startSize=level hasLocalRow=True terrainSupport=True
[arena] planes=4/4 boxes=0/0 ramps=0/0 supports=1 refused=0 kinds=1 ...
[story-lab] t=… center=(-12.50, 0.11, -12.50) diameter=0.222 bottom=0 gap=0 support=ground falling=False
```

- `diameter=0.222` 与 `startDiameterWorldM` 一致；球没有 NRE、没有 LogError，`falling=False`、`bottom=0`（站在地面上）。
- 帧率：4.37 s 内 3498→3892 帧 ≈ **90 fps**（Editor Game 视图 + 511 行拾取场；`PickupFieldHost` 没有 `GetLastStepMicros` 这类接口，改用帧率）。
- 两条 warning（原样）：`prop colliders not fully wired; attached props stay collidable. props=511`、`attachmentRoot is null; not submitting candidates`（`BallVisualRollRoot` 下没接吸收根，本拷贝没接视觉吸收）。
- 注意 `[arena] planes=4/4`：Cancer 关卡结构是 +5（1 地面 + 4 墙），运行时地面走电机的内建地面平面（`support=ground`），arena 只登记了 4 面墙；Lead 期望的 `planes=5` 与实际不一致，按实测记录。
