# 贴图试点（12 件）

`TEXTURE_PBR_SPEC.md` §10 的可视证据。全部由 `scripts/build_textured.py` 生成：

```bash
source ~/tools/blender-env.sh
$BLENDER $BLENDER_FLAGS --background --python \
  Docs/Art/OriginalModels/props/scripts/build_textured.py -- \
  --out Docs/Art/OriginalModels/props/texture_pilot \
  --props button,seed,bottle_cap,coin,plate,ceramic_mug,dandelion_puff,beetle_figurine,crate,storage_bin,postcard,grass_blade
```

- `compare3_old_bad_fixed.png` — 12 行 × 3 列：旧纯色 / **有问题的一版**（法线是 fbm 噪声）/ 修正后。
- `normal_maps_sheet.png` — 12 件的法线图（8 件平坦 = 该类材质不生成法线）。
- `uv_checker_sheet.png` — 12 件的 UV 棋盘格渲染（32 格，红带 = u 轴、绿带 = v 轴）。
- `previews/` — 贴图化后的 512² 预览。
- `textures/` — 每件 `_albedo.png` / `_mra.png` / `_normal.png`。
- `manifest.json` — 每件分辨率、显存（含 mip）、槽位→单材质颜色核对、尺寸漂移、`uv_check`（重叠/零面积/覆盖率/角度）、`normal_kinds`。
- FBX/GLB/blend **没放进来**（体积原因）：重跑上面的命令即得，模型模块未改动。

注意：`dandelion_puff` 与 `beetle_figurine` 的几何在同一轮重做过（绒球化 / 加前胸背板与腿），
所以对比图里这两件的左侧是旧几何。
