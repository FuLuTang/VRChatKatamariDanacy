# 占位音效：设计、参数与首批程序化渲染

状态：**占位音效 / 预制作草稿**。本目录的声音全部由 [synth/synth.py](<synth/synth.py>) 用振荡器、噪声和包络**程序化合成**，不含任何第三方采样或原作音频，因此不引入授权问题；但它们只用于在 Unity / VRChat 里判断**时机、层次和手感**，不是最终音质。

- 未导入 Unity、未在 VRChat 内试听、未在 VR 头显里验证过音量与定位；没有 4 人并发或池耗尽测试（T23 的实现与性能工作尚未开始）。
- 事件 id 来自 [events.csv](<../../Preparation/Feedback/events.csv>)，那些是**示例 id**，不是 Udon 事件名或网络协议；T06 之前公共接口不冻结。
- 参数（球径、速度归一化、档位阈值）是占位值，不是实测原作数值。
- 最终有一部分可能会换成实录素材；授权策略正在 [Docs/Research/AssetSources/README.md](<../../Research/AssetSources/README.md>) 里调研（该文档已给出"自制优先、CC0 次之、NC 排除"的偏好顺序）。本目录不指派项目许可，也不代表任何素材已获授权。

## 1. 结论摘要

| 项 | 现状 |
|---|---|
| 声音数量 | 23 个 WAV（17 个设计 id；滚动 3 变体、小拾取 5 变体） |
| 格式 | 48 kHz / 16-bit / 单声道 PCM，无压缩、无元数据 |
| 峰值 | 全部 ≤ **−1.94 dBFS**（最响的是一次性大事件），远低于 −1 dBFS 上限；无削波、无 DC 偏置 |
| 材质 | 全部程序化原创；**0 个第三方文件** |
| 覆盖事件 | events.csv 30 个事件全部有明确处理（11 行按设计无独立音/静音） |
| 循环 | 4 个循环文件（滚动 3 变体 + 磁铁持续）；接缝回绕跳变 −42.9 ~ −68.0 dBFS，且**小于接缝附近任何正常相邻采样步长**（比值 0.19 ~ 0.71） |
| 体积 | 合计 1,598.5 KB（1.56 MiB），单文件最大 211 KB（滚动·大） |
| 校验 | `unittest` 9 项通过；确定性（同 seed 同字节）、峰值/格式、循环接缝、manifest 一致性 |
| 最弱 | 滚动循环的真实质感、火箭、KO、交分铃的音色与音乐性（见 §9） |

## 2. 目录与复现

```
Docs/Art/Audio/
├── README.md                 ← 本文档
├── synth/
│   ├── synth.py              ← DSP 小工具 + 每个声音一个函数 + 渲染表
│   ├── test_synth.py         ← 确定性 / 峰值 / 接缝 / 清单一致性
│   └── .gitignore
└── rendered/
    ├── manifest.csv          ← 文件、事件、时长、循环、峰值、生成函数、seed
    └── sfx_*.wav             ← 23 个产物
```

```bash
# 渲染全部（约 4 秒，纯 Python 单线程）
cd Docs/Art/Audio/synth && python3 synth.py

# 只渲染名字含某子串的条目（调试用）
python3 synth.py --only roll,pickup --out /tmp/audiotest

# 校验
python3 -m unittest -v test_synth
```

- 仅标准库：`wave / math / random / array / csv / argparse`。本机 `python3 -c "import numpy"` 失败，所以生成器**不使用 numpy**，也没有第二套实现；以后装不装 numpy 都不影响复现。
- 每个声音一个函数（`sfx_roll_small`、`sfx_pickup_03` …），`SOUND_TABLE` 驱动渲染，seed 固定，**同一 seed 输出逐字节相同**（`test_same_seed_same_bytes`）。
- 归一化按类别目标峰值（§6），不做响度归一化、不压缩、不限制器；写盘前统一去 DC。
- 循环音用"周期化合成"：所有振荡器频率、LFO 速率、脉冲间距都取循环长度的整数分频；噪声用**环形一极点滤波**（把 IIR 在环形缓冲上跑两圈取最后一圈，误差按 `a^n` 衰减）。因此首尾样本按周期延拓连续，不需要交叉淡化，也不会因为 crossfade 损失低频。

