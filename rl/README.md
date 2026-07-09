# MouseKombat RL trainer

Python side of the RL pipeline. Trains policies against the **same** combat logic the game runs,
by hosting the `MouseKombat.Sim` .NET library in-process via pythonnet (no Godot, no IPC).

## Setup
- Python 3.11, torch 2.8 (present). `pip install pythonnet stable-baselines3 gymnasium onnx` (done).
- Build the sim: `dotnet build MouseKombat.Sim/MouseKombat.Sim.csproj -c Release` (or run `rl/build_sim.bat`).

## Contract (locked)
- Observation: `Observation.Get(sim, playerIndex)` → 32 floats (reserved tail for 斗气/必杀/countdown).
- Action: MultiBinary(10) = [L,R,U,D, LP,MP,HP,LK,MK,HK]. Pressed = value/logit > 0. Dirs are held;
  buttons are EDGE-detected into the just-pressed mask (a held button = one press). Packed into `InputFrame`.
  `OnnxAgent.cs` (game) mirrors this exactly.

## Files / workflow
- `bridge_smoke.py` — sanity-check the pythonnet bridge.
- `mk_env.py` — Gym env vs the C# state-machine AI. `train.py [steps]` — PPO baseline (`rl/train.bat`).
- `selfplay.py [steps] [init_ckpt] [out_name]` — self-play vs a pool of past snapshots + FSM/init anchors
  (`rl/selfplay.bat`). **Recipe that works: warm-start from a prior model AND anchor the pool with it**
  (else non-transitivity — a scratch self-play model can lose to the very baseline it should beat).
- `eval_vs.py <p1> <p2> [games]` — head-to-head win counts (spec = `statemachine` or a `.zip`), sides alternated.
- `export_onnx.py <ckpt> <out>` — export a checkpoint to `ai_rl_model/<out>.onnx` (validated vs SB3).
- `verify_reset.py` — regression check for the cross-round agent-reset bug.
- Per character: `set MK_CHAR=Kangaroo` (default Hamster) for selfplay/eval.

## Results so far
- `train.py` 150k → `ppo_hamster_v0`: beats state-machine 40/0, but weak (crouch/poke).
- `selfplay.py` 600k warm-started from v0 → `ppo_hamster_selfplay_v2`: 39/1 vs FSM, **25/15 vs v0**, 23/16 vs v1.
- Both exported to `ai_rl_model/*.onnx` and playable via the ReadyScreen `` ` `` menu.

## To push stronger
Longer runs (2–5M), continue warm-starting from the latest (`selfplay.bat 2000000 checkpoints\ppo_hamster_selfplay_v2.zip ppo_hamster_selfplay_v3`), keep old snapshots as anchors, then `export_onnx.py`.
