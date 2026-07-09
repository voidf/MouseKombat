# MouseKombat RL trainer

Python side of the RL pipeline. Trains policies against the **same** combat logic the game runs,
by hosting the `MouseKombat.Sim` .NET library in-process via pythonnet (no Godot, no IPC).

## Setup
- Python 3.11, torch 2.8 (already present).
- `pip install pythonnet stable-baselines3 gymnasium` (pythonnet installed; SB3/gymnasium pending).
- Build the sim first: `dotnet build MouseKombat.Sim/MouseKombat.Sim.csproj -c Release`.

## Files
- `bridge_smoke.py` — proves pythonnet can load the sim DLL and drive `GameSim`. Run it to sanity-check the bridge (`python rl/bridge_smoke.py`).

## Bridge facts (measured)
- Loads `MouseKombat.Sim.dll` (net8.0) via `clr_loader.get_coreclr` + its `runtimeconfig.json`.
- Python-driven single-env throughput ≈ 158k `GameSim.Step`/sec (~2,600x realtime). Batch envs in C# to push higher for PPO.

## Action / observation contract (to finalize before training)
- Observation: `Observation.Get(sim, playerIndex)` → 32 floats (reserved tail for 斗气/必杀/countdown).
- Action (proposed): 10 outputs = L/R/U/D + LP/MP/HP/LK/MK/HK → threshold to held dirs + edge-detected
  just-pressed mask, packed into an `InputFrame`. Lock this with the first PPO run.