## 3. 事件 → 声音映射（events.csv 全覆盖）

图例：`P0/P1/P2/P3` 沿用 events.csv 的审阅标签，不是最终调度契约；"预留"表示设计已定但本轮没有渲染文件。**本轮 MVP** = 有文件名的 23 个 WAV；**later** = 表中所有"预留"条目，按 P0→P1 补。

| 事件 id | 优先级 | 声音 / 文件 | 处理与理由 |
|---|---|---|---|
| `collect.predicted` | P2 | **无**（预留 `pickup.pending`） | 预测不是确认收益，不播奖励音；pending 标记留给视觉/HUD。 |
| `collect.confirmed` | P1 | `pickup.small` → `sfx_pickup_01..05`；`pickup.big` → `sfx_pickup_big` | 按物品稳定 ID 选变体；150 ms 时间窗合并，最多 3 层。 |
| `collect.rejected` | P0 | `bump.reject` → `sfx_bump_reject` | 中性闷响；**绝不**与成功音相似。也用于"太大、吸不动"的物理碰撞。 |
| `growth.threshold` | P1 | `growth.step` → `sfx_growth_step` | 一次提交跨多档只播最终档位。 |
| `growth.buried` | P3 | **无** | 纯视觉遮挡/回收，不额外奖励。 |
| `powerup.spawn-warning` | P1 | **无**（预留 `powerup.available`） | 需要"可用/刷新"世界标记，音效优先级低于本轮范围。 |
| `powerup.acquired` | P1 | **无独立音**（暂借 `sfx_magnet_activate` 前 120 ms；预留 `powerup.acquire`） | 现有借用容易和"激活"混淆，列为待做。 |
| `magnet.activated` | P0 | `magnet.activate` + `magnet.loop` → `sfx_magnet_activate` / `sfx_magnet_loop` | 启动音短、持续循环低；结束 150 ms 淡出；KO/交分立即停。 |
| `magnet.pull-predicted` | P2 | 仅磁铁循环本体 | 不对每个候选播放拾取音；候选飞行是装饰。 |
| `rocket.activated` | P0 | `rocket.boost` → `sfx_rocket_boost` | 约 1 s 收住，不做持续轰鸣；不抖屏、不改 FOV。 |
| `powerup.ended` | P1 | **无独立音**（预留 `powerup.end`） | 目前靠循环淡出/尾音自然结束；结束状态由 HUD 保证。 |
| `threat.approach` | P0 | `threat.warning` → `sfx_threat_warning` | 中频双音，限速重复；只在威胁实质性变化时升级。 |
| `threat.protection` | P0 | **无**（预留 `protection.start/end`） | 保护状态当前完全靠 HUD/世界环；音效是后续补充。 |
| `combat.hit-confirmed` | P1 | 复用 `sfx_bump_reject`（−2 半音、−6 dB）；预留 `impact.hit` | 首版用同一个中性撞击，避免新增相似音；确认结果仍以 HUD 为准。 |
| `combat.ko-confirmed` | P0 | `ko.swallow` → `sfx_swallow_ko` | 下行"咕噜"+ 软低频；不用爆裂声/惊吓音。 |
| `respawn.started` | P0 | `respawn.rise` → `sfx_respawn` | 安静返回，不庆祝；保护期由 HUD 常驻。 |
| `bank.entered` | P1 | **无**（预留 `bank.enter`） | 进入交分区不加奖励音，避免误以为已存入。 |
| `bank.request-pending` | P1 | **无**（沉默优先） | pending 只做 UI；不播"快好了"的期待音。 |
| `bank.committed` | P0 | `bank.deposit` → `sfx_bank_deposit` | 每个 `bankRequestId` 恰播一次；400 ms 内多笔合并。 |
| `bank.rejected` | P0 | `sfx_bump_reject` | 与"拒绝/撞不动"共用中性音；绝不播成功音。 |
| `round.lobby-join` | P1 | `sfx_ui_click`（确认加入后） | 仅确认后一声；预留独立 `lobby.join`。 |
| `round.countdown` | P0 | `sfx_round_countdown`（最后 3 声 +2 半音） | 由共享结束时间本地推导，**每秒不联网**；取消时停。 |
| `round.cancelled` | P0 | **无独立音**（暂用 `sfx_bump_reject` 降调，预留 `round.cancel`） | 中性、短；绝不能像开局或胜利。 |
| `round.started` | P0 | `sfx_round_start` | 每 `roundId` 一次；音量封顶，不做持续播报。 |
| `round.deadline-warning` | P0 | `sfx_round_countdown`（固定阈值，−6 dB） | 只在 30 s / 15 s 等固定阈值播 1–2 声，不逐秒。 |
| `round.settling` | P0 | `sfx_round_end`（−3 半音、−8 dB） | 弱化复用；不提前宣告赢家。 |
| `round.results-committed` | P0 | `sfx_round_end` | 只在结果提交后播；领奖台视觉另行评审。 |
| `round.reset` | P0 | **无强制音** | 清理需要可靠，不需要声音；可选极轻过渡。 |
| `lobby.cosmetic-preview` | P2 | `sfx_ui_hover` / `sfx_ui_click` | 纯 UI 导航，2D；连续悬停限速。 |
| `lobby.exit-and-recovery` | P0 | **无必需音**（UI 确认可用 `sfx_ui_click`） | 退出路径必须始终可用；音效不能成为退出条件。 |

