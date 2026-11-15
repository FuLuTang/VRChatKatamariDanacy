# 贴图试点（12 件）

`TEXTURE_PBR_SPEC.md` §10 的可视证据。全部由 `scripts/build_textured.py` 生成：

```bash
source ~/tools/blender-env.sh
$BLENDER $BLENDER_FLAGS --background --python \
  Docs/Art/OriginalModels/props/scripts/build_textured.py -- \
  --out Docs/Art/OriginalModels/props/texture_pilot \
  --props button,seed,bottle_cap,coin,plate,ceramic_mug,dandelion_puff,beetle_figurine,crate,storage_bin,postcard,grass_blade
```

- `compare_old_vs_new.png` — 24 格对比图（左旧右新，每件两格）。
- `previews/` — 贴图化后的 512² 预览。
- `textures/` — 每件 `_albedo.png` / `_mra.png` / `_normal.png`。
- `manifest.json` — 每件的分辨率、显存（含 mip）、槽位→单材质的颜色核对、尺寸漂移。
- FBX/GLB/blend **没放进来**（体积原因）：重跑上面的命令即得，模型模块未改动。

注意：`dandelion_puff` 与 `beetle_figurine` 的几何在同一轮重做过（绒球化 / 加前胸背板与腿），
所以对比图里这两件的左侧是旧几何。
