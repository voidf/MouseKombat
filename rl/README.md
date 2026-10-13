# MouseKombat RL trainer

Python side of the RL pipeline. Trains policies against the **same** combat logic the game runs,
by hosting the `MouseKombat.Sim` .NET library in-process via pythonnet (no Godot, no IPC).

## Setup
- Python 3.11, torch 2.8 (present). `pip install pythonnet stable-baselines3 gymnasium onnx` (done).
- Build the sim **in Release** before anything else: `dotnet build MouseKombat.Sim/MouseKombat.Sim.csproj -c Release`
  (or `rl/build_sim.bat`). The bridge loads `MouseKombat.Sim/bin/Release/net8.0/MouseKombat.Sim.dll`;
  if Release is stale you train against older rules than the game runs.

## Contract (locked)
- **The sim is fixed-point (Q16.16), not float.** Every continuous value inside `MouseKombat.Sim`
  is a `Fix` (see `MouseKombat.Sim/Fix.cs`), so results are bit-identical on Windows/x64, macOS/ARM
  and every training box — the property rollback netcode and replays depend on.
- Observation: `Observation.Get(sim, playerIndex)` → 32 floats. Layout: `0..22` core,
  `23..27` projectile block (incoming active/dx/dy/dir + own active), `28` self CharacterId,
  `29` opp CharacterId, `30/31` "held by a throw" flags.
- Action: MultiBinary(10) = [L,R,U,D, LP,MP,HP,LK,MK,HK]. Pressed = value/logit > 0. Dirs are held;
  buttons are EDGE-detected into the just-pressed mask (a held button = one press). Packed into
  `InputFrame`. `OnnxAgent.cs` (game) mirrors this exactly.