滚动底噪不是 events.csv 里的一个事件，而是 [01_GAME_DESIGN.md](../../01_GAME_DESIGN.md) §7 的"滚动底层"。`manifest.csv` 里把它的生命周期记为 `round.started`（开始）→ `round.settling`（停止），具体启停由本地球速驱动，不由事件触发。

## 4. 分段设计

除标注"循环"的文件外，其余全部是**一次性（one-shot）**；循环文件只有滚动 3 变体与磁铁持续音。

### 4.1 滚动底噪（3 个无缝循环）

| 文件 | 时长 | 层 | 说明 |
|---|---|---|---|
| `sfx_roll_small.wav` | 1.50 s | 低频核心（≈90 Hz + 2/3/4 次谐波）+ 颗粒噪声（≈180 Hz–2.5 kHz，双 LFO 调幅）+ 压地脉冲（每循环 47 次） | 整体再经约 3.1 kHz 低通；轻、快、颗粒细。 |
| `sfx_roll_medium.wav` | 1.80 s | 同上，f0≈65 Hz，每循环 41 次脉冲，约 2.5 kHz 低通 | 默认中段。 |
| `sfx_roll_large.wav` | 2.20 s | f0≈40 Hz，每循环 34 次脉冲，约 1.8 kHz 低通 | 低频更多、高频更少；避免"闷雷"持续轰鸣。 |

脉冲是"压过地面"的短促低频，不是节拍；速度变化交给引擎的播放速率与低通（§5），所以三个文件都录制在标称速度。

### 4.2 拾取与成长

| 文件 | 时长 | 层 | 用途 |
|---|---|---|---|
| `sfx_pickup_01.wav` … `sfx_pickup_05.wav` | 0.13 s ×5 | 上滑主体（×0.86→×1.42，基准 470/545/630/725/835 Hz）+ 噪声点击 + 高八度甜点 | 小物确认拾取；变体按物品稳定 ID 选，不运行时随机。 |
| `sfx_pickup_big.wav` | 0.36 s | 128→46 Hz 下扫 + 620 Hz 低通噪声团 + 180 Hz 敲击 | 大物确认拾取；低频重音，不刺耳。 |
| `sfx_bump_reject.wav` | 0.20 s | 230→150 Hz 闷响 + 900 Hz 噪声，2.4 kHz 低通 | 撞上太大物件 / 裁决拒绝 / 交分被拒。 |
| `sfx_growth_step.wav` | 0.50 s | C5–E5–G5 三音铃（2×/3× 泛音） | 成长档位；一次提交只播最终档。 |

### 4.3 道具

