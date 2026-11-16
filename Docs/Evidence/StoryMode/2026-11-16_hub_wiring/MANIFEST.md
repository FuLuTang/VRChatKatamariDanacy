# Round O4 bring-back

Windows HEAD `1003278`；无 stash/reset/commit/push；`ProjectSettings.asset`、`PhysicsTestField.unity`、`CpuSkirmish.unity` 未动。

## 需要提交
1. `Worlds/StoryMode/StoryMode.unity` — **2600 / 104**：hub 房间（`LevelContent/hub` 结构 7 + 道具 5 + 惰性 `PickupFieldHost`）、
   `HubCenter/HubSpawn/LevelEntry/HubRoom/HubLevel/HubReturnButton/HubResultBoard/HubPromptText/EventSystem`、
   结果卡搬到 World Space Canvas、`StoryLevelLab` 删除 + `StoryBallDiagnostics` 新增、`spawns`→HubSpawn、
   `RideSeat/StandPoint`、`RideSeat/ExitPoint`、Station 两个位置字段、launcher/host/presenter 的接线。
2. 新增程序资产（10 个文件 = 5 asset + 5 meta）：
   `Runtime/Story/Hub/HubRoomHost.asset`、`Runtime/Hub/HubLevelLauncher.asset`、`Runtime/Hub/HubReturnButton.asset`、
   `Runtime/Hub/HubStartButton.asset`、`Runtime/Story/StoryBallDiagnostics.asset`。
3. 新增 `SerializedUdonPrograms/*.asset` 5 个（`37abd96f`、`79943891`、`cc963642`、`ceec4cc7`、`f2ebbf18`）+ meta。
4. 这些资产所属脚本的 `.meta` 已在仓库里（除新建 asset 的 `.meta`），无其它新增 `.meta`。

## 未跟踪但不要提交
`Assets/_RefUnlicensed.meta`、`Docs/Evidence/PhysicsTestField/2026-11-15_pass*`。

## sha256（Windows → Linux，逐文件核对一致）

场景与 diff：

```text
0a89f0831f89f18f325d8cf9dc8bdaf94b7f06b3f702eecfcf0eb7e38f3ad334  StoryMode.unity  (完整存档，副本在 /tmp/unity_round_f/bring_back_o4/StoryMode.unity)
749fd193dda0cd8ab00a486c27308f9740c787b293c32268c063ee50c243927b  2026-11-16_hub_wiring/storymode_hub.diff  (git diff -U0，2776 行)
```

程序资产（`Assets/VRC_Katamari/…`；同名 `.meta` 逐一同路径复制，sha256 见下，未改任何既有文件）：

```text
48139789b81bc89cb355837331b7628e04311a9b1be11b6882434e2058f2bb68  Runtime/Story/Hub/HubRoomHost.asset
992ee2267a869f3e59b98a6575a0a501e1f986b2336e4818f8994690df53410b  Runtime/Hub/HubLevelLauncher.asset
de7b5b210e73a23f76f31843dd833c27e17ec233951f81de42c68eb3a84421cb  Runtime/Hub/HubReturnButton.asset
44a5cfd7b314ae132634d27220efe75e0b36c0c6ed50b14c9ceb428f2d10a928  Runtime/Hub/HubStartButton.asset
1a3b56759a6a46074a8a887afc168ae3cdecf98980e9a3a4f43e0cb9bd6bccdc  Runtime/Story/StoryBallDiagnostics.asset
```

`Assets/SerializedUdonPrograms/`（每个都带 `.asset.meta`）：

```text
8240daacf8df8d1ec183259fe464911f144b9d6a3a8ca86caf1c60f1a2a1c193  37abd96fb20fd98428551dca4de4c12b.asset
1c59a950e945faa2f036c0eb4df7edb1269954d772eb700deb76c750ddbc5448  79943891a3fb4ed46bf4e77aed2e5d1e.asset
785613e7972b5c6c8653f6177ddd03cce3c1c5d1f4c878750b013d2460cda92f  cc9636426dd68b74faf701c0e67dd142.asset
3efac7eb8f32e5a52cd072a4ce8530d9d9942c769eb0611c1db1bf91dcf1fec9  ceec4cc744cd79b44bb142a9d5c889d9.asset
4655b9f7c26aecd96dd048e0d665e09d21972310cc32f5863df09be8ab52ff1d  f2ebbf18a04a92a49a37bd26029280da.asset
```

合入方式：先把 `/tmp/unity_round_f/bring_back_o4/StoryMode.unity` 覆盖到 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Worlds/StoryMode/StoryMode.unity`
（或直接 `git apply` 上面那份 `-U0` diff），再照 §"需要提交" 加入 10 个程序资产文件与 10 个 `.meta`（5 asset + 5 program，各带 meta）。

### 合入方式（实测）
- `patch -p1 -F0 --dry-run < storymode_hub.diff` → **通过**（`checking file …`，无 reject）。
- 同一份文件 `git apply --check` 报 `patch does not apply`（在 `:896` 的 `m_Children: []` 处），是 git apply 对 Unity 场景里超长行/CRLF 属性的严格匹配问题，不是内容不符（已核对：Linux 工作树 blob = HEAD blob = `4e72cdd`，两侧 HEAD blob sha256 均为 `270e7071a546d7a5…`，LF 归一化后逐字相同）。
- 因此推荐：直接覆盖 `/tmp/unity_round_f/bring_back_o4/StoryMode.unity`（LF，sha256 `0a89f0831f89f18f325d8cf9dc8bdaf94b7f06b3f702eecfcf0eb7e38f3ad334`），或 `patch -p1`。
