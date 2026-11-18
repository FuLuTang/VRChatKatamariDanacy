# 吸附音效与手柄振动

## 现行设计

`PickupFieldHost` 每帧沿现有绝对游标消费电机的确认事件环，然后才扫描下一批候选。不另读环、不从碰撞或拒绝产生反馈。只把本字段正在收集的新确认项交给本地球的 `AbsorbFeedbackHost`；即使视觉附着失败，确认项也给反馈。未骑乘、正在下车、回合停止收集、重试和切关均不保留待播簇。不发网络事件，不影响账本。

音效从首件起开 60 ms 窗口，到期播放一次；有可用片段的特殊类优先于默认，同为特殊则保留更大尺寸档（同档取最早）。缺片段的特殊类用默认组；同组有多个不同片段时，用独立本地随机序列排除上次实际播放的片段。四个挂在球根上的 2D 音源各独占一个声部，满时丢新声而不叠加、抢占。件数系数从 0.18 起每件加 0.02，四件封顶 0.24，再乘音效音量；0 会停止已播放的吸附音。2D 是本地验证阶段的选择，不是原作空间化结论。

触觉不等音频窗口。每帧末可向本地正在骑乘的 VR 玩家两手各发一次；每手最短间隔 80 ms，间隔内取最大尺寸档，不叠加。道具自身三边乘积的立方根除以球语义直径，按 0.15、0.30 两个断点分档（20/28/35 ms、强度 0.20/0.27/0.35，频率参数固定 0.5）。设置为关/弱/标准时强度乘 0/0.5/1；桌面不发调用。未绑定设置宿主时，音效 0.8、振动标准。参数均为待试玩的项目值；API 见 [VRChat Player API](https://creators.vrchat.com/worlds/udon/players/#playhapticeventinhand)。

设置每帧只比一次 `HubSettingsHost.GetRevision()`，变了才重读 `GetEffectsVolume01()` / `GetHaptics01()`；没有设置宿主时用 0.8 与标准档。骑乘结束（下车、重试、切关、回合停止收集）打一条 `[absorb-feedback] confirmed=… audioClusters=… audioPlayed=… poolDrops=… hapticPulses=…`，便于操作员对照；计数是本次会话累计，重开不清零，重复打同一组数会跳过。

## 音类数据

导入器在常规拾取字段成功写入后，按已落盘的 `itemIds` 顺序另写 `propAbsorbSoundClasses`；音类不是玩法类别。参考关加入时走 placement `prop.def` → 同目录 `original_defs.json` 的 `defs[].mono` → `name_props_0x30.tsv` 中 `nameIndex` 对应的 `0x19` → 本地允许列表。只有明确列出的非零字节写特殊类；未知、缺本地映射或表时写 0。运行时旧关没有音类列、列长不匹配时也用 0。无数据的构建能装配；没有默认片段时静音，不影响振动和吸附。

[仓库内格式样例](<../../../Tools/levelbuild/ref_audio_map.json>) 只用假文件名，不会被读取。编辑器从操作系统用户目录下的 `KatamariRef/audio/ref_audio_map.json` 读取 `schema` 和 `groups`。每组 `soundClass` 为唯一非负整数；0 必须存在且不填 `selectorHex`；其余组必填非零且互异的十六进制字节（建议两位、不带 `0x`）。`wavs` 是该组非空的 WAV 文件名列表，只写文件名，不写路径。其他字段是给操作员看的说明，不参与处理。改组编号/字节后，要重跑参考关导入和合集装配，避免两边不同步。只有输出本身位于 `Assets/_RefUnlicensed/` 时才给它写入原作片段引用。

## 参考音频候选

扫描包内 125/125 个 WAV 的 PCM。起音定义为首个超过该文件峰值 10% 的采样点，峰值相对满幅；另用 50 ms RMS 观察包络。峰值不等于听感响度。**整表待 owner 试听，文件后缀不等于已知 soundID。**

| 用途候选 | 文件名 | 推断依据 |
|---|---|---|
| 默认三件组，先试听 | `common1.wav`、`common3.wav`、`common4.wav` | `common` 组仅这三件约 0.11–0.14 s；起音约 19/7/1 ms，峰值 0.92/0.89/0.97。后两件较轻且短衰减，第一件峰值靠后、整段 RMS 约 0.39，比另两件的 0.23/0.27 高，可能不是同类。`common2.wav` 三倍长且有两段明显脉冲，暂不选。 |
| 默认音的对照组 | `game33.wav`、`game34.wav`、`game35.wav`；`etc09.wav`、`etc10.wav`、`etc11.wav` | 两组时长分别成对同为 0.276/0.291/0.142 s，起音 1–4 ms，50 ms 包络也相近；可能是同组三音的不同变体，也可能是通用 UI 动作。 |
| 旋转/蓄力 | `game20.wav`、`game23.wav`、`game17.wav` | 约 0.81/0.87/2.15 s；前两件有持续段后增强，第三件长而缓起缓落（起音 24 ms）；先对照旋转是否需要持续声。 |
| 冲刺释放 | `game28.wav`、`game08.wav` | 约 1.21/0.42 s，均约 2 ms 起音、峰值近满幅，前强后弱；先对照冲出的动作边沿。 |
| 刹车 | `game06.wav`、`game14.wav` | 约 0.34/0.24 s，约 4/2 ms 起音，峰值近满幅且靠前，拖尾短、逐段减弱。 |
| 特殊吸附，本地先接 | `voice_184.wav`、`voice_192.wav` | 文件编号与 `B8`、`C0` 的十进制值相合；前者 0.472 s、峰值 0.86、起音 2 ms；后者 1.307 s、峰值 0.96、起音 7 ms，峰值靠后。只凭编号和包络不能认出物种或区分碰撞声。 |
| 特殊吸附，未接 | `voice_183.wav`、`voice_154.wav`、`voice_164.wav` | `voice_183.wav` 在包内（1.197 s，峰值近满幅，起音 3 ms），但现有两参考关没有 `B7` 实例；其余两个文件不在该包，若另有 Character Voices 包再对照。 |

## 与原作差异与待验证项

REROLL 的社区转录给普通吸附 `4/5/6`、小车 `446`、自行车 `484`；后两点吻合 `118 + 2 × 0x19` 的假设，不足以推导任意物型、零值和一对相邻 ID 的含义。运行时不使用这个公式、不按图鉴类别配音。也没有原作对短窗、池上限、随机分布、大小/振动档位的证据；这些都是当前实现的本地手感策略。旋转、冲刺、刹车等仅列试听候选，不接入吸附通道。待补正式素材及复核发布场景没有任何参考音频依赖。

## 操作员：只在本地验证

参考音频只用于本地试听：**不进 git、不进正式场景、不上传（含私有）**；可发布场景不得引用 `Assets/_RefUnlicensed/` 下的片段。下面是 Unity 操作员在 owner 的 Windows 工程上要做的步骤；实现者没跑过 Unity，预期读数是静态推导，不是实测。

1. 准备本地参考数据（运行 Unity 的账户）：
   - 把 Linux 工作区的 `~/KatamariRef/audio/ref_audio_map.json` 复制到 `%USERPROFILE%\KatamariRef\audio\ref_audio_map.json`。
   - 要验证特殊音类时，再把 `~/KatamariRef/meta/tables/raw_reroll_dump__name_props_0x30.tsv` 复制到 `%USERPROFILE%\KatamariRef\meta\tables\`。
   - 从素材包只复制 5 个 WAV 到工程 `Assets/_RefUnlicensed/Audio/`：`common1.wav`、`common3.wav`、`common4.wav`（默认组）、`voice_184.wav`（B8，风铃）、`voice_192.wav`（C0，布谷钟）。文件名必须和映射里逐字一致（大小写也算）。不要复制到 `Assets/VRC_Katamari/`，不要提交场景、素材或生成的参考文件。
2. 让新脚本编进 Udon：确认 `Runtime/Pickup/AbsorbFeedbackHost.cs` 旁边生成了 `AbsorbFeedbackHost.asset`（UdonSharp ProgramAsset），Unity 重新编译 U# 无报错。U# 程序过期时装配器会在 `CopyProxyToUdon` 处给出 `absorb feedback: CopyProxyToUdon threw …`，按提示 compile → save → reopen 再跑。
3. 重新导入两个参考关（每件道具的音类列在拾取数组落盘后写入）：
   - 蟹座 v2：`VRC Katamari/Level/Import Placement (LevelContent)` 或等效入口。预期行 `propAbsorbSoundClasses on '<host>': 10/510 special rows, Udon read-back verified`（9 个风铃 = 类 1，1 个布谷钟 = 类 2；511 个文档实例里 `sparrow_001` 是无代理贴花、不成行）。
   - MAS1 v1：预期 `2/648 special rows, Udon read-back verified`（`cuckoo` 与 `cuckoo_clock` 的 `0x19` 都是 C0）。
   - 没装映射、或不在参考关时预期 `0/<行数> special rows`，仍必须有 `Udon read-back verified`；回读失败会打 `error: absorb audio: …`，不是静默降级。
4. 重跑 `VRC Katamari/Hub/Assemble All-Maps Hub`（它自己装配骑乘和吸附反馈，不必另跑 Ride UX）。跑前保存并关闭输出场景 `Assets/_RefUnlicensed/Scenes/AllMapsHub.unity`（装配器拒绝在未保存场景上跑）。预期日志 `absorb feedback: voices=4 clips=5 linked pickup fields=N/N Udon read-back=verified`；分母是合集里的关数，分子是其中成功接上反馈的关（没有拾取字段的关不计数）。参考音频缺失时是 `clips=0` 加一条 `note: absorb feedback: …`（说明映射缺失或列出没导入的 WAV 文件名），**不是 PROBLEM**；只有接线或 Udon 回读失败才进 PROBLEM。
5. Editor/ClientSim 桌面检查（桌面不应发震动）。**在装配出的 `AllMapsHub` 场景里 Play**：反馈宿主是合集装配器建的，单独导入的关卡场景没有它，不会出声也不会震。骑上球吸一件后下车或结束回合，Console 出现 `[absorb-feedback] confirmed=1 audioClusters=1 audioPlayed=1 poolDrops=0 hapticPulses=0`（读两条日志的差；计数器是会话累计）：
   - 音效音量 0 或没有可用片段时 `audioClusters` 仍增长、`audioPlayed` 不增长。
   - 撞不可吸物、被拒绝的候选：五个数都不增长。
   - 密集扫蟹群：`confirmed` 等于实际确认件数，`audioPlayed` 明显小于它（60 ms 合并），`poolDrops` 只在 4 个声部都忙时增长。
   - 重试、下车、切关后没有旧声；把音效音量调到 0 后不再有新播放，调回恢复。
6. VR 真机另开获准环境测（不能含未授权素材）：单件应有短脉冲；`hapticPulses` 计调用次数、不随档位变，强度按关/弱/标准缩放；桌面模式 `hapticPulses=0`。ClientSim 与 API 计数不能代替触感实测。

## Owner：试听清单

先盲听默认候选与对照组，再与原作同一普通物型多次吸附的录音或播放日志比对；记录文件名、原作版本、关卡、动作和时间点。风铃及布谷钟各单独测一次，分开记录“撞”和“卷入”；自行车、小车、螃蟹另用能吸到它们的原作关卡测试（蟹座没有可吸的 Transport，Minivan 在本关文件里标不可吸）。用密集道具听音量和节奏，尤其注意合并后是否还能听到特殊声、60 ms 是否显迟；再对照原作旋转、冲刺释放和连刹录音。VR 验证关/弱/标准和分档，只在明确实际发震时记设备结果；听感和震动都以真实 PCVR 为准。