| 文件 | 时长 | 层 | 用途 |
|---|---|---|---|
| `sfx_magnet_activate.wav` | 0.50 s | 165→520 Hz 上扫 + 250→3250 Hz 低通噪声上扫 | 磁铁启动；同时可作为"道具获得"的临时前半段。 |
| `sfx_magnet_loop.wav` | 1.60 s 循环 | 58/116/174 Hz 哼鸣 + 200–420 Hz 带噪声 + 3/7 周 LFO 摆动，1.2 kHz 低通 | 磁铁持续；低能量、可被整体淡出，不抢警告。 |
| `sfx_rocket_boost.wav` | 0.95 s | 240→≈3640 Hz 噪声"呼"+ 96→52 Hz 推背 + 160→300 Hz 上扫 | 火箭启动；一次性收住。 |

### 4.4 威胁 / PVP / 重生

| 文件 | 时长 | 层 | 用途 |
|---|---|---|---|
| `sfx_threat_warning.wav` | 0.55 s | A4 + D5 两声中频（+2/+3 泛音）+ 110 Hz 底，3.6 kHz 低通 | P0 威胁提示；不尖叫、不惊吓。 |
| `sfx_swallow_ko.wav` | 1.10 s | 430→105 Hz 咕噜 + 240→70 Hz 第二层 + 70→48 Hz 软轰 + 420 Hz 噪声，2.6 kHz 低通 | 被吞/KO；随后交给 HUD 状态。 |
| `sfx_respawn.wav` | 1.20 s | G3/D4/G4 微上滑垫音 + 3 kHz 以上微光 + 0.62 s 处 G5 轻铃 | 重生返回；不庆祝。 |

### 4.5 回合 / 交分 / UI

| 文件 | 时长 | 层 | 用途 |
|---|---|---|---|
| `sfx_bank_deposit.wav` | 0.90 s | C5–E5–G5–C6 上行铃（2×/3.01×/4.2× 泛音）+ 140→70 Hz 落地 | 交分提交成功。 |
| `sfx_round_countdown.wav` | 0.14 s | 880 Hz + 1760 Hz 泛音，5.2 kHz 低通 | 倒计时/末段提醒；最后 3 声 +2 半音。 |
| `sfx_round_start.wav` | 1.10 s | G4–C5–E5 三音（6 次谐波加法）+ 98 Hz 底 | 开局；音量封顶。 |
| `sfx_round_end.wav` | 1.70 s | E5–D5–C5–G4 下行解决 + 98 Hz 底 | 结算/结果提交。 |
| `sfx_ui_click.wav` | 0.055 s | 1350/2700 Hz 短音 + 噪声 tick | 2D UI 确认。 |
| `sfx_ui_hover.wav` | 0.035 s | 1850 Hz 单音 | 2D UI 悬停；比点击再低 4 dB。 |

## 5. 随球径 / 速度变化的参数

占位归一化（**OPEN**，等 T06 与真机尺度再定）：

```
s = clamp(ln(d / 0.6) / ln(10), 0, 1)     # 球径 d：0.6 m → 0，6 m → 1
v = clamp(|ball_velocity| / 8.0, 0, 1)    # 8 m/s 作为满速参考
```

球径只驱动"选哪个变体 + 频谱明暗"，速度只驱动"播放速率 + 音量 + 低通"；两者不混在一个 pitch 里，避免尺寸和速度互相抵消。

### 5.1 滚动循环

| 参数 | 公式 / 规则 | 备注 |
|---|---|---|
| 变体选择 | `s < 0.33` → small；`0.33 ≤ s < 0.72` → medium；`s ≥ 0.72` → large | 相邻两路同时播放，0.35 s 等功率交叉淡化；不要在门限上硬切。 |
| 播放速率 | `pitch = 0.78 + 0.44·v`（0.78–1.22） | 三个文件用同一公式，切换变体时不会音高跳变。 |
| 增益 | `gain_dB = −22 + 10·v`（静音 −22，满速 −12） | 再叠加类别 trim（§6）与用户 SFX 音量；四球叠加时按距离与优先级衰减。 |
| 低通 | `fc = clamp((900 + 5200·v) · (1 − 0.22·s), 600, 8000)` Hz | 用 Unity `AudioLowPassFilter`；大球更闷，高速更亮。 |
| 起停 | 120 ms 淡入/淡出；KO、交分成功、Settling、退出时停 | 循环不参与一次性 voice 抢占，占用固定 ambience 槽。 |
| 空间化 | 本地球 **2D**；对手球 3D（min 2 m / max 25 m） | 自己的球贴着头，做 3D 会产生随头转动的定位感。 |