- **Character spec = `hero:<Folder>`.** `make_config("hero:Hamster", x, y, facing)` builds the
  PlayerConfig from `Heroes/Hamster` (char.json + actions/*.json) through `HeroDisk`, i.e. the
  exact files the game's `HeroLibrary` loads. Plain `"Hamster"` selects the legacy C# tables, which
  the game **no longer uses** — it is only kept as a fallback and for the golden-checksum tests.

## Why v9 was unplayable, and what v10 changed
v9 was trained against the legacy code tables. Between v9 and the 招式编辑器 migration, four things
silently broke the train↔game parity. All four are fixed; `parity_check.py` pins them.

1. **Wrong move tables.** The game reads `Heroes/<folder>/*.json`; training read `MoveSets.BuildCs()`.
   The editor had since edited content (Hamster `2LP` hitbox raised/advanced, `crouchEnterFrames`
   8 → 3), so the policy's learned reach and timings were for a character the game does not run.
   v10 trains on the JSON — including mirrors, both characters.
2. **A saturated observation.** `Observation` fed `CurrentMove.Damage / 20f`. Damage is HP in the
   legacy tables (3..16 → 0.15..0.80) but **centi-HP with the value living on the active window** in
   the Heroes/ tables, where `MoveDef.Damage` stays 0 — so the same feature was a constant 0 in-game
   while reading 0.15..0.80 in training. It is now `threat / MaxHp`, which means the same thing in
   both table kinds (3/100 == 300/10000 == 0.03). This is the "stands still and whiffs" bug: the
   policy could not tell its own attack state in the real game.
3. **Fireballs that could not hit.** `HeroCompiler.Compile(def, prefabHitbox)` gives a fireball a
   `default(SimRect)` (zero size) when no resolver is passed, and the bridge passed none. The game
   resolves the box from `FireballTSCN/<id>.tscn`; `HeroDisk.FireballHitboxResolver` now parses the
   same `Hitbox = Rect2(...)` out of the same file, and throws rather than silently training against
   a threat that cannot touch anyone.
4. **A ×100 reward.** `mk_env`/`selfplay` divided HP deltas by a literal 100. On the Heroes/ scale
   (MaxHp 10000, a light hit 300) that made one poke worth 3.0 — a hundred times the intended
   shaping, swamping the ±1 KO signal. Rewards are now normalized by the fighter's own `MaxHp`.

Plus two policy-level fixes, because v9's loss to a fireball spammer was reproducible **in its own
training world** (0-20-0 vs zoner, 0% damage dealt, 0.00 hits per attack, 23% of frames doing
nothing): a **whiff penalty** so mashing a normal at nothing is not free, and a **curriculum** that
puts zoning front and centre instead of 20% of episodes.

## Is 发波 spam actually beatable? (yes — run this if you doubt it)
`python rl/anti_zoner.py all 6` — two hand-written 25-line teachers (`BlockAdvanceAgent`,
`JumpInAgent`, defined in `train_v10.py`) guard the fireball, walk in and punish. `BlockAdvance`
beats the spammer 3-0-3 while dealing 77% of a health bar; both are in the v10 opponent mix as
demonstrators, so the learner sees the shape of the answer instead of having to discover it from a
reward that only says "you lost".

## Files / workflow
- `mk_env.py` — the pythonnet bridge + `make_config` (caches the compiled Heroes/ table per process;
  re-reading 70 JSON files per episode reset starves the sim on I/O).
- `train_v10.py` — **v10 trainer.** Self-play over Hamster+Kangaroo on the game's tables, whiff
  penalty, HP-relative reward, scripted-teacher mix configurable per stage.
- `campaign_v10.py` — the 2-day campaign: 5 stages, each gated by `eval_suite` against FIXED
  external baselines, and the best-scoring checkpoint (not the last) is exported as
  `checkpoints/ppo_selfplay_v10.zip` → `ai_rl_model/ppo_selfplay_v10.onnx`.
- `eval_suite.py` — play-quality eval. Win rate alone cannot see "whiffs at the air"; this reports
  neutral% (frames doing literally nothing), hits/attack, damage dealt/taken, fireball frames and
  fireball hits, blocks, jumps, and the max |observation| (anything outside Box(-4,4) is an
  out-of-distribution input). `--chars hero` evaluates exactly what the game runs.
  **Use at least 20 games per opponent before believing anything.** An 8-game sample mid-transition
  read "0-1-7 vs the state machine, the policy has become a turtle"; the same checkpoint at 20 games
  was 20-0-0 with 100% damage dealt. Small samples here are dominated by the policy's phase within
  the training run, not by its skill.
- `rank_candidates.py` — scores every checkpoint that has an eval JSON (campaign weights) and prints
  a ranking. The deliverable is chosen from this, not just from the campaign's own best-stage rule,
  because a mid-stage snapshot can beat every stage output.
- `ship_v10.py` — the final hand-off: refresh the ranking, take the top candidate, copy it to
  `checkpoints/ppo_selfplay_v10.zip`, then run `finalize_v10.py`. Refuses to ship below a score floor.
  `--dry` shows what it would pick. Zero-fireball-world evals live in `logs/zerofb_*.json` so they
  cannot be ranked against designed-world numbers.
- `parity_check.py` — train↔game parity guard: fireballs land, every obs dim is in range, dims 20/21
  carry the same meaning in both table kinds, CharacterIds survive the hero path.
- `test_env_reward.py` — reward/observation CONTRACT tests. Every bug that killed v9 was a silent
  scale error, so this pins the numbers on a real sim: damage pays exactly `-delta/MaxHp`, a whiff
  costs exactly `WHIFF_PENALTY`, a block pays exactly `BLOCK_BONUS`, an avoided fireball pays
  exactly `FIREBALL_AVOIDED` (A/B isolated), and no observation ever leaves `Box(-4,4)`.
- `finalize_v10.py` — the delivery half: export the chosen checkpoint, copy the .onnx next to both
  the editor's and the exported build's `ai_rl_model/`, re-verify ONNX≡SB3 frame by frame, boot the
  REAL GAME headless with that .onnx, and write `logs/final_report.md` with the v9-vs-v10 table.
- `anti_zoner.py` — duel harness for the two hand-written anti-zoning teachers; proves the fireball
  exploit is beatable and guards that the teachers still beat what they are meant to teach.
- `diag_tables.py` — full field-by-field diff of the legacy tables vs the Heroes/ JSON.
- `trace_match.py` — frame-by-frame trace of one match (positions, distance, move, press mask,
  damage). This is what showed v9 backing into its own corner and mashing 5HP from 500px.
- `selfplay.py` / `train.py` / `mk_env.py` — the v9-era scripts, kept; they default to the legacy
  tables and are **not** what v10 uses.

## Pool hygiene (read before a fresh campaign)
Opponents are sampled from the **20 highest-numbered** files in `checkpoints/pool/`. Snapshots are
named `snap_<timesteps>.zip`, so a pool inherited from a LONGER previous run silently excludes the
new run's own snapshots from the sampling window — self-play degenerates into training against a
fixed set of stale models, and the names can collide besides. Move the old pool aside first:

    move rl\checkpoints\pool rl\checkpoints\pool_archive_<why>

`campaign_v10.py` seeds the pool with the warm-start model (`snap_000000000_anchor.zip`) and
`train_v10.py` prunes down to the newest 80 snapshots. Stages run with `reset_num_timesteps=False`
so the step counter (and therefore the snapshot names) keeps rising across a multi-stage campaign
instead of overwriting the previous stage's pool.

## Throughput notes (measured on the 7950X box)
- **`MK_SUBPROC=0` is 3.3x faster than 1 here** (5575 vs 1696 steps/s at 16 envs). Piped stdio
  between processes is the bottleneck in this environment; `DummyVecEnv` keeps the sims in the
  training process. `MK_NENVS=32`, `MK_TORCH_THREADS=8` → ~7100 steps/s.
- `torch.set_num_threads` matters: every subprocess worker otherwise creates a full-size intra-op
  thread pool and the tiny per-step policy inference collapses.

## Results
全部数字都是 `eval_suite.py --chars hero`（= 游戏真正执行的 Heroes/ 数据表），每对手 20 局，
双角色双向轮流。加权分 = `0.5 * (1 + Σ w·(W-L)/局数)`，发波类对手权重 2.0。

| checkpoint | 加权分 | 状态机 | rusher | blocker | jumper | zoner | 发波流 |
|---|---|---|---|---|---|---|---|
| `ppo_selfplay_v9`（旧模型） | 0.291 | 9-0-11 | 0-0-20 | 10-10-0 | 10-10-0 | 0-20-0 | **0-20-0** |
| `ppo_v10_a_breakout`（12M 步，近战优先） | 0.471 | 20-0-0 | 20-0-0 | 20-0-0 | 10-10-0 | 0-20-0 | 0-20-0 |
| `ppo_v10_cand_b10M`（b 阶段早期） | 0.882 | 20-0-0 | 20-0-0 | 20-0-0 | 20-0-0 | 10-0-10 | 10-0-10 |
| **`ppo_selfplay_v10`（当前出货）** | **0.982** | 17-3-0 | 20-0-0 | 20-0-0 | 20-0-0 | **20-0-0** | **20-0-0** |

**当前出货的 `ppo_selfplay_v10` 不是任何"阶段产物"**——它来自自对弈池里的一个中途快照
（137.2M 步）。课程的评分只看阶段输出，而自对弈非传递：实测该候选对**自己训练过程里的快照**
只有 15-18-6，甚至 0-6 输给其中一个；同一段训练里 600k 步之隔的两个快照分数可以是 1.000 和
0.456。**所以 `screen_pool.py` 现在是常规动作**（自动把 ≥0.9 的快照抢救出池并改写排名指向，
免得峰值被池子的 80 个上限裁掉）。

同一个 checkpoint 的关键指标（对 zoner+spammer 平均）：

| 指标 | v9 | 出货的 v10 |
|---|---|---|
| 发呆帧占比 neutral% | 22.9% | **1.4%** |
| 每次出招命中数（空挥反向指标） | 0.00 | **0.30** |
| 对发波流造成的伤害 | 0% | **100%** |
| 吃到的火球数（20 局合计） | 740 | **440** |

**v10 修好的三件事**：①"站着发呆 + 对空气挥拳"消失；②近战基准全胜（v9 对 rusher 是 0-0-20
打不完）；③**发波压制被完全解决**（0-20-0 → 20-0-0，且自己造成的伤害从 0% 到 100%）。

**两种口径**（`MK_ZERO_FIREBALL=1` 把火球编译成零判定框，即修复前的实机状态）：当前出货的 v10
**两种口径完全相同**（17-3-0 / 20-0-0 ×5）——它不是因为火球修好了才变强的，火球全变哑弹它照样
全胜。**模型不是靠发波赢的**，修好火球只是让它多一种赢法。

### 评测方法学：固定开局会让结果几乎没有方差（读数字前必看）
默认评测里**开局距离固定**（就是游戏真实的 120/650），而策略是确定性推理、`zoner`/`spammer`
两个脚本教师又完全不看 seed（`StateMachineAgent` 看）——所以"10 局"实际上是**同样 2 局各重复 5
次**。这解释了一个反直觉现象：把回合上限从 3600 帧拉到 9000 帧（多打 90 秒），伤害、火球数
**一个字节都不变**。

`--jitter 1` 会把开局距离按训练时的范围随机（80~350 / 450~720），得到真实方差：

| 口径 | vs zoner | vs spammer | 伤害 | 挨打 |
|---|---|---|---|---|
| 固定开局（= 游戏真实开局，最难的距离） | **20-0-0** | **20-0-0** | 100% / 100% | 90% / 42% |
| `--jitter 1`（随机距离，20 局） | 10-10-0 | 10-5-5 | 92% / 82% | 80% / 66% |

两个数字都要看：**固定开局那列才是你在游戏里会遇到的开局**（满屏距离正是发波流最舒服的距离，
而出货的模型恰恰在这里最强）；随机距离那列是它的"距离画像"——离开满屏距离后对脚本 zoner 大致
五五开（那是挡拆与距离管理都接近最优的脚本，人类打不出这个水平）。默认关闭 jitter，这样课程门控
之间可比。

**实机**（真游戏二进制 + 真 ONNX，全部 checksum 自证）：v10 在两个角色、两个出生位置上都 KO 了
v9；v9 在 100 秒内一局都打不完。详见下面「实机验证」一节。

训练仍在跑长程自对弈阶段；**由 `rank_candidates.py` 的全榜排名决定出货哪一个**，命令是
`python rl/ship_v10.py`（自动导出 + 拷贝 + 对拍 + 实机验证 + 生成报告）。

### 已修复的游戏 bug：实机里火球打不到人（2026-10-12 定位 + 修复 + 端到端验证）
**症状**：任何角色的 236P 都只是演出——火球会飞出来、有动画，但穿人而过，永不掉血。

**根因（游戏代码，一处调用顺序）**：`HeroLibrary.Scan()` 先 `LoadHeroes()` 后 `LoadFireballs()`。
编译招式表要走 `HeroCompiler.Compile(def, PrefabHitbox)`，而 `PrefabHitbox` 是去
`FireballScene(id)` → `_fireballs[id]` 里取火球判定框的——那一刻 `_fireballs` **还是空的**，
于是回退 `default(SimRect)` = **(0,0,0,0)**。所有角色的所有火球都带着零尺寸判定框出生：
会飞、会画、打不到人。

**为什么长期没被发现**：视图侧 `SpawnProjectileView` 用的是
`FireballScene(id) ?? owner.ProjectileScene`，而它执行时 `LoadFireballs()` 早就跑完了——
于是火球**渲染完全正常**，只有判定是坏的。

**修法**：把两行换个顺序（`LoadFireballs()` 提到 `LoadHeroes()` 前面），已在 `HeroLibrary.cs`
中改好并加了说明注释。另外在 `PrefabHitbox` 的失败分支加了一条 `GD.PushWarning`——这个 bug 能活
这么久，靠的就是"静默回退到零尺寸"；现在只要预制体没加载就会在日志里喊出来（告警不影响行为，
不想要可以直接删）。**验证**：
1. 游戏自己报出的编译结果变成 `(-55,-40,110,80)` / `(-60,-26,138,73)`，与 `.tscn` 完全一致
   （修复前是 `(0,0,0,0)`）；
2. **端到端**：修好后跑一局带火球的对局（v9仓鼠 vs v10袋鼠），回放重演 **checksum 完全匹配**
   ——修复前同一配置**四种判定框组合一个都对不上**；
3. 那一局实战结果是 **v10 把 v9 KO 了（v9 0 血 / v10 剩 3700）**。

**注意（排查中的两个弯路，留给后人）**：
- `FireballTSCN/*.tscn` 里的**贴图 UID 确实过期**（`csFireball` 写 `uid://8ulh7uuxkehq`，实际
  `uid://df8f3rsvt2j00`；`dsFireball` 写 `uid://coppnrxcpjwda`，实际 `uid://d1gwq3cgqqclk`）。
  **但它不是本 bug 的原因**——Godot 会按 path 回退，场景加载正常。可以顺手清理，无害。
- 用 PowerShell `Set-Content -Encoding UTF8` 改 `.tscn` 会**写入 BOM**，Godot 直接报
  `Parse Error: Expected '['`。改游戏资源请在编辑器里改/存。本次改动已还原，`.tscn` 现为原始字节。

## 实机验证步骤（给人看的版本）

### 0. 前置：必须重新编译游戏
`Observation.cs`（观测契约）与 `HeroLibrary.cs`（火球加载顺序）都改过。**从 Godot 编辑器跑会自动
重编译**；如果你跑的是 `Out/MouseKombat.exe` 那套便携版，需要重新导出，并把新的
`ai_rl_model/ppo_selfplay_v10.onnx` 放到 exe 同级的 `ai_rl_model/` 下（`ship_v10.py` 已经替你
拷了一份）。**没重编译的话模型会跑在另一份 32 维观测上，行为会莫名其妙。**

### 1. 开一局
主界面 → 本地对战 → 到选人/准备界面按 **反引号 `` ` ``** 打开 AI 菜单 → 选
**`ppo_selfplay_v10`**（也会看到 `ppo_v10_cand_b10M`、`ppo_v10_stageA` 这两个中间版本，以及一堆
老模型 v0~v9）。

### 2. 肉眼该看到的（对照 v9 的毛病）
| 检查项 | v9 的表现 | v10 应该有 |
|---|---|---|
| 站位 | 退到自己角落、站着不动（23% 的可行动帧啥也不干） | 主动接近，保持在有效距离 |
| 出招 | 对着空气刷 5HP（命中率 0.00） | 在射程内出招，命中率 ~0.4 |
| 对你的发波 | 站着吃满，一格不挡、几乎不跳 | 会跳过去 / 架招 / 趁机压进 |
| 结果 | 打不完一局（100 秒内 0 次 KO），或被打死 | 打得出 KO |

### 3. 想量化的话（可选，但更硬的证据）
- 打完一局（**必须打出 KO**，游戏只在 KO 时写回放），然后：
  `python rl\read_replay.py` —— 它会解码最新回放、**用回放自带的校验和自证**、打印胜负/时间/末态。
  如果它说 `UNVERIFIED`，说明你的构建与这份仓库的仿真对不上（多半是没重编译），别采信那局数字。
- 想直接和 v9 比：把 AI 换成 `ppo_selfplay_v9` 打同一局。预期是**它一局都结不了**。

### 4. 顺手验一下火球修复（值得单独确认）
随便哪一方按 **236P**（↓↘→+P），看对手**掉不掉血**。
⚠️ **"能看见火球"不算验证** —— 修复前火球也一直渲染正常（视图有兜底场景），坏的只是判定。

### 5. 要知道的边界
- 松鼠（Squirrel）按你的要求不在 v10 范围内（它仍复用仓鼠招式表、只有 IDLE 美术）。选它时模型
  没见过这个角色，表现不做保证。
- 我生成的测试回放没有留在你的回放菜单里，都归档在 `rl/logs/replay_evidence/`（31 个，可删）。
  菜单里只剩你自己那局 8/23 的。
- 模型对**袋鼠与仓鼠**两个角色、两个出生位置都训练过（含镜像对局），所以你选哪个都能打。

## In-game use
Drop-in: `ReadyScreen` scans `ai_rl_model/*.onnx` (res:// in the editor, the folder next to the .exe
in an exported build) and lists them in the backtick (`` ` ``) AI menu. The **game binary must be
rebuilt** for v10: `Observation.cs` changed, so an old export would feed the policy a different
32-float vector. Run from the Godot editor (C# rebuilds on play) or re-export with the existing
preset, and copy the .onnx to `Out/ai_rl_model/` for the portable build.

### Verifying a model in the real game (headless, no window, no waiting)
`GameManager` has a dev hook: with no lobby config, `MK_AI_P1` / `MK_AI_P2` bind each seat to
`"statemachine"` or an ONNX path. The game writes a replay ONLY on a knockout, so *"did a replay
appear"* is a free behavioural signal:

    $env:MK_AI_P1="res://ai_rl_model/ppo_selfplay_v10.onnx"; $env:MK_AI_P2="statemachine"
    Godot_v4.6.3-stable_mono_win64_console.exe --headless --path <repo> res://MFEntry.tscn `
        --fixed-fps 60 --quit-after 6000

`--fixed-fps` fast-forwards: 6000 frames is 100 s of game time in a couple of seconds of wall clock.
Then read the result back with `python rl/read_replay.py` (newest replays by default), which
re-simulates the recorded inputs and REPORTS THE WINNER — but only when its reconstruction
reproduces the game's own `FinalChecksum`, which is why the tool refuses to answer otherwise.

Verified with this setup: v10 (stage A, and the stage-B candidate) knockout of the state machine,
checksum-verified, P1 winning 9500/10000 and 9700/10000 to 0; and v9 producing **no knockout at all
in 6000 frames** — in the real game v9 cannot finish a round, exactly as reported.

**修复火球后的实机对局（每一局都 checksum 自证，回放存于 `rl/logs/replay_evidence/`）**：

| 实机对局 | 结果 | 用时 | 校验和 |
|---|---|---|---|
| v10(仓鼠) vs 状态机 | **v10 胜**，9700 : 0 | 29.8s | MATCH |
| v10(仓鼠) vs v9(袋鼠) | **v10 胜**，8000 : 0 | 18.1s | MATCH（桥接端预测同为 1084 帧） |
| v10(袋鼠) vs v9(仓鼠) | **v10 胜**，3700 : 0 | 26.4s | MATCH |
| v9(仓鼠) vs 状态机 | **100 秒内无 KO**（打不完一局） | — | — |

三个角色位都验过：v10 在**两个角色、两个位置**上都 KO 了 v9，且与训练端仿真逐字节一致。

Open question for whoever picks this up: **已解决，见上一节**（火球预制体因过期贴图 UID 加载失败，
判定框退回零尺寸）。保留这些工具的原因：`read_replay.py` 的校验和自证是唯一能直接判断"游戏与本
仓库仿真是否一致"的手段，改完资源后跑一局带火球的回放，校验和对上就说明修好了。

Only ever decode the FIRST replay of a run: the game reuses one sim across rounds
(`GameManager.ResetMatch` -> `_sim.Reset()`), so round 2+ recordings start from a state a fresh
`GameSim` cannot reconstruct. That is a property of the reader, not a game bug.

Tools: `read_replay.py` (decode + self-verify), `probe_replay_setup.py` (grid over whole setups),
`probe_fireball_pair.py` (grid over per-prefab hit rects), `replay_fireballs.py` (what was thrown and
whether it connected). The sim-level numbers elsewhere in this file are unaffected — they come from
the bridge, not from replays.

### 训练口径 vs 实机现状
`MK_ZERO_FIREBALL=1` 会把所有火球编译成零判定框，用来回答"这个策略在**玩家今天手上那个构建**里
表现如何"（即 `HeroLibrary` 顺序 bug 修复之前）。默认（不设）按资源本意训练——这是正确口径。

`ppo_selfplay_v10` 两种口径下的实测（20 局/对手）：**六项全部完全一致**（状态机 17-3-0，其余五项
20-0-0）。**结论：这一版模型完全不依赖火球**——就算你把火球改回哑弹，它照样全胜；修好火球只是让
它多一种赢法。反过来看也说明第 5 个 bug（火球打不到人）不是你输给 AI 的原因，v9 的问题是它自己
不会打。