### 5.2 拾取 / 拒绝 / 档位

| 参数 | 公式 / 规则 |
|---|---|
| 变体 | `variant = stable_hash(itemInstanceId) % 5`；同一物品重放/重连得到同一变体，不用运行时 RNG。 |
| 音高 | `pitch = 0.97 + 0.07·t_size`（t_size 为物品在可拾取尺寸内的归一化档位，0–1）。 |
| 合并 | 150 ms 窗口内同一材质/同一提交批次最多 3 层；每多一层 −3 dB，总增益封顶 +2 dB。 |
| 拒绝 | 固定 −6 dB；`pitch = 0.90 + 0.10·t_size`；**不 duck、不延迟**，让"没吸到"立刻可听。 |
| 档位 | `pitch = 2^(min(level, 4) · 1/12)`（每档 +1 半音，最多 +4）；同一提交跨多档只播最终档。 |

### 5.3 道具 / 交分 / 回合 / 警告 / UI

| 声音 | 变化规则 |
|---|---|
| 磁铁启动 | 固定 pitch；−2 dB；每次 `activationId` 一次。 |
| 磁铁循环 | 固定 pitch；−12 dB；淡出 150 ms；激活期间 ambience duck −6 dB；KO/交分**立即**停（不做长尾）。 |
| 火箭 | 固定 pitch；0 dB；无循环；KO 时截断尾音。 |
| 交分 | 固定 pitch；400 ms 内多笔合并为一次；每 `bankRequestId` 一次。 |
| 倒计时 | 固定 pitch；最后 3 声 `+2` 半音、最后一声 +1 dB；由共享结束时间本地推导。 |
| 威胁 | 固定 pitch；重复间隔 ≥1.2 s；第二次起 `+2` 半音作为升级；ambience duck −6 dB、小拾取 duck −3 dB（250 ms）。 |
| KO | 固定 pitch；800 ms 内不重复；滚动 bed duck −6 dB（500 ms）。 |
| UI | 2D；click 比 pickup 低 6 dB，hover 再低 4 dB；80 ms 限速。 |

## 6. 混音与响度目标

**文件级**（已达成，见 manifest）：

| 类别 | 文件峰值目标 | 实测 |
|---|---|---|
| 滚动 / 磁铁循环 | −6.0 / −7.5 dBFS | 全部达标 |
| 一次性玩法音（拾取、大物、KO、回合、交分） | −2.2 ~ −1.9 dBFS | 全部达标 |
| 拒绝 / 倒计时 | −4.2 dBFS | 全部达标 |
| UI click / hover | −7.5 / −12.4 dBFS | 全部达标 |
| 硬上限 | ≤ −1.01 dBFS（0.89 满刻度） | 最响 −1.94 dBFS |

**混音级**（目标，未在引擎里验证）：

| 总线 | 引擎 trim（建议起点） | 听者处目标 | 并发上限 |
|---|---|---|---|
| 滚动 bed | −8 ~ −12 dB | −26 ~ −24 LUFS（short-term，持续） | 1（ambience 槽） |
| 小拾取 | −4 dB | 瞬态峰值约 −20 LUFS | ≤3 层 / 150 ms |
| 大物 / KO / 重生 | −3 dB | −18 ~ −16 LUFS | 1 |
| 道具启动 + 循环 | −4 / −12 dB | −20 LUFS | 各 1 |
| 交分 / 回合 | −3 dB | −18 LUFS | 1 |
| 威胁警告 | 0 dB | −15 LUFS | 1，预留不抢占 |
| UI | −8 dB | −30 LUFS | 1 |

- 主输出真峰值 **≤ −1 dBFS**；不挂总线压缩/限制器，不把 SFX 压成"广播响度"。上表 LUFS 是听者位置的**设计目标**，不是文件测量值。
- 8 路瞬态 voice 的分配沿用 [Feedback README](<../../Preparation/Feedback/README.md>)：2 路规则/威胁、4 路玩法、1 路 UI、1 路 ambience；滚动 bed 固定占 ambience，磁铁循环占玩法槽。
- Ducking 只作用于游戏总线：警告 duck ambience −6 dB；KO duck 滚动 −6 dB；**不默认改写玩家语音**。BGM、SFX、提示、振动、总音量各有独立滑杆。
- 文件级 integrated LUFS 用 ffmpeg `ebur128` 测过（滚动 −15.5 ~ −18.4、磁铁循环 −16.5、交分 −14.9、结算 −14.4、火箭 −13.8、重生 −13.7、警告 −16.4、KO −16.9）；**短于 0.3 s 的音被 gating 归零（−70），不能当响度结论**，所以上表只把文件响度当参考。

## 7. VR 舒适性与空间化

- **高频**：所有层都经过 ≤6 kHz 的低通（UI 两个短音也在 5.2 kHz 以下），没有 8 kHz 以上的宽带噪声；没有用白噪"嘶声"做奖励音。玩法短音起音 1.5–10 ms；UI click 的 1.5 ms 起音（噪声 tick 层 0.4 ms）是刻意的，但整体只有 −7.5 dBFS、55 ms。
- **瞬态与低频**：最响峰值 −1.94 dBFS；火箭 0.95 s 收住；KO 的低频约 0.9 s 内衰完；没有持续 sub-bass 循环。滚动 bed 在混音里再降 8–12 dB。
- **空间化**：本地球滚动 2D（不随头转动）；对手球 3D，`VRC Spatial Audio Source` 最小距离 2 m、最大 25 m、对数衰减；UI/警告 2D；任何音源都不挂在 HMD/相机上，不做随视角的声像移动。
- **循环管理**：同时最多两个循环（滚动 bed + 磁铁）；KO、交分、Settling、退出、重置都显式淡出/停止，不留下"幽灵循环"。
- **音量与可访问性**：总音量、BGM、SFX、UI/提示、振动独立；警告走独立"提示"通道，静音 SFX 时仍保留 HUD 的方向/状态。任何关键规则信息都不只靠声音（见 §8）。
- **不做的事**：不做强制镜头运动、不做相机抖动、不做突然的满响 jump-scare、不用高频尖叫当威胁提示。

## 8. 低特效规则的应用

对照 [low_fx_rules.md](<../../Preparation/Feedback/low_fx_rules.md>)：

| 规则 | 本音效集的处理 |
|---|---|
| 预测不播奖励音 | `collect.predicted` / `magnet.pull-predicted` 无音效；预留的 pending 音也不得带成功音色。 |
| 拒绝不播成功音 | `bump.reject` 是中性闷响，且不 duck 其它提示；交分被拒复用同一音。 |
| 只有确认状态才播奖励 | 拾取、交分、KO、回合开始/结束都只由确认状态触发；音效文件本身不携带"已提交"语义，触发点由代码保证。 |
| 结算前不宣告赢家 | `sfx_round_end` 只在 `round.results-committed` 播；`round.settling` 用降调弱化版。 |
| 低特效可去掉什么 | 滚动 bed（可静音，保留接触阴影）、远端球循环、UI hover、重复小拾取、磁铁循环、装饰性尾音。 |
| 低特效必须保留什么 | 威胁警告、KO、倒计时/开局/结束、交分成功/失败、拒绝；即使 voice 池满，也由固定 HUD 区域表达状态。 |
| 池满回退 | 按"规则/威胁 > 大物 > 小物 > 环境"抢占；本音效集里滚动 bed 最先让位，警告永不抢占。 |
| 不隐藏/throttle 规则状态 | 音效音量再低也不影响分数、计时、保护、资格；安静模式下 HUD 保留全部状态（low_fx_rules 的 6 区 + 1 提示）。 |
| 退出不被阻塞 | 退出路径无音效依赖；清理时停循环、清 one-shot 队列。 |

## 9. 程序化够不够？替换实录的搜索关键词

结论：**首版全部够用**（时机、层次、优先级可判断），但下面标"弱"的几项最终大概率要换实录或重做。

| 声音 | 程序化是否够用 | 若替换：搜索关键词（英） | 备注 |
|---|---|---|---|
| 滚动循环 ×3 | **够用但最弱**：有机质感、地表摩擦细节不足，循环久了容易"电子" | `fabric rumble loop`, `cart rolling gravel`, `low rumble texture 48k`, `rolling debris loop` | 优先换 1–2 个真实滚动/布料底噪，再叠加现有低频层。 |
| 小拾取 ×5 | 够用，辨识度好 | `wood block pop`, `plastic click`, `bubble pop`, `cork pop` | 后续按 6 种材质类各 2–3 个，而不是只有音高变体。 |
| 大物拾取 | 够用 | `heavy wooden thud`, `low impact boom foley`, `crate drop` | 需要"重量"，现有偏合成。 |
| 撞不动 / 拒绝 | 够用 | `dull knock`, `bonk`, `muted impact` | 关键是"不能像奖励"，不是音色。 |
| 成长档位 | 够用 | `marimba step`, `short bell ladder` | 可只保留 3 个档位变体。 |
| 交分成功 | **中等偏弱**：铃音干净但音乐性单薄 | `coin deposit chime`, `bell arpeggio`, `marimba run` | 需要和"回合结束"区分开，避免都是铃声。 |
| 磁铁启动 / 循环 | 够用 | `vacuum suck whoosh`, `magnetic hum loop`, `energy hum` | 循环最终建议实录低频 hum。 |
| 火箭启动 | **弱**：噪声上扫缺少真实喷流的颗粒 | `rocket whoosh`, `jet burst`, `flare ignite` | 换实录 + 低通，别用爆炸。 |
| KO / 被吞 | **弱**：咕噜方向对，但质感偏"合成 wobble" | `cartoon gulp`, `swallow foley`, `descending whoosh` | 避免任何恐怖/惊吓类素材。 |
| 重生 | 够用 | `soft shimmer rise`, `harp gliss`, `respawn chime` | 不要庆祝感。 |
| 倒计时 | 够用 | `UI beep`, `countdown tick` | 最终可由引擎变调，不必多个文件。 |
| 开局 / 结束 | 中等：能听出"开始/结束"，但音乐性弱 | `brass sting`, `marimba fanfare`, `game show ending` | 若要主题动机，需原创旋律设计，不抄原作。 |
| 威胁警告 | 够用 | `alert two tone`, `sonar ping`, `warning beep` | 中频、短、可重复；不要刺耳高频。 |
| UI click / hover | 够用 | `UI click`, `UI hover tick`, `menu blip` | 2D；音量最低。 |

授权边界（按 [AssetSources](<../../Research/AssetSources/README.md>)）：自制/程序化 → Kenney Audio（CC0）→ Sonniss GDC（商用免署名）→ Freesound CC0/CC-BY → Pixabay → Zapsplat（免费须署名）→ BBC RemArc 仅内部；**NC/ND/SA 一律排除**，CC-BY 需要世界内 credits。项目许可仍未决定，本目录不指派许可。

## 10. 校验结果

`cd Docs/Art/Audio/synth && python3 -m unittest -v test_synth` → **9 tests OK**（约 16–20 s，含两次完整重渲染）。检查项：格式/采样率/峰值、同 seed 逐字节一致、已提交文件与生成器一致、循环接缝、manifest 与渲染表一致、单文件 <300 KB、合计体积。

| 文件 | 时长 s | 循环 | 峰值 dBFS | 接缝跳变 dBFS | 接缝比 | 字节 |
|---|---:|---|---:|---:|---:|---:|
| `sfx_roll_small.wav` | 1.500 | 是 | −6.02 | −42.9 | 0.71 | 144,044 |
| `sfx_roll_medium.wav` | 1.800 | 是 | −6.02 | −48.7 | 0.47 | 172,844 |
| `sfx_roll_large.wav` | 2.200 | 是 | −6.02 | −51.3 | 0.52 | 211,244 |
| `sfx_pickup_01.wav` | 0.130 | 否 | −2.16 | — | — | 12,524 |
| `sfx_pickup_02.wav` | 0.130 | 否 | −2.16 | — | — | 12,524 |
| `sfx_pickup_03.wav` | 0.130 | 否 | −2.16 | — | — | 12,524 |
| `sfx_pickup_04.wav` | 0.130 | 否 | −2.16 | — | — | 12,524 |
| `sfx_pickup_05.wav` | 0.130 | 否 | −2.16 | — | — | 12,524 |
| `sfx_pickup_big.wav` | 0.360 | 否 | −1.94 | — | — | 34,604 |
| `sfx_bump_reject.wav` | 0.200 | 否 | −4.15 | — | — | 19,244 |
| `sfx_growth_step.wav` | 0.500 | 否 | −3.10 | — | — | 48,044 |
| `sfx_bank_deposit.wav` | 0.900 | 否 | −1.94 | — | — | 86,444 |
| `sfx_magnet_activate.wav` | 0.500 | 否 | −2.85 | — | — | 48,044 |
| `sfx_magnet_loop.wav` | 1.600 | 是 | −7.54 | −68.0 | 0.19 | 153,644 |
| `sfx_rocket_boost.wav` | 0.950 | 否 | −1.94 | — | — | 91,244 |
| `sfx_swallow_ko.wav` | 1.100 | 否 | −1.94 | — | — | 105,644 |
| `sfx_respawn.wav` | 1.200 | 否 | −3.35 | — | — | 115,244 |
| `sfx_round_countdown.wav` | 0.140 | 否 | −4.15 | — | — | 13,484 |
| `sfx_round_start.wav` | 1.100 | 否 | −1.94 | — | — | 105,644 |
| `sfx_round_end.wav` | 1.700 | 否 | −1.94 | — | — | 163,244 |
| `sfx_threat_warning.wav` | 0.550 | 否 | −2.16 | — | — | 52,844 |
| `sfx_ui_click.wav` | 0.055 | 否 | −7.54 | — | — | 5,324 |
| `sfx_ui_hover.wav` | 0.035 | 否 | −12.40 | — | — | 3,404 |

合计 **1,598.5 KB**；`rendered/manifest.csv` 由生成器写出（列：`file,event_ids,duration_s,loop,peak_dbfs,generator,seed,rms_dbfs,loop_seam_delta_dbfs,loop_seam_ratio,file_bytes`），不含时间戳/绝对路径，重跑不会变。

"接缝比" = 回绕跳变 ÷ 接缝两侧各 64 个采样里的最大相邻步长。**<1 表示回绕那一步比播放中任何一步都小**，听不出接缝；这一列只在循环文件上有意义。

另外做了非正式的频谱图目检（ffmpeg `showspectrumpic`，未作为测试）：滚动是低频宽带 + 规则脉冲；拾取是短促上滑；交分是四段上行泛音；KO 是下行扫频。没有发现宽带爆音、DC 线或异常共振。

## 11. 已知限制（未验证）

- **没有主观听感评测**：本目录只做了数值校验和频谱图目检；没有人在 VR/桌面试听，也没有和玩法节奏对过拍。
- 没有导入 Unity，未设置 `Load Type / Compression`、空间化曲线、混响区；这些会影响实际音量与 CPU，需在 T23 里定。
- 尺寸/速度归一化、8 m/s 满速、0.6–6 m 球径都是占位；没有和真实球速/尺寸模型对齐。
- 没有做 4 人并发、voice 池耗尽、重复快照下的"不重复 one-shot"测试；这些必须在实现里验证。
- 文件级 LUFS 只有参考意义：短音被 EBU R128 gating 丢弃；真实响度必须在引擎里测。
- 磁铁持续音/滚动 bed 在低特效与静音设置下的行为未实现，只是设计约定。
- 授权：本目录没有指派项目许可；换成任何外部素材前必须按 [AssetSources](<../../Research/AssetSources/README.md>) 逐文件登记来源、许可、署名与修改。

## 12. 后续

1. 补 `powerup.acquire`、`powerup.end`、`protection.start/end`、`impact.hit`、`lobby.join`、`round.cancel`、`bank.enter`、`pickup.pending`（顺序按 P0→P1）。
2. 材料分类：木/金属/塑料/玻璃/软物/有机物各 2–3 个拾取变体，替代现在"只有音高变体"的方案。
3. 在 Unity 里做 A/B：滚动 bed 的 trim、变体交叉淡化时长、低通曲线；用真机 4 人重叠场景验证抢占与 ducking。
4. 换掉最弱的三项（滚动质感、火箭、KO），优先用自制录音/拟音而不是外部包。
5. 若引入外部素材，先在 `Docs/Research/AssetSources/credits.csv` 登记，再进世界内 Credits 板。
